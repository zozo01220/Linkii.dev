namespace Linkii.Poc;

/// <summary>
/// Module « Écrans » de l'API (§153, supervision) : lire la liste des écrans et leur état, et recevoir <c>screen.offline</c> / <c>screen.online</c>.
/// Les événements sont émis par <see cref="ScreenWatchWorker"/> (délai « Alerter après » des Réglages) ; une clé voit tous les écrans de son organisation.
/// </summary>
public class ScreensApiModule : IApiModule
{
    public const string Read = "screens:read", Control = "screens:control";
    public string Id => "screens";

    public IEnumerable<ApiScope> Scopes { get; } = new[]
    {
        new ApiScope(Read, "Écrans", "Lister les écrans et leur état"),
        new ApiScope(Control, "Écrans", "Commander les écrans : redémarrer le player ou l'appareil, éteindre l'appareil, éteindre l'écran, le réveiller"),
    };

    public IEnumerable<ApiTemplate> Templates { get; } = new[]
    {
        new ApiTemplate("Lecture seule", "Consulter les écrans et leur état", new[] { Read }, 10),
        new ApiTemplate("Home Assistant", "Voir les écrans et les commander depuis Home Assistant", new[] { Read, Control }, 40),
    };

    public IEnumerable<EventType> Events { get; } = new[]
    {
        new EventType(ScreenEvents.Offline, "Écrans", "Un écran ne répond plus (après le délai d'alerte des Réglages)"),
        new EventType(ScreenEvents.Online, "Écrans", "Un écran signalé hors ligne est de retour"),
        new EventType(ScreenEvents.Published, "Écrans", "Une publication part vers un écran (sa liste, ses widgets, sa configuration)"),
        new EventType(ScreenEvents.Applied, "Écrans", "Un écran affiche une nouvelle version publiée"),
    };

    public static readonly ApiRoute List = new("GET", "/screens", Read, RateClass.Read, "Lister les écrans appairés et leur état (filtres status et areaId, pagination par curseur)",
        Query: "status:string:online, offline ou updating; areaId:uuid:aire de gestion; limit:integer:1 à 200 (50 par défaut); cursor:string:valeur « next » de la page précédente");
    public static readonly ApiRoute One = new("GET", "/screens/{id}", Read, RateClass.Read, "Lire un écran");

    public static readonly ApiRoute Command = new("POST", "/screens/{id}/commands", Control, RateClass.Write,
        "Commander un écran (Pro). « command » : restart-player (redémarrer l'application), reboot (redémarrer l'appareil), poweroff (éteindre l'appareil : Linkivi ne peut pas le rallumer), " +
        "screen-off (éteindre l'écran, jusqu'au réveil), wake (réveiller l'écran pour une heure). Les commandes acceptées par l'appareil sont dans « commands » de l'écran. Un écran de mur entraîne tout le mur.",
        Body: "{ \"command\": \"restart-player\" }");

    public IEnumerable<ApiRoute> Routes { get; } = new[] { List, One, Command };

    public record CommandBody(string? Command);

    public void Map(ApiRouter r)
    {
        r.Map(Command, async (HttpContext ctx, DeviceCommandService svc, Guid id, CommandBody? body) =>
        {
            var cid = ctx.User.ClientId();
            var res = await svc.Run(cid, ctx.User.OwnerId(), id, body?.Command?.Trim().ToLowerInvariant() ?? "", $"clé « {ctx.User.FindFirst(ApiAuth.KeyName)?.Value} »");
            if (res.NotFound) return ApiProblem.Result(ctx, ApiCodes.NotFound);
            if (!res.Ok) return ApiProblem.Result(ctx, DeviceCommandService.IsKnown(body?.Command?.Trim().ToLowerInvariant()) ? ApiCodes.ScreenUnavailable : ApiCodes.InvalidRequest, res.Error);
            return Results.Json(new
            {
                command = body!.Command!.Trim().ToLowerInvariant(), accepted = res.Done,
                skipped = res.Skipped.Select(x => new { id = x.Id, reason = x.Why })
            }, ApiJson.Options, statusCode: 202);
        });

        r.Map(List, (HttpContext ctx, JsonStore store, int? limit, string? cursor, string? status, Guid? areaId) =>
        {
            var cid = ctx.User.ClientId();
            var now = DateTime.UtcNow;
            var n = Math.Clamp(limit ?? 50, 1, 200);
            if (status != null && status is not ("online" or "offline" or "updating")) return ApiProblem.Result(ctx, ApiCodes.InvalidRequest, "status : online, offline ou updating.");
            var all = store.Read(db =>
            {
                var c = db.Clients.First(x => x.Id == cid);
                return db.Screens.Where(s => s.ClientId == cid && s.Token != null && (areaId == null || s.AreaId == areaId))
                    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id)
                    .Select(s => ScreenEvents.View(db, c, s, now)).Where(v => status == null || v.Status == status).ToList();
            });
            var start = Guid.TryParse(cursor, out var after) ? all.FindIndex(v => v.Id == after) + 1 : 0;
            var page = all.Skip(start).Take(n + 1).ToList();
            return Results.Json(new { data = page.Take(n), next = page.Count > n ? page[n - 1].Id.ToString() : null }, ApiJson.Options);
        });

