namespace Linkii.Poc;

/// <summary>Contenu d'un e-mail du parcours : objet, titre, paragraphes, valeurs à mettre en avant, bouton (chemin dans l'espace, ex. « settings?tab=abonnement »).</summary>
public record Notice(string Subject, string Title, List<string> Paras, List<(string Label, string Value)> Rows, string? Button, string? Path, string? Footnote = null);

/// <summary>
/// Parcours e-mails d'une organisation en libre-service, de l'inscription à la fin (plan acheté ou suppression).
/// Déclenchés par une action : partent tout de suite (plan actif, écran ajouté, passage à Essential, résiliation, paiement refusé).
/// Programmés : partent à partir de 9 h, heure de l'organisation (premiers pas, astuces, rappel de fin d'essai, avertissements de suppression).
/// Facultatifs (case « nouveautés et astuces » de l'inscription, et réglage de la console) : premiers pas et astuces. Tout le reste est obligatoire.
/// </summary>
public static class Lifecycle
{
    public const string AbonnementPath = "settings?tab=abonnement";
    public const int SendHour = 9;
    /// <summary>Avertissements avant la suppression des contenus (jours restants).</summary>
    public static readonly int[] DeleteWarnings = { 15, 7, 1 };

    public static string Chf(decimal x) => Subscriptions.Fmt(x) + " CHF";
    private static string Lic(string plan) => Plans.Label(plan);

    /// <summary>Jours écoulés depuis le début de l'essai.</summary>
    public static double TrialDay(Db db, Tenant c, DateTime now) =>
        c.TrialEndsUtc is { } end ? (now - end.AddDays(-Signup.TrialDaysOf(db, c))).TotalDays : 0;

    /// <summary>Jours de l'essai où part une astuce : J+3 pour un essai court, J+10 et J+20 pour un essai d'un mois.</summary>
    public static int[] TipDays(int trialDays) => trialDays <= 10 ? new[] { 3 } : new[] { 10, 20 };

