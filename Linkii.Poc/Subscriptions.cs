using System.Globalization;

namespace Linkii.Poc;

/// <summary>Un renouvellement payé (mensuel ou annuel) d'un client direct ou d'un revendeur : journal des paiements.</summary>
public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>reseller | client</summary>
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
    /// <summary>Premier paiement : fixe la date anniversaire et met fin à l'essai.</summary>
    public bool First { get; set; }
}

/// <summary>
/// Abonnements : un client direct paie au mois ou à l'année, un revendeur au mois. La date anniversaire est celle du premier paiement et ne bouge plus.
/// Sans paiement à la date anniversaire, le compte est bloqué automatiquement : pas de relance ni de suivi. Le paiement suivant le débloque.
/// Prix par écran et par mois selon la licence (Réglages › Facturation). En attendant Stripe, un bouton enregistre à la main le paiement du renouvellement.
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

    /// <summary>Un client compte dans la facture quand il est actif : ni en essai, ni à valider, ni suspendu, ni bloqué.</summary>
    public static bool Billable(Db db, Tenant c, DateTime now) => ClientStates.Of(c, Signup.OwnerOf(db, c), now) == ClientStates.Active;

    /// <summary>Montant mensuel d'un client : écrans × prix de sa licence.</summary>
    public static decimal MonthlyOf(Db db, Tenant c) => ScreensOf(db, c) * Unit(PricesOf(db), c.Plan);

    /// <summary>Montant d'un renouvellement d'un client direct : au mois, ou 12 mois remisés pour un paiement annuel.</summary>
    public static decimal ClientAmount(Db db, Tenant c) =>
        Math.Round(c.BillingPeriod == "annual" ? MonthlyOf(db, c) * 12 * (1 - PricesOf(db).AnnualPct / 100m) : MonthlyOf(db, c), 2);

    /// <summary>Équivalent mensuel d'un client direct (un paiement annuel est remisé).</summary>
    public static decimal ClientMonthlyEquivalent(Db db, Tenant c) =>
        Math.Round(c.BillingPeriod == "annual" ? MonthlyOf(db, c) * (1 - PricesOf(db).AnnualPct / 100m) : MonthlyOf(db, c), 2);

    public record Line(Tenant Client, string Plan, int Screens, int Users, decimal Unit, decimal Amount);

    public record ResellerBill(Reseller R, List<Line> Lines, decimal Gross, decimal Discount, decimal Net)
    {
        public int Screens => Lines.Sum(l => l.Screens);
    }

    /// <summary>Renouvellement mensuel d'un revendeur : écrans de ses clients actifs au tarif de leur licence, moins son rabais.</summary>
    public static ResellerBill ResellerBillOf(Db db, Reseller r, DateTime now)
    {
        var p = PricesOf(db);
        var lines = db.Clients.Where(c => c.ResellerId == r.Id && Billable(db, c, now)).OrderBy(c => c.Name)
            .Select(c => new Line(c, c.Plan, ScreensOf(db, c), UsersOf(db, c), Unit(p, c.Plan), Math.Round(ScreensOf(db, c) * Unit(p, c.Plan), 2))).ToList();
        var gross = lines.Sum(l => l.Amount);
        var disc = Math.Round(gross * r.DiscountPct / 100m, 2);
        return new(r, lines, gross, disc, gross - disc);
    }

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

    /// <summary>Enregistre le paiement d'un renouvellement d'un client direct. null : rien à enregistrer (renouvellement pas encore proche).</summary>
    public static Payment? RecordClient(Db db, Tenant c, DateTime now)
    {
        if (!CanRecord(c.NextRenewalUtc, now)) return null;
        var first = c.NextRenewalUtc == null;
        var from = first ? now : c.NextRenewalUtc!.Value;
        if (first) { c.AnchorDay = now.Day; c.TrialEndsUtc = null; c.TrialReminderSentUtc = null; c.TrialEndedNotifiedUtc = null; }
        var amount = ClientAmount(db, c);
        var next = NextAfter(from, c.AnchorDay, StepMonths(c));
        c.NextRenewalUtc = next;
        if (c.Blocked && c.BlockedReason == PaymentReason && next.Date >= now.Date) { c.Blocked = false; c.BlockedReason = ""; }
        var pay = new Payment { Kind = "client", ClientId = c.Id, ResellerId = c.ResellerId, Name = c.Name, PaidUtc = now, FromUtc = from, ToUtc = next, Period = c.BillingPeriod, Screens = ScreensOf(db, c), Amount = amount, First = first };
        db.Payments.Add(pay);
        return pay;
    }

    /// <summary>Enregistre le paiement du renouvellement mensuel d'un revendeur.</summary>
    public static Payment? RecordReseller(Db db, Reseller r, DateTime now)
    {
        if (r.IsDefault || !CanRecord(r.NextRenewalUtc, now)) return null;
        var first = r.NextRenewalUtc == null;
        var from = first ? now : r.NextRenewalUtc!.Value;
        if (first) r.AnchorDay = now.Day;
        var bill = ResellerBillOf(db, r, now);
        var next = NextAfter(from, r.AnchorDay, 1);
        r.NextRenewalUtc = next;
        if (!r.Active && r.BlockedReason == PaymentReason && next.Date >= now.Date) { r.Active = true; r.BlockedReason = ""; }
        var pay = new Payment { Kind = "reseller", ResellerId = r.Id, Name = r.Name, PaidUtc = now, FromUtc = from, ToUtc = next, Period = "monthly", Screens = bill.Screens, Amount = bill.Net, First = first };
        db.Payments.Add(pay);
        return pay;
    }

    /// <summary>Annule le dernier paiement d'un client ou d'un revendeur (erreur de saisie) : la date de renouvellement revient en arrière.</summary>
    public static bool UndoLast(Db db, Guid paymentId)
    {
        var pay = db.Payments.FirstOrDefault(p => p.Id == paymentId);
        if (pay == null) return false;
        var latest = db.Payments.Where(p => p.Kind == pay.Kind && p.ClientId == pay.ClientId && p.ResellerId == pay.ResellerId).OrderByDescending(p => p.PaidUtc).First();
        if (latest.Id != pay.Id) return false;
        if (pay.Kind == "reseller" && db.Resellers.FirstOrDefault(r => r.Id == pay.ResellerId) is { } r) r.NextRenewalUtc = pay.First ? null : pay.FromUtc;
        else if (pay.Kind == "client" && db.Clients.FirstOrDefault(c => c.Id == pay.ClientId) is { } c) c.NextRenewalUtc = pay.First ? null : pay.FromUtc;
        db.Payments.Remove(pay);
        return true;
    }

    // ---------- Blocage automatique ----------

    /// <summary>Bloque, à la date anniversaire dépassée, tout client direct ou revendeur dont le renouvellement n'a pas été payé. Retourne les clients à prévenir (écrans).</summary>
    public static List<Guid> Enforce(Db db, DateTime now)
    {
        var affected = new List<Guid>();
        foreach (var c in db.Clients.Where(c => c.NextRenewalUtc != null && now.Date > c.NextRenewalUtc.Value.Date && !c.Blocked))
        {
            if (db.Resellers.FirstOrDefault(r => r.Id == c.ResellerId) is not { IsDefault: true }) continue;   // un client de revendeur est facturé avec le revendeur
            c.Blocked = true; c.Suspended = false; c.BlockedReason = PaymentReason;
            affected.Add(c.Id);
        }
        foreach (var r in db.Resellers.Where(r => !r.IsDefault && r.NextRenewalUtc != null && now.Date > r.NextRenewalUtc.Value.Date && r.Active))
        {
            r.Active = false; r.Suspended = false; r.BlockedReason = PaymentReason;
            affected.AddRange(db.Clients.Where(c => c.ResellerId == r.Id).Select(c => c.Id));
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
