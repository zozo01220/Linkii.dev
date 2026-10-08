using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Linkii.Poc;

/// <summary>
/// Compte Google d'une organisation (« Se connecter avec Google », OAuth 2 + PKCE, lecture seule), partagé par Google Calendar et Google Drive.
/// Chaque intégration demande son accès (agendas, fichiers Drive) ; les accès déjà accordés sont conservés (autorisation incrémentale).
/// L'application OAuth est celle de la plateforme Linkii : Linkii:Google:ClientId, Linkii:Google:ClientSecret et, en production,
/// Linkii:Google:RedirectUri (adresse de retour déclarée dans Google Cloud ; à défaut, l'adresse du back-office + /google/callback).
/// </summary>
public class GoogleAuth(IConfiguration config, JsonStore store, SecretBox box, IHttpClientFactory httpFactory, ILogger<GoogleAuth> log)
{
    private const string AuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
    public const string DriveScope = "https://www.googleapis.com/auth/drive.readonly";
    public const string CalendarScope = "https://www.googleapis.com/auth/calendar.readonly";

    /// <summary>Accès demandé selon l'intégration qui lance la connexion (« drive » ou « calendar »).</summary>
    public static string ScopeFor(string purpose) => purpose == "calendar" ? CalendarScope : DriveScope;

    /// <summary>Le compte connecté a accordé cet accès.</summary>
    public static bool Has(Tenant t, string scope) => t.GoogleRefreshToken.Length > 0 && t.GoogleScopes.Split(' ').Contains(scope);

    private string ClientId => config["Linkii:Google:ClientId"] ?? "";
    private string ClientSecret => config["Linkii:Google:ClientSecret"] ?? "";

    /// <summary>L'application OAuth de la plateforme est configurée : le bouton « Se connecter avec Google » est proposé.</summary>
    public bool IsConfigured => ClientId.Length > 0 && ClientSecret.Length > 0;

    public record Connection(bool Connected, string User, DateTime? Since);

    public static Connection ConnectionOf(Tenant t) => new(t.GoogleRefreshToken.Length > 0, t.GoogleUser, t.GoogleConnectedUtc);

    public class GoogleAuthException(string message) : Exception(message);

    // ---------- Connexion (code d'autorisation + PKCE) ----------

    private record Pending(Guid ClientId, string Purpose, string Verifier, string ReturnUrl, string Redirect, DateTime Expires);
    private readonly ConcurrentDictionary<string, Pending> pending = new();

