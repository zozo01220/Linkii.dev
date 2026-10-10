using System.Collections;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Linkii.Poc;

/// <summary>
/// Filtre global d'isolation : cette liste ne voit, n'ajoute et ne supprime que les lignes de son client.
/// Tout le code du back-office passe par elle (via <see cref="ClientDb"/>), jamais par la liste brute.
/// </summary>
public class ScopedList<T>(List<T> root, Guid clientId, Guid? areaId = null) : IEnumerable<T> where T : IClientOwned
{
    // Avec une aire ouverte, les écrans, listes de lecture et médias des autres aires sont invisibles.
    private bool In(T x) => x.ClientId == clientId && (areaId == null || x is not IAreaOwned a || a.AreaId == areaId || Shared(x));

    /// <summary>Fichiers des dossiers Drive : les connexions sont celles de l'organisation, ils sont communs à toutes les aires.</summary>
    private static bool Shared(T x) => x is MediaItem m && m.AreaId == Guid.Empty && m.Info.ContainsKey("drive");

    public IEnumerator<T> GetEnumerator() => root.Where(In).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int Count => root.Count(In);

    /// <summary>Ajoute une ligne au client, rangée dans l'aire ouverte.</summary>
    public void Add(T item)
    {
        item.ClientId = clientId;
        if (areaId is { } a && item is IAreaOwned o && o.AreaId == Guid.Empty && !Shared(item)) o.AreaId = a;
        root.Add(item);
    }
    public int RemoveAll(Predicate<T> match) => root.RemoveAll(x => In(x) && match(x));
}

/// <summary>
/// Aires de gestion de la personne connectée dans une organisation : un administrateur de l'organisation (ou l'équipe Linkii, un revendeur
/// qui entre dans l'espace) les a toutes ; un membre a celles où il a un rôle. <see cref="Current"/> est l'aire ouverte.
/// </summary>
public class AreaAccess
{
    public Guid? UserId { get; init; }
    public bool IsOrgAdmin { get; init; } = true;
    /// <summary>Rôle dans chaque aire accessible (administrateur pour toutes, pour un administrateur de l'organisation).</summary>
    public Dictionary<Guid, string> Roles { get; init; } = new();
    /// <summary>Aires accessibles, de la plus ancienne à la plus récente.</summary>
    public List<Area> Areas { get; init; } = new();
    public Area? Current { get; init; }
    /// <summary>Nombre d'aires de l'organisation (toutes, accessibles ou non).</summary>
    public int OrgAreaCount { get; init; }

    /// <summary>Traitements internes et tests (aucune personne connectée) : aucun filtre d'aire.</summary>
    public static readonly AreaAccess System = new();

    public bool CanManage(Guid areaId) => IsOrgAdmin || Roles.GetValueOrDefault(areaId) == AreaRoles.Admin;
    /// <summary>Zones et membres de l'aire ouverte.</summary>
    public bool ManagesCurrent => Current != null && CanManage(Current.Id);
    public bool ManagesAnyArea => IsOrgAdmin || Roles.ContainsValue(AreaRoles.Admin);

    public static AreaAccess For(Db db, Guid clientId, Guid? userId)
    {
        var areas = db.Areas.Where(a => a.ClientId == clientId).OrderBy(a => a.CreatedUtc).ToList();
        var u = db.Users.FirstOrDefault(x => x.Id == userId);
        var org = u?.Role != Linkii.Poc.Roles.ClientMember;
        var roles = org ? areas.ToDictionary(a => a.Id, _ => AreaRoles.Admin)
            : u!.AreaRoles.Where(r => areas.Any(a => a.Id == r.AreaId)).GroupBy(r => r.AreaId).ToDictionary(g => g.Key, g => g.First().Role);
        var mine = areas.Where(a => roles.ContainsKey(a.Id)).ToList();
        Guid? last = u != null && u.LastAreas.TryGetValue(clientId, out var l) ? l : null;
        return new AreaAccess
        {
            UserId = userId, IsOrgAdmin = org, Roles = roles, Areas = mine, OrgAreaCount = areas.Count,
            Current = mine.FirstOrDefault(a => a.Id == last) ?? mine.FirstOrDefault()
        };
    }
}

