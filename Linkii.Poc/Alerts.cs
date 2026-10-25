namespace Linkii.Poc;

/// <summary>
/// Alertes de l'organisation (licence Pro) : un interrupteur général, deux canaux (e-mail, notification de l'app Android)
/// et quatre types suivis. Le délai (<see cref="Tenant.AlertOfflineMinutes"/>) ne concerne que les écrans hors ligne ;
/// les destinataires (<see cref="Tenant.AlertEmail"/>) ne concernent que l'e-mail, les notifications vont aux téléphones des administrateurs.
/// </summary>
public static class Alerts
{
    public const string Screens = "screens", Sync = "sync", Members = "members", Billing = "billing";

    public static readonly (string Id, string Label, string Hint)[] All =
    {
        (Screens, "Écrans hors ligne", "Un écran cesse d'émettre, puis quand il revient."),
        (Sync, "Synchronisations", "Un dossier connecté n'a pas pu être synchronisé."),
        (Members, "Équipe", "Une personne est ajoutée à l'organisation."),
        (Billing, "Abonnement", "Fin d'essai proche ou terminée, espace suspendu."),
    };

    /// <summary>L'organisation veut être prévenue de ce type, par au moins un canal.</summary>
    public static bool Wants(Tenant c, string kind) =>
        c.IsGrowth && c.AlertsOn && (c.AlertMail || c.AlertPush) && c.AlertTypes.Contains(kind);

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
    }
}

/// <summary>Envoie une alerte par les canaux choisis par l'organisation. Ne lève jamais d'exception.</summary>
public class AlertService(JsonStore store, Mailer mail, PushService push, ResellerResolver resolver, ILogger<AlertService> log)
{
    /// <summary>Contenu d'un e-mail propre à un type (écrans) : reçoit le revendeur, l'adresse de l'espace, le nom et l'adresse du destinataire.</summary>
    public delegate Task<string?> MailBuilder(Reseller r, string appUrl, string name, string email);

    /// <param name="path">Page ouverte par un toucher sur la notification, ou par le bouton de l'e-mail.</param>
    /// <param name="except">Personne qui n'a pas à être prévenue (celle qui vient d'agir).</param>
    /// <param name="skipEmail">Adresse qui a déjà reçu un e-mail sur le même sujet (rappel de fin d'essai au créateur de l'espace).</param>
    /// <param name="buildMail">E-mail propre au type ; à défaut, un e-mail simple avec le titre et le texte.</param>
    public async Task Raise(Guid clientId, string kind, string title, string body, string path = "/", Guid? except = null, string? skipEmail = null, MailBuilder? buildMail = null)
    {
        try
        {
            var (client, rcpt, reseller) = store.Read<(Tenant?, List<(string Email, string Name)>, Reseller?)>(db =>
            {
                var c = db.Clients.FirstOrDefault(x => x.Id == clientId);
                if (c == null || !Alerts.Wants(c, kind)) return (null, new(), null);
                var actor = except == null ? null : db.Users.FirstOrDefault(u => u.Id == except)?.Email;
                var list = c.AlertMail ? Alerts.Recipients(db, c).Where(r => !Same(r.Email, skipEmail) && !Same(r.Email, actor)).ToList() : new();
                return (c, list, db.Resellers.FirstOrDefault(r => r.Id == c.ResellerId));
            });
            if (client == null) return;
            if (client.AlertPush) await push.ToClientAdmins(clientId, kind, title, body, path, except);
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

    /// <summary>Notification seule (sans e-mail), si l'organisation suit ce type par notification.</summary>
    public async Task RaisePush(Guid clientId, string kind, string title, string body, string path = "/")
    {
        try
        {
            if (store.Read(db => db.Clients.FirstOrDefault(x => x.Id == clientId)) is { AlertPush: true } c && Alerts.Wants(c, kind))
                await push.ToClientAdmins(clientId, kind, title, body, path);
        }
        catch (Exception ex) { log.LogWarning(ex, "Notification « {Title} » non envoyée", title); }
    }

    private static bool Same(string a, string? b) => b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
