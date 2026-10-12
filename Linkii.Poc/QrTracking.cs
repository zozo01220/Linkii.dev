using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>
/// Comptage des scans de QR code (licence Growth). Le QR affiché sur l'écran n'encode pas la destination mais une adresse courte
/// <c>{domaine}/q/{code}</c> : le serveur compte le scan (<see cref="ScanLog"/>) puis redirige. La destination reste donc modifiable
/// sans changer le QR déjà affiché ni republier.
/// </summary>
public static class QrTracking
{
    public const double DefaultX = 78, DefaultY = 70, DefaultSize = 19, MinSize = 8, MaxSize = 45;
    public const int LabelMax = 60, UrlMax = 2000;

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";   // sans les caractères qui se ressemblent (0/O, 1/l/I)
    private static readonly Regex CodeShape = new("^[A-Za-z0-9]{6,16}$");

    /// <summary>Code de l'adresse courte : 8 caractères tirés au hasard (jamais devinable de proche en proche).</summary>
    public static string NewCode()
    {
        var sb = new StringBuilder(8);
        for (var i = 0; i < 8; i++) sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        return sb.ToString();
    }

    public static bool IsCode(string? s) => s != null && CodeShape.IsMatch(s);

    /// <summary>Destination saisie : adresse http(s) complète (« https:// » ajouté si absent). Null si elle n'est pas utilisable.</summary>
    public static string? Destination(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > UrlMax) return null;
        if (!s.Contains("://")) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return null;   // jamais javascript:, file:, data:…
        if (!u.Host.Contains('.') && !u.IsLoopback) return null;                               // un nom de domaine, pas un mot seul
        return u.AbsoluteUri.Length <= UrlMax ? u.AbsoluteUri : null;
    }

    /// <summary>« https://domaine/ » (sans chemin) à partir de l'adresse de l'application ; null si ce n'est pas une adresse http(s).</summary>
    public static string? Origin(string? baseUri) =>
        Uri.TryCreate(baseUri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.GetLeftPart(UriPartial.Authority) : null;

    public static string? ShortUrl(string? origin, string? code) => Origin(origin) is { } o && IsCode(code) ? o + "/q/" + code : null;

    public static string? CleanLabel(string? label) => label?.Trim() is { Length: > 0 } l ? (l.Length > LabelMax ? l[..LabelMax] : l) : null;

    public static double ClampSize(double v) => Math.Clamp(double.IsFinite(v) ? v : DefaultSize, MinSize, MaxSize);

    /// <summary>Position bornée pour que le QR reste entièrement dans la zone (sa hauteur dépasse sa largeur à cause du texte : marge prudente).</summary>
    public static (double X, double Y) ClampPos(double x, double y, double size)
    {
        if (!double.IsFinite(x)) x = DefaultX;
        if (!double.IsFinite(y)) y = DefaultY;
        return (Math.Clamp(x, 0, 100 - size), Math.Clamp(y, 0, 100 - size));
    }

    /// <summary>
    /// QR publié avec un élément de playlist : seulement pour un contenu pleine page, activé, d'une organisation Growth,
    /// avec un code, une destination et une adresse d'application valides ; sinon aucun QR.
    /// </summary>
    public static PublishedQr? Overlay(PlaylistItem i, bool growth, string placement)
    {
        if (!growth || !i.QrOn || placement != "full" || !IsCode(i.QrCode) || Destination(i.QrUrl) == null) return null;
        return Build(ShortUrl(i.QrBase, i.QrCode), i.QrX, i.QrY, i.QrSize, i.QrLabel);
    }

    /// <summary>QR (tracé, position, taille, texte) pour un texte donné : publication, vignettes et aperçus de la fenêtre d'un contenu.</summary>
    public static PublishedQr? Build(string? text, double x, double y, double size, string? label)
    {
        var data = Encoded(text);
        if (data == null) return null;
        size = ClampSize(size);
        (x, y) = ClampPos(x, y, size);
        return new PublishedQr { Data = data, X = x, Y = y, Size = size, Label = CleanLabel(label) };
    }

    // Les listes affichent un QR par ligne à chaque redessin : le tracé d'une adresse est calculé une fois (borné).
    private static readonly ConcurrentDictionary<string, string?> EncodeCache = new();

    private static string? Encoded(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (EncodeCache.TryGetValue(text, out var hit)) return hit;
        var data = QrCodes.Encode(text);
        if (EncodeCache.Count > 500) EncodeCache.Clear();
        EncodeCache[text] = data;
        return data;
    }

    /// <summary>Ouvertures qui ne sont pas un visiteur : prévisualisation d'une messagerie, antivirus, robots d'indexation, requêtes HEAD.</summary>
    public static bool LooksLikeBot(string? userAgent, string? method)
    {
        if (string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(userAgent)) return true;
        var ua = userAgent.ToLowerInvariant();
        return BotWords.Any(ua.Contains);
    }

    private static readonly string[] BotWords =
    {
        "bot", "crawler", "spider", "slurp", "preview", "facebookexternalhit", "whatsapp", "telegram", "skypeuripreview", "slack", "discord",
        "linkedin", "twitter", "curl/", "wget", "python-requests", "go-http-client", "okhttp/", "java/", "headlesschrome", "monitor", "uptime", "safelinks", "proofpoint", "mimecast", "barracuda"
    };
}

