using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Linkii.Poc;

/// <summary>Pages que l'on peut offrir à un niveau de droits. Intégrations et Widgets ne se cochent pas : leur page apparaît dès qu'une app (ou un widget) est autorisée.</summary>
public static class LevelPages
{
    public const string Screens = "screens", Playlists = "playlists", Planning = "planning", Media = "media";
    public const string Integrations = "integrations", Widgets = "widgets", Zones = "zones";
    /// <summary>Page du menu qui n'apparaît que si l'intégration « Compositions » est dans Mes intégrations et que le niveau peut l'utiliser.</summary>
    public const string Compositions = "compositions";

    /// <summary>Cases du niveau, dans l'ordre du menu.</summary>
    public static readonly string[] Offered = { Screens, Playlists, Planning, Media };
    /// <summary>Pages d'un membre (celles qui ont une politique d'accès « Page:… »). Zones n'est pas une case : elle suit « Gérer les zones » du niveau.</summary>
    public static readonly string[] All = { Screens, Playlists, Planning, Media, Integrations, Widgets, Zones, Compositions };

    public static string Label(string page) => page switch
    {
        Screens => "Écrans", Playlists => "Listes de lecture", Planning => "Planning", Media => "Médiathèque",
        Integrations => "Intégrations", Widgets => "Widgets", Zones => "Zones", Compositions => "Compositions", _ => page
    };

    /// <summary>Adresse d'une page, pour renvoyer un membre vers la première qui lui est ouverte.</summary>
    public static string Path(string page) => page switch
    {
        Screens => "/", Playlists => "/playlists", Planning => "/planning", Media => "/media",
        Integrations => "/integrations", Widgets => "/widgets", Zones => "/zones", Compositions => "/compositions", _ => "/"
    };
}

/// <summary>Ce qui borne la vue d'une personne : aires, zones, écrans (un élément coché donne tout ce qu'il contient, ajouts futurs compris).</summary>
public interface IPerimeter
{
    bool AllAreas { get; }
    List<Guid> AreaIds { get; }
    List<Guid> ZoneIds { get; }
    List<Guid> ScreenIds { get; }
}

/// <summary>
/// Périmètre d'un membre (« ce qu'il voit ») : aires, zones, écrans, et visibilité des dossiers des autres par domaine. Il est séparé du niveau de droits
/// (« ce qu'il peut faire ») : un même niveau sert à plusieurs équipes, chacune avec son périmètre, et changer de niveau ne change pas ce qu'on voit.
/// Un membre qui n'en a pas encore (enregistré avant le 18 octobre 2026) reprend celui de son niveau, voir <see cref="AccessLevels.Migrate"/>.
/// </summary>
public class Perimeter : IPerimeter
{
    public bool AllAreas { get; set; }
    public List<Guid> AreaIds { get; set; } = new();
    public List<Guid> ZoneIds { get; set; } = new();
    public List<Guid> ScreenIds { get; set; } = new();
    /// <summary>Visibilité des dossiers des autres (voir <see cref="LevelVisibility"/>) ; null : celle du niveau.</summary>
    public string? PlaylistsVisibility { get; set; }
    public string? MediaVisibility { get; set; }

    public bool HasScope => AllAreas || AreaIds.Count + ZoneIds.Count + ScreenIds.Count > 0;

    public string? VisibilityOf(string domain) => domain == LevelPages.Playlists ? PlaylistsVisibility : domain == LevelPages.Media ? MediaVisibility : null;

    /// <summary>Périmètre d'un niveau d'avant la séparation : copié sur chacun de ses membres.</summary>
    public static Perimeter From(AccessLevel l) => new()
    {
        AllAreas = l.AllAreas, AreaIds = [.. l.AreaIds], ZoneIds = [.. l.ZoneIds], ScreenIds = [.. l.ScreenIds],
        PlaylistsVisibility = l.VisibilityOf(LevelPages.Playlists), MediaVisibility = l.VisibilityOf(LevelPages.Media)
    };

    public Perimeter Copy() => new()
    {
        AllAreas = AllAreas, AreaIds = [.. AreaIds], ZoneIds = [.. ZoneIds], ScreenIds = [.. ScreenIds],
        PlaylistsVisibility = PlaylistsVisibility, MediaVisibility = MediaVisibility
    };

    public static Perimeter Everything() => new() { AllAreas = true };
}

