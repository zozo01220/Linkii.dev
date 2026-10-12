using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Linkii.Poc;

/// <summary>Les paramètres d'une app tels que le serveur les voit : valeurs publiques et secrets déchiffrés.</summary>
public record AppRuntime(Tenant Client, IReadOnlyDictionary<string, string> Values)
{
    public string Get(string key, string fallback = "") => Values.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;
    public int GetInt(string key, int fallback, int min, int max) =>
        int.TryParse(Get(key), out var n) ? Math.Clamp(n, min, max) : fallback;
}

/// <summary>Code serveur d'une app : lecture des données externes (secrets et appels sortants restent ici) et test de la configuration.</summary>
public interface IAppProvider
{
    string Id { get; }
    /// <summary>Données envoyées au player pour <c>/api/data/{id}</c> (null si l'app n'en a pas besoin).</summary>
    Task<object?> Fetch(AppRuntime rt);
    /// <summary>Vérifie la configuration ; renvoie un message (« ⚠ … » pour un avertissement) ou lève une exception.</summary>
    Task<string> Test(AppRuntime rt);
}

public class AppProviders(IEnumerable<IAppProvider> providers)
{
    public IAppProvider? Find(string? id) => providers.FirstOrDefault(p => p.Id == id);
}

// ---------------------------------------------------------------------------------------------
public class WeatherProvider(WeatherService weather) : IAppProvider
{
    public string Id => "weather";

    public async Task<object?> Fetch(AppRuntime rt)
    {
        var w = await weather.Get(rt.Get("city"), rt.Get("unit", "C")) ?? throw new InvalidOperationException("Ville introuvable ou météo indisponible.");
        return new { city = w.City, temp = w.Temp, code = w.Code, min = w.Min, max = w.Max, wind = w.Wind, unit = w.Unit };
    }

    public async Task<string> Test(AppRuntime rt)
    {
        var w = await weather.Get(rt.Get("city"), rt.Get("unit", "C")) ?? throw new InvalidOperationException("Ville introuvable ou météo indisponible.");
        return $"Ville trouvée : {w.City} · {w.Temp}°{w.Unit} (min {w.Min}°, max {w.Max}°).";
    }
}

// ---------------------------------------------------------------------------------------------
public class CalendarProvider(CalendarService calendars, SecretBox box) : IAppProvider
{
    public string Id => "calendar";

    private static (DateTime From, DateTime To) Window(AppRuntime rt, int days)
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(rt.Client.Timezone); } catch { tz = TimeZoneInfo.Utc; }
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var start = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified), tz);
        return (start, start.AddDays(days));
    }

    // Le contenu ne désigne qu'un calendrier partagé : la connexion est lue dans Intégrations au moment de l'appel.
    private CalendarSource Source(AppRuntime rt) => Guid.TryParse(rt.Get("calendar"), out var id)
        ? SharedCalendarSources.Resolve(rt.Client, id, box)
        : throw new InvalidOperationException("Choisissez un calendrier.");

    public async Task<object?> Fetch(AppRuntime rt)
    {
        var (from, to) = Window(rt, rt.GetInt("days", 7, 1, 14));
        var events = await calendars.GetEvents(Source(rt), rt.Client, from, to);
        return new
        {
            kind = "agenda", title = rt.Get("title"), fetchedAt = DateTime.UtcNow, showTitles = true,
            events = events.Select(e => new { title = e.Title, start = e.StartUtc, end = e.EndUtc, allDay = e.AllDay, location = e.Location })
        };
    }

    public async Task<string> Test(AppRuntime rt)
    {
        var (from, to) = Window(rt, rt.GetInt("days", 7, 1, 14));
        var ev = await calendars.GetEvents(Source(rt), rt.Client, from, to);
        return $"Calendrier lu · {ev.Count} événement(s) sur la période.";
    }
}

