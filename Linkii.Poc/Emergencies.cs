using Linkii.Poc.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Linkii.Poc;

/// <summary>
/// Une urgence (intégration « Alerte d'urgence », §159) : un message plein écran rouge, affiché par tous les écrans visés jusqu'à ce qu'on la lève
/// (back-office ou API). Pas d'expiration, sauf l'essai (un seul écran, 30 secondes). L'historique est gardé un an.
/// </summary>
public class Emergency
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    /// <summary>all (tous les écrans), areas, zones ou screens : <see cref="TargetIds"/> donne les identifiants.</summary>
    public string TargetKind { get; set; } = "all";
    public List<Guid> TargetIds { get; set; } = new();
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; set; }
    /// <summary>Qui l'a déclenchée : le nom de la personne, ou la « source » donnée par l'API.</summary>
    public string StartedBy { get; set; } = "";
    public Guid? StartedByUser { get; set; }
    /// <summary>backoffice ou api.</summary>
    public string Via { get; set; } = "backoffice";
    public Guid? KeyId { get; set; }
    public string? EndedBy { get; set; }
    /// <summary>Essai : un seul écran, bandeau « ESSAI », s'arrête seul après <see cref="Emergencies.TestSeconds"/> secondes.</summary>
    public bool Test { get; set; }
    /// <summary>Écrans qui dormaient quand elle a été déclenchée : ils sont réveillés (le rapport le dit).</summary>
    public List<Guid> AsleepAtStart { get; set; } = new();
    /// <summary>Rappel « active depuis 12 h » déjà envoyé.</summary>
    public DateTime? ReminderUtc { get; set; }

    public bool IsActive(DateTime now) => EndedUtc == null && (!Test || now < StartedUtc.AddSeconds(Emergencies.TestSeconds));
}

/// <summary>Modèle de message d'urgence : un nom court (le bouton) et le titre et le message qu'il place dans le formulaire.</summary>
public class EmergencyTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
}

public record EmergencyLine(Guid ScreenId, string Name, string State, bool Woke, DateTime? AtUtc);
public record EmergencyReport(Emergency Emergency, List<EmergencyLine> Lines)
{
    public int Targeted => Lines.Count;
    public int Displayed => Lines.Count(l => l.State == "displayed");
    public int Waking => Lines.Count(l => l.State == "waking");
    public int Pending => Lines.Count(l => l.State == "pending");
    public int Offline => Lines.Count(l => l.State == "offline");
}

/// <summary>Règles d'une urgence : qui la voit, dans quel état est chaque écran, ce que reçoit le player.</summary>
public static class Emergencies
{
    public const string AppId = "emergency";
    public const int TestSeconds = 30, ReminderHours = 12, KeepDays = 365, MaxTitle = 40, MaxMessage = 300, WakeMinutes = 3;
    public const int MaxTemplates = 10, MaxTemplateName = 24;

    /// <summary>Modèles de base, modifiables : une ébauche pour démarrer.</summary>
    public static List<EmergencyTemplate> DefaultTemplates() => new()
    {
        new() { Name = "Évacuation", Title = "Évacuation", Message = "Évacuation du bâtiment. Quittez les lieux calmement par les issues de secours et rejoignez le point de rassemblement." },
        new() { Name = "Confinement", Title = "Confinement", Message = "Restez à l'intérieur, fermez portes et fenêtres, éloignez-vous des vitres et attendez les consignes." },
        new() { Name = "Alerte météo", Title = "Alerte météo", Message = "Alerte tempête. Restez à l'abri, éloignez-vous des vitres et ne sortez pas jusqu'à nouvel ordre." },
        new() { Name = "Exercice", Title = "EXERCICE", Message = "EXERCICE : ceci est un exercice d'évacuation, suivez la procédure habituelle." },
    };

    public static readonly string[] Kinds = { "all", "areas", "zones", "screens" };

    /// <summary>L'intégration est utilisable : plan Pro, espace actif, et « Alerte d'urgence » installée dans le Store.</summary>
    public static bool Enabled(Db db, Tenant t) => t.IsGrowth && !t.IsCut && db.AppInstalls.Any(a => a.ClientId == t.Id && a.AppId == AppId);

