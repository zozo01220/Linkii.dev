using System.Net;
using System.Net.Mail;

namespace Linkii.Poc;

/// <summary>Envoi d'e-mails par SMTP. Réglages (variables d'environnement ou appsettings) :
/// console › Réglages de la plateforme (commun à toutes les organisations), ou à défaut Linkii:Smtp:Host, Port (587), User, Password, From, Ssl (true).
/// Sans Host, aucun e-mail ne part : l'appelant affiche alors l'information à l'administrateur.</summary>
public class Mailer(IConfiguration config, JsonStore store, SecretBox box, ILogger<Mailer> log)
{
    /// <summary>Réglages d'envoi : ceux de la console (Réglages de la plateforme) s'ils sont renseignés, sinon la configuration du serveur.</summary>
    private record Smtp(string Host, int Port, bool Ssl, string User, string Password, string From)
    {
        public bool Ready => !string.IsNullOrWhiteSpace(Host) && From.Contains('@');
    }

    private Smtp Current()
    {
        var p = store.Read(d => d.Platform);
        if (!string.IsNullOrWhiteSpace(p.SmtpHost))
        {
            var pwd = "";
            try { if (!string.IsNullOrEmpty(p.SmtpPassword)) pwd = box.Unprotect(p.SmtpPassword); }
            catch (Exception ex) { log.LogWarning(ex, "Mot de passe SMTP illisible"); }
            return new(p.SmtpHost.Trim(), p.SmtpPort > 0 ? p.SmtpPort : 587, p.SmtpSsl, p.SmtpUser.Trim(), pwd,
                !string.IsNullOrWhiteSpace(p.SmtpFrom) ? p.SmtpFrom.Trim() : p.SmtpUser.Trim());
        }
        return new(config["Linkii:Smtp:Host"] ?? "", int.TryParse(config["Linkii:Smtp:Port"], out var port) ? port : 587,
            !string.Equals(config["Linkii:Smtp:Ssl"], "false", StringComparison.OrdinalIgnoreCase),
            config["Linkii:Smtp:User"] ?? "", config["Linkii:Smtp:Password"] ?? "", config["Linkii:Smtp:From"] ?? config["Linkii:Smtp:User"] ?? "");
    }

    public bool Configured => Current().Ready;

    /// <summary>Site de Linkii (Linkii:SiteUrl, défaut https://linkii.com) : lien présent au pied de chaque e-mail de la plateforme.</summary>
    public string SiteUrl => config["Linkii:SiteUrl"] is { Length: > 0 } u ? u.TrimEnd('/') : "https://linkii.com";

    /// <summary>Retourne null si l'e-mail est parti, sinon la raison de l'échec (affichable). Ajoute le pied de page (lien vers le site de Linkii).</summary>
    public async Task<string?> Send(string to, string subject, string html, string text, string? fromName = null, string? replyTo = null)
    {
        var site = SiteUrl;
        var siteLabel = site.Replace("https://", "").Replace("http://", "");
        text += $"\n\n--\n{siteLabel} : {site}";
        var footer = $"""<p style="max-width:520px;margin:14px auto 0;text-align:center;font-family:Segoe UI,Arial,sans-serif;font-size:12px;color:#5A6B80"><a href="{WebUtility.HtmlEncode(site)}" style="color:#3B82C4;text-decoration:none">{WebUtility.HtmlEncode(siteLabel)}</a></p>""";
        html = html.Contains("</body>") ? html.Replace("</body>", footer + "</body>") : html + footer;
        var smtpCfg = Current();
        if (!smtpCfg.Ready)
        {
            // développement : sans SMTP, le contenu (lien de confirmation compris) n'apparaît que dans le journal du serveur
            log.LogInformation("E-mail non envoyé (SMTP non configuré) à {To} : « {Subject} »\n{Text}", to, subject, text);
            return "l'envoi d'e-mails n'est pas configuré sur le serveur";
        }
        try
        {
            using var msg = new MailMessage { From = new MailAddress(smtpCfg.From, fromName ?? "Linkii"), Subject = subject, Body = text, IsBodyHtml = false };
            msg.To.Add(to);
            if (!string.IsNullOrWhiteSpace(replyTo) && MailAddress.TryCreate(replyTo, out var rt)) msg.ReplyToList.Add(rt);
            msg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, System.Text.Encoding.UTF8, "text/html"));
            msg.BodyEncoding = msg.SubjectEncoding = System.Text.Encoding.UTF8;

