namespace Linkii.Poc;

/// <summary>Règles des listes interactives rangées dans des dossiers (même modèle que <see cref="PlaylistOps"/> : propriétaire, partage, lecture seule).</summary>
public static class InteractiveListOps
{
    public static FolderRight RightOf(ClientDb d, InteractiveList l) => RightOf(d, l, InteractiveFolderOps.All(d).ToDictionary(f => f.Id));

    public static FolderRight RightOf(ClientDb d, InteractiveList l, IReadOnlyDictionary<Guid, InteractiveListFolder> folders) =>
        Ownership.Right(d.Access, ShareKind.IList, l.Id, l.OwnerId, Ownership.Parents(l.FolderId, folders));

    /// <summary>Droit de la personne sur chaque liste interactive de l'organisation (une seule lecture des dossiers).</summary>
    public static Dictionary<Guid, FolderRight> Rights(ClientDb d)
    {
        var folders = InteractiveFolderOps.All(d).ToDictionary(f => f.Id);
        return d.InteractiveLists.ToDictionary(l => l.Id, l => RightOf(d, l, folders));
    }

    public static bool CanEdit(ClientDb d, InteractiveList l) => RightOf(d, l) >= FolderRight.Write;

    /// <summary>Supprimer une liste : la sienne, ou une liste d'un dossier qu'on modifie.</summary>
    public static bool CanDelete(ClientDb d, InteractiveList l) => Ownership.CanDelete(RightOf(d, l), l.FolderId);

    /// <summary>Copie « (copie) », « (copie 2) »… dans le dossier de l'original si la personne peut y écrire, sinon à la racine. Aucune borne ne l'affiche.</summary>
    public static InteractiveList Copy(ClientDb d, InteractiveList src)
    {
        if (d.InteractiveLists.Count >= InteractiveLists.MaxLists) throw new InvalidOperationException($"Au plus {InteractiveLists.MaxLists} listes interactives.");
        var names = d.InteractiveLists.Select(x => x.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var name = InteractiveLists.Cap(src.Name + " (copie)", 80);
        for (var n = 2; names.Contains(name); n++) name = InteractiveLists.Cap($"{src.Name} (copie {n})", 80);
        var c = System.Text.Json.JsonSerializer.Deserialize<InteractiveList>(System.Text.Json.JsonSerializer.Serialize(src))!;
        var map = c.Pages.ToDictionary(p => p.Id, _ => Guid.NewGuid());
        foreach (var p in c.Pages)
        {
            p.Id = map[p.Id];
            foreach (var e in p.Els)
            {
                e.Id = Guid.NewGuid();
                if (e.Action.PageId is { } pg && map.TryGetValue(pg, out var np)) e.Action.PageId = np;   // les tuiles de la copie mènent aux pages de la copie
                e.Action.Item.Id = Guid.NewGuid(); e.Action.Item.QrCode = null;   // un QR dupliqué aurait le même compteur : le code est recréé à l'enregistrement
            }
        }
        c.Id = Guid.NewGuid(); c.Name = name; c.Revision = 1; c.CreatedUtc = c.UpdatedUtc = DateTime.UtcNow;
        c.OwnerId = null;   // ScopedList.Add donne la copie à la personne qui la crée
        c.FolderId = src.FolderId is { } f && CanEdit(d, src) ? f : null;
        d.InteractiveLists.Add(c);
        return c;
    }

    /// <summary>Renomme une liste (celle qu'on peut modifier).</summary>
    public static string? Rename(ClientDb d, Guid id, string name)
    {
        if (d.InteractiveLists.FirstOrDefault(x => x.Id == id) is not { } l) return "Liste introuvable.";
        if (!CanEdit(d, l)) return "Cette liste est en lecture seule.";
        name = InteractiveLists.Cap(name, 80);
        if (name.Length == 0) return "Donnez un nom à la liste.";
        l.Name = name; l.UpdatedUtc = DateTime.UtcNow;
        return null;
    }

    /// <summary>Supprime des listes (celles qu'on peut supprimer, et qu'aucune borne n'affiche). Rend le nombre supprimé et les refus.</summary>
    public static (int Removed, List<string> Refused) Remove(ClientDb d, IReadOnlyCollection<Guid> ids)
    {
        var removed = new HashSet<Guid>(); var refused = new List<string>();
        foreach (var l in d.InteractiveLists.Where(x => ids.Contains(x.Id)).ToList())
        {
            if (!CanDelete(d, l)) { refused.Add($"« {l.Name} » : seul son propriétaire la supprime."); continue; }
            if (InteractiveLists.UsedBy(d.AllScreens, l.Id).Count > 0) { refused.Add($"« {l.Name} » : une borne l'affiche encore."); continue; }
            removed.Add(l.Id);
        }
        d.InteractiveLists.RemoveAll(x => removed.Contains(x.Id));
        ItemShareOps.Forget(d, ShareKind.IList, removed);
        return (removed.Count, refused);
    }
}

/// <summary>Création, renommage, suppression, partage et rangement des dossiers de listes interactives. Chaque méthode rend un message d'erreur, ou null.</summary>
public static class InteractiveFolderOps
{
    public static List<InteractiveListFolder> All(ClientDb d) => d.Root.InteractiveListFolders.Where(f => f.ClientId == d.ClientId).ToList();

