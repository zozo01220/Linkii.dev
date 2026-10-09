using System.Security.Cryptography;
using System.Text;

namespace Linkii.Poc;

/// <summary>Conditions générales de Linkii : version en vigueur, enregistrée avec chaque acceptation.</summary>
public static class Terms
{
    public const string Version = "1.0";
    public static readonly DateTime PublishedUtc = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
}

/// <summary>Consentements demandés à l'inscription : conditions générales (obligatoire), démarches commerciales et nouveautés (facultatifs).</summary>
public record Consents(bool Terms, bool Marketing, bool News);

/// <summary>Identité reçue de Microsoft ou de Google (« Continuer avec… ») : l'adresse est prouvée par le fournisseur.</summary>
public record ExternalIdentity(string Provider, string Id, string Email, string Name);

/// <summary>
/// Création de compte en libre-service et cycle de vie de l'essai gratuit.
/// Par e-mail : compte « à valider » jusqu'au clic sur le lien envoyé ; par Microsoft ou Google : validé tout de suite.
/// L'essai (durée réglée dans la console, commune à toutes les organisations) démarre à la validation.
/// </summary>
public static class Signup
{
    public enum ConfirmResult { Ok, Expired, Invalid }

    /// <summary>État affiché dans la console : « À valider » ou « Validé ».</summary>
    public static bool IsVerified(User? owner) => owner?.EmailVerifiedUtc != null;

    /// <summary>Administrateur qui a créé l'organisation (le plus ancien).</summary>
    public static User? OwnerOf(Db db, Tenant c) => db.Users.Where(u => u.ClientId == c.Id && u.Role == Roles.ClientAdmin)
        .OrderBy(u => u.CreatedUtc ?? DateTime.MinValue).FirstOrDefault();

    /// <summary>
    /// Crée l'organisation et son administrateur. external null : inscription par e-mail (password obligatoire, lien de confirmation à envoyer :
    /// le jeton est retourné). Une adresse déjà prise par une inscription jamais validée est libérée : la nouvelle inscription la remplace.
    /// </summary>
    public static (Tenant? Client, User? Admin, string? Token, string? Error) Create(Db db, Reseller reseller, string org, string name, string email,
        string? password, ExternalIdentity? external, Consents consents, DateTime now)
    {
        if (!consents.Terms) return (null, null, null, "Acceptez les conditions générales pour créer votre espace.");
        if (external == null && string.IsNullOrEmpty(password)) return (null, null, null, "Choisissez un mot de passe.");
        email = (external?.Email ?? email).Trim().ToLowerInvariant();
        if (!System.Net.Mail.MailAddress.TryCreate(email, out _)) return (null, null, null, "Adresse e-mail invalide.");
        foreach (var stale in db.Users.Where(u => u.Email == email && u.ResellerId == reseller.Id && u.AwaitsVerification).ToList())
            Remove(db, stale);

        var source = external?.Provider ?? SignupSources.Email;
        var (client, err) = Tenancy.CreateClient(db, reseller, org, name, email, external == null ? password : null, source: source);
        if (client == null) return (null, null, null, err == "Cette adresse e-mail est déjà utilisée." ? "Cette adresse a déjà un espace. Connectez-vous." : err);
        var admin = db.Users.Last();
        admin.TermsVersion = Terms.Version; admin.TermsAcceptedUtc = now;
        admin.MarketingOptIn = consents.Marketing; admin.MarketingOptInUtc = consents.Marketing ? now : null;
        admin.NewsOptIn = consents.News; admin.NewsOptInUtc = consents.News ? now : null;

        if (external != null)
        {
            admin.ExternalProvider = external.Provider;
            admin.ExternalId = external.Id;
            Verify(db, admin, client, now);
            return (client, admin, null, null);
        }
        return (client, admin, NewToken(admin, db.Platform.VerifyLinkHours, now), null);
    }

