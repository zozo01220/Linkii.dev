using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>Une publication d'une page LinkedIn, telle que lue par l'API Posts (texte nettoyé, médias en urn).</summary>
public record LinkedinPost(string Id, string Text, DateTime? Created, string? ImageUrn, string? VideoUrn, string? ArticleTitle)
{
    /// <summary>Adresse publique de la publication (celle du QR code).</summary>
    public string Url => "https://www.linkedin.com/feed/update/" + Id + "/";
}

/// <summary>Une publication prête pour l'écran : médias copiés sur le serveur (adresses /li-media/…), nombre de réactions et de commentaires.</summary>
public record LinkedinFeedPost(LinkedinPost Post, string? Image, string? Video, int? Likes, int? Comments);

/// <summary>
/// Copies des photos et vidéos des publications LinkedIn (leurs adresses chez LinkedIn expirent) : dossier linkedin-media/{organisation}/
/// du dossier de données, servies par /li-media/{fichier} au back-office et aux écrans de l'organisation. Nom : {organisation}-{empreinte}.{ext}.
/// </summary>
public class LinkedinMedia(IConfiguration config, IWebHostEnvironment env, SafeHttp http, ILogger<LinkedinMedia> log)
{
    private readonly string root = Path.Combine(config["Linkii:DataDir"] ?? env.ContentRootPath, "linkedin-media");
    private readonly ConcurrentDictionary<string, Task<string?>> running = new();
    public static readonly Regex FilePattern = new(@"^([0-9a-f]{32})-([0-9a-f]{16})\.(jpg|png|webp|gif|mp4)$");
    private const long MaxImage = 15_000_000, MaxVideo = 300_000_000;

