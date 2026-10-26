using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>Règles de l'intégration Home Assistant, sans accès réseau (testables seules).</summary>
public static class HomeAssistantRules
{
    /// <summary>Identifiant de l'application envoyé à Home Assistant pendant la connexion par identifiant et mot de passe.</summary>
    public const string ClientId = "https://linkii.app/";

    public const string Sensor = "sensor", Switch = "switch", Light = "light";

    /// <summary>Domaines qu'un compte Home Assistant expose aux widgets.</summary>
    public static readonly string[] Domains = { Sensor, Switch, Light };

    /// <summary>Widgets Home Assistant : capteurs (lecture), interrupteurs et lumières (lecture et commande).</summary>
    public static readonly string[] AppIds = { "ha-sensors", "ha-switches", "ha-lights" };

    private static readonly Regex EntityIdPattern = new("^[a-z_]+\\.[a-z0-9_]+$");
    private static readonly char[] Separators = { ',', ';', ' ', '\n', '\r', '\t' };

    public static bool IsHaApp(string? appId) => appId != null && AppIds.Contains(appId);

    /// <summary>Entités saisies ou choisies (séparées par des virgules), sans doublon et au bon format.</summary>
    public static List<string> ParseEntities(string? value) =>
        (value ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => EntityIdPattern.IsMatch(e)).Distinct().ToList();

    public static string Domain(string entityId) => entityId[..entityId.IndexOf('.')];

    /// <summary>Le domaine d'une entité est-il géré par Linkivi (capteur, interrupteur ou lumière) ?</summary>
    public static bool IsSupported(string entityId) => EntityIdPattern.IsMatch(entityId) && Domains.Contains(Domain(entityId));

    /// <summary>Commande autorisée : interrupteurs pour « ha-switches », lumières pour « ha-lights ». Les capteurs ne se commandent pas.</summary>
    public static bool Commandable(string appId, string entityId) => EntityIdPattern.IsMatch(entityId) && appId switch
    {
        "ha-switches" => Domain(entityId) == Switch,
        "ha-lights" => Domain(entityId) == Light,
        _ => false,
    };

    public static string BaseUrl(string? url) => (url ?? "").Trim().TrimEnd('/');
}

/// <summary>Commande d'un interrupteur ou d'une lumière depuis l'écran tactile.</summary>
public record HaSwitchDto(string? EntityId, bool On);

/// <summary>Une entité telle que Home Assistant la décrit (GET /api/states).</summary>
public sealed record HaState(string Id, string State, string Name, string? Unit, string? DeviceClass);

/// <summary>Paramètres de connexion en clair, le temps d'un appel : jamais stockés ni renvoyés.</summary>
public sealed record HaConn(string Url, string Mode, string User, string Secret);

/// <summary>
/// Appels à Home Assistant pour le compte de l'organisation : connexion et test, liste des entités, états, commande.
/// Connexion par jeton d'accès longue durée, ou par identifiant et mot de passe (flux de connexion de Home Assistant ; le jeton obtenu
/// est gardé en mémoire, jamais dans les données). Les états sont gardés quelques secondes : plusieurs widgets ne ré-interrogent pas l'instance.
/// </summary>
public class HomeAssistantClient(SafeHttp http, SecretBox box)
{
    private static readonly ConcurrentDictionary<string, (string Token, DateTime Expires)> Sessions = new();
    private static readonly ConcurrentDictionary<string, (DateTime At, Dictionary<string, HaState> States)> StatesCache = new();
    private static readonly TimeSpan StatesTtl = TimeSpan.FromSeconds(4);

    /// <summary>Paramètres de connexion d'un compte enregistré, secret déchiffré pour l'appel.</summary>
    public HaConn Conn(HomeAssistantAccount a) =>
        new(a.Url, a.AuthMode, a.User, a.Secret.Length > 0 ? box.Unprotect(a.Secret) : "");

    /// <summary>
    /// Connexion : vérifie l'accès, puis enregistre le compte (secret chiffré) avec la liste de ses entités.
    /// Renvoie le compte à enregistrer ; lève une exception si la connexion échoue (rien n'est enregistré).
    /// </summary>
    public async Task<HomeAssistantAccount> Connect(string url, string mode, string user, string secret, HomeAssistantAccount? previous)
    {
        var conn = new HaConn(HomeAssistantRules.BaseUrl(url), mode == "password" ? "password" : "token", user.Trim(), secret);
        var states = await States(conn, fresh: true);
        var account = new HomeAssistantAccount
        {
            Url = conn.Url, AuthMode = conn.Mode, User = conn.Mode == "password" ? conn.User : "",
            Secret = box.Protect(secret), ConnectedUtc = previous?.ConnectedUtc ?? DateTime.UtcNow,
        };
        Fill(account, states);
        return account;
    }

