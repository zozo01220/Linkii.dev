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
        var list = store.Read(d => d.Media.Where(IsFile).ToList());
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

    /// <summary>Contenus d'apps (et playlists, pour les anciens éléments directs) qui utilisent ce fichier.</summary>
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
        store.Write(d => { if (d.Media.FirstOrDefault(m => m.Id == id && IsOwnFile(m)) is { } m) m.Name = name.Length > 120 ? name[..120] : name; });
    }

    /// <summary>
    /// Supprime des fichiers : ils sont retirés des apps qui les utilisaient et des listes de lecture (les écrans concernés ont une mise à jour à publier ;
    /// ils gardent ce qu'ils affichent jusque-là). Les fichiers sur le disque sont effacés.
    /// </summary>
    public int Delete(IReadOnlyCollection<Guid> ids)
    {
        var files = store.Write(d =>
        {
            var gone = d.Media.Where(m => ids.Contains(m.Id) && IsOwnFile(m)).ToList();   // les fichiers Drive suivent leur dossier
            var set = gone.Select(m => m.Id).ToHashSet();
            if (set.Count == 0) return new List<string>();
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

    /// <summary>Même recherche pour les fichiers produits par une app (Info[key] : « canva », « pdf »).</summary>
    public static List<MediaItem> UnusedExports(IEnumerable<MediaItem> media, IEnumerable<Playlist> playlists, IEnumerable<Screen> screens, string key)
    {
        var mediaList = media.ToList();
        var candidates = mediaList.Where(m => IsFile(m) && m.Info.ContainsKey(key) && m.Status != "processing").ToList();
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

    private IEnumerable<string> MediaFields(MediaItem instance) =>
        catalog.Find(instance.AppId)?.Settings.Where(f => f.IsMedia).Select(f => f.Key) ?? Enumerable.Empty<string>();
}