    public static string Key(string urn) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(urn)))[..16].ToLowerInvariant();

    /// <summary>Chemin du fichier sur le disque, ou null si le nom n'est pas celui d'une copie.</summary>
    public string? PathOf(string file)
    {
        var m = FilePattern.Match(file);
        return m.Success ? Path.Combine(root, m.Groups[1].Value, file) : null;
    }

    /// <summary>Organisation propriétaire d'un fichier copié.</summary>
    public static Guid? OwnerOf(string file) => FilePattern.Match(file) is { Success: true } m && Guid.TryParseExact(m.Groups[1].Value, "N", out var g) ? g : null;

    /// <summary>Adresse de la copie si elle existe déjà sur le disque.</summary>
    public string? Existing(Guid client, string urn)
    {
        var dir = Path.Combine(root, client.ToString("N"));
        if (!Directory.Exists(dir)) return null;
        var hit = Directory.EnumerateFiles(dir, $"{client:N}-{Key(urn)}.*").FirstOrDefault();
        return hit == null ? null : "/li-media/" + Path.GetFileName(hit);
    }

    /// <summary>
    /// Copie (une seule fois) le fichier d'un média LinkedIn. downloadUrl : lit l'adresse de téléchargement chez LinkedIn (null si refusée).
    /// wait=false : la copie se fait en arrière-plan (vidéos) et null est rendu tant qu'elle n'est pas finie.
    /// </summary>
    public async Task<string?> Copy(Guid client, string urn, bool video, Func<Task<string?>> downloadUrl, bool wait = true)
    {
        if (Existing(client, urn) is { } done) return done;
        var key = client.ToString("N") + "|" + urn;
        var task = running.GetOrAdd(key, _ => Task.Run(async () =>
        {
            try { return await Download(client, urn, video, downloadUrl); }
            finally { running.TryRemove(key, out Task<string?>? _); }
        }));
        return wait ? await task : null;
    }

    private async Task<string?> Download(Guid client, string urn, bool video, Func<Task<string?>> downloadUrl)
    {
        try
        {
            if (await downloadUrl() is not { } url || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
            using var res = await http.Download(url, CancellationToken.None);
            if (!res.IsSuccessStatusCode) return null;
            var type = res.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            var ext = type switch { "image/jpeg" or "image/jpg" => "jpg", "image/png" => "png", "image/webp" => "webp", "image/gif" => "gif", "video/mp4" => "mp4", _ => null };
            if (ext == null || (ext == "mp4") != video) return null;
            var max = video ? MaxVideo : MaxImage;
            if (res.Content.Headers.ContentLength > max) return null;
            var dir = Path.Combine(root, client.ToString("N"));
            Directory.CreateDirectory(dir);
            var file = $"{client:N}-{Key(urn)}.{ext}";
            var tmp = Path.Combine(dir, file + ".part");
            await using (var src = await res.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[81920];
                long total = 0; int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    total += n;
                    if (total > max) { dst.Close(); File.Delete(tmp); return null; }
                    await dst.WriteAsync(buf.AsMemory(0, n));
                }
            }
            File.Move(tmp, Path.Combine(dir, file), true);
            return "/li-media/" + file;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            log.LogInformation(ex, "Média LinkedIn {Urn} non copié", urn);
            return null;
        }
    }

    /// <summary>Supprime les copies qu'aucune lecture n'a touchées depuis 45 jours (publications anciennes ou supprimées chez LinkedIn).</summary>
    public void Prune(DateTime now)
    {
        if (!Directory.Exists(root)) return;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { if (now - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(45) && now - File.GetLastAccessTimeUtc(f) > TimeSpan.FromDays(45)) File.Delete(f); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>
/// Application LinkedIn (§255) : publications d'une page entreprise, lues par l'API officielle avec le compte LinkedIn de l'organisation.
/// Le serveur garde les publications 15 minutes (une publication supprimée chez LinkedIn quitte les écrans à la lecture suivante), copie leurs
/// photos et vidéos, et n'envoie au player que texte, date, adresses /li-media/…, nombres de réactions et QR code. Le jeton ne quitte jamais le serveur.
/// Réglages communs (Applications › LinkedIn) : validation des nouvelles publications, réactions affichées, ancienneté maximale.
/// Les republications d'autres pages sont toujours écartées.
/// </summary>
public class LinkedinProvider(LinkedinAuth auth, LinkedinMedia media) : IAppProvider
{
    public string Id => "linkedin";
    private static readonly Regex OrgPattern = new(@"^urn:li:organization:\d{1,20}$");
    private readonly ConcurrentDictionary<string, (DateTime At, List<LinkedinFeedPost> Posts)> _cache = new();

    /// <summary>Publications récentes d'une page (jusqu'à 20), médias copiés ; dernière version connue si LinkedIn ne répond pas (sauf fresh).</summary>
    public async Task<List<LinkedinFeedPost>> Load(Tenant t, string urn, bool fresh = false)
    {
        if (!OrgPattern.IsMatch(urn)) throw new InvalidOperationException("Choisissez la page LinkedIn à afficher.");
        if (t.LinkedinPages.FirstOrDefault(p => p.Urn == urn) is not { CanRead: true })
            throw new InvalidOperationException("Cette page n'est pas lisible avec le compte LinkedIn connecté : choisissez une autre page, ou reconnectez un compte administrateur ou éditeur de contenu.");
        var key = t.Id.ToString("N") + "|" + urn;
        (DateTime At, List<LinkedinFeedPost> Posts) hit = default;
        if (!fresh && _cache.TryGetValue(key, out hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(15)) return hit.Posts;
        try
        {
            var token = await auth.AccessToken(t) ?? throw new InvalidOperationException(
                t.LinkedinConnectedUtc == null ? "Aucun compte LinkedIn connecté : ajoutez LinkedIn depuis le Store." : "La connexion LinkedIn a expiré : reconnectez le compte dans Applications › LinkedIn.");
            var (status, body) = await auth.Get("/rest/posts?author=" + Uri.EscapeDataString(urn) + "&q=author&count=20&sortBy=CREATED", token, "FINDER");
            if (status != 200)
            {
                if (status == 401) auth.MarkLost(t.Id);
                throw new InvalidOperationException(LinkedinAuth.Describe(status));
            }
            var list = new List<LinkedinFeedPost>();
            foreach (var p in ParsePosts(body)) list.Add(await Enrich(t, token, p));
            _cache[key] = (DateTime.UtcNow, list);
            return list;
        }
        catch when (!fresh && hit.Posts != null) { return hit.Posts; }
    }

    private async Task<LinkedinFeedPost> Enrich(Tenant t, string token, LinkedinPost p)
    {
        string? image = null, video = null;
        if (p.ImageUrn != null)
            image = await media.Copy(t.Id, p.ImageUrn, false, () => DownloadUrl(token, "/rest/images/", p.ImageUrn, "downloadUrl"));
        if (p.VideoUrn != null)
        {
            image ??= await media.Copy(t.Id, p.VideoUrn + "#poster", false, () => DownloadUrl(token, "/rest/videos/", p.VideoUrn, "thumbnail"));
            video = await media.Copy(t.Id, p.VideoUrn, true, () => DownloadUrl(token, "/rest/videos/", p.VideoUrn, "downloadUrl"), wait: false);
        }
        var (likes, comments) = await Counts(token, p.Id);
        return new LinkedinFeedPost(p, image, video, likes, comments);
    }

    private async Task<string?> DownloadUrl(string token, string path, string urn, string field)
    {
        var (status, body) = await auth.Get(path + Uri.EscapeDataString(urn), token);
        if (status != 200) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(field, out var u) && u.ValueKind == JsonValueKind.String && u.GetString() is { } url && AppCatalog.IsHttpUrl(url) ? url : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Nombre de réactions et de commentaires d'une publication (null si LinkedIn ne les donne pas).</summary>
    private async Task<(int?, int?)> Counts(string token, string postId)
    {
        try
        {
            var (status, body) = await auth.Get("/rest/socialActions/" + Uri.EscapeDataString(postId), token);
            return status == 200 ? ParseCounts(body) : (null, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (null, null); }
    }

    public static (int? Likes, int? Comments) ParseCounts(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            int? likes = r.TryGetProperty("likesSummary", out var l) && l.TryGetProperty("totalLikes", out var tl) && tl.TryGetInt32(out var nl) ? nl : null;
            int? comments = null;
            if (r.TryGetProperty("commentsSummary", out var c))
                comments = c.TryGetProperty("aggregatedTotalComments", out var ac) && ac.TryGetInt32(out var na) ? na
                         : c.TryGetProperty("totalFirstLevelComments", out var fc) && fc.TryGetInt32(out var nf) ? nf : null;
            return (likes, comments);
        }
        catch (JsonException) { return (null, null); }
    }

    /// <summary>Publications d'une page avec leur état pour l'organisation (affichée, à valider, masquée), pour la fenêtre du contenu.</summary>
    public async Task<List<(LinkedinFeedPost Post, string State)>> WithStates(Tenant t, string urn, bool fresh = false)
    {
        var page = t.LinkedinPages.FirstOrDefault(p => p.Urn == urn);
        return (await Load(t, urn, fresh)).Select(p => (p, LinkedinAuth.StateOf(t, page, p.Post.Id))).ToList();
    }

    /// <summary>Publications montrées sur l'écran : affichées (validées), pas plus anciennes que l'ancienneté maximale, les plus récentes d'abord.</summary>
    public static List<LinkedinFeedPost> Visible(Tenant t, string urn, IEnumerable<LinkedinFeedPost> posts, int count, DateTime now)
    {
        var page = t.LinkedinPages.FirstOrDefault(p => p.Urn == urn);
        var maxAge = t.LinkedinMaxAgeDays;
        return posts.Where(p => maxAge <= 0 || p.Post.Created is not { } d || now - d <= TimeSpan.FromDays(maxAge))
            .Where(p => LinkedinAuth.StateOf(t, page, p.Post.Id) == LinkedinAuth.Shown)
            .Take(count).ToList();
    }

    public async Task<object?> Fetch(AppRuntime rt)
    {
        var t = rt.Client;
        var urn = rt.Get("page");
        var all = await Load(t, urn);
        var shown = Visible(t, urn, all, rt.GetInt("count", 5, 1, 10), DateTime.UtcNow);
        var qr = rt.Get("qr", "true") != "false";
        var name = t.LinkedinPages.FirstOrDefault(p => p.Urn == urn)?.Name ?? "LinkedIn";
        return new
        {
            kind = "linkedin", page = name, initials = Initials(name), reactions = t.LinkedinReactions, fetchedAt = DateTime.UtcNow,
            posts = shown.Select(p => new
            {
                id = p.Post.Id, text = p.Post.Text, created = p.Post.Created, image = p.Image, video = p.Video,
                likes = t.LinkedinReactions ? p.Likes : null, comments = t.LinkedinReactions ? p.Comments : null,
                qr = qr ? QrCodes.Encode(p.Post.Url) : null
            })
        };
    }

    public async Task<string> Test(AppRuntime rt)
    {
        var t = rt.Client;
        var urn = rt.Get("page");
        var all = await WithStates(t, urn, fresh: true);
        var name = t.LinkedinPages.FirstOrDefault(p => p.Urn == urn)?.Name ?? "la page";
        var shown = Visible(t, urn, all.Select(a => a.Post), 10, DateTime.UtcNow).Count;
        var waiting = all.Count(a => a.State == LinkedinAuth.Waiting);
        var msg = $"Page lue : « {name} » · {all.Count} publication{(all.Count > 1 ? "s" : "")} · {shown} affichée{(shown > 1 ? "s" : "")}"
                  + (waiting > 0 ? $" · {waiting} à valider" : "");
        var days = LinkedinAuth.DaysLeft(t, DateTime.UtcNow);
        if (days is <= 7) return $"⚠ {msg}. La connexion LinkedIn expire dans {Math.Max(0, days.Value)} jour{(days > 1 ? "s" : "")} : reconnectez le compte.";
        if (all.Count == 0) return $"⚠ Connexion réussie, mais « {name} » n'a aucune publication lisible pour le moment.";
        return shown == 0 && waiting > 0 ? $"⚠ {msg} : validez-les pour qu'elles passent à l'écran." : msg;
    }

    public static string Initials(string name)
    {
        var words = name.Split([' ', '-', '–', '·'], StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).Take(2).ToList();
        return words.Count == 0 ? "in" : string.Concat(words.Select(w => char.ToUpperInvariant(w[0])));
    }

    /// <summary>
    /// Lit la réponse du finder « publications par auteur » : publications publiées, publiques, visibles dans le fil, écrites par la page.
    /// Écartées : republications d'autres pages (reshareContext), publications sponsorisées « dark », brouillons, et publications sans texte ni média.
    /// </summary>
    public static List<LinkedinPost> ParsePosts(string json)
    {
        var list = new List<LinkedinPost>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("elements", out var els) || els.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in els.EnumerateArray())
        {
            static string? Str(JsonElement x, string name) => x.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var id = Str(e, "id");
            if (id == null || !LinkedinAuth.PostPattern.IsMatch(id)) continue;
            if (Str(e, "lifecycleState") is { } ls && ls != "PUBLISHED") continue;
            if (Str(e, "visibility") is { } vis && vis != "PUBLIC") continue;
            if (e.TryGetProperty("distribution", out var dist) && Str(dist, "feedDistribution") == "NONE") continue;
            if (e.TryGetProperty("reshareContext", out var rc) && rc.ValueKind == JsonValueKind.Object) continue;

            string? image = null, video = null, articleTitle = null;
            if (e.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Object)
            {
                if (c.TryGetProperty("media", out var m) && Str(m, "id") is { } mid)
                {
                    if (mid.StartsWith("urn:li:image:")) image = mid;
                    else if (mid.StartsWith("urn:li:video:")) video = mid;
                }
                if (c.TryGetProperty("multiImage", out var mi) && mi.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0
                    && Str(imgs[0], "id") is { } fid && fid.StartsWith("urn:li:image:")) image ??= fid;
                if (c.TryGetProperty("article", out var art) && art.ValueKind == JsonValueKind.Object)
                {
                    articleTitle = Str(art, "title");
                    if (Str(art, "thumbnail") is { } th && th.StartsWith("urn:li:image:")) image ??= th;
                }
            }
            if (image != null && !Regex.IsMatch(image, @"^urn:li:image:[A-Za-z0-9_-]{1,100}$")) image = null;
            if (video != null && !Regex.IsMatch(video, @"^urn:li:video:[A-Za-z0-9_-]{1,100}$")) video = null;
            var text = CleanText(Str(e, "commentary") ?? "");
            if (text.Length == 0 && articleTitle is { Length: > 0 }) text = articleTitle.Trim();
            if (text.Length == 0 && image == null && video == null) continue;
            DateTime? created = e.TryGetProperty("publishedAt", out var pa) && pa.TryGetInt64(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                              : e.TryGetProperty("createdAt", out var ca) && ca.TryGetInt64(out var cms) ? DateTimeOffset.FromUnixTimeMilliseconds(cms).UtcDateTime : null;
            list.Add(new LinkedinPost(id, text, created, image, video, articleTitle));
        }
        return list.OrderByDescending(p => p.Created ?? DateTime.MinValue).ToList();
    }

    /// <summary>Texte d'une publication : mentions « @[Nom](urn:…) » ramenées au nom, gabarits « {hashtag|\#|mot} » à « #mot », 1 200 caractères au plus.</summary>
    public static string CleanText(string s)
    {
        s = Regex.Replace(s, @"@\[([^\]]*)\]\([^)]*\)", "$1");
        s = Regex.Replace(s, @"\{hashtag\|\\?#\|([^}]*)\}", "#$1");
        s = s.Replace("\\#", "#").Replace("\\(", "(").Replace("\\)", ")").Replace("\\*", "*").Replace("\\_", "_").Replace("\r", "");
        s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
        return s.Length > 1200 ? s[..1199].TrimEnd() + "…" : s;
    }
}