/// <summary>
/// Niveau de droits (créé par un administrateur de l'organisation, donné à des membres) : ce qu'on peut faire (pages, accès par domaine, apps utilisables ;
/// le périmètre est celui du membre, voir <see cref="Perimeter"/> : les champs aires / zones / écrans d'ici ne servent plus qu'à la migration). Un membre a un seul niveau ; un administrateur de l'organisation n'en a pas, il a tous les droits.
/// </summary>
public class AccessLevel : IPerimeter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Rôle de départ fourni avec l'organisation (<see cref="LevelKinds"/>) : Éditeur ou Lecteur, ni modifiable ni supprimable. Null : rôle sur mesure.</summary>
    public string? Kind { get; set; }
    /// <summary>Pages cochées (<see cref="LevelPages.Offered"/>).</summary>
    public List<string> Pages { get; set; } = new();
    /// <summary>Toutes les intégrations / tous les widgets, y compris ceux ajoutés plus tard au catalogue.</summary>
    public bool AllIntegrations { get; set; }
    public bool AllWidgets { get; set; }
    /// <summary>Identifiants des apps (intégrations et widgets) autorisées une à une.</summary>
    public List<string> Apps { get; set; } = new();
    /// <summary>Voir sans modifier ni publier.</summary>
    public bool ReadOnly { get; set; }
    /// <summary>Gérer les zones et les murs (dans les aires du périmètre) : page Zones, menu Zones, créer, renommer, synchroniser, supprimer.</summary>
    public bool ManageZones { get; set; }
    /// <summary>Dossiers de la médiathèque (option de l'organisation) : créer des dossiers à la racine de ses aires ; partager les dossiers dont il est propriétaire.</summary>
    public bool CanCreateFolders { get; set; }
    public bool CanShareFolders { get; set; }
    /// <summary>Accès à tous les dossiers partagés de l'organisation : null, « read » ou « write ». Les dossiers privés d'un autre ne sont jamais donnés.</summary>
    public string? AllFolders { get; set; }

    // Accès par domaine (§54) : « none », « read » ou « write » pour Écrans, Listes de lecture et Médiathèque ; null : niveau d'avant
    // (déduit de Pages et ReadOnly, voir AccessTo). Les pages et la lecture seule globale en sont déduites à l'enregistrement.
    public string? ScreensAccess { get; set; }
    public string? PlaylistsAccess { get; set; }
    public string? MediaAccess { get; set; }
    // Visibilité des dossiers des autres, par domaine (voir LevelVisibility) ; null : déduite de AllFolders.
    public string? PlaylistsVisibility { get; set; }
    public string? MediaVisibility { get; set; }
    // Ses propres dossiers, par domaine (les anciens CanCreateFolders / CanShareFolders valaient pour les deux).
    public bool PlaylistsCreateFolders { get; set; }
    public bool PlaylistsShareFolders { get; set; }
    public bool MediaCreateFolders { get; set; }
    public bool MediaShareFolders { get; set; }

    // Périmètre : un élément coché donne tout ce qu'il contient, y compris ce qui y sera ajouté plus tard.
    public bool AllAreas { get; set; }
    public List<Guid> AreaIds { get; set; } = new();
    public List<Guid> ZoneIds { get; set; } = new();
    public List<Guid> ScreenIds { get; set; } = new();

    public bool HasScope => AllAreas || AreaIds.Count + ZoneIds.Count + ScreenIds.Count > 0;
    public bool CanUse(AppManifest a) => (a.ScreenWidget ? AllWidgets : AllIntegrations) || Apps.Contains(a.Id);

    /// <summary>Niveau enregistré avant les accès par domaine : ses droits se lisent dans Pages, ReadOnly et AllFolders.</summary>
    public bool IsLegacy => ScreensAccess == null && PlaylistsAccess == null && MediaAccess == null;

    /// <summary>Accès à un domaine (<see cref="LevelPages.Screens"/>, <see cref="LevelPages.Playlists"/>, <see cref="LevelPages.Media"/>) : none, read ou write.</summary>
    public string AccessTo(string domain) => (domain switch
    {
        LevelPages.Screens => ScreensAccess,
        LevelPages.Playlists => PlaylistsAccess,
        LevelPages.Media => MediaAccess,
        _ => null
    }) ?? (Pages.Contains(domain) ? (ReadOnly ? DomainAccess.Read : DomainAccess.Write) : DomainAccess.None);

    /// <summary>Visibilité des dossiers des autres pour un domaine (<see cref="LevelVisibility"/>).</summary>
    public string VisibilityOf(string domain) => (domain == LevelPages.Playlists ? PlaylistsVisibility : domain == LevelPages.Media ? MediaVisibility : null)
        ?? (AllFolders == "write" ? LevelVisibility.All : AllFolders == "read" ? LevelVisibility.AllRead : LevelVisibility.Chosen);

    // Plus d'interrupteur : créer ses dossiers est toujours permis (le droit d'écriture sur le domaine reste requis, voir FolderAccess)
    public bool CreatesFolders(string domain) => true;
    public bool SharesFolders(string domain) => IsLegacy ? CanShareFolders : domain == LevelPages.Playlists ? PlaylistsShareFolders : MediaShareFolders;

    /// <summary>
    /// Remplit les réglages par domaine d'un niveau d'avant (sans rien changer à ses droits), puis en déduit les pages et la lecture seule globale :
    /// une page par domaine accessible (le Planning reste une case) ; lecture seule = aucun domaine en écriture.
    /// </summary>
    public void Normalize(Func<string, bool>? hasLevelShares = null)
    {
        if (IsLegacy)
        {
            ScreensAccess = AccessTo(LevelPages.Screens);
            PlaylistsAccess = AccessTo(LevelPages.Playlists);
            MediaAccess = AccessTo(LevelPages.Media);
            // « Dossiers choisis » si le niveau a des partages de ce type, sinon « les siens seulement » ; « Tous » gardé tel quel
            string Vis(string domain) => AllFolders != null ? VisibilityOf(domain) : hasLevelShares?.Invoke(domain) == true ? LevelVisibility.Chosen : LevelVisibility.Own;
            PlaylistsVisibility = Vis(LevelPages.Playlists);
            MediaVisibility = Vis(LevelPages.Media);
            PlaylistsCreateFolders = MediaCreateFolders = CanCreateFolders;
            PlaylistsShareFolders = MediaShareFolders = CanShareFolders;
        }
        PlaylistsCreateFolders = MediaCreateFolders = true;   // plus d'interrupteur : créer ses dossiers est toujours permis (le droit d'écriture reste requis)
        ScreensAccess = DomainAccess.Clean(ScreensAccess);
        PlaylistsAccess = DomainAccess.Clean(PlaylistsAccess);
        MediaAccess = DomainAccess.Clean(MediaAccess);
        PlaylistsVisibility = LevelVisibility.Clean(PlaylistsVisibility);
        MediaVisibility = LevelVisibility.Clean(MediaVisibility);
        var planning = Pages.Contains(LevelPages.Planning);
        Pages = new[] { (LevelPages.Screens, ScreensAccess), (LevelPages.Playlists, PlaylistsAccess), (LevelPages.Media, MediaAccess) }
            .Where(x => x.Item2 != DomainAccess.None).Select(x => x.Item1).ToList();
        if (planning && ScreensAccess != DomainAccess.None) Pages.Insert(Pages.IndexOf(LevelPages.Screens) + 1, LevelPages.Planning);
        ReadOnly = ScreensAccess != DomainAccess.Write && PlaylistsAccess != DomainAccess.Write && MediaAccess != DomainAccess.Write;
        if (ScreensAccess != DomainAccess.Write) ManageZones = false;   // gérer les zones demande de pouvoir modifier les écrans
        // anciens réglages communs : remplacés par ceux de chaque domaine
        AllFolders = null; CanCreateFolders = false; CanShareFolders = false;
    }
}

