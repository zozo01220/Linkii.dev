using Linkii.Poc.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Linkii.Poc;

public record CommandResult(bool Ok, string? Error = null, Guid? Id = null);

/// <summary>
/// Contrôle du direct (licence Growth) : le back-office envoie une commande à un écran (pause, reprise, redémarrage, rechargement, aperçu),
/// par SignalR et, en secours, dans la réponse du sondage du player. L'écran accuse réception puis exécution
/// (<see cref="ScreenCommand.Status"/>) ; l'état (pause en cours, dernier aperçu) est gardé sur l'écran pour le back-office.
/// </summary>
public class ScreenControl(JsonStore store, IHubContext<ScreenHub> hub)
{
    /// <summary>Une commande sans accusé après ce délai est déclarée en échec.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    public const int MaxKept = 20;
    public const int MaxPauseMinutes = 24 * 60;

    public static bool Online(Screen s, DateTime now) => s.LastSeenUtc is { } t && now - t <= TimeSpan.FromSeconds(90);

    /// <summary>
    /// Envoie une commande à un écran de l'organisation. minutes : durée d'une pause (0 : reprise manuelle seulement).
    /// Refusée si l'organisation n'a pas la licence Growth, si l'écran est hors ligne ou si une commande identique est déjà en cours.
    /// </summary>
    public async Task<CommandResult> Send(Guid clientId, Guid screenId, string kind, string by, int minutes = 0)
    {
        var now = DateTime.UtcNow;
        var r = store.Write<(string? Error, ScreenCommand? Cmd)>(db =>
        {
            var s = db.Screens.FirstOrDefault(x => x.Id == screenId && x.ClientId == clientId && x.Token != null);
            var t = db.Clients.FirstOrDefault(c => c.Id == clientId);
            if (s == null || t == null) return ("Écran introuvable.", null);
            if (!t.IsGrowth) return (Plans.LockedHint + ".", null);
            if (!Online(s, now)) return ("L'écran est hors ligne : la commande ne peut pas lui parvenir.", null);
            Expire(s, now);
            if (kind == ScreenCommand.Capture && s.Commands.Any(c => c.Kind == kind && c.Open))
                return ("Un aperçu est déjà en cours.", null);
            if (kind == ScreenCommand.Capture && s.Commands.LastOrDefault(c => c.Kind == kind) is { } last && now - last.SentUtc < TimeSpan.FromSeconds(3))
                return ("Patientez quelques secondes avant un nouvel aperçu.", null);
            if (kind is ScreenCommand.Pause or ScreenCommand.Resume && s.Commands.Any(c => c.Kind is ScreenCommand.Pause or ScreenCommand.Resume && c.Open))
                return ("Une commande de pause est déjà en cours.", null);
            if (kind == ScreenCommand.Restart && s.Commands.Any(c => c.Kind == kind && c.Open))
                return ("Un redémarrage est déjà en cours.", null);

            var cmd = new ScreenCommand { Kind = kind, By = by, SentUtc = now };
            switch (kind)
            {
                case ScreenCommand.Pause:
                    minutes = Math.Clamp(minutes, 0, MaxPauseMinutes);
                    DateTime? until = minutes > 0 ? now.AddMinutes(minutes) : null;
                    cmd.Args["until"] = until == null ? "0" : Helpers.EpochMs(until.Value).ToString();
                    cmd.Detail = minutes == 0 ? "reprise manuelle" : Duration(minutes);
                    if (PauseImage(db, t) is { } img) cmd.Args["image"] = img;
                    s.PausedUtc = now; s.PausedUntilUtc = until;
                    break;
                case ScreenCommand.Resume:
                    s.PausedUtc = null; s.PausedUntilUtc = null;
                    break;
                case ScreenCommand.Restart or ScreenCommand.Reload or ScreenCommand.Capture:
                    break;
                default:
                    return ("Commande inconnue.", null);
            }
            s.Commands.Add(cmd);
            if (s.Commands.Count > MaxKept) s.Commands.RemoveRange(0, s.Commands.Count - MaxKept);
            return (null, cmd);
        });
        if (r.Cmd is not { } c) return new(false, r.Error);
        await hub.Clients.Group(ScreenHub.Group(screenId)).SendAsync("Command", Payload(c));
        return new(true, null, c.Id);
    }

    public static object Payload(ScreenCommand c) => new { id = c.Id, kind = c.Kind, args = c.Args };