/// <summary>Vue de <see cref="Db"/> limitée à un client (et à l'aire ouverte, s'il y en a une) : mêmes noms que la racine, mais isolés.</summary>
public class ClientDb
{
    private readonly Db _root;
    public Guid ClientId { get; }
    public AreaAccess Access { get; }

    /// <summary>Aire qui ne correspond à rien : un membre sans aire ne voit aucun écran, aucune liste, aucun média.</summary>
    private static readonly Guid NoArea = new("00000000-0000-0000-0000-00000000a0e0");

    public ClientDb(Db root, Guid clientId, AreaAccess? access = null)
    {
        _root = root;
        ClientId = clientId;
        Access = access ?? AreaAccess.System;
        // Sans personne connectée (traitements internes, tests) ou pour une organisation encore sans aire : pas de filtre d'aire.
        Guid? area = Access == AreaAccess.System || Access.OrgAreaCount == 0 ? null : Access.Current?.Id ?? NoArea;
        Screens = new(root.Screens, clientId, area);
        Media = new(root.Media, clientId, area);
        Playlists = new(root.Playlists, clientId, area);
        AppInstalls = new(root.AppInstalls, clientId);
        Areas = new(root.Areas, clientId);
    }

    internal Db Root => _root;
    public Tenant Tenant => _root.Clients.First(c => c.Id == ClientId);
    public Reseller? Reseller => _root.Resellers.FirstOrDefault(r => r.Id == Tenant.ResellerId);
    public ScopedList<Screen> Screens { get; }
    public ScopedList<MediaItem> Media { get; }
    public ScopedList<Playlist> Playlists { get; }
    public ScopedList<AppInstall> AppInstalls { get; }
    /// <summary>Toutes les aires de l'organisation (pages réservées aux administrateurs).</summary>
    public ScopedList<Area> Areas { get; }
    /// <summary>Aire ouverte (null : membre sans aire, ou organisation sans aire).</summary>
    public Area? Area => Access.Current is { } a ? _root.Areas.FirstOrDefault(x => x.Id == a.Id) : null;

    /// <summary>Comptes de l'organisation (administrateurs et membres).</summary>
    public IEnumerable<User> Users => _root.Users.Where(u => u.ClientId == ClientId && Roles.IsClient(u.Role));
    public void AddUser(User u) { u.ClientId = ClientId; u.ResellerId = Tenant.ResellerId; _root.Users.Add(u); }
    public int RemoveUser(Guid id) => _root.Users.RemoveAll(u => u.Id == id && u.ClientId == ClientId && Roles.IsClient(u.Role));
    /// <summary>Adresse déjà prise chez ce revendeur (ou par l'équipe Linkii) : un compte se connecte par son e-mail sur le domaine du revendeur.</summary>
    public bool EmailTaken(string email) => _root.Users.Any(u => u.Email == email && (u.ResellerId == Tenant.ResellerId || u.ResellerId == null));

    /// <summary>Le code à 6 chiffres affiché par un écran non appairé identifie le client : il en prend possession, dans l'aire ouverte.</summary>
    public Screen? ClaimScreen(string code)
    {
        var s = _root.Screens.FirstOrDefault(x => x.Token == null && x.PairingCode == code);
        if (s == null) return null;
        s.ClientId = ClientId;
        if (Area is { } a) { s.AreaId = a.Id; s.ZoneId = AreaOps.EnsureDefaultZone(a).Id; }   // zone par défaut ; l'appairage peut en choisir une autre
        return s;
    }

    public Helpers.DeliveryInfo? Delivery(Screen s) => Helpers.Delivery(s, Playlists.FirstOrDefault(p => p.Id == s.PlaylistId), Tenant, Reseller, Helpers.SyncStamp(_root.Areas, s), DateTime.UtcNow);

    public string Status(Screen s) => Helpers.Status(s, Playlists.FirstOrDefault(p => p.Id == s.PlaylistId), Tenant, Reseller, Helpers.SyncStamp(_root.Areas, s));
}

public static class Claims
{
    public const string Client = "client";
    public const string Reseller = "reseller";
    public const string Impersonating = "impersonating";

