using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Linkii.Poc;

/// <summary>
/// Compte Microsoft (professionnel, scolaire ou personnel) d'une organisation (« Se connecter avec Microsoft », OAuth 2 + PKCE, permissions déléguées en lecture seule),
/// partagé par les calendriers Microsoft 365 et OneDrive / SharePoint. Même principe que <see cref="GoogleAuth"/> : chaque intégration
/// demande son accès (agendas, fichiers) ; les accès déjà accordés sont conservés.
/// L'application Entra ID est celle de la plateforme Linkii (multilocataire, comptes personnels acceptés) : Linkii:Microsoft:ClientId, Linkii:Microsoft:ClientSecret et,
/// en production, Linkii:Microsoft:RedirectUri (adresse déclarée dans Entra ; à défaut, l'adresse du back-office + /microsoft/callback).
/// </summary>
public class MicrosoftAuth(IConfiguration config, JsonStore store, SecretBox box, IHttpClientFactory httpFactory, ILogger<MicrosoftAuth> log)
{
    // « common » : comptes professionnels ou scolaires de n'importe quelle organisation, et comptes personnels (outlook.com, hotmail, live)
    private const string Authority = "https://login.microsoftonline.com/common/oauth2/v2.0";
    private const string GraphPrefix = "https://graph.microsoft.com/";
    public const string DriveScope = "Files.Read.All";
    public const string CalendarScope = "Calendars.Read";

    /// <summary>
    /// Accès demandés selon l'intégration qui lance la connexion (« drive » ou « calendar »). Tous en lecture seule, et tous disponibles
    /// pour les comptes personnels : ni Place.Read.All (annuaire des salles) ni Sites.Read.All (bibliothèques SharePoint) n'existent pour eux,
    /// et les demander ferait refuser la connexion. Files.Read.All couvre les fichiers SharePoint auxquels le compte a accès.
    /// </summary>
    public static string[] ScopesFor(string purpose) => purpose == "calendar"
        ? [CalendarScope, "Calendars.Read.Shared"]
        : [DriveScope];

    /// <summary>L'accès sans lequel l'intégration ne peut pas fonctionner.</summary>
    public static string RequiredScope(string purpose) => purpose == "calendar" ? CalendarScope : DriveScope;

    /// <summary>Le compte connecté a accordé cet accès.</summary>
    public static bool Has(Tenant t, string scope) => t.MsRefreshToken.Length > 0 && t.MsScopes.Split(' ').Contains(scope, StringComparer.OrdinalIgnoreCase);

    private string ClientId => config["Linkii:Microsoft:ClientId"] ?? "";
    private string ClientSecret => config["Linkii:Microsoft:ClientSecret"] ?? "";

    /// <summary>L'application Entra ID de la plateforme est configurée : le bouton « Se connecter avec Microsoft » est proposé.</summary>
    public bool IsConfigured => ClientId.Length > 0 && ClientSecret.Length > 0;

    public record Connection(bool Connected, string User, DateTime? Since);

    public static Connection ConnectionOf(Tenant t) => new(t.MsRefreshToken.Length > 0, t.MsUser, t.MsConnectedUtc);

    public class MicrosoftAuthException(string message, bool revoked = false) : Exception(message)
    {
        /// <summary>Microsoft refuse définitivement le jeton (accès retiré, mot de passe changé) : il faut se reconnecter.</summary>
        public bool Revoked { get; } = revoked;
    }

    // ---------- Connexion (code d'autorisation + PKCE) ----------

    private record Pending(Guid ClientId, string Purpose, string Verifier, string ReturnUrl, string Redirect, DateTime Expires);
    private readonly ConcurrentDictionary<string, Pending> pending = new();