    /// <summary>Recharge la liste des entités du compte (après un ajout dans Home Assistant).</summary>
    public async Task<HomeAssistantAccount> Refresh(HomeAssistantAccount a)
    {
        var copy = a.Copy();
        Fill(copy, await States(Conn(a), fresh: true));
        return copy;
    }

    /// <summary>Change l'état d'un interrupteur ou d'une lumière.</summary>
    public async Task SetState(HaConn conn, string entityId, bool on)
    {
        if (!HomeAssistantRules.IsSupported(entityId) || HomeAssistantRules.Domain(entityId) is not (HomeAssistantRules.Switch or HomeAssistantRules.Light))
            throw new InvalidOperationException("Entité non actionnable.");
        var service = on ? "turn_on" : "turn_off";
        var (status, _) = await Call(conn, HttpMethod.Post, $"/api/services/{HomeAssistantRules.Domain(entityId)}/{service}",
            JsonSerializer.Serialize(new { entity_id = entityId }));
        if (status is < 200 or >= 300) throw new InvalidOperationException($"Home Assistant a répondu {status}.");
    }

    /// <summary>États de toutes les entités. fresh : ignore le cache court (connexion, actualisation de la liste).</summary>
    public async Task<Dictionary<string, HaState>> States(HaConn conn, bool fresh = false)
    {
        var key = CacheKey(conn);
        if (!fresh && StatesCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < StatesTtl) return hit.States;
        var (status, body) = await Call(conn, HttpMethod.Get, "/api/states", null);
        if (status != 200) throw new InvalidOperationException($"Home Assistant a répondu {status}.");
        var map = new Dictionary<string, HaState>();
        using var doc = JsonDocument.Parse(body);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var id = e.GetProperty("entity_id").GetString() ?? "";
            var attrs = e.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            string? Text(string name) => attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(name, out var v) ? v.GetString() : null;
            map[id] = new HaState(id, e.GetProperty("state").GetString() ?? "", Text("friendly_name") ?? id, Text("unit_of_measurement"), Text("device_class"));
        }
        StatesCache[key] = (DateTime.UtcNow, map);
        return map;
    }

    /// <summary>Capteurs, interrupteurs et lumières, par nom.</summary>
    private static void Fill(HomeAssistantAccount a, Dictionary<string, HaState> states)
    {
        a.Entities = states.Values
            .Where(s => HomeAssistantRules.IsSupported(s.Id))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new HomeAssistantEntity { Id = s.Id, Name = s.Name, Domain = HomeAssistantRules.Domain(s.Id), Unit = s.Unit ?? "" })
            .ToList();
        a.EntitiesUtc = DateTime.UtcNow;
        a.LastError = "";
    }

    private static string CacheKey(HaConn c) => c.Url + "|" + c.Mode + "|" + c.User + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.Secret)));

    /// <summary>Appel authentifié à l'instance. Si le jeton a expiré (401) et que la connexion se fait par mot de passe, on se reconnecte une fois.</summary>
    private async Task<(int Status, string Body)> Call(HaConn c, HttpMethod method, string path, string? json, bool retry = true)
    {
        if (c.Url == "") throw new InvalidOperationException("Adresse de Home Assistant manquante.");
        var token = await AccessToken(c);
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token, ["Accept"] = "application/json" };
        var res = await http.Request(method, c.Url + path, json, json == null ? null : "application/json", headers, 8_000_000);
        if (res.Status == 401 && retry && c.Mode == "password")
        {
            Sessions.TryRemove(CacheKey(c), out _);
            return await Call(c, method, path, json, retry: false);
        }
        if (res.Status == 401) throw new InvalidOperationException("Jeton refusé par Home Assistant.");
        return res;
    }

    private async Task<string> AccessToken(HaConn c)
    {
        if (c.Mode != "password")
            return c.Secret.Length > 0 ? c.Secret : throw new InvalidOperationException("Jeton d'accès manquant.");
        var key = CacheKey(c);
        if (Sessions.TryGetValue(key, out var s) && s.Expires > DateTime.UtcNow) return s.Token;
        var (token, seconds) = await Login(c.Url, c.User, c.Secret);
        Sessions[key] = (token, DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 60)));
        return token;
    }

    /// <summary>Connexion comme l'écran de connexion de Home Assistant : étape d'identifiants, code d'autorisation, jeton.</summary>
    private async Task<(string Token, int Seconds)> Login(string baseUrl, string user, string password)
    {
        var flow = await Post(baseUrl + "/auth/login_flow", JsonSerializer.Serialize(new
        {
            client_id = HomeAssistantRules.ClientId, handler = new object?[] { "homeassistant", null }, redirect_uri = HomeAssistantRules.ClientId
        }));
        var flowId = Json(flow.Body).GetProperty("flow_id").GetString() ?? throw new InvalidOperationException("Connexion à Home Assistant impossible.");

        var step = await Post(baseUrl + $"/auth/login_flow/{flowId}", JsonSerializer.Serialize(new
        {
            client_id = HomeAssistantRules.ClientId, username = user, password
        }));
        var result = Json(step.Body);
        if (result.GetProperty("type").GetString() != "create_entry")
            throw new InvalidOperationException(result.TryGetProperty("errors", out _)
                ? "Identifiant ou mot de passe refusé par Home Assistant."
                : "Home Assistant demande une étape supplémentaire (double authentification) : utilisez un jeton d'accès.");
        var code = result.GetProperty("result").GetString();

        var tokenRes = await Post(baseUrl + "/auth/token",
            "grant_type=authorization_code&code=" + Uri.EscapeDataString(code ?? "") + "&client_id=" + Uri.EscapeDataString(HomeAssistantRules.ClientId),
            "application/x-www-form-urlencoded");
        var tok = Json(tokenRes.Body);
        var access = tok.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Jeton non reçu de Home Assistant.");
        var seconds = tok.TryGetProperty("expires_in", out var ex) && ex.TryGetInt32(out var n) ? n : 1800;
        return (access, seconds);
    }

    private async Task<(int Status, string Body)> Post(string url, string body, string contentType = "application/json")
    {
        var res = await http.Request(HttpMethod.Post, url, body, contentType, null, 1_000_000);
        if (res.Status is < 200 or >= 300) throw new InvalidOperationException($"Home Assistant a répondu {res.Status} pendant la connexion.");
        return res;
    }

    private static JsonElement Json(string body)
    {
        try { return JsonDocument.Parse(body).RootElement.Clone(); }
        catch (JsonException) { throw new InvalidOperationException("Réponse inattendue de Home Assistant : vérifiez l'adresse."); }
    }
}

