using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Linkii.Poc;

/// <summary>
/// Compte Dropbox d'une organisation (« Se connecter avec Dropbox », OAuth 2 + PKCE, lecture seule). L'application OAuth est celle de la plateforme
/// Linkii : Linkii:Dropbox:AppKey, Linkii:Dropbox:AppSecret et, en production, Linkii:Dropbox:RedirectUri (adresse de retour déclarée dans la console
/// Dropbox ; à défaut, l'adresse du back-office + /dropbox/callback). Permissions de l'application : files.metadata.read, files.content.read, account_info.read.
/// Le jeton de renouvellement est chiffré dans <see cref="DriveAccount.Secret"/> ; le jeton d'accès reste en mémoire.
/// </summary>
public class DropboxAuth(IConfiguration config, JsonStore store, SecretBox box, SafeHttp http, ILogger<DropboxAuth> log)
{
    private const string AuthorizeUrl = "https://www.dropbox.com/oauth2/authorize";
    private const string TokenUrl = "https://api.dropbox.com/oauth2/token";
    private const string RevokeUrl = "https://api.dropboxapi.com/2/auth/token/revoke";
    private const string AccountUrl = "https://api.dropboxapi.com/2/users/get_current_account";
    public const string Scopes = "files.metadata.read files.content.read account_info.read";

    private string AppKey => config["Linkii:Dropbox:AppKey"] ?? "";
    private string AppSecret => config["Linkii:Dropbox:AppSecret"] ?? "";

    /// <summary>L'application Dropbox de la plateforme est configurée : « Se connecter avec Dropbox » est proposé.</summary>
    public bool IsConfigured => AppKey.Length > 0 && AppSecret.Length > 0;

    /// <summary>Un compte Dropbox est connecté (jeton de renouvellement présent).</summary>
    public static bool Has(Tenant t) => DriveSources.Account(t, "dropbox") is { Secret.Length: > 0 };

    public class DropboxAuthException(string message) : Exception(message);

    // ---------- Connexion (code d'autorisation + PKCE) ----------

    private record Pending(Guid ClientId, string Verifier, string ReturnUrl, string Redirect, DateTime Expires);
    private readonly ConcurrentDictionary<string, Pending> pending = new();