    public static ClaimsPrincipal Build(Db db, User u, Guid? clientId = null, bool impersonating = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(u.Name) ? u.Email : u.Name),
            new(ClaimTypes.Email, u.Email),
            new(ClaimTypes.Role, u.Role),
        };
        var cid = clientId ?? u.ClientId;
        var rid = u.ResellerId;
        if (cid != null)
        {
            claims.Add(new(Client, cid.Value.ToString()));
            rid ??= db.Clients.FirstOrDefault(c => c.Id == cid)?.ResellerId;   // l'équipe Linkii prend la marque du client visité
        }
        if (rid != null) claims.Add(new(Reseller, rid.Value.ToString()));
        if (impersonating) claims.Add(new(Impersonating, "1"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }

    public static Guid? GetGuid(this ClaimsPrincipal p, string type) =>
        System.Guid.TryParse(p.FindFirst(type)?.Value, out var g) ? g : null;

    public static string Home(ClaimsPrincipal p) =>
        p.GetGuid(Client) != null ? "/" : p.IsInRole(Roles.PlatformAdmin) ? "/admin" : p.IsInRole(Roles.ResellerAdmin) ? "/reseller" : "/";
}

/// <summary>Qui est connecté, pour quel client et chez quel revendeur (un circuit Blazor = un utilisateur).</summary>
public class TenantContext(AuthenticationStateProvider auth, JsonStore store)
{
    private ClaimsPrincipal? _user;
    public ClaimsPrincipal User => _user ??= auth.GetAuthenticationStateAsync().GetAwaiter().GetResult().User;

    public Guid? UserId => User.GetGuid(ClaimTypes.NameIdentifier);
    public Guid? ClientId => User.GetGuid(Claims.Client);
    public Guid? ResellerId => User.GetGuid(Claims.Reseller);
    public bool IsImpersonating => User.HasClaim(c => c.Type == Claims.Impersonating);
    public bool IsPlatformAdmin => User.IsInRole(Roles.PlatformAdmin);
    public bool IsResellerAdmin => User.IsInRole(Roles.ResellerAdmin);
    /// <summary>Membre d'une organisation (rôle par aire) ; sinon administrateur de l'espace ouvert.</summary>
    public bool IsClientMember => User.IsInRole(Roles.ClientMember);
    public string Email => User.FindFirst(ClaimTypes.Email)?.Value ?? "";
    public string DisplayName => User.Identity?.Name ?? Email;

    /// <summary>Échoue (ferme l'accès) plutôt que de retomber sur un autre client.</summary>
    public Guid RequireClientId() => ClientId ?? throw new UnauthorizedAccessException("Aucun espace client sélectionné.");
    public Guid RequireResellerId() => ResellerId ?? throw new UnauthorizedAccessException("Aucun revendeur associé à ce compte.");

    public Reseller? Reseller => ResellerId is { } id ? store.Read(d => d.Resellers.FirstOrDefault(r => r.Id == id)) : null;
}

/// <summary>Accès aux données du client connecté uniquement (même API que <see cref="JsonStore"/>).</summary>
public class TenantStore(JsonStore root, TenantContext ctx)
{
    public Guid ClientId => ctx.RequireClientId();

    public Guid? UserId => ctx.UserId;
    /// <summary>Aire de gestion ouverte (null : organisation sans aire, ou membre sans aire).</summary>
    public Guid? AreaId => Read(d => d.Area?.Id);

    // Aires et rôles relus à chaque accès : un changement d'aire ou de rôle s'applique aussitôt.
    private ClientDb Open(Db db, Guid cid) => new(db, cid, AreaAccess.For(db, cid, ctx.UserId));

    public T Read<T>(Func<ClientDb, T> f) { var cid = ClientId; return root.Read(db => f(Open(db, cid))); }
    public T Write<T>(Func<ClientDb, T> f) { var cid = ClientId; return root.Write(db => f(Open(db, cid))); }
    public void Write(Action<ClientDb> a) { var cid = ClientId; root.Write(db => a(Open(db, cid))); }

    /// <summary>Ouvre une autre aire (mémorisée pour la personne connectée). Retourne false si elle ne lui est pas accessible.</summary>
    public bool SwitchArea(Guid areaId) => Write(d =>
    {
        if (!d.Access.Areas.Any(a => a.Id == areaId) || d.Root.Users.FirstOrDefault(u => u.Id == ctx.UserId) is not { } me) return false;
        me.LastAreas[d.ClientId] = areaId;
        return true;
    });
}