    public static bool Matches(Emergency e, Screen s) => e.TargetKind switch
    {
        "areas" => e.TargetIds.Contains(s.AreaId),
        "zones" => s.ZoneId is { } z && e.TargetIds.Contains(z),
        "screens" => e.TargetIds.Contains(s.Id),
        _ => true,
    };

    /// <summary>Écrans appairés visés (un écran jamais appairé n'a personne devant).</summary>
    public static List<Screen> Targets(Db db, Guid clientId, Emergency e) => db.Screens.Where(s => s.ClientId == clientId && s.Token != null && Matches(e, s)).ToList();

    /// <summary>L'urgence que cet écran doit afficher : la plus récente de celles qui sont en cours et le visent.</summary>
    public static Emergency? ActiveFor(Db db, Screen s, DateTime now)
    {
        var t = db.Clients.FirstOrDefault(c => c.Id == s.ClientId);
        if (t == null || !Enabled(db, t)) return null;
        return t.Emergencies.Where(e => e.IsActive(now) && Matches(e, s)).OrderByDescending(e => e.StartedUtc).FirstOrDefault();
    }

    /// <summary>Ce que reçoit le player (réponse de /api/player/version et message temps réel) ; null : rien à afficher.</summary>
    public static object? PayloadFor(Db db, Screen s, DateTime now) => ActiveFor(db, s, now) is { } e ? Payload(e) : null;

    public static object Payload(Emergency e) => new
    {
        id = e.Id, title = e.Title, message = e.Message, startedAt = Helpers.EpochMs(e.StartedUtc), test = e.Test,
        until = e.Test ? Helpers.EpochMs(e.StartedUtc.AddSeconds(TestSeconds)) : (long?)null,
    };

    /// <summary>Réveil à demander à l'écran tant qu'une urgence le vise (l'application Android le sonde chaque minute pendant la veille matérielle).</summary>
    public static long WakeUntilMs(Db db, Screen s, DateTime now) => ActiveFor(db, s, now) != null ? Helpers.EpochMs(now.AddMinutes(WakeMinutes)) : 0L;

    /// <summary>État d'un écran visé : displayed (confirmé), waking (veille, réveil en cours), pending (en ligne, pas encore confirmé), offline.</summary>
    public static (string State, bool Woke) StateOf(Db db, Tenant t, Emergency e, Screen s, DateTime now)
    {
        var woke = e.AsleepAtStart.Contains(s.Id);
        if (s.EmergencyAckId == e.Id) return ("displayed", woke);
        if (ScreenControl.Online(s, now)) return ("pending", woke);
        if (woke && now - e.StartedUtc < TimeSpan.FromMinutes(WakeMinutes)) return ("waking", true);
        return ("offline", woke);
    }

    public static EmergencyReport ReportOf(Db db, Tenant t, Emergency e, DateTime now) =>
        new(e, Targets(db, t.Id, e).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(s =>
        {
            var (state, woke) = StateOf(db, t, e, s, now);
            return new EmergencyLine(s.Id, s.Name, state, woke, s.EmergencyAckId == e.Id ? s.EmergencyAckUtc : null);
        }).ToList());

    /// <summary>Met fin à toutes les urgences en cours (intégration retirée).</summary>
    public static void EndAll(Tenant t, DateTime now, string by)
    {
        foreach (var e in t.Emergencies.Where(e => e.EndedUtc == null)) { e.EndedUtc = now; e.EndedBy = by; }
    }

    public static string Duration(Emergency e, DateTime now)
    {
        var d = (e.EndedUtc ?? now) - e.StartedUtc;
        return d.TotalMinutes < 1 ? "moins d'une minute" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min" : d.TotalDays < 1 ? $"{(int)d.TotalHours} h {d.Minutes:00}" : $"{(int)d.TotalDays} j {d.Hours} h";
    }
}

public record EmergencyRequest(string? Title, string? Message, string TargetKind, List<Guid> TargetIds, bool Test, string Via, string By, Guid? ByUser, Guid? KeyId);
public record EmergencyResult(Emergency? Emergency, string? Error, string? Code);

/// <summary>Message temps réel vers un écran (SignalR) : le player affiche l'urgence ou la retire sans attendre son sondage de 5 s.</summary>
public interface IEmergencyPush
{
    Task Push(Guid screenId, object? payload);
}