/// <summary>Un scan : date, contenu (élément de playlist) et empreinte anonyme du visiteur.</summary>
public record ScanEntry(DateTime AtUtc, Guid ItemId, string Visitor);

/// <summary>
/// Journal des scans : un fichier JSON Lines par client (scans/{client}.jsonl), conservé <see cref="RetentionDays"/> jours, comme l'historique de diffusion.
/// Aucune adresse IP n'est enregistrée : seulement une empreinte salée (sel secret du serveur) renouvelée chaque jour, qui sert à compter les visiteurs
/// distincts d'une journée sans pouvoir remonter à une personne ni suivre quelqu'un d'un jour à l'autre.
/// </summary>
public class ScanLog
{
    public const int RetentionDays = 90;

    private readonly string dir;
    private readonly byte[] salt;
    private readonly ConcurrentDictionary<Guid, object> locks = new();
    private readonly ConcurrentDictionary<Guid, DateTime> lastPrune = new();
    private readonly ConcurrentDictionary<(Guid, string), DateTime> recent = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ScanLog(IConfiguration config, IWebHostEnvironment env) : this(Path.Combine(config["Linkii:DataDir"] ?? env.ContentRootPath, "scans")) { }

    public ScanLog(string directory)
    {
        dir = directory;
        Directory.CreateDirectory(dir);
        var saltFile = Path.Combine(dir, "salt.bin");
        if (File.Exists(saltFile) && new FileInfo(saltFile).Length >= 32) salt = File.ReadAllBytes(saltFile);
        else { salt = RandomNumberGenerator.GetBytes(32); File.WriteAllBytes(saltFile, salt); }
    }

    private string FileOf(Guid clientId) => Path.Combine(dir, clientId.ToString("N") + ".jsonl");

    /// <summary>Empreinte du visiteur pour le jour donné : le même téléphone donne la même valeur toute la journée, une autre le lendemain.</summary>
    public string Visitor(string? ip, string? userAgent, DateTime nowUtc)
    {
        using var h = new HMACSHA256(salt);
        var bytes = h.ComputeHash(Encoding.UTF8.GetBytes(nowUtc.ToString("yyyyMMdd") + "|" + ip + "|" + userAgent));
        return Convert.ToHexString(bytes, 0, 8);
    }

    /// <summary>Enregistre un scan. Un même visiteur qui rouvre le même contenu dans les 10 secondes (double clic, rechargement) n'est compté qu'une fois.</summary>
    public bool Append(Guid clientId, ScanEntry e)
    {
        var key = (e.ItemId, e.Visitor);
        if (recent.TryGetValue(key, out var last) && e.AtUtc - last < TimeSpan.FromSeconds(10)) return false;
        recent[key] = e.AtUtc;
        if (recent.Count > 5000) foreach (var k in recent.Where(p => e.AtUtc - p.Value > TimeSpan.FromMinutes(1)).Select(p => p.Key).ToList()) recent.TryRemove(k, out _);
        lock (locks.GetOrAdd(clientId, _ => new object()))
        {
            File.AppendAllLines(FileOf(clientId), new[] { JsonSerializer.Serialize(e, Json) });
            if (DateTime.UtcNow - lastPrune.GetOrAdd(clientId, DateTime.MinValue) > TimeSpan.FromDays(1))
            {
                lastPrune[clientId] = DateTime.UtcNow;
                var keep = ReadUnlocked(clientId, DateTime.UtcNow.AddDays(-RetentionDays)).Select(x => JsonSerializer.Serialize(x, Json)).ToList();
                File.WriteAllLines(FileOf(clientId), keep);
            }
        }
        return true;
    }

    public List<ScanEntry> Read(Guid clientId, DateTime sinceUtc)
    {
        lock (locks.GetOrAdd(clientId, _ => new object())) return ReadUnlocked(clientId, sinceUtc);
    }

    private List<ScanEntry> ReadUnlocked(Guid clientId, DateTime sinceUtc)
    {
        var path = FileOf(clientId);
        var list = new List<ScanEntry>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<ScanEntry>(line, Json);
                if (e != null && e.AtUtc >= sinceUtc) list.Add(e);
            }
            catch (JsonException) { /* ligne tronquée : ignorée */ }
        }
        return list;
    }

    /// <summary>Scans et visiteurs distincts (par jour) de chaque contenu depuis la date donnée.</summary>
    public Dictionary<Guid, (int Scans, int Visitors)> Counts(Guid clientId, DateTime sinceUtc) =>
        Read(clientId, sinceUtc).GroupBy(e => e.ItemId)
            .ToDictionary(g => g.Key, g => (g.Count(), g.Select(e => e.AtUtc.ToString("yyyyMMdd") + e.Visitor).Distinct().Count()));
}
