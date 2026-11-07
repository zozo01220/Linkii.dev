namespace Linkii.Poc;

/// <summary>
/// Alertes de l'organisation (licence Pro) : un interrupteur général, deux canaux (e-mail, notification de l'app Android)
/// et quatre types suivis. Le délai (<see cref="Tenant.AlertOfflineMinutes"/>) ne concerne que les écrans hors ligne ;
/// les destinataires (<see cref="Tenant.AlertEmail"/>) ne concernent que l'e-mail, les notifications vont aux téléphones des administrateurs.
/// </summary>
public static class Alerts
{
    public const string Screens = "screens", Sync = "sync", Members = "members", Billing = "billing", News = "system", Integrations = "integrations";

    public static readonly (string Id, string Label, string Hint)[] All =
    {
        (Screens, "Écrans", "Écran hors ligne, puis de retour en ligne."),
        (Sync, "Synchronisations", "Un dossier connecté n'a pas pu être synchronisé."),
        (Members, "Équipe et partage", "Une personne est ajoutée à l'organisation."),
        (Billing, "Abonnement", "Fin d'essai proche ou terminée, paiement refusé, espace suspendu."),
        (Integrations, "Applications et API", "Adresse de webhook désactivée, clé d'API qui expire."),
        (News, "Nouveautés et système", "Nouvelles fonctions de Linkivi, maintenance, incident en cours."),
    };

    /// <summary>Choix d'une organisation qui n'en a pas fait : ce qui demande une action ou signale un problème. Abonnement est toujours suivi (<see cref="Always"/>).</summary>
    public static readonly string[] Defaults = { Screens, Sync, Members, Billing, Integrations };

    /// <summary>Types toujours suivis : l'abonnement, car un espace suspendu ne doit jamais passer inaperçu.</summary>
    public static bool Always(string kind) => kind == Billing;

    /// <summary>La notification entre dans le centre de notifications (cloche) : toujours pour l'abonnement et les nouveautés, sinon licence Pro + alertes activées + « par notification » + type suivi.</summary>
    public static bool InCenter(Tenant c, string kind, bool good = false) =>
        (!good || c.AlertGood) && (kind is Billing or News or Integrations || c.IsGrowth && c.AlertsOn && c.AlertPush && c.AlertTypes.Contains(kind));

    /// <summary>L'organisation veut être prévenue de ce type, par au moins un canal.</summary>
    public static bool Wants(Tenant c, string kind, bool good = false) =>
        (!good || c.AlertGood) && c.IsGrowth && c.AlertsOn && (c.AlertMail || c.AlertPush) && (c.AlertTypes.Contains(kind) || Always(kind));

    /// <summary>Destinataires des e-mails : l'adresse choisie, sinon les administrateurs de l'organisation.</summary>
    public static List<(string Email, string Name)> Recipients(Db db, Tenant c) =>
        System.Net.Mail.MailAddress.TryCreate(c.AlertEmail?.Trim(), out var one)
            ? new() { (one.Address, "") }
            : db.Users.Where(u => u.ClientId == c.Id && u.Role == Roles.ClientAdmin && !u.Disabled && !u.AwaitsVerification && u.Email.Contains('@'))
                .Select(u => (u.Email, u.Name)).ToList();

    /// <summary>
    /// Une fois (18 octobre 2026) : l'ancien interrupteur « Prévenir quand un écran tombe » commandait l'e-mail des écrans, et les notifications
    /// de l'app partaient sans réglage d'organisation. Une organisation qui l'avait activé garde e-mail et notifications pour les quatre types ;
    /// les autres restent tout coupé, comme une nouvelle organisation.
    /// </summary>
    public static void Migrate(Db db)
    {
        foreach (var c in db.Clients.Where(c => c.AlertsVersion < 1))
        {
            c.AlertsVersion = 1;
            if (!c.AlertOffline) continue;
            c.AlertsOn = c.AlertMail = c.AlertPush = true;
            c.AlertTypes = All.Select(a => a.Id).ToList();
            c.AlertOffline = false;
        }
        // 31 octobre 2026 (centre de notifications) : une organisation qui n'a jamais rien choisi reçoit les types par défaut, comme une nouvelle
        foreach (var c in db.Clients.Where(c => c.AlertsVersion < 2))
        {
            c.AlertsVersion = 2;
            if (!c.AlertsOn && c.AlertTypes.Count == 0) c.AlertTypes = new(Defaults);
        }
        // 1er novembre 2026 (API et webhooks) : la nouvelle catégorie est suivie par les organisations qui avaient activé leurs alertes
        foreach (var c in db.Clients.Where(c => c.AlertsVersion < 3))
        {
            c.AlertsVersion = 3;
            if (c.AlertsOn && !c.AlertTypes.Contains(Integrations)) c.AlertTypes.Add(Integrations);
        }
    }
}

/// <summary>
/// Envoie une alerte par les canaux choisis par l'organisation. Ne lève jamais d'exception.
/// Chaque alerte entre aussi dans le centre de notifications (<see cref="NotificationCenter"/>) quand l'organisation le veut (<see cref="Alerts.InCenter"/>).
/// </summary>
public class AlertService(JsonStore store, Mailer mail, PushService push, NotificationCenter center, ResellerResolver resolver, HistoryLog history, ILogger<AlertService> log)
{
    /// <summary>Contenu d'un e-mail propre à un type (écrans) : reçoit le revendeur, l'adresse de l'espace, le nom et l'adresse du destinataire.</summary>
    public delegate Task<string?> MailBuilder(Reseller r, string appUrl, string name, string email);

