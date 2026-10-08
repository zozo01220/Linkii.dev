using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Linkii.Poc;

/// <summary>
/// Canva (Connect API) : connexion du compte Canva d'un client (OAuth 2 + PKCE), liste de ses designs,
/// export d'un design (PNG par page ou MP4) importé dans la médiathèque pour une lecture hors ligne.
/// Intégration : saisie par l'organisation dans Réglages › Applications (identifiant, secret chiffré, adresse de retour),
/// à défaut Linkii:Canva:ClientId, Linkii:Canva:ClientSecret, Linkii:Canva:RedirectUri sur le serveur.
/// </summary>
public class CanvaService(IConfiguration config, JsonStore store, SecretBox box, IHttpClientFactory httpFactory, MediaImporter importer, ILogger<CanvaService> log)
{
    private const string AuthorizeUrl = "https://www.canva.com/api/oauth/authorize";
    private const string Api = "https://api.canva.com/rest/v1";
    private const string Scopes = "design:meta:read design:content:read profile:read";

    // Intégration Canva (portail développeurs) : saisie par l'organisation dans Réglages › Applications,
    // à défaut celle du serveur (Linkii:Canva:ClientId / ClientSecret / RedirectUri).
    private record Creds(string Id, string Secret, string Redirect);

    private Creds CredsOf(Guid clientId)
    {
        var (id, secret, redirect) = store.Read(d => d.Clients.FirstOrDefault(c => c.Id == clientId) is { } t
            ? (t.CanvaClientId, t.CanvaClientSecret, t.CanvaRedirectUri) : ("", "", ""));
        if (id.Length > 0 && secret.Length > 0)
            return new Creds(id, box.Unprotect(secret), redirect.Length > 0 ? redirect : ServerRedirect);
        return new Creds(config["Linkii:Canva:ClientId"] ?? "", config["Linkii:Canva:ClientSecret"] ?? "", ServerRedirect);
    }

    private string ServerRedirect => config["Linkii:Canva:RedirectUri"] is { Length: > 0 } r ? r : $"https://{config["Linkii:BaseDomain"] ?? "linkii.com"}/canva/callback";

    /// <summary>Intégration utilisable : identifiant et secret connus (organisation ou serveur).</summary>
    public bool IsConfigured(Guid clientId) => CredsOf(clientId) is { Id.Length: > 0, Secret.Length: > 0 };

    public record IntegrationSettings(string ClientId, bool HasSecret, string RedirectUri, bool FromServer);

    /// <summary>Réglages affichés dans le formulaire (le secret n'est jamais renvoyé).</summary>
    public IntegrationSettings SettingsOf(Guid clientId)
    {
        var t = store.Read(d => d.Clients.First(c => c.Id == clientId));
        var server = t.CanvaClientId.Length == 0 && (config["Linkii:Canva:ClientId"] ?? "").Length > 0;
        return new IntegrationSettings(t.CanvaClientId, t.CanvaClientSecret.Length > 0, t.CanvaRedirectUri, server);
    }

    /// <summary>Enregistre l'intégration de l'organisation ; secret vide : le secret enregistré est conservé.
    /// Changer d'intégration oublie la connexion en cours (ses jetons appartiennent à l'ancienne).</summary>
    public void SaveSettings(Guid clientId, string id, string? secret, string redirect)
    {
        var protectedSecret = string.IsNullOrWhiteSpace(secret) ? null : box.Protect(secret.Trim());
        var changed = store.Write(d =>
        {
            var t = d.Clients.First(c => c.Id == clientId);
            var diff = t.CanvaClientId != id.Trim() || protectedSecret != null;
            t.CanvaClientId = id.Trim();
            if (protectedSecret != null) t.CanvaClientSecret = protectedSecret;
            if (t.CanvaClientId.Length == 0) t.CanvaClientSecret = "";
            t.CanvaRedirectUri = redirect.Trim();
            return diff;
        });
        if (changed) Disconnect(clientId);
    }

    /// <summary>Adresse de retour proposée pour ce back-office (Canva refuse « localhost » : 127.0.0.1 à la place).</summary>
    public static string SuggestedRedirect(string baseUri)
    {
        var u = new Uri(baseUri);
        var host = u.Host == "localhost" ? "127.0.0.1" : u.Host;
        return $"{u.Scheme}://{host}{(u.IsDefaultPort ? "" : ":" + u.Port)}/canva/callback";
    }

