using Linkii.Poc.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Linkii.Poc;

/// <summary>Publication d'une playlist + notification SignalR des écrans concernés.</summary>
public class Notifier(IHubContext<ScreenHub> hub, JsonStore store, AppCatalog catalog, IEventPublisher events) : IScreenPublishing
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
    /// <remarks>
    /// Publication directe : l'interface passe par <see cref="ScreenPublisher"/>, qui demande d'abord l'approbation aux personnes qui y sont soumises (voir <see cref="Approvals"/>).
    /// </remarks>
    public async Task PublishScreen(Guid screenId)
    {
        var ok = store.Write(db =>
        {
            var s = db.Screens.FirstOrDefault(x => x.Id == screenId);
            if (s == null) return false;
            ApplySnapshot(db, s, Build(db, s));
            return true;
        });
        if (ok) await NotifyScreen(screenId);
    }

    /// <summary>Ce que publier cet écran figerait maintenant : listes de ses zones, widgets, planning, borne. Ne change rien (la demande d'approbation le garde tel quel).</summary>
    public ScreenSnapshot Build(Db db, Screen s)
    {
        var own = db.Playlists.Where(x => x.ClientId == s.ClientId).ToList();
        List<PublishedItem> Zone(Guid? id) => own.FirstOrDefault(x => x.Id == id) is { } p ? Items(db, p) : new();
        var wall = Helpers.Wall(db.Areas, s) != null;   // écran d'un mur : jamais de widgets
        var (lists, revs) = ListsFor(db, s);   // listes du planning : leur contenu du moment
        return new ScreenSnapshot
        {
            Published = Zone(s.PlaylistId).Concat(wall ? Enumerable.Empty<PublishedItem>() : s.Widgets.Select(Widget).OfType<PublishedItem>()).ToList(),
            Layout = ScreenLayouts.Find(s.Layout).Id,
            Zones = s.ZonePlaylists().Skip(1).Select(Zone).ToList(),   // zones 2 et 3 du découpage
            Lists = lists, ListRevs = revs,
            Kiosk = s.Kiosk == null ? null : BuildKiosk(db, s),   // borne interactive : menu de tuiles
            Stamp = Helpers.PublishStamp(s, own)
        };
    }

    /// <summary>Fait diffuser à l'écran ce contenu figé (publication directe, ou demande approuvée), puis annonce la publication aux webhooks. L'écran est prévenu par l'appelant.</summary>
    public void ApplySnapshot(Db db, Screen s, ScreenSnapshot snap)
    {
        s.Published = snap.Published;
        s.PublishedLayout = snap.Layout;
        s.PublishedZones = snap.Zones;
        s.PublishedLists = snap.Lists; s.PublishedListRevs = snap.ListRevs; s.PlanningRevision++;
        s.PublishedKiosk = snap.Kiosk;
        s.PublishedVersion++;
        s.PublishedStamp = snap.Stamp;
        s.PublishedUtc = DateTime.UtcNow;
        events.Publish(s.ClientId, ScreenEvents.Published, new { screen = ScreenEvents.Brief(db, s), version = s.PublishedVersion, publishedAt = s.PublishedUtc });   // webhooks (API) : sans attente, jamais d'erreur
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
        var list = db.InteractiveLists.FirstOrDefault(l => l.Id == k.ListId && l.ClientId == s.ClientId);
        var cfg = System.Text.Json.JsonSerializer.Deserialize<KioskConfig>(System.Text.Json.JsonSerializer.Serialize(k))!;   // copie : modifier le brouillon ne change pas ce qui est publié
        cfg.Tiles = new();
        return new PublishedKiosk { Config = cfg, Pages = KioskPages(db, s.ClientId, list) };
    }

    /// <summary>Aperçu tactile d'une liste interactive dans son état actuel (même pas encore sur une borne) : réglages de borne par défaut,
    /// curseur visible et sans verrou (c'est le back-office qui la manipule à la souris).</summary>
    public PublishedKiosk PreviewKiosk(Db db, InteractiveList l) =>
        new() { Config = new KioskConfig { Lock = false, HideCursor = false, NoZoom = false }, Pages = KioskPages(db, l.ClientId, l) };

    private List<PublishedKioskPage> KioskPages(Db db, Guid clientId, InteractiveList? list)
    {
        var growth = db.Clients.FirstOrDefault(c => c.Id == clientId)?.IsGrowth == true;
        string? Url(Guid? id) => id == null ? null : db.Media.FirstOrDefault(m => m.Id == id && m.ClientId == clientId && m.Type == "image" && m.Status == null && m.FileName != null) is { } m ? "/media/" + m.FileName : null;
        var pages = new List<PublishedKioskPage>();
        foreach (var p in list?.Pages ?? new())
        {
            var pp = new PublishedKioskPage { Id = p.Id, Name = p.Name, Bg = p.Bg, BgImageUrl = Url(p.BgImageId), Len = Math.Clamp(p.Len, 1, InteractiveLists.MaxLen) };
            foreach (var e in p.Els)
            {
                var act = new PublishedKioskAction { Type = e.Action.Type, PageId = e.Action.PageId };
                if (e.Kind == "tile" && e.Action.Type == "media")
                {
                    // une tuile dont le contenu a disparu, ou n'est pas prêt, n'est pas publiée : la borne n'affiche jamais de tuile cassée
                    var m = db.Media.FirstOrDefault(x => x.Id == e.Action.MediaId && x.ClientId == clientId);
                    if (m == null || !Kiosk.CanOpen(m)) continue;
                    e.Action.Item.MediaId = m.Id;
                    e.Action.Item.Placement = "full";
                    if (Snapshot(db, e.Action.Item, m, growth) is not { } pi) continue;
                    pi.Placement = "full";
                    act.Item = pi;
                }
                if (e.Kind == "image" && Url(e.ImageId) == null) continue;
                pp.Els.Add(new PublishedKioskEl
                {
                    Id = e.Id, Kind = e.Kind, X = e.X, Y = e.Y, W = e.W, H = e.H, Text = e.Text, Sub = e.Sub, Color = e.Color, ImageUrl = Url(e.ImageId), TextColor = e.TextColor,
                    Font = e.Font, FontSize = e.FontSize, Bold = e.Bold, Align = e.Align, Valign = e.Valign, Radius = e.Radius, ShowFrom = e.ShowFrom, ShowTo = e.ShowTo, Action = act
                });
            }
            pages.Add(pp);
        }
        return pages;
    }

    /// <summary>Instantané des listes que le planning de l'écran peut faire jouer (une liste supprimée n'y figure plus).</summary>
    private void SnapshotLists(Db db, Screen s)
    {
        (s.PublishedLists, s.PublishedListRevs) = ListsFor(db, s);
        s.PlanningRevision++;
    }

    private (Dictionary<Guid, List<PublishedItem>> Lists, Dictionary<Guid, int> Revs) ListsFor(Db db, Screen s)
    {
        var area = db.Areas.FirstOrDefault(a => a.Id == s.AreaId);
        var own = db.Playlists.Where(x => x.ClientId == s.ClientId).ToList();
        var lists = Planning.Effective(area, s).Select(x => x.PlaylistId).Distinct()
            .Select(id => own.FirstOrDefault(p => p.Id == id)).OfType<Playlist>()
            .ToDictionary(p => p.Id, p => Items(db, p));
        return (lists, lists.Keys.ToDictionary(id => id, id => own.First(p => p.Id == id).Revision));
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
        s.Transition = Transitions.Valid(i.Transition);
        s.TransitionMs = Transitions.ValidMs(i.TransitionMs <= 0 ? Transitions.DefaultMs : i.TransitionMs);
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
                id => db.Media.FirstOrDefault(x => x.Id == id && x.ClientId == m.ClientId && x.Type == "app") ?? SharedCalendarSources.RoomContent(db.Clients.FirstOrDefault(x => x.Id == m.ClientId), id),
                id => db.Clients.FirstOrDefault(x => x.Id == m.ClientId)?.DataSets.FirstOrDefault(x => x.Id == id),
                () => db.Clients.FirstOrDefault(x => x.Id == m.ClientId)?.SharedCalendars.Where(x => x.Room).Select(x => x.Id).ToList() ?? new List<Guid>()) is not { } c) return null;
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

    public string? AppName(string appId) => catalog.Find(appId)?.Name;

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