public class HubEmergencyPush(IHubContext<ScreenHub> hub) : IEmergencyPush
{
    public Task Push(Guid screenId, object? payload) => hub.Clients.Group(ScreenHub.Group(screenId)).SendAsync("Emergency", payload);
}

/// <summary>Ce que l'urgence signale aux administrateurs : centre de notifications, et e-mail (rappel de 12 h : toujours, en plus du centre).</summary>
public interface IEmergencyNotices
{
    Task Started(Guid clientId, Emergency e, int screens);
    Task Ended(Guid clientId, Emergency e);
    Task Reminder(Guid clientId, Emergency e);
}

public class EmergencyNotices(AlertService alerts, TimeProvider time) : IEmergencyNotices
{
    public Task Started(Guid clientId, Emergency e, int screens) =>
        alerts.Raise(clientId, Alerts.Integrations, $"Urgence déclenchée : {e.Title}", $"Par {e.StartedBy} · {screens} écran{(screens > 1 ? "s" : "")}.", "/", except: e.StartedByUser, severity: Notifs.Error, dedupe: "emergency:" + e.Id);

    public Task Ended(Guid clientId, Emergency e) =>
        alerts.Raise(clientId, Alerts.Integrations, $"Urgence levée : {e.Title}", $"Par {e.EndedBy} · durée {Emergencies.Duration(e, time.GetUtcNow().UtcDateTime)}.", "/", severity: Notifs.Ok, dedupe: "emergency-end:" + e.Id);

    public Task Reminder(Guid clientId, Emergency e) =>
        alerts.Raise(clientId, Alerts.Integrations, $"Urgence active depuis {Emergencies.ReminderHours} h : {e.Title}", "Elle est toujours affichée sur les écrans. Levez-la si elle est terminée.", "/",
            severity: Notifs.Warn, dedupe: "emergency-reminder:" + e.Id, force: true);
}

/// <summary>Déclenche, lève et suit les urgences. Le back-office et l'API passent par ici.</summary>
public class EmergencyService(JsonStore store, IEmergencyPush push, IEventPublisher events, IEmergencyNotices notices, TimeProvider time, ILogger<EmergencyService> log)
{
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Une urgence a changé (déclenchée, levée, confirmée) : le bandeau du back-office se met à jour. Paramètre : l'organisation.</summary>
    public event Action<Guid>? Changed;

    public const string NotInstalled = "not_installed", PlanRequired = "plan_required", Invalid = "invalid_request", Suspended = "space_suspended";

    private static EmergencyResult Fail(string code, string error) => new(null, error, code);

