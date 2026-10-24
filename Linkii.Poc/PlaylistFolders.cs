namespace Linkii.Poc;

/// <summary>Règles des listes de lecture rangées dans des dossiers (voir <see cref="PlaylistFolder"/>).</summary>
public static class PlaylistOps
{
    /// <summary>
    /// Droit de la personne sur une liste : la sienne (ou celle d'un de ses dossiers), un partage (la liste ou un de ses dossiers), ou la lecture seule
    /// de la page (voir <see cref="Ownership"/>).
    /// </summary>
    public static FolderRight RightOf(ClientDb d, Playlist p) => RightOf(d, p, PlaylistFolderOps.All(d).ToDictionary(f => f.Id));

    public static FolderRight RightOf(ClientDb d, Playlist p, IReadOnlyDictionary<Guid, PlaylistFolder> folders)
    {
        return Ownership.Right(d.Access, ShareKind.Playlist, p.Id, p.OwnerId, Ownership.Parents(p.FolderId, folders));
    }

    /// <summary>Droits de la personne sur chaque liste de l'aire (une seule lecture des dossiers).</summary>
    public static Dictionary<Guid, FolderRight> Rights(ClientDb d)
    {
        var folders = PlaylistFolderOps.All(d).ToDictionary(f => f.Id);
        return d.Playlists.ToDictionary(p => p.Id, p => RightOf(d, p, folders));
    }

    public static bool CanEdit(ClientDb d, Playlist p) => RightOf(d, p) >= FolderRight.Write;

    /// <summary>Supprimer une liste : la sienne, ou une liste d'un dossier qu'on modifie.</summary>
    public static bool CanDelete(ClientDb d, Playlist p) => Ownership.CanDelete(RightOf(d, p), p.FolderId);

