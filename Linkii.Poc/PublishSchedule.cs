namespace Linkii.Poc;

/// <summary>
/// Publication programmée (onglet « Publication » d'une liste de lecture) : à l'heure choisie, l'écran reçoit la liste (elle devient sa liste de lecture) puis est publié,
/// comme un clic sur « Publier ». Une seule programmation par écran : en programmer une nouvelle remplace la précédente.
/// </summary>
public class ScheduledPublication
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Guid ScreenId { get; set; }
    public Guid? PlaylistId { get; set; }   // la liste concernée (null : ancien format, l'écran est retiré de sa liste)
    public bool Remove { get; set; }        // à l'heure dite, l'écran n'a plus cette liste (sinon il la reçoit)
    public List<int>? Zones { get; set; }   // zones de l'écran qui auront la liste (0 = zone 1) ; null : ancien format (zone 1 si l'écran ne l'a nulle part)
    public DateTime DueUtc { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

public class PublishSchedule(JsonStore store, Notifier notifier, ILogger<PublishSchedule> log)
{
    /// <summary>Programme (ou reprogramme) la publication d'un écran. <paramref name="local"/> est l'heure de l'organisation (son fuseau).</summary>
    public void Schedule(Guid clientId, Guid screenId, Guid? playlistId, DateTime local, Guid? by, bool remove = false, List<int>? zones = null) => store.Write(db =>
    {
        var tz = Zone(db.Clients.FirstOrDefault(c => c.Id == clientId)?.Timezone);
        var due = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
        db.ScheduledPublications.RemoveAll(x => x.ClientId == clientId && x.ScreenId == screenId);
        db.ScheduledPublications.Add(new ScheduledPublication { ClientId = clientId, ScreenId = screenId, PlaylistId = playlistId, Remove = remove, Zones = zones, DueUtc = due, CreatedBy = by });
    });

    public void Cancel(Guid clientId, Guid screenId) => store.Write(db => db.ScheduledPublications.RemoveAll(x => x.ClientId == clientId && x.ScreenId == screenId));

    /// <summary>Programmations de l'organisation, avec leur date dans son fuseau.</summary>
    public static List<(ScheduledPublication S, DateTime Local)> Of(Db db, Guid clientId)
    {
        var tz = Zone(db.Clients.FirstOrDefault(c => c.Id == clientId)?.Timezone);
        return db.ScheduledPublications.Where(x => x.ClientId == clientId).Select(x => (x, TimeZoneInfo.ConvertTimeFromUtc(x.DueUtc, tz))).ToList();
    }

    private static TimeZoneInfo Zone(string? id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id ?? "Europe/Zurich"); } catch (Exception) { return TimeZoneInfo.Utc; }
    }

    /// <summary>
    /// Donne ou retire une liste à un écran. Donner : si l'écran ne la joue dans aucune zone, elle devient celle de la zone 1. Retirer : toutes les zones
    /// qui la jouent (un écran multizone peut l'avoir en zone 2 ou 3) sont vidées.
    /// </summary>
    public static void Assign(Screen s, Guid playlistId, bool give)
    {
        if (give) { if (!s.ZonePlaylists().Contains(playlistId)) s.SetZonePlaylist(0, playlistId); return; }
        for (var z = 0; z < s.ZoneCount; z++) if (s.ZonePlaylist(z) == playlistId) s.SetZonePlaylist(z, null);
    }

    /// <summary>Met la liste exactement dans les zones données (0 = zone 1) et la retire des autres zones de l'écran (onglet Diffusion, écrans multizone).</summary>
    public static void AssignZones(Screen s, Guid playlistId, IReadOnlyCollection<int> zones)
    {
        for (var z = 0; z < s.ZoneCount; z++)
        {
            if (zones.Contains(z)) s.SetZonePlaylist(z, playlistId);
            else if (s.ZonePlaylist(z) == playlistId) s.SetZonePlaylist(z, null);
        }
    }

    /// <summary>Passe les publications arrivées à échéance.</summary>
    public async Task Tick(DateTime nowUtc)
    {
        var done = store.Write(db =>
        {
            var due = db.ScheduledPublications.Where(x => x.DueUtc <= nowUtc).ToList();
            var ids = new List<Guid>();
            foreach (var x in due)
            {
                db.ScheduledPublications.Remove(x);
                if (db.Screens.FirstOrDefault(s => s.Id == x.ScreenId && s.ClientId == x.ClientId) is not { } sc) continue;
                if (x.PlaylistId is { } pid && !x.Remove && !db.Playlists.Any(p => p.Id == pid && p.ClientId == x.ClientId)) continue;   // la liste a été supprimée
                if (x.PlaylistId is { } lid && x.Zones != null) AssignZones(sc, lid, x.Remove ? Array.Empty<int>() : x.Zones);
                else if (x.PlaylistId is { } lid2) Assign(sc, lid2, !x.Remove); else sc.PlaylistId = null;
                ids.Add(sc.Id);
            }
            return ids;
        });
        foreach (var id in done)
        {
            try { await notifier.PublishScreen(id); }
            catch (Exception ex) { log.LogWarning(ex, "Publication programmée de l'écran {Screen} en échec", id); }
        }
    }
}

public class PublishScheduleWorker(PublishSchedule schedule, ILogger<PublishScheduleWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        do
        {
            try { await schedule.Tick(DateTime.UtcNow); }
            catch (Exception ex) { log.LogWarning(ex, "Passage des publications programmées en échec"); }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}
