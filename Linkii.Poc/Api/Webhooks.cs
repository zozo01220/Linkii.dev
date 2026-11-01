using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Linkii.Poc;

/// <summary>Adresse d'une organisation qui reçoit des événements (§154). Le secret sert à signer chaque envoi : il est chiffré (SecretBox), jamais renvoyé.</summary>
public class WebhookEndpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Types d'événements écoutés (« screen.offline »), ou avec joker (« screen.* », « * »).</summary>
    public List<string> Events { get; set; } = new();
    public string Secret { get; set; } = "";
    /// <summary>Ancien secret, encore signé pendant 24 h après un renouvellement (changer sans coupure).</summary>
    public string? PreviousSecret { get; set; }
    public DateTime? PreviousSecretUntilUtc { get; set; }
    public bool Active { get; set; } = true;
    public string DisabledReason { get; set; } = "";
    public DateTime? DisabledUtc { get; set; }
    /// <summary>Version de la charge utile figée à la création : l'évolution du format n'abîme pas un abonné existant.</summary>
    public string ApiVersion { get; set; } = ApiRegistry.Version;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public Guid? CreatedBy { get; set; }
}

/// <summary>Un événement tel qu'il est envoyé : enveloppe figée dès la première version.</summary>
public record EventEnvelope(string Id, string Type, string ApiVersion, DateTime CreatedUtc, Guid ClientId, JsonElement Data)
{
    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["id"] = Id, ["type"] = Type, ["apiVersion"] = ApiVersion, ["createdAt"] = CreatedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        ["organisationId"] = ClientId, ["data"] = Data
    }, ApiJson.Options);
}

/// <summary>Seul point d'émission des événements. Ne bloque jamais et ne lève jamais : il est appelé jusque sous le verrou de <see cref="JsonStore"/>.</summary>
public interface IEventPublisher
{
    void Publish(Guid clientId, string type, object? data = null);
}

public class EventBus : IEventPublisher
{
    private readonly Channel<EventEnvelope> channel = Channel.CreateBounded<EventEnvelope>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ApiRegistry registry;
    private readonly TimeProvider time;
    private readonly ILogger<EventBus> log;

    public EventBus(ApiRegistry registry, TimeProvider time, ILogger<EventBus> log) { this.registry = registry; this.time = time; this.log = log; }

    public ChannelReader<EventEnvelope> Reader => channel.Reader;

    public void Publish(Guid clientId, string type, object? data = null)
    {
        try
        {
            if (!registry.IsKnownEvent(type)) { log.LogError("Événement non déclaré par un module : « {Type} » (ignoré)", type); return; }
            var el = JsonSerializer.SerializeToElement(data ?? new { }, ApiJson.Options);
            channel.Writer.TryWrite(new EventEnvelope("evt_" + Guid.NewGuid().ToString("N")[..16], type, ApiRegistry.Version, time.GetUtcNow().UtcDateTime, clientId, el));
        }
        catch (Exception ex) { log.LogWarning(ex, "Événement « {Type} » perdu", type); }
    }
}

public class DeliveryAttempt
{
    public DateTime AtUtc { get; set; }
    public int? Status { get; set; }
    public string? Error { get; set; }
    public int Ms { get; set; }
    public string? Response { get; set; }
}

/// <summary>Une livraison d'un événement à une adresse : l'état complet est réécrit en fin de journal à chaque changement (la dernière ligne gagne).</summary>
public class Delivery
{
    public const string Pending = "pending", Delivered = "delivered", Failed = "failed", Abandoned = "abandoned";
    public string Id { get; set; } = "dlv_" + Guid.NewGuid().ToString("N")[..16];
    public string EventId { get; set; } = "";
    public string EventType { get; set; } = "";
    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }
    public string EndpointName { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime QueuedUtc { get; set; }
    public string Status { get; set; } = Pending;
    public DateTime? NextUtc { get; set; }
    public bool Test { get; set; }
    public string? Note { get; set; }
    public List<DeliveryAttempt> Attempts { get; set; } = new();
}

