namespace Linkii.Poc;

/// <summary>Droit sur un dossier de la médiathèque, du plus faible au plus fort.</summary>
public enum FolderRight { None, Read, Write, Owner }

/// <summary>
/// Droits sur les dossiers de la médiathèque (option « Dossiers dans la médiathèque », §49). L'administrateur de l'organisation a tout ; le
/// propriétaire d'un dossier a tout dessus ; les autres ont le plus fort des partages qui le couvrent (le dossier ou un de ses parents, donné à
/// la personne ou à son niveau), plus « tous les dossiers partagés » du niveau. Un niveau en lecture seule plafonne à la lecture.
/// Un dossier privé (sans partage, créé par un autre) n'est jamais donné par « tous les dossiers ».
/// </summary>
public static class FolderAccess
{
    /// <summary>Profondeur maximale : l'arborescence est affichée sur quatre niveaux.</summary>
    public const int MaxDepth = 4;

    public static FolderRight For<T>(AreaAccess a, T f, IReadOnlyCollection<T> all) where T : class, IFolderNode
    {
        if (a.IsOrgAdmin) return FolderRight.Owner;
        if (!a.HasArea(f.AreaId)) return FolderRight.None;
        if (a.UserId is { } uid && f.OwnerId == uid) return Capped(a, FolderRight.Owner);

        var best = FolderRight.None;
        var level = a.Level;
        foreach (var n in Chain(f, all))
        {
            foreach (var s in n.Shares)
            {
                var mine = s.Kind == FolderShareKind.User ? s.TargetId == a.UserId
                    : level != null && s.Kind == FolderShareKind.Level && s.TargetId == level.Id;
                if (mine) best = Max(best, s.Write ? FolderRight.Write : FolderRight.Read);
            }
            // « tous les dossiers partagés » : seulement ceux qui sont partagés (avec quelqu'un) et qui ne sont pas à la personne
            if (level?.AllFolders is { } mode && n.Shares.Count > 0 && n.OwnerId != a.UserId)
                best = Max(best, mode == "write" ? FolderRight.Write : FolderRight.Read);
        }
        return Capped(a, best);
    }

    /// <summary>Droits de la personne sur chaque dossier donné.</summary>
    public static Dictionary<Guid, FolderRight> Snapshot<T>(AreaAccess a, IReadOnlyCollection<T> folders) where T : class, IFolderNode =>
        folders.ToDictionary(f => f.Id, f => For(a, f, folders));

    /// <summary>Créer un dossier à la racine de l'aire : administrateur, ou niveau qui le permet (hors lecture seule).</summary>
    public static bool CanCreateAtRoot(AreaAccess a) => a.IsOrgAdmin || a.Level is { CanCreateFolders: true, ReadOnly: false };

    /// <summary>Partager un dossier : son propriétaire, si son niveau le permet (l'administrateur partage tout).</summary>
    public static bool CanShare<T>(AreaAccess a, T f) where T : class, IFolderNode =>
        a.IsOrgAdmin || (a.UserId is { } u && f.OwnerId == u && a.HasArea(f.AreaId) && a.Level is { CanShareFolders: true, ReadOnly: false });

    /// <summary>Le dossier ajouté à un niveau de droits ne donne rien au-delà de sa lecture seule.</summary>
    private static FolderRight Capped(AreaAccess a, FolderRight r) => a.ReadOnly && r > FolderRight.Read ? FolderRight.Read : r;
    private static FolderRight Max(FolderRight x, FolderRight y) => (FolderRight)Math.Max((int)x, (int)y);

    /// <summary>Le dossier, puis ses parents jusqu'à la racine de l'aire (sans boucle).</summary>
    public static IEnumerable<T> Chain<T>(T f, IReadOnlyCollection<T> all) where T : class, IFolderNode
    {
        T? cur = f;
        for (var n = 0; cur != null && n < 50; n++)
        {
            yield return cur;
            cur = cur.ParentId is { } p ? all.FirstOrDefault(x => x.Id == p) : null;
        }
    }
}

/// <summary>Création, renommage, suppression, partage et rangement des fichiers dans les dossiers. Chaque méthode rend un message d'erreur, ou null si c'est fait.</summary>
public static class FolderOps
{
    /// <summary>Tous les dossiers de l'organisation, toutes aires confondues (pour remonter aux parents).</summary>
    public static List<MediaFolder> All(ClientDb d) => d.Root.MediaFolders.Where(f => f.ClientId == d.ClientId).ToList();

    /// <summary>Dossier de l'aire ouverte (ou de n'importe quelle aire pour l'administrateur sans aire ouverte).</summary>
    public static MediaFolder? Find(ClientDb d, Guid id) => d.MediaFolders.FirstOrDefault(f => f.Id == id);

    public static FolderRight RightOf(ClientDb d, MediaFolder f) => FolderAccess.For(d.Access, f, All(d));

