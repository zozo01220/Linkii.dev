using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Linkii.Poc;

/// <summary>
/// Clé d'API d'une organisation (§154). Seule l'empreinte (SHA-256) est conservée : la clé n'est montrée qu'une fois, à sa création.
/// Elle agit « au nom » de la personne qui l'a créée (<see cref="OwnerId"/>) et ne voit jamais que son organisation.
/// </summary>
public class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Empreinte SHA-256 (hexadécimale) de la clé complète.</summary>
    public string Hash { get; set; } = "";
    /// <summary>Début et fin de la clé (« lkv_Qm4tAb12…c7Zp ») : de quoi la reconnaître, pas de quoi l'utiliser.</summary>
    public string Prefix { get; set; } = "";
    /// <summary>Gabarit choisi à la création (« Lecture seule »…) ou « Personnalisée ». Informatif : les accès réels sont <see cref="Scopes"/>.</summary>
    public string Template { get; set; } = "";
    public List<string> Scopes { get; set; } = new();
    /// <summary>Adresses IP ou plages (CIDR) autorisées. Vide : la clé fonctionne depuis n'importe où.</summary>
    public List<string> AllowedIps { get; set; } = new();
    public Guid? OwnerId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresUtc { get; set; }
    public DateTime? RevokedUtc { get; set; }
    /// <summary>Remplacée par une nouvelle clé : celle-ci reste valable 24 h (<see cref="ExpiresUtc"/>) pour changer sans coupure.</summary>
    public DateTime? RenewedUtc { get; set; }
    /// <summary>Enregistrée toutes les quelques minutes seulement (data.json est réécrit en entier à chaque écriture).</summary>
    public DateTime? LastUsedUtc { get; set; }
    /// <summary>Rappel « expire dans 14 jours » déjà envoyé.</summary>
    public DateTime? ExpiryNoticeUtc { get; set; }

    public const string Active = "active", Unused = "unused", Expiring = "expiring", Expired = "expired", Revoked = "revoked";
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(14);

    public string State(DateTime now) =>
        RevokedUtc != null ? Revoked
        : ExpiresUtc is { } e && e <= now ? Expired
        : RenewedUtc != null || ExpiresUtc is { } x && x - now <= ExpiryWarning ? Expiring
        : LastUsedUtc == null ? Unused
        : Active;

    /// <summary>Compte dans la limite de clés : ni révoquée, ni expirée, ni déjà remplacée.</summary>
    public bool Counts(DateTime now) => RevokedUtc == null && RenewedUtc == null && (ExpiresUtc == null || ExpiresUtc > now);
}

/// <summary>Format des clés : <c>lkv_</c> + 43 caractères aléatoires + 6 de somme de contrôle (écarte une faute de frappe, et un scanner de secrets la reconnaît).</summary>
public static class ApiKeyFormat
{
    public const string Marker = "lkv_";
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int BodyLength = 43, CheckLength = 6;
    public const int Length = 4 + BodyLength + CheckLength;

    public static string Generate()
    {
        var sb = new StringBuilder(Marker);
        for (var i = 0; i < BodyLength; i++) sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
        sb.Append(Check(sb.ToString(4, BodyLength)));
        return sb.ToString();
    }

    public static bool IsWellFormed(string? key)
    {
        if (key is not { Length: Length } || !key.StartsWith(Marker, StringComparison.Ordinal)) return false;
        if (key.Skip(4).Any(c => !Alphabet.Contains(c))) return false;
        return Check(key.Substring(4, BodyLength)) == key[(4 + BodyLength)..];
    }

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    public static string Display(string key) => key[..12] + "…" + key[^4..];

    private static string Check(string body)
    {
        var crc = Crc32(Encoding.ASCII.GetBytes(body));
        var chars = new char[CheckLength];
        for (var i = CheckLength - 1; i >= 0; i--) { chars[i] = Alphabet[(int)(crc % 62)]; crc /= 62; }
        return new string(chars);
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

/// <summary>Adresse IP ou plage CIDR autorisée pour une clé.</summary>
public static class IpRule
{
    public static bool TryParse(string text, out IPAddress ip, out int prefix)
    {
        ip = IPAddress.None; prefix = -1;
        var parts = text.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var a)) return false;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        var max = a.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var p = max;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out p) || p < 0 || p > max)) return false;
        ip = a; prefix = p;
        return true;
    }

    public static bool Allows(IEnumerable<string> rules, IPAddress? remote)
    {
        var list = rules.ToList();
        if (list.Count == 0) return true;
        if (remote == null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        foreach (var r in list)
        {
            if (!TryParse(r, out var net, out var prefix) || net.AddressFamily != remote.AddressFamily) continue;
            var a = net.GetAddressBytes(); var b = remote.GetAddressBytes();
            var full = prefix / 8; var rest = prefix % 8;
            if (!a.Take(full).SequenceEqual(b.Take(full))) continue;
            if (rest == 0 || ((a[full] ^ b[full]) & (0xFF << (8 - rest))) == 0) return true;
        }
        return false;
    }
}

