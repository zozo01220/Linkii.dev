using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Linkii.Poc;

/// <summary>Pages que l'on peut offrir à un niveau de droits. Intégrations et Widgets ne se cochent pas : leur page apparaît dès qu'une app (ou un widget) est autorisée.</summary>
public static class LevelPages
{
    public const string Screens = "screens", Playlists = "playlists", Planning = "planning", Media = "media";
    public const string Integrations = "integrations", Widgets = "widgets";

    /// <summary>Cases du niveau, dans l'ordre du menu.</summary>
    public static readonly string[] Offered = { Screens, Playlists, Planning, Media };
    /// <summary>Pages d'un membre (celles qui ont une politique d'accès « Page:… »).</summary>
    public static readonly string[] All = { Screens, Playlists, Planning, Media, Integrations, Widgets };

    public static string Label(string page) => page switch
    {
        Screens => "Écrans", Playlists => "Listes de lecture", Planning => "Planning", Media => "Médiathèque",
        Integrations => "Intégrations", Widgets => "Widgets", _ => page
    };

    /// <summary>Adresse d'une page, pour renvoyer un membre vers la première qui lui est ouverte.</summary>
    public static string Path(string page) => page switch
    {
        Screens => "/", Playlists => "/playlists", Planning => "/planning", Media => "/media",
        Integrations => "/integrations", Widgets => "/widgets", _ => "/"
    };
}

/// <summary>
/// Niveau de droits (créé par un administrateur de l'organisation, donné à des membres) : pages visibles, apps utilisables et périmètre
/// (aires, zones, écrans). Un membre a un seul niveau ; un administrateur de l'organisation n'en a pas, il a tous les droits.
/// </summary>
public class AccessLevel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Pages cochées (<see cref="LevelPages.Offered"/>).</summary>
    public List<string> Pages { get; set; } = new();
    /// <summary>Toutes les intégrations / tous les widgets, y compris ceux ajoutés plus tard au catalogue.</summary>
    public bool AllIntegrations { get; set; }
    public bool AllWidgets { get; set; }
    /// <summary>Identifiants des apps (intégrations et widgets) autorisées une à une.</summary>
    public List<string> Apps { get; set; } = new();
    /// <summary>Voir sans modifier ni publier.</summary>
    public bool ReadOnly { get; set; }

    // Périmètre : un élément coché donne tout ce qu'il contient, y compris ce qui y sera ajouté plus tard.
    public bool AllAreas { get; set; }
    public List<Guid> AreaIds { get; set; } = new();
    public List<Guid> ZoneIds { get; set; } = new();
    public List<Guid> ScreenIds { get; set; } = new();

    public bool HasScope => AllAreas || AreaIds.Count + ZoneIds.Count + ScreenIds.Count > 0;
    public bool CanUse(AppManifest a) => (a.ScreenWidget ? AllWidgets : AllIntegrations) || Apps.Contains(a.Id);

    public AccessLevel Copy() => new()
    {
        Id = Id, Name = Name, Pages = [.. Pages], AllIntegrations = AllIntegrations, AllWidgets = AllWidgets, Apps = [.. Apps], ReadOnly = ReadOnly,
        AllAreas = AllAreas, AreaIds = [.. AreaIds], ZoneIds = [.. ZoneIds], ScreenIds = [.. ScreenIds]
    };
}

/// <summary>Ce qu'un périmètre laisse voir, calculé une fois par accès (voir <see cref="AreaAccess.For"/>).</summary>
public class LevelScope
{
    public List<Area> Areas { get; }
    public Func<Screen, bool> Sees { get; }
    public Func<Guid, bool> SeesZone { get; }

    public LevelScope(AccessLevel l, List<Area> areas, IEnumerable<Screen> clientScreens)
    {
        var chosen = l.ScreenIds.Count > 0 ? clientScreens.Where(s => s.Token != null && l.ScreenIds.Contains(s.Id)).ToList() : new List<Screen>();
        var wholeZones = l.ZoneIds.ToHashSet();
        // un mur est entier : un seul de ses écrans coché donne tout le mur
        var touchedWalls = areas.SelectMany(a => a.Zones).Where(z => z.IsWall && chosen.Any(s => s.ZoneId == z.Id)).Select(z => z.Id);
        var zones = wholeZones.Concat(touchedWalls).ToHashSet();
        var withChosen = chosen.Where(s => s.ZoneId != null).Select(s => s.ZoneId!.Value).ToHashSet();
        var screenIds = chosen.Select(s => s.Id).ToHashSet();
        var areaIds = l.AreaIds.ToHashSet();
        bool whole(Guid a) => l.AllAreas || areaIds.Contains(a);

        Areas = areas.Where(a => whole(a.Id) || a.Zones.Any(z => zones.Contains(z.Id) || withChosen.Contains(z.Id)) || chosen.Any(s => s.AreaId == a.Id)).ToList();
        Sees = s => whole(s.AreaId) || s.ZoneId is { } z && zones.Contains(z) || screenIds.Contains(s.Id);
        var wholeAreas = areas.Where(a => whole(a.Id)).SelectMany(a => a.Zones).Select(z => z.Id).ToHashSet();
        SeesZone = z => wholeAreas.Contains(z) || zones.Contains(z) || withChosen.Contains(z);
    }
}

