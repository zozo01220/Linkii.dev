using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>
/// Compte LinkedIn d'une organisation (« Continuer avec LinkedIn », OAuth 2 à trois étapes, lecture seule) pour l'application LinkedIn (§255).
/// L'application LinkedIn est celle de la plateforme Linkivi, distincte de celle de la connexion au back-office (LinkedIn exige une application
/// à part pour la Community Management API) : Linkii:LinkedinPages:ClientId, :ClientSecret, et en production :RedirectUri
/// (adresse de retour déclarée dans le portail LinkedIn ; à défaut, l'adresse du back-office + /linkedin/pages/callback), :ApiVersion (en-tête LinkedIn-Version).
/// Autorisations : r_organization_admin (pages et rôles) et r_organization_social (publications, réactions, médias).
/// Le jeton d'accès dure environ 60 jours ; LinkedIn donne un jeton de renouvellement à certaines applications seulement : il sert alors avant l'échéance.
/// </summary>
public class LinkedinAuth(IConfiguration config, JsonStore store, SecretBox box, SafeHttp http, ILogger<LinkedinAuth> log)
{
    private const string AuthorizeUrl = "https://www.linkedin.com/oauth/v2/authorization";
    private const string TokenUrl = "https://www.linkedin.com/oauth/v2/accessToken";
    public const string Api = "https://api.linkedin.com";
    public const string Scopes = "r_organization_admin r_organization_social";
    public const string CallbackPath = "/linkedin/pages/callback";

    private string ClientId => config["Linkii:LinkedinPages:ClientId"] ?? "";
    private string ClientSecret => config["Linkii:LinkedinPages:ClientSecret"] ?? "";
    /// <summary>En-tête LinkedIn-Version (AAAAMM). LinkedIn retire ses versions au bout d'environ un an : à tenir à jour.</summary>
    public string ApiVersion => config["Linkii:LinkedinPages:ApiVersion"] is { Length: 6 } v && v.All(char.IsAsciiDigit) ? v : "202610";

    /// <summary>L'application LinkedIn de la plateforme est configurée : « Continuer avec LinkedIn » est proposé.</summary>
    public bool IsConfigured => ClientId.Length > 0 && ClientSecret.Length > 0;

    /// <summary>Un compte LinkedIn est connecté, ni perdu ni expiré.</summary>
    public static bool Has(Tenant t) => t.LinkedinConnectedUtc != null && t.LinkedinLostUtc == null && t.LinkedinToken.Length > 0
                                        && (t.LinkedinExpiresUtc == null || t.LinkedinExpiresUtc > DateTime.UtcNow);

    /// <summary>Jours avant l'échéance de la connexion (arrondis vers le bas), ou null sans connexion.</summary>
    public static int? DaysLeft(Tenant t, DateTime now) => t.LinkedinExpiresUtc is { } e && t.LinkedinToken.Length > 0 ? (int)Math.Floor((e - now).TotalDays) : null;

    /// <summary>Pages proposées dans les listes de lecture : lisibles et cochées (vide sans compte).</summary>
    public static List<LinkedinPageAccess> Pages(Tenant t) => Has(t) ? t.LinkedinPages.Where(p => p.CanRead && !p.Off).ToList() : new();

    public class LinkedinAuthException(string message) : Exception(message);

    // ---------- Connexion (code d'autorisation) ----------

    private record Pending(Guid ClientId, string ReturnUrl, string Redirect, DateTime Expires);
    private readonly ConcurrentDictionary<string, Pending> pending = new();