public record ApiIdentity(Guid ClientId, Guid KeyId, Guid? OwnerId, string KeyName, IReadOnlyList<string> Scopes);
public record ApiAuthResult(ApiIdentity? Identity, string? Code)
{
    public bool Ok => Identity != null;
}
public record ApiKeyCreated(ApiKey? Key, string? Secret, string? Error);

/// <summary>Création, révocation, renouvellement et authentification des clés. Les écrans d'administration et l'API passent par ici.</summary>
public class ApiKeyService(JsonStore store, ApiRegistry registry, TimeProvider time, ILogger<ApiKeyService> log)
{
    private readonly ConcurrentDictionary<string, (Guid Client, Guid Key)> index = new();
    private readonly ConcurrentDictionary<Guid, DateTime> touched = new();
    private volatile bool built;
    private readonly object buildLock = new();

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Index empreinte → clé, reconstruit au premier usage : l'authentification ne parcourt pas toutes les organisations.</summary>
    private void EnsureIndex()
    {
        if (built) return;
        lock (buildLock)
        {
            if (built) return;
            index.Clear();
            store.Read(db => { foreach (var c in db.Clients) foreach (var k in c.ApiKeys) index[k.Hash] = (c.Id, k.Id); return 0; });
            built = true;
        }
    }

    /// <summary>Clé reconnue d'après son format et son empreinte (sans toucher aux états) : sert au limiteur de débit.</summary>
    public (Guid Client, Guid Key)? Peek(string? authorization)
    {
        var token = Bearer(authorization);
        if (!ApiKeyFormat.IsWellFormed(token)) return null;
        EnsureIndex();
        return index.TryGetValue(ApiKeyFormat.Hash(token!), out var v) ? v : null;
    }

    public static string? Bearer(string? header) =>
        header != null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;

    public ApiAuthResult Authenticate(string? authorization, IPAddress? remote, Reseller? host)
    {
        if (string.IsNullOrWhiteSpace(authorization)) return new(null, ApiCodes.MissingKey);
        var token = Bearer(authorization);
        if (!ApiKeyFormat.IsWellFormed(token)) return new(null, ApiCodes.InvalidKey);
        EnsureIndex();
        if (!index.TryGetValue(ApiKeyFormat.Hash(token!), out var ids)) return new(null, ApiCodes.InvalidKey);
        var now = Now;
        return store.Read(db =>
        {
            var c = db.Clients.FirstOrDefault(x => x.Id == ids.Client);
            var k = c?.ApiKeys.FirstOrDefault(x => x.Id == ids.Key);
            if (c == null || k == null) return new ApiAuthResult(null, ApiCodes.InvalidKey);
            if (host != null && host.Id != c.ResellerId) return new ApiAuthResult(null, ApiCodes.InvalidKey);   // une clé n'existe que sur l'adresse de son revendeur
            if (k.RevokedUtc != null) return new ApiAuthResult(null, ApiCodes.KeyRevoked);
            if (k.ExpiresUtc is { } e && e <= now) return new ApiAuthResult(null, ApiCodes.KeyExpired);
            if (!IpRule.Allows(k.AllowedIps, remote)) return new ApiAuthResult(null, ApiCodes.IpNotAllowed);
            touched[k.Id] = now;
            return new ApiAuthResult(new ApiIdentity(c.Id, k.Id, k.OwnerId, k.Name, k.Scopes.ToList()), null);
        });
    }

    public ApiKeyCreated Create(Guid clientId, Guid? ownerId, string? name, string? template, IEnumerable<string> scopes, DateTime? expiresUtc, IEnumerable<string>? ips)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return Fail("Donnez un nom à la clé.");
        if (name.Length > 80) return Fail("Le nom est trop long (80 caractères au plus).");
        var list = scopes.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
        if (list.Count == 0) return Fail("Choisissez au moins un accès.");
        if (list.FirstOrDefault(s => !registry.IsKnownScope(s)) is { } bad) return Fail($"Accès inconnu : {bad}.");
        var rules = (ips ?? Array.Empty<string>()).Select(i => i.Trim()).Where(i => i.Length > 0).Distinct().ToList();
        if (rules.Count > 20) return Fail("20 adresses IP au plus.");
        if (rules.FirstOrDefault(r => !IpRule.TryParse(r, out _, out _)) is { } badIp) return Fail($"Adresse IP invalide : {badIp}.");
        var now = Now;
        if (expiresUtc is { } ex && (ex <= now || ex > now.AddYears(10))) return Fail("L'expiration doit être dans le futur (10 ans au plus).");

