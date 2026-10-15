namespace Linkii.Poc;

/// <summary>Règles des listes de lecture rangées dans des dossiers (voir <see cref="PlaylistFolder"/>).</summary>
public static class PlaylistOps
{
    /// <summary>
    /// Droit de la personne sur une liste : celui de son dossier (voir <see cref="FolderAccess"/>). À la racine de l'aire, la liste reste comme avant :
    /// visible de tous et modifiable, sauf pour un niveau en lecture seule.
    /// </summary>
    public static FolderRight RightOf(ClientDb d, Playlist p)
    {
        if (p.FolderId is not { } fid || PlaylistFolderOps.Find(d, fid) is not { } f) return d.Access.ReadOnly ? FolderRight.Read : FolderRight.Write;
        return PlaylistFolderOps.RightOf(d, f);
    }

    public static bool CanEdit(ClientDb d, Playlist p) => RightOf(d, p) >= FolderRight.Write;

    /// <summary>
    /// Supprime des listes de lecture : les écrans qui les diffusaient gardent leur instantané publié ; elles ne sont plus choisies (modification en attente sur ces écrans).
    /// Ni les contenus ni la médiathèque ne bougent.
    /// </summary>
    public static void Remove(ClientDb d, IReadOnlyCollection<Guid> listIds)
    {
        var ids = listIds.ToHashSet();
        foreach (var s in d.Screens.Where(s => s.PlaylistId is { } pid && ids.Contains(pid)))
        {
            if (s.PublishedVersion == 0 && d.Playlists.FirstOrDefault(x => x.Id == s.PlaylistId) is { } old)   // écran jamais publié depuis : il garde l'ancien instantané
            {
                s.Published = old.Published.ToList();
                s.PublishedVersion = 1;
            }
            s.PlaylistId = null;
        }
        foreach (var s in d.Screens)   // zones 2 et 3 des écrans découpés
            for (var z = 0; z < s.ZonePlaylistIds.Count; z++) if (s.ZonePlaylistIds[z] is { } zid && ids.Contains(zid)) s.ZonePlaylistIds[z] = null;
        foreach (var z in d.Areas.SelectMany(a => a.Zones)) z.Slots.RemoveAll(x => ids.Contains(x.PlaylistId));   // plus programmées dans un planning
        foreach (var s in d.Screens) s.Slots.RemoveAll(x => ids.Contains(x.PlaylistId));
        d.Playlists.RemoveAll(x => ids.Contains(x.Id));
    }
}

/// <summary>Création, renommage, suppression, partage et rangement des listes de lecture dans les dossiers. Chaque méthode rend un message d'erreur, ou null si c'est fait.</summary>
public static class PlaylistFolderOps
{
    /// <summary>Tous les dossiers de listes de l'organisation, toutes aires confondues (pour remonter aux parents).</summary>
    public static List<PlaylistFolder> All(ClientDb d) => d.Root.PlaylistFolders.Where(f => f.ClientId == d.ClientId).ToList();

    /// <summary>Dossier de l'aire ouverte.</summary>
    public static PlaylistFolder? Find(ClientDb d, Guid id) => d.PlaylistFolders.FirstOrDefault(f => f.Id == id);

    public static FolderRight RightOf(ClientDb d, PlaylistFolder f) => FolderAccess.For(d.Access, f, All(d));

    private static string? Option(ClientDb d) => d.Tenant.FoldersOn ? null : "Les dossiers ne sont pas activés pour cette organisation (Réglages).";

    public static (string? Error, PlaylistFolder? Folder) Create(ClientDb d, string name, Guid? parentId)
    {
        if (Option(d) is { } off) return (off, null);
        name = name.Trim();
        if (FolderOps.CheckName(name) is { } e) return (e, null);
        var all = All(d);
        PlaylistFolder? parent = null;
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
        var folder = new PlaylistFolder { Name = name, ParentId = parentId, AreaId = areaId, OwnerId = d.Access.UserId };
        d.PlaylistFolders.Add(folder);
        return (null, folder);
    }

    public static string? Rename(ClientDb d, Guid id, string name)
    {
        if (Find(d, id) is not { } f) return "Dossier introuvable.";
        if (RightOf(d, f) != FolderRight.Owner) return "Seul le propriétaire du dossier peut le renommer.";
        name = name.Trim();
        if (FolderOps.CheckName(name) is { } e) return e;
        if (All(d).Any(x => x.Id != id && x.AreaId == f.AreaId && x.ParentId == f.ParentId && x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
            return "Un dossier porte déjà ce nom ici.";
        f.Name = name;
        return null;
    }

    /// <summary>
    /// Dossier et sous-dossiers à supprimer, avec les listes qu'ils contiennent. Refusé si un sous-dossier appartient à quelqu'un d'autre.
    /// Ne retire rien : <see cref="PlaylistOps.Remove"/> retire les listes, puis <see cref="Forget"/> retire les dossiers.
    /// </summary>
    public static string? PlanDelete(ClientDb d, Guid id, out HashSet<Guid> folderIds, out List<Guid> playlistIds)
    {
        folderIds = new(); playlistIds = new();
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
        folderIds = tree;
        playlistIds = d.Playlists.Where(p => p.FolderId is { } fid && tree.Contains(fid)).Select(p => p.Id).ToList();
        return null;
    }

    /// <summary>Retire les dossiers planifiés par <see cref="PlanDelete"/> (après leurs listes).</summary>
    public static void Forget(ClientDb d, IEnumerable<Guid> folderIds)
    {
        var set = folderIds.ToHashSet();
        d.Root.PlaylistFolders.RemoveAll(f => f.ClientId == d.ClientId && set.Contains(f.Id));
    }

    /// <summary>Ajoute, change (write) ou retire (null) le partage d'un dossier de listes, pour une personne ou un niveau.</summary>
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
                if (ma.Level?.Pages.Contains(LevelPages.Playlists) != true) return $"{member.Label} n'a pas la page Listes de lecture.";
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

    /// <summary>Range des listes dans un dossier, ou à la racine (null). Il faut pouvoir écrire dans le dossier d'origine et dans celui d'arrivée.</summary>
    public static (int Moved, List<string> Refused) Move(ClientDb d, IEnumerable<Guid> playlistIds, Guid? folderId)
    {
        var moved = 0; var refused = new List<string>();
        var wanted = playlistIds.ToHashSet();
        PlaylistFolder? target = null;
        if (folderId is { } tid)
        {
            target = Find(d, tid);
            if (target == null || RightOf(d, target) < FolderRight.Write) return (0, new() { "Vous ne pouvez pas ranger de liste dans ce dossier." });
        }
        else if (!FolderAccess.CanCreateAtRoot(d.Access)) return (0, new() { "Votre niveau de droits ne permet pas de ranger des listes à la racine." });

        foreach (var p in d.Playlists.Where(x => wanted.Contains(x.Id)).ToList())
        {
            if (p.FolderId == folderId) continue;
            if (target != null && target.AreaId != p.AreaId) { refused.Add($"« {p.Name} » est dans une autre aire."); continue; }
            if (p.FolderId is { } from && Find(d, from) is { } src && RightOf(d, src) < FolderRight.Write) { refused.Add($"« {p.Name} » : pas d'écriture dans son dossier."); continue; }
            p.FolderId = folderId;   // ranger ne change pas le contenu : pas de mise à jour à publier
            moved++;
        }
        return (moved, refused);
    }
}
