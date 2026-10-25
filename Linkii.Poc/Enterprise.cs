using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Linkii.Poc;

/// <summary>Demande Enterprise (formulaire « Parler à un expert ») : suivie dans la console jusqu'au contrat ou à l'abandon.</summary>
public class Opportunity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Revendeur dont le site a reçu la demande (marque des e-mails, équipe prévenue). Celui par défaut : Linkii.</summary>
    public Guid ResellerId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Org { get; set; } = "";
    public string Sites { get; set; } = "";
    public string Screens { get; set; } = "";
    /// <summary>Voir <see cref="Deployments"/>.</summary>
    public string Deployment { get; set; } = Deployments.Cloud;
    public List<string> Needs { get; set; } = new();
    public string Message { get; set; } = "";
    /// <summary>Voir <see cref="OpportunityStates"/>.</summary>
    public string Status { get; set; } = OpportunityStates.New;
    public string Notes { get; set; } = "";
    /// <summary>Espace créé pour le pilote (cloud Linkii), puis pour le client.</summary>
    public Guid? ClientId { get; set; }
    public DateTime? PilotEndsUtc { get; set; }
    public Guid? LicenseId { get; set; }
    /// <summary>E-mails déjà envoyés (rappels de fin de pilote).</summary>
    public List<string> Sent { get; set; } = new();
}

public static class OpportunityStates
{
    public const string New = "new", Pilot = "pilot", Quote = "quote", Won = "won", Lost = "lost";
    public static readonly string[] All = { New, Pilot, Quote, Won, Lost };
    public static string Label(string s) => s switch { Pilot => "Pilote", Quote => "Devis", Won => "Gagné", Lost => "Sans suite", _ => "Nouveau" };
    public static string Css(string s) => s switch { Pilot => "warn", Quote => "ac", Won => "ok", Lost => "mute", _ => "ac" };
}

public static class Deployments
{
    public const string Cloud = "cloud", Dedicated = "dedicated", OnPrem = "onprem";
    public static string Label(string d) => d switch { Dedicated => "Cloud dédié", OnPrem => "Sur site", _ => "Cloud Linkii" };
}

/// <summary>
/// Licence annuelle d'une installation sur site. Par défaut vérifiée en ligne chaque jour (<c>/api/license/check</c>) avec 14 jours de tolérance
/// si le serveur est injoignable ou la licence expirée ; pour un serveur sans internet, un fichier signé valable un an, importé à la main, sans tolérance.
/// La clé est signée (ECDSA P-256) : l'instance la vérifie avec la clé publique, sans secret.
/// </summary>
public class License
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? OpportunityId { get; set; }
    public Guid ResellerId { get; set; }
    public string Org { get; set; } = "";
    public string Email { get; set; } = "";
    public int Screens { get; set; }
    public string Modules { get; set; } = "Pro";
    public DateTime IssuedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
    /// <summary>online | offline</summary>
    public string Mode { get; set; } = LicenseModes.Online;
    public string Key { get; set; } = "";
    public bool Revoked { get; set; }
    public DateTime? LastCheckUtc { get; set; }
    public string LastCheckFrom { get; set; } = "";
    public List<string> Sent { get; set; } = new();
}

public static class LicenseModes
{
    public const string Online = "online", Offline = "offline";
}

public static class Enterprise
{
    public const int GraceDays = 14;
    public const int PilotDays = 30;
    public static readonly int[] LicenseWarnings = { 60, 30, 7 };
    public static readonly int[] PilotWarnings = { 7, 2 };
    public static readonly string[] NeedOptions = { "Connexion imposée par l'annuaire (Entra, Google)", "Annuaire (LDAP)", "Hébergement en Suisse", "Serveur sans accès internet", "Audit de sécurité", "Paiement sur facture" };
    public static readonly string[] SiteOptions = { "1 site", "2 à 5", "6 à 20", "Plus de 20" };
    public static readonly string[] ScreenOptions = { "Moins de 50", "50 à 200", "200 à 1000", "Plus de 1000" };

    // ---------- Licences signées ----------

    private record Payload(string Id, string Org, int Screens, string Modules, string Mode, DateTime Issued, DateTime Expires);