/// <summary>Rôles de départ : l'Administrateur est le compte administrateur de l'organisation (tous les droits), les deux autres sont des niveaux fixes.</summary>
public static class LevelKinds
{
    public const string Editor = "editor", Reader = "reader";
    public const string EditorName = "Éditeur", ReaderName = "Lecteur", AdminName = "Administrateur";
}

/// <summary>Un accès d'un membre : un rôle (niveau de droits) et l'endroit où il s'applique (périmètre).</summary>
public class MemberAccess
{
    public Guid LevelId { get; set; }
    public Perimeter Perimeter { get; set; } = new();
}

public static class UserAccess
{
    /// <summary>Accès du membre ; pour un compte d'avant les accès multiples : son niveau et son périmètre (ou celui de son niveau).</summary>
    public static List<MemberAccess> AccessList(this User u, IEnumerable<AccessLevel> levels)
    {
        if (u.Accesses.Count > 0) return u.Accesses;
        if (u.LevelId is not { } id) return new();
        var lv = levels.FirstOrDefault(l => l.Id == id);
        return lv == null ? new() : [new MemberAccess { LevelId = id, Perimeter = u.Perimeter ?? Perimeter.From(lv) }];
    }

    public static bool HasLevel(this User u, Guid levelId) => u.Accesses.Count > 0 ? u.Accesses.Any(a => a.LevelId == levelId) : u.LevelId == levelId;

