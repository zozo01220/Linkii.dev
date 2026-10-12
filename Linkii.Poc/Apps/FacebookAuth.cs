using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>
/// Compte Facebook d'une organisation (« Se connecter avec Facebook », OAuth 2, lecture seule). L'application est celle de la plateforme Linkii :
/// Linkii:Facebook:AppId, Linkii:Facebook:AppSecret et, en production, Linkii:Facebook:RedirectUri (adresse de retour déclarée dans la console Meta ;
/// à défaut, l'adresse du back-office + /facebook/callback). Permissions : pages_show_list et pages_read_engagement (en mode développement de l'application
/// Meta, seuls ses administrateurs et testeurs peuvent se connecter ; ouvrir l'application à tous demande une revue Meta).
/// À la connexion, le jeton de l'utilisateur est échangé contre un jeton de longue durée, qui sert à lire les Pages administrées et leurs jetons de Page
/// (sans expiration). Seuls ces jetons de Page sont gardés (chiffrés dans <see cref="Tenant.FacebookPages"/>) ; le jeton de l'utilisateur est oublié.
/// </summary>
public class FacebookAuth(IConfiguration config, JsonStore store, SecretBox box, SafeHttp http, ILogger<FacebookAuth> log)
{
    private const string GraphVersion = "v21.0";
    private const string AuthorizeUrl = "https://www.facebook.com/" + GraphVersion + "/dialog/oauth";
    private const string GraphUrl = "https://graph.facebook.com/" + GraphVersion + "/";
    public const string Scopes = "pages_show_list,pages_read_engagement";

    private string AppId => config["Linkii:Facebook:AppId"] ?? "";
    private string AppSecret => config["Linkii:Facebook:AppSecret"] ?? "";

    /// <summary>L'application Facebook de la plateforme est configurée : « Se connecter avec Facebook » est proposé.</summary>
    public bool IsConfigured => AppId.Length > 0 && AppSecret.Length > 0;

    /// <summary>Un compte Facebook est connecté (et non perdu).</summary>
    public static bool Has(Tenant t) => t.FacebookConnectedUtc != null && t.FacebookLostUtc == null;

    public class FacebookAuthException(string message) : Exception(message);

    // ---------- Connexion (code d'autorisation) ----------

    private record Pending(Guid ClientId, string ReturnUrl, string Redirect, DateTime Expires);
    private readonly ConcurrentDictionary<string, Pending> pending = new();

    /// <summary>Adresse de consentement Facebook. requestBase : adresse du back-office (adresse de retour par défaut, utile en local).</summary>
    public string StartAuthorization(Guid clientId, string returnUrl, string requestBase)
    {
        foreach (var old in pending.Where(p => p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToList()) pending.TryRemove(old, out _);
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var redirect = config["Linkii:Facebook:RedirectUri"] is { Length: > 0 } r ? r : requestBase.TrimEnd('/') + "/facebook/callback";
        pending[state] = new Pending(clientId, returnUrl, redirect, DateTime.UtcNow.AddMinutes(15));
        return AuthorizeUrl + "?" + string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = AppId,
            ["redirect_uri"] = redirect,
            ["state"] = state,
            ["scope"] = Scopes,
            ["response_type"] = "code"
        }.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
    }

