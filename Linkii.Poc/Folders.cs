namespace Linkii.Poc;

/// <summary>Droit sur un dossier de la médiathèque, du plus faible au plus fort.</summary>
public enum FolderRight { None, Read, Write, Owner }

/// <summary>
/// Droits sur les dossiers de la médiathèque et des listes de lecture (24 octobre 2026) : l'administrateur de l'organisation a tout ; le propriétaire d'un
/// dossier (ou d'un dossier qui le contient) a tout dessus ; les autres ont le plus fort des partages qui le couvrent (le dossier ou un de ses parents,
/// donné à la personne ou à toute l'organisation, voir <see cref="ItemShare"/>). En « Voit tout · lecture seule », tout est visible, rien ne se modifie.
/// </summary>
public static class FolderAccess
{
    /// <summary>Profondeur maximale : l'arborescence est affichée sur quatre niveaux.</summary>
    public const int MaxDepth = 4;

    public static FolderRight For<T>(AreaAccess a, T f, IReadOnlyCollection<T> all) where T : class, IFolderNode
    {
        return Ownership.Right(a, KindOf(f), f.Id, f.OwnerId, Chain(f, all).Skip(1));
    }

    /// <summary>Droits de la personne sur chaque dossier donné.</summary>
    public static Dictionary<Guid, FolderRight> Snapshot<T>(AreaAccess a, IReadOnlyCollection<T> folders) where T : class, IFolderNode =>
        folders.ToDictionary(f => f.Id, f => For(a, f, folders));

    /// <summary>Domaine d'un dossier : Listes de lecture ou Médiathèque.</summary>
    public static string DomainOf(IFolderNode f) => f is PlaylistFolder ? LevelPages.Playlists : LevelPages.Media;
    public static string KindOf(IFolderNode f) => f switch { PlaylistFolder => ShareKind.PlaylistFolder, CompositionFolder => ShareKind.CompositionFolder, _ => ShareKind.MediaFolder };

    /// <summary>Créer un dossier (ou ranger un élément) à la racine : administrateur, ou utilisateur qui gère les siens dans ce domaine.</summary>
    public static bool CanCreateAtRoot(AreaAccess a, string domain) => a.CanWrite(domain);

    /// <summary>Partager un dossier : son propriétaire (l'administrateur partage tout).</summary>
    public static bool CanShare<T>(AreaAccess a, T f, IReadOnlyCollection<T> all) where T : class, IFolderNode => For(a, f, all) == FolderRight.Owner;

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

