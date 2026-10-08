using Linkii.Poc.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Linkii.Poc;

/// <summary>Publication d'une playlist + notification SignalR des écrans concernés.</summary>
public class Notifier(IHubContext<ScreenHub> hub, JsonStore store, AppCatalog catalog)
{
    /// <summary>Ancien instantané au niveau de la playlist (lu par les écrans jamais publiés depuis). L'interface publie écran par écran : <see cref="PublishScreen"/>.</summary>
    public async Task Publish(Guid playlistId)
    {
        store.Write(db =>
        {
            var p = db.Playlists.First(x => x.Id == playlistId);
            p.Published = Items(db, p);
            p.PublishedVersion++;
            p.DraftChanged = false;
        });
        await NotifyWhere(s => s.PlaylistId == playlistId);
    }

    /// <summary>
    /// Publie sur un seul écran : instantané de la playlist de chaque zone et de ses widgets. Les autres écrans des mêmes playlists gardent leur version.
    /// </summary>
    public async Task PublishScreen(Guid screenId)
    {
        var ok = store.Write(db =>
        {
            var s = db.Screens.FirstOrDefault(x => x.Id == screenId);
            if (s == null) return false;
            var own = db.Playlists.Where(x => x.ClientId == s.ClientId).ToList();
            List<PublishedItem> Zone(Guid? id) => own.FirstOrDefault(x => x.Id == id) is { } p ? Items(db, p) : new();
            s.Published = Zone(s.PlaylistId).Concat(s.Widgets.Select(Widget).OfType<PublishedItem>()).ToList();
            s.PublishedLayout = ScreenLayouts.Find(s.Layout).Id;
            s.PublishedZones = s.ZonePlaylists().Skip(1).Select(Zone).ToList();   // zones 2 et 3 du découpage
            s.PublishedVersion++;
            s.PublishedStamp = Helpers.PublishStamp(s, own);
            s.PublishedUtc = DateTime.UtcNow;
            return true;
        });
        if (ok) await NotifyScreen(screenId);
    }

    private List<PublishedItem> Items(Db db, Playlist p) => p.Draft
        .Select(i => (i, m: db.Media.FirstOrDefault(x => x.Id == i.MediaId && x.ClientId == p.ClientId)))   // jamais un média d'un autre client
        .Where(t => t.m != null && t.m.Status == null)   // une vidéo en cours de conversion n'est pas publiée
        .Select(t => Snapshot(db, t.i, t.m!))
        .OfType<PublishedItem>()
        .ToList();

    /// <summary>Widget d'écran : app en surimpression permanente, à la position libre choisie dans l'éditeur de l'écran.</summary>
    private PublishedItem? Widget(ScreenWidget w)
    {
        var app = catalog.Find(w.AppId);
        if (app == null) return null;
        return new PublishedItem
        {
            Id = w.Id, Type = "app", AppId = app.Id, AppVersion = app.Version,
            Placement = "free", X = Math.Clamp(w.X, 0, 100), Y = Math.Clamp(w.Y, 0, 100), Scale = Math.Clamp(w.Scale, 0.4, 3),
            Settings = new(w.Settings), DataId = app.Data ? w.Id.ToString() : null,
            // texte libre : période de validité (l'écran l'affiche puis le retire tout seul)
            ValidFrom = app.Validity && w.Settings.GetValueOrDefault("validFrom") is { Length: > 0 } f ? f : null,
            ValidTo = app.Validity && w.Settings.GetValueOrDefault("validTo") is { Length: > 0 } t ? t : null
        };
    }