/// <summary>Journal des livraisons par organisation (<c>api/webhooks/{organisation}.jsonl</c>), hors data.json. La file d'attente est reconstruite au démarrage.</summary>
public class WebhookJournal
{
    private sealed class ClientLog { public readonly object Lock = new(); public readonly Dictionary<string, Delivery> ById = new(); }
    private readonly ConcurrentDictionary<Guid, ClientLog> logs = new();
    private readonly ConcurrentDictionary<string, Delivery> pending = new();
    private readonly string dir;
    private readonly TimeProvider time;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public WebhookJournal(IConfiguration config, IWebHostEnvironment env, TimeProvider time) : this(Path.Combine(ApiData.Dir(config, env), "webhooks"), time) { }

    public WebhookJournal(string directory, TimeProvider time)
    {
        dir = directory;
        this.time = time;
        Directory.CreateDirectory(dir);
    }

    private string FileOf(Guid clientId) => Path.Combine(dir, clientId.ToString("N") + ".jsonl");

    private ClientLog Open(Guid clientId) => logs.GetOrAdd(clientId, id =>
    {
        var log = new ClientLog();
        try
        {
            if (File.Exists(FileOf(id)))
                foreach (var line in File.ReadLines(FileOf(id)))
                {
                    var d = JsonSerializer.Deserialize<Delivery>(line, Json);
                    if (d == null) continue;
                    log.ById[d.Id] = d;
                }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        foreach (var d in log.ById.Values.Where(d => d.Status == Delivery.Pending)) pending[d.Id] = d;
        return log;
    });

    /// <summary>Charge tous les journaux (au démarrage) : les livraisons en attente reprennent.</summary>
    public void LoadAll()
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl"))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(f), "N", out var id)) Open(id);
    }

    public void Save(Delivery d)
    {
        var log = Open(d.ClientId);
        lock (log.Lock)
        {
            log.ById[d.Id] = d;
            try { File.AppendAllText(FileOf(d.ClientId), JsonSerializer.Serialize(d, Json) + "\n"); } catch (IOException) { }
        }
        if (d.Status == Delivery.Pending) pending[d.Id] = d; else pending.TryRemove(d.Id, out _);
    }

    public Delivery? Get(Guid clientId, string id) { var log = Open(clientId); lock (log.Lock) return log.ById.GetValueOrDefault(id); }

    public List<Delivery> Pending() => pending.Values.ToList();
    public int PendingCount(Guid endpointId) => pending.Values.Count(d => d.EndpointId == endpointId);

    /// <summary>Livraisons les plus récentes d'abord. <paramref name="problemsOnly"/> : échecs, abandons et attentes.</summary>
    public List<Delivery> Query(Guid clientId, Guid? endpointId = null, bool problemsOnly = false, string? eventType = null, int limit = 50, DateTime? before = null)
    {
        var log = Open(clientId);
        lock (log.Lock)
            return log.ById.Values
                .Where(d => (endpointId == null || d.EndpointId == endpointId) && (eventType == null || d.EventType == eventType)
                    && (!problemsOnly || d.Status != Delivery.Delivered) && (before == null || d.QueuedUtc < before))
                .OrderByDescending(d => d.QueuedUtc).Take(limit).ToList();
    }

    public int Count(Guid clientId, bool problemsOnly = false)
    {
        var log = Open(clientId);
        lock (log.Lock) return log.ById.Values.Count(d => !problemsOnly || d.Status != Delivery.Delivered);
    }

    /// <summary>Retire les livraisons plus anciennes que la conservation de l'organisation et réécrit le fichier.</summary>
    public void Prune(Func<Guid, int> retentionDays)
    {
        foreach (var (id, log) in logs.ToArray())
        {
            var limit = time.GetUtcNow().UtcDateTime.AddDays(-retentionDays(id));
            lock (log.Lock)
            {
                var old = log.ById.Values.Where(d => d.QueuedUtc < limit && d.Status != Delivery.Pending).Select(d => d.Id).ToList();
                if (old.Count == 0) continue;
                foreach (var k in old) log.ById.Remove(k);
                try { File.WriteAllLines(FileOf(id), log.ById.Values.OrderBy(d => d.QueuedUtc).Select(d => JsonSerializer.Serialize(d, Json))); } catch (IOException) { }
            }
        }
    }

