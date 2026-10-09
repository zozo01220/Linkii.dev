using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Linkii.Poc;

/// <summary>
/// « Continuer avec Microsoft / Google » sur la page de connexion : création d'un espace ou connexion, identité seulement (openid, e-mail, nom),
/// aucun accès aux fichiers ni aux agendas (ceux-ci restent demandés dans Intégrations).
/// Réutilise les applications OAuth de la plateforme et leurs adresses de retour (/google/callback, /microsoft/callback) : aucune déclaration
/// supplémentaire chez Google ni dans Entra. Le retour peut arriver sur le domaine principal : l'identité est confiée à un ticket à usage unique,
/// repris sur le domaine du revendeur d'origine (/login/sso/finish), où le cookie de session est posé.
/// </summary>
public class ExternalLogin(IConfiguration config, IHttpClientFactory httpFactory, ILogger<ExternalLogin> log)
{
    public const string Login = "login", SignupMode = "signup";

    private record Provider(string Id, string AuthorizeUrl, string TokenUrl, string Scope, string ConfigKey, string Callback);

    private static readonly Provider[] Providers =
    {
        new(SignupSources.Google, "https://accounts.google.com/o/oauth2/v2/auth", "https://oauth2.googleapis.com/token", "openid email profile", "Google", "/google/callback"),
        new(SignupSources.Microsoft, "https://login.microsoftonline.com/common/oauth2/v2.0/authorize", "https://login.microsoftonline.com/common/oauth2/v2.0/token", "openid email profile", "Microsoft", "/microsoft/callback"),
    };

    private static Provider? Find(string? id) => Providers.FirstOrDefault(p => p.Id == id);
    private string ClientId(Provider p) => config[$"Linkii:{p.ConfigKey}:ClientId"] ?? "";
    private string ClientSecret(Provider p) => config[$"Linkii:{p.ConfigKey}:ClientSecret"] ?? "";

    /// <summary>L'application OAuth du fournisseur est configurée : son bouton est proposé sur la page de connexion.</summary>
    public bool IsConfigured(string provider) => Find(provider) is { } p && ClientId(p).Length > 0 && ClientSecret(p).Length > 0;

    private record Pending(string Provider, string Mode, string Verifier, string Origin, string Redirect, DateTime Expires);
    private readonly ConcurrentDictionary<string, Pending> pending = new();

    /// <summary>Identité reçue, en attente d'être reprise sur le domaine d'origine (connexion) ou complétée (nom de l'organisation, consentements).</summary>
    public record Ticket(ExternalIdentity Identity, string Mode, DateTime Expires);
    private readonly ConcurrentDictionary<string, Ticket> tickets = new();

    /// <summary>Adresse de consentement du fournisseur. origin : adresse du back-office du revendeur (schéma + domaine).</summary>
    public string? Start(string provider, string mode, string origin)
    {
        var p = Find(provider);
        if (p == null || !IsConfigured(provider)) return null;
        Sweep();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var redirect = config[$"Linkii:{p.ConfigKey}:RedirectUri"] is { Length: > 0 } r ? r : origin.TrimEnd('/') + p.Callback;
        pending[state] = new Pending(p.Id, mode == SignupMode ? SignupMode : Login, verifier, origin.TrimEnd('/'), redirect, DateTime.UtcNow.AddMinutes(15));
        return p.AuthorizeUrl + "?" + string.Join("&", new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId(p),
            ["redirect_uri"] = redirect,
            ["scope"] = p.Scope,
            ["prompt"] = "select_account",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
            ["state"] = state
        }.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
    }

    /// <summary>Ce retour (state) appartient à une connexion de la page de connexion, pas à une intégration.</summary>
    public bool Owns(string? state) => !string.IsNullOrEmpty(state) && pending.ContainsKey(state);

    /// <summary>Retour du fournisseur : rend l'adresse où renvoyer le navigateur (ticket sur le domaine d'origine, ou page de connexion avec l'erreur).</summary>
    public async Task<string> CompleteAsync(string? state, string? code, string? error)
    {
        if (string.IsNullOrEmpty(state) || !pending.TryRemove(state, out var pd) || pd.Expires < DateTime.UtcNow) return "/login?sso=expired";
        var back = pd.Origin + "/login" + (pd.Mode == SignupMode ? "?signup=1&sso=" : "?sso=");
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) return back + "denied";
        var p = Find(pd.Provider)!;
        try
        {
            using var res = await Http.PostAsync(p.TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["code_verifier"] = pd.Verifier,
                ["redirect_uri"] = pd.Redirect,
                ["client_id"] = ClientId(p),
                ["client_secret"] = ClientSecret(p),
                ["scope"] = p.Scope
            }));
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) { log.LogWarning("Connexion {Provider} refusée ({Status}) : {Body}", p.Id, (int)res.StatusCode, body); return back + "error"; }
            using var doc = JsonDocument.Parse(body);
            var id = Identity(p.Id, doc.RootElement.TryGetProperty("id_token", out var t) ? t.GetString() ?? "" : "");
            if (id == null) return back + "noemail";
            var ticket = Base64Url(RandomNumberGenerator.GetBytes(32));
            tickets[ticket] = new Ticket(id, pd.Mode, DateTime.UtcNow.AddMinutes(20));
            return pd.Origin + "/login/sso/finish?t=" + Uri.EscapeDataString(ticket);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "Connexion {Provider} impossible", p.Id);
            return back + "error";
        }
    }

    public Ticket? Peek(string? ticket) => ticket != null && tickets.TryGetValue(ticket, out var t) && t.Expires > DateTime.UtcNow ? t : null;
    public void Consume(string? ticket) { if (ticket != null) tickets.TryRemove(ticket, out _); }

    /// <summary>
    /// Identité du jeton d'identité (reçu directement du fournisseur, en HTTPS : pas de signature à vérifier ici). Google : l'adresse doit être
    /// vérifiée (email_verified). Microsoft : e-mail du compte, à défaut son identifiant de connexion. null sans adresse utilisable.
    /// </summary>
    public static ExternalIdentity? Identity(string provider, string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/').PadRight((parts[1].Length + 3) / 4 * 4, '=')));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string S(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var sub = S("sub");
            var email = S("email");
            if (provider == SignupSources.Google && !(root.TryGetProperty("email_verified", out var ev) && (ev.ValueKind == JsonValueKind.True || ev.ValueKind == JsonValueKind.String && ev.GetString() == "true")))
                return null;
            if (provider == SignupSources.Microsoft && email.Length == 0) email = S("preferred_username");
            email = email.Trim().ToLowerInvariant();
            if (sub.Length == 0 || !System.Net.Mail.MailAddress.TryCreate(email, out _)) return null;
            return new ExternalIdentity(provider, sub, email, S("name"));
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }

    private void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var k in pending.Where(x => x.Value.Expires < now).Select(x => x.Key).ToList()) pending.TryRemove(k, out _);
        foreach (var k in tickets.Where(x => x.Value.Expires < now).Select(x => x.Key).ToList()) tickets.TryRemove(k, out _);
    }

    private HttpClient Http { get { var h = httpFactory.CreateClient("sso"); h.Timeout = TimeSpan.FromSeconds(20); return h; } }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