    public static int LocalHour(Tenant c, DateTime now)
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById(c.Timezone)).Hour; }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return now.Hour; }
    }

    // ---------- Contenus ----------

    /// <summary>Ligne de l'e-mail de bienvenue : la règle de suppression, annoncée dès l'inscription (espaces en libre-service).</summary>
    public static string? WelcomeDeleteNote(Db db, Tenant c)
    {
        if (!SignupSources.IsSelfService(c.SignupSource)) return null;
        var days = Math.Max(1, db.Platform.RetentionDays);
        return c.TrialEndsUtc is { } end
            ? $"Sans plan choisi à la fin de l'essai, vos contenus sont conservés {days} jours puis supprimés définitivement, le {Signup.Day(c, Billing.DeleteDateAfter(db, end))}. Nous vous préviendrons avant."
            : $"Sans plan choisi à la fin de l'essai, vos contenus sont conservés {days} jours puis supprimés définitivement. Nous vous préviendrons avant.";
    }

    public static Notice FirstSteps(Tenant c) => new("Connectez votre premier écran en 3 minutes", "Votre premier écran",
        new() { "Branchez un écran ou un mini-PC, ouvrez l'adresse du lecteur Linkii dessus et saisissez dans votre espace le code à six chiffres qu'il affiche.",
                "Ensuite, choisissez la liste de lecture à diffuser : c'est tout." }, new(), "Connecter un écran", "", "Vous recevez ces conseils car vous avez accepté les nouveautés et astuces à l'inscription.");

    public static Notice Tip(Tenant c, bool hasPlaylist, int index) => !hasPlaylist
        ? new("Composez votre première liste de lecture", "Une liste, et vos écrans s'animent",
            new() { "Importez quelques images ou vidéos dans la médiathèque, rassemblez-les dans une liste de lecture, puis publiez-la sur vos écrans.",
                    "Vous pouvez aussi partir d'un modèle de composition prêt à l'emploi." }, new(), "Créer une liste", "playlists", "Vous recevez ces conseils car vous avez accepté les nouveautés et astuces à l'inscription.")
        : index == 0
            ? new("Ce que vous pouvez faire avec Pro pendant l'essai", "Trois idées pour profiter de l'essai",
                new() { "Assemblez plusieurs écrans en un mur d'écrans, programmez vos diffusions par créneau, ou ajoutez un QR code dont les scans sont comptés.",
                        "Toutes ces fonctions sont incluses pendant l'essai." }, new(), "Ouvrir mon espace", "", "Vous recevez ces conseils car vous avez accepté les nouveautés et astuces à l'inscription.")
            : new("Surveillez vos écrans sans vous déplacer", "Alertes et aperçu en direct",
                new() { "Recevez un e-mail ou une notification quand un écran tombe, et voyez en direct ce qu'il affiche.",
                        "Réglez-les dans Réglages › Alertes et pause." }, new(), "Régler les alertes", "settings?tab=supervision", "Vous recevez ces conseils car vous avez accepté les nouveautés et astuces à l'inscription.");

    public static Notice TrialReminder(Db db, Tenant c, int daysLeft)
    {
        var screens = Subscriptions.ScreensOf(db, c);
        var when = daysLeft <= 1 ? "demain" : $"dans {daysLeft} jours";
        var rows = screens > 0
            ? new List<(string, string)> { ($"Pro × {screens} écran{(screens > 1 ? "s" : "")}", Chf(Billing.PeriodAmount(db, Plans.Growth, "monthly", screens)) + " / mois"),
                                           ($"Base × {screens} écran{(screens > 1 ? "s" : "")}", Chf(Billing.PeriodAmount(db, Plans.Base, "monthly", screens)) + " / mois") }
            : new List<(string, string)>();
        return new($"Votre essai se termine {when}", $"Votre essai se termine le {Signup.Day(c, c.TrialEndsUtc!.Value)}",
            new() { "Pour que vos écrans continuent de diffuser, choisissez un plan. Rien n'est prélevé avant la fin de l'essai." + (screens > 0 ? " Estimation avec vos écrans :" : "") },
            rows, "Choisir mon plan", AbonnementPath);
    }

    public static Notice TrialEnded(Db db, Tenant c) => new("Votre essai est terminé : vos écrans ont cessé de diffuser", "Votre essai est terminé",
        new() { "Vos écrans ne diffusent plus, mais vos contenus et vos réglages sont conservés. Choisissez un plan pour tout reprendre là où vous en étiez." },
        new() { ("Contenus conservés jusqu'au", Signup.Day(c, c.DeleteOnUtc!.Value)) }, "Choisir mon plan", AbonnementPath);

    public static Notice DeleteWarning(Tenant c, int days) => new(
        days <= 1 ? "Suppression de vos contenus demain" : days <= 7 ? "Dernière semaine avant la suppression de vos contenus" : $"Vos contenus seront supprimés dans {days} jours",
        days <= 1 ? "Dernier jour" : $"Suppression dans {days} jours",
        new() { $"Sans plan choisi, l'espace « {c.Name} », ses contenus, ses listes et ses réglages seront définitivement supprimés le {Signup.Day(c, c.DeleteOnUtc!.Value)}. Cette action est irréversible." },
        new(), "Garder mon espace", AbonnementPath);

    public static Notice Deleted(string org) => new("Vos contenus ont été supprimés : bonne continuation", "Bonne continuation",
        new() { $"Comme annoncé, votre espace « {org} » et ses contenus ont été supprimés.", "Merci d'avoir essayé Linkii. Vous pouvez revenir quand vous voulez avec un nouvel essai." },
        new(), null, null);

    public static Notice PlanActive(Db db, Tenant c, DateTime now)
    {
        var screens = Subscriptions.ScreensOf(db, c);
        var rows = new List<(string, string)> { ("Plan", $"{Lic(c.Plan)} × {screens} écran{(screens > 1 ? "s" : "")}"), ("Facturation", c.BillingPeriod == "annual" ? "Annuelle" : "Mensuelle") };
        if (c.NextRenewalUtc is { } n) rows.Add((c.InTrial(now) ? "Premier prélèvement" : "Prochaine échéance", Signup.Day(c, n)));
        return new($"Votre plan {Lic(c.Plan)} est actif : bonne continuation", "Merci, votre plan est actif",
            new() { c.InTrial(now) ? "Vous gardez votre essai jusqu'au bout : le premier prélèvement aura lieu à sa fin, et vos écrans continueront sans interruption." : "Vos écrans continuent sans interruption." },
            rows, "Voir mon abonnement", AbonnementPath, "Le reçu de chaque paiement est envoyé par notre prestataire de paiement.");
    }

    public static Notice PaymentFailed(Db db, Tenant c) => new("Paiement refusé : votre compte est bloqué", "Votre paiement n'a pas abouti",
        new() { "Le renouvellement de votre abonnement a été refusé. Vos écrans ont cessé de diffuser.", "Mettez à jour votre moyen de paiement pour tout rétablir immédiatement." },
        new() { ("Montant", Chf(Subscriptions.ClientAmount(db, c))) }, "Mettre à jour ma carte", AbonnementPath);

    public static Notice ScreenAdded(Db db, Tenant c, string screen, string by, decimal prorata, DateTime now) => new("Un écran a été ajouté à votre abonnement", "Un écran de plus",
        new() { $"« {screen} » a été connecté{(by.Length > 0 ? " par " + by : "")}. Il est facturé au prorata, puis {Chf(Billing.Unit(db, c.Plan))} par mois." },
        new() { ("Prorata payé", Chf(prorata)), ("Écrans facturés", Subscriptions.ScreensOf(db, c).ToString()), ("Prochaine facture", Chf(Subscriptions.ClientAmount(db, c))) },
        "Voir mon abonnement", AbonnementPath);

    public static Notice BaseScheduled(Db db, Tenant c)
    {
        var use = Billing.UsageOf(db, c);
        var lost = new List<string>();
        if (use.Walls > 0) lost.Add($"{use.Walls} mur{(use.Walls > 1 ? "s" : "")} d'écrans");
        if (use.Alerts > 0) lost.Add("les alertes");
        if (use.Qr > 0) lost.Add("le comptage des scans");
        lost.Add("le contrôle du direct");
        return new($"Votre passage à Essential est programmé pour le {Signup.Day(c, c.NextRenewalUtc!.Value)}", "Changement programmé",
            new() { $"Vous gardez Pro jusqu'au {Signup.Day(c, c.NextRenewalUtc!.Value)}. Ensuite, {string.Join(", ", lost)} seront verrouillés : rien n'est supprimé, tout revient si vous repassez à Pro." },
            new(), "Annuler le changement", AbonnementPath);
    }

    public static Notice Cancelled(Db db, Tenant c) => new("Votre résiliation est enregistrée", "Résiliation enregistrée",
        new() { $"Votre abonnement reste actif jusqu'au {Signup.Day(c, c.CancelAtUtc!.Value)}. Ensuite, vos écrans cesseront de diffuser et vos contenus seront supprimés {db.Platform.RetentionDays} jours plus tard. Nous vous préviendrons avant." },
        new() { ("Fin de l'abonnement", Signup.Day(c, c.CancelAtUtc!.Value)), ("Suppression des contenus", Signup.Day(c, Billing.DeleteDateAfter(db, c.CancelAtUtc!.Value))) },
        "Annuler la résiliation", AbonnementPath);
}