    public void DeleteClient(Guid clientId)
    {
        logs.TryRemove(clientId, out _);
        foreach (var k in pending.Where(p => p.Value.ClientId == clientId).Select(p => p.Key).ToList()) pending.TryRemove(k, out _);
        try { File.Delete(FileOf(clientId)); } catch (IOException) { }
    }
}

public record SendResult(int? Status, string? Error, int Ms, string Response)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>Signature des envois : <c>Linkivi-Signature: t=&lt;secondes&gt;,v1=&lt;HMAC-SHA256(secret, t + "." + corps)&gt;</c>. Deux <c>v1</c> pendant le chevauchement d'un renouvellement de secret.</summary>
public static class WebhookSignature
{
    public static string Compute(string secret, long t, string body) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(t.ToString(CultureInfo.InvariantCulture) + "." + body))).ToLowerInvariant();

    public static string Header(IEnumerable<string> secrets, long t, string body) =>
        "t=" + t.ToString(CultureInfo.InvariantCulture) + string.Concat(secrets.Select(s => ",v1=" + Compute(s, t, body)));

    /// <summary>Contrôle côté récepteur (sert aux tests et à la documentation) : signature exacte et horodatage récent.</summary>
    public static bool Verify(string header, string body, string secret, DateTimeOffset now, TimeSpan tolerance)
    {
        long t = 0; var sigs = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "t") long.TryParse(kv[1], out t); else if (kv[0] == "v1") sigs.Add(kv[1]);
        }
        if (t == 0 || Math.Abs((now - DateTimeOffset.FromUnixTimeSeconds(t)).TotalSeconds) > tolerance.TotalSeconds) return false;
        var expected = Encoding.UTF8.GetBytes(Compute(secret, t, body));
        return sigs.Any(s => CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(s)));
    }
}

/// <summary>
/// Envoi HTTP des webhooks : POST, délai de 10 s, aucune redirection suivie, réponse tronquée à 2 Ko. Même contrôle d'adresse que <see cref="SafeHttp"/>
/// au moment de la connexion (réseau interne refusé, y compris par « DNS rebinding »), sauf <c>Linkii:Api:AllowLocalWebhooks</c> (développement et tests).
/// </summary>
public class WebhookSender
{
    private readonly HttpClient http;
    public bool AllowLocal { get; }
    public const int Seconds = 10, MaxResponse = 2048;

    public WebhookSender(IConfiguration config) : this(config.GetValue<bool>("Linkii:Api:AllowLocalWebhooks")) { }

    public WebhookSender(bool allowLocal) : this(Handler(allowLocal), allowLocal) { }

    /// <summary>Gestionnaire HTTP fourni (tests) : aucune connexion réelle.</summary>
    public WebhookSender(HttpMessageHandler handler, bool allowLocal)
    {
        AllowLocal = allowLocal;
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Seconds) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Linkivi-Webhooks/1.0");
    }

    private static SocketsHttpHandler Handler(bool allowLocal)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(5) };
        if (!allowLocal) handler.ConnectCallback = SafeHttp.Connect;
        return handler;
    }

    public async Task<SendResult> Post(string url, string body, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var text = "";
            try
            {
                await using var s = await res.Content.ReadAsStreamAsync(ct);
                var buf = new byte[MaxResponse]; var n = 0; int r;
                while (n < buf.Length && (r = await s.ReadAsync(buf.AsMemory(n), ct)) > 0) n += r;
                text = Encoding.UTF8.GetString(buf, 0, n);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException) { }
            return new SendResult((int)res.StatusCode, null, (int)sw.ElapsedMilliseconds, text);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new SendResult(null, $"délai de {Seconds} s dépassé", (int)sw.ElapsedMilliseconds, ""); }
        catch (HttpRequestException ex) { return new SendResult(null, ex.InnerException?.Message ?? ex.Message, (int)sw.ElapsedMilliseconds, ""); }
    }
}

