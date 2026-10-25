namespace Linkii.Poc;

/// <summary>Ce qu'un paiement demande au client : montant et libellé (« Growth × 6 écrans, mensuel »). Affiché par la fenêtre de paiement.</summary>
public record PayRequest(decimal Amount, string Label, string Detail);

/// <summary>
/// Abonnement en libre-service (côté client) : choix du plan, écran ajouté, passage à Base ou à Growth, résiliation.
/// Le paiement lui-même passe par <c>PayDialog</c> : en mode simulation, une fenêtre remplace Stripe (paiement réussi ou refusé) ;
/// en mode Stripe, c'est là que se brancheront Checkout et le portail client. Les règles ci-dessous ne dépendent pas du mode.
/// Prix : écrans appairés × prix de la licence (<see cref="Subscriptions"/>) ; un écran ajouté est facturé au prorata, un écran retiré sort à l'échéance.
/// </summary>
public static class Billing
{
    /// <summary>Durée d'essai d'une inscription : celle du code (actif) s'il y en a un, sinon la durée par défaut.</summary>
    public static int TrialDaysFor(Db db, string? code) =>
        FindCode(db, code) is { } tc ? Math.Max(1, tc.Days) : Math.Max(1, db.Platform.TrialDays);

    public static TrialCode? FindCode(Db db, string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : db.Platform.TrialCodes.FirstOrDefault(c => c.Active && string.Equals(c.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string NormalizeCode(string code) => new(code.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Date de suppression prévue d'un espace sans plan : fin de l'essai + conservation.</summary>
    public static DateTime DeleteDateAfter(Db db, DateTime end) => end.AddDays(Math.Max(1, db.Platform.RetentionDays));

    // ---------- Montants ----------

    /// <summary>Prix mensuel d'un écran pour une licence.</summary>
    public static decimal Unit(Db db, string plan) => Subscriptions.Unit(Subscriptions.PricesOf(db), plan);

    /// <summary>Montant d'une période (mois, ou année remisée) pour n écrans.</summary>
    public static decimal PeriodAmount(Db db, string plan, string period, int screens)
    {
        var monthly = screens * Unit(db, plan);
        return Math.Round(period == "annual" ? monthly * 12 * (1 - db.Platform.AnnualDiscountPct / 100m) : monthly, 2);
    }

    /// <summary>Part restante de la période en cours (0 à 1) : de maintenant jusqu'à l'échéance.</summary>
    public static decimal RemainingShare(Tenant c, DateTime now)
    {
        if (c.NextRenewalUtc is not { } next || next <= now) return 0;
        var start = Subscriptions.NextAfter(next, c.AnchorDay, -Subscriptions.StepMonths(c));
        var total = (next - start).TotalDays;
        return total <= 0 ? 0 : (decimal)Math.Clamp((next - now).TotalDays / total, 0, 1);
    }

    /// <summary>Supplément au prorata d'un écran de plus (période en cours). 0 pendant l'essai ou sans abonnement.</summary>
    public static decimal ScreenProrata(Db db, Tenant c, DateTime now) =>
        c.Subscribed && !c.InTrial(now) ? Math.Round(PeriodAmount(db, c.Plan, c.BillingPeriod, 1) * RemainingShare(c, now), 2) : 0;

    /// <summary>Supplément au prorata du passage de Base à Growth pour les écrans actuels.</summary>
    public static decimal UpgradeProrata(Db db, Tenant c, DateTime now)
    {
        if (!c.Subscribed || c.InTrial(now)) return 0;
        var screens = Subscriptions.ScreensOf(db, c);
        var diff = PeriodAmount(db, Plans.Growth, c.BillingPeriod, screens) - PeriodAmount(db, Plans.Base, c.BillingPeriod, screens);
        return Math.Round(diff * RemainingShare(c, now), 2);
    }

    /// <summary>Jours restants avant l'échéance (pour « prorata, 17 jours »).</summary>
    public static int DaysToRenewal(Tenant c, DateTime now) => c.NextRenewalUtc is { } n && n > now ? (int)Math.Ceiling((n - now).TotalDays) : 0;

    // ---------- Opérations (dans un Store.Write, après un paiement réussi quand il y en a un) ----------

    /// <summary>
    /// « Choisir mon plan ». Pendant l'essai : rien n'est prélevé, le premier prélèvement aura lieu à la fin de l'essai (date anniversaire).
    /// Après l'essai (ou une résiliation arrivée à terme) : premier paiement tout de suite, les écrans reprennent.
    /// </summary>
    public static Payment? Subscribe(Db db, Tenant c, string plan, string period, string last4, string expiry, DateTime now, bool simulated)
    {
        c.Plan = plan == Plans.Growth ? Plans.Growth : Plans.Base;
        c.PendingPlan = null; c.PendingPeriod = null;
        c.BillingPeriod = period == "annual" ? "annual" : "monthly";
        c.CancelAtUtc = null;
        c.CardLast4 = last4; c.CardExpiry = expiry;
        c.DeleteOnUtc = null;
        c.LifecycleSent.RemoveAll(k => k.StartsWith("del"));
        if (c.InTrial(now))
        {
            c.NextRenewalUtc = c.TrialEndsUtc;
            c.AnchorDay = c.TrialEndsUtc!.Value.Day;
            return null;
        }
        c.NextRenewalUtc = null;
        var pay = Subscriptions.RecordClient(db, c, now, simulated);
        if (c.Blocked && c.BlockedReason == Subscriptions.PaymentReason) { c.Blocked = false; c.BlockedReason = ""; }
        return pay;
    }

    /// <summary>Montant du premier paiement si le client s'abonne maintenant (0 pendant l'essai : prélevé à la fin).</summary>
    public static decimal SubscribeNow(Db db, Tenant c, string plan, string period, DateTime now) =>
        c.InTrial(now) ? 0 : PeriodAmount(db, plan, period, Subscriptions.ScreensOf(db, c));

    /// <summary>Écran appairé chez un abonné : supplément au prorata (déjà payé par la fenêtre de paiement, ou prélevé d'office).</summary>
    public static Payment? ChargeScreen(Db db, Tenant c, string screenName, DateTime now, bool simulated) =>
        Subscriptions.RecordExtra(db, c, ScreenProrata(db, c, now), $"+1 écran ({screenName}), prorata", now, simulated);

    /// <summary>Base → Growth : immédiat, différence au prorata.</summary>
    public static Payment? Upgrade(Db db, Tenant c, DateTime now, bool simulated)
    {
        var amount = UpgradeProrata(db, c, now);
        c.Plan = Plans.Growth; c.PendingPlan = null;
        return Subscriptions.RecordExtra(db, c, amount, "Passage à Growth, prorata", now, simulated);
    }

    /// <summary>Growth → Base : à l'échéance. Rien n'est supprimé, les fonctions Growth se verrouillent.</summary>
    public static void ScheduleBase(Tenant c) { if (c.IsGrowth) c.PendingPlan = Plans.Base; }
    public static void CancelBase(Tenant c) => c.PendingPlan = null;

    /// <summary>Résiliation à l'échéance en cours (sans remboursement). Annulable jusque-là.</summary>
    public static void Cancel(Tenant c) { if (c.NextRenewalUtc is { } n) c.CancelAtUtc = n; }
    public static void Resume(Tenant c) => c.CancelAtUtc = null;

    /// <summary>Mensuel ↔ annuel : à l'échéance (la période en cours est déjà payée). null : garder la facturation actuelle.</summary>
    public static void SwitchPeriod(Tenant c, string? period) =>
        c.PendingPeriod = period == null || period == c.BillingPeriod ? null : period == "annual" ? "annual" : "monthly";

    // ---------- Ce que le passage à Base verrouille ----------

    public record GrowthUse(int Walls, int Alerts, int Qr);

    /// <summary>Fonctions Growth réellement utilisées par l'organisation (murs, alertes actives, QR codes comptés).</summary>
    public static GrowthUse UsageOf(Db db, Tenant c) => new(
        db.Areas.Where(a => a.ClientId == c.Id).Sum(a => a.Zones.Count(z => z.IsWall)),
        c.AlertsOn ? Math.Max(1, c.AlertTypes.Count) : 0,
        db.Playlists.Where(p => p.ClientId == c.Id).Sum(p => p.Draft.Count(i => i.QrOn)));

    // ---------- Échéances (travail de fond) ----------

    public record Due(Guid ClientId, string Kind, decimal Amount);   // Kind : renewed | failed | ended

    /// <summary>
    /// À chaque passage : résiliations arrivées à terme (écrans coupés, conservation avant suppression), puis renouvellements échus.
    /// En simulation, le renouvellement est réussi sauf si la console a demandé un refus ; avec Stripe, il viendra du webhook (rien ici).
    /// </summary>
    public static List<Due> RunDue(Db db, DateTime now)
    {
        var done = new List<Due>();
        foreach (var c in db.Clients.Where(c => c.CancelAtUtc is { } at && at <= now && c.NextRenewalUtc != null))
        {
            c.NextRenewalUtc = null; c.PendingPlan = null;
            c.TrialEndsUtc = null;   // abonné pendant l'essai puis résilié avant le premier prélèvement
            if (SignupSources.IsSelfService(c.SignupSource)) c.DeleteOnUtc = DeleteDateAfter(db, now);   // client d'un revendeur ou de Linkii : jamais supprimé automatiquement
            c.LifecycleSent.RemoveAll(k => k.StartsWith("del"));
            done.Add(new(c.Id, "ended", 0));
        }
        if (db.Platform.PaymentMode != PaymentModes.Simulation) return done;
        foreach (var c in db.Clients.Where(c => c.NextRenewalUtc is { } n && n <= now && !c.Blocked && c.CancelAtUtc == null).ToList())
        {
            if (c.SimNextRenewalFails)
            {
                c.SimNextRenewalFails = false;
                c.Blocked = true; c.Suspended = false; c.BlockedReason = Subscriptions.PaymentReason;
                done.Add(new(c.Id, "failed", Subscriptions.ClientAmount(db, c)));
                continue;
            }
            if (Subscriptions.RecordClient(db, c, now, simulated: true) is { } pay) done.Add(new(c.Id, "renewed", pay.Amount));
        }
        return done;
    }
}