// ---------------------------------------------------------------------------------------------
public record FeedItem(string Title, string? Summary, string? Link, DateTime? Published);
public record Feed(string Title, List<FeedItem> Items);

public class RssProvider(SafeHttp http) : IAppProvider
{
    public string Id => "rss";
    private readonly ConcurrentDictionary<string, (DateTime At, Feed Feed)> _cache = new();

    private async Task<Feed> Load(string url)
    {
        if (_cache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(5)) return hit.Feed;
        try
        {
            var feed = Parse(await http.GetString(url));
            _cache[url] = (DateTime.UtcNow, feed);
            return feed;
        }
        catch when (hit.Feed != null) { return hit.Feed; }   // dernière version connue si la source est indisponible
    }

    public async Task<object?> Fetch(AppRuntime rt)
    {
        var feed = await Load(rt.Get("url"));
        var show = rt.Get("showSummary") == "true";
        return new
        {
            kind = "rss", title = string.IsNullOrWhiteSpace(rt.Get("title")) ? feed.Title : rt.Get("title"), fetchedAt = DateTime.UtcNow,
            items = feed.Items.Take(rt.GetInt("count", 8, 1, 20))
                .Select(i => new { title = i.Title, summary = show ? i.Summary : null, link = i.Link, published = i.Published })
        };
    }

    public async Task<string> Test(AppRuntime rt)
    {
        var feed = await Load(rt.Get("url"));
        return $"Flux lu : « {feed.Title} » · {feed.Items.Count} article(s)" + (feed.Items.Count > 0 ? $" · le plus récent : {feed.Items[0].Title}" : "") + ".";
    }

    /// <summary>Lit un flux RSS 2.0 ou Atom. Pas de DTD ni de ressource externe (XXE) ; le HTML des résumés est retiré.</summary>
    public static Feed Parse(string xml)
    {
        XDocument doc;
        using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0 }))
            doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new InvalidOperationException("Flux vide.");
        var items = new List<FeedItem>();
        string title;

        if (root.Name.LocalName == "feed")   // Atom
        {
            title = Text(root.Elements().FirstOrDefault(e => e.Name.LocalName == "title"));
            foreach (var e in root.Elements().Where(e => e.Name.LocalName == "entry"))
            {
                var link = e.Elements().Where(x => x.Name.LocalName == "link").FirstOrDefault(x => (string?)x.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value;
                var summary = e.Elements().FirstOrDefault(x => x.Name.LocalName is "summary" or "content");
                var date = e.Elements().FirstOrDefault(x => x.Name.LocalName is "published" or "updated")?.Value;
                items.Add(new FeedItem(Text(e.Elements().FirstOrDefault(x => x.Name.LocalName == "title")), Clean(summary?.Value), SafeLink(link), ParseDate(date)));
            }
        }
        else if (root.Name.LocalName is "rss" or "RDF")   // RSS 2.0 / RSS 1.0
        {
            var channel = root.Elements().FirstOrDefault(e => e.Name.LocalName == "channel") ?? root;
            title = Text(channel.Elements().FirstOrDefault(e => e.Name.LocalName == "title"));
            foreach (var e in root.Descendants().Where(e => e.Name.LocalName == "item"))
            {
                string? V(string n) => e.Elements().FirstOrDefault(x => x.Name.LocalName == n)?.Value;
                items.Add(new FeedItem(Text(e.Elements().FirstOrDefault(x => x.Name.LocalName == "title")), Clean(V("description") ?? V("encoded")), SafeLink(V("link")), ParseDate(V("pubDate") ?? V("date"))));
            }
        }
        else throw new InvalidOperationException("Ce n'est pas un flux RSS ou Atom.");

        items = items.Where(i => i.Title.Length > 0).ToList();
        return new Feed(title.Length > 0 ? title : "Actualités", items);
    }

    private static string Text(XElement? e) => Clean(e?.Value) ?? "";

    private static string? Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var t = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " "));
        t = Regex.Replace(t, @"\s+", " ").Trim();
        if (t.Length > 280) t = t[..277].TrimEnd() + "…";
        return t.Length == 0 ? null : t;
    }

    private static string? SafeLink(string? l) => l != null && AppCatalog.IsHttpUrl(l.Trim()) ? l.Trim() : null;

    private static DateTime? ParseDate(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;
}

