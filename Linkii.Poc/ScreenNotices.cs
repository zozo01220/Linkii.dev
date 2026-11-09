using Linkii.Poc.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Linkii.Poc;

/// <summary>Message affiché un moment sur un écran, par-dessus la diffusion (demandé de l'extérieur : API, Home Assistant). Un seul à la fois par écran : le plus récent remplace l'autre.</summary>
public class ScreenNotice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string Look { get; set; } = Notices.Information;
    public DateTime StartedUtc { get; set; }
    public DateTime UntilUtc { get; set; }
    /// <summary>L'écran a été réveillé pour l'occasion (réveil manuel jusqu'à la fin du message).</summary>
    public bool Wake { get; set; }
    public string By { get; set; } = "";
}

public static class Notices
{
    public const string Information = "information", Attention = "attention";
    public static readonly string[] Looks = [Information, Attention];
    public const int MinSeconds = 5, MaxSeconds = 3600, DefaultSeconds = 30, MaxTitle = 60, MaxMessage = 300;

    public static ScreenNotice? Active(Screen s, DateTime now) => s.Notice is { } n && n.UntilUtc > now ? n : null;

    /// <summary>Ce que reçoit le player (réponse de /api/player/version et message temps réel « Notice ») ; null : rien à afficher.</summary>
    public static object? PayloadFor(Screen s, DateTime now) => Active(s, now) is { } n ? Payload(n) : null;

    public static object Payload(ScreenNotice n) => new
    {
        id = n.Id, title = n.Title, message = n.Message, look = n.Look, wake = n.Wake,
        startedAt = Helpers.EpochMs(n.StartedUtc), until = Helpers.EpochMs(n.UntilUtc),
    };
}

public record NoticeResult(string? Error, string? Code = null)
{
    public const string Invalid = "invalid", Unavailable = "unavailable", NotFound = "notfound";
    public bool Ok => Error == null;
}

/// <summary>
/// Affiche un message sur un écran ou le retire (module « Écrans » de l'API, intégration Home Assistant). Mêmes règles d'accès que les commandes d'appareil :
/// plan Pro, écran appairé, clé qui agit avec les droits de son propriétaire. L'écran doit être en ligne (un message de sonnette qui arrive 10 minutes plus
/// tard n'a plus de sens) et éveillé, sauf si l'appelant demande de le réveiller. Le player reçoit le message en temps réel et le retire seul à l'échéance.
/// </summary>
public class NoticeService(JsonStore store, IHubContext<ScreenHub> hub, Notifier notifier, HistoryLog history)
{
    public async Task<NoticeResult> Show(Guid clientId, Guid? ownerId, Guid screenId, string? title, string? message, string? look, int? seconds, bool wake, string by)
    {
        title = (title ?? "").Trim(); message = (message ?? "").Trim(); look = string.IsNullOrWhiteSpace(look) ? Notices.Information : look.Trim().ToLowerInvariant();
        var secs = seconds ?? Notices.DefaultSeconds;
        if (title.Length == 0 && message.Length == 0) return new("Indiquez un titre ou un message.", NoticeResult.Invalid);
        if (title.Length > Notices.MaxTitle) return new($"Le titre est limité à {Notices.MaxTitle} caractères.", NoticeResult.Invalid);
        if (message.Length > Notices.MaxMessage) return new($"Le message est limité à {Notices.MaxMessage} caractères.", NoticeResult.Invalid);
        if (!Notices.Looks.Contains(look)) return new("Aspect inconnu : " + string.Join(", ", Notices.Looks) + ".", NoticeResult.Invalid);
        if (secs < Notices.MinSeconds || secs > Notices.MaxSeconds) return new($"La durée va de {Notices.MinSeconds} secondes à {Notices.MaxSeconds / 60} minutes.", NoticeResult.Invalid);

        var now = DateTime.UtcNow;
        var notice = new ScreenNotice { Title = title, Message = message, Look = look, StartedUtc = now, UntilUtc = now.AddSeconds(secs), Wake = wake, By = by };
        string? name = null;
        var res = store.Write(db =>
        {
            var t = db.Clients.FirstOrDefault(c => c.Id == clientId);
            if (t == null || !t.IsGrowth) return new NoticeResult(Plans.LockedHint + ".", NoticeResult.Unavailable);
            var s = db.Screens.FirstOrDefault(x => x.Id == screenId && x.ClientId == clientId && x.Token != null);
            if (s == null) return new NoticeResult("Écran introuvable.", NoticeResult.NotFound);
            if (new ClientDb(db, clientId, AreaAccess.For(db, clientId, ownerId)).Access.Sees?.Invoke(s) == false) return new NoticeResult("Cet écran ne vous est pas attribué.", NoticeResult.Unavailable);
            if (!ScreenControl.Online(s, now)) return new NoticeResult("L'écran est hors ligne.", NoticeResult.Unavailable);
            var area = db.Areas.FirstOrDefault(a => a.Id == s.AreaId);
            if (!wake && Sleep.State(t, area, s, Helpers.Wall(db.Areas, s) != null, now).Asleep) return new NoticeResult("L'écran est en veille : demandez de le réveiller pour afficher le message.", NoticeResult.Unavailable);
            s.Notice = notice;
            name = s.Name;
            if (wake)
            {
                s.ForcedSleep = false;
                if (s.WakeUntilUtc is not { } w || w < notice.UntilUtc) s.WakeUntilUtc = notice.UntilUtc;   // éveillé jusqu'à la fin du message ; ensuite plages et calendriers reprennent
            }
            return new NoticeResult(null);
        });
        if (!res.Ok) return res;
        if (wake) await notifier.NotifyScreen(screenId);
        await hub.Clients.Group(ScreenHub.Group(screenId)).SendAsync("Notice", Notices.Payload(notice));
        history.Record(clientId, History.Screens, History.Notice, $"Message affiché sur « {name} » : {(title.Length > 0 ? title : message)}", Notifs.Info,
            $"{secs} s · par {by}", by, targetKind: "screen", targetId: screenId, targetName: name);
        return new(null);
    }

    /// <summary>Retire le message en cours (sans effet s'il n'y en a pas). Ne demande ni plan ni écran en ligne : on peut toujours reprendre la main.</summary>
    public async Task<NoticeResult> Clear(Guid clientId, Guid? ownerId, Guid screenId, string by)
    {
        var found = store.Write(db =>
        {
            var t = db.Clients.FirstOrDefault(c => c.Id == clientId);
            var s = db.Screens.FirstOrDefault(x => x.Id == screenId && x.ClientId == clientId && x.Token != null);
            if (t == null || s == null) return false;
            if (new ClientDb(db, clientId, AreaAccess.For(db, clientId, ownerId)).Access.Sees?.Invoke(s) == false) return false;
            s.Notice = null;
            return true;
        });
        if (!found) return new("Écran introuvable.", NoticeResult.NotFound);
        await hub.Clients.Group(ScreenHub.Group(screenId)).SendAsync("Notice", (object?)null);
        return new(null);
    }
}
