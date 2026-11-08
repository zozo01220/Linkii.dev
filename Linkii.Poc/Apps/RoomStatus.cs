using System.Collections.Concurrent;

namespace Linkii.Poc;

/// <summary>
/// État des salles, lu côté serveur pour tout le monde (porte des tablettes, page de l'application, cartes des écrans, onglet Réservations) :
/// rendez-vous de la journée et du lendemain + réservations Linkivi pas encore lues dans l'agenda. Cache de 30 s par salle :
/// dix tablettes ou une page ouverte ne font pas dix appels chez Microsoft ou Google.
/// </summary>
public class RoomStatusService(JsonStore store, CalendarService calendars, SecretBox box)
{
    /// <summary>Ce qui a été lu : créneaux (avec leurs titres : chaque lecteur masque selon ses règles), erreur de la dernière lecture, date de la dernière lecture réussie.</summary>
    public record Snapshot(List<RoomInterval> Events, string? Error, DateTime ReadUtc);

    /// <summary>Au-delà, une copie qui n'a pas pu être relue n'est plus montrée (« Données indisponibles »).</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Life = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<Guid, (DateTime At, Snapshot S)> cache = new();

    public void Forget(Guid roomId) => cache.TryRemove(roomId, out _);

    /// <summary>Créneaux de la salle (fresh : sans cache, pour décider d'une réservation). Null si la salle n'existe pas.</summary>
    public async Task<Snapshot?> Read(Guid clientId, Guid roomId, bool fresh = false)
    {
        var now = DateTime.UtcNow;
        cache.TryGetValue(roomId, out var hit);
        if (!fresh && hit.S != null && now - hit.At < Life) return hit.S;
        var (tenant, cal, bookings, title) = store.Read(db =>
        {
            var t = db.Clients.FirstOrDefault(c => c.Id == clientId);
            var c = t?.SharedCalendars.FirstOrDefault(x => x.Id == roomId);
            return (t, c, t?.RoomBookings.Where(b => b.RoomId == roomId && b.Holds && b.EndUtc > now.AddDays(-1)).ToList() ?? new(), t?.RoomApp?.BookTitle ?? RoomRules.DefaultTitle);
        });
        if (tenant == null || cal == null) return null;
        var tz = RoomRules.Zone(tenant.Timezone);
        var from = RoomRules.DayStartUtc(now, tz);
        Snapshot snap;
        try
        {
            var src = SharedCalendarSources.Resolve(tenant, cal, box);
            if (fresh) calendars.Forget(src);
            var events = await calendars.GetEvents(src, tenant, from, from.AddDays(2));
            snap = new Snapshot(RoomRules.Merge(events, bookings, true, title), null, now);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or FormatException)
        {
            var msg = ex is TaskCanceledException ? "L'agenda n'a pas répondu à temps." : ex.Message;
            // la dernière copie reste valable un moment (la porte continue d'afficher l'état), avec l'erreur
            snap = hit.S != null
                ? hit.S with { Error = msg, Events = RoomRules.Merge(hit.S.Events.Where(e => !e.Linkivi).Select(e => new EventDto(e.Title, e.StartUtc, e.EndUtc, e.AllDay, null)), bookings, true, title) }
                : new Snapshot(RoomRules.Merge(Array.Empty<EventDto>(), bookings, true, title), msg, DateTime.MinValue);
        }
        cache[roomId] = (now, snap);
        return snap;
    }

    /// <summary>Plusieurs salles à la fois (page de l'application, cartes des écrans).</summary>
    public async Task<Dictionary<Guid, Snapshot>> ReadMany(Guid clientId, IEnumerable<Guid> roomIds)
    {
        var ids = roomIds.Distinct().ToList();
        var all = await Task.WhenAll(ids.Select(async id => (id, s: await Read(clientId, id))));
        return all.Where(x => x.s != null).ToDictionary(x => x.id, x => x.s!);
    }

    /// <summary>État calculé d'une salle, ou null si la copie est trop ancienne pour être montrée.</summary>
    public static RoomNow? StateOf(Snapshot? s, SharedCalendar room, TimeZoneInfo tz, DateTime nowUtc)
    {
        if (s == null || s.Error != null && nowUtc - s.ReadUtc > Stale) return null;
        return RoomRules.State(nowUtc, s.Events, room.RoomSoon, room.RoomAllDay, tz);
    }
}
