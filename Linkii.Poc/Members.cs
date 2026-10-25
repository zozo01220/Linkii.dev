namespace Linkii.Poc;

/// <summary>Ce qu'un utilisateur fait dans une page (Écrans, Médiathèque, Listes de lecture) : gérer les siens, ou tout voir sans rien modifier.</summary>
public static class PageMode
{
    public const string Own = "own", Read = "read";
    public static string Clean(string? v) => v == Read ? Read : Own;
    public static string Label(string v) => v == Read ? "Voit tout · lecture seule" : "Gère les siens";
}

/// <summary>
/// Droits d'un utilisateur (membre non administrateur, depuis le 24 octobre 2026) : un mode par page, et ses écrans (zones entières, y compris les écrans
/// qu'on y ajoutera, ou écrans un à un). C'est le seul filtre de visibilité : médias, listes et dossiers sont ceux qu'il possède ou qu'on lui partage.
/// </summary>
public class MemberRights
{
    public string Screens { get; set; } = PageMode.Own;
    public string Media { get; set; } = PageMode.Own;
    public string Playlists { get; set; } = PageMode.Own;
    public List<Guid> ZoneIds { get; set; } = new();
    public List<Guid> ScreenIds { get; set; } = new();

    public string ModeOf(string domain) => PageMode.Clean(domain switch
    {
        LevelPages.Screens => Screens,
        LevelPages.Media => Media,
        LevelPages.Playlists => Playlists,
        _ => null
    });

    public MemberRights Copy() => new() { Screens = Screens, Media = Media, Playlists = Playlists, ZoneIds = [.. ZoneIds], ScreenIds = [.. ScreenIds] };

    public void Clean()
    {
        Screens = PageMode.Clean(Screens); Media = PageMode.Clean(Media); Playlists = PageMode.Clean(Playlists);
        ZoneIds = ZoneIds.Distinct().ToList(); ScreenIds = ScreenIds.Distinct().ToList();
    }

    /// <summary>Niveau de droits équivalent, lu par les contrôles existants (pages du menu, écriture par domaine, apps) : toutes les apps, sans gestion des zones.</summary>
    public AccessLevel ToLevel()
    {
        string Acc(string mode) => mode == PageMode.Read ? DomainAccess.Read : DomainAccess.Write;
        var l = new AccessLevel
        {
            Name = AccessLevels.DefaultName, Pages = [LevelPages.Screens, LevelPages.Planning, LevelPages.Playlists, LevelPages.Media],
            ScreensAccess = Acc(PageMode.Clean(Screens)), PlaylistsAccess = Acc(PageMode.Clean(Playlists)), MediaAccess = Acc(PageMode.Clean(Media)),
            PlaylistsVisibility = LevelVisibility.Own, MediaVisibility = LevelVisibility.Own
        };
        l.Normalize();
        // les contenus (intégrations, widgets, compositions) servent à remplir ses listes : toutes les apps dès qu'il crée quelque chose
        l.AllIntegrations = l.AllWidgets = !l.ReadOnly;
        return l;
    }

    /// <summary>Périmètre équivalent (écrans visibles) : tout en lecture seule, sinon ses zones et ses écrans.</summary>
    public Perimeter ToPerimeter() => PageMode.Clean(Screens) == PageMode.Read
        ? Perimeter.Everything()
        : new Perimeter { ZoneIds = [.. ZoneIds], ScreenIds = [.. ScreenIds] };
}