    /// <summary>Enregistre les accès (le premier est recopié dans l'ancien couple niveau + périmètre, lu par les écrans qui n'ont pas changé).</summary>
    public static void SetAccesses(this User u, List<MemberAccess> list)
    {
        u.Accesses = list;
        u.LevelId = list.FirstOrDefault()?.LevelId;
        u.Perimeter = list.FirstOrDefault()?.Perimeter;
    }
}

/// <summary>Accès à un domaine d'un niveau de droits.</summary>
public static class DomainAccess
{
    public const string None = "none", Read = "read", Write = "write";
    public static string Clean(string? v) => v is Read or Write ? v : None;
    public static string Label(string v) => v == Write ? "Voir et modifier" : v == Read ? "Voir" : "Aucun";
}

/// <summary>
/// Visibilité des dossiers des autres, par domaine : All (tous les dossiers de l'organisation, avec le droit du domaine), AllRead (tous, en lecture
/// seulement), Chosen (les dossiers cochés, par partage de niveau), Own (les siens et ceux partagés avec la personne). Les éléments hors dossier
/// sont toujours visibles.
/// </summary>
public static class LevelVisibility
{
    public const string All = "all", AllRead = "all-read", Chosen = "chosen", Own = "own";
    public static string Clean(string? v) => v is All or AllRead or Chosen ? v : Own;
}

/// <summary>Ce qu'un périmètre laisse voir, calculé une fois par accès (voir <see cref="AreaAccess.For"/>).</summary>
public class LevelScope
{
    public List<Area> Areas { get; }
    public Func<Screen, bool> Sees { get; }
    public Func<Guid, bool> SeesZone { get; }

    public LevelScope(IPerimeter l, List<Area> areas, IEnumerable<Screen> clientScreens)
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
public class ReadOnlyLevelException(string? message = null) : UnauthorizedAccessException(message ?? "Votre niveau de droits est en lecture seule : vous ne pouvez rien modifier ni publier.")
{
    /// <summary>Refus d'une écriture dans un domaine que le niveau ne fait que voir (Écrans, Listes de lecture, Médiathèque).</summary>
    public static ReadOnlyLevelException For(string domain) => new("Votre niveau de droits permet de voir " + domain switch
    {
        LevelPages.Screens => "les écrans, pas de les modifier ni de les publier.",
        LevelPages.Playlists => "les listes de lecture, pas de les modifier.",
        LevelPages.Media => "la médiathèque, pas de la modifier.",
        _ => "cette page, pas de la modifier."
    });
}

/// <summary>Création, enregistrement, suppression et migration des niveaux de droits. Chaque méthode retourne un message d'erreur, ou null si c'est fait.</summary>
public static class AccessLevels
{
    public const string DefaultName = "Utilisateur";

    /// <summary>Niveau proposé à toute organisation : écrans, listes de lecture et médiathèque, toutes les apps, toutes les aires. Ni planning ni réglages.</summary>
    public static AccessLevel NewDefault()
    {
        var l = new AccessLevel
        {
            Name = DefaultName, Pages = [LevelPages.Screens, LevelPages.Playlists, LevelPages.Media],
            AllIntegrations = true, AllWidgets = true, AllAreas = true
        };
        l.Normalize();
        return l;
    }

    public static string? CheckName(string name) => name.Trim().Length == 0 ? "Donnez un nom au niveau." : name.Trim().Length > 60 ? "60 caractères au plus." : null;