        var secret = ApiKeyFormat.Generate();
        var key = new ApiKey
        {
            Name = name, Hash = ApiKeyFormat.Hash(secret), Prefix = ApiKeyFormat.Display(secret), Template = string.IsNullOrWhiteSpace(template) ? "Personnalisée" : template.Trim(),
            Scopes = list, AllowedIps = rules, OwnerId = ownerId, CreatedUtc = now, ExpiresUtc = expiresUtc
        };
        var error = store.Write(db =>
        {
            var c = db.Clients.FirstOrDefault(x => x.Id == clientId);
            if (c == null) return "Organisation introuvable.";
            if (!ApiLimits.Allowed(c)) return "L'API fait partie du plan Pro.";
            foreach (var sc in list)
                if (registry.Scope(sc)?.App is { } app && !db.AppInstalls.Any(a => a.ClientId == clientId && a.AppId == app))
                    return $"L'accès « {sc} » demande l'intégration « {app} » : installez-la depuis le Store.";
            if (c.ApiKeys.Count(k => k.Counts(now)) >= ApiLimits.Keys(c)) return $"Limite atteinte : {ApiLimits.Keys(c)} clés au plus. Révoquez une clé inutilisée.";
            c.ApiKeys.Add(key);
            return null;
        });
        if (error != null) return Fail(error);
        EnsureIndex();
        index[key.Hash] = (clientId, key.Id);
        return new(key, secret, null);
    }

    private static ApiKeyCreated Fail(string error) => new(null, null, error);

    public bool Revoke(Guid clientId, Guid keyId)
    {
        var now = Now;
        return store.Write(db =>
        {
            var k = db.Clients.FirstOrDefault(c => c.Id == clientId)?.ApiKeys.FirstOrDefault(x => x.Id == keyId);
            if (k == null || k.RevokedUtc != null) return false;
            k.RevokedUtc = now;
            return true;
        });
    }

    /// <summary>Crée une clé identique (nom, accès, adresses) ; l'ancienne reste valable 24 h pour laisser le temps de changer la clé dans l'outil.</summary>
    public ApiKeyCreated Renew(Guid clientId, Guid keyId)
    {
        var old = store.Read(db => db.Clients.FirstOrDefault(c => c.Id == clientId)?.ApiKeys.FirstOrDefault(x => x.Id == keyId));
        if (old == null || old.RevokedUtc != null) return Fail("Clé introuvable ou déjà révoquée.");
        if (old.RenewedUtc != null) return Fail("Cette clé a déjà été renouvelée.");
        var now = Now;
        var span = old.ExpiresUtc is { } e && e > old.CreatedUtc ? e - old.CreatedUtc : (TimeSpan?)null;
        var created = Create(clientId, old.OwnerId, old.Name, old.Template, old.Scopes, span == null ? null : now + span, old.AllowedIps);
        if (created.Key == null) return created;
        store.Write(db =>
        {
            var k = db.Clients.First(c => c.Id == clientId).ApiKeys.First(x => x.Id == keyId);
            k.RenewedUtc = now;
            var grace = now.AddHours(24);
            if (k.ExpiresUtc == null || k.ExpiresUtc > grace) k.ExpiresUtc = grace;
        });
        return created;
    }

    public List<ApiKey> List(Guid clientId) => store.Read(db => db.Clients.FirstOrDefault(c => c.Id == clientId)?.ApiKeys.OrderByDescending(k => k.CreatedUtc).ToList() ?? new());

    /// <summary>Dernière utilisation en mémoire (plus récente que celle de data.json).</summary>
    public DateTime? LastUsed(ApiKey k) => touched.TryGetValue(k.Id, out var t) && (k.LastUsedUtc == null || t > k.LastUsedUtc) ? t : k.LastUsedUtc;

    /// <summary>Écrit les dernières utilisations dans data.json : une seule écriture, appelée toutes les quelques minutes (jamais à chaque requête).</summary>
    public int FlushUsage()
    {
        var snapshot = touched.ToArray();
        if (snapshot.Length == 0) return 0;
        foreach (var kv in snapshot) touched.TryRemove(kv);
        store.Write(db =>
        {
            var all = db.Clients.SelectMany(c => c.ApiKeys).ToDictionary(k => k.Id);
            foreach (var (id, at) in snapshot)
                if (all.TryGetValue(id, out var k) && (k.LastUsedUtc == null || at > k.LastUsedUtc)) k.LastUsedUtc = at;
        });
        return snapshot.Length;
    }

    /// <summary>Clés qui expirent dans moins de 14 jours et dont personne n'a encore été prévenu (une seule fois par clé).</summary>
    public List<(Guid Client, ApiKey Key)> TakeExpiryNotices()
    {
        var now = Now;
        return store.Write(db =>
        {
            var due = new List<(Guid, ApiKey)>();
            foreach (var c in db.Clients)
                foreach (var k in c.ApiKeys.Where(k => k.RevokedUtc == null && k.RenewedUtc == null && k.ExpiryNoticeUtc == null && k.ExpiresUtc is { } e && e > now && e - now <= ApiKey.ExpiryWarning))
                {
                    k.ExpiryNoticeUtc = now;
                    due.Add((c.Id, k));
                }
            return due;
        });
    }
}