// ---------------------------------------------------------------------------------------------
/// <summary>Page web : rien à lire côté serveur, mais le test vérifie que la page est joignable et qu'elle accepte d'être affichée dans une iframe.</summary>
public class WebPageProvider(SafeHttp http) : IAppProvider
{
    public string Id => "webpage";
    public Task<object?> Fetch(AppRuntime rt) => Task.FromResult<object?>(null);

    public async Task<string> Test(AppRuntime rt)
    {
        var url = rt.Get("url");
        using var res = await http.Send(url);
        var code = (int)res.StatusCode;
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"La page répond avec une erreur (HTTP {code}).");

        var xfo = res.Headers.TryGetValues("X-Frame-Options", out var x) ? string.Join(",", x) : "";
        var csp = res.Headers.TryGetValues("Content-Security-Policy", out var c) ? string.Join(";", c) : "";
        var ancestors = Regex.Match(csp, @"frame-ancestors\s+([^;]*)", RegexOptions.IgnoreCase);
        if (Regex.IsMatch(xfo, "deny|sameorigin", RegexOptions.IgnoreCase) || ancestors.Success && !ancestors.Groups[1].Value.Contains('*'))
            return "⚠ Page joignable, mais ce site interdit son affichage dans une page externe : elle restera vide sur l'écran.";
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "⚠ Page joignable en http : préférez https (un player servi en https la bloquerait).";
        return $"Page joignable (HTTP {code}) et affichable dans une iframe.";
    }
}

// ---------------------------------------------------------------------------------------------
/// <summary>
/// YouTube : le player utilise l'API IFrame de YouTube ; côté serveur, le test interroge oEmbed (sans clé d'API) pour savoir si la vidéo
/// ou la playlist existe et si son propriétaire autorise l'intégration.
/// </summary>
public class YouTubeProvider(SafeHttp http) : IAppProvider
{
    public string Id => "youtube";
    public Task<object?> Fetch(AppRuntime rt) => Task.FromResult<object?>(null);

    public async Task<string> Test(AppRuntime rt)
    {
        var link = YouTube.Canonical(rt.Get("url")) ?? throw new InvalidOperationException("Lien YouTube non reconnu (vidéo ou playlist).");
        using var res = await http.Send("https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(link));
        var body = res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync() : "";
        return Describe((int)res.StatusCode, body, link.Contains("/playlist"));
    }

    /// <summary>Interprète la réponse d'oEmbed : 200 = intégrable, 401 / 403 = privée ou intégration désactivée, 404 = introuvable.</summary>
    public static string Describe(int status, string body, bool playlist)
    {
        var what = playlist ? "Playlist" : "Vidéo";
        switch (status)
        {
            case 200:
                using (var doc = System.Text.Json.JsonDocument.Parse(body))
                {
                    var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
                    var author = doc.RootElement.TryGetProperty("author_name", out var a) ? a.GetString() : null;
                    return $"{what} trouvée : « {title ?? "(sans titre)"} »" + (author != null ? $" · {author}" : "") + ". Elle nécessite une connexion internet sur l'écran.";
                }
            case 401 or 403:
                return $"⚠ {what} privée ou intégration désactivée par son propriétaire : elle ne pourra pas être lue sur l'écran.";
            case 404:
                throw new InvalidOperationException($"{what} introuvable (supprimée ou lien erroné).");
            default:
                throw new InvalidOperationException($"YouTube répond avec une erreur (HTTP {status}).");
        }
    }
}

// ---------------------------------------------------------------------------------------------
public record FacebookPost(string Id, string? Message, DateTime? Created, string? Picture);
public record FacebookPage(string Name, List<FacebookPost> Posts);

/// <summary>
/// Facebook : publications d'une Page que le client administre, lues avec l'API Graph de Meta. Le jeton de Page est celui du compte connecté
/// (<see cref="FacebookAuth"/>) ou, à défaut, un jeton collé dans les options avancées ; c'est un secret conservé sur le serveur (envoyé dans un en-tête,
/// jamais dans l'adresse) : le player ne reçoit que le texte, la date et l'adresse de la photo de chaque publication.
/// Les publications sont gardées 15 minutes ; si Facebook est indisponible, la dernière version connue est servie.
/// </summary>
public class FacebookProvider(SafeHttp http, FacebookAuth account) : IAppProvider
{
    public string Id => "facebook";
    private const string GraphUrl = "https://graph.facebook.com/v21.0/";
    private const string Fields = "message,created_time,full_picture,from{name}";
    private static readonly Regex TokenPattern = new(@"^[A-Za-z0-9_\-|.]{20,1000}$");
    private static readonly Regex PagePattern = new(@"^[A-Za-z0-9.\-]{3,100}$");
    private readonly ConcurrentDictionary<string, (DateTime At, FacebookPage Page)> _cache = new();