/// <summary>Ce qui se partage entre personnes. Les compositions et leurs modèles suivent la médiathèque (lecture seule de la médiathèque : lecture seule ici).</summary>
public static class ShareKind
{
    public const string Media = "media", MediaFolder = "mediafolder", Playlist = "playlist", PlaylistFolder = "playlistfolder";
    public const string Composition = "composition", Template = "template", CompositionFolder = "compositionfolder";
    public static string Domain(string kind) => kind is Playlist or PlaylistFolder ? LevelPages.Playlists : LevelPages.Media;
    public static bool IsFolder(string kind) => kind is MediaFolder or PlaylistFolder or CompositionFolder;
    /// <summary>Type de dossier qui peut contenir ce type d'élément (le dossier lui-même : celui de son contenu).</summary>
    public static string FolderOf(string kind) => kind switch
    {
        Playlist or PlaylistFolder => PlaylistFolder,
        Composition or Template or CompositionFolder => CompositionFolder,
        _ => MediaFolder
    };
    public static string Label(string kind) => kind switch
    {
        Media => "média", MediaFolder => "dossier de médias", Playlist => "liste de lecture", PlaylistFolder => "dossier de listes",
        Composition => "composition", CompositionFolder => "dossier de compositions", _ => "modèle"
    };
    /// <summary>Pluriel, pour « Partager 3 … ».</summary>
    public static string Plural(string kind) => kind switch
    {
        Media => "médias", MediaFolder => "dossiers de médias", Playlist => "listes de lecture", PlaylistFolder => "dossiers de listes",
        Composition => "compositions", CompositionFolder => "dossiers de compositions", _ => "modèles"
    };
}

/// <summary>Élément qui appartient à quelqu'un : média, dossier de médias, liste de lecture, dossier de listes, composition, modèle.</summary>
public interface IOwned { Guid? OwnerId { get; set; } }