/// <summary>Ce que le socle signale aux administrateurs (centre de notifications, e-mail, mobile selon leurs réglages d'alerte).</summary>
public interface IApiAlerts
{
    Task WebhookDisabled(Guid clientId, WebhookEndpoint endpoint, string reason);
    Task KeyExpiring(Guid clientId, ApiKey key);
}

public class ApiAlerts(AlertService alerts, TimeProvider time) : IApiAlerts
{
    public Task WebhookDisabled(Guid clientId, WebhookEndpoint e, string reason) =>
        alerts.Raise(clientId, Alerts.Integrations, $"Webhook « {e.Name} » désactivé", $"{reason}. Les événements des dernières 24 heures sont conservés : corrigez le récepteur, puis réactivez l'adresse.",
            "/apiwebhooks?tab=hooks", severity: Notifs.Error, dedupe: "webhook-off:" + e.Id);

    public Task KeyExpiring(Guid clientId, ApiKey k)
    {
        var days = Math.Max(1, (int)Math.Ceiling(((k.ExpiresUtc ?? time.GetUtcNow().UtcDateTime) - time.GetUtcNow().UtcDateTime).TotalDays));
        return alerts.Raise(clientId, Alerts.Integrations, $"La clé « {k.Name} » expire dans {days} jour{(days > 1 ? "s" : "")}",
            "Renouvelez-la avant l'expiration pour ne pas couper l'outil qui l'utilise.", "/apiwebhooks", severity: Notifs.Warn, dedupe: "key-expiring:" + k.Id);
    }
}

public record WebhookResult(WebhookEndpoint? Endpoint, string? Secret, string? Error);
public record WebhookStats(int Delivered, int Finished, int? RatePercent, int? MedianMs, int Consecutive, DateTime? LastUtc, string? LastStatus);

/// <summary>Gestion des adresses de webhook : les écrans d'administration et l'API passent par ici.</summary>
public class WebhookService(JsonStore store, ApiRegistry registry, SecretBox box, WebhookJournal journal, WebhookSender sender, TimeProvider time, IApiAlerts alerts, ILogger<WebhookService> log)
{
    private readonly ConcurrentDictionary<Guid, int> consecutive = new();
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public const int MaxEvents = 50;

    /// <summary>Adresse de réception : HTTPS (HTTP et réseau local seulement en développement), sans identifiants, pas d'adresse interne.</summary>
    public string? ValidateUrl(string? url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return "Indiquez l'adresse de réception.";
        if (url.Length > 500 || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return "Adresse invalide.";
        if (u.UserInfo.Length > 0) return "L'adresse ne doit pas contenir d'identifiants.";
        if (sender.AllowLocal) return u.Scheme is "http" or "https" ? null : "Adresse invalide : http:// ou https:// attendu.";
        if (u.Scheme != "https") return "L'adresse doit commencer par https://.";
        if (u.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && !SafeHttp.IsPublic(ip)) return "Les adresses du réseau interne sont refusées.";
        if (u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return "Les adresses du réseau interne sont refusées.";
        return null;
    }

    private string? ValidateEvents(List<string> events)
    {
        if (events.Count == 0) return "Choisissez au moins un événement.";
        if (events.Count > MaxEvents) return $"{MaxEvents} événements au plus.";
        return events.FirstOrDefault(e => !registry.IsValidSubscription(e)) is { } bad ? $"Événement inconnu : {bad}." : null;
    }

    /// <summary>Un événement d'une intégration du Store ne peut être choisi que si elle est installée (un joker qui n'en couvre que des non installées est refusé aussi).</summary>
    private string? CheckApps(Db db, Guid clientId, IEnumerable<string> patterns)
    {
        foreach (var p in patterns)
        {
            var matched = registry.Events.Where(e => ApiRegistry.Matches(p, e.Id)).ToList();
            if (matched.Count > 0 && matched.All(e => e.App != null && !db.AppInstalls.Any(a => a.ClientId == clientId && a.AppId == e.App)))
                return $"L'événement « {p} » demande l'intégration « {matched[0].App} » : installez-la depuis le Store.";
        }
        return null;
    }

    private static string NewSecret()
    {
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var sb = new StringBuilder("whsec_");
        for (var i = 0; i < 40; i++) sb.Append(alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]);
        return sb.ToString();
    }

    private static List<string> Clean(IEnumerable<string>? events) => (events ?? Array.Empty<string>()).Select(e => e.Trim()).Where(e => e.Length > 0).Distinct().ToList();

    public WebhookResult Create(Guid clientId, Guid? ownerId, string? name, string? url, IEnumerable<string>? events)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return new(null, null, "Donnez un nom à l'adresse.");
        if (name.Length > 80) return new(null, null, "Le nom est trop long (80 caractères au plus).");
        if (ValidateUrl(url) is { } ue) return new(null, null, ue);
        var list = Clean(events);
        if (ValidateEvents(list) is { } ee) return new(null, null, ee);
        var secret = NewSecret();
        var e = new WebhookEndpoint { Name = name, Url = url!.Trim(), Events = list, Secret = box.Protect(secret), CreatedBy = ownerId, CreatedUtc = Now };
        var error = store.Write(db =>
        {
            var c = db.Clients.FirstOrDefault(x => x.Id == clientId);
            if (c == null) return "Organisation introuvable.";
            if (!ApiLimits.Allowed(c)) return "L'API fait partie du plan Pro.";
            if (c.Webhooks.Count >= ApiLimits.Webhooks(c)) return $"Limite atteinte : {ApiLimits.Webhooks(c)} adresses au plus.";
            if (CheckApps(db, clientId, list) is { } missing) return missing;
            c.Webhooks.Add(e);
            return null;
        });
        return error == null ? new(e, secret, null) : new(null, null, error);
    }

