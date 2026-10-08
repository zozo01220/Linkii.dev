using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>
/// Réglages de l'application LinkedIn de la plateforme (créée une fois par l'éditeur dans le portail LinkedIn Developers) :
/// section « Linkedin » de la configuration (appsettings.json ou variables d'environnement Linkedin__ClientId…).
/// </summary>
public class LinkedinOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Facultatif : adresse https complète de /linkedin/callback. Vide : celle de la requête (https://domaine/linkedin/callback). Dans les deux cas, elle doit être déclarée à l'identique dans le portail LinkedIn.</summary>
    public string RedirectUri { get; set; } = "";
    /// <summary>En-tête LinkedIn-Version (AAAAMM). LinkedIn retire les anciennes versions : à tenir à jour.</summary>
    public string ApiVersion { get; set; } = "202609";
    public string Scopes { get; set; } = "r_organization_social r_organization_admin";

    public bool Configured => ClientId.Length > 0 && ClientSecret.Length > 0;
}

/// <summary>Identifiants de l'application LinkedIn utilisée pour la connexion (saisis par le client dans les paramètres de l'app, ou fournis par la plateforme).</summary>
public record LinkedinApp(string ClientId, string ClientSecret);

/// <summary>Jeton d'un administrateur de page. Les jetons durent 60 jours ; le renouvellement automatique est réservé à certains partenaires de LinkedIn.</summary>
public record LinkedinToken(string Access, DateTime ExpiresUtc)
{
    public bool Expired => DateTime.UtcNow >= ExpiresUtc;
}

public record LinkedinOrg(string Urn, string Name);

public record LinkedinPost(string Id, string Text, DateTime? PublishedUtc, string? ImageUrn, string? ArticleTitle)
{
    /// <summary>Adresse publique de la publication (celle du QR code).</summary>
    public string Url => "https://www.linkedin.com/feed/update/" + Id + "/";
}

/// <summary>Appels à l'API officielle de LinkedIn (OAuth, pages administrées, publications). Aucune lecture du site web.</summary>
public class LinkedinService(IConfiguration config, SafeHttp http)
{
    private const string Api = "https://api.linkedin.com";
    private readonly ConcurrentDictionary<string, (DateTime At, List<LinkedinPost> Posts)> _cache = new();

    public LinkedinOptions Options { get; } = config.GetSection("Linkedin").Get<LinkedinOptions>() ?? new LinkedinOptions();
    /// <summary>Application LinkedIn de la plateforme (facultative) : utilisée quand le client n'a pas saisi la sienne.</summary>
    public LinkedinApp? Platform => Options.Configured ? new LinkedinApp(Options.ClientId, Options.ClientSecret) : null;

    /// <summary>Application LinkedIn d'une instance : celle saisie dans ses paramètres, sinon celle de la plateforme.</summary>
    public LinkedinApp? AppFor(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> secrets, SecretBox box)
    {
        var id = settings.GetValueOrDefault("clientId")?.Trim() ?? "";
        if (id.Length > 0 && secrets.TryGetValue("clientSecret", out var enc))
        {
            try { var sec = box.Unprotect(enc); if (sec.Length > 0) return new LinkedinApp(id, sec); } catch { /* secret illisible : retombe sur la plateforme */ }
        }
        return Platform;
    }

    // ---------- OAuth ----------

    public string RedirectFor(HttpRequest req) =>
        Options.RedirectUri.Length > 0 ? Options.RedirectUri : req.Scheme + "://" + req.Host + "/linkedin/callback";

    public string AuthUrl(string state, string redirectUri, LinkedinApp app) =>
        "https://www.linkedin.com/oauth/v2/authorization?response_type=code"
        + "&client_id=" + Uri.EscapeDataString(app.ClientId)
        + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
        + "&state=" + Uri.EscapeDataString(state)
        + "&scope=" + Uri.EscapeDataString(Options.Scopes);

    public async Task<LinkedinToken> Exchange(string code, string redirectUri, LinkedinApp app)
    {
        var body = "grant_type=authorization_code&code=" + Uri.EscapeDataString(code)
            + "&client_id=" + Uri.EscapeDataString(app.ClientId)
            + "&client_secret=" + Uri.EscapeDataString(app.ClientSecret)
            + "&redirect_uri=" + Uri.EscapeDataString(redirectUri);
        var (status, text) = await http.Request(HttpMethod.Post, "https://www.linkedin.com/oauth/v2/accessToken", body, "application/x-www-form-urlencoded");
        if (status != 200) throw new InvalidOperationException("LinkedIn a refusé la connexion (" + status + ").");
        return ParseToken(text, DateTime.UtcNow);
    }