/// <summary>
/// Partage d'un élément avec une personne (ou toute l'organisation, <see cref="ToUserId"/> null), en lien vivant : « Peut utiliser » (lecture : le mettre
/// dans ses listes, le diffuser) ou « Peut modifier » (<see cref="Edit"/> : ajouter, retirer, renommer ; supprimer reste au propriétaire).
/// Un dossier partagé donne ses sous-dossiers et son contenu, ajouts futurs compris.
/// </summary>
public class ItemShare : IClientOwned
{
    public Guid ClientId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = ShareKind.Media;
    public Guid ItemId { get; set; }
    public Guid? ToUserId { get; set; }
    public bool Edit { get; set; }
    public Guid? FromUserId { get; set; }
    public DateTime SharedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Partages reçus par une personne (directs ou pour toute l'organisation) : (type, élément) → peut modifier.</summary>
public class ShareIndex
{
    public static readonly ShareIndex None = new(new());
    private readonly Dictionary<(string, Guid), bool> _map;
    private ShareIndex(Dictionary<(string, Guid), bool> map) => _map = map;

    public bool Has(string kind, Guid id) => _map.ContainsKey((kind, id));
    public bool? Edit(string kind, Guid id) => _map.TryGetValue((kind, id), out var e) ? e : null;
    public int Count => _map.Count;

    public static ShareIndex For(Db db, Guid clientId, Guid? userId)
    {
        if (userId is not { } me) return None;
        var map = new Dictionary<(string, Guid), bool>();
        foreach (var s in db.ItemShares.Where(s => s.ClientId == clientId && (s.ToUserId == me || s.ToUserId == null) && s.FromUserId != me))
            map[(s.Kind, s.ItemId)] = map.GetValueOrDefault((s.Kind, s.ItemId)) || s.Edit;
        return map.Count == 0 ? None : new ShareIndex(map);
    }
}

/// <summary>
/// Droit d'une personne sur un élément : l'administrateur a tout ; le propriétaire (ou le propriétaire d'un dossier qui le contient) a tout ; sinon le plus fort
/// des partages qui le couvrent (l'élément ou un de ses dossiers). En « Voit tout · lecture seule », tout est visible et rien n'est modifiable.
/// </summary>
public static class Ownership
{
    public static FolderRight Right(AreaAccess a, string kind, Guid id, Guid? ownerId, IEnumerable<IFolderNode> chain)
    {
        if (a.IsOrgAdmin) return FolderRight.Owner;
        var domain = ShareKind.Domain(kind);
        if (a.ModeOf(domain) == PageMode.Read) return FolderRight.Read;
        if (a.UserId is not { } me) return FolderRight.None;
        if (ownerId == me) return FolderRight.Owner;
        var folderKind = ShareKind.FolderOf(kind);
        var best = a.Shares.Edit(kind, id) switch { true => FolderRight.Write, false => FolderRight.Read, _ => FolderRight.None };
        foreach (var f in chain)
        {
            if (f.OwnerId == me) return FolderRight.Owner;   // son dossier : il en gère tout le contenu
            if (a.Shares.Edit(folderKind, f.Id) is { } e) best = (FolderRight)Math.Max((int)best, (int)(e ? FolderRight.Write : FolderRight.Read));
        }
        return best;
    }

    /// <summary>Propriétaire de l'espace d'un élément : celui du dossier le plus haut qui le contient, sinon le sien (null : élément de l'organisation, d'avant les propriétaires).</summary>
    public static Guid? SpaceOf(Guid? ownerId, IEnumerable<IFolderNode> chain)
    {
        IFolderNode? top = null;
        foreach (var f in chain) top = f;
        return top != null ? top.OwnerId : ownerId;
    }

    /// <summary>Place d'un élément dans les onglets ; un propriétaire hors de l'organisation (équipe Linkii entrée dans l'espace) vaut « l'organisation » (null).</summary>
    public static LibraryTab.Info TabInfo(AreaAccess a, string kind, Guid id, Guid? ownerId, IReadOnlyList<IFolderNode> parents)
    {
        Guid? Known(Guid? u) => u is { } x && a.OrgUserIds.Contains(x) ? x : null;
        return new LibraryTab.Info(Known(ownerId), Known(SpaceOf(ownerId, parents)), SharedWith(a, kind, id, parents));
    }

    /// <summary>L'élément (ou un de ses dossiers) est partagé avec la personne.</summary>
    public static bool SharedWith(AreaAccess a, string kind, Guid id, IEnumerable<IFolderNode> chain)
    {
        if (a.Shares.Has(kind, id)) return true;
        var folderKind = ShareKind.FolderOf(kind);
        return chain.Any(f => a.Shares.Has(folderKind, f.Id));
    }

    /// <summary>Dossiers qui contiennent un élément rangé dans <paramref name="folderId"/>, du plus proche au plus haut.</summary>
    public static List<T> Parents<T>(Guid? folderId, IReadOnlyDictionary<Guid, T> byId) where T : class, IFolderNode
    {
        var list = new List<T>();
        for (var cur = folderId; cur is { } id && list.Count < 50 && byId.TryGetValue(id, out var f); cur = f.ParentId) list.Add(f);
        return list;
    }

    /// <summary>Supprimer : le propriétaire, ou celui qui modifie le dossier qui contient l'élément (« ajouter et retirer »). Un élément partagé seul ne se supprime pas.</summary>
    public static bool CanDelete(FolderRight right, Guid? folderId) => right == FolderRight.Owner || right == FolderRight.Write && folderId != null;
}

/// <summary>Où se range un élément dans les onglets d'une page : ses éléments, ceux qu'on lui partage, tout (administrateur, lecture seule).</summary>
public static class LibraryTab
{
    public const string Mine = "mine", Shared = "shared", All = "all";

    /// <summary>Un élément (ou dossier) dans les onglets : son propriétaire, celui de son espace, et s'il est partagé avec la personne.</summary>
    public record Info(Guid? Owner, Guid? Space, bool SharedIn);

    public static bool IsMine(Info i, Guid? me, bool admin) => i.Space == me || i.Owner == me || admin && i.Space == null;

    public static bool InTab(string tab, Info i, Guid? me, bool admin) => tab switch
    {
        Mine => IsMine(i, me, admin),
        Shared => !IsMine(i, me, admin) && (i.SharedIn || !admin),
        _ => true
    };

    /// <summary>
    /// Dossiers de l'onglet, avec tout ce qu'ils contiennent : on part des dossiers de l'onglet et on descend dans leurs sous-dossiers (visibles).
    /// Les dossiers « racines » de l'onglet sont ceux dont le parent n'en fait pas partie.
    /// </summary>
    public static HashSet<Guid> Folders(string tab, IEnumerable<(Guid Id, Guid? Parent, Info Info)> folders, Guid? me, bool admin)
    {
        var list = folders.ToList();
        var set = list.Where(f => InTab(tab, f.Info, me, admin)).Select(f => f.Id).ToHashSet();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var f in list) if (!set.Contains(f.Id) && f.Parent is { } p && set.Contains(p)) { set.Add(f.Id); grew = true; }
        }
        return set;
    }
}