    /// <summary>Commandes à remettre à l'écran par le sondage (filet si SignalR est coupé) : envoyées sans accusé, récentes.</summary>
    public static List<object> Pending(Screen s, DateTime now) =>
        s.Commands.Where(c => c.Status == ScreenCommand.Sent && now - c.SentUtc < Timeout).Select(Payload).ToList();

    /// <summary>Accusé de l'écran : reçue, exécutée ou en échec. L'état ne recule jamais.</summary>
    public static bool Ack(Screen s, Guid id, string status, string? detail)
    {
        var c = s.Commands.FirstOrDefault(x => x.Id == id);
        if (c == null || status is not (ScreenCommand.Received or ScreenCommand.Done or ScreenCommand.Failed)) return false;
        if (!c.Open || c.Status == ScreenCommand.Received && status == ScreenCommand.Received) return true;   // déjà terminée, ou déjà accusée
        c.Status = status;
        if (!string.IsNullOrWhiteSpace(detail)) c.Detail = detail.Length > 200 ? detail[..200] : detail;
        if (status == ScreenCommand.Failed && c.Kind == ScreenCommand.Pause) { s.PausedUtc = null; s.PausedUntilUtc = null; }   // la pause n'a pas eu lieu
        return true;
    }

    /// <summary>L'écran se reconnecte après un redémarrage demandé : la commande est exécutée.</summary>
    public static void CompleteRestarts(Screen s, DateTime now)
    {
        foreach (var c in s.Commands.Where(c => c.Kind == ScreenCommand.Restart && c.Open && now - c.SentUtc < TimeSpan.FromMinutes(10)))
            if (c.Status == ScreenCommand.Received) c.Status = ScreenCommand.Done;
    }

    /// <summary>Commandes restées sans réponse : en échec. Pause terminée : état effacé.</summary>
    public static void Expire(Screen s, DateTime now)
    {
        foreach (var c in s.Commands.Where(c => c.Open && now - c.SentUtc > Timeout))
        {
            c.Detail = c.Status == ScreenCommand.Received ? "sans suite" : "l'écran n'a pas répondu";
            c.Status = ScreenCommand.Failed;
            if (c.Kind == ScreenCommand.Pause) { s.PausedUtc = null; s.PausedUntilUtc = null; }
        }
        if (s.PausedUtc != null && s.PausedUntilUtc is { } u && u <= now) { s.PausedUtc = null; s.PausedUntilUtc = null; }
    }

    /// <summary>Aperçu reçu de l'écran : image gardée (la dernière seulement) avec l'état du miroir à cet instant ; la commande est exécutée.</summary>
    public static bool SaveCapture(Db db, Screen s, byte[] jpeg, Guid? cmd, DateTime now)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return false;   // pas un JPEG
        Directory.CreateDirectory(AppPaths.CaptureDir);
        File.WriteAllBytes(CapturePath(s.Id), jpeg);
        s.CaptureUtc = now;
        (s.CaptureW, s.CaptureH) = JpegSize(jpeg);
        (s.CaptureMirrorH, s.CaptureMirrorV) = Helpers.Mirror(db.Areas, s);
        if (cmd != null && s.Commands.FirstOrDefault(c => c.Id == cmd) is { } c) { c.Status = ScreenCommand.Done; }
        return true;
    }

    public static string CapturePath(Guid screenId) => Path.Combine(AppPaths.CaptureDir, screenId.ToString("N") + ".jpg");

    /// <summary>Largeur et hauteur d'un JPEG (marqueur SOF), (0, 0) s'il est illisible.</summary>
    public static (int W, int H) JpegSize(byte[] b)
    {
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var m = b[i + 1];
            if (m is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7 or 0xFF) { i += m == 0xFF ? 1 : 2; continue; }
            var len = b[i + 2] << 8 | b[i + 3];
            if (m is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) return (b[i + 7] << 8 | b[i + 8], b[i + 5] << 8 | b[i + 6]);
            i += 2 + len;
        }
        return (0, 0);
    }

    /// <summary>
    /// Degrés dont tourner l'aperçu pour le voir dans le sens configuré de l'écran : le player pivote sa scène quand l'appareil est tenu
    /// dans l'autre sens (téléphone en paysage pour un écran portrait), et la capture montre l'appareil tel qu'il est.
    /// </summary>
    public static int CaptureTurn(Screen s)
    {
        if (s.CaptureW <= 0 || s.CaptureH <= 0) return 0;
        var captured = s.CaptureH > s.CaptureW;
        var wanted = s.Orientation == "portrait";
        return captured == wanted ? 0 : wanted ? -90 : 90;
    }

    private static string? PauseImage(Db db, Tenant t) =>
        t.PauseMediaId is { } id && db.Media.FirstOrDefault(m => m.Id == id && m.ClientId == t.Id && m.Type == "image" && m.FileName != null) is { } m ? "/media/" + m.FileName : null;

    public static string Duration(int minutes) => minutes % 60 == 0 && minutes >= 60 ? (minutes / 60 == 1 ? "1 heure" : $"{minutes / 60} heures") : $"{minutes} minutes";
}

