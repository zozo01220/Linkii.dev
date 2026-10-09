using System.Net;
using System.Text;
using Linkii.Poc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Création de compte en libre-service : consentements, confirmation de l'adresse, essai gratuit, purge des comptes non validés.</summary>
public class SignupTests
{
    readonly Db db = new();
    readonly Reseller linkii = new() { Name = "Linkii", IsDefault = true, AllowSelfSignup = true };
    readonly DateTime now = new(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);
    static readonly Consents Yes = new(Terms: true, Marketing: true, News: false);

    public SignupTests() => db.Resellers.Add(linkii);

    (Tenant C, User U, string Token) EmailSignup(string email = "marie@horizon.ch", string org = "Horizon SA")
    {
        var (c, u, t, err) = Signup.Create(db, linkii, org, "Marie Dupont", email, "motdepasse", null, Yes, now);
        Assert.Null(err);
        return (c!, u!, t!);
    }

    [Fact]
    public void Terms_are_mandatory_commercial_and_news_are_not()
    {
        var (_, _, _, err) = Signup.Create(db, linkii, "Horizon SA", "Marie", "marie@horizon.ch", "motdepasse", null, new Consents(false, true, true), now);
        Assert.NotNull(err);
        Assert.Empty(db.Clients);

        var (c, u, _) = EmailSignup();
        Assert.Equal(Terms.Version, u.TermsVersion);
        Assert.Equal(now, u.TermsAcceptedUtc);
        Assert.True(u.MarketingOptIn);
        Assert.False(u.NewsOptIn);
        Assert.Null(u.NewsOptInUtc);
        Assert.Equal(SignupSources.Email, c.SignupSource);
    }

    [Fact]
    public void Email_signup_awaits_verification_and_trial_starts_at_confirmation()
    {
        db.Platform.TrialDays = 10;
        var (c, u, token) = EmailSignup();
        Assert.True(u.AwaitsVerification);
        Assert.False(Signup.IsVerified(u));
        Assert.Null(c.TrialEndsUtc);   // l'essai ne démarre pas avant la confirmation

        var later = now.AddHours(5);
        var (result, who, client) = Signup.Confirm(db, token, later);
        Assert.Equal(Signup.ConfirmResult.Ok, result);
        Assert.Equal(u.Id, who!.Id);
        Assert.False(u.AwaitsVerification);
        Assert.Equal(later, u.EmailVerifiedUtc);
        Assert.Equal(later.AddDays(10), client!.TrialEndsUtc);
        Assert.Equal(10, c.TrialDaysLeft(later));

        // lien à usage unique
        Assert.Equal(Signup.ConfirmResult.Invalid, Signup.Confirm(db, token, later).Result);
    }

    [Fact]
    public void Expired_or_replaced_links_do_not_validate()
    {
        db.Platform.VerifyLinkHours = 48;
        var (_, u, first) = EmailSignup();
        Assert.Equal(Signup.ConfirmResult.Expired, Signup.Confirm(db, first, now.AddHours(49)).Result);
        Assert.Null(u.EmailVerifiedUtc);

        var second = Signup.NewToken(u, 48, now.AddHours(50));
        Assert.Equal(Signup.ConfirmResult.Invalid, Signup.Confirm(db, first, now.AddHours(51)).Result);   // remplacé par un envoi plus récent
        Assert.Equal(Signup.ConfirmResult.Ok, Signup.Confirm(db, second, now.AddHours(51)).Result);
        Assert.Equal(Signup.ConfirmResult.Invalid, Signup.Confirm(db, "n-importe-quoi", now).Result);
    }

    [Fact]
    public void Resending_is_limited_to_once_a_minute_and_five_an_hour()
    {
        var (_, u, _) = EmailSignup();   // premier envoi à la création
        Assert.NotNull(Signup.ResendWait(u, now.AddSeconds(30)));
        Assert.Null(Signup.ResendWait(u, now.AddSeconds(61)));
        for (var i = 1; i <= 4; i++) Signup.NewToken(u, 48, now.AddMinutes(2 * i));
        Assert.NotNull(Signup.ResendWait(u, now.AddMinutes(20)));    // cinq envois dans l'heure
        Assert.Null(Signup.ResendWait(u, now.AddMinutes(61)));
    }