/// <summary>Avec qui un élément est partagé : personnes, toute l'organisation (null : non ; sinon peut modifier), et les noms avec leur droit.</summary>
public record ShareSummary(int People, bool? Everyone, List<(string Label, bool Edit)> Names)
{
    public static readonly ShareSummary None = new(0, null, new());
    public bool Any => People > 0 || Everyone != null;
}

/// <summary>Une personne à qui partager, avec son partage actuel (null : aucun) et pourquoi ce n'est pas utile (null : possible).</summary>
public record ShareTarget(Guid Id, string Label, string Email, bool? Edit, string? Note);

/// <summary>Partages d'un élément : avec qui, et en quel droit. Seul son propriétaire (ou un administrateur) le partage.</summary>
public static class ItemShareOps
{
    /// <summary>Clé « toute l'organisation » dans les choix de partage.</summary>
    public static readonly Guid Everyone = Guid.Empty;

    /// <summary>Droit de la personne sur l'élément (null : introuvable).</summary>
    public static FolderRight? RightOn(ClientDb d, string kind, Guid id) => kind switch
    {
        ShareKind.Media => d.Media.FirstOrDefault(m => m.Id == id) is { } m ? FolderOps.RightOfFile(d, m) : null,
        ShareKind.MediaFolder => FolderOps.Find(d, id) is { } f ? FolderOps.RightOf(d, f) : null,
        ShareKind.Playlist => d.Playlists.FirstOrDefault(p => p.Id == id) is { } p ? PlaylistOps.RightOf(d, p) : null,
        ShareKind.PlaylistFolder => PlaylistFolderOps.Find(d, id) is { } pf ? PlaylistFolderOps.RightOf(d, pf) : null,
        ShareKind.Composition => d.Media.FirstOrDefault(m => m.Id == id && CompositionRights.Is(m)) is { } c ? CompositionRights.RightOf(d, c) : null,
        ShareKind.Template => d.CompositionTemplates.FirstOrDefault(t => t.Id == id) is { } t ? CompositionRights.RightOf(d, t) : null,
        ShareKind.CompositionFolder => CompositionFolderOps.Find(d, id) is { } cf ? CompositionFolderOps.RightOf(d, cf) : null,
        _ => null
    };

    private static Guid? OwnerOf(ClientDb d, string kind, Guid id) => kind switch
    {
        ShareKind.Media or ShareKind.Composition => d.Root.Media.FirstOrDefault(m => m.Id == id && m.ClientId == d.ClientId)?.OwnerId,
        ShareKind.Template => d.Root.CompositionTemplates.FirstOrDefault(t => t.Id == id && t.ClientId == d.ClientId)?.OwnerId,
        ShareKind.MediaFolder => d.Root.MediaFolders.FirstOrDefault(f => f.Id == id && f.ClientId == d.ClientId)?.OwnerId,
        ShareKind.Playlist => d.Root.Playlists.FirstOrDefault(p => p.Id == id && p.ClientId == d.ClientId)?.OwnerId,
        ShareKind.CompositionFolder => d.Root.CompositionFolders.FirstOrDefault(f => f.Id == id && f.ClientId == d.ClientId)?.OwnerId,
        _ => d.Root.PlaylistFolders.FirstOrDefault(f => f.Id == id && f.ClientId == d.ClientId)?.OwnerId
    };

    /// <summary>Partager : propriétaire (ou administrateur), dans une organisation de plus d'une personne.</summary>
    public static bool CanShare(ClientDb d, string kind, Guid id) =>
        d.Users.Count(u => !u.Disabled) > 1 && RightOn(d, kind, id) == FolderRight.Owner;