    /// <summary>Nouveau lien de confirmation : l'ancien ne marche plus. Seule l'empreinte du jeton est gardée.</summary>
    public static string NewToken(User u, int hours, DateTime now)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        u.VerifyTokenHash = Hash(token);
        u.VerifyExpiresUtc = now.AddHours(Math.Max(1, hours));
        u.VerifySends.RemoveAll(t => t < now.AddHours(-1));
        u.VerifySends.Add(now);
        return token;
    }

    /// <summary>Renvoi du lien : au plus un par minute et cinq par heure. Rend le délai d'attente, ou null si l'envoi est permis.
    /// perMinute false : correction de l'adresse, possible aussitôt (seul le plafond horaire s'applique).</summary>
    public static TimeSpan? ResendWait(User u, DateTime now, bool perMinute = true)
    {
        var recent = u.VerifySends.Where(t => t > now.AddHours(-1)).OrderBy(t => t).ToList();
        if (recent.Count >= 5) return recent[0].AddHours(1) - now;
        if (perMinute && recent.Count > 0 && recent[^1] > now.AddMinutes(-1)) return recent[^1].AddMinutes(1) - now;
        return null;
    }

    /// <summary>Corrige l'adresse d'une inscription non validée (faute de frappe) et prépare un nouveau lien.</summary>
    public static (string? Token, string? Error) ChangeEmail(Db db, User u, string email, DateTime now)
    {
        email = email.Trim().ToLowerInvariant();
        if (!u.AwaitsVerification) return (null, "Cette adresse est déjà confirmée.");
        if (!System.Net.Mail.MailAddress.TryCreate(email, out _)) return (null, "Adresse e-mail invalide.");
        if (email == u.Email) return (null, "C'est déjà l'adresse enregistrée.");
        if (db.Users.Any(x => x.Id != u.Id && x.Email == email && (x.ResellerId == u.ResellerId || x.ResellerId == null)))
            return (null, "Cette adresse a déjà un espace.");
        if (ResendWait(u, now, perMinute: false) is { } wait) return (null, $"Patientez {Wait(wait)} avant un nouvel envoi.");
        u.Email = email;
        return (NewToken(u, db.Platform.VerifyLinkHours, now), null);
    }

    /// <summary>Clic sur le lien de confirmation : l'adresse est validée et l'essai démarre.</summary>
    public static (ConfirmResult Result, User? User, Tenant? Client) Confirm(Db db, string? token, DateTime now)
    {
        if (string.IsNullOrEmpty(token)) return (ConfirmResult.Invalid, null, null);
        var hash = Hash(token);
        var u = db.Users.FirstOrDefault(x => x.VerifyTokenHash == hash);
        if (u == null) return (ConfirmResult.Invalid, null, null);
        var client = db.Clients.FirstOrDefault(c => c.Id == u.ClientId);
        if (u.VerifyExpiresUtc < now || client == null) return (ConfirmResult.Expired, u, client);
        Verify(db, u, client, now);
        return (ConfirmResult.Ok, u, client);
    }

    /// <summary>Adresse validée (lien, Microsoft / Google, ou console) : l'essai gratuit démarre pour une organisation créée en libre-service.</summary>
    public static void Verify(Db db, User u, Tenant? client, DateTime now)
    {
        u.EmailVerifiedUtc ??= now;
        u.VerifyTokenHash = null;
        u.VerifyExpiresUtc = null;
        u.VerifySends.Clear();
        if (client != null && SignupSources.IsSelfService(client.SignupSource) && client.TrialEndsUtc == null && u.Id == OwnerOf(db, client)?.Id)
            client.TrialEndsUtc = now.AddDays(Math.Max(1, db.Platform.TrialDays));
    }

    /// <summary>Prolonge l'essai de days jours (à partir d'aujourd'hui s'il est terminé) ; les écrans reprennent.</summary>
    public static void Extend(Tenant c, int days, DateTime now)
    {
        var from = c.TrialEndsUtc is { } end && end > now ? end : now;
        c.TrialEndsUtc = from.AddDays(Math.Max(1, days));
        c.TrialReminderSentUtc = null;
        c.TrialEndedNotifiedUtc = null;
    }

    /// <summary>Supprime une inscription jamais validée : l'organisation (vide) et ses comptes.</summary>
    public static void Remove(Db db, User u)
    {
        var cid = u.ClientId;
        db.Users.Remove(u);
        if (cid == null || db.Users.Any(x => x.ClientId == cid)) return;
        db.Clients.RemoveAll(c => c.Id == cid);
        db.Areas.RemoveAll(a => a.ClientId == cid);
        db.AppInstalls.RemoveAll(a => a.ClientId == cid);
        db.Playlists.RemoveAll(p => p.ClientId == cid);
        db.Screens.RemoveAll(s => s.ClientId == cid);
    }

    /// <summary>Supprime définitivement une organisation et tout ce qu'elle contient. Renvoie les fichiers de la médiathèque à effacer du disque.</summary>
    public static List<string> RemoveClient(Db db, Guid cid)
    {
        var files = db.Media.Where(m => m.ClientId == cid && !string.IsNullOrEmpty(m.FileName)).Select(m => m.FileName!).ToList();
        db.Users.RemoveAll(u => u.ClientId == cid);
        db.Screens.RemoveAll(s => s.ClientId == cid);
        db.Playlists.RemoveAll(p => p.ClientId == cid);
        db.Media.RemoveAll(m => m.ClientId == cid);
        db.AppInstalls.RemoveAll(a => a.ClientId == cid);
        db.Areas.RemoveAll(a => a.ClientId == cid);
        db.Clients.RemoveAll(c => c.Id == cid);
        return files;
    }

    /// <summary>Inscriptions jamais validées depuis plus de PurgeUnverifiedDays jours (adresse fausse ou mal saisie).</summary>
    public static List<User> Stale(Db db, DateTime now) => db.Users
        .Where(u => u.AwaitsVerification && (u.CreatedUtc ?? now) < now.AddDays(-Math.Max(1, db.Platform.PurgeUnverifiedDays)))
        .ToList();

    /// <summary>
    /// Migration (9 octobre 2026) : les comptes qui se sont déjà connectés ont prouvé leur adresse. Les invités la prouveront à leur
    /// première connexion avec le mot de passe provisoire reçu par e-mail.
    /// </summary>
    public static void MarkVerified(Db db)
    {
        foreach (var u in db.Users.Where(u => u.EmailVerifiedUtc == null && u.VerifyTokenHash == null && u.LastLoginUtc != null))
            u.EmailVerifiedUtc = u.CreatedUtc ?? u.LastLoginUtc;
    }

    /// <summary>Date affichable dans le fuseau de l'organisation, ex. « 16 octobre 2026 ».</summary>
    public static string Day(Tenant c, DateTime utc)
    {
        var local = utc;
        try { local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.FindSystemTimeZoneById(c.Timezone)); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        return local.ToString("d MMMM yyyy", Fr);
    }

    public static readonly System.Globalization.CultureInfo Fr = new("fr-CH");

    /// <summary>Comment l'administrateur se connecte, pour l'e-mail de bienvenue.</summary>
    public static string SignInHint(User u) => u.ExternalProvider switch
    {
        SignupSources.Microsoft => "le bouton « Continuer avec Microsoft »",
        SignupSources.Google => "le bouton « Continuer avec Google »",
        SignupSources.LinkedIn => "le bouton « Continuer avec LinkedIn »",
        SignupSources.GitHub => "le bouton « Continuer avec GitHub »",
        _ => "votre adresse e-mail et votre mot de passe"
    };

    public static string Wait(TimeSpan t) => t.TotalSeconds < 90 ? $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))} s" : $"{(int)Math.Ceiling(t.TotalMinutes)} min";

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
/// Toutes les 30 minutes : supprime les inscriptions jamais validées, envoie le rappel 2 jours avant la fin de l'essai,
/// et prévient les écrans d'une organisation dont l'essai vient de se terminer (ils affichent « Essai terminé »).
/// </summary>
public class SignupWorker(JsonStore store, Mailer mail, Notifier notifier, ResellerResolver resolver, ILogger<SignupWorker> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ReminderBefore = TimeSpan.FromDays(2);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stop); } catch (TaskCanceledException) { return; }
        using var timer = new PeriodicTimer(Every);
        do
        {
            try { await Run(DateTime.UtcNow); }
            catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogError(ex, "Inscriptions et essais gratuits"); }
        } while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
    }

    public async Task Run(DateTime now)
    {
        var purged = store.Write(db =>
        {
            var stale = Signup.Stale(db, now);
            foreach (var u in stale) Signup.Remove(db, u);
            return stale.Select(u => u.Email).ToList();
        });
        if (purged.Count > 0) log.LogInformation("Inscriptions jamais validées supprimées : {Emails}", string.Join(", ", purged));

        var reminders = store.Read(db => db.Clients
            .Where(c => c.InTrial(now) && c.TrialEndsUtc - now <= ReminderBefore && c.TrialReminderSentUtc == null)
            .Select(c => (Client: c, Owner: Signup.OwnerOf(db, c), Reseller: db.Resellers.FirstOrDefault(r => r.Id == c.ResellerId)))
            .Where(x => x.Owner != null && x.Reseller != null).ToList());
        foreach (var (c, owner, r) in reminders)
        {
            var app = resolver.LoginUrl(r!)[..^"login".Length];
            var err = await mail.SendTrialReminder(r!, app, owner!.Name, owner.Email, c.Name, Signup.Day(c, c.TrialEndsUtc!.Value), c.TrialDaysLeft(now));
            if (err != null) log.LogWarning("Rappel de fin d'essai non envoyé à {Email} : {Error}", owner.Email, err);
            store.Write(db => { if (db.Clients.FirstOrDefault(x => x.Id == c.Id) is { } x) x.TrialReminderSentUtc = now; });   // une seule tentative
        }

        var ended = store.Write(db =>
        {
            var list = db.Clients.Where(c => c.TrialEnded(now) && c.TrialEndedNotifiedUtc == null).ToList();
            foreach (var c in list) c.TrialEndedNotifiedUtc = now;
            return list.Select(c => c.Id).ToList();
        });
        foreach (var id in ended) await notifier.NotifyClient(id);
    }
}

/// <summary>Pose le cookie de session d'un compte et indique où aller ensuite (mot de passe provisoire, adresse à confirmer, accueil).</summary>
public static class SessionCookie
{
    public static async Task<string> SignIn(Microsoft.AspNetCore.Http.HttpContext http, JsonStore store, Guid userId)
    {
        var (principal, next) = store.Write(d =>
        {
            var u = d.Users.First(x => x.Id == userId);
            u.LastLoginUtc = u.LastActiveUtc = DateTime.UtcNow;
            // mot de passe provisoire reçu par e-mail : sa première utilisation prouve l'adresse (client créé par un revendeur ou par Linkii, membre invité)
            if (u.EmailVerifiedUtc == null && !u.AwaitsVerification) u.EmailVerifiedUtc = DateTime.UtcNow;
            var p = Claims.Build(d, u);
            return (p, u.AwaitsVerification ? "/login/verify" : u.MustChangePassword ? "/login/password" : Claims.Home(p));
        });
        await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignInAsync(http,
            Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true });
        return next;
    }
}