/// <summary>Résolution du revendeur à partir du nom de domaine de la requête : créer un revendeur ne demande aucun déploiement.</summary>
public class ResellerResolver(JsonStore store, IConfiguration config)
{
    private string BaseDomain => (config["Linkii:BaseDomain"] ?? "linkii.com").ToLowerInvariant();

    /// <summary>Retourne le revendeur de ce domaine, le revendeur par défaut pour un domaine neutre (localhost, IP…), ou null si le sous-domaine est inconnu.</summary>
    public Reseller? Resolve(string? host) => store.Read(db => ResolveIn(db, host));

    public Reseller? ResolveIn(Db db, string? host)
    {
        var h = (host ?? "").Trim().ToLowerInvariant();
        var colon = h.LastIndexOf(':');
        if (colon > 0 && !h.EndsWith(']')) h = h[..colon];

        var byDomain = db.Resellers.FirstOrDefault(r => !string.IsNullOrEmpty(r.CustomDomain) && r.CustomDomain.Equals(h, StringComparison.OrdinalIgnoreCase));
        if (byDomain != null) return byDomain;

        foreach (var baseDomain in new[] { BaseDomain, "localhost" })
        {
            if (h == baseDomain) return Default(db);
            if (h.EndsWith("." + baseDomain))
            {
                var slug = h[..^(baseDomain.Length + 1)];
                return db.Resellers.FirstOrDefault(r => r.Slug == slug);   // sous-domaine inconnu => null
            }
        }
        return Default(db);
    }

    public static Reseller? Default(Db db) => db.Resellers.FirstOrDefault(r => r.IsDefault) ?? db.Resellers.FirstOrDefault();

    /// <summary>Adresse de connexion d'un revendeur. currentBase : adresse du back-office en cours ; reprise telle quelle
    /// (schéma, port) si elle sert le même revendeur, ce qui donne un lien juste aussi en développement.</summary>
    public string LoginUrl(Reseller r, string? currentBase = null)
    {
        if (Uri.TryCreate(currentBase, UriKind.Absolute, out var cur) && Resolve(cur.Host)?.Id == r.Id)
            return cur.GetLeftPart(UriPartial.Authority) + "/login";
        var host = !string.IsNullOrWhiteSpace(r.CustomDomain) ? r.CustomDomain.Trim().ToLowerInvariant()
                 : r.IsDefault ? BaseDomain : r.Slug + "." + BaseDomain;
        return "https://" + host + "/login";
    }

    /// <summary>Domaine déclaré par un revendeur (utilisé par l'autorisation de certificat à la demande de Caddy).</summary>
    public bool IsKnownDomain(string? domain)
    {
        var d = (domain ?? "").Trim().ToLowerInvariant();
        if (d.Length == 0) return false;
        return store.Read(db => db.Resellers.Any(r => r.Active &&
            (!string.IsNullOrEmpty(r.CustomDomain) && r.CustomDomain.Equals(d, StringComparison.OrdinalIgnoreCase) || d == r.Slug + "." + BaseDomain)));
    }
}

/// <summary>Marque envoyée au player (écran d'appairage compris).</summary>
public record BrandDto(string Name, string Color, string? LogoUrl, bool PoweredBy)
{
    public static BrandDto From(Reseller? r) => r == null
        ? new("Linkii", "#0B1F3A", null, false)
        : new(r.DisplayName, r.BrandColor, r.LogoUrl, r.ShowPoweredBy && !r.IsDefault);
}

/// <summary>Comptes et aires d'une organisation, pour la console Linkii, l'espace revendeur et la facturation.</summary>
public record UserStats(int Total, int Admins, int Members, int Active30, int Invited, int Areas)
{
    public static UserStats Of(Db db, Tenant c)
    {
        var list = db.Users.Where(u => u.ClientId == c.Id && Roles.IsClient(u.Role)).ToList();
        var since = DateTime.UtcNow.AddDays(-30);
        return new(list.Count, list.Count(u => u.Role == Roles.ClientAdmin), list.Count(u => u.Role == Roles.ClientMember),
            list.Count(u => u.ActiveSince(since)), list.Count(u => u.IsInvited && !u.Disabled), db.Areas.Count(a => a.ClientId == c.Id));
    }

