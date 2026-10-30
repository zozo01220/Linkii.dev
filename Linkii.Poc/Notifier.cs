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
            var wall = Helpers.Wall(db.Areas, s) != null;   // écran d'un mur : jamais de widgets
            s.Published = Zone(s.PlaylistId).Concat(wall ? Enumerable.Empty<PublishedItem>() : s.Widgets.Select(Widget).OfType<PublishedItem>()).ToList();
            s.PublishedLayout = ScreenLayouts.Find(s.Layout).Id;
            s.PublishedZones = s.ZonePlaylists().Skip(1).Select(Zone).ToList();   // zones 2 et 3 du découpage
            SnapshotLists(db, s);   // listes du planning : leur contenu du moment
            s.PublishedKiosk = s.Kiosk == null ? null : BuildKiosk(db, s);   // borne interactive : menu de tuiles
            s.PublishedVersion++;
            s.PublishedStamp = Helpers.PublishStamp(s, own);
            s.PublishedUtc = DateTime.UtcNow;
            return true;
        });
        if (ok) await NotifyScreen(screenId);
    }

    /// <summary>
    /// Planning modifié : les écrans touchés prennent un instantané des listes de leurs créneaux et les reçoivent aussitôt.
    /// Leur liste par défaut n'est pas republiée (seule la publication de l'écran la fait) ; ni les modifications en attente.
    /// </summary>
    public async Task PublishPlanning(IEnumerable<Guid> screenIds)
    {
        var ids = screenIds.ToHashSet();
        if (ids.Count == 0) return;
        store.Write(db => { foreach (var s in db.Screens.Where(x => ids.Contains(x.Id))) SnapshotLists(db, s); });
        await NotifyWhere(s => ids.Contains(s.Id));
    }

    /// <summary>
    /// Menu de la borne figé : chaque tuile devient le contenu tel que le player sait l'afficher (même instantané qu'un élément de liste de lecture).
    /// Une tuile dont le contenu a disparu, ou n'est pas prêt, n'est pas publiée : la borne n'affiche jamais de tuile cassée.
    /// </summary>
    private PublishedKiosk BuildKiosk(Db db, Screen s)
    {
        var k = s.Kiosk!;
        var growth = db.Clients.FirstOrDefault(c => c.Id == s.ClientId)?.IsGrowth == true;
        string? Url(Guid? id) => id == null ? null : db.Media.FirstOrDefault(m => m.Id == id && m.ClientId == s.ClientId && m.Type == "image" && m.Status == null && m.FileName != null) is { } m ? "/media/" + m.FileName : null;
        var tiles = new List<PublishedKioskTile>();
        foreach (var t in k.Tiles)
        {
            var m = db.Media.FirstOrDefault(x => x.Id == t.MediaId && x.ClientId == s.ClientId);
            if (m == null || !Kiosk.CanOpen(m)) continue;
            t.Item.MediaId = t.MediaId;
            t.Item.Placement = "full";
            if (Snapshot(db, t.Item, m, growth) is not { } pi) continue;
            pi.Placement = "full";
            tiles.Add(new PublishedKioskTile
            {
                Id = t.Id, Title = t.Title, Subtitle = t.Subtitle, TextScale = Kiosk.CleanScale(t.TextScale), Color = Kiosk.CleanColor(t.Color), ImageUrl = Url(t.ImageMediaId),
                ShowFrom = t.ShowFrom, ShowTo = t.ShowTo, Item = pi
            });
        }
        var cfg = System.Text.Json.JsonSerializer.Deserialize<KioskConfig>(System.Text.Json.JsonSerializer.Serialize(k))!;   // copie : modifier le brouillon ne change pas ce qui est publié
        cfg.Tiles = new();
        return new PublishedKiosk { Config = cfg, LogoUrl = Url(k.LogoMediaId), Tiles = tiles };
    }

    /// <summary>Instantané des listes que le planning de l'écran peut faire jouer (une liste supprimée n'y figure plus).</summary>
    private void SnapshotLists(Db db, Screen s)
    {
        var area = db.Areas.FirstOrDefault(a => a.Id == s.AreaId);
        var own = db.Playlists.Where(x => x.ClientId == s.ClientId).ToList();
        s.PublishedLists = Planning.Effective(area, s).Select(x => x.PlaylistId).Distinct()
            .Select(id => own.FirstOrDefault(p => p.Id == id)).OfType<Playlist>()
            .ToDictionary(p => p.Id, p => Items(db, p));
        s.PublishedListRevs = s.PublishedLists.Keys.ToDictionary(id => id, id => own.First(p => p.Id == id).Revision);
        s.PlanningRevision++;
    }

    /// <summary>Ce qu'une publication figerait de la playlist (aussi joué tel quel par le simulateur « Essayer »).</summary>
    public List<PublishedItem> Items(Db db, Playlist p)
    {
        var growth = db.Clients.FirstOrDefault(c => c.Id == p.ClientId)?.IsGrowth == true;   // QR code de comptage des scans : licence Pro
        return p.Draft
            .Where(i => !i.Off)   // désactivé dans la liste : gardé dans le brouillon, jamais publié
            .Select(i => (i, m: db.Media.FirstOrDefault(x => x.Id == i.MediaId && x.ClientId == p.ClientId)))   // jamais un média d'un autre client
            .Where(t => t.m != null && t.m.Status == null)   // une vidéo en cours de conversion n'est pas publiée
            .Select(t => Snapshot(db, t.i, t.m!, growth) is { } s ? Windowed(s, t.i) : null)
            .OfType<PublishedItem>()
            .ToList();
    }

    /// <summary>Période de diffusion du contenu (onglet Programmation) : le player n'écarte de la boucle que ce qui est hors période.</summary>
    private static PublishedItem Windowed(PublishedItem s, PlaylistItem i)
    {
        s.StartsAt = i.StartsAt;
        s.EndsAt = i.EndsAt;
        return s;
    }

    /// <summary>Widget d'écran : app en surimpression permanente, à la position libre choisie dans l'éditeur de l'écran.</summary>
    private PublishedItem? Widget(ScreenWidget w)
    {
        var app = catalog.Find(w.AppId);
        if (app == null) return null;
        var settings = new Dictionary<string, string>(w.Settings);
        if (app.Id == "qrcode") settings["qr"] = QrCodes.Encode(w.Settings.GetValueOrDefault("content")) ?? "";   // tracé calculé ici : l'écran ne dessine que le résultat
        return new PublishedItem
        {
            Id = w.Id, Type = "app", AppId = app.Id, AppVersion = app.Version,
            Placement = "free", X = Math.Clamp(w.X, 0, 100), Y = Math.Clamp(w.Y, 0, 100), Scale = Math.Clamp(w.Scale, 0.4, 3),
            Settings = settings, DataId = app.Data ? w.Id.ToString() : null,
            // texte libre : période de validité (l'écran l'affiche puis le retire tout seul)
            ValidFrom = app.Validity && w.Settings.GetValueOrDefault("validFrom") is { Length: > 0 } f ? f : null,
            ValidTo = app.Validity && w.Settings.GetValueOrDefault("validTo") is { Length: > 0 } t ? t : null
        };
    }

    /// <summary>
    /// Instantané d'un élément de playlist à la publication. Pour une app : paramètres (publics et secrets) figés tels qu'ils sont à cet instant ;
    /// les modifications faites ensuite sur l'instance n'atteignent les écrans qu'à la publication suivante.
    /// </summary>
    private PublishedItem? Snapshot(Db db, PlaylistItem i, MediaItem m, bool growth)
    {
        if (m.Type == "app" && string.IsNullOrEmpty(m.AppId)) return null;   // reste d'un ancien widget : jamais publié
        if (string.IsNullOrEmpty(m.AppId))
            return new PublishedItem
            {
                Id = i.Id, Type = m.Type, Url = m.FileName == null ? null : "/media/" + m.FileName, MediaId = m.Id,
                Placement = "full", DurationSec = i.DurationSec, Qr = QrTracking.Overlay(i, growth, "full")
            };

        var app = catalog.Find(m.AppId);
        if (app == null) return null;   // app retirée du catalogue : l'élément n'est plus publié
        if (AppCatalog.MissingRequired(app, m.Settings, m.Secrets.Keys.ToHashSet()).Count > 0) return null;   // ajouté à vide, pas encore configuré : jamais publié
        var pi = new PublishedItem
        {
            Id = i.Id, Type = "app", AppId = app.Id, AppVersion = app.Version, MediaId = m.Id,
            Placement = app.Placements.Contains(i.Placement) ? i.Placement : app.Placements[0],   // une position non prévue par l'app est corrigée
            DurationSec = i.DurationSec,
            DurationMode = app.Duration,
            Settings = new(m.Settings), Secrets = new(m.Secrets),
            DataId = app.Data ? i.Id.ToString() : null
        };
        pi.Qr = QrTracking.Overlay(i, growth, pi.Placement);
        if (app.Duration == "content" && i.DurationMode == "fixed")   // durée choisie pour cet élément de playlist : la vidéo est coupée ou rejouée à ce terme
            pi.DurationMode = "fixed";
        else if (app.Duration == "content")   // l'app décide de la durée (fin de la vidéo) ; la durée maximale limite la boucle
            pi.DurationSec = (int.TryParse(m.Settings.GetValueOrDefault("maxMinutes"), out var mm) ? Math.Clamp(mm, 1, 240) : 30) * 60;
        if (app.Id == Compositions.AppId)   // composition : images (fichiers prêts du même client), QR codes et sources de données (contenus d'intégration du même client) résolus maintenant
        {
            if (Compositions.Publish(m.Settings, id => db.Media.FirstOrDefault(x => x.Id == id && x.ClientId == m.ClientId && x.Status == null && (x.Type == "image" || x.Type == "video") && x.FileName != null),
                id => db.Media.FirstOrDefault(x => x.Id == id && x.ClientId == m.ClientId && x.Type == "app"),
                id => db.Clients.FirstOrDefault(x => x.Id == m.ClientId)?.DataSets.FirstOrDefault(x => x.Id == id)) is not { } c) return null;
            (pi.Settings, pi.Urls, pi.Secrets) = c;
            return pi;
        }
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

    /// <summary>Identifier les écrans : chacun affiche son numéro en très grand pendant 5 s (assemblage d'un mur).</summary>
    public async Task Identify(IReadOnlyList<Guid> screenIds)
    {
        for (var i = 0; i < screenIds.Count; i++)
            await hub.Clients.Group(ScreenHub.Group(screenIds[i])).SendAsync("Identify", (i + 1).ToString());
    }

    /// <summary>Synchro d'une zone activée ou coupée : ses écrans rechargent leur configuration (origine commune de la boucle).</summary>
    public Task NotifyZone(Guid zoneId) => NotifyWhere(s => s.ZoneId == zoneId);

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

    /// <summary>
    /// Contenu Google Slides ou Google Docs mis à jour (nouvelles images) : les éléments déjà publiés qui l'affichent reçoivent les nouvelles images,
    /// sans republier ; les autres réglages non publiés de l'instance restent en brouillon.
    /// </summary>
    public async Task RefreshPages(Guid clientId, Guid mediaId)
    {
        var screens = store.Write(db =>
        {
            var ids = new HashSet<Guid>();
            var m = db.Media.FirstOrDefault(x => x.Id == mediaId && x.ClientId == clientId);
            if (m == null || catalog.Find(m.AppId) is not { } app || app.Settings.FirstOrDefault(f => f.IsMedia && AppCatalog.IsVisible(f, m.Settings)) is not { } mf) return ids;
            var urls = AppField.MediaIds(m.Settings.GetValueOrDefault(mf.Key))
                .Select(id => db.Media.FirstOrDefault(x => x.Id == id && x.ClientId == clientId && x.Status == null && x.Type == mf.Accept && x.FileName != null))
                .OfType<MediaItem>().Select(x => "/media/" + x.FileName).ToList();
            if (urls.Count == 0) return ids;

            bool Update(IEnumerable<PublishedItem> items)
            {
                var n = 0;
                foreach (var pi in items.Where(x => x.MediaId == mediaId))
                {
                    if (pi.Urls != null && urls.SequenceEqual(pi.Urls)) continue;
                    pi.Urls = urls;
                    pi.Settings[mf.Key] = m.Settings.GetValueOrDefault(mf.Key) ?? "";
                    n++;
                }
                return n > 0;
            }
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

    /// <summary>Réglage d'une aire modifié (mise en veille) : ses écrans rechargent leur configuration.</summary>
    public Task NotifyArea(Guid areaId) => NotifyWhere(s => s.AreaId == areaId);

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
        return Helpers.Revision(db.Playlists.FirstOrDefault(p => p.Id == s.PlaylistId && p.ClientId == s.ClientId), s, t, db.Resellers.FirstOrDefault(r => r.Id == t.ResellerId), Helpers.SyncStamp(db, s));
    }
}