    public EmergencyResult Trigger(Guid clientId, EmergencyRequest r)
    {
        var title = (r.Title ?? "").Trim();
        var message = (r.Message ?? "").Trim();
        if (title.Length == 0) return Fail(Invalid, "Donnez un titre court à l'urgence.");
        if (title.Length > Emergencies.MaxTitle) return Fail(Invalid, $"Le titre est trop long ({Emergencies.MaxTitle} caractères au plus).");
        if (message.Length == 0) return Fail(Invalid, "Écrivez le message à afficher.");
        if (message.Length > Emergencies.MaxMessage) return Fail(Invalid, $"Le message est trop long ({Emergencies.MaxMessage} caractères au plus).");
        if (!Emergencies.Kinds.Contains(r.TargetKind)) return Fail(Invalid, "Cibles inconnues : all, areas, zones ou screens.");
        var now = Now;
        Emergency? created = null; List<Guid> targets = new(); string? error = null, code = null;
        store.Write(db =>
        {
            var t = db.Clients.FirstOrDefault(c => c.Id == clientId);
            if (t == null) { error = "Organisation introuvable."; code = Invalid; return; }
            if (t.IsCut) { error = "L'espace de cette organisation est suspendu."; code = Suspended; return; }
            if (!t.IsGrowth) { error = "L'alerte d'urgence fait partie du plan Pro."; code = PlanRequired; return; }
            if (!Emergencies.Enabled(db, t)) { error = "Installez l'intégration « Alerte d'urgence » dans le Store."; code = NotInstalled; return; }
            var ids = r.TargetIds.Distinct().ToList();
            if (r.TargetKind == "areas" && ids.Any(i => !db.Areas.Any(a => a.Id == i && a.ClientId == clientId))) { error = "Aire inconnue."; code = Invalid; return; }
            if (r.TargetKind == "zones" && ids.Any(i => !db.Areas.Where(a => a.ClientId == clientId).SelectMany(a => a.Zones).Any(z => z.Id == i))) { error = "Zone inconnue."; code = Invalid; return; }
            if (r.TargetKind == "screens" && ids.Any(i => !db.Screens.Any(s => s.Id == i && s.ClientId == clientId && s.Token != null))) { error = "Écran inconnu ou non appairé."; code = Invalid; return; }
            if (r.TargetKind != "all" && ids.Count == 0) { error = "Choisissez au moins une cible."; code = Invalid; return; }
            if (r.Test && (r.TargetKind != "screens" || ids.Count != 1)) { error = "Un essai se fait sur un seul écran."; code = Invalid; return; }
            var e = new Emergency
            {
                Title = title, Message = message, TargetKind = r.TargetKind, TargetIds = r.TargetKind == "all" ? new() : ids, StartedUtc = now, StartedBy = r.By, StartedByUser = r.ByUser,
                Via = r.Via, KeyId = r.KeyId, Test = r.Test
            };
            var screens = Emergencies.Targets(db, clientId, e);
            if (screens.Count == 0) { error = "Aucun écran appairé n'est concerné."; code = Invalid; return; }
            e.AsleepAtStart = screens.Where(s => Sleep.State(t, db.Areas.FirstOrDefault(a => a.Id == s.AreaId), s, Helpers.Wall(db.Areas, s) != null, now).Asleep).Select(s => s.Id).ToList();
            t.Emergencies.Add(e);
            created = e; targets = screens.Select(s => s.Id).ToList();
        });
        if (created == null) return Fail(code ?? Invalid, error ?? "Urgence refusée.");
        Deliver(clientId, targets);
        if (!created.Test)
        {
            events.Publish(clientId, "emergency.started", new { emergency = Brief(created), targeted = targets.Count });
            Notify(() => notices.Started(clientId, created, targets.Count));
        }
        Changed?.Invoke(clientId);
        return new EmergencyResult(created, null, null);
    }

    /// <summary>Modèles de l'organisation. À la première lecture, les modèles de base sont enregistrés pour qu'on puisse les modifier.</summary>
    public List<EmergencyTemplate> Templates(Guid clientId)
    {
        var list = store.Read(db => db.Clients.FirstOrDefault(c => c.Id == clientId)?.EmergencyTemplates?.Select(Copy).ToList());
        if (list != null) return list;
        return store.Write(db =>
        {
            var t = db.Clients.FirstOrDefault(c => c.Id == clientId);
            return t == null ? new() : (t.EmergencyTemplates ??= Emergencies.DefaultTemplates()).Select(Copy).ToList();
        });
    }

    private static EmergencyTemplate Copy(EmergencyTemplate x) => new() { Id = x.Id, Name = x.Name, Title = x.Title, Message = x.Message };

    /// <summary>Ajoute (id vide) ou modifie un modèle. Rend le message d'erreur, ou null si tout va bien.</summary>
    public string? SaveTemplate(Guid clientId, Guid? id, string? name, string? title, string? message)
    {
        name = (name ?? "").Trim(); title = (title ?? "").Trim(); message = (message ?? "").Trim();
        if (name.Length == 0 || name.Length > Emergencies.MaxTemplateName) return $"Donnez un nom au modèle ({Emergencies.MaxTemplateName} caractères au plus).";
        if (title.Length == 0 || title.Length > Emergencies.MaxTitle) return $"Le titre doit faire de 1 à {Emergencies.MaxTitle} caractères.";
        if (message.Length == 0 || message.Length > Emergencies.MaxMessage) return $"Le message doit faire de 1 à {Emergencies.MaxMessage} caractères.";
        string? error = null;
        store.Write(db =>
        {
            var t = db.Clients.FirstOrDefault(c => c.Id == clientId);
            if (t == null) { error = "Organisation introuvable."; return; }
            var list = t.EmergencyTemplates ??= Emergencies.DefaultTemplates();
            var m = id is { } i ? list.FirstOrDefault(x => x.Id == i) : null;
            if (m == null)
            {
                if (list.Count >= Emergencies.MaxTemplates) { error = $"{Emergencies.MaxTemplates} modèles au plus."; return; }
                list.Add(m = new EmergencyTemplate());
            }
            m.Name = name; m.Title = title; m.Message = message;
        });
        return error;
    }