    /// <summary>« 1 administrateur · 4 membres ».</summary>
    public string Breakdown => $"{Admins} administrateur{(Admins > 1 ? "s" : "")}" + (Members > 0 ? $" · {Members} membre{(Members > 1 ? "s" : "")}" : "");
}

public static class Tenancy
{
    /// <summary>Un écran ne diffuse que si son client, puis son revendeur, sont actifs, et que l'essai gratuit du client n'est pas terminé.</summary>
    public static bool IsActive(Db db, Screen s) => Blocked(db, s) == null;

    /// <summary>Pourquoi un écran ne diffuse pas : « trial » (essai gratuit terminé), « suspended » (client ou revendeur suspendu), sinon null.</summary>
    public static string? Blocked(Db db, Screen s)
    {
        var c = db.Clients.FirstOrDefault(x => x.Id == s.ClientId);
        if (c == null || c.Suspended || db.Resellers.FirstOrDefault(r => r.Id == c.ResellerId)?.Active != true) return "suspended";
        return c.TrialEnded(DateTime.UtcNow) ? "trial" : null;
    }

    /// <summary>Un administrateur ne se connecte que sur le domaine de son revendeur ; l'équipe Linkii sur le domaine principal.</summary>
    public static bool HostAllows(User u, Reseller host) => u.Role == Roles.PlatformAdmin ? host.IsDefault : u.ResellerId == host.Id;

    public static string Slugify(string s)
    {
        var chars = s.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static readonly string[] ReservedSlugs = { "www", "app", "api", "admin", "player", "mail", "linkii" };
    public static bool IsValidSlug(string s) =>
        s.Length is >= 2 and <= 40 && s == Slugify(s) && !ReservedSlugs.Contains(s);

    /// <summary>Crée un client et son premier administrateur. Retourne null avec un message si l'e-mail est déjà pris chez ce revendeur.
    /// password null : compte Microsoft ou Google, sans mot de passe Linkii. source : voir <see cref="SignupSources"/>.</summary>
    public static (Tenant? Client, string? Error) CreateClient(Db db, Reseller reseller, string clientName, string adminName, string email, string? password, bool mustChangePassword = false, string? source = null)
    {
        email = email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(clientName) || string.IsNullOrWhiteSpace(email)) return (null, "Nom du client et e-mail obligatoires.");
        if (password != null && password.Length < 8) return (null, "Mot de passe trop court (8 caractères minimum).");
        if (db.Users.Any(u => u.Email == email && (u.ResellerId == reseller.Id || u.ResellerId == null)))
            return (null, "Cette adresse e-mail est déjà utilisée.");
        var client = new Tenant { ResellerId = reseller.Id, Name = clientName.Trim(), SignupSource = source };
        var firstArea = new Area { ClientId = client.Id, Name = Seed.DefaultAreaName };   // une aire dès le départ : invisible tant qu'elle est seule
        AreaOps.EnsureDefaultZone(firstArea);
        db.Areas.Add(firstArea);
        db.Clients.Add(client);
        db.Users.Add(new User { Email = email, Name = adminName.Trim(), PasswordHash = password == null ? "" : Passwords.Hash(password), Role = Roles.ClientAdmin, ResellerId = reseller.Id, ClientId = client.Id, MustChangePassword = mustChangePassword, CreatedUtc = DateTime.UtcNow });
        return (client, null);
    }
}