        r.Map(One, (HttpContext ctx, JsonStore store, Guid id) =>
        {
            var cid = ctx.User.ClientId();
            var now = DateTime.UtcNow;
            var v = store.Read(db =>
            {
                var s = db.Screens.FirstOrDefault(x => x.Id == id && x.ClientId == cid && x.Token != null);
                return s == null ? null : ScreenEvents.View(db, db.Clients.First(x => x.Id == cid), s, now);
            });
            return v == null ? ApiProblem.Result(ctx, ApiCodes.NotFound) : Results.Json(v, ApiJson.Options);
        });
    }
}

public record RefDto(Guid Id, string Name);
public record ParamDto(string Name, string Value);
public record ScreenDto(Guid Id, string Name, string Status, bool Online, bool Paused, bool Asleep, DateTime? LastSeenAt, RefDto? Area, RefDto? Zone, RefDto? Playlist,
    string ScreenType, string Orientation, string Resolution, List<ParamDto> Params, string[] Commands, string? Device = null);
public record ScreenEvent(Guid ClientId, string Type, object Data);

/// <summary>État d'un écran vu de l'API, et détection des pannes à signaler aux webhooks.</summary>
public static class ScreenEvents
{
    public const string Offline = "screen.offline", Online = "screen.online", Published = "screen.published", Applied = "screen.applied";

    public static ScreenDto View(Db db, Tenant c, Screen s, DateTime now)
    {
        var area = db.Areas.FirstOrDefault(a => a.Id == s.AreaId);
        var zone = area?.Zones.FirstOrDefault(z => z.Id == s.ZoneId);
        var playlist = db.Playlists.FirstOrDefault(p => p.Id == s.PlaylistId && p.ClientId == c.Id);
        var reseller = db.Resellers.FirstOrDefault(r => r.Id == c.ResellerId);
        var online = ScreenControl.Online(s, now);
        var state = !online ? "offline" : Helpers.Status(s, playlist, c, reseller, Helpers.SyncStamp(db, s)) == "Mise à jour" ? "updating" : "online";
        return new ScreenDto(s.Id, s.Name, state, online, s.IsPaused(now), Sleep.State(c, area, s, Helpers.Wall(db.Areas, s) != null, now).Asleep, s.LastSeenUtc,
            area == null ? null : new RefDto(area.Id, area.Name), zone == null ? null : new RefDto(zone.Id, zone.Name), playlist == null ? null : new RefDto(playlist.Id, playlist.Name),
            s.ScreenType, s.Orientation, s.DetectedW is { } w ? $"{w}x{s.DetectedH}" : s.Resolution, s.Params.Select(p => new ParamDto(p.Name, p.Value)).ToList(),
            DeviceCommandService.Supported(s), string.IsNullOrEmpty(s.DeviceKind) ? null : s.DeviceKind);
    }

    public static object Brief(Db db, Screen s)
    {
        var area = db.Areas.FirstOrDefault(a => a.Id == s.AreaId);
        var zone = area?.Zones.FirstOrDefault(z => z.Id == s.ZoneId);
        return new { id = s.Id, name = s.Name, area = area == null ? null : new RefDto(area.Id, area.Name), zone = zone == null ? null : new RefDto(zone.Id, zone.Name) };
    }

    /// <summary>
    /// Écrans tombés ou revenus depuis le dernier passage, pour les organisations Pro qui ont au moins une adresse de webhook active. Même délai que l'alerte
    /// « écran hors ligne » (Réglages), même exception pour un écran en veille, mais un repère à part (<see cref="Screen.ApiOfflineUtc"/>) : l'événement
    /// part même si les alertes e-mail ou notification sont coupées. À appeler dans un Write ; les événements sont émis ensuite, hors du verrou.
    /// </summary>
    public static List<ScreenEvent> Plan(Db db, DateTime now, DateTime startedUtc)
    {
        var events = new List<ScreenEvent>();
        foreach (var c in db.Clients)
        {
            var listened = ApiLimits.Allowed(c) && !c.IsCut && c.Webhooks.Any(w => w.Active);
            var screens = db.Screens.Where(s => s.ClientId == c.Id && s.Token != null).ToList();
            if (!listened) { foreach (var s in screens.Where(s => s.ApiOfflineUtc != null)) s.ApiOfflineUtc = null; continue; }
            var delay = TimeSpan.FromMinutes(Math.Clamp(c.AlertOfflineMinutes, 1, 1440));
            if (now - startedUtc < delay) continue;   // les écrans n'ont pas encore eu le temps de se reconnecter après le démarrage du serveur
            foreach (var s in screens.Where(s => s.LastSeenUtc != null))
            {
                if (s.ApiOfflineUtc == null && now - s.LastSeenUtc > delay && !ScreenAlerts.IsSleeping(db, c, s, now))
                {
                    s.ApiOfflineUtc = now;
                    events.Add(new(c.Id, Offline, new { screen = Brief(db, s), lastSeenAt = s.LastSeenUtc, offlineAfterMinutes = (int)delay.TotalMinutes }));
                }
                else if (s.ApiOfflineUtc is { } since && ScreenControl.Online(s, now))
                {
                    s.ApiOfflineUtc = null;
                    events.Add(new(c.Id, Online, new { screen = Brief(db, s), offlineSince = since, downtimeMinutes = (int)Math.Max(0, (now - since).TotalMinutes) }));
                }
            }
        }
        return events;
    }
}