    /// <summary>Ajoute (Id inconnu) ou remplace le niveau. L'effet est immédiat pour tous ses membres.</summary>
    public static string? Save(ClientDb d, AccessLevel level)
    {
        if (!d.Access.IsOrgAdmin) return "Seul un administrateur de l'organisation gère les niveaux de droits.";
        level.Name = level.Name.Trim();
        if (CheckName(level.Name) is { } e) return e;
        var levels = d.Tenant.Levels;
        if (levels.FirstOrDefault(l => l.Id == level.Id)?.Kind != null) return "Ce rôle est fourni d'office : il ne se modifie pas. Dupliquez-le pour en faire un rôle sur mesure.";
        level.Kind = null;
        if (levels.Any(l => l.Id != level.Id && l.Name.Equals(level.Name, StringComparison.CurrentCultureIgnoreCase))) return "Un niveau porte déjà ce nom.";
        level.Pages = level.Pages.Where(LevelPages.Offered.Contains).Distinct().ToList();
        if (level.AllFolders is not ("read" or "write")) level.AllFolders = null;
        level.Normalize(domain => HasLevelShares(d.Root, d.ClientId, level.Id, domain));
        level.Apps = level.Apps.Distinct().ToList();
        var i = levels.FindIndex(l => l.Id == level.Id);
        if (i >= 0) levels[i] = level; else levels.Add(level);
        return null;
    }

    /// <summary>Le niveau donne-t-il accès à quelque chose dans cette aire (l'aire entière, une de ses zones ou un de ses écrans) ?</summary>
    public static bool Reaches(IPerimeter l, Area a, IEnumerable<Screen> screens) =>
        l.AllAreas || l.AreaIds.Contains(a.Id) || a.Zones.Any(z => l.ZoneIds.Contains(z.Id)) || screens.Any(s => s.AreaId == a.Id && l.ScreenIds.Contains(s.Id));

    public static List<User> MembersOf(ClientDb d, Guid levelId) => d.Users.Where(u => u.Role == Roles.ClientMember && u.HasLevel(levelId)).ToList();

    /// <summary>Un niveau utilisé ne se supprime pas : ses membres sont d'abord réaffectés.</summary>
    public static string? Delete(ClientDb d, Guid levelId)
    {
        if (!d.Access.IsOrgAdmin) return "Seul un administrateur de l'organisation gère les niveaux de droits.";
        if (d.Tenant.Levels.FirstOrDefault(l => l.Id == levelId)?.Kind != null) return "Ce rôle est fourni d'office : il ne se supprime pas.";
        var used = MembersOf(d, levelId);
        if (used.Count > 0) return $"Ce niveau est donné à {string.Join(", ", used.Take(5).Select(u => u.Label))}{(used.Count > 5 ? $" et {used.Count - 5} autre(s)" : "")} : donnez-leur d'abord un autre niveau.";
        d.Tenant.Levels.RemoveAll(l => l.Id == levelId);
        foreach (var f in d.Root.MediaFolders.Where(f => f.ClientId == d.ClientId)) f.Shares.RemoveAll(s => s.Kind == FolderShareKind.Level && s.TargetId == levelId);   // ses partages de dossiers tombent avec lui
        foreach (var f in d.Root.PlaylistFolders.Where(f => f.ClientId == d.ClientId)) f.Shares.RemoveAll(s => s.Kind == FolderShareKind.Level && s.TargetId == levelId);
        return null;
    }

    /// <summary>Une aire, une zone ou un écran supprimé disparaît des périmètres.</summary>
    public static void Forget(ClientDb d, IEnumerable<Guid> areas, IEnumerable<Guid> zones)
    {
        var a = areas.ToHashSet(); var z = zones.ToHashSet();
        foreach (var l in d.Tenant.Levels) { l.AreaIds.RemoveAll(a.Contains); l.ZoneIds.RemoveAll(z.Contains); }
        foreach (var u in d.Root.Users.Where(u => u.ClientId == d.ClientId))
        {
            if (u.Perimeter != null) { u.Perimeter.AreaIds.RemoveAll(a.Contains); u.Perimeter.ZoneIds.RemoveAll(z.Contains); }
            foreach (var ac in u.Accesses) { ac.Perimeter.AreaIds.RemoveAll(a.Contains); ac.Perimeter.ZoneIds.RemoveAll(z.Contains); }
        }
    }

    // ---------- Textes d'affichage ----------

