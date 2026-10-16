using System.Globalization;

namespace Linkii.Poc;

/// <summary>Un paiement de renouvellement (mensuel ou annuel) d'un client : journal des paiements encaissés par Linkii.</summary>
public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Toujours « client » : les revendeurs ne paient pas Linkii, ils en reçoivent une commission.</summary>
    public string Kind { get; set; } = "client";
    public Guid? ResellerId { get; set; }
    public Guid? ClientId { get; set; }
    public string Name { get; set; } = "";
    public DateTime PaidUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Période couverte : du renouvellement payé jusqu'au suivant.</summary>
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    /// <summary>monthly | annual</summary>
    public string Period { get; set; } = "monthly";
    public int Screens { get; set; }
    public decimal Amount { get; set; }
    /// <summary>Frais de paiement (estimés tant que Stripe n'envoie pas le montant réel).</summary>
    public decimal Fee { get; set; }
    /// <summary>Commission du revendeur au moment du paiement : taux figé, montant calculé sur le net (montant moins frais). 0 pour un client direct.</summary>
    public int CommissionPct { get; set; }
    public decimal Commission { get; set; }
    /// <summary>Premier paiement : fixe la date anniversaire et met fin à l'essai.</summary>
    public bool First { get; set; }
}

/// <summary>Versement d'un mois à un revendeur (dernier jour du mois) : enregistré à la main une fois le virement fait.</summary>
public class Payout
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResellerId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public DateTime PaidUtc { get; set; }
    public string Reference { get; set; } = "";
    /// <summary>Montant versé, figé au moment du versement.</summary>
    public decimal Amount { get; set; }
}

/// <summary>
/// Abonnements : tous les clients (directs ou rattachés à un revendeur) paient Linkii au tarif unique, au mois ou à l'année. La date anniversaire est celle du premier
/// paiement et ne bouge plus ; sans paiement à cette date, le client est bloqué automatiquement (pas de relance). Un revendeur reçoit de Linkii, le dernier jour du mois,
/// une commission sur le net (montant payé moins frais) des paiements reçus dans le mois ; un paiement annuel est commissionné d'un coup.
/// En attendant Stripe, un bouton enregistre à la main le paiement du renouvellement.
/// </summary>
public static class Subscriptions
{
    public const string PaymentReason = "payment";

    public record Prices(decimal Base, decimal Growth, int AnnualPct);
    public static Prices PricesOf(Db db) => new(db.Platform.PriceBase, db.Platform.PriceGrowth, db.Platform.AnnualDiscountPct);
    public static decimal Unit(Prices p, string plan) => plan == Plans.Growth ? p.Growth : p.Base;

    /// <summary>« 1'234.50 » (apostrophe des milliers, deux décimales).</summary>
    public static string Fmt(decimal x) => x.ToString("#,##0.00", CultureInfo.InvariantCulture).Replace(",", "'");

    public static int ScreensOf(Db db, Tenant c) => db.Screens.Count(s => s.ClientId == c.Id && s.Token != null);
    public static int UsersOf(Db db, Tenant c) => db.Users.Count(u => u.ClientId == c.Id && Roles.IsClient(u.Role) && !u.Disabled);

    /// <summary>Un client compte dans le chiffre d'affaires quand il est actif : ni en essai, ni à valider, ni suspendu, ni bloqué.</summary>
    public static bool Billable(Db db, Tenant c, DateTime now) => ClientStates.Of(c, Signup.OwnerOf(db, c), now) == ClientStates.Active;

    /// <summary>Montant mensuel d'un client : écrans × prix de sa licence.</summary>
    public static decimal MonthlyOf(Db db, Tenant c) => ScreensOf(db, c) * Unit(PricesOf(db), c.Plan);

    /// <summary>Montant d'un renouvellement : au mois, ou 12 mois remisés pour un paiement annuel.</summary>
    public static decimal ClientAmount(Db db, Tenant c) =>
        Math.Round(c.BillingPeriod == "annual" ? MonthlyOf(db, c) * 12 * (1 - PricesOf(db).AnnualPct / 100m) : MonthlyOf(db, c), 2);

    /// <summary>Équivalent mensuel d'un client (un paiement annuel est remisé).</summary>
    public static decimal ClientMonthlyEquivalent(Db db, Tenant c) =>
        Math.Round(c.BillingPeriod == "annual" ? MonthlyOf(db, c) * (1 - PricesOf(db).AnnualPct / 100m) : MonthlyOf(db, c), 2);

    // ---------- Commission des revendeurs ----------

    public static bool IsReseller(Db db, Tenant c) => db.Resellers.FirstOrDefault(r => r.Id == c.ResellerId) is { IsDefault: false };

    public static int CommissionPct(Db db, Reseller r) => r.IsDefault ? 0 : Math.Clamp(r.CommissionPct ?? db.Platform.DefaultCommissionPct, 0, 100);

