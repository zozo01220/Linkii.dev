using System.Collections.Concurrent;
using Linkii.Poc.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Linkii.Poc;

/// <summary>Ce que reçoit la porte d'une salle (écran de salle) : sa configuration dans le flux du player, ses créneaux et les durées possibles par /api/player/room.</summary>
public static class RoomDoor
{
    /// <summary>ok : la porte de la salle ; none : écran sans salle ; off : salle désactivée ou retirée ; retired : application désinstallée.</summary>
    public static string StateOf(bool installed, Screen s, SharedCalendar? room) =>
        !installed ? "retired" : s.RoomCalendarId == null ? "none" : room is not { Room: true, Off: false } ? "off" : "ok";

    /// <summary>Empreinte de la porte dans la révision de l'écran : un changement (salle, réglages, application) recharge la tablette.</summary>
    public static string Stamp(Screen s, Tenant t) => s.Room == null ? "" : $"|rm{s.Room.Revision}.{t.RoomApp?.Revision ?? 0}.{s.RoomCalendarId:N}";

    /// <summary>Partie « room » du flux du player (null : autre type d'écran).</summary>
    public static object? Config(Db db, Tenant t, Screen s)
    {
        if (s.Room is not { } r) return null;
        var room = s.RoomCalendarId is { } id ? t.SharedCalendars.FirstOrDefault(c => c.Id == id) : null;
        return new
        {
            state = StateOf(RoomSetup.Installed(db, t.Id), s, room),
            theme = r.Theme == "dark" ? "dark" : "light",
            buttons = r.Buttons,
            name = room != null ? SharedCalendarSources.RoomName(room) : "",
            rev = r.Revision
        };
    }