    public string? Update(Guid clientId, Guid id, string? name, string? url, IEnumerable<string>? events)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return "Donnez un nom à l'adresse.";
        if (name.Length > 80) return "Le nom est trop long (80 caractères au plus).";
        if (ValidateUrl(url) is { } ue) return ue;
        var list = Clean(events);
        if (ValidateEvents(list) is { } ee) return ee;
        return store.Write(db =>
        {
            var e = db.Clients.FirstOrDefault(c => c.Id == clientId)?.Webhooks.FirstOrDefault(w => w.Id == id);
            if (e == null) return "Adresse introuvable.";
            if (CheckApps(db, clientId, list.Where(p => !e.Events.Contains(p)).ToList()) is { } missing) return missing;   // seuls les événements ajoutés sont contrôlés
            e.Name = name; e.Url = url!.Trim(); e.Events = list;
            return null;
        });
    }

    public bool Delete(Guid clientId, Guid id)
    {
        var ok = store.Write(db => db.Clients.FirstOrDefault(c => c.Id == clientId)?.Webhooks.RemoveAll(w => w.Id == id) > 0);
        consecutive.TryRemove(id, out _);
        return ok;
    }

    public WebhookEndpoint? Get(Guid clientId, Guid id) => store.Read(db => db.Clients.FirstOrDefault(c => c.Id == clientId)?.Webhooks.FirstOrDefault(w => w.Id == id));
    public List<WebhookEndpoint> List(Guid clientId) => store.Read(db => db.Clients.FirstOrDefault(c => c.Id == clientId)?.Webhooks.OrderBy(w => w.CreatedUtc).ToList() ?? new());

    /// <summary>Nouveau secret ; l'ancien reste signé 24 h pour laisser le temps de changer côté récepteur.</summary>
    public string? RotateSecret(Guid clientId, Guid id)
    {
        var secret = NewSecret();
        var now = Now;
        var ok = store.Write(db =>
        {
            var e = db.Clients.FirstOrDefault(c => c.Id == clientId)?.Webhooks.FirstOrDefault(w => w.Id == id);
            if (e == null) return false;
            e.PreviousSecret = e.Secret; e.PreviousSecretUntilUtc = now.AddHours(24);
            e.Secret = box.Protect(secret);
            return true;
        });
        return ok ? secret : null;
    }

    /// <summary>Secrets avec lesquels signer maintenant : le courant, et l'ancien pendant le chevauchement.</summary>
    public List<string> SigningSecrets(WebhookEndpoint e)
    {
        var list = new List<string> { box.Unprotect(e.Secret) };
        if (e.PreviousSecret != null && e.PreviousSecretUntilUtc > Now) list.Add(box.Unprotect(e.PreviousSecret));
        return list;
    }

    public bool Disable(Guid clientId, Guid id, string reason, bool notify)
    {
        var now = Now;
        var e = store.Write(db =>
        {
            var w = db.Clients.FirstOrDefault(c => c.Id == clientId)?.Webhooks.FirstOrDefault(x => x.Id == id);
            if (w == null || !w.Active) return null;
            w.Active = false; w.DisabledReason = reason; w.DisabledUtc = now;
            return w;
        });
        if (e == null) return false;
        if (notify) _ = Task.Run(async () => { try { await alerts.WebhookDisabled(clientId, e, reason); } catch (Exception ex) { log.LogWarning(ex, "Alerte de webhook désactivé non envoyée"); } });
        return true;
    }

    /// <summary>Réactive l'adresse et rejoue les livraisons abandonnées des dernières 24 h (celles-ci sont renvoyées au premier passage du répartiteur). Rend leur nombre.</summary>
    public int Reactivate(Guid clientId, Guid id, bool replay = true)
    {
        var e = store.Write(db =>
        {
            var w = db.Clients.FirstOrDefault(c => c.Id == clientId)?.Webhooks.FirstOrDefault(x => x.Id == id);
            if (w == null) return null;
            w.Active = true; w.DisabledReason = ""; w.DisabledUtc = null;
            return w;
        });
        if (e == null) return 0;
        consecutive[id] = 0;
        if (!replay) return 0;
        var since = Now.AddHours(-24);
        var n = 0;
        foreach (var d in journal.Query(clientId, id, problemsOnly: true, limit: 1000).Where(d => d.Status == Delivery.Abandoned && !d.Test && d.QueuedUtc >= since).OrderBy(d => d.QueuedUtc))
        {
            journal.Save(new Delivery { EventId = d.EventId, EventType = d.EventType, ClientId = clientId, EndpointId = id, EndpointName = e.Name, Payload = d.Payload, QueuedUtc = Now, NextUtc = Now, Note = "rejouée" });
            n++;
        }
        return n;
    }

    /// <summary>Renvoie une livraison du journal : une nouvelle livraison (même événement, même contenu) part au prochain passage du répartiteur.</summary>
    public string? Resend(Guid clientId, string deliveryId)
    {
        var d = journal.Get(clientId, deliveryId);
        if (d == null) return "Livraison introuvable.";
        var e = Get(clientId, d.EndpointId);
        if (e == null) return "L'adresse n'existe plus.";
        if (!e.Active) return "L'adresse est désactivée : réactivez-la d'abord.";
        journal.Save(new Delivery { EventId = d.EventId, EventType = d.EventType, ClientId = clientId, EndpointId = e.Id, EndpointName = e.Name, Payload = d.Payload, QueuedUtc = Now, NextUtc = Now, Note = "renvoyée" });
        return null;
    }

    public int NoteFailure(Guid id) => consecutive.AddOrUpdate(id, 1, (_, n) => n + 1);
    public void NoteSuccess(Guid id) => consecutive[id] = 0;
    public int Consecutive(Guid id) => consecutive.GetValueOrDefault(id);

    /// <summary>Un événement « ping » envoyé tout de suite, une seule fois, sans réessai : le résultat est écrit au journal et rendu.</summary>
    public async Task<Delivery?> SendTest(Guid clientId, Guid id, CancellationToken ct = default)
    {
        var e = Get(clientId, id);
        if (e == null) return null;
        var ev = new EventEnvelope("evt_" + Guid.NewGuid().ToString("N")[..16], "ping", ApiRegistry.Version, Now, clientId, JsonSerializer.SerializeToElement(new { ping = true }, ApiJson.Options));
        var d = new Delivery { EventId = ev.Id, EventType = ev.Type, ClientId = clientId, EndpointId = id, EndpointName = e.Name, Payload = ev.ToJson(), QueuedUtc = Now, Test = true, Status = Delivery.Failed };
        var r = await sender.Post(e.Url, d.Payload, Headers(d, e), ct);
        d.Attempts.Add(new DeliveryAttempt { AtUtc = Now, Status = r.Status, Error = r.Error, Ms = r.Ms, Response = r.Response });
        d.Status = r.Ok ? Delivery.Delivered : Delivery.Failed;
        journal.Save(d);
        return d;
    }

    public Dictionary<string, string> Headers(Delivery d, WebhookEndpoint e)
    {
        var t = time.GetUtcNow().ToUnixTimeSeconds();
        return new()
        {
            ["Linkivi-Event"] = d.EventType, ["Linkivi-Delivery"] = d.Id,
            ["Linkivi-Signature"] = WebhookSignature.Header(SigningSecrets(e), t, d.Payload),
        };
    }

    public WebhookStats Stats(Guid clientId, Guid id)
    {
        var since = Now.AddDays(-7);
        var recent = journal.Query(clientId, id, limit: 5000).Where(d => d.QueuedUtc >= since && !d.Test).ToList();
        var finished = recent.Where(d => d.Status is Delivery.Delivered or Delivery.Abandoned).ToList();
        var ok = finished.Count(d => d.Status == Delivery.Delivered);
        var ms = recent.Where(d => d.Status == Delivery.Delivered && d.Attempts.Count > 0).Select(d => d.Attempts[^1].Ms).OrderBy(x => x).ToList();
        var last = journal.Query(clientId, id, limit: 1).FirstOrDefault();
        return new WebhookStats(ok, finished.Count, finished.Count == 0 ? null : (int)Math.Round(100.0 * ok / finished.Count), ms.Count == 0 ? null : ms[ms.Count / 2], Consecutive(id), last?.QueuedUtc, last?.Status);
    }
}

