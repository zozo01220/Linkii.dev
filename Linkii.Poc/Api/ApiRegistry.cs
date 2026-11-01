namespace Linkii.Poc;

// API publique et webhooks (§154, maquettes/api-webhooks.html).
//
// Le socle ne connaît aucune fonction métier. Une fonction future s'ajoute avec UN module (IApiModule) qui déclare :
//   - ses accès (Scopes) et les gabarits qu'ils rejoignent (Templates),
//   - ses routes sous /api/v1 (Routes, branchées par Map avec l'accès et la classe de débit exigés),
//   - ses types d'événements (Events),
// puis émet ses événements par IEventPublisher.Publish. Les écrans (création de clé, cases des webhooks, documentation) lisent le registre.

/// <summary>Un accès (« screens:read ») : il apparaît dans « Personnalisée » à la création d'une clé.</summary>
public record ApiScope(string Id, string Group, string Label);

/// <summary>Gabarit de clé. Plusieurs modules peuvent déclarer le même nom : leurs accès s'additionnent.</summary>
public record ApiTemplate(string Name, string Hint, string[] Scopes, int Order = 100);

/// <summary>Type d'événement que les webhooks peuvent recevoir (« screen.offline »).</summary>
public record EventType(string Id, string Group, string Label);

/// <summary>Route déclarée : sert au branchement, au contrôle d'accès, au débit et à la documentation OpenAPI.</summary>
public record ApiRoute(string Method, string Path, string Scope, string Class, string Summary);

public interface IApiModule
{
    string Id { get; }
    IEnumerable<ApiScope> Scopes { get; }
    IEnumerable<ApiTemplate> Templates { get; }
    IEnumerable<EventType> Events { get; }
    IEnumerable<ApiRoute> Routes { get; }
    /// <summary>Branche les routes de <see cref="Routes"/> (une par appel de <see cref="ApiRouter.Map"/>).</summary>
    void Map(ApiRouter router);
}

/// <summary>Classes de débit : chaque route choisit la sienne (voir <see cref="ApiLimits.Permits"/>).</summary>
public static class RateClass
{
    public const string Read = "read", Write = "write", Urgent = "urgent";
    public static readonly string[] All = { Read, Write, Urgent };
}

/// <summary>Limites d'une organisation : par défaut (10 clés, 5 adresses, 60 requêtes/min, 30 jours de journal), réglables par la console.</summary>
public static class ApiLimits
{
    public const int DefaultKeys = 10, DefaultWebhooks = 5, DefaultRatePerMinute = 60, DefaultJournalDays = 30;
    public const int MaxRatePerMinute = 10_000;

    public static int Keys(Tenant t) => t.ApiMaxKeys is { } v and >= 0 ? v : DefaultKeys;
    public static int Webhooks(Tenant t) => t.ApiMaxWebhooks is { } v and >= 0 ? v : DefaultWebhooks;
    public static int RatePerMinute(Tenant t) => t.ApiRatePerMinute is { } v and >= 1 ? Math.Min(v, MaxRatePerMinute) : DefaultRatePerMinute;
    public static int JournalDays(Tenant t) => t.ApiJournalDays is { } v and >= 1 ? v : DefaultJournalDays;

    /// <summary>Requêtes par minute pour une classe : lecture = la limite, écriture = la moitié, urgence = un sixième (60 → 60, 30, 10).</summary>
    public static int Permits(Tenant t, string cls) => cls switch
    {
        RateClass.Write => Math.Max(1, RatePerMinute(t) / 2),
        RateClass.Urgent => Math.Max(1, RatePerMinute(t) / 6),
        _ => RatePerMinute(t),
    };

    /// <summary>L'API est réservée à la licence Pro.</summary>
    public static bool Allowed(Tenant t) => t.IsGrowth;
}