    private async Task<FacebookPage> Load(AppRuntime rt, bool fresh = false)
    {
        var page = PageRef(rt.Get("pageId")) ?? throw new InvalidOperationException("Page Facebook non reconnue : collez son identifiant ou son adresse (facebook.com/NomDeLaPage).");
        var token = rt.Get("token");   // jeton collé à la main : prioritaire
        var fromAccount = false;
        if (token.Length == 0 && account.PageToken(rt.Client, page) is { Length: > 0 } own) { token = own; fromAccount = true; }
        if (token.Length == 0)
            throw new InvalidOperationException(FacebookAuth.Has(rt.Client)
                ? "Cette Page n'est pas administrée par le compte Facebook connecté : choisissez une de ses Pages, ou collez un jeton dans les options avancées."
                : "Aucun compte Facebook connecté : connectez-le dans Intégrations › Comptes connectés, ou collez un jeton de Page dans les options avancées.");
        if (!TokenPattern.IsMatch(token)) throw new InvalidOperationException("Jeton d'accès invalide.");
        var count = rt.GetInt("count", 6, 1, 12);
        var key = $"{page}|{count}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))[..16]}";   // le jeton fait partie de la clé : un autre client n'hérite pas des publications d'un autre
        (DateTime At, FacebookPage Page) hit = default;
        if (!fresh && _cache.TryGetValue(key, out hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(15)) return hit.Page;
        try
        {
            var url = $"{GraphUrl}{page}/published_posts?fields={Uri.EscapeDataString(Fields)}&limit={count}";
            var (status, body) = await http.Request(HttpMethod.Get, url, headers: new Dictionary<string, string> { ["Authorization"] = "OAuth " + token });
            if (status != 200)
            {
                if (fromAccount && ErrorCode(body) == 190) account.MarkLost(rt.Client.Id);   // accès retiré chez Facebook : le compte passe en « à reconnecter »
                throw new InvalidOperationException(Describe(status, body));
            }
            var feed = Parse(body);
            _cache[key] = (DateTime.UtcNow, feed);
            return feed;
        }
        catch when (!fresh && hit.Page != null) { return hit.Page; }
    }

    public async Task<object?> Fetch(AppRuntime rt)
    {
        var page = await Load(rt);
        var media = rt.Get("showMedia", "true") != "false";
        return new
        {
            kind = "facebook", page = page.Name, fetchedAt = DateTime.UtcNow,
            posts = page.Posts.Select(p => new { id = p.Id, message = p.Message, created = p.Created, picture = media ? p.Picture : null })
        };
    }

    public async Task<string> Test(AppRuntime rt)
    {
        var page = await Load(rt, fresh: true);
        if (page.Posts.Count == 0) return $"⚠ Connexion réussie, mais « {page.Name} » n'a aucune publication lisible pour le moment.";
        var first = page.Posts[0].Message ?? "(photo)";
        return $"Page lue : « {page.Name} » · {page.Posts.Count} publication(s) · la plus récente : {(first.Length > 60 ? first[..57] + "…" : first)}";
    }

    /// <summary>Identifiant de la Page à partir de ce que le client colle : identifiant, nom ou adresse (facebook.com/Nom, /pages/Nom/123, profile.php?id=123). Null si rien d'exploitable.</summary>
    public static string? PageRef(string? input)
    {
        var v = (input ?? "").Trim().TrimStart('@');
        if (v.Length == 0) return null;
        if (v.Contains('/') || v.Contains('?'))
        {
            if (!Uri.TryCreate(v.Contains("://") ? v : "https://" + v, UriKind.Absolute, out var u)) return null;
            var host = u.Host.ToLowerInvariant();
            if (host is not ("facebook.com" or "fb.com") && !host.EndsWith(".facebook.com")) return null;
            var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            v = parts[0].Equals("profile.php", StringComparison.OrdinalIgnoreCase) ? System.Web.HttpUtility.ParseQueryString(u.Query)["id"] ?? ""
              : parts[0].Equals("pages", StringComparison.OrdinalIgnoreCase) ? parts.LastOrDefault(p => p.All(char.IsDigit)) ?? ""
              : parts[0];
        }
        return PagePattern.IsMatch(v) ? v : null;
    }

    /// <summary>Code d'erreur Meta d'une réponse de l'API Graph (190 : jeton expiré ou retiré), ou null si la réponse n'est pas une erreur lisible.</summary>
    public static int? ErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c) && c.TryGetInt32(out var n)) return n;
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Explique une erreur de l'API Graph (code d'erreur Meta) sans reprendre son texte brut.</summary>
    public static string Describe(int status, string body)
    {
        return ErrorCode(body) switch
        {
            190 => "Le jeton d'accès est expiré ou invalide : générez-en un nouveau pour la Page.",
            10 or >= 200 and <= 299 => "Ce jeton n'a pas l'autorisation de lire les publications de la Page (permission pages_read_engagement).",
            100 or 803 => "Page introuvable : vérifiez son identifiant, et que le jeton est bien celui de cette Page.",
            4 or 17 or 32 or 613 => "Facebook limite temporairement les requêtes : réessayez dans quelques minutes.",
            _ => $"Facebook répond avec une erreur (HTTP {status})."
        };
    }

    /// <summary>Lit la réponse de /published_posts. Une publication sans texte ni photo (partage vide, événement) est ignorée ; seules les photos en https sont gardées.</summary>
    public static FacebookPage Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var posts = new List<FacebookPost>();
        string? name = null;
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var e in data.EnumerateArray())
            {
                string? Str(string prop) => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (name == null && e.TryGetProperty("from", out var from) && from.TryGetProperty("name", out var fn) && fn.ValueKind == JsonValueKind.String) name = fn.GetString();
                var message = Clean(Str("message"));
                var picture = Str("full_picture") is { } p && p.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && AppCatalog.IsHttpUrl(p) ? p : null;
                if (message == null && picture == null) continue;
                var created = DateTimeOffset.TryParse(Str("created_time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : (DateTime?)null;
                posts.Add(new FacebookPost(Str("id") ?? "", message, created, picture));
            }
        name = name?.Trim();
        return new FacebookPage(string.IsNullOrEmpty(name) ? "Facebook" : name.Length > 100 ? name[..100] : name, posts);
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Regex.Replace(text, @"\s+", " ").Trim();
        return t.Length > 400 ? t[..399].TrimEnd() + "…" : t;
    }
}