/// <summary>Une alerte « écrans hors ligne » (ou « de retour ») pour une organisation.</summary>
public record AlertBatch(Tenant Client, List<Screen> Down, List<Screen> Back);

public static class ScreenAlerts
{
    /// <summary>
    /// Écrans à signaler : hors ligne depuis plus que le délai choisi (une seule alerte par panne), ou revenus après une alerte.
    /// Pendant le délai qui suit le démarrage du serveur, rien n'est signalé : les écrans n'ont pas encore eu le temps de se reconnecter.
    /// Marque les écrans (<see cref="Screen.OfflineAlertUtc"/>) : à appeler dans un Write.
    /// </summary>
    public static List<AlertBatch> Plan(Db db, DateTime now, DateTime startedUtc)
    {
        var batches = new List<AlertBatch>();
        foreach (var c in db.Clients.Where(c => c.IsGrowth && c.AlertOffline && !c.Suspended))
        {
            var delay = TimeSpan.FromMinutes(Math.Clamp(c.AlertOfflineMinutes, 1, 1440));
            if (now - startedUtc < delay) continue;
            var screens = db.Screens.Where(s => s.ClientId == c.Id && s.Token != null && s.LastSeenUtc != null).ToList();
            var down = screens.Where(s => s.OfflineAlertUtc == null && now - s.LastSeenUtc > delay).ToList();
            var back = screens.Where(s => s.OfflineAlertUtc != null && ScreenControl.Online(s, now)).ToList();
            foreach (var s in down) s.OfflineAlertUtc = now;
            foreach (var s in back) s.OfflineAlertUtc = null;
            if (down.Count + back.Count > 0) batches.Add(new(c, down, back));
        }
        return batches;
    }

    /// <summary>Destinataires : l'adresse choisie, sinon les administrateurs de l'organisation.</summary>
    public static List<(string Email, string Name)> Recipients(Db db, Tenant c) =>
        System.Net.Mail.MailAddress.TryCreate(c.AlertEmail?.Trim(), out var one)
            ? new() { (one.Address, "") }
            : db.Users.Where(u => u.ClientId == c.Id && u.Role == Roles.ClientAdmin && !u.Disabled && !u.AwaitsVerification && u.Email.Contains('@'))
                .Select(u => (u.Email, u.Name)).ToList();
}

/// <summary>Toutes les minutes : commandes sans réponse, pauses terminées, et e-mail quand un écran tombe (ou revient) si l'organisation l'a activé.</summary>
public class ScreenWatchWorker(JsonStore store, Mailer mail, ResellerResolver resolver, ILogger<ScreenWatchWorker> log) : BackgroundService
{
    private readonly DateTime started = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await Run(DateTime.UtcNow); }
            catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogError(ex, "Supervision des écrans"); }
        } while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
    }

    public async Task Run(DateTime now)
    {
        var work = store.Write(db =>
        {
            foreach (var s in db.Screens.Where(s => s.Commands.Count > 0 || s.PausedUtc != null)) ScreenControl.Expire(s, now);
            return ScreenAlerts.Plan(db, now, started).Select(b => (b, Rcpt: ScreenAlerts.Recipients(db, b.Client),
                Reseller: db.Resellers.FirstOrDefault(r => r.Id == b.Client.ResellerId))).ToList();
        });
        foreach (var (b, rcpt, reseller) in work)
        {
            if (reseller == null) continue;
            var app = resolver.LoginUrl(reseller)[..^"login".Length];
            var down = b.Down.Select(s => (s.Name, Since: Local(b.Client, s.LastSeenUtc ?? now))).ToList();
            var back = b.Back.Select(s => s.Name).ToList();
            foreach (var (email, name) in rcpt)
            {
                var err = await mail.SendScreenAlert(reseller, app, b.Client.Name, name, email, down, back, b.Client.AlertOfflineMinutes);
                if (err != null) log.LogWarning("Alerte d'écran non envoyée à {Email} : {Error}", email, err);
            }
        }
    }

    private static string Local(Tenant c, DateTime utc)
    {
        try { utc = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById(c.Timezone)); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        return utc.ToString("HH:mm");
    }
}