    /// <summary>Téléverser ou ranger un fichier dans ce dossier : écriture dessus (à la racine, tout le monde comme avant).</summary>
    public static bool CanUploadTo(ClientDb d, Guid? folderId) =>
        folderId is not { } id || Find(d, id) is { } f && RightOf(d, f) >= FolderRight.Write;

    public static string? CheckName(string name) =>
        name.Trim().Length == 0 ? "Donnez un nom au dossier." : name.Trim().Length > 60 ? "60 caractères au plus." : null;

    private static string? Option(ClientDb d) => d.Tenant.FoldersOn ? null : "Les dossiers ne sont pas activés pour cette organisation (Réglages).";

    public static (string? Error, MediaFolder? Folder) Create(ClientDb d, string name, Guid? parentId)
    {
        if (Option(d) is { } off) return (off, null);
        name = name.Trim();
        if (CheckName(name) is { } e) return (e, null);
        var all = All(d);
        MediaFolder? parent = null;
        var depth = 1;
        if (parentId is { } pid)
        {
            parent = Find(d, pid);
            if (parent == null) return ("Dossier introuvable.", null);
            if (RightOf(d, parent) < FolderRight.Write) return ("Vous ne pouvez pas créer de dossier dans celui-ci.", null);
            depth = FolderAccess.Chain(parent, all).Count() + 1;
        }
        else if (!FolderAccess.CanCreateAtRoot(d.Access)) return ("Votre niveau de droits ne permet pas de créer des dossiers.", null);
        if (depth > FolderAccess.MaxDepth) return ($"Les dossiers ne s'imbriquent pas au-delà de {FolderAccess.MaxDepth} niveaux.", null);

        var areaId = parent?.AreaId ?? d.Area?.Id ?? Guid.Empty;
        if (all.Any(f => f.AreaId == areaId && f.ParentId == parentId && f.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
            return ("Un dossier porte déjà ce nom ici.", null);
        var folder = new MediaFolder { Name = name, ParentId = parentId, AreaId = areaId, OwnerId = d.Access.UserId };
        d.MediaFolders.Add(folder);
        return (null, folder);
    }

    public static string? Rename(ClientDb d, Guid id, string name)
    {
        if (Find(d, id) is not { } f) return "Dossier introuvable.";
        if (RightOf(d, f) != FolderRight.Owner) return "Seul le propriétaire du dossier peut le renommer.";
        name = name.Trim();
        if (CheckName(name) is { } e) return e;
        if (All(d).Any(x => x.Id != id && x.AreaId == f.AreaId && x.ParentId == f.ParentId && x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
            return "Un dossier porte déjà ce nom ici.";
        f.Name = name;
        return null;
    }

    /// <summary>
    /// Dossier et sous-dossiers à supprimer, avec les fichiers qu'ils contiennent. Refusé si un sous-dossier appartient à quelqu'un d'autre.
    /// Ne retire rien : la médiathèque retire les fichiers (avec leurs contrôles d'usage), puis <see cref="Forget"/> retire les dossiers.
    /// </summary>
    public static string? PlanDelete(ClientDb d, Guid id, out HashSet<Guid> folderIds, out List<Guid> fileIds)
    {
        folderIds = new(); fileIds = new();
        if (Find(d, id) is not { } f) return "Dossier introuvable.";
        if (RightOf(d, f) != FolderRight.Owner) return "Seul le propriétaire du dossier peut le supprimer.";
        var all = All(d);
        var tree = new HashSet<Guid>();   // le dossier et ses descendants
        var queue = new Queue<Guid>(new[] { id });
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!tree.Add(cur)) continue;
            foreach (var child in all.Where(x => x.ParentId == cur)) queue.Enqueue(child.Id);
        }
        foreach (var sub in all.Where(x => tree.Contains(x.Id)))
            if (FolderAccess.For(d.Access, sub, all) != FolderRight.Owner) return $"Le sous-dossier « {sub.Name} » appartient à un autre membre : retirez-le d'abord.";
        folderIds = tree;
        fileIds = d.Root.Media.Where(m => m.ClientId == d.ClientId && m.FolderId is { } fid && tree.Contains(fid)).Select(m => m.Id).ToList();
        return null;
    }

    /// <summary>Retire les dossiers planifiés par <see cref="PlanDelete"/> (après leurs fichiers).</summary>
    public static void Forget(ClientDb d, IEnumerable<Guid> folderIds)
    {
        var set = folderIds.ToHashSet();
        d.Root.MediaFolders.RemoveAll(f => f.ClientId == d.ClientId && set.Contains(f.Id));
    }

    /// <summary>Ajoute, change (write) ou retire (null) le partage d'un dossier pour une personne ou un niveau.</summary>
    public static string? SetShare(ClientDb d, Guid folderId, string kind, Guid target, bool? write)
    {
        if (Find(d, folderId) is not { } f) return "Dossier introuvable.";
        if (!FolderAccess.CanShare(d.Access, f)) return "Seul le propriétaire du dossier le partage, avec un niveau qui le permet.";
        if (write != null)
        {
            if (kind == FolderShareKind.User)
            {
                if (d.Root.Users.FirstOrDefault(u => u.Id == target && u.ClientId == d.ClientId && u.Role == Roles.ClientMember) is not { } member) return "Personne introuvable parmi les membres.";
                if (member.Disabled) return $"{member.Label} est désactivé.";
                var ma = AreaAccess.For(d.Root, d.ClientId, member.Id);
                var area = d.Root.Areas.FirstOrDefault(a => a.Id == f.AreaId)?.Name ?? "";
                if (!ma.HasArea(f.AreaId)) return $"{member.Label} ne voit pas l'aire « {area} ».";
                if (ma.Level?.Pages.Contains(LevelPages.Media) != true) return $"{member.Label} n'a pas la page Médiathèque.";
            }
            else if (kind == FolderShareKind.Level)
            {
                if (!d.Tenant.Levels.Any(l => l.Id == target)) return "Niveau introuvable.";
            }
            else return "Type de partage inconnu.";
        }
        f.Shares.RemoveAll(s => s.Kind == kind && s.TargetId == target);
        if (write is { } w) f.Shares.Add(new FolderShare { Kind = kind, TargetId = target, Write = w });
        return null;
    }

    /// <summary>Donne au niveau les dossiers choisis (« read » ou « write » par dossier) ; les autres dossiers ne lui sont plus partagés.</summary>
    public static string? SetLevelShares(ClientDb d, Guid levelId, IReadOnlyDictionary<Guid, string> modes)
    {
        if (!d.Access.IsOrgAdmin) return "Seul un administrateur de l'organisation gère les niveaux de droits.";
        // dossiers de la médiathèque et des listes de lecture : un même choix par niveau
        var nodes = All(d).Cast<IFolderNode>().Concat(PlaylistFolderOps.All(d));
        foreach (var f in nodes)
        {
            var mode = modes.GetValueOrDefault(f.Id);
            f.Shares.RemoveAll(s => s.Kind == FolderShareKind.Level && s.TargetId == levelId);
            if (mode is "read" or "write") f.Shares.Add(new FolderShare { Kind = FolderShareKind.Level, TargetId = levelId, Write = mode == "write" });
        }
        return null;
    }

    /// <summary>Change le propriétaire d'un dossier : administrateur seul. Le nouveau propriétaire doit être un compte de l'organisation.</summary>
    public static string? TransferOwner(ClientDb d, Guid folderId, Guid userId)
    {
        if (!d.Access.IsOrgAdmin) return "Seul un administrateur change le propriétaire d'un dossier.";
        if (Find(d, folderId) is not { } f) return "Dossier introuvable.";
        if (!d.Users.Any(u => u.Id == userId)) return "Personne introuvable dans l'organisation.";
        f.OwnerId = userId;
        return null;
    }

    /// <summary>
    /// Range des fichiers dans un dossier (null : à la racine de l'aire). Le fichier quitte sa place d'origine : il faut pouvoir écrire dans le dossier
    /// d'origine (à la racine : pouvoir créer des dossiers) et dans le dossier d'arrivée. Rend le nombre rangé et les refus.
    /// </summary>
    public static (int Moved, List<string> Refused) Move(ClientDb d, IEnumerable<Guid> mediaIds, Guid? folderId)
    {
        var moved = 0; var refused = new List<string>();
        var wanted = mediaIds.ToHashSet();
        MediaFolder? target = null;
        if (folderId is { } tid)
        {
            target = Find(d, tid);
            if (target == null || RightOf(d, target) < FolderRight.Write) return (0, new() { "Vous ne pouvez pas ranger de fichier dans ce dossier." });
        }
        else if (!FolderAccess.CanCreateAtRoot(d.Access)) return (0, new() { "Votre niveau de droits ne permet pas de ranger des fichiers à la racine." });

        foreach (var m in d.Root.Media.Where(x => x.ClientId == d.ClientId && wanted.Contains(x.Id) && MediaLibrary.IsFile(x) && !MediaLibrary.IsDrive(x)).ToList())
        {
            if (m.FolderId == folderId) continue;
            if (target != null && target.AreaId != m.AreaId) { refused.Add($"« {m.Name} » est dans une autre aire."); continue; }
            if (m.FolderId is { } from && Find(d, from) is { } src && RightOf(d, src) < FolderRight.Write) { refused.Add($"« {m.Name} » : pas d'écriture dans son dossier."); continue; }
            m.FolderId = folderId;
            moved++;
        }
        return (moved, refused);
    }

    /// <summary>Un fichier qu'on peut retirer ou renommer : à la racine (comme avant), ou dans un dossier où l'on écrit.</summary>
    public static bool CanEditFile(ClientDb d, MediaItem m) =>
        m.FolderId is not { } fid || Find(d, fid) is { } f && RightOf(d, f) >= FolderRight.Write;
}