/// <summary>Ce que le socle sait des modules : accès, gabarits, événements, routes. Les contrôles de cohérence se font au démarrage.</summary>
public class ApiRegistry
{
    public IReadOnlyList<IApiModule> Modules { get; }
    public IReadOnlyList<ApiScope> Scopes { get; }
    public IReadOnlyList<ApiTemplate> Templates { get; }
    public IReadOnlyList<EventType> Events { get; }
    public IReadOnlyList<ApiRoute> Routes { get; }
    public const string Version = "2026-11";

    public ApiRegistry(IEnumerable<IApiModule> modules)
    {
        Modules = modules.ToList();
        Scopes = Unique("accès", Modules.SelectMany(m => m.Scopes), s => s.Id);
        Events = Unique("événement", Modules.SelectMany(m => m.Events), e => e.Id);
        Routes = Unique("route", Modules.SelectMany(m => m.Routes), r => r.Method + " " + r.Path);
        foreach (var r in Routes)
        {
            if (!Scopes.Any(s => s.Id == r.Scope)) throw new InvalidOperationException($"Route {r.Method} {r.Path} : accès inconnu « {r.Scope} ».");
            if (!RateClass.All.Contains(r.Class)) throw new InvalidOperationException($"Route {r.Method} {r.Path} : classe de débit inconnue « {r.Class} ».");
        }
        Templates = Modules.SelectMany(m => m.Templates).GroupBy(t => t.Name)
            .Select(g => new ApiTemplate(g.Key, g.First().Hint, g.SelectMany(t => t.Scopes).Distinct().ToArray(), g.Min(t => t.Order)))
            .OrderBy(t => t.Order).ThenBy(t => t.Name).ToList();
        foreach (var t in Templates)
            foreach (var s in t.Scopes)
                if (!Scopes.Any(x => x.Id == s)) throw new InvalidOperationException($"Gabarit « {t.Name} » : accès inconnu « {s} ».");
    }

    private static List<T> Unique<T>(string what, IEnumerable<T> items, Func<T, string> key)
    {
        var list = items.ToList();
        var dup = list.GroupBy(key).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidOperationException($"Deux modules déclarent le même {what} : « {dup.Key} ».");
        return list;
    }

    public ApiScope? Scope(string id) => Scopes.FirstOrDefault(s => s.Id == id);
    public bool IsKnownScope(string id) => Scopes.Any(s => s.Id == id);
    public bool IsKnownEvent(string type) => Events.Any(e => e.Id == type);

    /// <summary>« screen.* » correspond à tous les événements de « screen », « * » à tous.</summary>
    public static bool Matches(string pattern, string type) =>
        pattern == "*" || pattern == type || pattern.EndsWith(".*") && type.StartsWith(pattern[..^1], StringComparison.Ordinal);

    /// <summary>Un abonnement est valide s'il désigne au moins un événement déclaré.</summary>
    public bool IsValidSubscription(string pattern) => Events.Any(e => Matches(pattern, e.Id));

    /// <summary>Accès regroupés par groupe, dans l'ordre de déclaration (création d'une clé personnalisée).</summary>
    public IEnumerable<IGrouping<string, ApiScope>> ScopeGroups => Scopes.GroupBy(s => s.Group);
    public IEnumerable<IGrouping<string, EventType>> EventGroups => Events.GroupBy(e => e.Group);
}

/// <summary>Branche une route déclarée sous /api/v1 avec ses contrôles communs (clé, accès, licence, suspension, débit).</summary>
public class ApiRouter(RouteGroupBuilder group, ApiRegistry registry)
{
    public RouteHandlerBuilder Map(ApiRoute route, Delegate handler)
    {
        if (!registry.Routes.Contains(route)) throw new InvalidOperationException($"Route non déclarée par un module : {route.Method} {route.Path}.");
        return group.MapMethods(route.Path, new[] { route.Method }, handler)
            .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { AuthenticationSchemes = ApiAuth.Scheme })
            .AddEndpointFilter(new ApiGuard(route.Scope))
            .RequireRateLimiting("api-" + route.Class)
            .WithMetadata(route);
    }
}