/// <summary>
/// Répartiteur : lit le bus, crée une livraison par adresse abonnée (écrite au journal d'abord), puis envoie. Réessais à 1 min, 5 min, 30 min, 2 h (5 tentatives),
/// concurrence limitée par adresse, 10 échecs consécutifs ou une réponse 410 désactivent l'adresse.
/// </summary>
public class WebhookDispatcher(EventBus bus, WebhookJournal journal, WebhookSender sender, WebhookService svc, JsonStore store, TimeProvider time, ILogger<WebhookDispatcher> log) : BackgroundService
{
    public static readonly TimeSpan[] Backoff = { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2) };
    public const int MaxAttempts = 5, DisableAfter = 10, QueueCap = 1000, PerEndpoint = 2;

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<string, byte> inflight = new();
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try { journal.LoadAll(); } catch (Exception ex) { log.LogWarning(ex, "Journal des webhooks : reprise impossible"); }
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try { await foreach (var ev in bus.Reader.ReadAllAsync(ct)) { try { FanOut(ev); } catch (Exception ex) { log.LogWarning(ex, "Événement {Type} non réparti", ev.Type); } } }
            catch (OperationCanceledException) { }
        }, ct);
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); } catch (Exception ex) { log.LogWarning(ex, "Webhooks : passage en erreur"); }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Une livraison par adresse active qui écoute ce type d'événement.</summary>
    public int FanOut(EventEnvelope ev)
    {
        var targets = store.Read(db => db.Clients.FirstOrDefault(c => c.Id == ev.ClientId) is { } t && ApiLimits.Allowed(t)
            ? t.Webhooks.Where(w => w.Active && w.Events.Any(p => ApiRegistry.Matches(p, ev.Type))).Select(w => (w.Id, w.Name)).ToList()
            : new());
        var payload = ev.ToJson();
        foreach (var (id, name) in targets)
        {
            if (journal.PendingCount(id) >= QueueCap)
            {
                var oldest = journal.Pending().Where(d => d.EndpointId == id).OrderBy(d => d.QueuedUtc).First();
                oldest.Status = Delivery.Abandoned; oldest.NextUtc = null; oldest.Note = "file d'attente pleine";
                journal.Save(oldest);
            }
            journal.Save(new Delivery { EventId = ev.Id, EventType = ev.Type, ClientId = ev.ClientId, EndpointId = id, EndpointName = name, Payload = payload, QueuedUtc = ev.CreatedUtc, NextUtc = ev.CreatedUtc });
        }
        return targets.Count;
    }

    /// <summary>Lance les envois arrivés à échéance. Rend leurs tâches (les tests les attendent).</summary>
    public List<Task> Tick()
    {
        var now = Now;
        var started = new List<Task>();
        foreach (var d in journal.Pending().Where(d => d.NextUtc <= now))
            if (inflight.TryAdd(d.Id, 0)) started.Add(Task.Run(() => Send(d)));
        return started;
    }

    public Task TickAndWait() => Task.WhenAll(Tick());

    private async Task Send(Delivery d)
    {
        var gate = gates.GetOrAdd(d.EndpointId, _ => new SemaphoreSlim(PerEndpoint));
        await gate.WaitAsync();
        try
        {
            var e = svc.Get(d.ClientId, d.EndpointId);
            if (e == null) { Abandon(d, "adresse supprimée"); return; }
            if (!e.Active) { Abandon(d, "adresse désactivée"); return; }
            var r = await sender.Post(e.Url, d.Payload, svc.Headers(d, e));
            d.Attempts.Add(new DeliveryAttempt { AtUtc = Now, Status = r.Status, Error = r.Error, Ms = r.Ms, Response = r.Response });
            if (r.Ok)
            {
                d.Status = Delivery.Delivered; d.NextUtc = null;
                svc.NoteSuccess(e.Id);
            }
            else
            {
                var failures = svc.NoteFailure(e.Id);
                if (d.Attempts.Count >= MaxAttempts) { d.Status = Delivery.Abandoned; d.NextUtc = null; }
                else d.NextUtc = Now + Backoff[d.Attempts.Count - 1];
                var why = r.Status is { } s ? $"HTTP {s}" : r.Error ?? "aucune réponse";
                if (r.Status == 410) { d.Status = Delivery.Abandoned; d.NextUtc = null; svc.Disable(d.ClientId, e.Id, "Le récepteur répond 410 (adresse supprimée de son côté)", notify: true); }
                else if (failures >= DisableAfter) svc.Disable(d.ClientId, e.Id, $"{DisableAfter} échecs consécutifs ({why})", notify: true);
            }
            journal.Save(d);
        }
        catch (Exception ex) { log.LogWarning(ex, "Livraison {Id} en erreur", d.Id); }
        finally { gate.Release(); inflight.TryRemove(d.Id, out _); }
    }

    private void Abandon(Delivery d, string note)
    {
        d.Status = Delivery.Abandoned; d.NextUtc = null; d.Note = note;
        journal.Save(d);
    }
}

