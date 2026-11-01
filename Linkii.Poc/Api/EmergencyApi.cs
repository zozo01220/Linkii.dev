namespace Linkii.Poc;

/// <summary>
/// Module « Urgence » de l'API (§159) : déclencher, suivre et lever une urgence depuis une alarme, un contrôle d'accès ou un script.
/// Réservé aux organisations Pro qui ont installé l'intégration « Alerte d'urgence » (Store). Pas de niveau ni de durée : l'urgence reste active
/// jusqu'au <c>DELETE</c>.
/// </summary>
public class EmergencyApiModule : IApiModule
{
    public const string Read = "emergency:read", Write = "emergency:write";
    public string Id => "emergency";

    public IEnumerable<ApiScope> Scopes { get; } = new[]
    {
        new ApiScope(Write, "Urgence", "Déclencher et lever une urgence", Emergencies.AppId),
        new ApiScope(Read, "Urgence", "Lire les urgences et leur rapport de réception", Emergencies.AppId),
    };

    public IEnumerable<ApiTemplate> Templates { get; } = new[]
    {
        new ApiTemplate("Alerte d'urgence seule", "Déclencher et lever une urgence (centrale d'alarme, contrôle d'accès)", new[] { Write }, 20),
        new ApiTemplate("Lecture seule", "Consulter les urgences", new[] { Read }, 10),
    };

    public IEnumerable<EventType> Events { get; } = new[]
    {
        new EventType("emergency.started", "Urgence", "Une urgence est déclenchée et part sur les écrans", Emergencies.AppId),
        new EventType("emergency.ended", "Urgence", "Une urgence est levée", Emergencies.AppId),
        new EventType("emergency.acknowledged", "Urgence", "Un écran confirme afficher l'urgence", Emergencies.AppId),
    };

    public static readonly ApiRoute Start = new("POST", "/emergency", Write, RateClass.Urgent, "Déclencher une urgence : plein écran rouge, tout de suite, jusqu'à ce qu'on la lève",
        Body: "{ \"title\": \"Évacuation\", \"message\": \"Évacuation du bâtiment B. Quittez les lieux par les issues de secours.\", \"targets\": { \"zones\": [\"4f0c0000-0000-0000-0000-000000000001\"] }, \"source\": \"Centrale incendie bâtiment B\" }");
    public static readonly ApiRoute List = new("GET", "/emergency", Read, RateClass.Read, "Lister les urgences (en cours par défaut ; status=all pour l'historique)",
        Query: "status:string:active (par défaut) ou all; limit:integer:1 à 100 (50 par défaut)");
    public static readonly ApiRoute One = new("GET", "/emergency/{id}", Read, RateClass.Read, "Lire une urgence et son rapport de réception écran par écran");
    public static readonly ApiRoute Lift = new("DELETE", "/emergency/{id}", Write, RateClass.Urgent, "Lever une urgence (sans effet si elle est déjà levée)");

    public IEnumerable<ApiRoute> Routes { get; } = new[] { Start, List, One, Lift };

    public record TargetsBody(bool? All, List<Guid>? Areas, List<Guid>? Zones, List<Guid>? Screens);
    public record StartBody(string? Title, string? Message, TargetsBody? Targets, string? Source);

    private static object Dto(Emergency e, EmergencyReport? r, bool withScreens, DateTime now) => new
    {
        id = e.Id, status = e.IsActive(now) ? "active" : "ended", title = e.Title, message = e.Message, startedAt = e.StartedUtc, endedAt = e.EndedUtc,
        startedBy = e.StartedBy, endedBy = e.EndedBy, via = e.Via, targets = new { kind = e.TargetKind, ids = e.TargetIds },
        report = r == null ? null : new { targeted = r.Targeted, displayed = r.Displayed, waking = r.Waking, pending = r.Pending, offline = r.Offline },
        screens = withScreens && r != null ? r.Lines.Select(l => new { id = l.ScreenId, name = l.Name, state = l.State, wokeFromSleep = l.Woke, at = l.AtUtc }) : null,
    };

    private static string Who(HttpContext ctx, string? source) => string.IsNullOrWhiteSpace(source) ? $"clé « {ctx.User.FindFirst(ApiAuth.KeyName)?.Value} »" : source.Trim();

    public void Map(ApiRouter r)
    {
        r.Map(Start, (HttpContext ctx, EmergencyService svc, StartBody? body) =>
        {
            var t = body?.Targets;
            string kind; List<Guid> ids;
            var lists = new[] { (t?.All == true), t?.Areas is { Count: > 0 }, t?.Zones is { Count: > 0 }, t?.Screens is { Count: > 0 } }.Count(x => x);
            if (lists != 1) return ApiProblem.Result(ctx, ApiCodes.InvalidRequest, "targets : indiquez soit « all »: true, soit une liste « areas », « zones » ou « screens ».");
            if (t!.All == true) { kind = "all"; ids = new(); }
            else if (t.Areas is { Count: > 0 }) { kind = "areas"; ids = t.Areas; }
            else if (t.Zones is { Count: > 0 }) { kind = "zones"; ids = t.Zones; }
            else { kind = "screens"; ids = t.Screens!; }
            var cid = ctx.User.ClientId();
            var res = svc.Trigger(cid, new EmergencyRequest(body?.Title, body?.Message, kind, ids, false, "api", Who(ctx, body?.Source), null, ctx.User.KeyId()));
            if (res.Emergency is not { } e)
                return ApiProblem.Result(ctx, res.Code switch { EmergencyService.NotInstalled => ApiCodes.IntegrationMissing, EmergencyService.PlanRequired => ApiCodes.PlanRequired, EmergencyService.Suspended => ApiCodes.SpaceSuspended, _ => ApiCodes.InvalidRequest }, res.Error);
            var now = DateTime.UtcNow;
            return Results.Json(Dto(e, svc.Report(cid, e.Id), false, now), ApiJson.Options, statusCode: 201);
        });

        r.Map(List, (HttpContext ctx, EmergencyService svc, string? status, int? limit) =>
        {
            var cid = ctx.User.ClientId();
            var now = DateTime.UtcNow;
            var items = status == "all" ? svc.History(cid, Math.Clamp(limit ?? 50, 1, 100)) : svc.Active(cid).Where(e => !e.Test).Take(Math.Clamp(limit ?? 50, 1, 100)).ToList();
            return Results.Json(new { data = items.Select(e => Dto(e, null, false, now)) }, ApiJson.Options);
        });

        r.Map(One, (HttpContext ctx, EmergencyService svc, Guid id) =>
        {
            var cid = ctx.User.ClientId();
            return svc.Report(cid, id) is { } rep && !rep.Emergency.Test ? Results.Json(Dto(rep.Emergency, rep, true, DateTime.UtcNow), ApiJson.Options) : ApiProblem.Result(ctx, ApiCodes.NotFound);
        });

        r.Map(Lift, (HttpContext ctx, EmergencyService svc, Guid id) =>
        {
            var cid = ctx.User.ClientId();
            if (svc.Get(cid, id) is not { Test: false } e) return ApiProblem.Result(ctx, ApiCodes.NotFound);
            svc.Lift(cid, id, Who(ctx, null));
            var after = svc.Get(cid, id)!;
            return Results.Json(new { id = after.Id, status = "ended", endedAt = after.EndedUtc, endedBy = after.EndedBy }, ApiJson.Options);
        });
    }
}