    public static string PagesText(AccessLevel l, AppCatalog catalog)
    {
        // « Écrans, Listes de lecture (voir), Médiathèque » : l'accès « Voir » d'un domaine se lit à côté de sa page (sauf tout en lecture seule)
        var pages = LevelPages.Offered.Where(l.Pages.Contains)
            .Select(p => LevelPages.Label(p) + (!l.ReadOnly && p != LevelPages.Planning && l.AccessTo(p) == DomainAccess.Read ? " (voir)" : "")).ToList();
        if (l.ManageZones) pages.Add("Gérer les zones");
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
    public static string ScopeText(IPerimeter l, IReadOnlyCollection<Area> areas, IReadOnlyCollection<Screen> screens)
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

    // ---------- Accès d'un membre, depuis l'arbre de l'organisation ----------

    private static List<MemberAccess> Editable(ClientDb d, User u) =>
        u.AccessList(d.Tenant.Levels).Select(a => new MemberAccess { LevelId = a.LevelId, Perimeter = a.Perimeter.Copy() }).ToList();

    /// <summary>
    /// Donne à un membre le rôle <paramref name="levelId"/> sur une aire, une zone, ou (sans les deux) toute l'organisation. Un accès de même rôle existant s'élargit
    /// au lieu d'en créer un autre.
    /// </summary>
    public static string? GrantAccess(ClientDb d, Guid userId, Guid levelId, Guid? areaId, Guid? zoneId)
    {
        if (!d.Access.IsOrgAdmin) return "Seul un administrateur de l'organisation donne des accès.";
        var u = d.Users.FirstOrDefault(x => x.Id == userId);
        if (u == null || u.Role != Roles.ClientMember) return "Membre introuvable : les administrateurs ont déjà accès à tout.";
        if (d.Tenant.Levels.All(l => l.Id != levelId)) return "Rôle introuvable.";
        if (areaId != null && d.Areas.All(a => a.Id != areaId)) return "Aire introuvable.";
        if (zoneId != null && d.Areas.SelectMany(a => a.Zones).All(z => z.Id != zoneId)) return "Zone introuvable.";
        var list = Editable(d, u);
        var acc = list.FirstOrDefault(a => a.LevelId == levelId && !a.Perimeter.AllAreas);
        if (acc == null) list.Add(acc = new MemberAccess { LevelId = levelId });
        if (areaId == null && zoneId == null) { acc.Perimeter.AllAreas = true; acc.Perimeter.AreaIds.Clear(); acc.Perimeter.ZoneIds.Clear(); acc.Perimeter.ScreenIds.Clear(); }
        else if (areaId is { } a)
        {
            if (!acc.Perimeter.AreaIds.Contains(a)) acc.Perimeter.AreaIds.Add(a);
            var zones = d.Areas.First(x => x.Id == a).Zones.Select(z => z.Id).ToHashSet();
            acc.Perimeter.ZoneIds.RemoveAll(zones.Contains);   // l'aire entière couvre déjà ses zones
        }
        else if (zoneId is { } z && !acc.Perimeter.ZoneIds.Contains(z)) acc.Perimeter.ZoneIds.Add(z);
        u.SetAccesses(list);
        return null;
    }

    /// <summary>Retire à un membre l'accès donné directement sur cette aire, cette zone ou l'organisation entière ; un accès sans périmètre disparaît.</summary>
    public static string? RevokeAccess(ClientDb d, Guid userId, Guid levelId, Guid? areaId, Guid? zoneId)
    {
        if (!d.Access.IsOrgAdmin) return "Seul un administrateur de l'organisation retire des accès.";
        var u = d.Users.FirstOrDefault(x => x.Id == userId);
        if (u == null || u.Role != Roles.ClientMember) return "Membre introuvable.";
        var list = Editable(d, u);
        foreach (var acc in list.Where(a => a.LevelId == levelId))
        {
            if (areaId == null && zoneId == null) acc.Perimeter.AllAreas = false;
            else if (areaId is { } a) acc.Perimeter.AreaIds.Remove(a);
            else if (zoneId is { } z) acc.Perimeter.ZoneIds.Remove(z);
        }
        list.RemoveAll(a => !a.Perimeter.HasScope);
        u.SetAccesses(list);
        return null;
    }

    // ---------- Rôles de départ (23 octobre 2026) ----------

    /// <summary>Éditeur : écrans, listes de lecture et médiathèque en modification, toutes les apps, sans Planning ni gestion des zones.</summary>
    public static AccessLevel NewEditor()
    {
        var l = NewDefault();
        l.Name = LevelKinds.EditorName; l.Kind = LevelKinds.Editor;
        return l;
    }

    /// <summary>Lecteur : voit les écrans, les listes de lecture et la médiathèque, ne modifie ni ne publie rien.</summary>
    public static AccessLevel NewReader()
    {
        var l = new AccessLevel
        {
            Name = LevelKinds.ReaderName, Kind = LevelKinds.Reader, Pages = [LevelPages.Screens, LevelPages.Playlists, LevelPages.Media],
            ScreensAccess = DomainAccess.Read, PlaylistsAccess = DomainAccess.Read, MediaAccess = DomainAccess.Read,
            PlaylistsVisibility = LevelVisibility.AllRead, MediaVisibility = LevelVisibility.AllRead, AllAreas = true
        };
        l.Normalize();
        return l;
    }

    /// <summary>
    /// Chaque organisation a les rôles Éditeur et Lecteur. L'ancien niveau « Utilisateur » d'origine (jamais modifié) devient l'Éditeur : ses membres le gardent ;
    /// un niveau sur mesure qui porterait déjà un de ces noms est renommé « … (sur mesure) ».
    /// </summary>
    public static void EnsureRoles(Tenant c)
    {
        if (!c.Levels.Any(l => l.Kind == LevelKinds.Editor))
        {
            var legacy = c.Levels.FirstOrDefault(l => l.Kind == null && l.Name == DefaultName && l.AllAreas && l.AllIntegrations && l.AllWidgets
                && !l.Pages.Contains(LevelPages.Planning) && !l.ManageZones
                && l.AccessTo(LevelPages.Screens) == DomainAccess.Write && l.AccessTo(LevelPages.Playlists) == DomainAccess.Write && l.AccessTo(LevelPages.Media) == DomainAccess.Write);
            FreeName(c, LevelKinds.EditorName);
            if (legacy != null) { legacy.Name = LevelKinds.EditorName; legacy.Kind = LevelKinds.Editor; }
            else c.Levels.Add(NewEditor());
        }
        if (!c.Levels.Any(l => l.Kind == LevelKinds.Reader)) { FreeName(c, LevelKinds.ReaderName); c.Levels.Add(NewReader()); }
    }

    private static void FreeName(Tenant c, string name)
    {
        foreach (var l in c.Levels.Where(l => l.Kind == null && l.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))) l.Name += " (sur mesure)";
    }

