using System.Net;
using System.Net.Mail;

namespace Linkii.Poc;

/// <summary>Envoi d'e-mails par SMTP. Réglages (variables d'environnement ou appsettings) :
/// Linkii:Smtp:Host, Port (587), User, Password, From (adresse d'expédition), Ssl (true).
/// Sans Host, aucun e-mail ne part : l'appelant affiche alors l'information à l'administrateur.</summary>
public class Mailer(IConfiguration config, ILogger<Mailer> log)
{
    private string? Host => config["Linkii:Smtp:Host"];
    private string From => config["Linkii:Smtp:From"] ?? config["Linkii:Smtp:User"] ?? "";

    public bool Configured => !string.IsNullOrWhiteSpace(Host) && From.Contains('@');

    /// <summary>Retourne null si l'e-mail est parti, sinon la raison de l'échec (affichable).</summary>
    public async Task<string?> Send(string to, string subject, string html, string text, string? fromName = null, string? replyTo = null)
    {
        if (!Configured) return "l'envoi d'e-mails n'est pas configuré sur le serveur";
        try
        {
            using var msg = new MailMessage { From = new MailAddress(From, fromName ?? "Linkii"), Subject = subject, Body = text, IsBodyHtml = false };
            msg.To.Add(to);
            if (!string.IsNullOrWhiteSpace(replyTo) && MailAddress.TryCreate(replyTo, out var rt)) msg.ReplyToList.Add(rt);
            msg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, System.Text.Encoding.UTF8, "text/html"));
            msg.BodyEncoding = msg.SubjectEncoding = System.Text.Encoding.UTF8;

            using var smtp = new SmtpClient(Host, int.TryParse(config["Linkii:Smtp:Port"], out var p) ? p : 587)
            {
                EnableSsl = !string.Equals(config["Linkii:Smtp:Ssl"], "false", StringComparison.OrdinalIgnoreCase),
                Timeout = 20000
            };
            if (!string.IsNullOrEmpty(config["Linkii:Smtp:User"]))
                smtp.Credentials = new NetworkCredential(config["Linkii:Smtp:User"], config["Linkii:Smtp:Password"]);
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
        var color = System.Text.RegularExpressions.Regex.IsMatch(r.BrandColor ?? "", "^#[0-9A-Fa-f]{6}$") ? r.BrandColor : "#0B1F3A";
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
}
