namespace Linkii.Poc;

/// <summary>
/// Médiathèque d'un client : ses images et ses vidéos (les autres contenus sont des apps).
/// Les apps Image, Vidéo et Diaporamas y prennent leurs fichiers (champs « media » / « medias »).
/// </summary>
public class MediaLibrary(TenantStore store, AppCatalog catalog)
{
    public static bool IsFile(MediaItem m) => m.Type is "image" or "video" && m.AppId == null;

    /// <summary>Fichier copié depuis un dossier Drive (Info["drive"]) : affiché et utilisable, mais il suit son dossier (ni renommé ni supprimé ici).</summary>
    public static bool IsDrive(MediaItem m) => m.Info.ContainsKey("drive");

    private static bool IsOwnFile(MediaItem m) => IsFile(m) && !IsDrive(m);

    /// <summary>Dossiers Drive du client, par identifiant (Info["drive"] des fichiers synchronisés).</summary>
    public Dictionary<string, DriveFolder> DriveFolders() => store.Read(d => d.Tenant.DriveFolders.ToDictionary(f => f.Id.ToString()));

    /// <summary>Images et vidéos, les plus récentes d'abord. La taille des fichiers importés avant son enregistrement est lue sur le disque.</summary>
    public List<MediaItem> Files()
    {
        // un fichier rangé dans un dossier n'est listé que si la personne y a un droit (voir FolderAccess) ;
        // à la racine (hors dossier, dont les fichiers Drive), avec les dossiers activés, seuls les administrateurs voient
        var list = store.Read(d =>
        {
            return Normal(d);
        });
        foreach (var m in list.Where(m => m.Size == null && m.FileName != null))
        {
            try { var fi = new FileInfo(Path.Combine(AppPaths.MediaDir, m.FileName!)); if (fi.Exists) m.Size = fi.Length; } catch { }
        }
        foreach (var m in list.Where(m => m.Width == null && m.FileName != null && m.Status == null))
        {
            if (MediaProbe.Size(Path.Combine(AppPaths.MediaDir, m.FileName!)) is { } d) { m.Width = d.W; m.Height = d.H; }
        }
        return list.OrderByDescending(m => m.AddedUtc ?? DateTime.MinValue).ThenBy(m => m.Name).ToList();
    }

    private static List<MediaItem> Normal(ClientDb d)
    {
        var rights = FolderAccess.Snapshot(d.Access, FolderOps.All(d));
        var rootVisible = !d.Tenant.FoldersOn || d.Access.IsOrgAdmin;
        var shared = d.Access.Shared;
        return d.Media.Where(m => IsFile(m) && (shared.MediaAll.Contains(m.Id)
            // média d'une autre aire : partagé directement (à la racine), ou dans un dossier partagé ; ceux que seule une liste partagée utilise ne sont pas listés
            ? shared.Media.Contains(m.Id) || m.FolderId is { } sf && rights.GetValueOrDefault(sf) != FolderRight.None
            : m.FolderId is not { } f ? rootVisible || (m.OwnerId != null && m.OwnerId == d.Access.UserId) : rights.GetValueOrDefault(f) != FolderRight.None)).ToList();
    }

    // ---------- Partage entre aires (lien vivant, lecture seule) ----------