    [Fact]
    public void A_typo_in_the_address_can_be_corrected_before_validation()
    {
        var (_, u, first) = EmailSignup("marie@horizn.ch");
        var (token, err) = Signup.ChangeEmail(db, u, "Marie@Horizon.ch", now.AddSeconds(10));   // aussitôt : une correction n'attend pas la minute
        Assert.Null(err);
        Assert.Equal("marie@horizon.ch", u.Email);
        Assert.Equal(Signup.ConfirmResult.Invalid, Signup.Confirm(db, first, now.AddMinutes(3)).Result);
        Assert.Equal(Signup.ConfirmResult.Ok, Signup.Confirm(db, token, now.AddMinutes(3)).Result);
        Assert.NotNull(Signup.ChangeEmail(db, u, "autre@horizon.ch", now.AddMinutes(5)).Error);   // validée : plus modifiable ici
    }

    [Fact]
    public void A_new_signup_replaces_an_unverified_one_but_not_a_verified_one()
    {
        var (old, _, _) = EmailSignup(org: "Horizn SA");
        var (c, _, _) = EmailSignup(org: "Horizon SA");
        Assert.DoesNotContain(db.Clients, x => x.Id == old.Id);
        Assert.DoesNotContain(db.Areas, a => a.ClientId == old.Id);
        Assert.Single(db.Users);

        Signup.Verify(db, db.Users[0], c, now);
        var (_, _, _, err) = Signup.Create(db, linkii, "Autre", "Marie", "marie@horizon.ch", "motdepasse", null, Yes, now);
        Assert.NotNull(err);
        Assert.Single(db.Clients);
    }

    [Fact]
    public void Microsoft_or_Google_signup_is_validated_at_once()
    {
        var id = new ExternalIdentity(SignupSources.Microsoft, "sub-123", "Paul@Pins.ch", "Paul Martin");
        var (c, u, token, err) = Signup.Create(db, linkii, "Collège des Pins", "Paul Martin", "", null, id, Yes, now);
        Assert.Null(err);
        Assert.Null(token);
        Assert.Equal("paul@pins.ch", u!.Email);
        Assert.False(u.HasPassword);
        Assert.Equal((SignupSources.Microsoft, "sub-123"), (u.ExternalProvider, u.ExternalId));
        Assert.Equal(now, u.EmailVerifiedUtc);
        Assert.Equal(now.AddDays(db.Platform.TrialDays), c!.TrialEndsUtc);
        Assert.Equal(SignupSources.Microsoft, c.SignupSource);
    }

    [Fact]
    public void Accounts_created_by_a_reseller_have_no_trial()
    {
        var (c, _) = Tenancy.CreateClient(db, linkii, "Fondation Arc", "Admin", "admin@arc.ch", "Hq7k-Wm3p", mustChangePassword: true, source: SignupSources.Reseller);
        var u = db.Users.Single();
        Assert.False(u.AwaitsVerification);   // validé à la première connexion avec le mot de passe provisoire
        Signup.Verify(db, u, c, now);
        Assert.Null(c!.TrialEndsUtc);
    }

    [Fact]
    public void Unverified_accounts_are_purged_after_the_configured_delay()
    {
        db.Platform.PurgeUnverifiedDays = 7;
        var (c, u, _) = EmailSignup();
        u.CreatedUtc = now;
        Assert.Empty(Signup.Stale(db, now.AddDays(6)));
        var stale = Signup.Stale(db, now.AddDays(8));
        Assert.Single(stale);
        Signup.Remove(db, stale[0]);
        Assert.Empty(db.Users);
        Assert.DoesNotContain(db.Clients, x => x.Id == c.Id);
    }

    [Fact]
    public void Screens_stop_when_the_trial_ends_and_resume_when_extended()
    {
        var (c, u, token) = EmailSignup();
        Signup.Confirm(db, token, now);
        var s = new Screen { ClientId = c.Id, Token = "t" };
        db.Screens.Add(s);
        Assert.Null(Tenancy.Blocked(db, s));

        c.TrialEndsUtc = DateTime.UtcNow.AddMinutes(-1);
        Assert.Equal("trial", Tenancy.Blocked(db, s));
        Assert.False(Tenancy.IsActive(db, s));

        Signup.Extend(c, 5, DateTime.UtcNow);
        Assert.Null(Tenancy.Blocked(db, s));
        Assert.Equal(5, c.TrialDaysLeft(DateTime.UtcNow));

        c.Suspended = true;
        Assert.Equal("suspended", Tenancy.Blocked(db, s));
    }