    /// <summary>Partages en place sur un élément (un seul), et les personnes possibles. Les administrateurs et les personnes en lecture seule voient déjà tout.</summary>
    public static (bool? Everyone, List<ShareTarget> People) TargetsOf(ClientDb d, string kind, Guid? id)
    {
        var shares = id is { } iid ? d.Root.ItemShares.Where(s => s.ClientId == d.ClientId && s.Kind == kind && s.ItemId == iid).ToList() : new();
        var owner = id is { } oid ? OwnerOf(d, kind, oid) : null;
        var domain = ShareKind.Domain(kind);
        var people = d.Users.Where(u => u.Id != d.Access.UserId && u.Id != owner)
            .OrderBy(u => u.Role == Roles.ClientAdmin ? 1 : 0).ThenBy(u => u.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(u =>
            {
                var s = shares.FirstOrDefault(x => x.ToUserId == u.Id);
                var note = u.Disabled ? "compte désactivé"
                    : u.Role == Roles.ClientMember && (u.Rights ?? new()).ModeOf(domain) == PageMode.Read ? "voit déjà tout (lecture seule)" : null;
                return new ShareTarget(u.Id, u.Label, u.Email, s?.Edit, note);
            }).ToList();
        return (shares.FirstOrDefault(s => s.ToUserId == null)?.Edit, people);
    }

    /// <summary>Personnes (et toute l'organisation) avec qui un élément est partagé : nombre de la pastille.</summary>
    public static Dictionary<Guid, int> Counts(ClientDb d, string kind) =>
        d.Root.ItemShares.Where(s => s.ClientId == d.ClientId && s.Kind == kind).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Avec qui chaque élément d'un type est partagé (pastille et infobulle).</summary>
    public static Dictionary<Guid, ShareSummary> Summaries(ClientDb d, string kind)
    {
        var names = d.Users.ToDictionary(u => u.Id, u => u.Label);
        return d.Root.ItemShares.Where(s => s.ClientId == d.ClientId && s.Kind == kind).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => new ShareSummary(
            g.Count(s => s.ToUserId != null), g.FirstOrDefault(s => s.ToUserId == null)?.Edit,
            g.Where(s => s.ToUserId is { } t && names.ContainsKey(t)).Select(s => (names[s.ToUserId!.Value], s.Edit)).OrderBy(x => x.Item1, StringComparer.CurrentCultureIgnoreCase).ToList()));
    }

    /// <summary>
    /// Fixe les partages des éléments : <paramref name="targets"/> = personne (ou <see cref="Everyone"/>) → peut modifier. Un seul élément : remplace ses partages ;
    /// plusieurs : ajoute seulement. Rend l'erreur, ou null.
    /// </summary>
    public static string? Set(ClientDb d, string kind, IReadOnlyCollection<Guid> ids, IReadOnlyDictionary<Guid, bool> targets)
    {
        var users = d.Users.Select(u => u.Id).ToHashSet();
        if (targets.Keys.Any(t => t != Everyone && !users.Contains(t))) return "Personne introuvable dans l'organisation.";
        foreach (var id in ids)
        {
            if (RightOn(d, kind, id) != FolderRight.Owner) return "Seul le propriétaire d'un élément le partage.";
            var owner = OwnerOf(d, kind, id);
            var mine = d.Root.ItemShares.Where(s => s.ClientId == d.ClientId && s.Kind == kind && s.ItemId == id).ToList();
            foreach (var (t, edit) in targets)
            {
                if (t == owner) continue;
                Guid? to = t == Everyone ? null : t;
                if (mine.FirstOrDefault(s => s.ToUserId == to) is { } cur) cur.Edit = edit;
                else d.Root.ItemShares.Add(new ItemShare { ClientId = d.ClientId, Kind = kind, ItemId = id, ToUserId = to, Edit = edit, FromUserId = d.Access.UserId });
            }
            if (ids.Count == 1)
                foreach (var gone in mine.Where(s => !targets.ContainsKey(s.ToUserId ?? Everyone))) d.Root.ItemShares.Remove(gone);
        }
        return null;
    }

    /// <summary>« Ne plus voir » : le destinataire retire le partage direct qu'il a reçu (un partage à toute l'organisation ne se retire que par son auteur).</summary>
    public static string? Leave(ClientDb d, string kind, Guid id)
    {
        var n = d.Root.ItemShares.RemoveAll(s => s.ClientId == d.ClientId && s.Kind == kind && s.ItemId == id && s.ToUserId == d.Access.UserId && s.ToUserId != null);
        return n > 0 ? null : "Cet élément est partagé avec toute l'organisation : seul son propriétaire peut retirer ce partage.";
    }