    public void DeleteTemplate(Guid clientId, Guid id) => store.Write(db =>
    {
        var t = db.Clients.FirstOrDefault(c => c.Id == clientId);
        if (t == null) return;
        (t.EmergencyTemplates ??= Emergencies.DefaultTemplates()).RemoveAll(x => x.Id == id);
    });

    /// <summary>Lève une urgence en cours. Rend false si elle n'existe pas ou n'est plus en cours.</summary>
    public bool Lift(Guid clientId, Guid id, string by)
    {
        Emergency? e = null; List<Guid> targets = new();
        var now = Now;
        store.Write(db =>
        {
            var t = db.Clients.FirstOrDefault(c => c.Id == clientId);
            e = t?.Emergencies.FirstOrDefault(x => x.Id == id && x.EndedUtc == null);
            if (t == null || e == null) return;
            e.EndedUtc = now; e.EndedBy = by;
            targets = Emergencies.Targets(db, clientId, e).Select(s => s.Id).ToList();
        });
        if (e == null) return false;
        Deliver(clientId, targets);   // chaque écran reçoit ce qu'il doit afficher maintenant : l'urgence précédente qui le vise, ou rien
        if (!e.Test)
        {
            events.Publish(clientId, "emergency.ended", new { emergency = Brief(e), endedBy = by, durationMinutes = (int)Math.Max(0, (now - e.StartedUtc).TotalMinutes) });
            Notify(() => notices.Ended(clientId, e));
        }
        Changed?.Invoke(clientId);
        return true;
    }

    private object Brief(Emergency e) => new { id = e.Id, title = e.Title, message = e.Message, startedAt = e.StartedUtc, test = e.Test, startedBy = e.StartedBy, via = e.Via };

    private Task pending = Task.CompletedTask;
    private readonly object pendingLock = new();
    private void Track(Task t) { lock (pendingLock) pending = Task.WhenAll(pending, t); }
    /// <summary>Attend les envois en cours (messages aux écrans, notifications) : sert aux tests.</summary>
    public Task Idle() { lock (pendingLock) return pending; }

    private void Notify(Func<Task> send) => Track(Task.Run(async () => { try { await send(); } catch (Exception ex) { log.LogWarning(ex, "Notification d'urgence non envoyée"); } }));

    /// <summary>Envoie à chaque écran, tout de suite, ce qu'il doit afficher (SignalR). Le sondage de 5 s du player est le filet.</summary>
    public void Deliver(Guid clientId, IEnumerable<Guid> screenIds)
    {
        var now = Now;
        var items = store.Read(db => screenIds.Select(id => db.Screens.FirstOrDefault(s => s.Id == id) is { } s ? (id, Emergencies.PayloadFor(db, s, now)) : (id, (object?)null)).ToList());
        Track(Task.Run(async () =>
        {
            foreach (var (id, payload) in items)
                try { await push.Push(id, payload); } catch (Exception ex) { log.LogWarning(ex, "Urgence non poussée à l'écran {Id}", id); }
        }));
    }

    /// <summary>Un écran confirme afficher l'urgence (rapport de réception).</summary>
    public void Ack(Guid screenId, Guid emergencyId)
    {
        var now = Now;
        var evt = store.Write(db =>
        {
            var s = db.Screens.FirstOrDefault(x => x.Id == screenId);
            var e = s == null ? null : db.Clients.FirstOrDefault(c => c.Id == s.ClientId)?.Emergencies.FirstOrDefault(x => x.Id == emergencyId && x.EndedUtc == null);
            if (s == null || e == null || !Emergencies.Matches(e, s) || s.EmergencyAckId == emergencyId) return ((Guid, object)?)null;
            s.EmergencyAckId = emergencyId; s.EmergencyAckUtc = now;
            return e.Test ? null : (s.ClientId, new { emergencyId, screen = ScreenEvents.Brief(db, s), at = now, woke = e.AsleepAtStart.Contains(s.Id) });
        });
        if (evt is { } v) events.Publish(v.Item1, "emergency.acknowledged", v.Item2);
        Changed?.Invoke(store.Read(db => db.Screens.FirstOrDefault(x => x.Id == screenId)?.ClientId ?? Guid.Empty));
    }

