namespace Linkii.Poc;

/// <summary>
/// Application LinkedIn (§255), toutes les heures : renouvelle le jeton quand LinkedIn le permet, sinon prévient l'organisation 7 jours puis 1 jour
/// avant l'échéance de la connexion (centre de notifications, téléphones et e-mail selon ses réglages d'alertes « Applications et API »),
/// puis passe l'application « à reconnecter » à l'échéance. Les écrans gardent la dernière version des publications.
/// Supprime aussi les copies de médias qui ne servent plus.
/// </summary>
public class LinkedinWorker(JsonStore store, LinkedinAuth auth, LinkedinMedia media, AlertService alerts, ILogger<LinkedinWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stop); } catch (OperationCanceledException) { return; }
        var lastPrune = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Tick(DateTime.UtcNow);
                if (DateTime.UtcNow - lastPrune > TimeSpan.FromDays(1)) { media.Prune(DateTime.UtcNow); lastPrune = DateTime.UtcNow; }
            }
            catch (Exception ex) { log.LogWarning(ex, "Rappels LinkedIn"); }
            try { await Task.Delay(TimeSpan.FromHours(1), stop); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Palier de rappel pour une connexion : 7 (une semaine), 1 (veille), 0 (expirée) ; null s'il n'y a rien à dire.</summary>
    public static int? Step(int? daysLeft) => daysLeft switch { null => null, <= 0 => 0, <= 1 => 1, <= 7 => 7, _ => null };

    public async Task Tick(DateTime now)
    {
        var clients = store.Read(db => db.Clients.Where(c => c.LinkedinConnectedUtc != null && c.LinkedinLostUtc == null && c.LinkedinToken.Length > 0).ToList());
        foreach (var c in clients)
        {
            if (LinkedinAuth.DaysLeft(c, now) is <= 7 && c.LinkedinRefresh.Length > 0) await auth.AccessToken(c);   // renouvellement si LinkedIn l'accorde
            var t = store.Read(db => db.Clients.FirstOrDefault(x => x.Id == c.Id));
            if (t == null || t.LinkedinExpiresUtc is not { } exp) continue;
            if (Step(LinkedinAuth.DaysLeft(t, now)) is not { } step) continue;
            var key = $"{step}|{exp:yyyy-MM-dd}";
            if (t.LinkedinReminded == key) continue;
            store.Write(db => { if (db.Clients.FirstOrDefault(x => x.Id == t.Id) is { } w) w.LinkedinReminded = key; });
            var date = exp.ToLocalTime().ToString("dd.MM.yyyy");
            if (step == 0)
            {
                auth.MarkLost(t.Id);
                await alerts.Raise(t.Id, Alerts.Integrations, "La connexion LinkedIn a expiré",
                    "Les écrans gardent les dernières publications, mais elles ne sont plus mises à jour. Reconnectez le compte LinkedIn dans Applications › LinkedIn.",
                    "/apps/linkedin", severity: Notifs.Error, dedupe: "linkedin-expiry");
            }
            else
            {
                await alerts.Raise(t.Id, Alerts.Integrations, step == 1 ? "La connexion LinkedIn expire demain" : "La connexion LinkedIn expire dans 7 jours",
                    $"LinkedIn limite chaque connexion à 60 jours. Reconnectez le compte avant le {date} (Applications › LinkedIn › Reconnecter) pour que vos écrans continuent de recevoir les nouvelles publications.",
                    "/apps/linkedin", severity: Notifs.Warn, dedupe: "linkedin-expiry");
            }
        }
    }
}