    /// <summary>Retire les partages d'éléments supprimés.</summary>
    public static void Forget(ClientDb d, string kind, IReadOnlyCollection<Guid> ids) =>
        d.Root.ItemShares.RemoveAll(s => s.ClientId == d.ClientId && s.Kind == kind && ids.Contains(s.ItemId));
}

/// <summary>Une personne de la page Membres : son compte, son rôle, ses droits et ses écrans.</summary>
public record MemberRow(User User, bool Admin, MemberRights Rights, int Screens);

/// <summary>Membres de l'organisation (administrateur seulement) : création, droits, écrans attribués, retrait. Chaque méthode rend un message d'erreur, ou null.</summary>
public static class MemberOps
{
    private static string? AdminOnly(ClientDb d, string action) => d.Access.IsOrgAdmin ? null : $"Seul un administrateur de l'organisation {action}.";

    /// <summary>Écrans d'un utilisateur : ceux de ses zones et ceux attribués un à un (tous en lecture seule).</summary>
    public static List<Screen> ScreensOf(ClientDb d, MemberRights r)
    {
        var all = d.Root.Screens.Where(s => s.ClientId == d.ClientId && s.Token != null);
        if (PageMode.Clean(r.Screens) == PageMode.Read) return all.ToList();
        return all.Where(s => s.ZoneId is { } z && r.ZoneIds.Contains(z) || r.ScreenIds.Contains(s.Id)).ToList();
    }

    public static List<MemberRow> Rows(ClientDb d) => d.Users
        .OrderBy(u => u.Role == Roles.ClientAdmin ? 0 : 1).ThenBy(u => u.Label, StringComparer.CurrentCultureIgnoreCase)
        .Select(u => { var r = u.Rights ?? new MemberRights(); return new MemberRow(u, u.Role != Roles.ClientMember, r, u.Role == Roles.ClientMember ? ScreensOf(d, r).Count : 0); })
        .ToList();

    /// <summary>Enregistre le rôle et les droits d'une personne. Un administrateur ne se retire pas son rôle ; l'organisation garde un administrateur actif.</summary>
    public static string? Save(ClientDb d, Guid userId, string name, bool admin, MemberRights rights)
    {
        if (AdminOnly(d, "modifie un compte") is { } e) return e;
        if (d.Users.FirstOrDefault(u => u.Id == userId) is not { } u) return "Compte introuvable.";
        if (name.Trim().Length == 0) return "Saisissez le nom.";
        if (u.Role == Roles.ClientAdmin && !admin)
        {
            if (u.Id == d.Access.UserId) return "Vous ne pouvez pas vous retirer le rôle d'administrateur : demandez-le à un autre administrateur.";
            if (!d.Users.Any(x => x.Id != u.Id && x.Role == Roles.ClientAdmin && !x.Disabled)) return "L'organisation doit garder au moins un administrateur actif.";
        }
        u.Name = name.Trim();
        u.Role = admin ? Roles.ClientAdmin : Roles.ClientMember;
        var r = rights.Copy(); r.Clean();
        u.Rights = r;
        u.Accesses = new(); u.LevelId = null; u.Perimeter = null;   // anciens rôles et périmètres : remplacés
        return null;
    }