    [Fact]
    public void Existing_accounts_that_already_signed_in_are_marked_verified()
    {
        var old = new User { Email = "ancien@ecole.ch", CreatedUtc = now.AddDays(-30), LastLoginUtc = now.AddDays(-2) };
        var invited = new User { Email = "invite@ecole.ch", MustChangePassword = true };
        db.Users.AddRange(new[] { old, invited });
        Signup.MarkVerified(db);
        Assert.Equal(now.AddDays(-30), old.EmailVerifiedUtc);
        Assert.Null(invited.EmailVerifiedUtc);
    }

    static string IdToken(string json) => "x." + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    [Fact]
    public void Google_identity_requires_a_verified_address()
    {
        Assert.Null(ExternalLogin.Identity(SignupSources.Google, IdToken("""{"sub":"1","email":"a@b.ch","email_verified":false}""")));
        var id = ExternalLogin.Identity(SignupSources.Google, IdToken("""{"sub":"1","email":"A@B.ch","email_verified":true,"name":"Anne"}"""));
        Assert.Equal(new ExternalIdentity(SignupSources.Google, "1", "a@b.ch", "Anne"), id);
        // Microsoft : identifiant de connexion à défaut d'e-mail
        Assert.Equal("paul@pins.ch", ExternalLogin.Identity(SignupSources.Microsoft, IdToken("""{"sub":"2","preferred_username":"paul@pins.ch"}"""))!.Email);
        Assert.Null(ExternalLogin.Identity(SignupSources.Microsoft, IdToken("""{"sub":"2"}""")));
    }
}

/// <summary>Bout en bout : lien de confirmation, garde « à valider », écrans en fin d'essai.</summary>
[Collection("platform")]
public class SignupFlowTests(Platform p)
{
    [Fact]
    public async Task Confirmation_link_signs_in_and_starts_the_trial()
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        var (client, user, token, _) = store.Write(db => Signup.Create(db, p.R1, "Studio Lumen", "Paul", "paul@lumen.ch", "motdepasse", null, new Consents(true, false, true), DateTime.UtcNow));

        var r = await p.Client().GetAsync("/login/verify/confirm?t=" + Uri.EscapeDataString(token!));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/login/verify/done", r.Headers.Location!.ToString());
        Assert.Contains(r.Headers.GetValues("Set-Cookie"), c => c.StartsWith("linkii.auth"));
        Assert.True(store.Read(db => db.Users.First(u => u.Id == user!.Id).EmailVerifiedUtc != null && db.Clients.First(c => c.Id == client!.Id).TrialEndsUtc != null));

        var again = await p.Client().GetAsync("/login/verify/confirm?t=" + Uri.EscapeDataString(token!));
        Assert.Equal("/login/verify/done?state=invalid", again.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Player_shows_trial_ended()
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        store.Write(db => { db.Clients.First(c => c.Id == p.A.Id).TrialEndsUtc = DateTime.UtcNow.AddDays(-1); });
        try
        {
            var r = await p.Client().SendAsync(Platform.Get("/api/player/version", p.TokenA));
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Contains("\"trial\"", await r.Content.ReadAsStringAsync());
        }
        finally { store.Write(db => { db.Clients.First(c => c.Id == p.A.Id).TrialEndsUtc = null; }); }
    }

    [Fact]
    public async Task Signup_page_offers_terms_and_both_optional_consents()
    {
        var html = WebUtility.HtmlDecode(await (await p.Client().GetAsync("/login?signup=1")).Content.ReadAsStringAsync());
        Assert.Contains("conditions générales de Linkii", html);
        Assert.Contains("offres commerciales", html);
        Assert.Contains("nouveautés et astuces", html);
        Assert.Contains("terms-dialog", html);
    }
}