    public static InteractiveListFolder? Find(ClientDb d, Guid id) => d.InteractiveListFolders.FirstOrDefault(f => f.Id == id);

    public static FolderRight RightOf(ClientDb d, InteractiveListFolder f) => FolderAccess.For(d.Access, f, All(d));

    public static (string? Error, InteractiveListFolder? Folder) Create(ClientDb d, string name, Guid? parentId)
    {
        name = name.Trim();
        if (FolderOps.CheckName(name) is { } e) return (e, null);
        var all = All(d);
        InteractiveListFolder? parent = null;
        var depth = 1;
        if (parentId is { } pid)
        {
            parent = Find(d, pid);
            if (parent == null) return ("Dossier introuvable.", null);
            if (RightOf(d, parent) < FolderRight.Write) return ("Vous ne pouvez pas créer de dossier dans celui-ci.", null);
            depth = FolderAccess.Chain(parent, all).Count() + 1;
        }
        else if (!FolderAccess.CanCreateAtRoot(d.Access, LevelPages.Playlists)) return ("Vous ne pouvez pas créer de dossier ici (lecture seule).", null);
        if (depth > FolderAccess.MaxDepth) return ($"Les dossiers ne s'imbriquent pas au-delà de {FolderAccess.MaxDepth} niveaux.", null);
        if (all.Any(f => f.ParentId == parentId && (parentId != null || f.OwnerId == d.Me) && f.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
            return ("Un dossier porte déjà ce nom ici.", null);
        var folder = new InteractiveListFolder { Name = name, ParentId = parentId, AreaId = parent?.AreaId ?? d.Area?.Id ?? Guid.Empty };
        d.InteractiveListFolders.Add(folder);
        return (null, folder);
    }

    public static string? Rename(ClientDb d, Guid id, string name)
    {
        if (Find(d, id) is not { } f) return "Dossier introuvable.";
        if (RightOf(d, f) != FolderRight.Owner) return "Seul le propriétaire du dossier peut le renommer.";
        name = name.Trim();
        if (FolderOps.CheckName(name) is { } e) return e;
        if (All(d).Any(x => x.Id != id && x.ParentId == f.ParentId && (f.ParentId != null || x.OwnerId == f.OwnerId) && x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
            return "Un dossier porte déjà ce nom ici.";
        f.Name = name;
        return null;
    }

    /// <summary>Dossier et sous-dossiers à supprimer, avec les listes qu'ils contiennent. Refusé si un sous-dossier appartient à quelqu'un d'autre ou si une borne affiche une des listes.</summary>
    public static string? PlanDelete(ClientDb d, Guid id, out HashSet<Guid> folderIds, out List<Guid> listIds)
    {
        folderIds = new(); listIds = new();
        if (Find(d, id) is not { } f) return "Dossier introuvable.";
        if (RightOf(d, f) != FolderRight.Owner) return "Seul le propriétaire du dossier peut le supprimer.";
        var all = All(d);
        var tree = new HashSet<Guid>();
        var queue = new Queue<Guid>(new[] { id });
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!tree.Add(cur)) continue;
            foreach (var child in all.Where(x => x.ParentId == cur)) queue.Enqueue(child.Id);
        }
        foreach (var sub in all.Where(x => tree.Contains(x.Id)))
            if (FolderAccess.For(d.Access, sub, all) != FolderRight.Owner) return $"Le sous-dossier « {sub.Name} » appartient à un autre membre : retirez-le d'abord.";
        var inside = d.Root.InteractiveLists.Where(l => l.ClientId == d.ClientId && l.FolderId is { } fid && tree.Contains(fid)).ToList();
        var busy = inside.FirstOrDefault(l => InteractiveLists.UsedBy(d.AllScreens, l.Id).Count > 0);
        if (busy != null) return $"La liste « {busy.Name} » est affichée par une borne : choisissez-en une autre sur cette borne avant de supprimer le dossier.";
        folderIds = tree;
        listIds = inside.Select(l => l.Id).ToList();
        return null;
    }

    public static void Forget(ClientDb d, IEnumerable<Guid> folderIds)
    {
        var set = folderIds.ToHashSet();
        d.Root.InteractiveListFolders.RemoveAll(f => f.ClientId == d.ClientId && set.Contains(f.Id));
        ItemShareOps.Forget(d, ShareKind.IListFolder, set);
    }

    public static FolderContents ContentsOf(ClientDb d, Guid id) =>
        new(d.Root.InteractiveLists.Count(l => l.ClientId == d.ClientId && l.FolderId == id), All(d).Count(f => f.ParentId == id));

    public static List<FolderTarget> MoveTargets(ClientDb d, Guid id)
    {
        var all = All(d);
        if (Find(d, id) is not { } f) return new();
        var list = new List<FolderTarget> { new(null, "Racine des listes interactives", 0, f.ParentId == null ? "déjà à la racine" : null) };
        foreach (var g in d.InteractiveListFolders.Select(x => (Folder: x, Path: string.Join(" › ", FolderAccess.Chain(x, all).Reverse().Select(c => c.Name)))).OrderBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase))
        {
            if (RightOf(d, g.Folder) == FolderRight.None) continue;
            var reason = g.Folder.Id == f.ParentId ? "déjà ici" : FolderMove.CheckTarget(all, f, g.Folder, RightOf(d, g.Folder)) is { } r ? FolderMove.Explain(r) : null;
            list.Add(new(g.Folder.Id, g.Path, g.Path.Count(c => c == '›'), reason));
        }
        return list;
    }

    /// <summary>Range un dossier dans un autre, ou le dissout (destination : la racine) : ses listes et ses sous-dossiers vont à la racine.</summary>
    public static string? MoveFolder(ClientDb d, Guid id, Guid? newParent)
    {
        if (Find(d, id) is not { } f) return "Dossier introuvable.";
        if (RightOf(d, f) != FolderRight.Owner) return "Seul le propriétaire du dossier peut le déplacer.";
        var all = All(d);
        if (newParent == null)
        {
            if (f.ParentId == null) return null;
            if (FolderMove.RootNameClash(all, f) is { } clash) return $"Un dossier « {clash} » existe déjà à la racine : renommez-le d'abord.";
            foreach (var l in d.Root.InteractiveLists.Where(l => l.ClientId == d.ClientId && l.FolderId == id)) l.FolderId = null;
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

    /// <summary>Range des listes dans un dossier, ou à la racine (null). Il faut pouvoir écrire dans le dossier d'origine et dans celui d'arrivée.</summary>
    public static (int Moved, List<string> Refused) Move(ClientDb d, IEnumerable<Guid> listIds, Guid? folderId)
    {
        var moved = 0; var refused = new List<string>();
        var wanted = listIds.ToHashSet();
        if (folderId is { } tid)
        {
            var target = Find(d, tid);
            if (target == null || RightOf(d, target) < FolderRight.Write) return (0, new() { "Vous ne pouvez pas ranger de liste dans ce dossier." });
        }
        else if (!FolderAccess.CanCreateAtRoot(d.Access, LevelPages.Playlists)) return (0, new() { "Vous ne pouvez pas ranger de liste à la racine (listes en lecture seule)." });
        foreach (var l in d.InteractiveLists.Where(x => wanted.Contains(x.Id)).ToList())
        {
            if (l.FolderId == folderId) continue;
            if (!InteractiveListOps.CanEdit(d, l)) { refused.Add($"« {l.Name} » : vous ne pouvez pas la ranger (lecture seule)."); continue; }
            l.FolderId = folderId;
            moved++;
        }
        return (moved, refused);
    }
}

/// <summary>Un dossier de listes interactives vu par la personne, avec son droit, le nom de son propriétaire et s'il est partagé.</summary>
public record InteractiveFolderRow(InteractiveListFolder Folder, FolderRight Right, string OwnerName, bool Shared);

/// <summary>Dossiers et listes interactives d'une organisation, vus par la personne connectée (même rôle que <see cref="PlaylistLibrary"/>).</summary>
public class InteractiveListLibrary(TenantStore store)
{
    public List<InteractiveFolderRow> Folders() => store.Read(d =>
    {
        var all = InteractiveFolderOps.All(d);
        var shared = ItemShareOps.Counts(d, ShareKind.IListFolder);
        var rows = new List<InteractiveFolderRow>();
        foreach (var f in d.InteractiveListFolders)
        {
            var right = FolderAccess.For(d.Access, f, all);
            if (right == FolderRight.None) continue;
            rows.Add(new(f, right, d.Users.FirstOrDefault(u => u.Id == f.OwnerId)?.Label ?? "—", shared.ContainsKey(f.Id)));
        }
        return rows.OrderBy(r => r.Folder.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    public Dictionary<Guid, FolderRight> ListRights() => store.Read(InteractiveListOps.Rights);

    public (string? Error, InteractiveListFolder? Folder) CreateFolder(string name, Guid? parentId) => store.Write(d => InteractiveFolderOps.Create(d, name, parentId));
    public string? RenameFolder(Guid id, string name) => store.Write(d => InteractiveFolderOps.Rename(d, id, name));
    public string? MoveFolder(Guid id, Guid? newParent) => store.Write(d => InteractiveFolderOps.MoveFolder(d, id, newParent));
    public List<FolderTarget> MoveFolderTargets(Guid id) => store.Read(d => InteractiveFolderOps.MoveTargets(d, id));
    public FolderContents ContentsOfFolder(Guid id) => store.Read(d => InteractiveFolderOps.ContentsOf(d, id));

    public string? DeleteFolder(Guid id) => store.Write(d =>
    {
        var err = InteractiveFolderOps.PlanDelete(d, id, out var folders, out var lists);
        if (err != null) return err;
        InteractiveListOps.Remove(d, lists);
        InteractiveFolderOps.Forget(d, folders);
        return (string?)null;
    });

    public (int Moved, List<string> Refused) MoveLists(IReadOnlyCollection<Guid> ids, Guid? folderId) => store.Write(d => InteractiveFolderOps.Move(d, ids, folderId));
    public string? RenameList(Guid id, string name) => store.Write(d => InteractiveListOps.Rename(d, id, name));
    public (int Removed, List<string> Refused) RemoveLists(IReadOnlyCollection<Guid> ids) => store.Write(d => InteractiveListOps.Remove(d, ids));
    public InteractiveList Duplicate(Guid id) => store.Write(d => InteractiveListOps.Copy(d, d.InteractiveLists.First(x => x.Id == id)));

    /// <summary>Où ranger : la racine (si la personne peut y ranger) et les dossiers où elle écrit, avec leur chemin.</summary>
    public List<(Guid? Id, string Path)> MoveTargets() => store.Read(d =>
    {
        var all = InteractiveFolderOps.All(d);
        var list = new List<(Guid? Id, string Path)>();
        if (FolderAccess.CanCreateAtRoot(d.Access, LevelPages.Playlists)) list.Add((null, "Mes listes interactives (racine)"));
        foreach (var f in d.InteractiveListFolders)
        {
            if (FolderAccess.For(d.Access, f, all) < FolderRight.Write) continue;
            list.Add((f.Id, string.Join(" › ", FolderAccess.Chain(f, all).Reverse().Select(x => x.Name))));
        }
        return list.OrderBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
    });
}