    /// <summary>Ce que montrent le bouton, la pastille et le bandeau du back-office.</summary>
    public EmergencySnapshot Snapshot(Guid clientId)
    {
        var now = Now;
        return store.Read(db =>
        {
            var t = db.Clients.FirstOrDefault(c => c.Id == clientId);
            if (t == null || !Emergencies.Enabled(db, t)) return EmergencySnapshot.None;
            var active = t.Emergencies.Where(e => e.IsActive(now) && !e.Test).OrderByDescending(e => e.StartedUtc).ToList();
            if (active.Count == 0) return new EmergencySnapshot(true, null, 0, 0, 0);
            var r = Emergencies.ReportOf(db, t, active[0], now);
            return new EmergencySnapshot(true, active[0], active.Count, r.Displayed, r.Targeted);
        });
    }

    public Emergency? Get(Guid clientId, Guid id) => store.Read(db => db.Clients.FirstOrDefault(c => c.Id == clientId)?.Emergencies.FirstOrDefault(e => e.Id == id));

    /// <summary>Urgences en cours (essais compris), la plus récente d'abord.</summary>
    public List<Emergency> Active(Guid clientId)
    {
        var now = Now;
        return store.Read(db => db.Clients.FirstOrDefault(c => c.Id == clientId)?.Emergencies.Where(e => e.IsActive(now)).OrderByDescending(e => e.StartedUtc).ToList() ?? new());
    }

    public List<Emergency> History(Guid clientId, int limit = 50) =>
        store.Read(db => db.Clients.FirstOrDefault(c => c.Id == clientId)?.Emergencies.Where(e => !e.Test).OrderByDescending(e => e.StartedUtc).Take(limit).ToList() ?? new());

    public EmergencyReport? Report(Guid clientId, Guid id)
    {
        var now = Now;
        return store.Read(db => db.Clients.FirstOrDefault(c => c.Id == clientId) is { } t && t.Emergencies.FirstOrDefault(e => e.Id == id) is { } e ? Emergencies.ReportOf(db, t, e, now) : null);
    }

    /// <summary>Essais finis, rappels de 12 h, historique de plus d'un an. Appelé toutes les 15 secondes.</summary>
    public int Tick()
    {
        var now = Now;
        var remind = new List<(Guid, Emergency)>();
        var ended = new List<Guid>();
        store.Write(db =>
        {
            foreach (var t in db.Clients.Where(c => c.Emergencies.Count > 0))
            {
                foreach (var e in t.Emergencies.Where(e => e.Test && e.EndedUtc == null && now >= e.StartedUtc.AddSeconds(Emergencies.TestSeconds))) { e.EndedUtc = e.StartedUtc.AddSeconds(Emergencies.TestSeconds); e.EndedBy = "fin de l'essai"; ended.Add(t.Id); }
                foreach (var e in t.Emergencies.Where(e => !e.Test && e.EndedUtc == null && e.ReminderUtc == null && now - e.StartedUtc >= TimeSpan.FromHours(Emergencies.ReminderHours))) { e.ReminderUtc = now; remind.Add((t.Id, e)); }
                t.Emergencies.RemoveAll(e => e.EndedUtc is { } end && now - end > TimeSpan.FromDays(Emergencies.KeepDays));
            }
        });
        foreach (var (cid, e) in remind) Notify(() => notices.Reminder(cid, e));
        foreach (var cid in ended.Distinct()) Changed?.Invoke(cid);
        return remind.Count + ended.Count;
    }
}

/// <summary>Toutes les 15 secondes : fin des essais, rappels des urgences actives depuis 12 h, purge de l'historique.</summary>
public class EmergencyWorker(EmergencyService svc, ILogger<EmergencyWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try { svc.Tick(); }
            catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogWarning(ex, "Urgences : entretien en erreur"); }
        } while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
    }
}