            using var smtp = new SmtpClient(smtpCfg.Host, smtpCfg.Port)
            {
                EnableSsl = smtpCfg.Ssl,
                Timeout = 20000
            };
            if (!string.IsNullOrEmpty(smtpCfg.User))
                smtp.Credentials = new NetworkCredential(smtpCfg.User, smtpCfg.Password);
            await smtp.SendMailAsync(msg);
            log.LogInformation("E-mail « {Subject} » envoyé à {To}", subject, to);
            return null;
        }
        catch (Exception ex) when (ex is SmtpException or FormatException or InvalidOperationException)
        {
            log.LogWarning(ex, "Échec de l'envoi de l'e-mail « {Subject} » à {To}", subject, to);
            return "le serveur d'e-mails a refusé l'envoi (" + ex.Message + ")";
        }
    }

    /// <summary>E-mail de bienvenue d'un nouveau compte (client, revendeur, équipe) avec son mot de passe provisoire.
    /// intro : phrase d'accueil, ex. « Votre espace « UNIL » est prêt. »</summary>
    public Task<string?> SendWelcome(Reseller r, string loginUrl, string intro, string name, string email, string password)
    {
        var brand = r.DisplayName is { Length: > 0 } b ? b : "Linkii";
        var hello = string.IsNullOrWhiteSpace(name) ? "Bonjour," : $"Bonjour {name.Trim()},";
        var subject = $"Votre accès à {brand}";
        var text =
            $"{hello}\n\n" +
            $"{intro}\n\n" +
            $"Adresse : {loginUrl}\n" +
            $"E-mail : {email}\n" +
            $"Mot de passe provisoire : {password}\n\n" +
            "À votre première connexion, il vous sera demandé de choisir votre propre mot de passe.\n\n" +
            $"— L'équipe {brand}";
        string E(string s) => WebUtility.HtmlEncode(s);
        var color = System.Text.RegularExpressions.Regex.IsMatch(r.BrandColor ?? "", "^#[0-9A-Fa-f]{6}$") ? r.BrandColor! : "#0B1F3A";
        var html = $"""
            <!doctype html>
            <html lang="fr"><body style="margin:0;padding:24px;background:#F4F6F8;font-family:Segoe UI,Arial,sans-serif;color:#0B1F3A">
              <table role="presentation" width="100%" style="max-width:520px;margin:0 auto;background:#fff;border:1px solid #DDE3EA;border-radius:6px">
                <tr><td style="padding:20px 28px;border-bottom:3px solid {color};font-size:20px;font-weight:600">{E(brand)}</td></tr>
                <tr><td style="padding:24px 28px;font-size:15px;line-height:1.55">
                  <p style="margin:0 0 14px">{E(hello)}</p>
                  <p style="margin:0 0 18px">{E(intro)} Voici vos identifiants :</p>
                  <table role="presentation" style="width:100%;background:#F4F6F8;border-radius:4px;font-size:14px">
                    <tr><td style="padding:10px 14px;color:#5A6B80;width:170px">E-mail</td><td style="padding:10px 14px"><b>{E(email)}</b></td></tr>
                    <tr><td style="padding:10px 14px;color:#5A6B80">Mot de passe provisoire</td><td style="padding:10px 14px;font-family:Consolas,monospace;font-size:16px"><b>{E(password)}</b></td></tr>
                  </table>
                  <p style="margin:22px 0"><a href="{E(loginUrl)}" style="display:inline-block;background:{color};color:#fff;text-decoration:none;padding:11px 20px;border-radius:4px;font-weight:600">Se connecter</a></p>
                  <p style="margin:0;color:#5A6B80;font-size:13px">À votre première connexion, il vous sera demandé de choisir votre propre mot de passe.</p>
                </td></tr>
              </table>
            </body></html>
            """;
        return Send(email, subject, html, text, brand, r.SenderEmail);
    }

    // ---------- Inscription en libre-service ----------

    /// <summary>Lien de confirmation de l'adresse, envoyé à la création du compte (et à chaque renvoi ou correction).</summary>
    public Task<string?> SendVerification(Reseller r, string link, string name, string email, string org, int hours)
    {
        var brand = BrandOf(r);
        var hello = Hello(name);
        var text =
            $"{hello}\n\n" +
            $"Vous venez de créer l'espace « {org} » sur {brand}. Confirmez votre adresse e-mail pour l'activer :\n{link}\n\n" +
            $"Ce lien est valable {hours} heures.\n\n" +
            "Vous n'êtes pas à l'origine de cette demande ? Ignorez cet e-mail : sans confirmation, l'espace est supprimé automatiquement.\n\n" +
            $"— L'équipe {brand}";
        var body = $"""
            <p style="margin:0 0 14px">{E(hello)}</p>
            <p style="margin:0 0 18px">Vous venez de créer l'espace <b>{E(org)}</b> sur {E(brand)}. Confirmez votre adresse e-mail pour l'activer :</p>
            {Button(r, link, "Confirmer mon adresse")}
            <p style="margin:0 0 14px;color:#5A6B80;font-size:13px">Ce lien est valable {hours} heures. Si le bouton ne fonctionne pas, copiez ce lien dans votre navigateur :<br /><span style="font-family:Consolas,monospace;font-size:12px;word-break:break-all">{E(link)}</span></p>
            <p style="margin:0;padding-top:12px;border-top:1px solid #DDE3EA;color:#5A6B80;font-size:12px">Vous n'êtes pas à l'origine de cette demande ? Ignorez cet e-mail : sans confirmation, l'espace est supprimé automatiquement.</p>
            """;
        return Send(email, "Confirmez votre adresse e-mail", Frame(r, body), text, brand, r.SenderEmail);
    }

    /// <summary>Bienvenue à l'administrateur, une fois son compte entièrement validé. trialEnd : fin de l'essai gratuit (date affichable), ou null.
    /// signIn : comment se connecter (« votre e-mail et votre mot de passe », « le bouton Continuer avec Microsoft »).</summary>
    public Task<string?> SendSignupWelcome(Reseller r, string appUrl, string name, string email, string org, int trialDays, string? trialEnd, string signIn, bool pending = false)
    {
        var brand = BrandOf(r);
        var who = string.IsNullOrWhiteSpace(name) ? "" : " " + name.Trim().Split(' ')[0];
        var trial = trialEnd == null ? null : $"Essai gratuit : {trialDays} jours, jusqu'au {trialEnd}";
        var text =
            $"Bienvenue{who},\n\n" +
            (pending
                ? $"Votre espace « {org} » est créé et vous en êtes l'administrateur. Il vous reste à confirmer votre adresse e-mail avec le lien que vous venez de recevoir : l'essai gratuit ({trialDays} jours) démarre à la confirmation.\n\n"
                : $"Votre adresse est confirmée et votre espace « {org} » est prêt. Vous en êtes l'administrateur.\n\n") +
            (trial == null ? "" : trial + "\n") +
            $"Adresse de votre espace : {appUrl}\n" +
            $"Connexion : {signIn}\n\n" +
            "Pour bien démarrer :\n1. Ajoutez un écran avec son code d'appairage.\n2. Importez vos images et vidéos dans la médiathèque.\n3. Composez une liste de lecture et publiez-la.\n\n" +
            $"Une question ? Répondez simplement à cet e-mail.\n\n— L'équipe {brand}";
        var body = $"""
            <p style="margin:0 0 14px;font-size:19px;font-weight:600">Bienvenue{E(who)}</p>
            <p style="margin:0 0 16px">{(pending ? $"Votre espace <b>{E(org)}</b> est créé et vous en êtes l'administrateur. Il vous reste à <b>confirmer votre adresse e-mail</b> avec le lien que vous venez de recevoir : l'essai gratuit ({trialDays} jours) démarre à la confirmation." : $"Votre adresse est confirmée et votre espace <b>{E(org)}</b> est prêt. Vous en êtes l'administrateur.")}</p>
            <table role="presentation" style="width:100%;background:#F4F6F8;border-radius:4px;font-size:14px;margin:0 0 18px">
              {(trial == null ? "" : $"""<tr><td style="padding:10px 14px 0">Essai gratuit : <b>{trialDays} jours</b>, jusqu'au <b>{E(trialEnd!)}</b></td></tr>""")}
              <tr><td style="padding:10px 14px 0">Adresse de votre espace : <a href="{E(appUrl)}" style="color:#3B82C4">{E(appUrl.Replace("https://", "").Replace("http://", ""))}</a></td></tr>
              <tr><td style="padding:10px 14px 10px">Connexion : {E(signIn)}</td></tr>
            </table>
            <p style="margin:0 0 6px"><b>Pour bien démarrer</b></p>
            <ol style="margin:0 0 18px;padding-left:20px">
              <li>Ajoutez un écran avec son code d'appairage.</li>
              <li>Importez vos images et vidéos dans la médiathèque.</li>
              <li>Composez une liste de lecture et publiez-la.</li>
            </ol>
            {Button(r, appUrl, "Accéder à mon espace")}
            <p style="margin:0;color:#5A6B80;font-size:13px">Une question ? Répondez simplement à cet e-mail.</p>
            """;
        return Send(email, pending ? $"Bienvenue sur {brand}, votre espace {org} est créé" : $"Bienvenue sur {brand}, votre espace {org} est prêt", Frame(r, body), text, brand, r.SenderEmail);
    }

    /// <summary>Rappel : l'essai gratuit se termine bientôt (envoyé 2 jours avant).</summary>
    public Task<string?> SendTrialReminder(Reseller r, string appUrl, string name, string email, string org, string end, int daysLeft)
    {
        var brand = BrandOf(r);
        var hello = Hello(name);
        var left = daysLeft <= 1 ? "demain" : $"dans {daysLeft} jours";
        var text =
            $"{hello}\n\n" +
            $"L'essai gratuit de votre espace « {org} » se termine {left}, le {end}. Ensuite, vos écrans afficheront « Essai terminé ».\n\n" +
            $"Pour continuer sans interruption, répondez à cet e-mail : nous vous aiderons à choisir votre abonnement.\n\nVotre espace : {appUrl}\n\n— L'équipe {brand}";
        var body = $"""
            <p style="margin:0 0 14px">{E(hello)}</p>
            <p style="margin:0 0 14px">L'essai gratuit de votre espace <b>{E(org)}</b> se termine <b>{E(left)}</b>, le {E(end)}. Ensuite, vos écrans afficheront « Essai terminé ».</p>
            <p style="margin:0 0 18px">Pour continuer sans interruption, répondez à cet e-mail : nous vous aiderons à choisir votre abonnement.</p>
            {Button(r, appUrl, "Accéder à mon espace")}
            """;
        return Send(email, $"Votre essai {brand} se termine {left}", Frame(r, body), text, brand, r.SenderEmail);
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string BrandOf(Reseller r) => r.DisplayName is { Length: > 0 } b ? b : "Linkii";
    private static string Hello(string name) => string.IsNullOrWhiteSpace(name) ? "Bonjour," : $"Bonjour {name.Trim().Split(' ')[0]},";
    private static string Color(Reseller r) => System.Text.RegularExpressions.Regex.IsMatch(r.BrandColor ?? "", "^#[0-9A-Fa-f]{6}$") ? r.BrandColor! : "#0B1F3A";

    private static string Button(Reseller r, string url, string label) =>
        $"""<p style="margin:4px 0 20px"><a href="{E(url)}" style="display:inline-block;background:{Color(r)};color:#fff;text-decoration:none;padding:11px 20px;border-radius:4px;font-weight:600">{E(label)}</a></p>""";

    /// <summary>Gabarit commun : bandeau à la marque du revendeur, contenu, pied de page ajouté par <see cref="Send"/>.</summary>
    private static string Frame(Reseller r, string body) => $"""
        <!doctype html>
        <html lang="fr"><body style="margin:0;padding:24px;background:#F4F6F8;font-family:Segoe UI,Arial,sans-serif;color:#0B1F3A">
          <table role="presentation" width="100%" style="max-width:520px;margin:0 auto;background:#fff;border:1px solid #DDE3EA;border-radius:6px">
            <tr><td style="padding:20px 28px;border-bottom:3px solid {Color(r)};font-size:20px;font-weight:600">{E(BrandOf(r))}</td></tr>
            <tr><td style="padding:24px 28px;font-size:15px;line-height:1.55">{body}</td></tr>
          </table>
        </body></html>
        """;
}