    /// <summary>Adresse de consentement LinkedIn. requestBase : adresse du back-office (adresse de retour par défaut, utile en local).</summary>
    public string StartAuthorization(Guid clientId, string returnUrl, string requestBase)
    {
        foreach (var old in pending.Where(p => p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToList()) pending.TryRemove(old, out _);
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var redirect = config["Linkii:LinkedinPages:RedirectUri"] is { Length: > 0 } r ? r : requestBase.TrimEnd('/') + CallbackPath;
        pending[state] = new Pending(clientId, returnUrl, redirect, DateTime.UtcNow.AddMinutes(15));
        return AuthorizeUrl + "?" + string.Join("&", new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirect,
            ["state"] = state,
            ["scope"] = Scopes
        }.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
    }

    /// <summary>Retour de LinkedIn : échange du code, lecture des pages et des rôles. Rend l'adresse où renvoyer l'utilisateur (?linkedin=ok, nopages ou une erreur).</summary>
    public async Task<string> CompleteAsync(string? state, string? code, string? error)
    {
        if (string.IsNullOrEmpty(state) || !pending.TryRemove(state, out var p) || p.Expires < DateTime.UtcNow) return "/applications?linkedin=expired";
        var back = p.ReturnUrl + (p.ReturnUrl.Contains('?') ? "&" : "?") + "linkedin=";
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) return back + "denied";
        try
        {
            var token = await TokenRequest(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = p.Redirect }, DateTime.UtcNow);
            var orgs = await Organizations(token.Access);
            var user = await NameOf(token.Access);
            store.Write(d =>
            {
                var c = d.Clients.First(x => x.Id == p.ClientId);
                Apply(c, token, box);
                c.LinkedinUser = user; c.LinkedinConnectedUtc = DateTime.UtcNow; c.LinkedinLostUtc = null; c.LinkedinReminded = "";
                c.LinkedinPages = Merge(c.LinkedinPages, orgs);
            });
            return back + (orgs.Any(o => o.CanRead) ? "ok" : "nopages");
        }
        catch (Exception ex) when (ex is LinkedinAuthException or HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            log.LogWarning(ex, "Connexion LinkedIn refusée pour le client {Client}", p.ClientId);
            return back + "error";
        }
    }

    /// <summary>Nouvelles pages lues chez LinkedIn, en gardant les validations, les masquages et le choix (Off) des pages déjà connues.</summary>
    public static List<LinkedinPageAccess> Merge(List<LinkedinPageAccess> known, List<LinkedinPageAccess> fresh) => fresh.Select(f =>
    {
        if (known.FirstOrDefault(k => k.Urn == f.Urn) is not { } k) return f;
        f.Off = k.Off; f.Approved = k.Approved; f.Hidden = k.Hidden;
        return f;
    }).ToList();

    private static void Apply(Tenant c, Token token, SecretBox box)
    {
        c.LinkedinToken = box.Protect(token.Access);
        c.LinkedinExpiresUtc = token.Expires;
        if (token.Refresh is { Length: > 0 } r) { c.LinkedinRefresh = box.Protect(r); c.LinkedinRefreshExpiresUtc = token.RefreshExpires; }
        else if (c.LinkedinRefreshExpiresUtc is { } re && re <= DateTime.UtcNow) { c.LinkedinRefresh = ""; c.LinkedinRefreshExpiresUtc = null; }
    }

    /// <summary>Oublie le compte et les jetons : les contenus LinkedIn gardent leur dernière version. (L'accès reste listé chez LinkedIn, dans les applications autorisées.)</summary>
    public void Disconnect(Guid clientId) => store.Write(d =>
    {
        var c = d.Clients.First(x => x.Id == clientId);
        c.LinkedinUser = ""; c.LinkedinConnectedUtc = null; c.LinkedinLostUtc = null; c.LinkedinExpiresUtc = null;
        c.LinkedinToken = ""; c.LinkedinRefresh = ""; c.LinkedinRefreshExpiresUtc = null; c.LinkedinReminded = "";
        c.LinkedinPages = new();
    });

    /// <summary>LinkedIn refuse le jeton (expiré ou retiré) : l'application passe « à reconnecter ».</summary>
    public void MarkLost(Guid clientId) => store.Write(d =>
    {
        var c = d.Clients.FirstOrDefault(x => x.Id == clientId);
        if (c == null || c.LinkedinConnectedUtc == null || c.LinkedinLostUtc != null) return;
        c.LinkedinLostUtc = DateTime.UtcNow; c.LinkedinToken = ""; c.LinkedinRefresh = "";
    });

    /// <summary>
    /// Jeton d'accès déchiffré, renouvelé d'abord s'il expire dans moins de 7 jours et que LinkedIn a donné un jeton de renouvellement.
    /// Null sans compte (ou compte perdu, ou expiré sans renouvellement possible).
    /// </summary>
    public async Task<string?> AccessToken(Tenant t)
    {
        if (t.LinkedinConnectedUtc == null || t.LinkedinLostUtc != null || t.LinkedinToken.Length == 0) return null;
        var now = DateTime.UtcNow;
        if (t.LinkedinExpiresUtc is { } exp && exp - now < TimeSpan.FromDays(7) && t.LinkedinRefresh.Length > 0 && (t.LinkedinRefreshExpiresUtc is not { } re || re > now))
        {
            try
            {
                var fresh = await TokenRequest(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = box.Unprotect(t.LinkedinRefresh) }, now);
                store.Write(d => { if (d.Clients.FirstOrDefault(x => x.Id == t.Id) is { } c) { Apply(c, fresh, box); c.LinkedinReminded = ""; } });
                return fresh.Access;
            }
            catch (Exception ex) when (ex is LinkedinAuthException or HttpRequestException or TaskCanceledException or JsonException or CryptographicException or InvalidOperationException)
            {
                log.LogWarning(ex, "Renouvellement du jeton LinkedIn impossible pour le client {Client}", t.Id);
            }
        }
        if (t.LinkedinExpiresUtc is { } e && e <= now) return null;
        try { return box.Unprotect(t.LinkedinToken); }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException) { return null; }
    }

    /// <summary>Relit les pages et les rôles chez LinkedIn (bouton « Configurer » de la page de l'application) ; false si le compte n'est plus utilisable.</summary>
    public async Task<bool> RefreshPages(Tenant t)
    {
        if (await AccessToken(t) is not { } token) return false;
        try
        {
            var orgs = await Organizations(token);
            store.Write(d => { if (d.Clients.FirstOrDefault(x => x.Id == t.Id) is { } c) c.LinkedinPages = Merge(c.LinkedinPages, orgs); });
            return true;
        }
        catch (LinkedinAuthException ex) when (ex.Message.Contains("401")) { MarkLost(t.Id); return false; }
    }

    // ---------- Validation des publications (réglage « Valider chaque nouvelle publication ») ----------

    public const string Shown = "ok", Waiting = "wait", Masked = "hid";

    /// <summary>État d'une publication pour l'organisation : masquée, affichée (validée, ou validation coupée), ou à valider.</summary>
    public static string StateOf(Tenant t, LinkedinPageAccess? page, string postId) =>
        page == null ? (t.LinkedinApproval ? Waiting : Shown)
        : page.Hidden.Contains(postId) ? Masked
        : !t.LinkedinApproval || page.Approved.Contains(postId) ? Shown : Waiting;

    /// <summary>Valide (ok), masque (hid) ou remet à valider (wait) une publication. Pris en compte par les écrans à leur prochaine lecture, sans republier.</summary>
    public static void SetState(Tenant c, string urn, string postId, string state)
    {
        if (c.LinkedinPages.FirstOrDefault(p => p.Urn == urn) is not { } page || !PostPattern.IsMatch(postId)) return;
        page.Approved.Remove(postId); page.Hidden.Remove(postId);
        if (state == Shown) page.Approved.Insert(0, postId);
        else if (state == Masked) page.Hidden.Insert(0, postId);
        if (page.Approved.Count > 200) page.Approved.RemoveRange(200, page.Approved.Count - 200);
        if (page.Hidden.Count > 200) page.Hidden.RemoveRange(200, page.Hidden.Count - 200);
    }

    // ---------- Échanges avec LinkedIn ----------

    public record Token(string Access, DateTime Expires, string? Refresh, DateTime? RefreshExpires);

    /// <summary>Lit la réponse de /oauth/v2/accessToken (expires_in en secondes ; refresh_token facultatif).</summary>
    public static Token ParseToken(string json, DateTime now)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var access = r.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        if (string.IsNullOrEmpty(access)) throw new LinkedinAuthException("Réponse de LinkedIn sans jeton.");
        var secs = r.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var s) && s > 0 ? s : 60L * 24 * 3600;
        var refresh = r.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null;
        DateTime? refreshExp = r.TryGetProperty("refresh_token_expires_in", out var re) && re.TryGetInt64(out var rs) && rs > 0 ? now.AddSeconds(rs) : null;
        return new Token(access, now.AddSeconds(secs), string.IsNullOrEmpty(refresh) ? null : refresh, refreshExp);
    }

    /// <summary>Échange un code ou un jeton de renouvellement. Les secrets partent dans le corps d'une requête POST, jamais dans l'adresse.</summary>
    private async Task<Token> TokenRequest(Dictionary<string, string> form, DateTime now)
    {
        if (!IsConfigured) throw new LinkedinAuthException("La connexion LinkedIn de la plateforme n'est pas configurée (administrateur Linkivi).");
        form["client_id"] = ClientId;
        form["client_secret"] = ClientSecret;
        var body = string.Join("&", form.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
        var (status, text) = await http.Request(HttpMethod.Post, TokenUrl, body, "application/x-www-form-urlencoded");
        if (status is >= 400 and < 500) throw new LinkedinAuthException($"LinkedIn a refusé l'authentification ({status}).");
        if (status != 200) throw new HttpRequestException($"LinkedIn ({status})");
        return ParseToken(text, now);
    }

    /// <summary>Appel en lecture à l'API REST de LinkedIn (jeton dans l'en-tête, version imposée). method : X-RestLi-Method (FINDER…).</summary>
    public Task<(int Status, string Body)> Get(string pathAndQuery, string token, string? method = null)
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + token,
            ["LinkedIn-Version"] = ApiVersion,
            ["X-Restli-Protocol-Version"] = "2.0.0",
        };
        if (method != null) headers["X-RestLi-Method"] = method;
        return http.Request(HttpMethod.Get, Api + pathAndQuery, headers: headers);
    }

    /// <summary>Pages où la personne a un rôle approuvé, avec leur nom (lu à part : il est facultatif).</summary>
    private async Task<List<LinkedinPageAccess>> Organizations(string token)
    {
        var (status, body) = await Get("/rest/organizationAcls?q=roleAssignee&state=APPROVED&count=100", token, "FINDER");
        if (status != 200) throw new LinkedinAuthException(Describe(status));
        var list = ParseAcls(body);
        foreach (var p in list)
        {
            try
            {
                var (s2, b2) = await Get("/rest/organizations/" + p.Urn[(p.Urn.LastIndexOf(':') + 1)..], token);
                if (s2 == 200)
                {
                    using var doc = JsonDocument.Parse(b2);
                    if (doc.RootElement.TryGetProperty("localizedName", out var n) && n.GetString() is { Length: > 0 } nm) p.Name = nm.Length > 100 ? nm[..100] : nm;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { /* le nom est facultatif : l'identifiant suffit */ }
        }
        return list;
    }

    /// <summary>Nom de la personne connectée : LinkedIn ne le donne pas sans autorisation de profil, on garde alors « Compte LinkedIn ».</summary>
    private async Task<string> NameOf(string token)
    {
        try
        {
            var (status, body) = await Get("/v2/me", token);
            if (status != 200) return "";
            using var doc = JsonDocument.Parse(body);
            string S(string k) => doc.RootElement.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return (S("localizedFirstName") + " " + S("localizedLastName")).Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return ""; }
    }

    private static readonly Regex OrgPattern = new(@"^urn:li:organization:\d{1,20}$");
    public static readonly Regex PostPattern = new(@"^urn:li:(share|ugcPost):\d{1,30}$");

    /// <summary>Rôles qui permettent de lire les publications d'une page (r_organization_social).</summary>
    public static bool Readable(string role) => role is "ADMINISTRATOR" or "CONTENT_ADMINISTRATOR";

    /// <summary>Libellé d'un rôle LinkedIn pour l'assistant.</summary>
    public static string RoleLabel(string role) => role switch
    {
        "ADMINISTRATOR" => "Administrateur",
        "CONTENT_ADMINISTRATOR" => "Éditeur de contenu",
        "ANALYST" => "Analyste",
        "CURATOR" => "Curateur",
        "DIRECT_SPONSORED_CONTENT_POSTER" => "Annonceur",
        "RECRUITING_POSTER" => "Recruteur",
        "LEAD_GEN_FORMS_MANAGER" => "Formulaires",
        _ => "Autre rôle"
    };

    /// <summary>
    /// Lit la réponse du finder organizationAcls : une page par organisation, avec le rôle qui donne le plus d'accès
    /// (Administrateur, puis Éditeur de contenu, puis les autres). Les entrées inattendues sont ignorées.
    /// </summary>
    public static List<LinkedinPageAccess> ParseAcls(string json)
    {
        var byOrg = new Dictionary<string, LinkedinPageAccess>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("elements", out var els) || els.ValueKind != JsonValueKind.Array) return new();
        static int Rank(string r) => r == "ADMINISTRATOR" ? 0 : r == "CONTENT_ADMINISTRATOR" ? 1 : 2;
        foreach (var e in els.EnumerateArray())
        {
            string? Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var urn = Str("organization") ?? Str("organizationTarget");
            if (urn == null || !OrgPattern.IsMatch(urn)) continue;
            if (Str("state") is { } st && st != "APPROVED") continue;
            var role = Str("role") ?? "";
            if (byOrg.TryGetValue(urn, out var known) && Rank(known.Role) <= Rank(role)) continue;
            byOrg[urn] = new LinkedinPageAccess { Urn = urn, Name = "Page " + urn[(urn.LastIndexOf(':') + 1)..], Role = role, CanRead = Readable(role) };
        }
        return byOrg.Values.ToList();
    }

    /// <summary>Explique une erreur HTTP de l'API LinkedIn sans reprendre son texte brut (LinkedIn répond souvent 403 sans dire pourquoi).</summary>
    public static string Describe(int status) => status switch
    {
        401 => "La connexion LinkedIn a expiré (401) : reconnectez le compte.",
        403 => "LinkedIn refuse l'accès : le compte doit être administrateur ou éditeur de contenu de la page, et l'application Linkivi doit avoir l'accès à la Community Management API.",
        404 => "Page ou publication introuvable chez LinkedIn.",
        429 => "LinkedIn limite temporairement les appels : réessayez dans quelques minutes.",
        _ => $"LinkedIn répond avec une erreur (HTTP {status})."
    };

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