    /// <summary>Frais de paiement estimés : pourcentage + montant fixe (Réglages › Facturation).</summary>
    public static decimal FeeOf(Db db, decimal amount) =>
        amount <= 0 ? 0 : Math.Round(amount * db.Platform.StripeFeePct / 100m + db.Platform.StripeFeeFixed, 2);

    /// <summary>Commission sur le net : (montant − frais) × taux, arrondie au centime.</summary>
    public static decimal CommissionOf(decimal amount, decimal fee, int pct) => Math.Round(Math.Max(0, amount - fee) * pct / 100m, 2);

    // ---------- Mois (fuseau de Zurich) ----------

    private static readonly TimeZoneInfo Zurich = FindZone();
    private static TimeZoneInfo FindZone() { try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"); } catch (Exception) { return TimeZoneInfo.Local; } }
    public static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zurich);
    public static bool InMonth(DateTime utc, int year, int month) { var l = ToLocal(utc); return l.Year == year && l.Month == month; }
    /// <summary>Jour du versement : le dernier jour du mois.</summary>
    public static DateTime PayoutDate(int year, int month) => new(year, month, DateTime.DaysInMonth(year, month));

    public record Share(int Payments, decimal Gross, decimal Fees, decimal Net, decimal Commission)
    {
        public static readonly Share None = new(0, 0, 0, 0, 0);
    }

    /// <summary>Part d'un revendeur pour un mois : paiements de ses clients reçus ce mois-là, frais, net et commission figée sur chaque paiement.</summary>
    public static Share ShareOf(Db db, Reseller r, int year, int month)
    {
        var pays = db.Payments.Where(p => p.ResellerId == r.Id && InMonth(p.PaidUtc, year, month)).ToList();
        if (pays.Count == 0) return Share.None;
        return new(pays.Count, pays.Sum(p => p.Amount), pays.Sum(p => p.Fee), pays.Sum(p => p.Amount - p.Fee), pays.Sum(p => p.Commission));
    }

    public record Expected(Tenant Client, DateTime Due, decimal Amount, decimal Fee, decimal Commission);

    /// <summary>Renouvellements encore attendus d'ici la fin du mois en cours (clients actifs du revendeur) et la commission qu'ils rapporteraient : une prévision, pas un acquis.</summary>
    public static List<Expected> ExpectedOf(Db db, Reseller r, DateTime now)
    {
        var local = ToLocal(now);
        var end = new DateTime(local.Year, local.Month, DateTime.DaysInMonth(local.Year, local.Month));
        var pct = CommissionPct(db, r);
        return db.Clients.Where(c => c.ResellerId == r.Id && !c.Blocked && c.NextRenewalUtc is { } n && n.Date >= local.Date && n.Date <= end && Billable(db, c, now))
            .OrderBy(c => c.NextRenewalUtc).Select(c =>
            {
                var amount = ClientAmount(db, c);
                var fee = FeeOf(db, amount);
                return new Expected(c, c.NextRenewalUtc!.Value, amount, fee, CommissionOf(amount, fee, pct));
            }).ToList();
    }

    public static Payout? PayoutOf(Db db, Guid resellerId, int year, int month) => db.Payouts.FirstOrDefault(p => p.ResellerId == resellerId && p.Year == year && p.Month == month);

    public const string PayoutOpen = "En cours", PayoutDue = "À verser", PayoutDone = "Versé", PayoutEmpty = "Rien à verser";

    /// <summary>« En cours » jusqu'au dernier jour du mois, « À verser » le jour même et après, « Versé » une fois le virement saisi.</summary>
    public static string PayoutState(Db db, Reseller r, int year, int month, DateTime now)
    {
        if (PayoutOf(db, r.Id, year, month) != null) return PayoutDone;
        var share = ShareOf(db, r, year, month);
        if (share.Commission <= 0) return PayoutEmpty;
        return ToLocal(now).Date >= PayoutDate(year, month) ? PayoutDue : PayoutOpen;
    }

    public static Payout? MarkPaid(Db db, Reseller r, int year, int month, string reference, DateTime now)
    {
        if (PayoutOf(db, r.Id, year, month) != null) return null;
        var share = ShareOf(db, r, year, month);
        if (share.Commission <= 0) return null;
        var p = new Payout { ResellerId = r.Id, Year = year, Month = month, PaidUtc = now, Reference = (reference ?? "").Trim(), Amount = share.Commission };
        db.Payouts.Add(p);
        return p;
    }

    public static bool Unpay(Db db, Guid payoutId) => db.Payouts.RemoveAll(p => p.Id == payoutId) > 0;

    // ---------- Renouvellement ----------

    /// <summary>Date anniversaire suivante : on ajoute des mois puis on revient au jour d'origine (31 janvier → 28 février → 31 mars).</summary>
    public static DateTime NextAfter(DateTime from, int anchorDay, int months)
    {
        var d = from.AddMonths(months);
        var day = Math.Min(Math.Max(anchorDay, 1), DateTime.DaysInMonth(d.Year, d.Month));
        return new DateTime(d.Year, d.Month, day, 0, 0, 0, DateTimeKind.Utc);
    }