    /// <summary>Place d'un dossier dans les onglets « Mes … / Partagés avec moi » : propriétaire, espace (dossier le plus haut) et partage reçu.</summary>
    public static LibraryTab.Info TabInfo<T>(AreaAccess a, T f, IReadOnlyCollection<T> all) where T : class, IFolderNode
    {
        var parents = Chain(f, all).Skip(1).Cast<IFolderNode>().ToList();
        return Ownership.TabInfo(a, KindOf(f), f.Id, f.OwnerId, parents);
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
        folderId is not { } id ? d.Access.CanWrite(LevelPages.Media) : Find(d, id) is { } f && RightOf(d, f) >= FolderRight.Write;

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
        else if (!FolderAccess.CanCreateAtRoot(d.Access, LevelPages.Media)) return ("Vous ne pouvez pas créer de dossier ici (lecture seule).", null);
        if (depth > FolderAccess.MaxDepth) return ($"Les dossiers ne s'imbriquent pas au-delà de {FolderAccess.MaxDepth} niveaux.", null);

        var areaId = parent?.AreaId ?? d.Area?.Id ?? Guid.Empty;
        if (all.Any(f => f.ParentId == parentId && (parentId != null || f.OwnerId == d.Me) && f.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
            return ("Un dossier porte déjà ce nom ici.", null);
        var folder = new MediaFolder { Name = name, ParentId = parentId, AreaId = areaId };
        d.MediaFolders.Add(folder);
        return (null, folder);
    }

    public static string? Rename(ClientDb d, Guid id, string name)
    {
        if (Find(d, id) is not { } f) return "Dossier introuvable.";
        if (RightOf(d, f) != FolderRight.Owner) return "Seul le propriétaire du dossier peut le renommer.";
        name = name.Trim();
        if (CheckName(name) is { } e) return e;
        if (All(d).Any(x => x.Id != id && x.ParentId == f.ParentId && (f.ParentId != null || x.OwnerId == f.OwnerId) && x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
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
        ItemShareOps.Forget(d, ShareKind.MediaFolder, set);
    }

    // ---------- Déplacer un dossier dans un autre (voir FolderMove) ----------

    /// <summary>Ce que contient un dossier directement : fichiers et sous-dossiers.</summary>
    public static FolderContents ContentsOf(ClientDb d, Guid id) =>
        new(d.Root.Media.Count(m => m.ClientId == d.ClientId && m.FolderId == id), All(d).Count(f => f.ParentId == id));

    /// <summary>Destinations d'un dossier : la racine (le dissout) puis les dossiers de l'aire, chacun avec la raison éventuelle de son refus.</summary>
    public static List<FolderTarget> MoveTargets(ClientDb d, Guid id)
    {
        var all = All(d);
        if (Find(d, id) is not { } f) return new();
        var list = new List<FolderTarget> { new(null, "Racine de la médiathèque", 0, f.ParentId == null ? "déjà à la racine" : null) };
        foreach (var g in d.MediaFolders.Select(x => (Folder: x, Path: string.Join(" › ", FolderAccess.Chain(x, all).Reverse().Select(c => c.Name)))).OrderBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase))
        {
            if (RightOf(d, g.Folder) == FolderRight.None) continue;
            var reason = g.Folder.Id == f.ParentId ? "déjà ici" : FolderMove.CheckTarget(all, f, g.Folder, RightOf(d, g.Folder)) is { } r ? FolderMove.Explain(r) : null;
            list.Add(new(g.Folder.Id, g.Path, g.Path.Count(c => c == '›'), reason));
        }
        return list;
    }

    /// <summary>
    /// Range un dossier dans un autre (même aire), ou le dissout quand la destination est la racine : ses fichiers et ses sous-dossiers vont à la racine
    /// de l'aire, le dossier disparaît. Propriétaire du dossier seulement ; il faut pouvoir écrire dans le dossier d'arrivée.
    /// </summary>
    public static string? MoveFolder(ClientDb d, Guid id, Guid? newParent)
    {
        if (Option(d) is { } off) return off;
        if (Find(d, id) is not { } f) return "Dossier introuvable.";
        if (RightOf(d, f) != FolderRight.Owner) return "Seul le propriétaire du dossier peut le déplacer.";
        var all = All(d);
        if (newParent == null)
        {
            if (f.ParentId == null) return null;
            if (FolderMove.RootNameClash(all, f) is { } clash) return $"Un dossier « {clash} » existe déjà à la racine : renommez-le d'abord.";
            foreach (var m in d.Root.Media.Where(m => m.ClientId == d.ClientId && m.FolderId == id)) { m.FolderId = null; m.OwnerId ??= f.OwnerId; }
            foreach (var c in all.Where(x => x.ParentId == id)) c.ParentId = null;
            Forget(d, new[] { id });
            return null;
        }
        if (Find(d, newParent.Value) is not { } target) return "Dossier introuvable.";
        if (f.ParentId == target.Id) return null;
        if (FolderMove.CheckTarget(all, f, target, RightOf(d, target)) is { } reason) return FolderMove.Explain(reason);
        f.ParentId = target.Id;
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
        else if (!FolderAccess.CanCreateAtRoot(d.Access, LevelPages.Media)) return (0, new() { "Vous ne pouvez pas ranger de fichier à la racine (médiathèque en lecture seule)." });

        foreach (var m in d.Root.Media.Where(x => x.ClientId == d.ClientId && wanted.Contains(x.Id) && (MediaLibrary.IsFile(x) || Pres.Is(x))).ToList())
        {
            if (m.FolderId == folderId) continue;
            // les fichiers Drive sont communs à toutes les aires : on peut les ranger dans le dossier de n'importe laquelle
            if (!CanEditFile(d, m)) { refused.Add($"« {m.Name} » : vous ne pouvez pas le ranger (lecture seule)."); continue; }
            m.FolderId = folderId;
            if (Pres.Is(m)) foreach (var pg in d.Root.Media.Where(x => x.ClientId == d.ClientId && Pres.IdOf(x) == m.Id.ToString())) pg.FolderId = folderId;   // les images d'une présentation suivent leur carte
            moved++;
        }
        return (moved, refused);
    }

    /// <summary>Droit de la personne sur un fichier : le sien, celui d'un de ses dossiers, ou un partage (le fichier ou un de ses dossiers). Voir <see cref="Ownership"/>.</summary>
    public static FolderRight RightOfFile(ClientDb d, MediaItem m)
    {
        var all = All(d).ToDictionary(f => f.Id);
        return Ownership.Right(d.Access, ShareKind.Media, m.Id, m.OwnerId, Ownership.Parents(m.FolderId, all));
    }

    /// <summary>Droits de la personne sur des fichiers (une seule lecture des dossiers).</summary>
    public static Dictionary<Guid, FolderRight> RightsOfFiles(ClientDb d, IEnumerable<MediaItem> files)
    {
        var all = All(d).ToDictionary(f => f.Id);
        return files.ToDictionary(m => m.Id, m => Ownership.Right(d.Access, ShareKind.Media, m.Id, m.OwnerId, Ownership.Parents(m.FolderId, all)));
    }

    /// <summary>Un fichier qu'on peut renommer ou ranger : le sien, ou un partage « Peut modifier ».</summary>
    public static bool CanEditFile(ClientDb d, MediaItem m) => RightOfFile(d, m) >= FolderRight.Write;

    /// <summary>Un fichier qu'on peut supprimer : le sien, ou un fichier d'un dossier qu'on modifie (voir <see cref="Ownership.CanDelete"/>).</summary>
    public static bool CanDeleteFile(ClientDb d, MediaItem m) => Ownership.CanDelete(RightOfFile(d, m), m.FolderId);
}
