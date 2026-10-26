using Linkii.Poc;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Essai, abonnement en libre-service (paiement simulé), échéances, résiliation, et licences sur site (voir Billing, Enterprise).</summary>
public class BillingTests
{
    readonly Db db = new();
    readonly Reseller linkii = new() { Name = "Linkivi", IsDefault = true, AllowSelfSignup = true };
    readonly DateTime now = new(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc);
    static readonly Consents Yes = new(Terms: true, Marketing: false, News: true);

    public BillingTests() => db.Resellers.Add(linkii);

    /// <summary>Organisation inscrite et validée (essai démarré), avec n écrans appairés.</summary>
    Tenant Trial(int screens = 6, string? code = null)
    {
        var (c, u, token, err) = Signup.Create(db, linkii, "Horizon SA", "Marie", $"marie{db.Clients.Count}@horizon.ch", "motdepasse", null, Yes, now, trialCode: code);
        Assert.Null(err);
        Signup.Confirm(db, token, now);
        for (var i = 0; i < screens; i++) db.Screens.Add(new Screen { ClientId = c!.Id, Name = "É" + i, Token = "t" + i });
        return c!;
    }

    [Fact]
    public void Trial_gives_growth_and_a_code_gives_its_own_duration()
    {
        db.Platform.TrialCodes.Add(new TrialCode { Code = "SALON30", Days = 30 });
        var plain = Trial();
        Assert.Equal(Plans.Growth, plain.Plan);
        Assert.Equal(now.AddDays(7), plain.TrialEndsUtc);

        var coded = Trial(code: "salon30");   // la casse ne compte pas
        Assert.Equal("SALON30", coded.TrialCode);
        Assert.Equal(now.AddDays(30), coded.TrialEndsUtc);
        Assert.Equal(1, db.Platform.TrialCodes[0].Uses);
        Assert.Equal(30, Billing.TrialDaysFor(db, "SALON30"));
        db.Platform.TrialCodes[0].Active = false;
        Assert.Equal(7, Billing.TrialDaysFor(db, "SALON30"));   // code désactivé : durée par défaut
    }

    [Fact]
    public void Subscribing_during_the_trial_charges_nothing_until_its_end()
    {
        var c = Trial();
        var pay = Billing.Subscribe(db, c, Plans.Base, "monthly", "4242", "10/29", now.AddDays(2), simulated: true);
        Assert.Null(pay);
        Assert.Empty(db.Payments);
        Assert.Equal(c.TrialEndsUtc, c.NextRenewalUtc);
        Assert.Equal(Plans.Base, c.Plan);

        // la fin de l'essai n'arrête pas les écrans d'un abonné : le premier prélèvement suit
        var after = c.TrialEndsUtc!.Value.AddMinutes(5);
        Assert.Equal(ClientStates.Active, ClientStates.Of(c, Signup.OwnerOf(db, c), after));
        var due = Billing.RunDue(db, after);
        Assert.Contains(due, d => d.ClientId == c.Id && d.Kind == "renewed");
        var p = Assert.Single(db.Payments);
        Assert.True(p.Simulated);
        Assert.True(p.First);
        Assert.Equal(6 * 15m, p.Amount);
        Assert.Null(c.TrialEndsUtc);
    }

    [Fact]
    public void Subscribing_after_the_trial_pays_at_once_and_cancels_the_deletion()
    {
        var c = Trial(screens: 2);
        var ended = c.TrialEndsUtc!.Value.AddDays(3);
        c.DeleteOnUtc = Billing.DeleteDateAfter(db, ended);
        c.LifecycleSent.Add("del15");
        Assert.Equal(ClientStates.Ended, ClientStates.Of(c, Signup.OwnerOf(db, c), ended));

        var pay = Billing.Subscribe(db, c, Plans.Growth, "annual", "4242", "10/29", ended, simulated: true);
        Assert.NotNull(pay);
        Assert.Equal(Math.Round(2 * 29m * 12 * 0.85m, 2), pay!.Amount);
        Assert.Null(c.DeleteOnUtc);
        Assert.Empty(c.LifecycleSent);
        Assert.Equal(Subscriptions.NextAfter(ended, ended.Day, 12), c.NextRenewalUtc);
    }