    /// <summary>Retour de Facebook : échange du code, lecture des Pages administrées. Rend l'adresse où renvoyer l'utilisateur (?facebook=ok ou une erreur).</summary>
    public async Task<string> CompleteAsync(string? state, string? code, string? error)
    {
        if (string.IsNullOrEmpty(state) || !pending.TryRemove(state, out var p) || p.Expires < DateTime.UtcNow) return "/integrations?facebook=expired";
        var back = p.ReturnUrl + (p.ReturnUrl.Contains('?') ? "&" : "?") + "facebook=";
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) return back + "denied";
        try
        {
            var shortLived = await TokenRequest(new() { ["code"] = code, ["redirect_uri"] = p.Redirect });
            var longLived = await TokenRequest(new() { ["grant_type"] = "fb_exchange_token", ["fb_exchange_token"] = shortLived });   // les jetons de Page en découlent sans expiration
            var user = await NameOf(longLived);
            var pages = ParsePages(await Graph("me/accounts?fields=" + Uri.EscapeDataString("id,name,access_token") + "&limit=100", longLived));
            var sealedPages = pages.Select(x => new FacebookPageAccess { Id = x.Id, Name = x.Name, Token = box.Protect(x.Token) }).ToList();
            store.Write(d =>
            {
                var c = d.Clients.First(x => x.Id == p.ClientId);
                c.FacebookUser = user; c.FacebookConnectedUtc = DateTime.UtcNow; c.FacebookLostUtc = null; c.FacebookPages = sealedPages;
            });
            return back + (sealedPages.Count == 0 ? "nopages" : "ok");
        }
        catch (Exception ex) when (ex is FacebookAuthException or HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            log.LogWarning(ex, "Connexion Facebook refusée pour le client {Client}", p.ClientId);
            return back + "error";
        }
    }

    /// <summary>Oublie le compte et les jetons de Page : les contenus Facebook gardent leur dernière version. (L'accès reste listé chez Facebook, dans les intégrations du compte.)</summary>
    public void Disconnect(Guid clientId) => store.Write(d =>
    {
        var c = d.Clients.First(x => x.Id == clientId);
        c.FacebookUser = ""; c.FacebookConnectedUtc = null; c.FacebookLostUtc = null; c.FacebookPages = new();
    });

    /// <summary>Facebook refuse un jeton de Page : le compte reste affiché « à reconnecter » dans Comptes connectés.</summary>
    public void MarkLost(Guid clientId) => store.Write(d =>
    {
        var c = d.Clients.FirstOrDefault(x => x.Id == clientId);
        if (c == null || c.FacebookConnectedUtc == null) return;
        c.FacebookLostUtc = DateTime.UtcNow; c.FacebookPages = c.FacebookPages.Select(x => new FacebookPageAccess { Id = x.Id, Name = x.Name }).ToList();
    });

    /// <summary>Pages administrées par le compte connecté (vide s'il n'est pas connecté ou perdu).</summary>
    public static List<FacebookPageAccess> Pages(Tenant t) => Has(t) ? t.FacebookPages.Where(p => p.Token.Length > 0).ToList() : new();

    /// <summary>Jeton de Page déchiffré, ou null si cette Page n'est pas administrée par le compte connecté.</summary>
    public string? PageToken(Tenant t, string pageId) =>
        Pages(t).FirstOrDefault(p => p.Id == pageId) is { } page ? box.Unprotect(page.Token) : null;

    // ---------- Échanges avec Facebook ----------

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    }

    /// <summary>Échange un code (ou un jeton de courte durée) contre un jeton d'utilisateur. Les secrets partent dans le corps d'une requête POST, jamais dans l'adresse.</summary>
    private async Task<string> TokenRequest(Dictionary<string, string> form)
    {
        if (!IsConfigured) throw new FacebookAuthException("La connexion Facebook de la plateforme n'est pas configurée (administrateur Linkii).");
        form["client_id"] = AppId;
        form["client_secret"] = AppSecret;
        var body = string.Join("&", form.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
        var (status, text) = await http.Request(HttpMethod.Post, GraphUrl + "oauth/access_token", body, "application/x-www-form-urlencoded");
        if (status is >= 400 and < 500) throw new FacebookAuthException($"Facebook a refusé l'authentification ({status}).");
        if (status != 200) throw new HttpRequestException($"Facebook ({status})");
        var token = JsonSerializer.Deserialize<TokenResponse>(text)?.AccessToken;
        return string.IsNullOrEmpty(token) ? throw new FacebookAuthException("Réponse de Facebook illisible.") : token;
    }

    private async Task<string> Graph(string path, string token)
    {
        var (status, text) = await http.Request(HttpMethod.Get, GraphUrl + path, headers: new Dictionary<string, string> { ["Authorization"] = "OAuth " + token });
        if (status != 200) throw new FacebookAuthException(FacebookProvider.Describe(status, text));
        return text;
    }

    private async Task<string> NameOf(string token)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Graph("me?fields=name", token));
            return doc.RootElement.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "";
        }
        catch (Exception ex) when (ex is FacebookAuthException or HttpRequestException or TaskCanceledException or JsonException) { return ""; }
    }

    private static readonly Regex IdPattern = new(@"^\d{5,30}$");

    /// <summary>Pages de la réponse de /me/accounts : identifiant, nom et jeton de Page. Une entrée sans jeton ou avec un identifiant inattendu est ignorée.</summary>
    public static List<(string Id, string Name, string Token)> ParsePages(string json)
    {
        var list = new List<(string, string, string)>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in data.EnumerateArray())
        {
            string? Str(string prop) => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var id = Str("id"); var token = Str("access_token");
            if (id == null || !IdPattern.IsMatch(id) || string.IsNullOrEmpty(token)) continue;
            var name = (Str("name") ?? "").Trim();
            list.Add((id, name.Length == 0 ? id : name.Length > 100 ? name[..100] : name, token));
        }
        return list;
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