/// <summary>Démarrage : migration de l'ancien data.json mono-client, revendeur par défaut, compte de la plateforme.</summary>
public static class Seed
{
    public static void Run(JsonStore store, IConfiguration config, string dataPath, ILogger log)
    {
        var banner = (string?)null;
        // zone par défaut dans chaque aire (ajoutée le 9 octobre 2026) : sauvegarde avant la première migration
        if (store.Read(db => db.Areas.Any(a => !a.Zones.Any(z => z.IsDefault))) && File.Exists(dataPath))
            try { File.Copy(dataPath, dataPath + ".pre-zone-defaut.bak", overwrite: false); } catch { }
        store.Write(db =>
        {
            if (db.Resellers.Count == 0)
                db.Resellers.Add(new Reseller { Name = "Linkii", Slug = "linkii", BrandName = "Linkii", IsDefault = true, AllowSelfSignup = true });
            else if (!db.Resellers.Any(r => r.IsDefault))
                db.Resellers[0].IsDefault = true;
            var def = db.Resellers.First(r => r.IsDefault);

            MigrateLegacy(db, def, dataPath, log);
            PurgeWidgets(db, dataPath, log);
            MigrateGoogleAccount(db);
            EnsureAreas(db);
            Signup.MarkVerified(db);

            if (!db.Users.Any(u => u.Role == Roles.PlatformAdmin))
            {
                var email = (config["Linkii:PlatformAdmin:Email"] ?? "admin@linkii.local").Trim().ToLowerInvariant();
                var pwd = config["Linkii:PlatformAdmin:Password"];
                var generated = string.IsNullOrEmpty(pwd);
                if (generated) pwd = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
                db.Users.RemoveAll(u => u.Email == email && u.ResellerId == null);
                db.Users.Add(new User { Email = email, Name = "Administrateur Linkii", PasswordHash = Passwords.Hash(pwd!), Role = Roles.PlatformAdmin });
                banner = generated ? $"Compte administrateur de la plateforme créé : {email} / {pwd}  (à changer ; affiché une seule fois)"
                                   : $"Compte administrateur de la plateforme créé : {email}";
            }
        });
        if (banner != null) log.LogWarning("{Banner}", banner);
    }

    /// <summary>
    /// Apps retirées du catalogue (manifeste supprimé) : leurs contenus, leurs activations et leurs éléments déjà publiés sont retirés,
    /// pour qu'aucun écran n'affiche un emplacement vide. Une sauvegarde de data.json est faite avant (data.json.pre-apps-retirees.bak).
    /// </summary>
    public static readonly string[] RetiredApps = { "linkedin", "menu-api", "menu-csv", "weather-forecast", "room" };   // retirées le 7 octobre 2026 (code archivé dans _archive/)

    public static void PurgeRetiredApps(JsonStore store, AppCatalog catalog, string dataPath, ILogger log)
    {
        // liste explicite : un manifeste devenu illisible ne doit jamais faire effacer les contenus de son app
        bool Retired(string? appId) => appId != null && RetiredApps.Contains(appId) && catalog.Find(appId) == null;
        var any = store.Read(db => db.Media.Any(m => Retired(m.AppId)) || db.AppInstalls.Any(a => Retired(a.AppId))
            || db.Screens.Any(s => s.Published.Any(x => Retired(x.AppId))) || db.Playlists.Any(p => p.Published.Any(x => Retired(x.AppId))));
        if (!any) return;
        try { File.Copy(dataPath, dataPath + ".pre-apps-retirees.bak", overwrite: false); } catch { }
        var (contents, apps) = store.Write(db =>
        {
            var ids = db.Media.Where(m => Retired(m.AppId)).Select(m => m.Id).ToHashSet();
            var names = db.Media.Where(m => ids.Contains(m.Id)).Select(m => m.AppId!).Concat(db.AppInstalls.Where(a => Retired(a.AppId)).Select(a => a.AppId)).Distinct().ToList();
            db.Media.RemoveAll(m => ids.Contains(m.Id));
            db.AppInstalls.RemoveAll(a => Retired(a.AppId));
            foreach (var p in db.Playlists)
            {
                if (p.Draft.RemoveAll(i => ids.Contains(i.MediaId)) > 0) p.Touch();
                if (p.Published.RemoveAll(x => Retired(x.AppId)) > 0) p.PublishedVersion++;   // les écrans relisent leur liste
            }
            foreach (var s in db.Screens)
                if (s.Published.RemoveAll(x => Retired(x.AppId)) > 0) s.PublishedVersion++;
            return (ids.Count, names);
        });
        log.LogInformation("Apps retirées du catalogue ({Apps}) : {Count} contenu(s) supprimé(s) (sauvegarde : data.json.pre-apps-retirees.bak).", string.Join(", ", apps), contents);
    }