    [Fact]
    public void A_refused_renewal_blocks_and_base_applies_at_the_renewal()
    {
        var c = Trial(screens: 3);
        var start = c.TrialEndsUtc!.Value.AddDays(1);
        Billing.Subscribe(db, c, Plans.Growth, "monthly", "4242", "10/29", start, simulated: true);
        Billing.ScheduleBase(c);
        Assert.Equal(Plans.Growth, c.Plan);   // Growth jusqu'à l'échéance

        var renewal = c.NextRenewalUtc!.Value;
        Billing.RunDue(db, renewal);
        Assert.Equal(Plans.Base, c.Plan);
        Assert.Equal(3 * 15m, db.Payments.Last().Amount);

        c.SimNextRenewalFails = true;
        var due = Billing.RunDue(db, c.NextRenewalUtc!.Value);
        Assert.Contains(due, d => d.Kind == "failed");
        Assert.True(c.Blocked);
        Assert.Equal(Subscriptions.PaymentReason, c.BlockedReason);
    }

    [Fact]
    public void An_added_screen_costs_its_prorata_of_the_period()
    {
        var c = Trial(screens: 1);
        Assert.Equal(0, Billing.ScreenProrata(db, c, now));   // pendant l'essai : rien
        var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        c.TrialEndsUtc = start.AddDays(-1);
        Billing.Subscribe(db, c, Plans.Growth, "monthly", "4242", "10/29", start, simulated: true);
        var half = start.AddDays(15);   // novembre : 30 jours, la moitié reste
        Assert.Equal(14.5m, Billing.ScreenProrata(db, c, half));
        var p = Billing.ChargeScreen(db, c, "Accueil 2", half, simulated: true);
        Assert.Equal(c.NextRenewalUtc, start.AddMonths(1));   // la date anniversaire ne bouge pas
        Assert.StartsWith("+1 écran", p!.Label);
    }

    [Fact]
    public void A_cancellation_ends_at_the_renewal_then_the_space_waits_for_deletion()
    {
        var c = Trial(screens: 1);
        var start = c.TrialEndsUtc!.Value.AddDays(1);
        Billing.Subscribe(db, c, Plans.Growth, "monthly", "4242", "10/29", start, simulated: true);
        Billing.Cancel(c);
        var end = c.NextRenewalUtc!.Value;
        Assert.Equal(end, c.CancelAtUtc);
        Assert.False(c.SubscriptionEnded(end.AddMinutes(-1)));   // rien n'est coupé avant

        var due = Billing.RunDue(db, end.AddMinutes(1));
        Assert.Contains(due, d => d.Kind == "ended");
        Assert.Single(db.Payments);   // pas de renouvellement
        Assert.False(c.Subscribed);
        Assert.Equal(ClientStates.Cancelled, ClientStates.Of(c, Signup.OwnerOf(db, c), end.AddMinutes(1)));
        Assert.Equal(Billing.DeleteDateAfter(db, end.AddMinutes(1)), c.DeleteOnUtc);
    }

    [Fact]
    public void Clients_created_by_the_console_are_never_scheduled_for_deletion()
    {
        var (c, _) = Tenancy.CreateClient(db, linkii, "Pilote", "Paul", "paul@pilote.ch", "motdepasse", source: SignupSources.Console);
        c!.NextRenewalUtc = now; c.CancelAtUtc = now;
        Billing.RunDue(db, now.AddMinutes(1));
        Assert.Null(c.DeleteOnUtc);
    }

    [Fact]
    public void A_license_key_verifies_until_it_is_replaced_revoked_or_expired()
    {
        var box = new SecretBox(new EphemeralDataProtectionProvider());
        var l = Enterprise.Issue(db, box, new License { Org = "Grande Organisation", Screens = 200, ExpiresUtc = now.AddYears(1) });
        Assert.StartsWith("LKI1.", l.Key);
        Assert.NotEqual("", db.Platform.LicensePublicKey);

        var ok = Enterprise.Verify(db, l.Key, "10.0.0.1", now);
        Assert.True(ok.Valid);
        Assert.Equal(200, ok.Screens);
        Assert.Equal(now, l.LastCheckUtc);

        Assert.False(Enterprise.Verify(db, l.Key[..^4] + "AAAA", "x", now).Valid);   // signature altérée
        Assert.Equal("expired", Enterprise.Verify(db, l.Key, "x", now.AddYears(2)).Reason);

        var old = l.Key;
        l.IssuedUtc = now.AddDays(1); Enterprise.Issue(db, box, l);   // réémise : nouvelle clé, même paire de clés de signature
        Assert.Equal("replaced", Enterprise.Verify(db, old, "x", now).Reason);
        l.Revoked = true;
        Assert.Equal("revoked", Enterprise.Verify(db, l.Key, "x", now).Reason);
    }
}