/// <summary>Refus d'une écriture : le niveau de droits de la personne est en lecture seule.</summary>
public class ReadOnlyLevelException() : UnauthorizedAccessException("Votre niveau de droits est en lecture seule : vous ne pouvez rien modifier ni publier.");

/// <summary>Création, enregistrement, suppression et migration des niveaux de droits. Chaque méthode retourne un message d'erreur, ou null si c'est fait.</summary>
public static class AccessLevels
{
    public const string DefaultName = "Utilisateur";

    /// <summary>Niveau proposé à toute organisation : écrans, listes de lecture et médiathèque, toutes les apps, toutes les aires. Ni planning ni réglages.</summary>
    public static AccessLevel NewDefault() => new()
    {
        Name = DefaultName, Pages = [LevelPages.Screens, LevelPages.Playlists, LevelPages.Media],
        AllIntegrations = true, AllWidgets = true, AllAreas = true
    };

    public static string? CheckName(string name) => name.Trim().Length == 0 ? "Donnez un nom au niveau." : name.Trim().Length > 60 ? "60 caractères au plus." : null;

    /// <summary>Ajoute (Id inconnu) ou remplace le niveau. L'effet est immédiat pour tous ses membres.</summary>
    public static string? Save(ClientDb d, AccessLevel level)
    {
        if (!d.Access.IsOrgAdmin) return "Seul un administrateur de l'organisation gère les niveaux de droits.";
        level.Name = level.Name.Trim();
        if (CheckName(level.Name) is { } e) return e;
        var levels = d.Tenant.Levels;
        if (levels.Any(l => l.Id != level.Id && l.Name.Equals(level.Name, StringComparison.CurrentCultureIgnoreCase))) return "Un niveau porte déjà ce nom.";
        if (!level.HasScope) return "Choisissez au moins une aire, une zone ou un écran : sans périmètre, la personne ne verrait rien.";
        level.Pages = level.Pages.Where(LevelPages.Offered.Contains).Distinct().ToList();
        level.Apps = level.Apps.Distinct().ToList();
        var i = levels.FindIndex(l => l.Id == level.Id);
        if (i >= 0) levels[i] = level; else levels.Add(level);
        return null;
    }

    /// <summary>Le niveau donne-t-il accès à quelque chose dans cette aire (l'aire entière, une de ses zones ou un de ses écrans) ?</summary>
    public static bool Reaches(AccessLevel l, Area a, IEnumerable<Screen> screens) =>
        l.AllAreas || l.AreaIds.Contains(a.Id) || a.Zones.Any(z => l.ZoneIds.Contains(z.Id)) || screens.Any(s => s.AreaId == a.Id && l.ScreenIds.Contains(s.Id));

    public static List<User> MembersOf(ClientDb d, Guid levelId) => d.Users.Where(u => u.Role == Roles.ClientMember && u.LevelId == levelId).ToList();

    /// <summary>Un niveau utilisé ne se supprime pas : ses membres sont d'abord réaffectés.</summary>
    public static string? Delete(ClientDb d, Guid levelId)
    {
        if (!d.Access.IsOrgAdmin) return "Seul un administrateur de l'organisation gère les niveaux de droits.";
        var used = MembersOf(d, levelId);
        if (used.Count > 0) return $"Ce niveau est donné à {string.Join(", ", used.Take(5).Select(u => u.Label))}{(used.Count > 5 ? $" et {used.Count - 5} autre(s)" : "")} : donnez-leur d'abord un autre niveau.";
        d.Tenant.Levels.RemoveAll(l => l.Id == levelId);
        return null;
    }

    /// <summary>Une aire, une zone ou un écran supprimé disparaît des périmètres.</summary>
    public static void Forget(ClientDb d, IEnumerable<Guid> areas, IEnumerable<Guid> zones)
    {
        var a = areas.ToHashSet(); var z = zones.ToHashSet();
        foreach (var l in d.Tenant.Levels) { l.AreaIds.RemoveAll(a.Contains); l.ZoneIds.RemoveAll(z.Contains); }
    }

    // ---------- Textes d'affichage ----------

    public static string PagesText(AccessLevel l, AppCatalog catalog)
    {
        var pages = LevelPages.Offered.Where(l.Pages.Contains).Select(LevelPages.Label).ToList();
        if (l.AllIntegrations || catalog.All.Any(a => !a.ScreenWidget && l.Apps.Contains(a.Id))) pages.Add(LevelPages.Label(LevelPages.Integrations));
        if (l.AllWidgets || catalog.All.Any(a => a.ScreenWidget && l.Apps.Contains(a.Id))) pages.Add(LevelPages.Label(LevelPages.Widgets));
        return pages.Count == 0 ? "Aucune page" : string.Join(", ", pages);
    }

