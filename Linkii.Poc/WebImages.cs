using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>Une photo trouvée dans la banque d'images : miniature, fichier à importer, ce qu'elle montre (texte de la banque), photographe et page de la photo.</summary>
public record WebImage(string Id, string Thumb, string File, string Alt, string Author, string Page, int Width, int Height);

/// <summary>
/// Images d'illustration d'internet (banque Pexels, une clé pour toute la plateforme dans la console) : quand l'IA crée une composition et que la médiathèque
/// n'a pas d'image qui convienne, on cherche une photo neutre — « impersonnelle » : objets, lieux, ambiances ; une photo dont la description parle de personnes
/// est écartée — puis on l'importe dans la médiathèque avec le crédit du photographe. Licence Pexels : usage commercial permis, crédit apprécié,
/// pas de revente telle quelle. Les adresses appelées sont fixes (API et serveur d'images de Pexels, ou le serveur de test <c>Linkii:WebImages:BaseUrl</c>),
/// jamais saisies par un client ; les mots cherchés ne contiennent que le sujet de l'image.
/// </summary>
public class WebImages(JsonStore root, SecretBox box, MediaImporter importer, IConfiguration config, ILogger<WebImages> log)
{
    public const string Provider = "Pexels";
    public const string ProviderUrl = "https://www.pexels.com";
    private const long MaxBytes = 15_000_000;
    // redirections refusées : l'image vient de l'hôte contrôlé, pas d'ailleurs
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8) }) { Timeout = TimeSpan.FromSeconds(25) };

    private string? TestBase => config["Linkii:WebImages:BaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : null;
    private string Base => TestBase ?? "https://api.pexels.com";

    /// <summary>Hôtes d'où une image peut être téléchargée : le serveur d'images de Pexels, ou le serveur de test configuré.</summary>
    private bool AllowedImage(Uri u) =>
        u.Scheme == "https" && u.Host == "images.pexels.com"
        || TestBase is { } b && Uri.TryCreate(b, UriKind.Absolute, out var bu) && u.Host == bu.Host && u.Port == bu.Port;

    public const string Off = "Les images d'internet ne sont pas disponibles.";

    /// <summary>Une clé est enregistrée dans la console.</summary>
    public bool Configured => root.Read(d => d.Platform.WebImagesKey != "");

    /// <summary>L'organisation reçoit des images d'internet : clé de la plateforme et réglage de l'organisation (Réglages › Intelligence artificielle).</summary>
    public bool On(Guid clientId) => root.Read(d => d.Platform.WebImagesKey != "" && d.Clients.FirstOrDefault(c => c.Id == clientId)?.AiWebImagesOn == true);

    private string? Key()
    {
        var stored = root.Read(d => d.Platform.WebImagesKey);
        if (stored.Length == 0) return null;
        try { return box.Unprotect(stored); }
        catch (Exception e) { log.LogWarning(e, "Clé de la banque d'images illisible sur ce serveur."); return null; }
    }

    // ---------- Photo « impersonnelle » ----------

    private static readonly Regex People = new(
        @"\b(man|men|woman|women|person|persons|people|boy|boys|girl|girls|child|children|kid|kids|baby|babies|toddler|teen|teens|teenager|teenagers|couple|couples|family|guy|guys|lady|ladies|gentleman|crowd|portrait|selfie|student|students|worker|workers|businessman|businesswoman|businesspeople|team|friends|bride|groom|face|faces|"
        + @"homme|hommes|femme|femmes|personne|personnes|gens|enfant|enfants|garçon|garçons|fille|filles|bébé|bébés|famille|foule|visage|visages)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>La photo est « impersonnelle » : sa description ne parle d'aucune personne. Sans description, on ne sait pas ce qu'elle montre : écartée aussi.</summary>
    public static bool Impersonal(string alt) => (alt ?? "").Trim().Length > 0 && !People.IsMatch(alt!);

    // ---------- Recherche ----------

    /// <summary>Lit la réponse d'une recherche Pexels (<c>photos</c>, <c>src</c>, <c>alt</c>, <c>photographer</c>, <c>url</c>).</summary>
    public static List<WebImage> Parse(string json)
    {
        var list = new List<WebImage>();
        if (JsonNode.Parse(json) is not JsonObject o || o["photos"] is not JsonArray photos) return list;
        static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        static int I(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        foreach (var p in photos.OfType<JsonObject>())
        {
            var src = p["src"] as JsonObject;
            var file = S(src?["large2x"]) is { Length: > 0 } l2 ? l2 : S(src?["large"]);
            if (file.Length == 0) continue;
            list.Add(new WebImage(p["id"]?.ToJsonString().Trim('"') ?? "", S(src?["medium"]), file, S(p["alt"]), S(p["photographer"]), S(p["url"]), I(p["width"]), I(p["height"])));
        }
        return list;
    }

    /// <summary>Cherche des photos d'illustration (en français, dans le format de l'écran) et ne garde que les impersonnelles.</summary>
    public async Task<(List<WebImage> Found, string? Error)> Search(string query, bool portrait, CancellationToken ct = default, string locale = "fr-FR")
    {
        query = Regex.Replace(query ?? "", @"\s+", " ").Trim();
        if (query.Length < 2) return (new(), "Indiquez ce que l'image doit montrer.");
        if (query.Length > 80) query = query[..80];
        if (Key() is not { } key) return (new(), Off);
        var url = $"{Base}/v1/search?query={Uri.EscapeDataString(query)}&orientation={(portrait ? "portrait" : "landscape")}&size=medium&locale={locale}&per_page=20";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", key);
            using var res = await Http.SendAsync(req, ct);
            var code = (int)res.StatusCode;
            if (code is 401 or 403) return (new(), "La banque d'images refuse la clé enregistrée dans la console.");
            if (code == 429) return (new(), "Trop de recherches d'images pour le moment : réessayez dans une heure.");
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > 3_000_000) return (new(), "La banque d'images ne répond pas. Réessayez plus tard.");
            return (Parse(await res.Content.ReadAsStringAsync(ct)).Where(i => Impersonal(i.Alt)).ToList(), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            log.LogInformation(e, "Recherche d'images en échec.");
            return (new(), "La banque d'images ne répond pas. Réessayez plus tard.");
        }
    }

    /// <summary>La vignette d'une photo trouvée (2 Mo au plus), pour que l'IA la regarde avant de la choisir ; null si elle ne se télécharge pas.</summary>
    public async Task<byte[]?> Thumb(WebImage img, CancellationToken ct = default)
    {
        var address = img.Thumb.Length > 0 ? img.Thumb : img.File;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var u) || !AllowedImage(u)) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var res = await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > 2_000_000) return null;
            using var ms = new MemoryStream();
            await using var s = await res.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[64 * 1024];
            int n;
            while ((n = await s.ReadAsync(buf, cts.Token)) > 0) { ms.Write(buf, 0, n); if (ms.Length > 2_000_000) return null; }
            return ms.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested) { return null; }
    }

    // ---------- Import dans la médiathèque ----------

    /// <summary>Importe la photo dans la médiathèque (aire et membre donnés), avec le crédit du photographe. L'IA la décrit ensuite comme toute image importée.</summary>
    public async Task<(MediaItem? Item, string? Error)> Import(Guid clientId, WebImage img, string name, Guid? areaId, Guid? ownerId, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(img.File, UriKind.Absolute, out var u) || !AllowedImage(u)) return (null, "Adresse d'image inattendue.");
        // déjà dans la médiathèque (même photo de la banque) : reprise telle quelle, pas de doublon
        if (img.Page.Length > 0 && root.Read(d => d.Media.FirstOrDefault(m => m.ClientId == clientId && m.CreditUrl == img.Page && !m.Off && m.FileName != null && (areaId == null || m.AreaId == areaId))) is { } known)
            return (known, null);
        try
        {
            using var res = await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return (null, "L'image n'a pas pu être téléchargée.");
            if (res.Content.Headers.ContentLength > MaxBytes) return (null, "Image trop volumineuse.");
            using var ms = new MemoryStream();
            await using (var s = await res.Content.ReadAsStreamAsync(ct))
            {
                var buf = new byte[64 * 1024];
                int n;
                while ((n = await s.ReadAsync(buf, ct)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > MaxBytes) return (null, "Image trop volumineuse.");
                }
            }
            ms.Position = 0;
            var ext = res.Content.Headers.ContentType?.MediaType switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
            var r = await importer.ImportDownloaded(ms, FileName(name) + ext, clientId, areaId, ownerId, ct);
            if (r.Item is not { } item) return (null, r.Error ?? "L'image n'a pas pu être ajoutée à la médiathèque.");
            var credit = img.Author.Trim().Length > 0 ? $"Photo : {img.Author.Trim()} sur {Provider}" : "Photo : " + Provider;
            var page = img.Page.StartsWith("https://", StringComparison.Ordinal) ? img.Page : ProviderUrl;
            root.Write(d => { if (d.Media.FirstOrDefault(m => m.Id == item.Id) is { } m) { m.Credit = credit; m.CreditUrl = page; } });
            item.Credit = credit; item.CreditUrl = page;
            return (item, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            log.LogInformation(e, "Import d'une image d'internet en échec.");
            return (null, "L'image n'a pas pu être téléchargée.");
        }
    }

    /// <summary>Cherche puis importe la première photo impersonnelle trouvée (nommée d'après ce qu'elle doit montrer).</summary>
    public async Task<(MediaItem? Item, string? Error)> FindAndImport(Guid clientId, string query, bool portrait, Guid? areaId, Guid? ownerId, CancellationToken ct = default)
    {
        if (!On(clientId)) return (null, Off);
        var (found, err) = await Search(query, portrait, ct);
        if (err != null) return (null, err);
        if (found.Count == 0) return (null, $"Aucune photo neutre trouvée pour « {query} ».");
        return await Import(clientId, found[0], Title(query), areaId, ownerId, ct);
    }

    /// <summary>Console : une petite recherche pour vérifier la clé.</summary>
    public async Task<(bool Ok, string Message)> Test()
    {
        if (Key() == null) return (false, "Aucune clé enregistrée.");
        var (found, err) = await Search("nature", false);
        return err != null ? (false, err) : (true, $"Connexion réussie : {found.Count} photo(s) trouvée(s) pour « nature ».");
    }

    private static string Title(string query)
    {
        var q = Regex.Replace(query.Trim(), @"\s+", " ");
        return q.Length == 0 ? "Image d'internet" : char.ToUpper(q[0]) + q[1..];
    }

    private static string FileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? ' ' : c).ToArray()).Trim();
        return s.Length == 0 ? "Image d'internet" : s.Length > 80 ? s[..80] : s;
    }
}