/// <summary>Appels et erreurs par clé et par jour (graphique « 30 derniers jours »). En mémoire, enregistré dans <c>api/usage.json</c> toutes les quelques minutes.</summary>
public class ApiUsage
{
    public sealed class Counter { public long Calls, Errors; }
    private readonly ConcurrentDictionary<string, Counter> counters = new();
    private readonly string file;
    private readonly TimeProvider time;
    private readonly object io = new();
    private const int KeepDays = 60;

    public ApiUsage(IConfiguration config, IWebHostEnvironment env, TimeProvider time) : this(ApiData.Dir(config, env), time) { }

    public ApiUsage(string directory, TimeProvider time)
    {
        this.time = time;
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "usage.json");
        try
        {
            if (File.Exists(file))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, long[]>>(File.ReadAllText(file)) ?? new())
                    if (v.Length == 2) counters[k] = new Counter { Calls = v[0], Errors = v[1] };
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
    }

    private static string Key(Guid keyId, DateTime day) => keyId.ToString("N") + "|" + day.ToString("yyyyMMdd");

    public void Record(Guid keyId, bool error)
    {
        var c = counters.GetOrAdd(Key(keyId, time.GetUtcNow().UtcDateTime), _ => new Counter());
        Interlocked.Increment(ref c.Calls);
        if (error) Interlocked.Increment(ref c.Errors);
    }

    /// <summary>Appels par jour sur les N derniers jours (le plus ancien d'abord) et total des erreurs.</summary>
    public (long[] Calls, long Errors) Series(Guid keyId, int days = 30)
    {
        var today = time.GetUtcNow().UtcDateTime.Date;
        var calls = new long[days]; long errors = 0;
        for (var i = 0; i < days; i++)
            if (counters.TryGetValue(Key(keyId, today.AddDays(i - days + 1)), out var c)) { calls[i] = Interlocked.Read(ref c.Calls); errors += Interlocked.Read(ref c.Errors); }
        return (calls, errors);
    }

    public long Total(Guid keyId, int days = 30) => Series(keyId, days).Calls.Sum();

    /// <summary>Appels du mois civil en cours pour toutes les clés données (Réglages › Utilisation).</summary>
    public long ThisMonth(IEnumerable<Guid> keyIds)
    {
        var now = time.GetUtcNow().UtcDateTime;
        long n = 0;
        foreach (var id in keyIds)
            for (var d = new DateTime(now.Year, now.Month, 1); d <= now.Date; d = d.AddDays(1))
                if (counters.TryGetValue(Key(id, d), out var c)) n += Interlocked.Read(ref c.Calls);
        return n;
    }

    public void Flush()
    {
        var limit = time.GetUtcNow().UtcDateTime.Date.AddDays(-KeepDays).ToString("yyyyMMdd");
        foreach (var k in counters.Keys.Where(k => string.CompareOrdinal(k[(k.IndexOf('|') + 1)..], limit) < 0).ToList()) counters.TryRemove(k, out _);
        var data = counters.ToDictionary(p => p.Key, p => new[] { Interlocked.Read(ref p.Value.Calls), Interlocked.Read(ref p.Value.Errors) });
        lock (io)
        {
            try { File.WriteAllText(file, JsonSerializer.Serialize(data)); } catch (IOException) { }
        }
    }
}

/// <summary>Dossier des fichiers de l'API (usage, idempotence, webhooks) : hors de data.json, comme les journaux de scans.</summary>
public static class ApiData
{
    public static string Dir(IConfiguration config, IWebHostEnvironment env) => Path.Combine(config["Linkii:DataDir"] ?? env.ContentRootPath, "apidata"); // pas « api » : sous Windows il se confondrait avec le dossier source Api/
}