    /// <summary>Crée un compte (mot de passe provisoire). Rend le compte, ou l'erreur.</summary>
    public static (User? User, string? Error) Create(ClientDb d, string name, string email, bool admin, MemberRights rights, string password)
    {
        if (AdminOnly(d, "crée un membre") is { } e) return (null, e);
        var mail = email.Trim().ToLowerInvariant();
        if (name.Trim().Length == 0) return (null, "Saisissez le nom.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(mail, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) return (null, "Saisissez une adresse e-mail valide.");
        if (d.EmailTaken(mail)) return (null, d.Users.Any(u => u.Email == mail) ? "Cette personne fait déjà partie de l'organisation." : "Cette adresse e-mail est déjà utilisée.");
        var r = rights.Copy(); r.Clean();
        var u = new User { Email = mail, Name = name.Trim(), PasswordHash = Passwords.Hash(password), Role = admin ? Roles.ClientAdmin : Roles.ClientMember, MustChangePassword = true, CreatedUtc = DateTime.UtcNow, Rights = r };
        d.AddUser(u);
        return (u, null);
    }

    /// <summary>Personnes qui gèrent un écran : utilisateurs dont les zones ou les écrans le contiennent.</summary>
    public static List<User> ManagersOf(ClientDb d, Screen s) => d.Users.Where(u => u.Role == Roles.ClientMember && u.Rights is { } r
        && PageMode.Clean(r.Screens) == PageMode.Own && (s.ZoneId is { } z && r.ZoneIds.Contains(z) || r.ScreenIds.Contains(s.Id))).ToList();

    /// <summary>
    /// Attribue un écran aux personnes données (et le retire aux autres utilisateurs qui l'ont un à un). Ceux qui l'ont par sa zone le gardent :
    /// on le leur retire en retirant la zone, depuis leur fiche.
    /// </summary>
    public static string? AssignScreen(ClientDb d, Guid screenId, IReadOnlyCollection<Guid> userIds)
    {
        if (AdminOnly(d, "attribue les écrans") is { } e) return e;
        if (d.Root.Screens.FirstOrDefault(s => s.Id == screenId && s.ClientId == d.ClientId) is not { } screen) return "Écran introuvable.";
        foreach (var u in d.Users.Where(u => u.Role == Roles.ClientMember))
        {
            var r = u.Rights ??= new MemberRights();
            if (userIds.Contains(u.Id)) { if (!r.ScreenIds.Contains(screenId)) r.ScreenIds.Add(screenId); }
            else r.ScreenIds.Remove(screenId);
        }
        return null;
    }

    /// <summary>Un utilisateur qui connecte un écran le gère d'office.</summary>
    public static void AdoptScreen(ClientDb d, Guid screenId)
    {
        if (d.Access.IsOrgAdmin || d.Users.FirstOrDefault(u => u.Id == d.Access.UserId) is not { } me) return;
        var r = me.Rights ??= new MemberRights();
        if (!r.ScreenIds.Contains(screenId)) r.ScreenIds.Add(screenId);
    }

    /// <summary>
    /// Retire un compte de l'organisation : ses médias, listes et dossiers passent à l'administrateur qui le retire (partages conservés,
    /// sauf ceux qu'il recevait).
    /// </summary>
    public static string? Remove(ClientDb d, Guid userId)
    {
        if (AdminOnly(d, "retire un membre") is { } e) return e;
        if (d.Users.FirstOrDefault(x => x.Id == userId) is not { } u) return "Compte introuvable.";
        if (u.Id == d.Access.UserId) return "Vous ne pouvez pas retirer votre propre compte.";
        if (u.Role == Roles.ClientAdmin && !u.Disabled && !d.Users.Any(x => x.Id != u.Id && x.Role == Roles.ClientAdmin && !x.Disabled))
            return "L'organisation doit garder au moins un administrateur actif.";
        d.RemoveUser(u.Id);
        return null;
    }

    /// <summary>Rend à l'administrateur (<paramref name="to"/>) tout ce que possédait une personne retirée.</summary>
    public static void TransferAll(Db root, Guid clientId, Guid from, Guid? to)
    {
        foreach (var m in root.Media.Where(x => x.ClientId == clientId && x.OwnerId == from)) m.OwnerId = to;
        foreach (var f in root.MediaFolders.Where(x => x.ClientId == clientId && x.OwnerId == from)) f.OwnerId = to;
        foreach (var p in root.Playlists.Where(x => x.ClientId == clientId && x.OwnerId == from)) p.OwnerId = to;
        foreach (var f in root.PlaylistFolders.Where(x => x.ClientId == clientId && x.OwnerId == from)) f.OwnerId = to;
        foreach (var f in root.CompositionFolders.Where(x => x.ClientId == clientId && x.OwnerId == from)) f.OwnerId = to;
        foreach (var t in root.CompositionTemplates.Where(x => x.ClientId == clientId && x.OwnerId == from)) t.OwnerId = to;
        root.ItemShares.RemoveAll(s => s.ClientId == clientId && s.ToUserId == from);
        foreach (var s in root.ItemShares.Where(s => s.ClientId == clientId && s.FromUserId == from)) s.FromUserId = to;
        if (to != null) root.ItemShares.RemoveAll(s => s.ClientId == clientId && s.ToUserId == to && OwnerIs(root, s, to));
    }

    // un partage vers le nouveau propriétaire n'a plus de sens
    private static bool OwnerIs(Db root, ItemShare s, Guid? to) => s.Kind switch
    {
        ShareKind.Media or ShareKind.Composition => root.Media.Any(m => m.Id == s.ItemId && m.OwnerId == to),
        ShareKind.Template => root.CompositionTemplates.Any(t => t.Id == s.ItemId && t.OwnerId == to),
        ShareKind.MediaFolder => root.MediaFolders.Any(f => f.Id == s.ItemId && f.OwnerId == to),
        ShareKind.CompositionFolder => root.CompositionFolders.Any(f => f.Id == s.ItemId && f.OwnerId == to),
        ShareKind.Playlist => root.Playlists.Any(p => p.Id == s.ItemId && p.OwnerId == to),
        _ => root.PlaylistFolders.Any(f => f.Id == s.ItemId && f.OwnerId == to)
    };

    /// <summary>
    /// Une fois (24 octobre 2026) : chaque membre reçoit ses droits par page (« Gère les siens » là où son ancien rôle écrivait, lecture seule ailleurs)
    /// et ses écrans (les zones de ses anciens lieux) ; les partages de dossiers à une personne deviennent des partages « Peut utiliser » / « Peut modifier ».
    /// Les partages à un niveau de droits disparaissent avec les niveaux.
    /// </summary>
    public static void MigrateV3(Db db)
    {
        if (db.Done.Contains("members-v3")) return;
        db.Done.Add("members-v3");
        foreach (var u in db.Users.Where(u => u.Role == Roles.ClientMember && u.Rights == null && u.ClientId != null))
        {
            var levels = db.Clients.FirstOrDefault(c => c.Id == u.ClientId)?.Levels ?? new();
            var areas = db.Areas.Where(a => a.ClientId == u.ClientId).ToList();
            var r = new MemberRights();
            var accs = u.AccessList(levels).Where(a => levels.Any(l => l.Id == a.LevelId)).ToList();
            if (accs.Count > 0)
            {
                string Mode(string dom) => accs.Any(a => levels.First(l => l.Id == a.LevelId).AccessTo(dom) == DomainAccess.Write) ? PageMode.Own : PageMode.Read;
                r.Screens = Mode(LevelPages.Screens); r.Media = Mode(LevelPages.Media); r.Playlists = Mode(LevelPages.Playlists);
                foreach (var p in accs.Select(a => a.Perimeter))
                {
                    var zones = p.AllAreas ? areas.SelectMany(a => a.Zones) : areas.Where(a => p.AreaIds.Contains(a.Id)).SelectMany(a => a.Zones);
                    r.ZoneIds.AddRange(zones.Select(z => z.Id).Concat(p.ZoneIds));
                    r.ScreenIds.AddRange(p.ScreenIds);
                }
            }
            r.Clean();
            u.Rights = r;
        }
        foreach (var (kind, nodes) in new (string, IEnumerable<IFolderNode>)[] { (ShareKind.MediaFolder, db.MediaFolders), (ShareKind.PlaylistFolder, db.PlaylistFolders) })
            foreach (var f in nodes)
            {
                var cid = f is MediaFolder mf ? mf.ClientId : ((PlaylistFolder)f).ClientId;
                foreach (var s in f.Shares.Where(s => s.Kind == FolderShareKind.User))
                    if (!db.ItemShares.Any(x => x.Kind == kind && x.ItemId == f.Id && x.ToUserId == s.TargetId))
                        db.ItemShares.Add(new ItemShare { ClientId = cid, Kind = kind, ItemId = f.Id, ToUserId = s.TargetId, Edit = s.Write, FromUserId = f.OwnerId });
                f.Shares.Clear();
            }
    }
}
