using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic.Models.Messages;
using SkiaSharp;

namespace Linkii.Poc;

/// <summary>Ce que l'assistant a lu sur le site : textes, couleurs repérées, logos possibles (adresses absolues) et les images assez grandes,
/// réduites, que l'IA regarde avant de choisir les photos des pages (<see cref="Images"/> : une par image regardée, dans le même ordre).</summary>
public sealed record SiteRead(Uri Home, string Host, int Pages, string Title, string SiteName, string Description, string? Color, List<string> Colors,
    List<string> Headings, string Text, List<string> Logos, List<SiteImage> Images);

/// <summary>Une image du site : adresse, texte alternatif, taille d'origine et copie réduite (JPEG en base 64) montrée à l'IA.</summary>
public sealed record SiteImage(string Url, string Alt, int W, int H, string Jpeg);

/// <summary>Choix de l'IA parmi les candidats du site (logo, photos) et recherches de photos neutres pour compléter.</summary>
public sealed record StartPick(int? Logo, List<int> Photos, List<string> Queries)
{
    public static readonly StartPick None = new(null, new(), new());
}

/// <summary>L'écran préparé par l'assistant : son contenu, son format, la composition de chaque page, la météo et la liste.</summary>
public sealed class StartState
{
    public StartBrief Brief { get; set; } = new();
    public bool Portrait { get; set; }
    public bool Bilingual { get; set; }
    public Dictionary<string, Guid> Pages { get; } = new();
    public Dictionary<string, Guid> EnPages { get; } = new();
    public Guid? Weather { get; set; }
    public Guid? ListId { get; set; }

    public record Snapshot(StartBrief Brief, bool Portrait, bool Bilingual);
    public Snapshot Save() => new(Brief.Clone(), Portrait, Bilingual);
    public void Restore(Snapshot s) { Brief = s.Brief.Clone(); Portrait = s.Portrait; Bilingual = s.Bilingual; }

    /// <summary>Contenus de la liste, dans l'ordre : chaque page suivie de sa version anglaise, la météo avant l'actualité.</summary>
    public List<Guid> Order()
    {
        var ids = new List<Guid>();
        foreach (var k in StartTemplates.Kinds)
        {
            if (k == "news" && Weather is { } w) ids.Add(w);
            if (Pages.TryGetValue(k, out var id)) ids.Add(id);
            if (Bilingual && EnPages.TryGetValue(k, out var en)) ids.Add(en);
        }
        if (Weather is { } w2 && !ids.Contains(w2)) ids.Insert(Math.Min(ids.Count, 3), w2);
        return ids;
    }
}