/// <summary>
/// Fournisseur des widgets Home Assistant (capteurs, interrupteurs, lumières) : lit le compte de l'organisation, et les seules entités
/// choisies dans le widget.
/// </summary>
public class HomeAssistantProvider(HomeAssistantClient ha) : IAppProvider
{
    public string Id => "homeassistant";

    public async Task<object?> Fetch(AppRuntime rt)
    {
        var account = rt.Client.HomeAssistant ?? throw new InvalidOperationException("Compte Home Assistant non connecté.");
        var configured = HomeAssistantRules.ParseEntities(rt.Get("entities"));
        var states = await ha.States(ha.Conn(account));
        var list = configured.Select(id => states.TryGetValue(id, out var s)
            ? new
            {
                id, name = s.Name, domain = HomeAssistantRules.Domain(id), state = s.State, unit = s.Unit, deviceClass = s.DeviceClass,
                on = s.State == "on", switchable = HomeAssistantRules.Domain(id) is HomeAssistantRules.Switch or HomeAssistantRules.Light,
            }
            : new { id, name = id, domain = HomeAssistantRules.Domain(id), state = "absent", unit = (string?)null, deviceClass = (string?)null, on = false, switchable = false });
        return new { title = rt.Get("title"), fetchedAt = DateTime.UtcNow, entities = list };
    }

    public async Task<string> Test(AppRuntime rt)
    {
        var account = rt.Client.HomeAssistant ?? throw new InvalidOperationException("Connectez d'abord le compte Home Assistant dans Intégrations.");
        var configured = HomeAssistantRules.ParseEntities(rt.Get("entities"));
        if (configured.Count == 0) throw new InvalidOperationException("Choisissez au moins une entité.");
        var states = await ha.States(ha.Conn(account), fresh: true);
        var missing = configured.Where(id => !states.ContainsKey(id)).ToList();
        var msg = $"{configured.Count - missing.Count} entité(s) disponible(s).";
        if (missing.Count > 0) msg += $" ⚠ Introuvable(s) : {string.Join(", ", missing)}.";
        return msg;
    }
}