    public record Connection(bool Connected, string User, DateTime? Since);
    public record Design(string Id, string Title, string? Thumbnail, int? Pages, DateTime? Updated, int? Width, int? Height);
    public record DesignPage(List<Design> Items, string? Continuation);
    public record ExportResult(List<MediaItem> Files, string? Error);

    public class CanvaException(string message) : Exception(message);

    // ---------- Connexion (OAuth 2, code d'autorisation + PKCE) ----------

    private record Pending(Guid ClientId, string Verifier, string ReturnUrl, string Redirect, DateTime Expires);
    private readonly ConcurrentDictionary<string, Pending> pending = new();

    /// <summary>Adresse d'autorisation Canva ; returnUrl : page du back-office où revenir (même domaine que celui de l'utilisateur).</summary>
    public string StartAuthorization(Guid clientId, string returnUrl)
    {
        foreach (var old in pending.Where(p => p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToList()) pending.TryRemove(old, out _);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var creds = CredsOf(clientId);
        pending[state] = new Pending(clientId, verifier, returnUrl, creds.Redirect, DateTime.UtcNow.AddMinutes(15));
        return AuthorizeUrl + "?" + string.Join("&", new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = creds.Id,
            ["redirect_uri"] = creds.Redirect,
            ["scope"] = Scopes,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state
        }.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
    }

    /// <summary>Retour de Canva : échange du code contre les jetons. Retourne l'adresse où renvoyer l'utilisateur (avec ?canva=ok ou une erreur).</summary>
    public async Task<string> CompleteAsync(string? state, string? code, string? error)
    {
        if (string.IsNullOrEmpty(state) || !pending.TryRemove(state, out var p) || p.Expires < DateTime.UtcNow) return "/integrations?canva=expired";
        var back = p.ReturnUrl + (p.ReturnUrl.Contains('?') ? "&" : "?");
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) return back + "canva=denied";
        try
        {
            var tokens = await TokenRequest(p.ClientId, new()
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["code_verifier"] = p.Verifier,
                ["redirect_uri"] = p.Redirect
            });
            Remember(p.ClientId, tokens);
            var user = "";
            try
            {
                using var doc = await Get(p.ClientId, "/users/me/profile");
                user = doc.RootElement.GetProperty("profile").GetProperty("display_name").GetString() ?? "";
            }
            catch (Exception ex) when (ex is CanvaException or KeyNotFoundException or HttpRequestException) { /* nom facultatif */ }
            store.Write(d =>
            {
                var t = d.Clients.First(c => c.Id == p.ClientId);
                t.CanvaUser = user;
                t.CanvaConnectedUtc = DateTime.UtcNow;
            });
            return back + "canva=ok";
        }
        catch (Exception ex) when (ex is CanvaException or HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(ex, "Connexion Canva refusée pour le client {Client}", p.ClientId);
            return back + "canva=error";
        }
    }

    public Connection ConnectionOf(Guid clientId) => store.Read(d =>
    {
        var t = d.Clients.FirstOrDefault(c => c.Id == clientId);
        return new Connection(t is { CanvaRefreshToken.Length: > 0 }, t?.CanvaUser ?? "", t?.CanvaConnectedUtc);
    });

    /// <summary>Oublie le compte Canva (les designs déjà exportés restent dans la médiathèque et sur les écrans).</summary>
    public void Disconnect(Guid clientId)
    {
        access.TryRemove(clientId, out _);
        store.Write(d =>
        {
            var t = d.Clients.First(c => c.Id == clientId);
            t.CanvaRefreshToken = ""; t.CanvaUser = ""; t.CanvaConnectedUtc = null;
        });
    }

    // ---------- Jetons : jeton d'accès en mémoire, jeton de renouvellement chiffré (à usage unique) ----------

    private record Access(string Token, DateTime Expires);
    private readonly ConcurrentDictionary<Guid, Access> access = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> refreshLocks = new();

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private void Remember(Guid clientId, TokenResponse t)
    {
        access[clientId] = new Access(t.AccessToken, DateTime.UtcNow.AddSeconds(Math.Max(60, t.ExpiresIn) - 60));
        var refresh = box.Protect(t.RefreshToken);
        store.Write(d => { d.Clients.First(c => c.Id == clientId).CanvaRefreshToken = refresh; });
    }

    private async Task<string> AccessToken(Guid clientId)
    {
        if (access.TryGetValue(clientId, out var a) && a.Expires > DateTime.UtcNow) return a.Token;
        var gate = refreshLocks.GetOrAdd(clientId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (access.TryGetValue(clientId, out a) && a.Expires > DateTime.UtcNow) return a.Token;   // renouvelé entre-temps
            var stored = store.Read(d => d.Clients.FirstOrDefault(c => c.Id == clientId)?.CanvaRefreshToken ?? "");
            if (stored.Length == 0) throw new CanvaException("Canva n'est pas connecté. Connectez-le dans Réglages › Applications.");
            TokenResponse t;
            try { t = await TokenRequest(clientId, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = box.Unprotect(stored) }); }
            catch (CanvaException)
            {
                Disconnect(clientId);   // accès retiré dans Canva, ou jeton déjà utilisé : il faut se reconnecter
                throw new CanvaException("La connexion à Canva a expiré. Reconnectez Canva dans Réglages › Applications.");
            }
            Remember(clientId, t);
            return t.AccessToken;
        }
        finally { gate.Release(); }
    }

    private async Task<TokenResponse> TokenRequest(Guid clientId, Dictionary<string, string> form)
    {
        var creds = CredsOf(clientId);
        if (creds.Id.Length == 0 || creds.Secret.Length == 0) throw new CanvaException("L'intégration Canva n'est pas configurée (Réglages › Applications › Canva).");
        using var req = new HttpRequestMessage(HttpMethod.Post, Api + "/oauth/token") { Content = new FormUrlEncodedContent(form) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{creds.Id}:{creds.Secret}")));
        using var res = await Http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new CanvaException($"Canva a refusé l'authentification ({(int)res.StatusCode}).");
        return JsonSerializer.Deserialize<TokenResponse>(body) ?? throw new CanvaException("Réponse de Canva illisible.");
    }

    // ---------- Appels à l'API ----------

    private HttpClient Http
    {
        get { var h = httpFactory.CreateClient("canva"); h.Timeout = TimeSpan.FromSeconds(30); return h; }
    }

    private async Task<JsonDocument> Send(Guid clientId, HttpMethod method, string path, object? body = null)
    {
        using var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken(clientId));
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if ((int)res.StatusCode == 401) { access.TryRemove(clientId, out _); throw new CanvaException("Canva a refusé l'accès. Reconnectez Canva dans Réglages › Applications."); }
        if ((int)res.StatusCode == 429) throw new CanvaException("Trop de demandes envoyées à Canva. Réessayez dans une minute.");
        if (!res.IsSuccessStatusCode) throw new CanvaException($"Canva a répondu {(int)res.StatusCode}{MessageOf(text)}.");
        return JsonDocument.Parse(text);
    }

    private Task<JsonDocument> Get(Guid clientId, string path) => Send(clientId, HttpMethod.Get, path);

    private static string MessageOf(string json)
    {
        try { using var d = JsonDocument.Parse(json); return d.RootElement.TryGetProperty("message", out var m) ? " : " + m.GetString() : ""; }
        catch (JsonException) { return ""; }
    }

    /// <summary>Designs du compte connecté (les siens et ceux partagés avec lui), les plus récents d'abord.</summary>
    public async Task<DesignPage> ListDesigns(Guid clientId, string? query, string? continuation)
    {
        var q = new List<string> { "limit=24", "sort_by=" + (string.IsNullOrWhiteSpace(query) ? "modified_descending" : "relevance") };
        if (!string.IsNullOrWhiteSpace(query)) q.Add("query=" + Uri.EscapeDataString(query.Trim()[..Math.Min(255, query.Trim().Length)]));
        if (!string.IsNullOrEmpty(continuation)) q.Add("continuation=" + Uri.EscapeDataString(continuation));
        using var doc = await Get(clientId, "/designs?" + string.Join("&", q));
        var items = new List<Design>();
        foreach (var e in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var thumb = e.TryGetProperty("thumbnail", out var t) ? t : default;
            items.Add(new Design(
                e.GetProperty("id").GetString() ?? "",
                e.TryGetProperty("title", out var ti) && ti.GetString() is { Length: > 0 } title ? title : "Design sans titre",
                thumb.ValueKind == JsonValueKind.Object && thumb.TryGetProperty("url", out var u) ? u.GetString() : null,
                e.TryGetProperty("page_count", out var pc) && pc.TryGetInt32(out var pages) ? pages : null,
                e.TryGetProperty("updated_at", out var up) && up.TryGetInt64(out var ts) ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime : null,
                thumb.ValueKind == JsonValueKind.Object && thumb.TryGetProperty("width", out var w) && w.TryGetInt32(out var wi) ? wi : null,
                thumb.ValueKind == JsonValueKind.Object && thumb.TryGetProperty("height", out var h) && h.TryGetInt32(out var hi) ? hi : null));
        }
        var next = doc.RootElement.TryGetProperty("continuation", out var c) ? c.GetString() : null;
        return new DesignPage(items, next);
    }

    /// <summary>
    /// Exporte un design (images : une PNG par page ; video : un MP4) et importe les fichiers dans la médiathèque du client.
    /// Les fichiers sont marqués comme venant de ce design (Info["canva"]) et nommés d'après lui.
    /// </summary>
    public async Task<ExportResult> Export(Guid clientId, Design design, string kind, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var portrait = design is { Width: > 0, Height: > 0 } && design.Height > design.Width;
            object format = kind == "video"
                ? new { type = "mp4", quality = portrait ? "vertical_1080p" : "horizontal_1080p" }
                : new { type = "png", width = portrait ? 1080 : 1920 };   // la hauteur suit les proportions du design
            progress?.Report("Export demandé à Canva…");
            string jobId;
            using (var doc = await Send(clientId, HttpMethod.Post, "/exports", new { design_id = design.Id, format }))
                jobId = doc.RootElement.GetProperty("job").GetProperty("id").GetString() ?? "";

            // Export asynchrone : on interroge Canva jusqu'au résultat (une vidéo peut prendre plusieurs minutes).
            List<string> urls = new();
            var deadline = DateTime.UtcNow.AddMinutes(kind == "video" ? 10 : 3);
            for (var i = 0; ; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(i == 0 ? 1000 : 2500, ct);
                using var doc = await Get(clientId, "/exports/" + Uri.EscapeDataString(jobId));
                var job = doc.RootElement.GetProperty("job");
                var status = job.GetProperty("status").GetString();
                if (status == "success") { urls = job.GetProperty("urls").EnumerateArray().Select(u => u.GetString() ?? "").Where(u => u.Length > 0).ToList(); break; }
                if (status == "failed")
                {
                    var code = job.TryGetProperty("error", out var err) && err.TryGetProperty("code", out var co) ? co.GetString() : null;
                    return new(new(), code switch
                    {
                        "license_required" => "Ce design contient des éléments payants : Canva refuse de l'exporter sans licence.",
                        "approval_required" => "Ce design doit être approuvé dans Canva avant de pouvoir être exporté.",
                        _ => "Canva n'a pas pu exporter ce design. Réessayez dans un instant."
                    });
                }
                if (DateTime.UtcNow > deadline) return new(new(), "L'export Canva prend trop de temps. Réessayez dans un instant.");
                progress?.Report(kind == "video" ? "Canva prépare la vidéo…" : "Canva prépare les pages…");
            }
            if (urls.Count == 0) return new(new(), "Canva n'a renvoyé aucun fichier pour ce design.");

            var files = new List<MediaItem>();
            for (var n = 0; n < urls.Count; n++)
            {
                progress?.Report(urls.Count > 1 ? $"Import dans la médiathèque : page {n + 1} / {urls.Count}…" : "Import dans la médiathèque…");
                var res = await importer.ImportUrl(urls[n], clientId, null, ct);
                if (res.Item is not { } item)
                {
                    DeleteFiles(clientId, files.Select(f => f.Id));   // export incomplet : rien n'est gardé
                    return new(new(), res.Error ?? "Import du fichier exporté impossible.");
                }
                var name = urls.Count > 1 ? $"{design.Title} — page {n + 1}" : design.Title;
                store.Write(d => { item.Name = name.Length > 120 ? name[..120] : name; item.Info["canva"] = design.Id; });
                files.Add(item);
            }
            return new(files, null);
        }
        catch (CanvaException ex) { return new(new(), ex.Message); }
        catch (HttpRequestException) { return new(new(), "Canva est injoignable. Vérifiez la connexion du serveur et réessayez."); }
        catch (KeyNotFoundException) { return new(new(), "Réponse de Canva inattendue."); }
    }

    /// <summary>Retire de la médiathèque des fichiers exportés depuis Canva (fichiers sur disque compris).</summary>
    public void DeleteFiles(Guid clientId, IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        if (set.Count == 0) return;
        var removed = store.Write(d =>
        {
            var list = d.Media.Where(m => m.ClientId == clientId && set.Contains(m.Id) && m.Info.ContainsKey("canva")).ToList();
            foreach (var m in list) d.Media.Remove(m);
            return list;
        });
        foreach (var f in removed.Where(m => m.FileName != null))
            try { File.Delete(Path.Combine(AppPaths.MediaDir, f.FileName!)); } catch (IOException) { }
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