    /// <summary>Paire de clés de signature des licences : créée une fois, la clé privée chiffrée (SecretBox).</summary>
    private static ECDsa SigningKey(Db db, SecretBox box)
    {
        var p = db.Platform;
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (p.LicensePrivateKey != "") { ec.ImportECPrivateKey(Convert.FromBase64String(box.Unprotect(p.LicensePrivateKey)), out _); return ec; }
        p.LicensePrivateKey = box.Protect(Convert.ToBase64String(ec.ExportECPrivateKey()));
        p.LicensePublicKey = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
        return ec;
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] UnB64(string s) { s = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=')); }

    /// <summary>Émet (ou réémet) la licence : clé « LKI1.charge.signature ».</summary>
    public static License Issue(Db db, SecretBox box, License l)
    {
        using var ec = SigningKey(db, box);
        var json = JsonSerializer.SerializeToUtf8Bytes(new Payload(l.Id.ToString("N"), l.Org, l.Screens, l.Modules, l.Mode, l.IssuedUtc, l.ExpiresUtc));
        l.Key = "LKI1." + B64(json) + "." + B64(ec.SignData(json, HashAlgorithmName.SHA256));
        if (!db.Licenses.Contains(l)) db.Licenses.Add(l);
        return l;
    }

    public record Check(bool Valid, string Reason, DateTime? Expires, int Screens, string Modules, int GraceDays);

    /// <summary>Vérification en ligne : signature, révocation, échéance. Note la dernière vérification (le serveur sur site appelle chaque jour).</summary>
    public static Check Verify(Db db, string? key, string from, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(key) || db.Platform.LicensePublicKey == "") return new(false, "invalid", null, 0, "", GraceDays);
        var parts = key.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != "LKI1") return new(false, "invalid", null, 0, "", GraceDays);
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(db.Platform.LicensePublicKey), out _);
            var json = UnB64(parts[1]);
            if (!ec.VerifyData(json, UnB64(parts[2]), HashAlgorithmName.SHA256)) return new(false, "invalid", null, 0, "", GraceDays);
            var p = JsonSerializer.Deserialize<Payload>(json)!;
            var l = db.Licenses.FirstOrDefault(x => x.Id.ToString("N") == p.Id && x.Key == key.Trim());
            if (l == null) return new(false, "replaced", p.Expires, p.Screens, p.Modules, GraceDays);   // réémise depuis : l'ancienne clé ne vaut plus
            l.LastCheckUtc = now; l.LastCheckFrom = from;
            if (l.Revoked) return new(false, "revoked", l.ExpiresUtc, l.Screens, l.Modules, GraceDays);
            return new(l.ExpiresUtc > now, l.ExpiresUtc > now ? "ok" : "expired", l.ExpiresUtc, l.Screens, l.Modules, GraceDays);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException) { return new(false, "invalid", null, 0, "", GraceDays); }
    }

    /// <summary>Fichier hors ligne (.lic) : la clé et la clé publique pour la vérifier, sans appel au serveur.</summary>
    public static string OfflineFile(Db db, License l) =>
        $"# Licence Linkii · {l.Org}\n# {l.Screens} écrans · {l.Modules} · valable jusqu'au {l.ExpiresUtc:yyyy-MM-dd} · sans tolérance\n" +
        $"license={l.Key}\npublic-key={db.Platform.LicensePublicKey}\n";

    // ---------- Contenus des e-mails ----------

    public static Notice Ack(Opportunity o) => new("Nous avons bien reçu votre demande", $"Merci {o.Name.Split(' ')[0]}",
        new() { "Nous revenons vers vous sous un jour ouvré pour un échange de 20 minutes." },
        new() { ("Organisation", o.Org), ("Sites", o.Sites), ("Écrans", o.Screens), ("Déploiement", Deployments.Label(o.Deployment)) }, null, null);

    public static Notice Internal(Opportunity o) => new($"Nouvelle demande Enterprise : {o.Org}", "Nouvelle opportunité",
        new List<string> { $"{o.Name} ({o.Email}) · {o.Sites} · {o.Screens} écrans · {Deployments.Label(o.Deployment)}." }
            .Concat(o.Needs.Count > 0 ? new[] { "Exigences : " + string.Join(", ", o.Needs) + "." } : Array.Empty<string>())
            .Concat(o.Message.Trim().Length > 0 ? new[] { "« " + o.Message.Trim() + " »" } : Array.Empty<string>()).ToList(),
        new(), "Ouvrir les opportunités", "admin/opportunities");

    public static Notice PilotEnding(Opportunity o, int days, string end) => new($"Votre pilote se termine dans {days} jour{(days > 1 ? "s" : "")}", "Faisons le point",
        new() { $"Votre pilote Linkii se termine le {end}. Répondez à cet e-mail pour planifier un point et recevoir votre devis." }, new(), "Ouvrir mon espace", "");

    public static Notice LicenseEnding(License l, int days) => new($"Votre licence Linkii arrive à échéance dans {days} jours", "Pensez au renouvellement",
        new() { $"La licence de {l.Org} ({l.Screens} écrans) expire le {l.ExpiresUtc.ToString("d MMMM yyyy", Signup.Fr)}." ,
                l.Mode == LicenseModes.Online ? $"Sans renouvellement, vos écrans continueront {GraceDays} jours puis s'arrêteront." : "Le serveur étant sans accès internet, un nouveau fichier de licence devra être importé avant cette date." },
        new(), null, null, "Répondez à cet e-mail pour renouveler.");
}