/// <summary>Tâches périodiques du socle : dernières utilisations des clés, compteurs d'usage, purge des journaux, rappels d'expiration.</summary>
public class ApiMaintenanceWorker(ApiKeyService keys, ApiUsage usage, IdempotencyStore idempotency, WebhookJournal journal, JsonStore store, IApiAlerts alerts, ILogger<ApiMaintenanceWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var n = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { break; }
            await Run(hourly: ++n % 12 == 0);
        }
        try { keys.FlushUsage(); usage.Flush(); } catch (Exception ex) { log.LogWarning(ex, "Usage de l'API non enregistré à l'arrêt"); }
    }

    public async Task Run(bool hourly)
    {
        try
        {
            keys.FlushUsage();
            usage.Flush();
            if (!hourly) return;
            idempotency.Prune();
            journal.Prune(id => store.Read(db => db.Clients.FirstOrDefault(c => c.Id == id) is { } t ? ApiLimits.JournalDays(t) : ApiLimits.DefaultJournalDays));
            foreach (var (client, key) in keys.TakeExpiryNotices())
                try { await alerts.KeyExpiring(client, key); } catch (Exception ex) { log.LogWarning(ex, "Rappel d'expiration non envoyé"); }
        }
        catch (Exception ex) { log.LogWarning(ex, "Entretien de l'API en erreur"); }
    }
}