    /// <summary>Adresse de consentement Dropbox. requestBase : adresse du back-office (adresse de retour par défaut, utile en local).</summary>
    public string StartAuthorization(Guid clientId, string returnUrl, string requestBase)
    {
        foreach (var old in pending.Where(p => p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToList()) pending.TryRemove(old, out _);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var redirect = config["Linkii:Dropbox:RedirectUri"] is { Length: > 0 } r ? r : requestBase.TrimEnd('/') + "/dropbox/callback";
        pending[state] = new Pending(clientId, verifier, returnUrl, redirect, DateTime.UtcNow.AddMinutes(15));
        return AuthorizeUrl + "?" + string.Join("&", new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = AppKey,
            ["redirect_uri"] = redirect,
            ["token_access_type"] = "offline",   // jeton de renouvellement : synchronisation sans l'utilisateur
            ["scope"] = Scopes,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state
        }.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
    }

    /// <summary>Retour de Dropbox : échange du code contre les jetons. Rend l'adresse où renvoyer l'utilisateur (?dropbox=ok ou une erreur).</summary>
    public async Task<string> CompleteAsync(string? state, string? code, string? error)
    {
        if (string.IsNullOrEmpty(state) || !pending.TryRemove(state, out var p) || p.Expires < DateTime.UtcNow) return "/integrations?dropbox=expired";
        var back = p.ReturnUrl + (p.ReturnUrl.Contains('?') ? "&" : "?") + "dropbox=";
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
            if (t.RefreshToken.Length == 0) return back + "error";
            if (!t.Scope.Contains("files.content.read")) return back + "scope";   // permissions de l'application incomplètes côté Dropbox
            access[p.ClientId] = new Access(t.AccessToken, DateTime.UtcNow.AddSeconds(Math.Max(60, t.ExpiresIn) - 60));
            var email = await EmailOf(t.AccessToken);
            var refresh = box.Protect(t.RefreshToken);
            store.Write(d =>
            {
                var c = d.Clients.First(x => x.Id == p.ClientId);
                var a = c.DriveAccounts.FirstOrDefault(x => x.Source == "dropbox");
                if (a == null) c.DriveAccounts.Add(a = new DriveAccount { Source = "dropbox" });
                a.Secret = refresh; a.User = email; a.ConnectedUtc = DateTime.UtcNow; a.LostUtc = null; a.LastError = "";
            });
            return back + "ok";
        }
        catch (Exception ex) when (ex is DropboxAuthException or HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "Connexion Dropbox refusée pour le client {Client}", p.ClientId);
            return back + "error";
        }
    }

    /// <summary>Oublie le compte Dropbox (et retire l'accès chez Dropbox) : la source s'arrête.</summary>
    public async Task Disconnect(Guid clientId)
    {
        string? token = null;
        try { token = await AccessToken(store.Read(d => d.Clients.First(x => x.Id == clientId))); }
        catch (Exception ex) when (ex is InvalidOperationException or DropboxAuthException or HttpRequestException or TaskCanceledException) { /* déjà expiré : rien à révoquer */ }
        access.TryRemove(clientId, out _);
        store.Write(d => d.Clients.First(x => x.Id == clientId).DriveAccounts.RemoveAll(x => x.Source == "dropbox"));
        if (token != null) await Revoke(token);
    }

    /// <summary>Dropbox refuse le jeton : le compte reste affiché (adresse) comme « à reconnecter » dans Comptes connectés.</summary>
    private void Expire(Guid clientId)
    {
        access.TryRemove(clientId, out _);
        store.Write(d =>
        {
            if (d.Clients.First(x => x.Id == clientId).DriveAccounts.FirstOrDefault(x => x.Source == "dropbox") is { } a) { a.Secret = ""; a.LostUtc = DateTime.UtcNow; }
        });
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
    }

    /// <summary>Jeton d'accès Dropbox de l'organisation, renouvelé au besoin. Lève une exception si le compte n'est plus connecté.</summary>
    public async Task<string> AccessToken(Tenant t)
    {
        if (access.TryGetValue(t.Id, out var a) && a.Expires > DateTime.UtcNow) return a.Token;
        var gate = refreshLocks.GetOrAdd(t.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (access.TryGetValue(t.Id, out a) && a.Expires > DateTime.UtcNow) return a.Token;   // renouvelé entre-temps
            var stored = store.Read(d => d.Clients.FirstOrDefault(c => c.Id == t.Id)?.DriveAccounts.FirstOrDefault(x => x.Source == "dropbox")?.Secret ?? "");
            if (stored.Length == 0) throw new InvalidOperationException("Aucun compte Dropbox connecté : connectez-le dans Intégrations › Comptes connectés.");
            TokenResponse r;
            try { r = await TokenRequest(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = box.Unprotect(stored) }); }
            catch (DropboxAuthException)
            {
                Expire(t.Id);   // accès retiré dans le compte Dropbox : il faut se reconnecter
                throw new InvalidOperationException("La connexion au compte Dropbox a expiré. Reconnectez-le dans Intégrations › Comptes connectés.");
            }
            access[t.Id] = new Access(r.AccessToken, DateTime.UtcNow.AddSeconds(Math.Max(60, r.ExpiresIn) - 60));
            return r.AccessToken;
        }
        finally { gate.Release(); }
    }

    private async Task<TokenResponse> TokenRequest(Dictionary<string, string> form)
    {
        if (!IsConfigured) throw new DropboxAuthException("La connexion Dropbox de la plateforme n'est pas configurée (administrateur Linkii).");
        form["client_id"] = AppKey;
        form["client_secret"] = AppSecret;
        var body = string.Join("&", form.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
        var (status, text) = await http.Request(HttpMethod.Post, TokenUrl, body, "application/x-www-form-urlencoded");
        if (status is >= 400 and < 500) throw new DropboxAuthException($"Dropbox a refusé l'authentification ({status}).");
        if (status != 200) throw new HttpRequestException($"Dropbox ({status})");
        return JsonSerializer.Deserialize<TokenResponse>(text) ?? throw new DropboxAuthException("Réponse de Dropbox illisible.");
    }

    private async Task<string> EmailOf(string accessToken)
    {
        try
        {
            var (status, text) = await http.Request(HttpMethod.Post, AccountUrl, headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + accessToken });
            if (status != 200) return "";
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return ""; }
    }

    private async Task Revoke(string token)
    {
        try { await http.Request(HttpMethod.Post, RevokeUrl, headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* au pire, l'accès reste visible dans le compte Dropbox */ }
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