    public static LinkedinToken ParseToken(string json, DateTime now)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var token = r.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("Réponse de LinkedIn sans jeton.");
        var seconds = r.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var s) ? s : 60L * 24 * 3600;
        return new LinkedinToken(token, now.AddSeconds(seconds));
    }

    // ---------- Jeton enregistré (secret chiffré de l'instance) ----------

    public const string AuthKey = "linkedinAuth";

    public static string AuthJson(LinkedinToken t) => JsonSerializer.Serialize(new { access = t.Access, exp = t.ExpiresUtc });

    public static LinkedinToken? ReadAuth(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var d = JsonDocument.Parse(json);
            return new LinkedinToken(d.RootElement.GetProperty("access").GetString()!, d.RootElement.GetProperty("exp").GetDateTime().ToUniversalTime());
        }
        catch { return null; }
    }

    /// <summary>
    /// Enregistre (ou retire, si null) le jeton chiffré de l'instance, ainsi que dans ses instantanés déjà publiés : se reconnecter ne demande
    /// pas de republier la playlist. Rend false si l'instance n'appartient pas à ce client.
    /// </summary>
    public static bool StoreAuth(JsonStore store, Guid clientId, Guid mediaId, string? protectedAuth) => store.Write(db =>
    {
        var m = db.Media.FirstOrDefault(x => x.Id == mediaId && x.ClientId == clientId && x.AppId == "linkedin");
        if (m == null) return false;
        void Apply(Dictionary<string, string> secrets) { if (protectedAuth == null) secrets.Remove(AuthKey); else secrets[AuthKey] = protectedAuth; }
        Apply(m.Secrets);
        foreach (var pl in db.Playlists.Where(p => p.ClientId == clientId))
            foreach (var pi in pl.Published.Where(x => x.MediaId == mediaId)) Apply(pi.Secrets);
        foreach (var s in db.Screens.Where(s => s.ClientId == clientId))   // instantanés publiés écran par écran
            foreach (var pi in s.Published.Where(x => x.MediaId == mediaId)) Apply(pi.Secrets);
        return true;
    });

    // ---------- Pages administrées ----------

    private Task<(int Status, string Body)> Get(string pathAndQuery, string token, string? method = null)
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + token,
            ["LinkedIn-Version"] = Options.ApiVersion,
            ["X-Restli-Protocol-Version"] = "2.0.0",
        };
        if (method != null) headers["X-RestLi-Method"] = method;
        return http.Request(HttpMethod.Get, Api + pathAndQuery, headers: headers);
    }

    public async Task<List<LinkedinOrg>> Organizations(string token)
    {
        var (status, body) = await Get("/rest/organizationAcls?q=roleAssignee&role=ADMINISTRATOR&state=APPROVED&count=50", token, "FINDER");
        if (status != 200) throw new InvalidOperationException(Describe(status));
        var urns = ParseOrganizationUrns(body);
        var list = new List<LinkedinOrg>();
        foreach (var urn in urns)
        {
            var name = "Page " + urn[(urn.LastIndexOf(':') + 1)..];
            try
            {
                var (s2, b2) = await Get("/rest/organizations/" + urn[(urn.LastIndexOf(':') + 1)..], token);
                if (s2 == 200 && JsonDocument.Parse(b2).RootElement.TryGetProperty("localizedName", out var n) && n.GetString() is { Length: > 0 } nm) name = nm;
            }
            catch { /* le nom est facultatif : l'identifiant suffit */ }
            list.Add(new LinkedinOrg(urn, name));
        }
        return list;
    }

    public static List<string> ParseOrganizationUrns(string json)
    {
        var urns = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("elements", out var els)) return urns;
        foreach (var e in els.EnumerateArray())
        {
            var urn = (e.TryGetProperty("organization", out var o) ? o.GetString() : null) ?? (e.TryGetProperty("organizationTarget", out var t) ? t.GetString() : null);
            if (urn != null && Regex.IsMatch(urn, @"^urn:li:organization:\d+$") && !urns.Contains(urn)) urns.Add(urn);
        }
        return urns;
    }

    // ---------- Publications ----------

    /// <summary>Publications publiques d'une page, les plus récentes d'abord (mises en cache 10 minutes : l'API de LinkedIn est limitée en volume).</summary>
    public async Task<List<LinkedinPost>> Posts(string token, string orgUrn, int count)
    {
        if (!Regex.IsMatch(orgUrn, @"^urn:li:organization:\d+$")) throw new InvalidOperationException("Page LinkedIn invalide.");
        var key = orgUrn + "|" + count;
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10)) return hit.Posts;
        try
        {
            var (status, body) = await Get("/rest/posts?author=" + Uri.EscapeDataString(orgUrn) + "&q=author&count=" + Math.Clamp(count, 1, 50) + "&sortBy=LAST_MODIFIED", token, "FINDER");
            if (status != 200) throw new InvalidOperationException(Describe(status));
            var posts = ParsePosts(body);
            _cache[key] = (DateTime.UtcNow, posts);
            return posts;
        }
        catch when (hit.Posts != null) { return hit.Posts; }   // dernière version connue si LinkedIn est indisponible
    }

    /// <summary>Adresse de téléchargement d'une image de publication. L'API la réserve parfois à des droits supplémentaires : sans eux, la publication reste affichée sans image.</summary>
    public async Task<string?> ImageUrl(string token, string imageUrn)
    {
        if (!Regex.IsMatch(imageUrn, @"^urn:li:image:[A-Za-z0-9_-]+$")) return null;
        try
        {
            var (status, body) = await Get("/rest/images/" + imageUrn, token);
            if (status != 200) return null;
            return JsonDocument.Parse(body).RootElement.TryGetProperty("downloadUrl", out var u) && u.GetString() is { } url && AppCatalog.IsHttpUrl(url) ? url : null;
        }
        catch { return null; }
    }

    /// <summary>Lit la réponse du finder « posts par auteur » : publications publiées, publiques, visibles dans le fil (les publications sponsorisées « dark » sont écartées).</summary>
    public static List<LinkedinPost> ParsePosts(string json)
    {
        var list = new List<LinkedinPost>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("elements", out var els)) return list;
        foreach (var e in els.EnumerateArray())
        {
            string? Str(JsonElement x, string name) => x.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var id = Str(e, "id");
            if (id == null || !Regex.IsMatch(id, @"^urn:li:(share|ugcPost):\d+$")) continue;
            if (Str(e, "lifecycleState") != "PUBLISHED" || Str(e, "visibility") != "PUBLIC") continue;
            if (e.TryGetProperty("distribution", out var dist) && Str(dist, "feedDistribution") == "NONE") continue;

            string? image = null, articleTitle = null;
            if (e.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                if (c.TryGetProperty("media", out var m) && Str(m, "id") is { } mid && mid.StartsWith("urn:li:image:")) image = mid;
                if (c.TryGetProperty("multiImage", out var mi) && mi.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0
                    && Str(imgs[0], "id") is { } fid) image ??= fid;
                if (c.TryGetProperty("article", out var art)) { articleTitle = Str(art, "title"); if (Str(art, "thumbnail") is { } th) image ??= th; }
            }
            DateTime? published = e.TryGetProperty("publishedAt", out var p) && p.TryGetInt64(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;
            list.Add(new LinkedinPost(id, CleanText(Str(e, "commentary") ?? ""), published, image, articleTitle));
        }
        return list;
    }

    /// <summary>Texte d'une publication : mentions « @[Nom](urn:…) » ramenées au nom, gabarits de hashtags « {hashtag|\#|mot} » à « #mot ».</summary>
    public static string CleanText(string s)
    {
        s = Regex.Replace(s, @"@\[([^\]]*)\]\([^)]*\)", "$1");
        s = Regex.Replace(s, @"\{hashtag\|\\?#\|([^}]*)\}", "#$1");
        s = s.Replace("\\#", "#").Replace("\r", "");
        return s.Trim();
    }

    private static string Describe(int status) => status switch
    {
        401 => "Le jeton LinkedIn a expiré : reconnectez le compte.",
        403 => "LinkedIn refuse l'accès : le compte doit administrer la page et l'application doit avoir été approuvée pour lire les publications d'une page.",
        429 => "LinkedIn limite temporairement les appels : réessayez plus tard.",
        _ => "LinkedIn a répondu avec une erreur (" + status + ").",
    };
}