    /// <summary>Adresse de consentement Microsoft. requestBase : adresse du back-office (adresse de retour par défaut, utile en local).</summary>
    public string StartAuthorization(Guid clientId, string purpose, string returnUrl, string requestBase)
    {
        foreach (var old in pending.Where(p => p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToList()) pending.TryRemove(old, out _);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var redirect = config["Linkii:Microsoft:RedirectUri"] is { Length: > 0 } r ? r : requestBase.TrimEnd('/') + "/microsoft/callback";
        pending[state] = new Pending(clientId, purpose, verifier, returnUrl, redirect, DateTime.UtcNow.AddMinutes(15));
        // User.Read + openid/profile : afficher quel compte est connecté ; offline_access : jeton de renouvellement (synchronisation sans l'utilisateur)
        var scope = string.Join(' ', ScopesFor(purpose).Select(s => GraphPrefix + s).Append(GraphPrefix + "User.Read").Concat(["openid", "profile", "offline_access"]));
        return Authority + "/authorize?" + string.Join("&", new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirect,
            ["response_mode"] = "query",
            ["scope"] = scope,
            ["prompt"] = "select_account",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state
        }.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
    }

    /// <summary>
    /// Retour de Microsoft : échange du code contre les jetons. Rend l'adresse où renvoyer l'utilisateur
    /// (?microsoft=ok&amp;for=… ; sinon denied, admin, scope, expired ou error).
    /// </summary>
    public async Task<string> CompleteAsync(string? state, string? code, string? error, string? errorDescription = null)
    {
        if (string.IsNullOrEmpty(state) || !pending.TryRemove(state, out var p) || p.Expires < DateTime.UtcNow) return "/integrations?microsoft=expired";
        var back = p.ReturnUrl + (p.ReturnUrl.Contains('?') ? "&" : "?") + "for=" + p.Purpose + "&microsoft=";
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) return back + (NeedsAdmin(error, errorDescription) ? "admin" : "denied");
        try
        {
            var t = await TokenRequest(new()
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["code_verifier"] = p.Verifier,
                ["redirect_uri"] = p.Redirect
            });
            var granted = GrantedScopes(t.Scope);
            if (!granted.Contains(RequiredScope(p.Purpose), StringComparer.OrdinalIgnoreCase)) return back + "scope";
            if (t.RefreshToken.Length == 0) return back + "error";
            access[p.ClientId] = new Access(t.AccessToken, DateTime.UtcNow.AddSeconds(Math.Max(60, t.ExpiresIn) - 60));
            var refresh = box.Protect(t.RefreshToken);
            var user = UserOf(t.IdToken);
            store.Write(d =>
            {
                var c = d.Clients.First(x => x.Id == p.ClientId);
                c.MsRefreshToken = refresh;
                // accès déjà accordés à l'autre intégration conservés : on cumule
                c.MsScopes = string.Join(' ', c.MsScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Concat(granted).Distinct(StringComparer.OrdinalIgnoreCase));
                if (user.Length > 0) c.MsUser = user;
                c.MsConnectedUtc = DateTime.UtcNow;
            });
            return back + "ok";
        }
        catch (Exception ex) when (ex is MicrosoftAuthException or HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "Connexion Microsoft refusée pour le client {Client}", p.ClientId);
            return back + (ex is MicrosoftAuthException { Message: var m } && m.Contains("AADSTS65001") ? "admin" : "error");
        }
    }

    /// <summary>L'organisation interdit le consentement des utilisateurs : son administrateur doit approuver l'application (AADSTS65001, 90094, 90008…).</summary>
    public static bool NeedsAdmin(string? error, string? description) =>
        error == "consent_required" || error == "interaction_required"
        || description is { Length: > 0 } d && (d.Contains("AADSTS65001") || d.Contains("AADSTS90094") || d.Contains("AADSTS90008") || d.Contains("AADSTS650051")
                                                  || d.Contains("admin", StringComparison.OrdinalIgnoreCase) && d.Contains("consent", StringComparison.OrdinalIgnoreCase));

    /// <summary>Oublie le compte Microsoft : les calendriers Microsoft 365 et OneDrive / SharePoint s'arrêtent tous les deux.</summary>
    public void Disconnect(Guid clientId)
    {
        // Microsoft n'a pas d'adresse de révocation pour les jetons d'un utilisateur : l'accès se retire dans « Mes applications » du compte.
        access.TryRemove(clientId, out _);
        store.Write(d =>
        {
            var c = d.Clients.First(x => x.Id == clientId);
            c.MsRefreshToken = ""; c.MsScopes = ""; c.MsUser = ""; c.MsConnectedUtc = null;
        });
    }

    // ---------- Jetons : jeton d'accès en mémoire, jeton de renouvellement chiffré (et renouvelé à chaque emploi) ----------

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

    /// <summary>Jeton d'accès Microsoft Graph de l'organisation, renouvelé au besoin. Lève une exception si le compte n'est plus connecté.</summary>
    public async Task<string> AccessToken(Tenant t)
    {
        if (access.TryGetValue(t.Id, out var a) && a.Expires > DateTime.UtcNow) return a.Token;
        var gate = refreshLocks.GetOrAdd(t.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (access.TryGetValue(t.Id, out a) && a.Expires > DateTime.UtcNow) return a.Token;   // renouvelé entre-temps
            var (stored, scopes) = store.Read(d => d.Clients.FirstOrDefault(c => c.Id == t.Id) is { } c ? (c.MsRefreshToken, c.MsScopes) : ("", ""));
            if (stored.Length == 0) throw new MicrosoftAuthException("Aucun compte Microsoft connecté : connectez-le dans Intégrations (Microsoft 365 ou OneDrive et SharePoint).");
            TokenResponse r;
            try
            {
                r = await TokenRequest(new()
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = box.Unprotect(stored),
                    ["scope"] = string.Join(' ', scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(s => s.Contains('.') && !s.Contains("://"))
                        .Select(s => GraphPrefix + s).Append("offline_access"))
                });
            }
            catch (MicrosoftAuthException ex) when (ex.Revoked)
            {
                Disconnect(t.Id);   // accès retiré, mot de passe changé ou session révoquée : il faut se reconnecter
                throw new MicrosoftAuthException("La connexion au compte Microsoft a expiré. Reconnectez-le dans Intégrations (Microsoft 365 ou OneDrive et SharePoint).");
            }
            access[t.Id] = new Access(r.AccessToken, DateTime.UtcNow.AddSeconds(Math.Max(60, r.ExpiresIn) - 60));
            if (r.RefreshToken.Length > 0)   // Microsoft fait tourner le jeton de renouvellement : le nouveau remplace l'ancien
            {
                var next = box.Protect(r.RefreshToken);
                store.Write(d => { if (d.Clients.FirstOrDefault(c => c.Id == t.Id) is { MsRefreshToken.Length: > 0 } c) c.MsRefreshToken = next; });
            }
            return r.AccessToken;
        }
        finally { gate.Release(); }
    }

    private async Task<TokenResponse> TokenRequest(Dictionary<string, string> form)
    {
        if (!IsConfigured) throw new MicrosoftAuthException("La connexion Microsoft de la plateforme n'est pas configurée (administrateur Linkii).");
        form["client_id"] = ClientId;
        form["client_secret"] = ClientSecret;
        var http = httpFactory.CreateClient("microsoft");
        http.Timeout = TimeSpan.FromSeconds(20);
        using var res = await http.PostAsync(Authority + "/token", new FormUrlEncodedContent(form));
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            // invalid_grant : jeton révoqué ou expiré ; invalid_client : secret de la plateforme périmé (pas la faute de l'utilisateur)
            var revoked = body.Contains("invalid_grant") && (int)res.StatusCode is 400 or 401;
            throw new MicrosoftAuthException($"Microsoft a refusé l'authentification ({(int)res.StatusCode}) : {GraphService.Describe(body)}", revoked);
        }
        return JsonSerializer.Deserialize<TokenResponse>(body) ?? throw new MicrosoftAuthException("Réponse de Microsoft illisible.");
    }

    /// <summary>Accès Graph accordés d'après la réponse de Microsoft (adresses complètes ramenées au nom court, openid et profile écartés).</summary>
    private static string[] GrantedScopes(string scope) => scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.StartsWith(GraphPrefix, StringComparison.OrdinalIgnoreCase) ? s[GraphPrefix.Length..] : s)
        .Where(s => s.Contains('.'))
        .ToArray();

    /// <summary>Adresse du compte d'après le jeton d'identité (reçu directement de Microsoft, en HTTPS : pas de signature à vérifier ici).</summary>
    private static string UserOf(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2) return "";
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/').PadRight((parts[1].Length + 3) / 4 * 4, '=')));
            using var doc = JsonDocument.Parse(json);
            foreach (var claim in new[] { "preferred_username", "email", "upn" })
                if (doc.RootElement.TryGetProperty(claim, out var e) && e.GetString() is { Length: > 0 } v) return v;
            return "";
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return ""; }
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