    /// <summary>Données de la porte : créneaux (titres seulement si l'écran les affiche ; « Réservé sur place » toujours), durées possibles, mode, erreur de lecture.</summary>
    public static async Task<Dictionary<string, object?>> Payload(RoomStatusService status, Tenant t, Screen s, SharedCalendar? room, bool installed, DateTime nowUtc, bool fresh = false)
    {
        var state = StateOf(installed, s, room);
        var res = new Dictionary<string, object?> { ["state"] = state, ["serverNow"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        if (state != "ok" || room == null || s.Room == null) return res;
        var tz = RoomRules.Zone(t.Timezone);
        var snap = await status.Read(t.Id, room.Id, fresh);
        var events = snap?.Events ?? new();
        var stale = snap == null || snap.Error != null && nowUtc - snap.ReadUtc > RoomStatusService.Stale;
        var mode = room.RoomWriteMode;
        var canBook = RoomModes.CanBook(mode) && s.Room.Buttons && !stale;
        var refused = t.RoomBookings.Where(b => b.RoomId == room.Id && b.State == RoomBooking.Refused && nowUtc - b.CreatedUtc < TimeSpan.FromMinutes(15)).OrderByDescending(b => b.CreatedUtc).FirstOrDefault();
        res["room"] = new { name = SharedCalendarSources.RoomName(room), cap = room.RoomCap, place = room.RoomPlace, allDay = room.RoomAllDay, soon = room.RoomSoon };
        res["events"] = events.Select(e => new { title = e.Linkivi || s.Room.Titles ? e.Title : "", start = e.StartUtc, end = e.EndUtc, allDay = e.AllDay, linkivi = e.Linkivi });
        res["options"] = canBook ? RoomRules.Options(nowUtc, events, t.RoomApp ?? new(), room.RoomAllDay, tz).Select(o => new { min = o.Minutes, ok = o.Ok, why = o.Why }) : Array.Empty<object>();
        res["canBook"] = canBook;
        res["mode"] = mode;
        res["error"] = snap?.Error;
        res["stale"] = stale;
        res["readAt"] = snap == null || snap.ReadUtc == DateTime.MinValue ? null : new DateTimeOffset(snap.ReadUtc).ToUnixTimeMilliseconds();
        res["refused"] = refused?.Id.ToString("N");
        return res;
    }
}

/// <summary>
/// Réservation depuis une tablette : le serveur décide (jamais la tablette). Vérifie l'écran, la salle et les règles, relit l'agenda sans cache,
/// écrit (ou garde la réservation pour un lien ICS), puis prévient tous les écrans de la salle. Une salle à la fois (verrou), limite par écran.
/// </summary>
public class RoomBookingService(JsonStore store, CalendarService calendars, RoomStatusService status, SecretBox box, IEnumerable<IWritableCalendarConnector> writers,
    IHubContext<ScreenHub> hub, AlertService alerts, ILogger<RoomBookingService> log)
{
    public record Outcome(int Status, string? Error, string? Code = null, DateTime? From = null, DateTime? To = null);

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();
    private readonly ConcurrentDictionary<Guid, List<DateTime>> recent = new();
    public const int PerHour = 12;
    public static readonly TimeSpan Spacing = TimeSpan.FromSeconds(10);

    /// <summary>Oublie les réservations récentes de chaque écran (tests).</summary>
    public void ResetLimits() => recent.Clear();

    /// <summary>Limite par écran : une réservation toutes les 10 s, 12 par heure (rend vrai si la demande passe et la compte).</summary>
    private bool Allow(Guid screenId, DateTime now)
    {
        var l = recent.GetOrAdd(screenId, _ => new List<DateTime>());
        lock (l)
        {
            l.RemoveAll(x => now - x > TimeSpan.FromHours(1));
            if (l.Count >= PerHour || l.Count > 0 && now - l[^1] < Spacing) return false;
            l.Add(now);
            return true;
        }
    }

    public async Task<Outcome> Book(Guid screenId, int minutes)
    {
        var now = DateTime.UtcNow;
        var ctx = store.Read<(Tenant? Tenant, Screen? Screen, SharedCalendar? Room, bool Installed, bool Buttons, string ScreenName)>(db =>
        {
            var s = db.Screens.FirstOrDefault(x => x.Id == screenId);
            if (s?.Room == null) return default;
            var t = db.Clients.FirstOrDefault(c => c.Id == s.ClientId);
            var room = s.RoomCalendarId is { } rid ? t?.SharedCalendars.FirstOrDefault(c => c.Id == rid) : null;
            return (Tenant: t, Screen: s, Room: room, Installed: t != null && RoomSetup.Installed(db, t.Id), Buttons: s.Room.Buttons, ScreenName: s.Name);
        });
        if (ctx.Tenant == null || ctx.Screen == null) return new(404, "Cet écran n'est pas un écran de salle.", "screen");
        var state = RoomDoor.StateOf(ctx.Installed, ctx.Screen, ctx.Room);
        if (state != "ok" || ctx.Room == null) return new(403, state == "retired" ? "L'application Réservation de salle a été retirée." : "Salle indisponible.", state);
        if (!ctx.Buttons || !RoomModes.CanBook(ctx.Room.RoomWriteMode)) return new(403, "Cette salle s'affiche seulement : la réservation n'est pas proposée ici.", "read");
        if (!AppRules.ValidMinutes(minutes)) return new(400, "Durée non proposée.", "duration");
        if (!Allow(screenId, now)) return new(429, "Trop de réservations depuis cet écran : réessayez dans un instant.", "rate");

        var t = ctx.Tenant;
        var room = ctx.Room;
        var tz = RoomRules.Zone(t.Timezone);
        var gate = locks.GetOrAdd(room.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(15))) return new(503, "La salle est occupée par une autre demande : réessayez.", "busy");
        try
        {
            var snap = await status.Read(t.Id, room.Id, fresh: true);
            if (snap == null) return new(404, "Salle introuvable.", "off");
            if (snap.Error != null) return new(503, "L'agenda de la salle ne répond pas : réservation impossible pour le moment.", "calendar");
            var rules = t.RoomApp ?? new RoomAppSettings();
            var opts = RoomRules.Options(now, snap.Events, rules, room.RoomAllDay, tz);
            var o = opts.FirstOrDefault(x => x.Minutes == minutes);
            if (opts.Count == 0) return new(409, "Déjà réservée : la salle vient d'être prise.", "taken");
            if (o == null) return new(400, "Durée non proposée.", "duration");
            if (!o.Ok) return new(409, o.Why == "Hors horaires" ? "Hors des horaires réservables." : $"Déjà réservée : {o.Why?.ToLowerInvariant()}.", "taken");

            var start = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            var booking = new RoomBooking { RoomId = room.Id, ScreenId = screenId, ScreenName = ctx.ScreenName, StartUtc = start, EndUtc = start.AddMinutes(minutes), Mode = room.RoomWriteMode };
            Save(t.Id, booking, add: true);
            var src = SharedCalendarSources.Resolve(t, room, box);
            if (booking.Mode != RoomModes.Local)
            {
                var writer = writers.FirstOrDefault(w => w.Id == room.Source);
                if (writer == null) { booking.State = RoomBooking.Failed; booking.Error = "Cet agenda ne s'écrit pas."; Save(t.Id, booking); return new(403, "Cette salle s'affiche seulement.", "read"); }
                try
                {
                    booking.ProviderEventId = await writer.Create(src, t, new BookRequest(rules.BookTitle, $"Réservé depuis l'écran « {ctx.ScreenName} » (Linkivi).", booking.StartUtc, booking.EndUtc, booking.Id, SharedCalendarSources.RoomName(room), booking.Mode));
                }
                catch (Exception ex) when (ex is CalendarWriteException or HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    booking.State = RoomBooking.Failed;
                    booking.Error = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
                    Save(t.Id, booking);
                    log.LogWarning(ex, "Réservation refusée par le fournisseur pour la salle {Room}", room.Id);
                    if (ex is CalendarWriteException { Rights: true }) await LoseRights(t.Id, room.Id, room.Source);
                    return ex is TaskCanceledException ? new(504, "L'agenda n'a pas répondu à temps : réservation impossible.", "timeout")
                        : new(502, "Réservation impossible. Prévenez l'administrateur.", ex is CalendarWriteException { Rights: true } ? "rights" : "provider");
                }
            }
            booking.State = booking.Mode == RoomModes.Invite ? RoomBooking.Pending : RoomBooking.Confirmed;
            Save(t.Id, booking);
            calendars.Forget(src);
            status.Forget(room.Id);
            await NotifyRoom(room.Id);
            return new(200, null, null, booking.StartUtc, booking.EndUtc);
        }
        finally { gate.Release(); }
    }

    /// <summary>Enregistre la réservation (ajout, ou mise à jour de son état).</summary>
    private void Save(Guid clientId, RoomBooking b, bool add = false) => store.Write(db =>
    {
        if (db.Clients.FirstOrDefault(c => c.Id == clientId) is not { } t) return;
        if (add) { t.RoomBookings.Add(b); return; }
        if (t.RoomBookings.FirstOrDefault(x => x.Id == b.Id) is { } cur && !ReferenceEquals(cur, b))
            (cur.State, cur.ProviderEventId, cur.Error, cur.Shown) = (b.State, b.ProviderEventId, b.Error, b.Shown);
    });

    /// <summary>Les écrans de la salle relisent leurs créneaux tout de suite (message « RoomChanged » du hub).</summary>
    public async Task NotifyRoom(Guid roomId)
    {
        var ids = store.Read(db => db.Screens.Where(s => s.Room != null && s.RoomCalendarId == roomId && s.Token != null).Select(s => s.Id).ToList());
        foreach (var id in ids) await hub.Clients.Group(ScreenHub.Group(id)).SendAsync("RoomChanged");
    }

    /// <summary>Le compte ne peut plus écrire : toute la source passe en affichage seul (jeton ou droit retiré), ou seulement cette salle (agenda non modifiable).</summary>
    private async Task LoseRights(Guid clientId, Guid roomId, string source)
    {
        var (name, user, whole, screens) = store.Write(db =>
        {
            var t = db.Clients.First(c => c.Id == clientId);
            var whole = !RoomSetup.HasWriteRight(t, source);
            if (whole) { var a = t.RoomApp ??= new RoomAppSettings(); if (!a.LostRights.Contains(source)) a.LostRights.Add(source); RoomSetup.Recompute(t); }
            else if (t.SharedCalendars.FirstOrDefault(c => c.Id == roomId) is { } c) { c.RoomWriteMode = RoomModes.None; if (t.RoomApp != null) t.RoomApp.Revision++; }
            var room = t.SharedCalendars.FirstOrDefault(c => c.Id == roomId);
            var ids = db.Screens.Where(s => s.ClientId == clientId && s.Room != null && s.Token != null).Select(s => s.Id).ToList();
            return (room != null ? SharedCalendarSources.RoomName(room) : "la salle", source == "m365" ? t.MsUser : t.GoogleUser, whole, ids);
        });
        foreach (var id in screens) await hub.Clients.Group(ScreenHub.Group(id)).SendAsync("PlaylistChanged", "");   // les portes relisent leur mode (boutons masqués)
        var who = user.Length > 0 ? user : "Le compte connecté";
        await alerts.Raise(clientId, Alerts.Integrations, $"Réservation impossible : « {name} »",
            whole ? $"{who} ne peut plus créer de rendez-vous : les salles de {SharedCalendarSources.Find(source)?.Name} passent en affichage seul. Autorisez à nouveau dans Réservation de salle › Configuration."
                  : $"{who} ne peut pas créer de rendez-vous dans cette salle : elle passe en affichage seul.",
            "/apps/" + RoomRules.AppId, severity: Notifs.Warn, dedupe: "room-rights:" + (whole ? source : roomId.ToString()));
    }

    /// <summary>
    /// Invitations en attente (salle Microsoft, ressource Google) : la réponse de la salle est relue. Acceptée → confirmée ; refusée → Linkivi supprime
    /// son propre événement et la porte le dit ; sans réponse après 10 min → confirmée (salle à validation manuelle).
    /// </summary>
    public async Task CheckPending()
    {
        var now = DateTime.UtcNow;
        var todo = store.Read(db => db.Clients.SelectMany(t => t.RoomBookings
            .Where(b => b.State == RoomBooking.Pending && b.Mode == RoomModes.Invite && b.ProviderEventId != null && now - b.CreatedUtc > TimeSpan.FromSeconds(15))
            .Select(b => (Tenant: t, Booking: b))).ToList());
        foreach (var (t, b) in todo)
        {
            var room = t.SharedCalendars.FirstOrDefault(c => c.Id == b.RoomId);
            var writer = room == null ? null : writers.FirstOrDefault(w => w.Id == room.Source);
            if (room == null || writer == null) { Update(t.Id, b.Id, RoomBooking.Confirmed); continue; }
            var src = SharedCalendarSources.Resolve(t, room, box);
            string answer;
            try { answer = await writer.Response(src, t, b.ProviderEventId!); }
            catch (Exception ex) when (ex is CalendarWriteException or HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
            {
                log.LogInformation("Réponse de la salle {Room} illisible : {Error}", room.Id, ex.Message);
                answer = "none";
            }
            if (answer is "accepted" or "tentative" || answer == "none" && now - b.CreatedUtc > TimeSpan.FromMinutes(10))
            {
                Update(t.Id, b.Id, RoomBooking.Confirmed);
            }
            else if (answer == "declined")
            {
                try { await writer.Delete(src, t, b.ProviderEventId!, b.Mode); } catch (Exception ex) when (ex is CalendarWriteException or HttpRequestException or TaskCanceledException) { log.LogInformation("Invitation refusée non supprimée : {Error}", ex.Message); }
                Update(t.Id, b.Id, RoomBooking.Refused, "La salle a refusé l'invitation.");
                calendars.Forget(src);
                status.Forget(room.Id);
                await NotifyRoom(room.Id);
                await alerts.Raise(t.Id, Alerts.Integrations, $"Réservation refusée par « {SharedCalendarSources.RoomName(room)} »",
                    $"La salle a refusé la réservation de {RoomRules.Hm(b.StartUtc, RoomRules.Zone(t.Timezone))} faite depuis « {b.ScreenName} » (elle était sans doute déjà prise).",
                    "/apps/" + RoomRules.AppId, severity: Notifs.Warn, dedupe: "room-refused:" + b.Id);
            }
        }
    }

    private void Update(Guid clientId, Guid bookingId, string state, string? error = null) => store.Write(db =>
    {
        if (db.Clients.FirstOrDefault(c => c.Id == clientId)?.RoomBookings.FirstOrDefault(x => x.Id == bookingId) is { } b) { b.State = state; if (error != null) b.Error = error; }
    });

    /// <summary>Journal gardé 90 jours.</summary>
    public void Purge() => store.Write(db =>
    {
        var limit = DateTime.UtcNow.AddDays(-RoomRules.RetentionDays);
        foreach (var t in db.Clients.Where(t => t.RoomBookings.Count > 0)) t.RoomBookings.RemoveAll(b => b.EndUtc < limit);
    });

    public record TestLine(string Room, bool Ok, string Message);

    /// <summary>
    /// Test de réservation (Configuration › Santé) : pour chaque salle réservable, un événement « Test Linkivi » de 15 min demain à 02:00, aussitôt supprimé ;
    /// en invitation, la réponse de la salle est attendue 60 s au plus. ICS : rien à tester.
    /// </summary>
    public async Task<List<TestLine>> Test(Guid clientId)
    {
        var t = store.Read(db => db.Clients.First(c => c.Id == clientId));
        var tz = RoomRules.Zone(t.Timezone);
        var start = RoomRules.Utc(RoomRules.Local(DateTime.UtcNow, tz).Date.AddDays(1).AddHours(2), tz);
        var lines = new List<TestLine>();
        foreach (var room in RoomSetup.Rooms(t).Where(r => !r.Off))
        {
            var name = SharedCalendarSources.RoomName(room);
            if (room.RoomWriteMode == RoomModes.Local) { lines.Add(new(name, true, "Réservations Linkivi (lien ICS) : rien à tester.")); continue; }
            if (room.RoomWriteMode == RoomModes.None) { lines.Add(new(name, true, "Affichage seul : pas de réservation.")); continue; }
            var writer = writers.FirstOrDefault(w => w.Id == room.Source);
            if (writer == null) { lines.Add(new(name, false, "Cet agenda ne s'écrit pas.")); continue; }
            var src = SharedCalendarSources.Resolve(t, room, box);
            string? id = null;
            try
            {
                id = await writer.Create(src, t, new BookRequest("Test Linkivi", "Test de réservation de l'application Réservation de salle : supprimé aussitôt.", start, start.AddMinutes(15), Guid.NewGuid(), name, room.RoomWriteMode));
                var msg = "Écriture possible.";
                if (room.RoomWriteMode == RoomModes.Invite)
                {
                    var answer = "none";
                    for (var i = 0; i < 12 && answer == "none"; i++) { await Task.Delay(5000); answer = await writer.Response(src, t, id); }
                    msg = answer switch { "accepted" or "tentative" => "Écriture possible : la salle accepte les invitations.", "declined" => "La salle a refusé l'invitation de test.", _ => "La salle n'a pas répondu à l'invitation (validation manuelle ?)." };
                    lines.Add(new(name, answer is "accepted" or "tentative", msg));
                }
                else lines.Add(new(name, true, msg));
            }
            catch (Exception ex) when (ex is CalendarWriteException or HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
            {
                lines.Add(new(name, false, ex is CalendarWriteException { Rights: true } ? "Le compte connecté ne peut pas écrire dans cet agenda." : ex.Message));
            }
            finally
            {
                if (id != null) { try { await writer.Delete(src, t, id, room.RoomWriteMode); } catch (Exception ex) when (ex is CalendarWriteException or HttpRequestException or TaskCanceledException) { log.LogInformation("Événement de test non supprimé : {Error}", ex.Message); } }
            }
        }
        return lines;
    }
}

/// <summary>Réservations : réponses des salles aux invitations (toutes les 30 s), journal purgé chaque heure.</summary>
public class RoomWorker(RoomBookingService rooms, ILogger<RoomWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var lastPurge = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await rooms.CheckPending();
                if (DateTime.UtcNow - lastPurge > TimeSpan.FromHours(1)) { rooms.Purge(); lastPurge = DateTime.UtcNow; }
            }
            catch (Exception ex) { log.LogWarning(ex, "Réservation de salle : tâche de fond en échec"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stop); } catch (TaskCanceledException) { }
        }
    }
}

/// <summary>Petites règles partagées par l'API.</summary>
public static class AppRules
{
    public static bool ValidMinutes(int m) => RoomRules.AllDurations.Contains(m);
}