    public static string AppsText(AccessLevel l, AppCatalog catalog, bool widgets)
    {
        var all = catalog.All.Where(a => a.ScreenWidget == widgets).ToList();
        if (widgets ? l.AllWidgets : l.AllIntegrations) return "Tous";
        var n = all.Count(a => l.Apps.Contains(a.Id));
        return n == 0 ? "Aucun" : $"{n} sur {all.Count}";
    }

    /// <summary>« Magasin › Vitrines, Caisses · Atelier » : le périmètre en clair.</summary>
    public static string ScopeText(AccessLevel l, IReadOnlyCollection<Area> areas, IReadOnlyCollection<Screen> screens)
    {
        if (l.AllAreas) return "Toutes les aires";
        var parts = new List<string>();
        foreach (var a in areas)
        {
            if (l.AreaIds.Contains(a.Id)) { parts.Add(a.Name); continue; }
            var zones = a.Zones.Where(z => l.ZoneIds.Contains(z.Id)).Select(z => z.Name).ToList();
            var n = screens.Count(s => s.AreaId == a.Id && l.ScreenIds.Contains(s.Id) && !l.ZoneIds.Contains(s.ZoneId ?? Guid.Empty));
            if (n > 0) zones.Add($"{n} écran{(n > 1 ? "s" : "")}");
            if (zones.Count > 0) parts.Add(a.Name + " › " + string.Join(", ", zones));
        }
        return parts.Count == 0 ? "Aucun périmètre" : string.Join(" · ", parts);
    }

    // ---------- Démarrage ----------

    /// <summary>
    /// Niveau « Utilisateur » dans chaque organisation qui n'a encore aucun niveau ; puis les anciens rôles par aire (avant le 13 octobre 2026)
    /// deviennent des niveaux : un par jeu d'aires, avec les quatre pages, toutes les apps et ces aires (l'administrateur d'aire perd la gestion
    /// des zones et des membres, réservée aux administrateurs de l'organisation). Les rôles anciens sont vidés.
    /// </summary>
    public static void Migrate(Db db)
    {
        foreach (var c in db.Clients)
        {
            if (c.Levels.Count == 0) c.Levels.Add(NewDefault());
            var areas = db.Areas.Where(a => a.ClientId == c.Id).OrderBy(a => a.CreatedUtc).ToList();
            var legacy = db.Users.Where(u => u.ClientId == c.Id && u.Role == Roles.ClientMember && u.LevelId == null && u.AreaRoles.Count > 0).ToList();
            foreach (var group in legacy.GroupBy(u => string.Join(",", u.AreaRoles.Select(r => r.AreaId).Distinct().Order())))
            {
                var ids = group.First().AreaRoles.Select(r => r.AreaId).Where(id => areas.Any(a => a.Id == id)).Distinct().ToList();
                var every = ids.Count == areas.Count;
                var name = every ? "Accès à toutes les aires" : "Accès " + string.Join(", ", areas.Where(a => ids.Contains(a.Id)).Select(a => a.Name));
                if (ids.Count == 0) { foreach (var u in group) u.AreaRoles.Clear(); continue; }   // aires disparues : un compte sans niveau, à compléter
                var level = c.Levels.FirstOrDefault(l => l.Name == name) ?? new AccessLevel
                {
                    Name = name, Pages = [.. LevelPages.Offered], AllIntegrations = true, AllWidgets = true, AllAreas = every, AreaIds = every ? [] : ids
                };
                if (!c.Levels.Contains(level)) c.Levels.Add(level);
                foreach (var u in group) { u.LevelId = level.Id; u.AreaRoles.Clear(); }
            }
            foreach (var u in db.Users.Where(u => u.ClientId == c.Id && u.AreaRoles.Count > 0)) u.AreaRoles.Clear();
        }
    }
}

public class PageRequirement(string page) : IAuthorizationRequirement { public string Page { get; } = page; }

/// <summary>Une page d'un membre n'est ouverte que si son niveau de droits la donne (relu à chaque navigation) ; les administrateurs y accèdent toujours.</summary>
public class PageHandler(JsonStore store, AppCatalog catalog) : AuthorizationHandler<PageRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext c, PageRequirement r)
    {
        if (c.User.GetGuid(Claims.Client) is not { } cid) return Task.CompletedTask;
        if (!c.User.IsInRole(Roles.ClientMember) || store.Read(db => AreaAccess.For(db, cid, c.User.GetGuid(ClaimTypes.NameIdentifier)).CanSee(r.Page, catalog))) c.Succeed(r);
        return Task.CompletedTask;
    }
}