    /// <param name="path">Page ouverte par un toucher sur la notification, ou par le bouton de l'e-mail.</param>
    /// <param name="except">Personne qui n'a pas à être prévenue (celle qui vient d'agir).</param>
    /// <param name="skipEmail">Adresse qui a déjà reçu un e-mail sur le même sujet (rappel de fin d'essai au créateur de l'espace).</param>
    /// <param name="buildMail">E-mail propre au type ; à défaut, un e-mail simple avec le titre et le texte.</param>
    /// <param name="severity">Couleur dans le centre (info, ok, warn, error) ; à défaut, selon le type.</param>
    /// <param name="dedupe">Clé de regroupement : le même événement se regroupe dans une seule notification.</param>
    /// <param name="subjects">Éléments concernés (écrans en panne), pour résoudre la notification quand ils sont rétablis.</param>
    /// <param name="good">Bonne nouvelle (retour en ligne, synchronisation rétablie) : envoyée seulement si l'organisation les a demandées.</param>
    /// <param name="force">Envoie aussi l'e-mail aux administrateurs même si l'organisation a coupé ses alertes (rappel d'urgence active).</param>
    public async Task Raise(Guid clientId, string kind, string title, string body, string path = "/", Guid? except = null, string? skipEmail = null, MailBuilder? buildMail = null,
        string? severity = null, string? dedupe = null, IEnumerable<string>? subjects = null, bool good = false, bool force = false)
    {
        try
        {
            var (client, rcpt, reseller, wants, inCenter) = store.Read<(Tenant?, List<(string Email, string Name)>, Reseller?, bool, bool)>(db =>
            {
                var c = db.Clients.FirstOrDefault(x => x.Id == clientId);
                if (c == null) return (null, new(), null, false, false);
                var w = Alerts.Wants(c, kind, good) || force;   // force : rappel qu'on veut aussi par e-mail, même si l'organisation a coupé ses alertes
                var actor = except == null ? null : db.Users.FirstOrDefault(u => u.Id == except)?.Email;
                var list = w && (c.AlertMail || force) ? Alerts.Recipients(db, c).Where(r => !Same(r.Email, skipEmail) && !Same(r.Email, actor)).ToList() : new();
                return (c, list, db.Resellers.FirstOrDefault(r => r.Id == c.ResellerId), w, Alerts.InCenter(c, kind, good));
            });
            if (client == null) return;
            Remember(clientId, kind, title, body, path, severity, good);
            if (inCenter) center.Add(clientId, kind, severity ?? DefaultSeverity(kind, good), title, body, path, except, dedupe, subjects);
            if (!wants) return;
            if (client.AlertPush && Alerts.Wants(client, kind, good)) await push.ToClientAdmins(clientId, kind, title, body, path, except);
            if (rcpt.Count == 0 || reseller == null) return;
            var app = resolver.LoginUrl(reseller)[..^"login".Length];
            foreach (var (email, name) in rcpt)
            {
                var err = buildMail != null
                    ? await buildMail(reseller, app, name, email)
                    : await mail.SendAlert(reseller, app, client.Name, name, email, title, body, app + path.TrimStart('/'));
                if (err != null) log.LogWarning("Alerte « {Title} » non envoyée à {Email} : {Error}", title, email, err);
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Alerte « {Title} » non envoyée", title); }
    }

    /// <summary>Notification seule (centre et téléphones, sans e-mail), si l'organisation suit ce type par notification.</summary>
    public async Task RaisePush(Guid clientId, string kind, string title, string body, string path = "/", string? severity = null, bool good = false)
    {
        try
        {
            if (store.Read(db => db.Clients.FirstOrDefault(x => x.Id == clientId)) is not { } c) return;
            if (Alerts.InCenter(c, kind, good)) center.Add(clientId, kind, severity ?? DefaultSeverity(kind, good), title, body, path);
            if (c.AlertPush && Alerts.Wants(c, kind, good)) await push.ToClientAdmins(clientId, kind, title, body, path);
        }
        catch (Exception ex) { log.LogWarning(ex, "Notification « {Title} » non envoyée", title); }
    }

    /// <summary>
    /// Toute alerte entre dans l'historique, que l'organisation l'ait demandée ou non. Les écrans y entrent par <see cref="ScreenHistory"/> (qui date la panne sans attendre le délai d'alerte).
    /// Synchronisations et applications sont aussi des lignes du diagnostic.
    /// </summary>
    private void Remember(Guid clientId, string kind, string title, string body, string path, string? severity, bool good)
    {
        if (kind == Alerts.Screens) return;
        var cat = kind switch { Alerts.Sync => History.Sync, Alerts.Members => History.Members, Alerts.Billing => History.Account, Alerts.Integrations => History.Integrations, _ => History.Settings };
        history.Record(clientId, History.Activity, cat, title, severity ?? DefaultSeverity(kind, good), body, "Système", path: path, tech: kind is Alerts.Sync or Alerts.Integrations);
    }

    private static string DefaultSeverity(string kind, bool good) => good ? Notifs.Ok : kind switch
    {
        Alerts.Screens => Notifs.Error,
        Alerts.Sync or Alerts.Billing => Notifs.Warn,
        _ => Notifs.Info,
    };

    private static bool Same(string a, string? b) => b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