    /// <summary>
    /// Calendrier partagé (8 octobre 2026) : la connexion se règle une fois par source dans Intégrations, plus dans chaque contenu.
    /// Les contenus « Agenda » réglés à l'ancienne sont supprimés, avec leurs éléments déjà publiés. Les clients qui avaient l'app
    /// retrouvent les liens ICS (et Microsoft 365, s'il était configuré) activés. Sauvegarde : data.json.pre-calendriers.bak.
    /// </summary>
    public static void PurgeOldCalendars(JsonStore store, string dataPath, ILogger log)
    {
        static bool Old(string? appId, Dictionary<string, string> s) => appId == "agenda" && !s.ContainsKey("calendar");
        static bool Untouched(Db db, Tenant c) => c.CalendarAccounts.Count == 0 && db.AppInstalls.Any(a => a.ClientId == c.Id && a.AppId == "agenda");
        var any = store.Read(db => db.Media.Any(m => Old(m.AppId, m.Settings))
            || db.Playlists.Any(p => p.Published.Any(x => Old(x.AppId, x.Settings)))
            || db.Screens.Any(s => s.Published.Concat(s.PublishedZones.SelectMany(z => z)).Any(x => Old(x.AppId, x.Settings)))
            || db.Clients.Any(c => Untouched(db, c)));
        if (!any) return;
        try { File.Copy(dataPath, dataPath + ".pre-calendriers.bak", overwrite: false); } catch { }
        var removed = store.Write(db =>
        {
            var ids = db.Media.Where(m => Old(m.AppId, m.Settings)).Select(m => m.Id).ToHashSet();
            db.Media.RemoveAll(m => ids.Contains(m.Id));
            foreach (var p in db.Playlists)
            {
                if (p.Draft.RemoveAll(i => ids.Contains(i.MediaId)) > 0) p.Touch();
                if (p.Published.RemoveAll(x => Old(x.AppId, x.Settings)) > 0) p.PublishedVersion++;   // les écrans relisent leur liste
            }
            foreach (var s in db.Screens)
                if (s.Published.RemoveAll(x => Old(x.AppId, x.Settings)) + s.PublishedZones.Sum(z => z.RemoveAll(x => Old(x.AppId, x.Settings))) > 0) s.PublishedVersion++;
            foreach (var c in db.Clients.Where(c => Untouched(db, c)))
            {
                c.CalendarAccounts.Add(new CalendarAccount { Source = "ics", Enabled = true });
            }
            return ids.Count;
        });
        if (removed > 0) log.LogInformation("Calendrier partagé : {Count} ancien(s) contenu(s) Agenda supprimé(s) (sauvegarde : data.json.pre-calendriers.bak).", removed);
    }

    /// <summary>
    /// Compte Google : la connexion d'abord réservée à Google Drive (GoogleDrive*) devient le compte Google de l'organisation,
    /// partagé avec Google Calendar. Son accès Drive est conservé ; l'accès aux agendas se demande depuis Google Calendar.
    /// </summary>
    public const string DefaultAreaName = "Principale";

    /// <summary>
    /// Aires de gestion : chaque organisation en a au moins une. Les écrans, listes de lecture et médias sans aire (données d'avant les aires)
    /// rejoignent la plus ancienne ; les fichiers des dossiers Drive restent communs à toutes les aires.
    /// </summary>
    public static void EnsureAreas(Db db)
    {
        foreach (var c in db.Clients)
        {
            var first = db.Areas.Where(a => a.ClientId == c.Id).OrderBy(a => a.CreatedUtc).FirstOrDefault();
            if (first == null) db.Areas.Add(first = new Area { ClientId = c.Id, Name = DefaultAreaName, CreatedUtc = c.CreatedUtc });
            foreach (var s in db.Screens.Where(x => x.ClientId == c.Id && x.AreaId == Guid.Empty)) s.AreaId = first.Id;
            foreach (var p in db.Playlists.Where(x => x.ClientId == c.Id && x.AreaId == Guid.Empty)) p.AreaId = first.Id;
            foreach (var m in db.Media.Where(x => x.ClientId == c.Id && x.AreaId == Guid.Empty && !x.Info.ContainsKey("drive"))) m.AreaId = first.Id;
        }
        // Chaque aire a sa zone par défaut ; tout écran de l'aire est dans une zone de l'aire (sinon : la zone par défaut).
        foreach (var a in db.Areas)
        {
            var def = AreaOps.EnsureDefaultZone(a);
            foreach (var s in db.Screens.Where(x => x.AreaId == a.Id && !a.Zones.Any(z => z.Id == x.ZoneId))) s.ZoneId = def.Id;
        }
    }

