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
        }
        return files.Count;
    }

    private IEnumerable<string> MediaFields(MediaItem instance) =>
        catalog.Find(instance.AppId)?.Settings.Where(f => f.IsMedia).Select(f => f.Key) ?? Enumerable.Empty<string>();
}