    public static int StepMonths(Tenant c) => c.BillingPeriod == "annual" ? 12 : 1;

    public const string FirstDue = "Premier paiement", Current = "À jour", Soon = "À renouveler", Unpaid = "Non payé", NotBilled = "Non facturé";

    /// <summary>État d'un abonnement : premier paiement attendu, à jour, à renouveler (dans les 7 jours), non payé (date dépassée).</summary>
    public static string StateOf(DateTime? next, bool billable, DateTime now)
    {
        if (next == null) return billable ? FirstDue : NotBilled;
        if (now.Date > next.Value.Date) return Unpaid;
        return next.Value.Date <= now.Date.AddDays(7) ? Soon : Current;
    }

    /// <summary>Le bouton « Paiement reçu » : premier paiement, ou renouvellement dû dans les 7 jours (ou déjà dépassé). Pas plus tôt : évite d'avancer la date par erreur.</summary>
    public static bool CanRecord(DateTime? next, DateTime now) => next == null || next.Value.Date <= now.Date.AddDays(7);

    /// <summary>Enregistre le paiement d'un renouvellement d'un client (direct ou d'un revendeur). null : rien à enregistrer (renouvellement pas encore proche).</summary>
    public static Payment? RecordClient(Db db, Tenant c, DateTime now)
    {
        if (!CanRecord(c.NextRenewalUtc, now)) return null;
        var first = c.NextRenewalUtc == null;
        var from = first ? now : c.NextRenewalUtc!.Value;
        if (first) { c.AnchorDay = now.Day; c.TrialEndsUtc = null; c.TrialReminderSentUtc = null; c.TrialEndedNotifiedUtc = null; }
        var amount = ClientAmount(db, c);
        var fee = FeeOf(db, amount);
        var reseller = db.Resellers.FirstOrDefault(r => r.Id == c.ResellerId);
        var pct = reseller == null ? 0 : CommissionPct(db, reseller);
        var next = NextAfter(from, c.AnchorDay, StepMonths(c));
        c.NextRenewalUtc = next;
        if (c.Blocked && c.BlockedReason == PaymentReason && next.Date >= now.Date) { c.Blocked = false; c.BlockedReason = ""; }
        var pay = new Payment { ClientId = c.Id, ResellerId = c.ResellerId, Name = c.Name, PaidUtc = now, FromUtc = from, ToUtc = next, Period = c.BillingPeriod, Screens = ScreensOf(db, c),
            Amount = amount, Fee = fee, CommissionPct = pct, Commission = CommissionOf(amount, fee, pct), First = first };
        db.Payments.Add(pay);
        return pay;
    }

    /// <summary>Annule le dernier paiement d'un client (erreur de saisie) : la date de renouvellement revient en arrière. Refusé si le versement du mois est déjà fait.</summary>
    public static bool UndoLast(Db db, Guid paymentId)
    {
        var pay = db.Payments.FirstOrDefault(p => p.Id == paymentId);
        if (pay == null) return false;
        var latest = db.Payments.Where(p => p.ClientId == pay.ClientId).OrderByDescending(p => p.PaidUtc).First();
        if (latest.Id != pay.Id) return false;
        var local = ToLocal(pay.PaidUtc);
        if (pay.ResellerId is { } rid && PayoutOf(db, rid, local.Year, local.Month) != null) return false;
        if (db.Clients.FirstOrDefault(c => c.Id == pay.ClientId) is { } c) c.NextRenewalUtc = pay.First ? null : pay.FromUtc;
        db.Payments.Remove(pay);
        return true;
    }

    // ---------- Blocage automatique ----------

    /// <summary>Bloque, à la date anniversaire dépassée, tout client dont le renouvellement n'a pas été payé. Retourne les clients à prévenir (écrans).</summary>
    public static List<Guid> Enforce(Db db, DateTime now)
    {
        var affected = new List<Guid>();
        foreach (var c in db.Clients.Where(c => c.NextRenewalUtc != null && now.Date > c.NextRenewalUtc.Value.Date && !c.Blocked))
        {
            c.Blocked = true; c.Suspended = false; c.BlockedReason = PaymentReason;
            affected.Add(c.Id);
        }
        return affected;
    }
}

/// <summary>Applique le blocage faute de paiement : au démarrage puis toutes les heures, et prévient les écrans concernés.</summary>
public class SubscriptionWorker(JsonStore store, Notifier notifier, ILogger<SubscriptionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ids = store.Write(db => Subscriptions.Enforce(db, DateTime.UtcNow));
                if (ids.Count > 0) log.LogInformation("{N} client(s) bloqué(s) faute de paiement à la date anniversaire", ids.Distinct().Count());
                foreach (var id in ids.Distinct()) await notifier.NotifyClient(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Blocage faute de paiement"); }
            await Task.Delay(TimeSpan.FromHours(1), ct);
        }
    }
}