    /// <summary>
    /// Instantané d'un élément de playlist à la publication. Pour une app : paramètres (publics et secrets) figés tels qu'ils sont à cet instant ;
    /// les modifications faites ensuite sur l'instance n'atteignent les écrans qu'à la publication suivante.
    /// </summary>
    private PublishedItem? Snapshot(Db db, PlaylistItem i, MediaItem m)
    {
        if (m.Type == "app" && string.IsNullOrEmpty(m.AppId)) return null;   // reste d'un ancien widget : jamais publié
        if (string.IsNullOrEmpty(m.AppId))
            return new PublishedItem
            {
                Id = i.Id, Type = m.Type, Url = m.FileName == null ? null : "/media/" + m.FileName, MediaId = m.Id,
                Placement = "full", DurationSec = i.DurationSec
            };

        var app = catalog.Find(m.AppId);
        if (app == null) return null;   // app retirée du catalogue : l'élément n'est plus publié
        var pi = new PublishedItem
        {
            Id = i.Id, Type = "app", AppId = app.Id, AppVersion = app.Version, MediaId = m.Id,
            Placement = app.Placements.Contains(i.Placement) ? i.Placement : app.Placements[0],   // une position non prévue par l'app est corrigée
            DurationSec = i.DurationSec,
            DurationMode = app.Duration,
            Settings = new(m.Settings), Secrets = new(m.Secrets),
            DataId = app.Data ? i.Id.ToString() : null
        };
        if (app.Duration == "content" && i.DurationMode == "fixed")   // durée choisie pour cet élément de playlist : la vidéo est coupée ou rejouée à ce terme
            pi.DurationMode = "fixed";
        else if (app.Duration == "content")   // l'app décide de la durée (fin de la vidéo) ; la durée maximale limite la boucle
            pi.DurationSec = (int.TryParse(m.Settings.GetValueOrDefault("maxMinutes"), out var mm) ? Math.Clamp(mm, 1, 240) : 30) * 60;
        // Apps de la médiathèque : les fichiers choisis deviennent des adresses /media/… (fichiers prêts, du même client, dans l'ordre choisi).
        if (app.Settings.FirstOrDefault(f => f.IsMedia && AppCatalog.IsVisible(f, m.Settings)) is { } mf)   // Canva : pages ou vidéo selon l'export
        {
            pi.Urls = AppField.MediaIds(m.Settings.GetValueOrDefault(mf.Key))
                .Select(id => db.Media.FirstOrDefault(x => x.Id == id && x.ClientId == m.ClientId && x.Status == null && x.Type == mf.Accept && x.FileName != null))
                .OfType<MediaItem>()
                .Select(x => "/media/" + x.FileName)
                .ToList();
            if (pi.Urls.Count == 0) return null;   // plus aucun fichier (supprimé de la médiathèque) : l'élément n'est pas publié
        }
        if (app.Id == "drive")   // dossier synchronisé : liste des fichiers du moment, tenue à jour ensuite par RefreshDrive
            pi.Urls = DriveSources.Urls(db.Media, m.ClientId, m.Settings);
        if (app.Validity)
        {
            pi.ValidFrom = m.Settings.GetValueOrDefault("validFrom") is { Length: > 0 } f ? f : null;
            pi.ValidTo = m.Settings.GetValueOrDefault("validTo") is { Length: > 0 } t ? t : null;
        }
        return pi;
    }

    public Task NotifyScreen(Guid screenId) => NotifyWhere(s => s.Id == screenId);

    /// <summary>
    /// Dossier Drive synchronisé : les éléments déjà publiés qui l'affichent reçoivent la nouvelle liste de fichiers, sans republier.
    /// Seuls les écrans dont la liste a changé sont prévenus (ils préchargent les nouveaux fichiers pour la lecture hors ligne).
    /// </summary>
    public async Task RefreshDrive(Guid clientId, Guid folderId)
    {
        var key = folderId.ToString();
        var screens = store.Write(db =>
        {
            bool Update(IEnumerable<PublishedItem> items)
            {
                var n = 0;
                foreach (var pi in items.Where(x => x.AppId == "drive" && x.Settings.GetValueOrDefault("folder") == key))
                {
                    var urls = DriveSources.Urls(db.Media, clientId, pi.Settings);
                    if (pi.Urls != null && urls.SequenceEqual(pi.Urls)) continue;
                    pi.Urls = urls;
                    n++;
                }
                return n > 0;
            }
            var ids = new HashSet<Guid>();
            foreach (var s in db.Screens.Where(s => s.ClientId == clientId))
                if (Update(s.Published.Concat(s.PublishedZones.SelectMany(z => z)))) { s.PublishedVersion++; ids.Add(s.Id); }
            foreach (var p in db.Playlists.Where(p => p.ClientId == clientId))   // ancien instantané de liste (écrans jamais publiés depuis)
                if (Update(p.Published))
                {
                    p.PublishedVersion++;
                    foreach (var s in db.Screens.Where(s => s.PlaylistId == p.Id && s.PublishedVersion == 0)) ids.Add(s.Id);
                }
            return ids;
        });
        foreach (var id in screens) await NotifyScreen(id);
    }

    /// <summary>Paramètres du client modifiés : tous ses écrans rechargent leur configuration.</summary>
    public Task NotifyClient(Guid clientId) => NotifyWhere(s => s.ClientId == clientId);

    /// <summary>Marque du revendeur modifiée : les écrans de tous ses clients se mettent à jour.</summary>
    public Task NotifyReseller(Guid resellerId)
    {
        var clients = store.Read(db => db.Clients.Where(c => c.ResellerId == resellerId).Select(c => c.Id).ToHashSet());
        return NotifyWhere(s => clients.Contains(s.ClientId));
    }

    private async Task NotifyWhere(Func<Screen, bool> pred)
    {
        var targets = store.Read(db => db.Screens
            .Where(s => s.Token != null && pred(s))
            .Where(s => db.Clients.Any(c => c.Id == s.ClientId))
            .Select(s => (s.Id, Rev: Revision(db, s)))
            .ToList());
        foreach (var (id, rev) in targets)
            await hub.Clients.Group(ScreenHub.Group(id)).SendAsync("PlaylistChanged", rev);
    }

    public static string Revision(Db db, Screen s)
    {
        var t = db.Clients.First(c => c.Id == s.ClientId);
        return Helpers.Revision(db.Playlists.FirstOrDefault(p => p.Id == s.PlaylistId && p.ClientId == s.ClientId), s, t, db.Resellers.FirstOrDefault(r => r.Id == t.ResellerId));
    }
}