/// <summary>Envoie les e-mails du parcours : destinataires (propriétaire ou tous les administrateurs), marque du revendeur, adresse de l'espace.</summary>
public class LifecycleMail(JsonStore store, Mailer mail, ResellerResolver resolver, ILogger<LifecycleMail> log)
{
    /// <summary>allAdmins : tous les administrateurs (avertissements de suppression, paiement, écran ajouté…), sinon le propriétaire seul.</summary>
    public async Task Send(Guid clientId, Notice n, bool allAdmins = false)
    {
        var (r, people) = store.Read(db =>
        {
            var c = db.Clients.FirstOrDefault(x => x.Id == clientId);
            if (c == null) return (null, new List<(string, string)>());
            var owner = Signup.OwnerOf(db, c);
            var list = allAdmins
                ? db.Users.Where(u => u.ClientId == c.Id && u.Role == Roles.ClientAdmin && !u.Disabled).Select(u => (u.Email, u.Name)).ToList()
                : owner == null ? new() : new List<(string, string)> { (owner.Email, owner.Name) };
            return (db.Resellers.FirstOrDefault(x => x.Id == c.ResellerId), list);
        });
        if (r == null) return;
        await SendTo(r, people, n);
    }

    public async Task SendTo(Reseller r, IEnumerable<(string Email, string Name)> people, Notice n)
    {
        var app = resolver.LoginUrl(r)[..^"login".Length];
        foreach (var (email, name) in people)
        {
            var err = await mail.SendNotice(r, email, name, n, n.Path == null ? null : app + n.Path);
            if (err != null) log.LogWarning("E-mail « {Subject} » non envoyé à {Email} : {Error}", n.Subject, email, err);
        }
    }
}