/// <summary>
/// Premiers pas avec l'assistant (DEV-POC §232) : lit le site (ou la phrase), demande à l'IA le contenu de l'écran, importe logo et photos,
/// dessine les pages (<see cref="StartTemplates"/>), ajoute la météo si la ville est trouvée et monte la liste « Accueil ».
/// Agit avec les droits de la personne connectée (service par session). Rien n'est publié sur un écran.
/// </summary>
public sealed class AiStarter(TenantStore store, AppService svc, AiService ai, SafeHttp http, MediaImporter importer, MediaLibrary library,
    WebImages webImages, WeatherService weather, JsonStore root, IWebHostEnvironment env, ILogger<AiStarter> log)
{
    public const int PageSeconds = 10;
    public const string ListName = "Accueil";
    public const int MaxAsk = 600;
    private static readonly Regex UrlLike = new(@"^\s*(https?://)?(www\.)?[a-z0-9-]+(\.[a-z0-9-]+)*\.[a-z]{2,}(/\S*)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HexColor = new(@"#[0-9a-fA-F]{6}\b", RegexOptions.Compiled);

    public static bool LooksLikeUrl(string? s) => s != null && !s.Trim().Contains(' ') && UrlLike.IsMatch(s);

    /// <summary>L'assistant peut tourner : IA disponible pour l'organisation (essai ou Pro, quota, interrupteurs) et compositions utilisables.</summary>
    public bool Available() => ai.State(store.ClientId, "compose").Available && (svc.IsInstalled(Compositions.AppId) || store.Read(d => d.Access.IsOrgAdmin));

    private bool Ready(string appId)
    {
        if (svc.IsInstalled(appId)) return true;
        if (!store.Read(d => d.Access.IsOrgAdmin)) return false;
        svc.Install(appId);
        return true;
    }

    // ---------- 1. Lire le site ----------

    public async Task<(SiteRead? Site, string? Error)> ReadSite(string address, CancellationToken ct)
    {
        Uri home;
        string html;
        try
        {
            home = SafeHttp.ParseUrl(address.Contains("://") ? address.Trim() : "https://" + address.Trim());
            html = await http.GetString(home.AbsoluteUri, 1_500_000, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is InvalidOperationException or HttpRequestException or UriFormatException or TaskCanceledException or ArgumentException)
        {
            log.LogInformation(e, "Site non lu pour les Premiers pas.");
            return (null, "Je n'ai pas pu lire ce site. Vérifiez l'adresse, ou décrivez votre activité en une phrase.");
        }
        var host = home.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? home.Host[4..] : home.Host;
        var main = Page(html, home);
        // quelques pages utiles en plus : contact, à propos, services, horaires…
        var extra = Links(html, home).Take(3).ToList();
        var texts = await Task.WhenAll(extra.Select(async u =>
        {
            try { return Page(await http.GetString(u.AbsoluteUri, 800_000, ct), u); }
            catch (Exception e) when (!ct.IsCancellationRequested && e is InvalidOperationException or HttpRequestException or TaskCanceledException or ArgumentException) { return null; }
        }));
        var read = texts.Where(t => t != null).Select(t => t!).ToList();
        var text = Cap(main.Text, 3500) + string.Concat(read.Select(t => "\n\n[" + t.Title + "]\n" + Cap(t.Text, 1500)));
        if (text.Length < 40 && main.Title.Length == 0) return (null, "Ce site ne contient pas assez de texte lisible. Décrivez votre activité en une phrase.");
        var heads = main.Headings.Concat(read.SelectMany(t => t.Headings)).Distinct().Take(20).ToList();
        var images = main.Images.Concat(read.SelectMany(t => t.Images)).GroupBy(i => i.Url).Select(g => g.First()).Take(12).ToList();
        var logos = main.Logos.Concat(read.SelectMany(t => t.Logos)).Distinct().Take(5).ToList();
        var seen = await Look(images, 6, ct);
        return (new SiteRead(home, host, 1 + read.Count, main.Title, main.SiteName, main.Description, main.Color, main.Colors, heads, Cap(text, 8000), logos, seen), null);
    }

    /// <summary>
    /// Télécharge les images candidates (en parallèle, 8 Mo et 8 s au plus chacune), garde celles d'au moins 700 × 400 et les réduit (384 px)
    /// pour que l'IA les voie : elle écarte ainsi bannières, logos, captures et images où du texte est incrusté.
    /// </summary>
    private async Task<List<SiteImage>> Look(List<(string Url, string Alt)> candidates, int max, CancellationToken ct)
    {
        var found = await Task.WhenAll(candidates.Take(10).Select(async c =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                using var res = await http.Download(c.Url, cts.Token);
                if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > 8_000_000) return null;
                await using var s = await res.Content.ReadAsStreamAsync(cts.Token);
                using var ms = new MemoryStream();
                var buf = new byte[64 * 1024];
                int n;
                while ((n = await s.ReadAsync(buf, cts.Token)) > 0) { ms.Write(buf, 0, n); if (ms.Length > 8_000_000) return null; }
                ms.Position = 0;
                using var bmp = SKBitmap.Decode(ms);
                if (bmp == null || bmp.Width < 700 || bmp.Height < 400) return null;
                var k = 384.0 / Math.Max(bmp.Width, bmp.Height);
                using var small = bmp.Resize(new SKImageInfo(Math.Max(1, (int)(bmp.Width * k)), Math.Max(1, (int)(bmp.Height * k))), SKFilterQuality.Medium);
                if (small == null) return null;
                using var data = SKImage.FromBitmap(small).Encode(SKEncodedImageFormat.Jpeg, 70);
                return new SiteImage(c.Url, c.Alt, bmp.Width, bmp.Height, Convert.ToBase64String(data.ToArray()));
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogDebug(e, "Image du site non regardée : {Url}", c.Url);
                return null;
            }
        }));
        return found.OfType<SiteImage>().Take(max).ToList();
    }

    /// <summary>Ce qu'une page du site donne : titre, nom, description, couleurs, titres, texte, logos et images possibles.</summary>
    public sealed record PageInfo(string Title, string SiteName, string Description, string? Color, List<string> Colors, List<string> Headings, string Text, List<string> Logos, List<(string Url, string Alt)> Images);

    private static string Cap(string s, int n) => s.Length > n ? s[..n] : s;

    public static PageInfo Page(string html, Uri page)
    {
        string Meta(string key)
        {
            var m = Regex.Match(html, $"<meta[^>]+(?:property|name)=[\"']{Regex.Escape(key)}[\"'][^>]*>", RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            var c = Regex.Match(m.Value, "content=[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase);
            return c.Success ? WebUtility.HtmlDecode(c.Groups[1].Value).Trim() : "";
        }
        static string Clean(string s) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", " ")), @"\s+", " ").Trim();
        string? Abs(string? u)
        {
            u = WebUtility.HtmlDecode(u ?? "").Trim();
            if (u.Length == 0 || u.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
            return Uri.TryCreate(page, u, out var r) && r.Scheme is "http" or "https" ? r.AbsoluteUri : null;
        }
        static string? Attr(string tag, string name)
        {
            var m = Regex.Match(tag, $@"\b{name}\s*=\s*([""'])(.*?)\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return m.Success ? m.Groups[2].Value : null;
        }

        var title = Regex.Match(html, "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline) is { Success: true } t ? Clean(t.Groups[1].Value) : "";
        var body = Regex.Replace(html, @"<(script|style|noscript|svg|template)\b.*?</\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Regex.Replace(body, "<!--.*?-->", " ", RegexOptions.Singleline);
        var heads = Regex.Matches(body, @"<h[1-3][^>]*>(.*?)</h[1-3]>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Select(m => Clean(m.Groups[1].Value)).Where(s => s.Length is > 2 and < 140).Distinct().ToList();

        // couleurs les plus présentes dans les styles (hors blancs, noirs et gris)
        var styles = string.Concat(Regex.Matches(html, @"<style\b[^>]*>(.*?)</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Select(m => m.Groups[1].Value))
            + string.Concat(Regex.Matches(html, @"style\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value));
        var colors = HexColor.Matches(styles).Select(m => m.Value.ToUpperInvariant()).Where(Colorful)
            .GroupBy(c => c).OrderByDescending(g => g.Count()).Take(6).Select(g => g.Key).ToList();

        var logos = new List<string>();
        foreach (Match m in Regex.Matches(html, @"""logo""\s*:\s*(?:""([^""]+)""|\{[^}]*?""url""\s*:\s*""([^""]+)"")", RegexOptions.IgnoreCase))
            if (Abs(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) is { } u) logos.Add(u);
        var images = new List<(string Url, string Alt)>();
        if (Abs(Meta("og:image")) is { } og) images.Add((og, "image de partage du site"));
        if (Abs(Meta("twitter:image")) is { } tw && tw != images.FirstOrDefault().Url) images.Add((tw, "image de partage du site"));
        foreach (Match m in Regex.Matches(body, @"<img\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = m.Value;
            var src = Attr(tag, "data-src") ?? Attr(tag, "data-lazy-src") ?? Attr(tag, "src");
            if (Attr(tag, "srcset") is { } set && set.Split(',').LastOrDefault()?.Trim().Split(' ')[0] is { Length: > 0 } big) src = big;
            var alt = Clean(Attr(tag, "alt") ?? "");
            var hay = string.Join(' ', src, alt, Attr(tag, "class"), Attr(tag, "id")).ToLowerInvariant();
            if (Abs(src) is not { } url) continue;
            if (hay.Contains("logo")) { logos.Add(url); continue; }
            if (Regex.IsMatch(url, @"\.(svg|gif|ico)(\?|$)", RegexOptions.IgnoreCase) || Regex.IsMatch(hay, @"icon|sprite|avatar|pixel|tracking|badge|flag|picto|emoji")) continue;
            if (int.TryParse(Attr(tag, "width"), out var w) && w < 400) continue;
            images.Add((url, alt.Length > 80 ? alt[..80] : alt));
        }
        foreach (Match m in Regex.Matches(html, @"background(?:-image)?\s*:\s*url\(\s*['""]?([^'"")]+)['""]?\s*\)", RegexOptions.IgnoreCase))
            if (Abs(m.Groups[1].Value) is { } bg && !Regex.IsMatch(bg, @"\.(svg|gif|ico)(\?|$)|logo|icon|sprite", RegexOptions.IgnoreCase)) images.Add((bg, "image de fond"));
        foreach (Match m in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var rel = (Attr(m.Value, "rel") ?? "").ToLowerInvariant();
            if (!rel.Contains("icon") || Abs(Attr(m.Value, "href")) is not { } href || href.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.Contains("apple-touch-icon")) logos.Add(href);
            else if (Regex.IsMatch(href, @"\.(png|svg)(\?|$)", RegexOptions.IgnoreCase)) logos.Add(href);
        }
        var color = Meta("theme-color");
        var desc = Meta("og:description") is { Length: > 0 } d ? d : Meta("description");
        static string Twice(string s) => s.Contains('&') ? WebUtility.HtmlDecode(s) : s;   // certains sites encodent deux fois (&amp;#39;)
        desc = Twice(desc);
        return new PageInfo(Twice(Meta("og:title") is { Length: > 0 } ot ? ot : title), Twice(Meta("og:site_name")), desc.Length > 400 ? desc[..400] : desc,
            StartTemplates.Norm(color, "") is { Length: > 0 } c ? c : null, colors, heads, Clean(body), logos.Distinct().ToList(), images);
    }

    /// <summary>Une couleur qui dit quelque chose de la marque : ni trop claire, ni trop foncée, ni grise.</summary>
    private static bool Colorful(string hex)
    {
        int r = Convert.ToInt32(hex[1..3], 16), g = Convert.ToInt32(hex[3..5], 16), b = Convert.ToInt32(hex[5..7], 16);
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        return max - min > 40 && max > 40 && min < 235;
    }

    /// <summary>Pages du même site qui disent l'essentiel (contact, à propos, services, horaires, actualités).</summary>
    private static IEnumerable<Uri> Links(string html, Uri home)
    {
        var host = home.Host.Replace("www.", "");
        var seen = new HashSet<string> { home.AbsolutePath.TrimEnd('/') };
        var key = new Regex(@"contact|propos|about|qui-sommes|services|prestations|horaires|infos|acces|equipe|entreprise|actualit|news|offres|nous-trouver", RegexOptions.IgnoreCase);
        foreach (Match m in Regex.Matches(html, @"<a\b[^>]*href\s*=\s*[""']([^""'#]+)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            if (!Uri.TryCreate(home, WebUtility.HtmlDecode(m.Groups[1].Value.Trim()), out var u) || u.Scheme is not ("http" or "https") || u.Host.Replace("www.", "") != host) continue;
            if (Regex.IsMatch(u.AbsolutePath, @"\.(pdf|jpe?g|png|zip|docx?)$", RegexOptions.IgnoreCase)) continue;
            if (!key.IsMatch(u.AbsolutePath) && !key.IsMatch(Regex.Replace(m.Groups[2].Value, "<[^>]+>", ""))) continue;
            if (seen.Add(u.AbsolutePath.TrimEnd('/'))) yield return u;
        }
    }

    // ---------- 2. Le contenu de l'écran, par l'IA ----------

    private static string Rules => """
        Règles :
        - Écris en français, simplement, pour être lu de loin : kicker ≤ 28 caractères, title ≤ 42, sub ≤ 80, chaque point ≤ 40, label ≤ 20, value ≤ 32, note ≤ 40, tag ≤ 16.
        - N'invente rien : pas d'horaire, d'adresse, de téléphone, de prix, de date ni de nom qui ne soient dans les informations données. Une information pratique utile mais inconnue s'écrit entre crochets pour que la personne la complète, par exemple "[Vos horaires]".
        - "info" : seulement des informations utiles au visiteur (horaires, adresse, accès, contact, Wi-Fi…), 1 à 3 cartes ; « icon » parmi : horloge, position, telephone, courrier, wifi, parking, accessibilite, information, calendrier. null si rien d'utile.
        - "news" : une actualité, un événement ou une offre réellement annoncé, avec dans "link" l'adresse de la page qui en parle ; null s'il n'y en a pas.
        - "thanks" : une page de fin chaleureuse (« Merci de votre visite », « À bientôt »…).
        """;

    private static string BriefSystem(bool site) => $$$"""
        Tu prépares le premier écran d'affichage dynamique d'une organisation (accueil des visiteurs, vitrine, salle d'attente, communication interne…).
        Tu reçois {{{(site ? "le contenu de son site web" : "une phrase qui décrit son activité")}}}. Réponds uniquement par un objet JSON, sans commentaire autour :
        {"name":"nom de l'organisation, ou \"\"","activity":"activité en 2 à 4 mots","city":"ville ou null","goal":"ce que l'écran doit faire, en une phrase courte","tone":"ton en 2 à 4 mots",
         "font":"Montserrat","colors":{"deep":"#RRGGBB","accent":"#RRGGBB","light":"#RRGGBB"},
         "logo":null,"photos":[],"photo_queries":["…","…","…","…"],
         "pages":{
          "welcome":{"kicker":"…","title":"…","sub":"…"},
          "services":{"kicker":"…","title":"…","points":["…","…","…"]},
          "info":{"kicker":"…","title":"…","cards":[{"icon":"horloge","label":"…","value":"…","note":"…"}]},
          "news":{"tag":"…","title":"…","sub":"…","link":"https://…"},
          "thanks":{"title":"…","sub":"…"} } }
        {{{Rules}}}
        - "city" : la ville où se trouve l'organisation, seulement si elle est écrite dans les informations ; sinon null.
        - "colors" : {{{(site ? "celles du site (couleur de la marque, couleurs repérées)" : "des couleurs sobres et chaleureuses qui conviennent à l'activité")}}} ; "deep" très foncée pour les fonds, "light" très claire, "accent" vive.
        - "font" : une de Montserrat, Poppins, Playfair Display, Oswald, IBM Plex Sans{{{(site ? ", proche du style du site" : "")}}}.
        {{{(site ? "- \"logo\" : le numéro du logo parmi les logos possibles, null si aucun ne semble être celui de l'organisation. \"photos\" : jusqu'à 4 numéros d'images du site (tu les vois, avec leur numéro) qui sont de vraies photos d'ambiance : pas de logo, d'icône, de bannière, de capture d'écran ni de texte incrusté dans l'image ; les plus belles d'abord ; [] si aucune ne convient." : "- \"logo\" : null ; \"photos\" : [].")}}}
        - "photo_queries" : 4 recherches de photos d'illustration neutres, en français, 2 à 4 mots (un lieu, un objet, une ambiance liés à l'activité ; jamais de personne, de visage ni de marque).
        """;

    private static string Facts(SiteRead s)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Site : ").Append(s.Host).Append(" (").Append(s.Pages).Append(" pages lues)\n");
        sb.Append("Titre : ").Append(s.Title).Append('\n');
        if (s.SiteName.Length > 0) sb.Append("Nom : ").Append(s.SiteName).Append('\n');
        if (s.Description.Length > 0) sb.Append("Description : ").Append(s.Description).Append('\n');
        sb.Append("Couleur de la marque : ").Append(s.Color ?? "non indiquée").Append('\n');
        if (s.Colors.Count > 0) sb.Append("Couleurs repérées sur le site : ").Append(string.Join(", ", s.Colors)).Append('\n');
        if (s.Logos.Count > 0)
        {
            sb.Append("\nLogos possibles :\n");
            for (var i = 0; i < s.Logos.Count; i++) sb.Append(i).Append(") ").Append(Tail(s.Logos[i])).Append('\n');
        }
        if (s.Images.Count > 0)
        {
            sb.Append("\nImages du site (montrées plus bas) :\n");
            for (var i = 0; i < s.Images.Count; i++) sb.Append(i).Append(") ").Append(Tail(s.Images[i].Url)).Append(" · ").Append(s.Images[i].W).Append(" × ").Append(s.Images[i].H).Append(s.Images[i].Alt.Length > 0 ? " · " + s.Images[i].Alt : "").Append('\n');
        }
        sb.Append("\nTitres des pages :\n").Append(string.Join("\n", s.Headings)).Append("\n\nTexte des pages :\n").Append(s.Text);
        return sb.ToString();
    }

    /// <summary>La demande avec les images du site, réduites, chacune précédée de son numéro.</summary>
    private static List<ContentBlockParam> Look(string facts, SiteRead site)
    {
        var blocks = new List<ContentBlockParam> { new TextBlockParam { Text = facts } };
        for (var i = 0; i < site.Images.Count; i++)
        {
            blocks.Add(new TextBlockParam { Text = $"Image {i} · {site.Images[i].W} × {site.Images[i].H}" });
            blocks.Add(new ImageBlockParam { Source = new Base64ImageSource { Data = site.Images[i].Jpeg, MediaType = MediaType.ImageJpeg } });
        }
        blocks.Add(new TextBlockParam { Text = "Réponds par l'objet JSON demandé." });
        return blocks;
    }

    private static string Tail(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.AbsolutePath.Length > 70 ? "…" + u.AbsolutePath[^70..] : u.AbsolutePath) : url;

    /// <summary>Demande à l'IA le contenu de l'écran (une seule demande). Rend le contenu contrôlé et ses choix de logo et de photos.</summary>
    public async Task<(StartBrief? Brief, StartPick Pick, string? Error)> Brief(string ask, SiteRead? site, CancellationToken ct)
    {
        var user = site == null ? "Phrase de la personne : " + Cap(ask.Trim(), MaxAsk) : Facts(site);
        var message = site is { Images.Count: > 0 } ? new UserMessageParam { Content = Look(user, site) } : new UserMessageParam { Content = user };
        var r = await ai.Complete(store.ClientId, "compose", BriefSystem(site != null), message, 2500, ct);
        if (!r.Ok) return (null, StartPick.None, r.Message);
        if (ParseObject(r.Text) is not { } o || ReadPages(o["pages"]) is not { } pages)
            return (null, StartPick.None, "L'IA n'a pas réussi à préparer vos pages. Réessayez, ou ajoutez vos contenus vous-même.");
        var b = new StartBrief
        {
            Name = StartTemplates.Cut(Str(o, "name"), 60),
            Activity = StartTemplates.Cut(Str(o, "activity"), 60),
            City = StartTemplates.Cut(Str(o, "city"), 60) is { Length: > 1 } c && !c.Equals("null", StringComparison.OrdinalIgnoreCase) ? c : null,
            Goal = StartTemplates.Cut(Str(o, "goal"), 120),
            Tone = StartTemplates.Cut(Str(o, "tone"), 60),
            Font = Str(o, "font") ?? "",
            Colors = ReadColors(o["colors"]) ?? new StartColors(),
            Pages = pages,
            Site = site?.Host ?? "",
        };
        Sanitize(b);
        var pick = new StartPick(Int(o["logo"], site?.Logos.Count ?? 0), Ints(o["photos"], site?.Images.Count ?? 0), Strings(o["photo_queries"]));
        return (b, pick, null);
    }

    private static string ChangeSystem => $$"""
        Tu modifies les textes et les couleurs d'un écran d'affichage dynamique préparé pour une organisation. Tu reçois le contenu actuel en JSON
        (« pages » en français, « en » en anglais s'il existe) puis la demande. Réponds uniquement par un objet JSON, sans commentaire autour :
        {"font":"…","colors":{"deep":"#RRGGBB","accent":"#RRGGBB","light":"#RRGGBB"},"pages":{…même forme…},"en":{…même forme, ou null…},"summary":"ce que tu as changé, en une phrase"}
        Fais exactement ce qui est demandé et garde tout le reste à l'identique. Pour retirer une page facultative (info ou news), mets-la à null.
        Si « en » existe, applique la même modification aux textes anglais.
        {{Rules}}
        """;

    /// <summary>Une retouche demandée en langage courant (« plus chaleureux », « ajoute le samedi matin »…) : rend le nouveau contenu et ce qui a changé.</summary>
    public async Task<(StartBrief? Brief, string Summary, string? Error)> Change(StartBrief b, string request, CancellationToken ct)
    {
        request = (request ?? "").Trim();
        if (request.Length < 2) return (null, "", "Dites ce qu'il faut changer.");
        if (request.Length > MaxAsk) return (null, "", "Cette demande est trop longue : une ou deux phrases suffisent.");
        var now = new JsonObject
        {
            ["name"] = b.Name, ["city"] = b.City, ["font"] = b.Font,
            ["colors"] = JsonSerializer.SerializeToNode(b.Colors, StartTemplates.Json),
            ["pages"] = JsonSerializer.SerializeToNode(b.Pages, StartTemplates.Json),
            ["en"] = b.En == null ? null : JsonSerializer.SerializeToNode(b.En, StartTemplates.Json),
        };
        var r = await ai.Complete(store.ClientId, "compose", ChangeSystem, new UserMessageParam { Content = "Contenu actuel :\n" + now.ToJsonString(StartTemplates.Json) + "\n\nDemande : " + request }, 3500, ct);
        if (!r.Ok) return (null, "", r.Message);
        if (ParseObject(r.Text) is not { } o || ReadPages(o["pages"]) is not { } pages)
            return (null, "", "L'IA n'a pas réussi cette modification. Reformulez, ou faites-la ensuite dans l'éditeur.");
        var nb = b.Clone();
        nb.Pages = pages;
        if (Str(o, "font") is { } f) nb.Font = f;
        if (ReadColors(o["colors"]) is { } c) nb.Colors = c;
        nb.En = b.En == null ? null : ReadPages(o["en"]) ?? b.En;
        Sanitize(nb);
        return (nb, StartTemplates.Cut(Str(o, "summary"), 200), null);
    }

    private static string TranslateSystem => """
        Traduis en anglais tous les textes de cet écran d'affichage dynamique, en gardant le sens, le ton et des textes courts (même longueur environ).
        Réponds uniquement par l'objet JSON « pages » traduit, exactement de la même forme, sans commentaire autour. Garde tels quels les noms propres, adresses, numéros, liens et icônes.
        """;

    public async Task<(StartPages? En, string? Error)> Translate(StartBrief b, CancellationToken ct)
    {
        var r = await ai.Complete(store.ClientId, "compose", TranslateSystem, new UserMessageParam { Content = JsonSerializer.Serialize(b.Pages, StartTemplates.Json) }, 3000, ct);
        if (!r.Ok) return (null, r.Message);
        if (ParseObject(r.Text) is not { } o) return (null, "L'IA n'a pas réussi la traduction. Réessayez dans un instant.");
        var pages = ReadPages(o["pages"] ?? o) ?? ReadPages(o);
        if (pages == null) return (null, "L'IA n'a pas réussi la traduction. Réessayez dans un instant.");
        var tmp = new StartBrief { Pages = pages, Site = b.Site };
        Sanitize(tmp);
        return (tmp.Pages, null);
    }

    /// <summary>Longueurs, couleurs, police, icônes et liens contrôlés ; pages vides retirées.</summary>
    public static void Sanitize(StartBrief b)
    {
        b.Font = StartTemplates.Fonts.Contains(b.Font) ? b.Font : "Montserrat";
        b.Colors = StartTemplates.Fix(b.Colors);
        Fix(b.Pages, b);
        if (b.En != null) Fix(b.En, b);
    }

    private static void Fix(StartPages p, StartBrief b)
    {
        static string C(string? s, int n) => StartTemplates.Cut(s, n);
        p.Welcome ??= new(); p.Services ??= new(); p.Thanks ??= new();
        p.Welcome.Kicker = C(p.Welcome.Kicker, 30); p.Welcome.Title = C(p.Welcome.Title, 50); p.Welcome.Sub = C(p.Welcome.Sub, 90);
        if (p.Welcome.Title.Length == 0) p.Welcome.Title = b.Name.Length > 0 ? "Bienvenue chez " + b.Name : "Bienvenue";
        p.Services.Kicker = C(p.Services.Kicker, 30); p.Services.Title = C(p.Services.Title, 50);
        p.Services.Points = (p.Services.Points ?? new()).Select(x => C(x, 48)).Where(x => x.Length > 0).Take(4).ToList();
        if (p.Info != null)
        {
            p.Info.Kicker = C(p.Info.Kicker, 30); p.Info.Title = C(p.Info.Title, 40);
            p.Info.Cards = (p.Info.Cards ?? new()).Where(x => x != null).Take(3).Select(x => new StartCard
            {
                Icon = StartTemplates.Icons.Contains(x.Icon ?? "") ? x.Icon! : "information",
                Label = C(x.Label, 22), Value = C(x.Value, 36), Note = C(x.Note, 44),
            }).Where(x => x.Value.Length > 0 || x.Label.Length > 0).ToList();
            if (p.Info.Cards.Count == 0 || p.Info.Title.Length == 0) p.Info = null;
        }
        if (p.News != null)
        {
            p.News.Tag = C(p.News.Tag, 18); p.News.Title = C(p.News.Title, 64); p.News.Sub = C(p.News.Sub, 90);
            p.News.Link = b.Site.Length > 0 ? StartTemplates.SafeLink(p.News.Link) : null;   // sans site, pas de lien inventé
            if (p.News.Title.Length == 0) p.News = null;
        }
        p.Thanks.Title = C(p.Thanks.Title, 42); p.Thanks.Sub = C(p.Thanks.Sub, 64);
        if (p.Thanks.Title.Length == 0) p.Thanks.Title = "Merci de votre visite";
        if (p.Thanks.Sub.Length == 0 && b.Site.Length > 0) p.Thanks.Sub = b.Site;
    }

    private static JsonObject? ParseObject(string text)
    {
        var a = text.IndexOf('{'); var z = text.LastIndexOf('}');
        if (a < 0 || z <= a) return null;
        try { return JsonNode.Parse(text[a..(z + 1)]) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static StartPages? ReadPages(JsonNode? n)
    {
        if (n is not JsonObject o || o["welcome"] is not JsonObject) return null;
        try { return JsonSerializer.Deserialize<StartPages>(o.ToJsonString(), StartTemplates.Json); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException) { return null; }
    }

    private static StartColors? ReadColors(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        return new StartColors { Deep = Str(o, "deep") ?? "", Accent = Str(o, "accent") ?? "", Light = Str(o, "light") ?? "" };
    }

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonNode? n, int count) =>
        n is JsonValue v && (v.TryGetValue<int>(out var i) || v.TryGetValue<double>(out var d) && (i = (int)d) == d) && i >= 0 && i < count ? i : null;

    private static List<int> Ints(JsonNode? n, int count) =>
        n is JsonArray a ? a.Select(x => Int(x, count)).OfType<int>().Distinct().Take(6).ToList() : new();

    private static List<string> Strings(JsonNode? n) =>
        n is JsonArray a ? a.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? StartTemplates.Cut(s, 60) : "").Where(s => s.Length > 1).Take(4).ToList() : new();

    // ---------- 3. Logo et photos ----------

    /// <summary>Le logo choisi par l'IA (puis les autres candidats), importé dans la médiathèque.</summary>
    public async Task<Guid?> ImportLogo(SiteRead site, int? pick, CancellationToken ct)
    {
        var order = (pick is { } p ? new[] { p } : Array.Empty<int>()).Concat(Enumerable.Range(0, site.Logos.Count)).Distinct().Take(3);
        foreach (var i in order)
        {
            var r = await importer.ImportUrl(site.Logos[i], store.ClientId, null, ct, store.AreaId, null, store.UserId);
            if (r.Item is { } item && (item.Width is null || item.Width >= 48)) return item.Id;
            if (r.Item is { } small) library.Delete(new[] { small.Id });
        }
        return null;
    }

    /// <summary>Photos du site choisies par l'IA (vraies photos, assez grandes), importées ; jusqu'à <paramref name="max"/>.</summary>
    public async Task<List<Guid>> SitePhotos(SiteRead site, StartPick pick, int max, CancellationToken ct)
    {
        var got = new List<Guid>();
        foreach (var i in pick.Photos.Distinct().Take(6))   // l'IA a vu les images : celles qu'elle écarte ne sont pas reprises
        {
            if (got.Count >= max) break;
            var r = await importer.ImportUrl(site.Images[i].Url, store.ClientId, null, ct, store.AreaId, null, store.UserId);
            if (r.Item is not { } item) continue;
            if (item.Type == "image" && item.Width >= 700 && item.Height >= 400) got.Add(item.Id);
            else library.Delete(new[] { item.Id });   // trop petite (vignette, icône) : elle n'encombre pas la médiathèque
        }
        return got;
    }

    /// <summary>Photos neutres d'internet (sans personne), si la plateforme et l'organisation y ont droit.</summary>
    public bool CanSearchWeb => webImages.On(store.ClientId);

    public async Task<List<Guid>> WebPhotos(IEnumerable<string> queries, int max, bool portrait, CancellationToken ct)
    {
        var got = new List<Guid>();
        if (!CanSearchWeb) return got;
        foreach (var q in queries)
        {
            if (got.Count >= max) break;
            var (item, err) = await webImages.FindAndImport(store.ClientId, q, portrait, store.AreaId, store.UserId, ct);
            if (item != null && !got.Contains(item.Id)) got.Add(item.Id);
            else if (err != null) log.LogInformation("Photo d'internet non trouvée pour « {Query} » : {Error}", q, err);
        }
        return got;
    }

    /// <summary>Images d'exemple de Linkivi (paysages), reprises si elles sont déjà dans la médiathèque.</summary>
    public async Task<List<Guid>> SamplePhotos(int max, CancellationToken ct)
    {
        var got = new List<Guid>();
        foreach (var (file, name) in GettingStarted.Samples.Take(max))
        {
            if (store.Read(d => d.Media.FirstOrDefault(m => m.Name == name && m.Type == "image" && m.FileName != null && !m.Off)?.Id) is { } have) { got.Add(have); continue; }
            var r = await importer.ImportFile(Path.Combine(env.WebRootPath, "samples", "premiers-pas", file), name + ".jpg", store.ClientId, store.AreaId, store.UserId, ct);
            if (r.Item != null) got.Add(r.Item.Id);
        }
        return got;
    }

    // ---------- 4-6. Pages, météo, liste ----------

    /// <summary>Dessine une page (française ou anglaise) et l'enregistre comme composition (nouvelle, ou la même qu'avant).</summary>
    public Guid? SavePage(StartState st, string kind, bool en)
    {
        var p = en ? st.Brief.En : st.Brief.Pages;
        if (p == null || !StartTemplates.Has(p, kind) || !Ready(Compositions.AppId)) return null;
        if (Compositions.Clean(StartTemplates.Doc(st.Brief, kind, st.Portrait, en)) is not { } c) return null;
        var map = en ? st.EnPages : st.Pages;
        Guid? existing = map.TryGetValue(kind, out var id) && store.Read(d => d.Media.Any(m => m.Id == id && m.AppId == Compositions.AppId)) ? id : null;
        var (item, errors) = svc.SaveInstance(Compositions.AppId, existing, StartTemplates.Name(p, kind, en), new Dictionary<string, string> { ["doc"] = c.Doc, ["images"] = string.Join(",", c.Images) });
        if (item == null) { log.LogInformation("Page « {Kind} » non enregistrée : {Errors}", kind, string.Join(" ", errors)); return null; }
        map[kind] = item.Id;
        return item.Id;
    }

    /// <summary>La météo de la ville, seulement si elle est trouvée par le service. Rend aussi une note (« Ville trouvée sur votre site », raison de l'absence).</summary>
    public async Task<(Guid? Id, string Note)> Weather(StartState st)
    {
        if (st.Brief.City is not { } city) return (null, "");
        if (!weather.IsConfigured) return (null, "La météo n'est pas encore branchée sur ce serveur : l'heure seulement.");
        WeatherForecast? f;
        try { f = await weather.Forecast(city, TimeSpan.FromMinutes(30)); }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException) { return (null, "Service météo indisponible : l'heure seulement."); }
        if (f == null) return (null, $"« {city} » est inconnue du service météo : l'heure seulement.");
        if (!Ready("weather-slide")) return (null, "Application Météo non activée : l'heure seulement.");
        var values = new Dictionary<string, string> { ["city"] = city, ["unit"] = "C", ["theme"] = "dark", ["forecast"] = "true", ["airQuality"] = "false", ["alerts"] = "true", ["feel"] = "true", ["wind"] = "true", ["humidity"] = "false", ["uv"] = "false", ["sun"] = "false", ["refresh"] = "30" };
        var (item, errors) = svc.SaveInstance("weather-slide", st.Weather, StartTemplates.Cut("Météo " + f.City, 80), values);
        if (item == null) return (null, string.Join(" ", errors));
        st.Weather = item.Id;
        return (item.Id, "");
    }

    /// <summary>La liste « Accueil » : celle de cet écran, ou la liste vide des Premiers pas, sinon une nouvelle (devient la liste des Premiers pas).</summary>
    public Guid SaveList(StartState st)
    {
        var order = st.Order();
        var id = store.Write(d =>
        {
            Playlist pl;
            if (st.ListId is { } lid && d.Playlists.FirstOrDefault(p => p.Id == lid) is { } mine) pl = mine;
            else if (d.Tenant.FirstScreenListId is { } fid && d.Playlists.FirstOrDefault(p => p.Id == fid) is { Draft.Count: 0 } empty) { pl = empty; pl.Name = ListName; }
            else
            {
                pl = new Playlist { Name = ListName, UpdatedAt = DateTime.UtcNow };
                d.Playlists.Add(pl);
                d.Tenant.FirstScreenListId = pl.Id;
            }
            pl.Draft = order.Select(m => new PlaylistItem { MediaId = m, DurationSec = PageSeconds, DurationMode = "fixed", Placement = "full", Transition = Transitions.Default }).ToList();
            pl.Touch();
            return pl.Id;
        });
        st.ListId = id;
        return id;
    }

    /// <summary>Redessine tout après une retouche : pages gardées ou retirées, versions anglaises ajoutées ou retirées, liste remise dans l'ordre.</summary>
    public void Apply(StartState st)
    {
        foreach (var k in StartTemplates.Kinds)
        {
            if (StartTemplates.Has(st.Brief.Pages, k)) SavePage(st, k, false);
            else Drop(st, st.Pages, k);
            if (st.Bilingual && st.Brief.En != null && StartTemplates.Has(st.Brief.En, k)) SavePage(st, k, true);
            else Drop(st, st.EnPages, k);
        }
        SaveList(st);
    }

    /// <summary>Une page retirée : sa composition disparaît si aucune autre liste ne l'utilise.</summary>
    private void Drop(StartState st, Dictionary<string, Guid> map, string kind)
    {
        if (!map.Remove(kind, out var id)) return;
        store.Write(d =>
        {
            if (!d.Playlists.Any(p => p.Id != st.ListId && p.Draft.Any(i => i.MediaId == id))) d.Media.RemoveAll(m => m.Id == id && m.AppId == Compositions.AppId);
        });
    }

    // ---------- Discussion gardée (panneau Assistant) ----------

    /// <summary>Range l'échange dans les discussions de la personne (panneau Assistant, Ctrl+J). Rend l'identifiant de la discussion.</summary>
    public Guid? SaveChat(Guid? chatId, string ask, string say, IEnumerable<(string Label, string State)> acts)
    {
        if (store.UserId is not { } uid) return null;
        var cid = store.ClientId;
        var list = acts.ToList();
        return root.Write(d =>
        {
            var chat = chatId is { } id ? AiChats.Get(d, cid, uid, id) : null;
            if (chat == null)
            {
                chat = new AiChat { ClientId = cid, UserId = uid, Title = StartTemplates.Cut("Premiers pas · " + AiChats.TitleOf(ask), 70), Page = "Premiers pas", Area = "" };
                d.AiChats.Add(chat);
                AiChats.Purge(d, cid, uid, DateTime.UtcNow);
            }
            chat.Lines.Add(new AiChatLine { Ask = ask, From = "Premiers pas", Say = say, Acts = list.Select(a => new AiChatAct { Label = a.Label, State = a.State }).ToList() });
            chat.Updated = DateTime.UtcNow;
            return (Guid?)chat.Id;
        });
    }
}