    /// <summary>
    /// Une fois (23 octobre 2026) : les rôles de départ sont créés, et tous les membres existants deviennent administrateurs de l'organisation
    /// (ils gardent leur compte, perdent leur niveau et leur périmètre : à redéfinir dans Organisation › Membres). Un niveau qu'ils avaient reste un rôle sur mesure.
    /// </summary>
    public static void MigrateRolesV2(Db db)
    {
        foreach (var c in db.Clients) EnsureRoles(c);
        if (db.Done.Contains("roles-v2")) return;
        db.Done.Add("roles-v2");
        foreach (var u in db.Users.Where(u => u.Role == Roles.ClientMember))
        {
            u.Role = Roles.ClientAdmin;
            u.Accesses = new(); u.LevelId = null; u.Perimeter = null;
        }
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
            // accès par domaine (15 octobre 2026) : un niveau d'avant garde exactement ses droits (lecture seule → « Voir » partout)
            foreach (var l in c.Levels.Where(l => l.IsLegacy)) l.Normalize(domain => HasLevelShares(db, c.Id, l.Id, domain));
            // périmètre séparé du niveau (18 octobre 2026) : chaque membre reprend celui de son niveau, rien ne change pour personne
            foreach (var u in db.Users.Where(u => u.ClientId == c.Id && u.Role == Roles.ClientMember && u.Perimeter == null && u.LevelId != null))
                if (c.Levels.FirstOrDefault(l => l.Id == u.LevelId) is { } lv) u.Perimeter = Perimeter.From(lv);
        }
    }

    /// <summary>Le niveau a des partages de dossiers de ce domaine (« Dossiers choisis »).</summary>
    public static bool HasLevelShares(Db db, Guid clientId, Guid levelId, string domain)
    {
        bool Mine(FolderShare s) => s.Kind == FolderShareKind.Level && s.TargetId == levelId;
        return domain == LevelPages.Playlists
            ? db.PlaylistFolders.Any(f => f.ClientId == clientId && f.Shares.Any(Mine))
            : db.MediaFolders.Any(f => f.ClientId == clientId && f.Shares.Any(Mine));
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