    /// <summary>
    /// Listes proposées dans les choix (écrans, zones, murs, planning) : celles que la personne voit, plus celles déjà en place
    /// sur un écran, une zone ou un créneau qu'elle voit (pour ne pas perdre ni masquer le choix actuel). Triées par nom.
    /// </summary>
    public static List<Playlist> Choosable(ClientDb d)
    {
        // (les listes de l'instantané du planning comptent aussi : le calcul « à publier » d'un écran les relit)
        var inUse = d.AllScreens.SelectMany(s => s.ZonePlaylists().OfType<Guid>().Concat(s.Slots.Select(x => x.PlaylistId)).Concat(s.PublishedListRevs.Keys))
            .Concat(d.Areas.Where(a => d.Access.HasArea(a.Id)).SelectMany(a => a.Zones).Where(z => d.Access.SeesZone(z.Id))
                .SelectMany(z => z.Slots.Select(x => x.PlaylistId).Concat(z.PlaylistId is { } w ? [w] : Array.Empty<Guid>())))
            .ToHashSet();
        var rights = Rights(d);
        return d.Playlists.Where(p => rights.GetValueOrDefault(p.Id) != FolderRight.None || inUse.Contains(p.Id))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Supprime des listes de lecture : les écrans qui les diffusaient gardent leur instantané publié ; elles ne sont plus choisies (modification en attente sur ces écrans).
    /// Ni les contenus ni la médiathèque ne bougent.
    /// </summary>
    public static void Remove(ClientDb d, IReadOnlyCollection<Guid> listIds)
    {
        var ids = listIds.ToHashSet();
        var screens = d.Root.Screens.Where(s => s.ClientId == d.ClientId).ToList();   // les listes n'ont plus d'aire : les écrans de toutes les aires
        foreach (var s in screens.Where(s => s.PlaylistId is { } pid && ids.Contains(pid)))
        {
            if (s.PublishedVersion == 0 && d.Playlists.FirstOrDefault(x => x.Id == s.PlaylistId) is { } old)   // écran jamais publié depuis : il garde l'ancien instantané
            {
                s.Published = old.Published.ToList();
                s.PublishedVersion = 1;
            }
            s.PlaylistId = null;
        }
        foreach (var s in screens)   // zones 2 et 3 des écrans découpés
            for (var z = 0; z < s.ZonePlaylistIds.Count; z++) if (s.ZonePlaylistIds[z] is { } zid && ids.Contains(zid)) s.ZonePlaylistIds[z] = null;
        foreach (var z in d.Areas.SelectMany(a => a.Zones)) z.Slots.RemoveAll(x => ids.Contains(x.PlaylistId));   // plus programmées dans un planning
        foreach (var s in screens) s.Slots.RemoveAll(x => ids.Contains(x.PlaylistId));
        d.Playlists.RemoveAll(x => ids.Contains(x.Id));
        ItemShareOps.Forget(d, ShareKind.Playlist, ids);
    }

    /// <summary>
    /// Copie d'une liste (nouveaux identifiants d'éléments), nommée « (copie) », « (copie 2) »… ; choisie sur aucun écran.
    /// Elle reste dans le dossier de l'original si la personne peut y écrire, sinon elle part à la racine de l'aire.
    /// </summary>
    public static Playlist Copy(ClientDb d, Playlist p)
    {
        var names = d.Playlists.Select(x => x.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var name = p.Name + " (copie)";
        for (var n = 2; names.Contains(name); n++) name = $"{p.Name} (copie {n})";
        var copy = new Playlist
        {
            Name = name, UpdatedAt = DateTime.UtcNow,
            FolderId = p.FolderId is { } f && CanEdit(d, p) ? f : null,
            Draft = p.Draft.Select(i => new PlaylistItem { MediaId = i.MediaId, DurationSec = i.DurationSec, DurationMode = i.DurationMode, Placement = i.Placement, Off = i.Off, StartsAt = i.StartsAt, EndsAt = i.EndsAt,
                // QR de comptage : mêmes réglages, mais une adresse courte à elle (les scans de la copie ne se mélangent pas à ceux de l'original)
                QrOn = i.QrOn, QrCode = i.QrOn ? QrTracking.NewCode() : null, QrUrl = i.QrUrl, QrLabel = i.QrLabel, QrBase = i.QrBase, QrX = i.QrX, QrY = i.QrY, QrSize = i.QrSize }).ToList()
        };
        d.Playlists.Add(copy);
        return copy;
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
        else if (!FolderAccess.CanCreateAtRoot(d.Access, LevelPages.Playlists)) return ("Vous ne pouvez pas créer de dossier ici (lecture seule).", null);
        if (depth > FolderAccess.MaxDepth) return ($"Les dossiers ne s'imbriquent pas au-delà de {FolderAccess.MaxDepth} niveaux.", null);

        var areaId = parent?.AreaId ?? d.Area?.Id ?? Guid.Empty;
        if (all.Any(f => f.ParentId == parentId && (parentId != null || f.OwnerId == d.Me) && f.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
            return ("Un dossier porte déjà ce nom ici.", null);
        var folder = new PlaylistFolder { Name = name, ParentId = parentId, AreaId = areaId };
        d.PlaylistFolders.Add(folder);
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
        ItemShareOps.Forget(d, ShareKind.PlaylistFolder, set);
    }

    // ---------- Déplacer un dossier dans un autre (voir FolderMove) ----------

    public static FolderContents ContentsOf(ClientDb d, Guid id) =>
        new(d.Root.Playlists.Count(p => p.ClientId == d.ClientId && p.FolderId == id), All(d).Count(f => f.ParentId == id));

    public static List<FolderTarget> MoveTargets(ClientDb d, Guid id)
    {
        var all = All(d);
        if (Find(d, id) is not { } f) return new();
        var list = new List<FolderTarget> { new(null, "Racine des listes de lecture", 0, f.ParentId == null ? "déjà à la racine" : null) };
        foreach (var g in d.PlaylistFolders.Select(x => (Folder: x, Path: string.Join(" › ", FolderAccess.Chain(x, all).Reverse().Select(c => c.Name)))).OrderBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase))
        {
            if (RightOf(d, g.Folder) == FolderRight.None) continue;
            var reason = g.Folder.Id == f.ParentId ? "déjà ici" : FolderMove.CheckTarget(all, f, g.Folder, RightOf(d, g.Folder)) is { } r ? FolderMove.Explain(r) : null;
            list.Add(new(g.Folder.Id, g.Path, g.Path.Count(c => c == '›'), reason));
        }
        return list;
    }

    /// <summary>Range un dossier de listes dans un autre, ou le dissout (destination : la racine) : ses listes et ses sous-dossiers vont à la racine de l'aire.</summary>
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
            foreach (var p in d.Root.Playlists.Where(p => p.ClientId == d.ClientId && p.FolderId == id)) p.FolderId = null;
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
        else if (!FolderAccess.CanCreateAtRoot(d.Access, LevelPages.Playlists)) return (0, new() { "Vous ne pouvez pas ranger de liste à la racine (listes en lecture seule)." });

        foreach (var p in d.Playlists.Where(x => wanted.Contains(x.Id)).ToList())
        {
            if (p.FolderId == folderId) continue;
            if (!PlaylistOps.CanEdit(d, p)) { refused.Add($"« {p.Name} » : vous ne pouvez pas la ranger (lecture seule)."); continue; }
            p.FolderId = folderId;   // ranger ne change pas le contenu : pas de mise à jour à publier
            moved++;
        }
        return (moved, refused);
    }
}