    private static void MigrateGoogleAccount(Db db)
    {
        foreach (var c in db.Clients.Where(c => c.GoogleDriveRefreshToken != null))
        {
            if (c.GoogleRefreshToken.Length == 0 && c.GoogleDriveRefreshToken!.Length > 0)
            {
                c.GoogleRefreshToken = c.GoogleDriveRefreshToken;
                c.GoogleScopes = GoogleAuth.DriveScope + " openid https://www.googleapis.com/auth/userinfo.email";
                c.GoogleUser = c.GoogleDriveUser ?? "";
                c.GoogleConnectedUtc = c.GoogleDriveConnectedUtc;
            }
            c.GoogleDriveRefreshToken = null; c.GoogleDriveUser = null; c.GoogleDriveConnectedUtc = null;
        }
    }

    /// <summary>Les widgets (contenus de type « app » sans app du catalogue) n'existent plus : on les retire des médiathèques et des playlists. Une sauvegarde de data.json est faite avant.</summary>
    public static void PurgeWidgets(Db db, string dataPath, ILogger log)
    {
        var ids = db.Media.Where(m => m.Type == "app" && string.IsNullOrEmpty(m.AppId)).Select(m => m.Id).ToHashSet();
        var stale = db.Playlists.Any(p => p.Published.Any(x => x.Type == "app" && string.IsNullOrEmpty(x.AppId)));
        if (ids.Count == 0 && !stale) return;
        try { File.Copy(dataPath, dataPath + ".pre-widgets.bak", overwrite: false); } catch { }
        db.Media.RemoveAll(m => ids.Contains(m.Id));
        foreach (var p in db.Playlists)
        {
            if (p.Draft.RemoveAll(i => ids.Contains(i.MediaId)) > 0) p.Touch();
            if (p.Published.RemoveAll(x => x.Type == "app" && string.IsNullOrEmpty(x.AppId)) > 0) p.PublishedVersion++;   // les écrans relisent la playlist
        }
        log.LogInformation("{Count} widget(s) retiré(s) des médiathèques et des playlists (sauvegarde : data.json.pre-widgets.bak).", ids.Count);
    }

    private static void MigrateLegacy(Db db, Reseller def, string dataPath, ILogger log)
    {
        var old = db.Tenant;
        if (old == null) return;
        var hasData = !string.IsNullOrEmpty(old.PasswordHash) || db.Screens.Count + db.Media.Count + db.Playlists.Count > 0;
        if (hasData && db.Clients.Count == 0)
        {
            try { File.Copy(dataPath, dataPath + ".pre-multitenant.bak", overwrite: false); } catch { }
            old.ResellerId = def.Id;
            if (string.IsNullOrWhiteSpace(old.Name)) old.Name = "Mon organisation";
            var client = new Tenant
            {
                Id = old.Id, ResellerId = def.Id, Name = old.Name, Timezone = old.Timezone, DefaultDurationSec = old.DefaultDurationSec,
                ReloadHour = old.ReloadHour,
                DefaultScreenType = old.DefaultScreenType, DefaultOrientation = old.DefaultOrientation, DefaultResolution = old.DefaultResolution
            };
            db.Clients.Add(client);
            if (!string.IsNullOrEmpty(old.PasswordHash))
                db.Users.Add(new User { Email = old.AdminEmail.Trim().ToLowerInvariant(), Name = old.AdminName, PasswordHash = old.PasswordHash, Role = Roles.ClientAdmin, ResellerId = def.Id, ClientId = client.Id });
            foreach (var s in db.Screens.Where(s => s.ClientId == Guid.Empty && s.Token != null)) s.ClientId = client.Id;
            foreach (var m in db.Media.Where(m => m.ClientId == Guid.Empty)) m.ClientId = client.Id;
            foreach (var p in db.Playlists.Where(p => p.ClientId == Guid.Empty)) p.ClientId = client.Id;
            log.LogInformation("Données mono-client migrées vers le client « {Name} » (sauvegarde : data.json.pre-multitenant.bak).", client.Name);
        }
        db.Tenant = null;
    }
}
