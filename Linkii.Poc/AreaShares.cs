namespace Linkii.Poc;

/// <summary>Ce qui se partage entre aires de gestion.</summary>
public static class ShareKind
{
    public const string Media = "media", MediaFolder = "mediafolder", Playlist = "playlist", PlaylistFolder = "playlistfolder";
    public static string Domain(string kind) => kind is Media or MediaFolder ? LevelPages.Media : LevelPages.Playlists;
    public static bool IsFolder(string kind) => kind is MediaFolder or PlaylistFolder;
    public static string Label(string kind) => kind switch { Media => "média", MediaFolder => "dossier de médias", Playlist => "liste de lecture", _ => "dossier de listes" };
}

/// <summary>
/// Partage d'un élément d'une aire avec une autre aire, <b>en lien vivant</b> (rien n'est copié) et en lecture seule : l'aire qui reçoit voit l'élément
/// d'origine, toujours à jour, et l'utilise (médias dans ses listes, listes sur ses écrans) sans pouvoir le modifier, le ranger ni le supprimer.
/// Un dossier partagé donne aussi ses sous-dossiers et son contenu, ajouts futurs compris ; une liste partagée rend ses médias lisibles.
/// </summary>
public class AreaShare : IClientOwned
{
    public Guid ClientId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = ShareKind.Media;
    public Guid ItemId { get; set; }
    public Guid FromAreaId { get; set; }
    public Guid ToAreaId { get; set; }
    public Guid? FromUserId { get; set; }
    public DateTime SharedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Les éléments d'autres aires que l'aire ouverte reçoit en partage (ensembles d'identifiants ; ne contient jamais d'élément de l'aire elle-même).
/// <see cref="ScopedList{T}"/> les ajoute à la vue de l'aire ; <see cref="FolderAccess"/>, <see cref="FolderOps.CanEditFile"/> et <see cref="PlaylistOps.RightOf"/> les mettent en lecture seule.
/// </summary>
public class SharedView
{
    public static readonly SharedView None = new();

    /// <summary>Médias partagés directement : affichés à la racine de la médiathèque.</summary>
    public HashSet<Guid> Media { get; } = new();
    /// <summary>Tous les médias d'autres aires lisibles ici : directs, ceux des dossiers partagés et ceux qu'utilisent les listes partagées (pas listés dans la médiathèque pour ces derniers).</summary>
    public HashSet<Guid> MediaAll { get; } = new();
    public HashSet<Guid> MediaFolders { get; } = new();
    public HashSet<Guid> PlaylistFolders { get; } = new();
    public HashSet<Guid> Playlists { get; } = new();

    public bool Any => MediaAll.Count + MediaFolders.Count + PlaylistFolders.Count + Playlists.Count > 0;

    public bool Has(object x) => x switch
    {
        MediaItem m => MediaAll.Contains(m.Id),
        MediaFolder f => MediaFolders.Contains(f.Id),
        PlaylistFolder f => PlaylistFolders.Contains(f.Id),
        Playlist p => Playlists.Contains(p.Id),
        _ => false
    };

    public static SharedView Build(Db root, Guid clientId, Guid? areaId)
    {
        if (areaId is not { } area) return None;
        var mine = root.AreaShares.Where(s => s.ClientId == clientId && s.ToAreaId == area).ToList();
        if (mine.Count == 0) return None;
        var v = new SharedView();
        HashSet<Guid> Seeds(string kind) => mine.Where(s => s.Kind == kind).Select(s => s.ItemId).ToHashSet();

        // dossiers de médias : ceux partagés et leurs sous-dossiers ; leurs fichiers
        Expand(root.MediaFolders.Where(f => f.ClientId == clientId).ToList(), Seeds(ShareKind.MediaFolder), area, v.MediaFolders);
        var seedMedia = Seeds(ShareKind.Media);
        foreach (var m in root.Media.Where(m => m.ClientId == clientId && m.AreaId != area && MediaLibrary.IsFile(m)))
        {
            if (seedMedia.Contains(m.Id)) { v.Media.Add(m.Id); v.MediaAll.Add(m.Id); }
            else if (m.FolderId is { } f && v.MediaFolders.Contains(f)) v.MediaAll.Add(m.Id);
        }

        // dossiers de listes : ceux partagés et leurs sous-dossiers ; leurs listes, et les médias que ces listes utilisent
        Expand(root.PlaylistFolders.Where(f => f.ClientId == clientId).ToList(), Seeds(ShareKind.PlaylistFolder), area, v.PlaylistFolders);
        var seedLists = Seeds(ShareKind.Playlist);
        var byId = root.Media.Where(m => m.ClientId == clientId).ToDictionary(m => m.Id);
        foreach (var p in root.Playlists.Where(p => p.ClientId == clientId && p.AreaId != area))
        {
            if (!seedLists.Contains(p.Id) && !(p.FolderId is { } f && v.PlaylistFolders.Contains(f))) continue;
            v.Playlists.Add(p.Id);
            foreach (var id in p.Draft.Select(i => i.MediaId).Concat(p.Published.Select(i => i.MediaId).OfType<Guid>()))
                AddUsed(id, byId, area, v.MediaAll);
        }
        return v;
    }

    private static void AddUsed(Guid id, Dictionary<Guid, MediaItem> byId, Guid area, HashSet<Guid> into)
    {
        if (!byId.TryGetValue(id, out var m)) return;
        if (m.AreaId != area) into.Add(m.Id);
        if (m.AppId == null) return;
        foreach (var v in m.Settings.Values)   // un contenu (app) utilise des médias dans ses paramètres
            foreach (var mid in AppField.MediaIds(v)) if (byId.TryGetValue(mid, out var inner) && inner.AreaId != area) into.Add(inner.Id);
    }

    /// <summary>Les dossiers choisis et leurs descendants, hors dossiers de l'aire elle-même.</summary>
    private static void Expand<T>(List<T> all, HashSet<Guid> seeds, Guid area, HashSet<Guid> into) where T : class, IFolderNode
    {
        var frontier = all.Where(f => seeds.Contains(f.Id) && f.AreaId != area).ToList();
        while (frontier.Count > 0)
        {
            var ids = frontier.Select(f => f.Id).ToList();
            into.UnionWith(ids);
            frontier = all.Where(f => f.ParentId is { } p && ids.Contains(p) && !into.Contains(f.Id) && f.AreaId != area).ToList();
        }
    }
}

/// <summary>Une aire avec laquelle un élément est partagé, et combien d'écrans de cette aire diffusent la liste (listes seulement).</summary>
public record AreaShareRow(Guid AreaId, string Name, DateTime Utc, int Screens);

/// <summary>Une aire à qui l'on peut partager : <paramref name="Shared"/> dit si c'est déjà fait.</summary>
public record AreaTarget(Guid Id, string Name, int Screens, bool Shared);

/// <summary>Partages entre aires : qui partage quoi (droits de l'émetteur), à quelles aires, retrait côté émetteur ou destinataire.</summary>
public static class AreaShareOps
{
    private static Area? CurrentArea(ClientDb d) => d.Area;

    /// <summary>Aires vers lesquelles la personne peut partager : les siennes, sauf l'aire ouverte.</summary>
    public static List<AreaTarget> Targets(ClientDb d, string kind, Guid itemId)
    {
        if (CurrentArea(d) is not { } cur) return new();
        return d.Access.Areas.Where(a => a.Id != cur.Id).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new AreaTarget(a.Id, a.Name, d.Root.Screens.Count(s => s.ClientId == d.ClientId && s.AreaId == a.Id),
                d.AreaShares.Any(s => s.ClientId == d.ClientId && s.Kind == kind && s.ItemId == itemId && s.ToAreaId == a.Id))).ToList();
    }

    /// <summary>Peut-on partager cet élément de l'aire ouverte ? Propriétaire (ou administrateur) d'un dossier, ou droit d'écriture sur le média ou la liste.</summary>
    public static bool CanShare(ClientDb d, string kind, Guid itemId)
    {
        if (CurrentArea(d) is not { } cur || d.Access.Areas.Count < 2 && !d.Access.IsOrgAdmin) return false;
        return kind switch
        {
            ShareKind.Media => d.Media.FirstOrDefault(m => m.Id == itemId) is { } m && m.AreaId == cur.Id && MediaLibrary.IsFile(m) && !MediaLibrary.IsDrive(m) && FolderOps.CanEditFile(d, m),
            ShareKind.MediaFolder => d.MediaFolders.FirstOrDefault(f => f.Id == itemId) is { } f && f.AreaId == cur.Id && FolderAccess.CanShare(d.Access, f),
            ShareKind.Playlist => d.Playlists.FirstOrDefault(p => p.Id == itemId) is { } p && p.AreaId == cur.Id && PlaylistOps.CanEdit(d, p),
            ShareKind.PlaylistFolder => d.PlaylistFolders.FirstOrDefault(f => f.Id == itemId) is { } pf && pf.AreaId == cur.Id && FolderAccess.CanShare(d.Access, pf),
            _ => false
        };
    }

    /// <summary>Aires avec lesquelles un élément de l'aire ouverte est partagé.</summary>
    public static List<AreaShareRow> RowsOf(ClientDb d, string kind, Guid itemId) =>
        d.AreaShares.Where(s => s.ClientId == d.ClientId && s.Kind == kind && s.ItemId == itemId).OrderBy(s => s.SharedUtc)
            .Select(s => new AreaShareRow(s.ToAreaId, d.Root.Areas.FirstOrDefault(a => a.Id == s.ToAreaId)?.Name ?? "Aire supprimée", s.SharedUtc,
                kind == ShareKind.Playlist ? d.Root.Screens.Count(x => x.ClientId == d.ClientId && x.AreaId == s.ToAreaId && x.ZonePlaylists().OfType<Guid>().Contains(itemId)) : 0)).ToList();

    /// <summary>Fixe les aires avec lesquelles les éléments donnés sont partagés (ajoute les cochées, retire les autres). Rend l'erreur, ou null.</summary>
    public static string? SetTargets(ClientDb d, string kind, IReadOnlyCollection<Guid> itemIds, IReadOnlyCollection<Guid> areaIds, bool onlyAdd)
    {
        if (CurrentArea(d) is not { } cur) return "Aucune aire ouverte.";
        var allowed = d.Access.Areas.Where(a => a.Id != cur.Id).Select(a => a.Id).ToHashSet();
        if (areaIds.Any(a => !allowed.Contains(a))) return "Vous ne pouvez partager qu'avec vos propres aires.";
        foreach (var id in itemIds)
        {
            if (!CanShare(d, kind, id)) return $"Vous ne pouvez pas partager ce {ShareKind.Label(kind)}.";
            foreach (var a in areaIds)
                if (!d.AreaShares.Any(s => s.ClientId == d.ClientId && s.Kind == kind && s.ItemId == id && s.ToAreaId == a))
                    d.AreaShares.Add(new AreaShare { ClientId = d.ClientId, Kind = kind, ItemId = id, FromAreaId = cur.Id, ToAreaId = a, FromUserId = d.Access.UserId });
            if (!onlyAdd)
                foreach (var gone in d.AreaShares.Where(s => s.ClientId == d.ClientId && s.Kind == kind && s.ItemId == id && allowed.Contains(s.ToAreaId) && !areaIds.Contains(s.ToAreaId)).ToList())
                    Revoke(d, gone);
        }
        return null;
    }

    /// <summary>Retire le partage vers une aire (côté émetteur).</summary>
    public static string? Unshare(ClientDb d, string kind, Guid itemId, Guid toArea)
    {
        if (!CanShare(d, kind, itemId)) return $"Vous ne pouvez pas modifier le partage de ce {ShareKind.Label(kind)}.";
        if (d.AreaShares.FirstOrDefault(s => s.ClientId == d.ClientId && s.Kind == kind && s.ItemId == itemId && s.ToAreaId == toArea) is { } s) Revoke(d, s);
        return null;
    }

    /// <summary>L'aire qui reçoit ne veut plus voir l'élément (administrateur, ou droit d'écriture sur le domaine).</summary>
    public static string? Leave(ClientDb d, string kind, Guid itemId)
    {
        if (CurrentArea(d) is not { } cur) return "Aucune aire ouverte.";
        if (!d.Access.IsOrgAdmin && !d.Access.CanWrite(ShareKind.Domain(kind))) return "Votre niveau de droits ne permet pas de retirer ce partage.";
        var s = d.AreaShares.FirstOrDefault(x => x.ClientId == d.ClientId && x.Kind == kind && x.ItemId == itemId && x.ToAreaId == cur.Id);
        if (s == null) return "Cet élément n'est pas partagé avec votre aire.";
        Revoke(d, s);
        return null;
    }

    /// <summary>
    /// Supprime un partage, puis met l'aire destinataire à jour : les listes qui ne sont plus visibles ne sont plus choisies sur ses écrans (ceux-ci gardent
    /// leur contenu publié jusqu'à la prochaine publication) et les médias qui ne sont plus lisibles sortent de ses listes.
    /// </summary>
    public static void Revoke(ClientDb d, AreaShare share)
    {
        var root = d.Root;
        var before = SharedView.Build(root, d.ClientId, share.ToAreaId);
        root.AreaShares.Remove(share);
        var after = SharedView.Build(root, d.ClientId, share.ToAreaId);
        var area = root.Areas.FirstOrDefault(a => a.Id == share.ToAreaId);
        if (area == null) return;
        var rd = new ClientDb(root, d.ClientId, AccessFor(root, d.ClientId, area, before));
        var lostLists = before.Playlists.Except(after.Playlists).ToHashSet();
        if (lostLists.Count > 0) PlaylistOps.Remove(rd, lostLists, detachOnly: true);
        var lostMedia = before.MediaAll.Except(after.MediaAll).ToHashSet();
        if (lostMedia.Count == 0) return;
        foreach (var p in rd.Playlists.Where(p => p.AreaId == area.Id))
            if (p.Draft.RemoveAll(i => lostMedia.Contains(i.MediaId)) > 0) p.Touch();
    }

    /// <summary>Retire tous les partages d'éléments supprimés (ou déplacés vers une autre aire).</summary>
    public static void Forget(ClientDb d, string kind, IReadOnlyCollection<Guid> itemIds)
    {
        foreach (var s in d.AreaShares.Where(s => s.ClientId == d.ClientId && s.Kind == kind && itemIds.Contains(s.ItemId)).ToList()) d.Root.AreaShares.Remove(s);
    }

    private static AreaAccess AccessFor(Db root, Guid clientId, Area area, SharedView shared) => new()
    {
        IsOrgAdmin = true, Areas = new() { area }, Current = area, OrgAreaCount = root.Areas.Count(a => a.ClientId == clientId), Shared = shared
    };
}