    /// <summary>Adresse de consentement Google. requestBase : adresse du back-office (adresse de retour par défaut, utile en local).</summary>
    public string StartAuthorization(Guid clientId, string purpose, string returnUrl, string requestBase)
    {
        foreach (var old in pending.Where(p => p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToList()) pending.TryRemove(old, out _);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var redirect = config["Linkii:Google:RedirectUri"] is { Length: > 0 } r ? r : requestBase.TrimEnd('/') + "/google/callback";
        pending[state] = new Pending(clientId, purpose, verifier, returnUrl, redirect, DateTime.UtcNow.AddMinutes(15));
        return AuthorizeUrl + "?" + string.Join("&", new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirect,
            ["scope"] = ScopeFor(purpose) + " openid email",   // email : afficher quel compte est connecté
            ["access_type"] = "offline",                       // jeton de renouvellement : synchronisation sans l'utilisateur
            ["prompt"] = "consent select_account",
            ["include_granted_scopes"] = "true",               // l'accès déjà accordé à l'autre intégration est conservé
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state
        }.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
    }

    /// <summary>Retour de Google : échange du code contre les jetons. Rend l'adresse où renvoyer l'utilisateur (?google=ok&amp;for=… ou une erreur).</summary>
    public async Task<string> CompleteAsync(string? state, string? code, string? error)
    {
        if (string.IsNullOrEmpty(state) || !pending.TryRemove(state, out var p) || p.Expires < DateTime.UtcNow) return "/integrations?google=expired";
        var back = p.ReturnUrl + (p.ReturnUrl.Contains('?') ? "&" : "?") + "for=" + p.Purpose + "&google=";
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) return back + "denied";
        try
        {
            var t = await TokenRequest(new()
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["code_verifier"] = p.Verifier,
                ["redirect_uri"] = p.Redirect
            });
            // consentement granulaire : l'utilisateur peut décocher l'accès demandé
            if (!t.Scope.Split(' ').Contains(ScopeFor(p.Purpose))) return back + "scope";
            if (t.RefreshToken.Length == 0) return back + "error";
            access[p.ClientId] = new Access(t.AccessToken, DateTime.UtcNow.AddSeconds(Math.Max(60, t.ExpiresIn) - 60));
            var refresh = box.Protect(t.RefreshToken);
            var email = EmailOf(t.IdToken);
            store.Write(d =>
            {
                var c = d.Clients.First(x => x.Id == p.ClientId);
                c.GoogleRefreshToken = refresh;
                c.GoogleScopes = t.Scope;
                c.GoogleUser = email;
                c.GoogleConnectedUtc = DateTime.UtcNow;
            });
            return back + "ok";
        }
        catch (Exception ex) when (ex is GoogleAuthException or HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "Connexion Google refusée pour le client {Client}", p.ClientId);
            return back + "error";
        }
    }

    /// <summary>Oublie le compte Google (et retire l'accès chez Google) : Google Calendar et Google Drive s'arrêtent tous les deux.</summary>
    public async Task Disconnect(Guid clientId)
    {
        access.TryRemove(clientId, out _);
        var stored = store.Write(d =>
        {
            var c = d.Clients.First(x => x.Id == clientId);
            var s = c.GoogleRefreshToken;
            c.GoogleRefreshToken = ""; c.GoogleScopes = ""; c.GoogleUser = ""; c.GoogleConnectedUtc = null;
            return s;
        });
        if (stored.Length > 0) await Revoke(box.Unprotect(stored));
    }

    // ---------- Jetons : jeton d'accès en mémoire, jeton de renouvellement chiffré ----------

    private record Access(string Token, DateTime Expires);
    private readonly ConcurrentDictionary<Guid, Access> access = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> refreshLocks = new();

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("scope")] public string Scope { get; set; } = "";
        [JsonPropertyName("id_token")] public string IdToken { get; set; } = "";
    }

    /// <summary>Jeton d'accès Google de l'organisation, renouvelé au besoin. Lève une exception si le compte n'est plus connecté.</summary>
    public async Task<string> AccessToken(Tenant t)
    {
        if (access.TryGetValue(t.Id, out var a) && a.Expires > DateTime.UtcNow) return a.Token;
        var gate = refreshLocks.GetOrAdd(t.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (access.TryGetValue(t.Id, out a) && a.Expires > DateTime.UtcNow) return a.Token;   // renouvelé entre-temps
            var stored = store.Read(d => d.Clients.FirstOrDefault(c => c.Id == t.Id)?.GoogleRefreshToken ?? "");
            if (stored.Length == 0) throw new GoogleAuthException("Aucun compte Google connecté : connectez-le dans Intégrations (Google Calendar ou Google Drive).");
            TokenResponse r;
            try { r = await TokenRequest(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = box.Unprotect(stored) }); }
            catch (GoogleAuthException)
            {
                await Disconnect(t.Id);   // accès retiré dans le compte Google, ou mot de passe changé : il faut se reconnecter
                throw new GoogleAuthException("La connexion au compte Google a expiré. Reconnectez-le dans Intégrations (Google Calendar ou Google Drive).");
            }
            access[t.Id] = new Access(r.AccessToken, DateTime.UtcNow.AddSeconds(Math.Max(60, r.ExpiresIn) - 60));
            return r.AccessToken;
        }
        finally { gate.Release(); }
    }

    private async Task<TokenResponse> TokenRequest(Dictionary<string, string> form)
    {
        if (!IsConfigured) throw new GoogleAuthException("La connexion Google de la plateforme n'est pas configurée (administrateur Linkii).");
        form["client_id"] = ClientId;
        form["client_secret"] = ClientSecret;
        using var res = await Http.PostAsync(TokenUrl, new FormUrlEncodedContent(form));
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new GoogleAuthException($"Google a refusé l'authentification ({(int)res.StatusCode}) : {GoogleConnector.Describe(body)}");
        return JsonSerializer.Deserialize<TokenResponse>(body) ?? throw new GoogleAuthException("Réponse de Google illisible.");
    }

    private async Task Revoke(string token)
    {
        try { using var _ = await Http.PostAsync(RevokeUrl, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token })); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* au pire, l'accès reste visible dans le compte Google */ }
    }

    private HttpClient Http
    {
        get { var h = httpFactory.CreateClient("google"); h.Timeout = TimeSpan.FromSeconds(20); return h; }
    }

    /// <summary>Adresse e-mail du jeton d'identité (reçu directement de Google, en HTTPS : pas de signature à vérifier ici).</summary>
    private static string EmailOf(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2) return "";
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/').PadRight((parts[1].Length + 3) / 4 * 4, '=')));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return ""; }
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
