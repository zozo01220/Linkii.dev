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
        string? password, ExternalIdentity? external, Consents consents, DateTime now, bool expert = false, string? trialCode = null)
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
        if (Billing.FindCode(db, trialCode) is { } tc) { client.TrialCode = tc.Code; tc.Uses++; }
        admin.TermsVersion = Terms.Version; admin.TermsAcceptedUtc = now;
        admin.MarketingOptIn = consents.Marketing; admin.MarketingOptInUtc = consents.Marketing ? now : null;
        admin.NewsOptIn = consents.News; admin.NewsOptInUtc = consents.News ? now : null;
        admin.ExpertMode = expert; admin.ExpertIntroSeen = true;   // mode choisi à l'inscription : pas de présentation

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
        if (client != null && SignupSources.IsSelfService(client.SignupSource) && client.TrialEndsUtc == null && !client.Subscribed && u.Id == OwnerOf(db, client)?.Id)
        {
            client.TrialEndsUtc = now.AddDays(TrialDaysOf(db, client));
            client.Plan = db.Platform.TrialPlan == Plans.Base ? Plans.Base : Plans.Growth;   // l'essai donne la licence choisie dans la console (Growth d'office)
        }
    }

    /// <summary>Durée d'essai d'une organisation : celle de son code d'essai, sinon la durée par défaut.</summary>
    public static int TrialDaysOf(Db db, Tenant c) =>
        c.TrialCode != null && db.Platform.TrialCodes.FirstOrDefault(t => t.Code == c.TrialCode) is { } tc ? Math.Max(1, tc.Days) : Math.Max(1, db.Platform.TrialDays);

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
/// Toutes les 30 minutes : supprime les inscriptions jamais validées, traite les échéances (renouvellement, résiliation arrivée à terme),
/// prévient les écrans d'une organisation dont l'essai vient de se terminer (« Essai terminé »), envoie les e-mails programmés du parcours
/// (voir <see cref="Lifecycle"/>) et supprime les espaces sans plan au terme de la conservation.
/// </summary>
public class SignupWorker(JsonStore store, Notifier notifier, AlertService alerts, LifecycleMail lifecycle, ILogger<SignupWorker> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

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

        // Échéances : résiliations arrivées à terme, renouvellements (simulation : réussis, ou refusés si la console l'a demandé)
        var due = store.Write(db => Billing.RunDue(db, now));
        foreach (var d in due)
        {
            if (d.Kind == "renewed") continue;   // le reçu viendra de Stripe ; en simulation, rien à envoyer
            await notifier.NotifyClient(d.ClientId);
            if (d.Kind == "failed")
            {
                var n = store.Read(db => db.Clients.FirstOrDefault(c => c.Id == d.ClientId) is { } c ? Lifecycle.PaymentFailed(db, c) : null);
                if (n != null) await lifecycle.Send(d.ClientId, n, allAdmins: true);
            }
        }

        // Fin d'essai sans plan : écrans prévenus, conservation avant suppression (espaces en libre-service seulement), e-mail « Essai terminé »
        var ended = store.Write(db =>
        {
            var list = db.Clients.Where(c => c.TrialEnded(now) && !c.Subscribed && c.TrialEndedNotifiedUtc == null).ToList();
            var mails = new List<(Guid, Notice?)>();
            foreach (var c in list)
            {
                c.TrialEndedNotifiedUtc = now;
                if (SignupSources.IsSelfService(c.SignupSource)) c.DeleteOnUtc ??= Billing.DeleteDateAfter(db, now);
                mails.Add((c.Id, c.DeleteOnUtc != null ? Lifecycle.TrialEnded(db, c) : null));
            }
            return mails;
        });
        foreach (var (id, n) in ended)
        {
            await notifier.NotifyClient(id);
            if (n != null) await lifecycle.Send(id, n);
            const string title = "Essai gratuit terminé", body = "Vos écrans ne diffusent plus : choisissez un plan pour les réactiver.";
            if (n != null) await alerts.RaisePush(id, Alerts.Billing, title, body, "/" + Lifecycle.AbonnementPath);   // l'e-mail vient de partir
            else await alerts.Raise(id, Alerts.Billing, title, body, "/" + Lifecycle.AbonnementPath);
        }

        await RunScheduled(now);
        await RunDeletions(now);
        await RunEnterprise(now);
    }

    /// <summary>Offre Entreprise : rappels de fin de pilote (7 et 2 jours) et d'échéance des licences sur site (60, 30, 7 jours), au contact et à l'équipe Linkii.</summary>
    public async Task RunEnterprise(DateTime now)
    {
        if (Subscriptions.ToLocal(now).Hour < Lifecycle.SendHour) return;
        var todo = store.Write(db =>
        {
            var list = new List<(Reseller R, string Email, string Name, Notice N)>();
            var def = db.Resellers.FirstOrDefault(r => r.IsDefault);
            var team = db.Users.Where(u => u.Role == Roles.PlatformAdmin && !u.Disabled).Select(u => (u.Email, u.Name)).ToList();
            void Add(Guid resellerId, string email, string name, Notice n)
            {
                if ((db.Resellers.FirstOrDefault(r => r.Id == resellerId) ?? def) is { } r) list.Add((r, email, name, n));
                if (def != null) foreach (var (e, nm) in team) list.Add((def, e, nm, n with { Subject = "[Copie] " + n.Subject, Button = null, Path = null }));
            }
            foreach (var o in db.Opportunities.Where(o => o.Status == OpportunityStates.Pilot && o.PilotEndsUtc is { } e && e > now))
            {
                var left = (int)Math.Ceiling((o.PilotEndsUtc!.Value - now).TotalDays);
                var hit = Enterprise.PilotWarnings.Where(w => left <= w && !o.Sent.Contains("pilot" + w)).ToList();
                if (hit.Count == 0) continue;
                o.Sent.AddRange(hit.Select(w => "pilot" + w));
                Add(o.ResellerId, o.Email, o.Name, Enterprise.PilotEnding(o, left, o.PilotEndsUtc!.Value.ToString("d MMMM yyyy", Signup.Fr)));
            }
            foreach (var l in db.Licenses.Where(l => !l.Revoked && l.ExpiresUtc > now))
            {
                var left = (int)Math.Ceiling((l.ExpiresUtc - now).TotalDays);
                var hit = Enterprise.LicenseWarnings.Where(w => left <= w && !l.Sent.Contains("lic" + w)).ToList();
                if (hit.Count == 0) continue;
                l.Sent.AddRange(hit.Select(w => "lic" + w));
                var name = db.Opportunities.FirstOrDefault(o => o.Id == l.OpportunityId)?.Name ?? "";
                Add(l.ResellerId, l.Email, name, Enterprise.LicenseEnding(l, left));
            }
            return list;
        });
        foreach (var (r, email, name, n) in todo) await lifecycle.SendTo(r, new[] { (email, name) }, n);
    }

    /// <summary>E-mails programmés (à partir de 9 h, heure de l'organisation) : premiers pas, astuces, rappel de fin d'essai, avertissements de suppression.</summary>
    public async Task RunScheduled(DateTime now)
    {
        var todo = store.Write(db =>
        {
            var p = db.Platform;
            var list = new List<(Guid Id, Notice N, bool All, bool Reminder)>();
            foreach (var c in db.Clients.Where(c => SignupSources.IsSelfService(c.SignupSource) && !c.Blocked && !c.Suspended))
            {
                if (Lifecycle.LocalHour(c, now) < Lifecycle.SendHour) continue;
                var owner = Signup.OwnerOf(db, c);
                if (owner == null || !Signup.IsVerified(owner)) continue;
                if (c.InTrial(now) && !c.Subscribed)
                {
                    var day = Lifecycle.TrialDay(db, c, now);
                    var paired = db.Screens.Any(s => s.ClientId == c.Id && s.Token != null);
                    if (owner.NewsOptIn && p.MailFirstSteps && day >= 1 && !paired && Mark(c, "first")) list.Add((c.Id, Lifecycle.FirstSteps(c), false, false));
                    var tips = Lifecycle.TipDays(Signup.TrialDaysOf(db, c));
                    for (var i = 0; i < tips.Length; i++)
                        if (owner.NewsOptIn && p.MailTips && day >= tips[i] && Mark(c, "tip" + tips[i]))
                            list.Add((c.Id, Lifecycle.Tip(c, db.Playlists.Any(x => x.ClientId == c.Id && x.Draft.Count > 0), i), false, false));
                    if (c.TrialEndsUtc - now <= TimeSpan.FromDays(Math.Max(1, p.TrialReminderDays)) && c.TrialReminderSentUtc == null)
                    {
                        c.TrialReminderSentUtc = now;   // une seule tentative
                        list.Add((c.Id, Lifecycle.TrialReminder(db, c, c.TrialDaysLeft(now)), false, true));
                    }
                }
                if (c.DeleteOnUtc is { } del && !c.Subscribed && del > now)
                {
                    // le plus proche des seuils atteints ; les seuils plus lointains déjà dépassés sont marqués sans envoi
                    var left = (int)Math.Ceiling((del - now).TotalDays);
                    var hit = Lifecycle.DeleteWarnings.Where(w => left <= w && !c.LifecycleSent.Contains("del" + w)).ToList();
                    if (hit.Count > 0)
                    {
                        foreach (var w in hit) c.LifecycleSent.Add("del" + w);
                        list.Add((c.Id, Lifecycle.DeleteWarning(c, left), true, false));
                    }
                }
            }
            return list;
        });
        foreach (var (id, n, all, reminder) in todo)
        {
            await lifecycle.Send(id, n, all);
            // rappel de fin d'essai : l'alerte prévient aussi les autres destinataires choisis (application, téléphone)
            if (reminder) await alerts.RaisePush(id, Alerts.Billing, "Votre essai se termine bientôt", n.Title, "/" + Lifecycle.AbonnementPath);
        }
    }

    /// <summary>Note qu'un e-mail du parcours est parti : faux s'il était déjà parti.</summary>
    private static bool Mark(Tenant c, string key) { if (c.LifecycleSent.Contains(key)) return false; c.LifecycleSent.Add(key); return true; }

    /// <summary>Espaces sans plan arrivés au terme de la conservation : supprimés définitivement, puis « Bonne continuation » au propriétaire.</summary>
    public async Task RunDeletions(DateTime now)
    {
        var gone = store.Write(db =>
        {
            var list = new List<(Reseller R, string Email, string Name, string Org, List<string> Files)>();
            foreach (var c in db.Clients.Where(c => c.DeleteOnUtc is { } del && del <= now && !c.Subscribed && SignupSources.IsSelfService(c.SignupSource)).ToList())
            {
                var owner = Signup.OwnerOf(db, c);
                var r = db.Resellers.FirstOrDefault(x => x.Id == c.ResellerId);
                var files = Signup.RemoveClient(db, c.Id);
                if (r != null && owner != null) list.Add((r, owner.Email, owner.Name, c.Name, files));
                log.LogInformation("Espace « {Org} » supprimé au terme de la conservation", c.Name);
            }
            return list;
        });
        foreach (var g in gone)
        {
            foreach (var f in g.Files) try { File.Delete(Path.Combine(AppPaths.MediaDir, Path.GetFileName(f))); } catch (IOException) { }
            await lifecycle.SendTo(g.R, new[] { (g.Email, g.Name) }, Lifecycle.Deleted(g.Org));
        }
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