    /// <summary>Médias d'une autre aire partagés directement avec l'aire ouverte (à sa racine) : identifiant → aire d'origine et date.</summary>
    public Dictionary<Guid, MediaSharedWithMe> SharedWithMe() => store.Read(d =>
    {
        var cur = d.Area?.Id;
        return d.AreaShares.Where(s => s.ClientId == d.ClientId && s.Kind == ShareKind.Media && s.ToAreaId == cur && d.Access.Shared.Media.Contains(s.ItemId))
            .GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g =>
            {
                var s = g.OrderBy(x => x.SharedUtc).First();
                return new MediaSharedWithMe(d.Root.Areas.FirstOrDefault(a => a.Id == s.FromAreaId)?.Name ?? "Une autre aire", s.SharedUtc);
            });
    });

    /// <summary>Médias de l'aire ouverte partagés avec d'autres aires (pastille de partage sur la vignette).</summary>
    public HashSet<Guid> SharedOut() => store.Read(d =>
    {
        var mine = d.Media.Where(m => m.AreaId == d.Area?.Id).Select(m => m.Id).ToHashSet();
        return d.AreaShares.Where(s => s.ClientId == d.ClientId && s.Kind == ShareKind.Media && mine.Contains(s.ItemId)).Select(s => s.ItemId).ToHashSet();
    });

    // ---------- Dossiers (option « Dossiers dans la médiathèque », §49) ----------

    /// <summary>Dossiers de l'aire que la personne voit, avec son droit sur chacun. Un dossier privé d'un autre n'y figure pas.</summary>
    public List<FolderRow> Folders() => store.Read(d =>
    {
        var all = FolderOps.All(d);
        var rows = new List<FolderRow>();
        foreach (var f in d.MediaFolders)
        {
            var right = FolderAccess.For(d.Access, f, all);
            if (right == FolderRight.None) continue;
            var owner = d.Users.FirstOrDefault(u => u.Id == f.OwnerId)?.Label ?? "—";
            rows.Add(new FolderRow(f, right, owner, f.Shares.Count > 0));
        }
        return rows.OrderBy(r => r.Folder.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    public (string? Error, MediaFolder? Folder) CreateFolder(string name, Guid? parentId) => store.Write(d => { return FolderOps.Create(d, name, parentId); });

    public string? RenameFolder(Guid id, string name) => store.Write(d => { return FolderOps.Rename(d, id, name); });

    /// <summary>Fichiers qui servent dans une app ou une liste de lecture, parmi ceux d'un dossier (à confirmer avant suppression).</summary>
    public List<string> UsedInFolder(Guid folderId) => Files().Where(m => m.FolderId == folderId).SelectMany(m => UsedBy(m.Id)).Distinct().ToList();

    /// <summary>Supprime un dossier, ses sous-dossiers et leurs fichiers (même retrait que pour un fichier : apps et listes mises à jour).</summary>
    public string? DeleteFolder(Guid id)
    {
        var plan = store.Write(d => { var err = FolderOps.PlanDelete(d, id, out var folders, out var files); return (err, folders, files); });
        if (plan.err != null) return plan.err;
        Delete(plan.files);
        store.Write(d => { FolderOps.Forget(d, plan.folders); });
        return null;
    }

    /// <summary>Partages d'un dossier, et les personnes et niveaux à qui l'on peut en donner (les personnes qui ne le peuvent pas ont une raison).</summary>
    public FolderShareInfo? ShareInfo(Guid folderId) => store.Read(d =>
    {
        if (FolderOps.Find(d, folderId) is not { } f) return null;
        var area = d.Root.Areas.FirstOrDefault(a => a.Id == f.AreaId)?.Name ?? "";
        var people = d.Users.Where(u => u.Role == Roles.ClientMember).OrderBy(u => u.Label).Select(u =>
        {
            var ua = AreaAccess.For(d.Root, d.ClientId, u.Id);
            var reason = u.Disabled ? "compte désactivé"
                : !ua.HasArea(f.AreaId) ? $"ne voit pas l'aire « {area} »"
                : ua.Level?.Pages.Contains(LevelPages.Media) != true ? "n'a pas la page Médiathèque" : null;
            return new ShareCandidate(u.Id, u.Label, ua.Level?.Name ?? "Sans niveau", reason);
        }).ToList();
        var levels = d.Tenant.Levels.Select(l => new ShareCandidate(l.Id, l.Name,
            d.Users.Count(u => u.LevelId == l.Id) is var n && n > 0 ? $"{n} membre{(n > 1 ? "s" : "")}" : "aucun membre", null)).ToList();
        var shares = f.Shares.Select(s =>
        {
            var label = s.Kind == FolderShareKind.Level ? levels.FirstOrDefault(l => l.Id == s.TargetId)?.Label : people.FirstOrDefault(p => p.Id == s.TargetId)?.Label;
            return new ShareRow(s.Kind, s.TargetId, label ?? "Retiré de l'organisation", s.Write);
        }).ToList();
        return new FolderShareInfo(f, shares, people, levels, FolderAccess.CanShare(d.Access, f), Audience(d, f));
    });

    /// <summary>Personnes qui ont accès à chaque dossier vu (nombre de l'icône Partage).</summary>
    public Dictionary<Guid, FolderAudience> Audiences() => store.Read(d => FolderAudiences.Compute(d, FolderOps.All(d), d.MediaFolders.ToList(), LevelPages.Media));

    private static FolderAudience Audience(ClientDb d, MediaFolder f) =>
        FolderAudiences.Compute(d, FolderOps.All(d), new[] { f }, LevelPages.Media).GetValueOrDefault(f.Id, FolderAudience.None);

    public string? SetShare(Guid folderId, string kind, Guid target, bool? write) => store.Write(d => { return FolderOps.SetShare(d, folderId, kind, target, write); });

    public string? TransferOwner(Guid folderId, Guid userId) => store.Write(d => { return FolderOps.TransferOwner(d, folderId, userId); });

    /// <summary>Range des fichiers dans un dossier, ou à la racine (null). Rend le nombre rangé et les refus.</summary>
    public (int Moved, List<string> Refused) MoveFiles(IReadOnlyCollection<Guid> ids, Guid? folderId) => store.Write(d => { return FolderOps.Move(d, ids, folderId); });

    /// <summary>Où ranger : la racine (si la personne peut y ranger) et les dossiers où elle écrit, avec leur chemin.</summary>
    public List<(Guid? Id, string Path)> MoveTargets() => store.Read(d =>
    {
        var all = FolderOps.All(d);
        var list = new List<(Guid? Id, string Path)>();
        if (FolderAccess.CanCreateAtRoot(d.Access, LevelPages.Media)) list.Add((null, "Médiathèque de l'aire (racine)"));
        foreach (var f in d.MediaFolders)
        {
            if (FolderAccess.For(d.Access, f, all) < FolderRight.Write) continue;
            var path = string.Join(" › ", FolderAccess.Chain(f, all).Reverse().Select(x => x.Name));
            list.Add((f.Id, path));
        }
        return list.OrderBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    /// <summary>Contenus d'apps (et playlists, pour les anciens éléments directs) qui utilisent ce fichier.</summary>
    /// <summary>Le fichier existe dans l'organisation, même s'il est rangé dans un dossier que la personne ne voit pas.</summary>
    public bool Exists(Guid id) => store.Read(d => d.Root.Media.Any(m => m.Id == id && m.ClientId == d.ClientId));

    public List<string> UsedBy(Guid mediaId) => store.Read(d =>
    {
        var names = d.Media.Where(m => m.AppId != null && MediaFields(m).Any(k => AppField.MediaIds(m.Settings.GetValueOrDefault(k)).Contains(mediaId)))
            .Select(m => m.Name).ToList();
        names.AddRange(d.Playlists.Where(p => p.Draft.Any(i => i.MediaId == mediaId)).Select(p => "liste de lecture « " + p.Name + " »"));
        return names.Distinct().ToList();
    });

    public void Rename(Guid id, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        store.Write(d => { if (d.Media.FirstOrDefault(m => m.Id == id && IsOwnFile(m)) is { } m && FolderOps.CanEditFile(d, m)) m.Name = name.Length > 120 ? name[..120] : name; });
    }

    /// <summary>
    /// Supprime des fichiers : ils sont retirés des apps qui les utilisaient et des listes de lecture (les écrans concernés ont une mise à jour à publier ;
    /// ils gardent ce qu'ils affichent jusque-là). Les fichiers sur le disque sont effacés.
    /// </summary>
    public int Delete(IReadOnlyCollection<Guid> ids)
    {
        var files = store.Write(d =>
        {
            var gone = d.Media.Where(m => ids.Contains(m.Id) && IsOwnFile(m) && !d.Access.Shared.MediaAll.Contains(m.Id) && FolderOps.CanEditFile(d, m)).ToList();   // un média partagé par une autre aire ne se supprime que dans son aire   // les fichiers Drive suivent leur dossier ; ceux d'un dossier qu'on ne modifie pas restent
            var set = gone.Select(m => m.Id).ToHashSet();
            if (set.Count == 0) return new List<string>();
            DetachFromContent(d, set);
            AreaShareOps.Forget(d, ShareKind.Media, set);
            d.Media.RemoveAll(m => set.Contains(m.Id));
            return gone.Where(m => m.FileName != null).Select(m => m.FileName!).ToList();
        });
        foreach (var f in files)
        {
            try { File.Delete(Path.Combine(AppPaths.MediaDir, f)); } catch { }   // déjà absent : sans conséquence
            Thumbnails.Delete(f);
        }
        return files.Count;
    }

    /// <summary>Fichiers exportés depuis Canva (Info["canva"]) que plus rien n'utilise.</summary>
    public List<MediaItem> UnusedCanva() => store.Read(d => UnusedCanva(d.Media, d.Playlists, d.Screens));

    /// <summary>
    /// Exports Canva sans emploi : ni dans un contenu, ni dans une liste de lecture, ni dans ce que les écrans ou les listes ont publié
    /// (un fichier encore diffusé n'est jamais proposé). Les exports en cours de conversion sont ignorés.
    /// </summary>
    public static List<MediaItem> UnusedCanva(IEnumerable<MediaItem> media, IEnumerable<Playlist> playlists, IEnumerable<Screen> screens) =>
        UnusedExports(media, playlists, screens, "canva");

    /// <summary>Fichiers de l'aire que la personne ne voit pas (rangés dans un dossier qui ne lui est pas accordé) : masqués dans les listes (§54).</summary>
    public HashSet<Guid> HiddenFileIds()
    {
        var visible = Files().Select(m => m.Id).ToHashSet();
        return store.Read(d => d.Media.Where(m => IsFile(m) && !visible.Contains(m.Id)).Select(m => m.Id).ToHashSet());
    }

    /// <summary>Le contenu montre un fichier masqué : le fichier lui-même, ou un contenu (app) qui en a choisi un dans ses réglages.</summary>
    public static bool UsesHidden(MediaItem? m, IReadOnlySet<Guid> hidden) =>
        m != null && hidden.Count > 0 && (IsFile(m) ? hidden.Contains(m.Id) : m.Settings.Values.SelectMany(AppField.MediaIds).Any(hidden.Contains));

    /// <summary>Tous les fichiers sans emploi (même règle que les exports Canva) : filtre « Non utilisés » et mention sur la vignette.</summary>
    public HashSet<Guid> UnusedIds() => store.Read(d => UnusedExports(d.Media, d.Playlists, d.Screens, null).Select(m => m.Id).ToHashSet());

    /// <summary>Où sert un fichier : contenus (apps) qui l'ont choisi, listes de lecture qui le diffusent (directement ou par un contenu) et leurs écrans.</summary>
    public MediaUses Uses(Guid mediaId) => store.Read(d =>
    {
        var contents = d.Media.Where(m => m.AppId != null && MediaFields(m).Any(k => AppField.MediaIds(m.Settings.GetValueOrDefault(k)).Contains(mediaId))).ToList();
        var ids = contents.Select(c => c.Id).Append(mediaId).ToHashSet();
        var screens = d.Screens.Where(s => s.Token != null).ToList();
        var lists = d.Playlists.Where(p => p.Draft.Any(i => ids.Contains(i.MediaId)))
            .Select(p => new MediaUse(p.Id, p.Name, screens.Count(s => s.ZonePlaylists().Contains(p.Id))))
            .OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var used = contents.Select(c => new MediaUse(c.Id, c.Name, d.Playlists.Count(p => p.Draft.Any(i => i.MediaId == c.Id))))
            .OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new MediaUses(used, lists);
    });

    /// <summary>Même recherche pour les fichiers produits par une app (Info[key] : « canva », « pdf ») ; key null : tous les fichiers.</summary>
    public static List<MediaItem> UnusedExports(IEnumerable<MediaItem> media, IEnumerable<Playlist> playlists, IEnumerable<Screen> screens, string? key)
    {
        var mediaList = media.ToList();
        var candidates = mediaList.Where(m => IsFile(m) && (key == null || m.Info.ContainsKey(key)) && m.Status != "processing").ToList();
        if (candidates.Count == 0) return candidates;

        var used = new HashSet<Guid>();
        foreach (var inst in mediaList.Where(m => m.AppId != null)) used.UnionWith(inst.Settings.Values.SelectMany(AppField.MediaIds));
        var lists = playlists.ToList();
        foreach (var p in lists) used.UnionWith(p.Draft.Select(i => i.MediaId));

        var published = lists.SelectMany(p => p.Published)
            .Concat(screens.SelectMany(s => s.Published.Concat(s.PublishedZones.SelectMany(z => z)).Concat(s.PublishedLists.Values.SelectMany(l => l))))
            .ToList();
        foreach (var i in published)
        {
            if (i.MediaId is { } id) used.Add(id);
            used.UnionWith(i.Settings.Values.SelectMany(AppField.MediaIds));
        }
        var urls = published.SelectMany(i => (i.Urls ?? new()).Append(i.Url ?? "")).Where(u => u.Length > 0).ToList();

        return candidates.Where(m => !used.Contains(m.Id) && (m.FileName == null || !urls.Any(u => u.Contains(m.FileName)))).ToList();
    }

    /// <summary>Retire de la médiathèque les exports Canva sans emploi (voir <see cref="UnusedCanva()"/>), fichiers sur le disque compris. Rend leur nombre.</summary>
    public int DeleteUnusedCanva() => Delete(UnusedCanva().Select(m => m.Id).ToList());

    /// <summary>Retire des contenus (apps) et des listes de lecture de l'aire ouverte les médias donnés.</summary>
    private void DetachFromContent(ClientDb d, HashSet<Guid> set)
    {
        var changedInstances = new HashSet<Guid>();
        foreach (var inst in d.Media.Where(m => m.AppId != null))
            foreach (var key in MediaFields(inst))
            {
                var cur = AppField.MediaIds(inst.Settings.GetValueOrDefault(key));
                if (!cur.Any(set.Contains)) continue;
                inst.Settings[key] = string.Join(",", cur.Where(x => !set.Contains(x)));
                changedInstances.Add(inst.Id);
            }
        foreach (var p in d.Playlists)
        {
            var removed = p.Draft.RemoveAll(i => set.Contains(i.MediaId)) > 0;
            if (removed || p.Draft.Any(i => changedInstances.Contains(i.MediaId))) p.Touch();
        }
    }

    private IEnumerable<string> MediaFields(MediaItem instance) =>
        catalog.Find(instance.AppId)?.Settings.Where(f => f.IsMedia).Select(f => f.Key) ?? Enumerable.Empty<string>();
}

/// <summary>Un contenu ou une liste qui utilise un fichier ; Count : nombre de listes (contenu) ou d'écrans (liste).</summary>
public record MediaUse(Guid Id, string Name, int Count);

/// <summary>Utilisations d'un fichier (panneau d'aperçu de la médiathèque).</summary>
public record MediaUses(List<MediaUse> Contents, List<MediaUse> Lists)
{
    public bool Any => Contents.Count > 0 || Lists.Count > 0;
    public int Screens => Lists.Sum(l => l.Count);
}

/// <summary>Un dossier vu par la personne, avec son droit, le nom de son propriétaire et s'il est partagé.</summary>
public record FolderRow(MediaFolder Folder, FolderRight Right, string OwnerName, bool Shared);

/// <summary>Une personne ou un niveau à qui partager : <paramref name="Reason"/> dit pourquoi ce n'est pas possible (null : possible).</summary>
public record ShareCandidate(Guid Id, string Label, string Detail, string? Reason);

/// <summary>Un partage en place sur un dossier.</summary>
public record ShareRow(string Kind, Guid TargetId, string Label, bool Write);

/// <summary>Partages d'un dossier : ceux en place, les personnes et niveaux possibles, et si la personne peut partager.</summary>
public record FolderShareInfo(MediaFolder Folder, List<ShareRow> Shares, List<ShareCandidate> People, List<ShareCandidate> Levels, bool CanShare, FolderAudience Audience);

/// <summary>Un média partagé avec l'aire ouverte : de quelle aire, et quand.</summary>
public record MediaSharedWithMe(string By, DateTime Utc);

