using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic.Models.Messages;

namespace Linkii.Poc;

/// <summary>Une composition proposée par l'IA : son nom et son document (même format que l'éditeur de compositions, déjà nettoyé).</summary>
public record AiSlide(string Name, string Doc);

public record AiComposeResult(bool Ok, List<AiSlide> Slides, string Message);

/// <summary>
/// Création de compositions par l'IA (Pro, maquette maquettes/ia-pistes.html, pistes 4) : à partir d'une phrase (« affiche pour la soirée d'automne… »)
/// ou du site web de l'organisation (Premiers pas). L'IA ne dessine que des textes et des formes, dans les polices de l'éditeur ; tout est contrôlé ici
/// (positions dans la page, couleurs, tailles) avant d'être enregistré, puis se retouche dans l'éditeur comme n'importe quelle composition.
/// </summary>
public class AiCompose(AiService ai, SafeHttp http, MediaImporter importer, ILogger<AiCompose> log)
{
    private static readonly string[] Fonts = { "IBM Plex Sans", "Montserrat", "Poppins", "Playfair Display", "Oswald", "Arial" };
    private static readonly Regex Hex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    public const int MaxSentence = 600;

    private static string System(int w, int h, int count, bool photo) => $$"""
        Tu crées des pages pour des écrans d'affichage dynamique (accueil, vitrines, salles d'attente), à lire de loin.
        La page fait {{w}} × {{h}} pixels (origine en haut à gauche). Réponds uniquement par un objet JSON, sans commentaire autour :
        {"slides":[{"name":"titre court de la page","bg":"#RRGGBB","els":[ ... ]}]}
        Fais {{count}} page{{(count > 1 ? "s" : "")}}. Chaque élément de « els » est l'un de :
        - texte : {"type":"text","x":0,"y":0,"w":0,"h":0,"text":"...","font":"Montserrat","size":64,"weight":600,"color":"#RRGGBB","align":"left"}
          polices permises : IBM Plex Sans, Montserrat, Poppins, Playfair Display, Oswald, Arial ; « weight » de 300 à 900 ; « align » : left, center ou right ; \n pour passer à la ligne.
        - forme : {"type":"shape","kind":"rect","x":0,"y":0,"w":0,"h":0,"fill":"#RRGGBB","radius":0} (kind : rect ou circle), pour des bandeaux, filets ou fonds de couleur.
        {{(photo ? "- photo : {\"type\":\"image\",\"slot\":\"photo\",\"x\":0,\"y\":0,\"w\":0,\"h\":0} : la photo du site, à utiliser au plus une fois, par exemple sur une moitié de la page." : "Pas de photo : on les ajoutera ensuite.")}}
        Règles : tout reste dans la page, avec une marge d'au moins 5 % ; au plus 12 éléments par page ; un titre fort (au moins {{h * 7 / 100}} px de haut de texte), le reste d'au moins {{h * 35 / 1000}} px ;
        un fond et des textes très contrastés ; une seule idée par page ; les éléments dessinés dans l'ordre (fond d'abord). Écris en français, simplement.
        N'invente rien : pas de date, d'heure, de prix, de téléphone ni d'adresse qui ne soient dans les informations données.
        """;

    // ---------- À partir d'une phrase ----------

    public async Task<AiComposeResult> FromSentence(Guid clientId, string sentence, bool portrait, CancellationToken ct = default)
    {
        sentence = (sentence ?? "").Trim();
        if (sentence.Length < 4) return new AiComposeResult(false, new(), "Décrivez en une phrase ce que l'écran doit annoncer.");
        if (sentence.Length > MaxSentence) return new AiComposeResult(false, new(), "Cette description est trop longue : une ou deux phrases suffisent.");
        var (w, h) = portrait ? (1080, 1920) : (1920, 1080);
        var r = await ai.Complete(clientId, "compose", System(w, h, 1, false), new UserMessageParam { Content = "Page à créer : " + sentence }, 3000, ct);
        if (!r.Ok) return new AiComposeResult(false, new(), r.Message);
        var slides = Slides(r.Text, w, h, null);
        return slides.Count == 0 ? new AiComposeResult(false, new(), "L'IA n'a pas réussi cette page. Reformulez, ou choisissez un modèle.") : new AiComposeResult(true, slides, "");
    }

    // ---------- À partir du site web ----------

    /// <summary>Lit la page d'accueil du site et propose trois compositions (accueil, présentation, infos pratiques) ; la photo du site entre dans la médiathèque.</summary>
    public async Task<AiComposeResult> FromSite(Guid clientId, string address, Guid? areaId, Guid? ownerId, CancellationToken ct = default)
    {
        var state = ai.State(clientId, "compose");
        if (!state.Available) return new AiComposeResult(false, new(), AiService.Say(state));
        string html;
        Uri page;
        try
        {
            page = SafeHttp.ParseUrl(address.Contains("://") ? address.Trim() : "https://" + address.Trim());
            html = await http.GetString(page.AbsoluteUri, 1_500_000, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or UriFormatException or TaskCanceledException or ArgumentException)
        {
            return new AiComposeResult(false, new(), "Cette adresse n'a pas pu être lue. Vérifiez-la, ou ajoutez vos contenus à la main.");
        }
        var info = Extract(html, page);
        if (info.Text.Length < 40 && info.Title.Length == 0) return new AiComposeResult(false, new(), "Ce site ne contient pas assez de texte lisible pour en tirer des pages.");

        // photo du site (image de partage) : importée dans la médiathèque, utilisée par la composition si elle en veut une
        Guid? photo = null;
        if (info.Image is { } img)
        {
            try { photo = (await importer.ImportUrl(img, clientId, null, ct, areaId, null, ownerId)).Item?.Id; }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogInformation(e, "Photo du site non importée."); }
        }

        var facts = $"Site : {info.Title}\nNom : {info.SiteName}\nDescription : {info.Description}\nCouleur de la marque : {(info.Color ?? "non indiquée : choisis une couleur sobre qui lui va")}\nPhoto disponible : {(photo != null ? "oui" : "non")}\n\nTitres de la page :\n{string.Join("\n", info.Headings.Take(14))}\n\nTexte de la page :\n{info.Text}";
        var prompt = "Crée trois pages d'écran pour cette organisation : 1) un accueil chaleureux avec son nom et sa phrase d'accroche, 2) ce qu'elle fait ou propose (3 à 4 points courts), 3) les infos pratiques que la page donne (horaires, adresse, contact) ; si elles manquent, une page « Bienvenue » avec le nom et l'adresse du site. Même couleur de marque et mêmes polices sur les trois.\n\n" + facts;
        var r = await ai.Complete(clientId, "compose", System(1920, 1080, 3, photo != null), new UserMessageParam { Content = prompt }, 4500, ct);
        if (!r.Ok) return new AiComposeResult(false, new(), r.Message);
        var slides = Slides(r.Text, 1920, 1080, photo);
        return slides.Count == 0 ? new AiComposeResult(false, new(), "L'IA n'a pas réussi à préparer ces pages. Ajoutez vos contenus à la main ou choisissez un modèle.") : new AiComposeResult(true, slides, "");
    }

    private record SiteInfo(string Title, string SiteName, string Description, string? Color, string? Image, List<string> Headings, string Text);

    private static SiteInfo Extract(string html, Uri page)
    {
        string Meta(string key)
        {
            var m = Regex.Match(html, $"<meta[^>]+(?:property|name)=[\"']{Regex.Escape(key)}[\"'][^>]*>", RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            var c = Regex.Match(m.Value, "content=[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase);
            return c.Success ? WebUtility.HtmlDecode(c.Groups[1].Value).Trim() : "";
        }
        string Clean(string s) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", " ")), @"\s+", " ").Trim();
        var title = Regex.Match(html, "<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline) is { Success: true } t ? Clean(t.Groups[1].Value) : "";
        var body = Regex.Replace(html, @"<(script|style|noscript|svg|template)\b.*?</\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        body = Regex.Replace(body, "<!--.*?-->", " ", RegexOptions.Singleline);
        var heads = Regex.Matches(body, @"<h[1-3][^>]*>(.*?)</h[1-3]>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Select(m => Clean(m.Groups[1].Value)).Where(s => s.Length is > 2 and < 140).Distinct().ToList();
        var text = Clean(body);
        string? image = null;
        if (Meta("og:image") is { Length: > 0 } og && Uri.TryCreate(page, og, out var iu) && iu.Scheme is "http" or "https") image = iu.AbsoluteUri;
        var color = Meta("theme-color");
        var site = Meta("og:site_name");
        var desc = Meta("og:description") is { Length: > 0 } d ? d : Meta("description");
        return new SiteInfo(Meta("og:title") is { Length: > 0 } ot ? ot : title, site, desc.Length > 400 ? desc[..400] : desc, Hex.IsMatch(color) ? color : null, image, heads, text.Length > 3500 ? text[..3500] : text);
    }

    // ---------- Contrôle de ce que l'IA a dessiné ----------

    private List<AiSlide> Slides(string answer, int w, int h, Guid? photo)
    {
        var a = answer.IndexOf('{'); var b = answer.LastIndexOf('}');
        if (a < 0 || b <= a) return new();
        try
        {
            if (JsonNode.Parse(answer[a..(b + 1)]) is not JsonObject o || o["slides"] is not JsonArray arr) return new();
            var list = new List<AiSlide>();
            foreach (var s in arr.OfType<JsonObject>().Take(4))
            {
                var els = Elements(s["els"] as JsonArray, w, h, photo);
                if (els.Count == 0) continue;
                var bg = Str(s, "bg") is { } c && Hex.IsMatch(c) ? c : "#FFFFFF";
                var doc = new JsonObject { ["v"] = 1, ["w"] = w, ["h"] = h, ["bg"] = bg, ["els"] = els };
                if (Compositions.Clean(doc.ToJsonString()) is not { } clean) continue;
                var name = (Str(s, "name") ?? "Page").Trim();
                list.Add(new AiSlide(name.Length == 0 ? "Page" : name.Length > 60 ? name[..60] : name, clean.Doc));
            }
            return list;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            log.LogInformation(e, "Réponse de l'IA illisible pour la composition.");
            return new();
        }
    }

    private static JsonArray Elements(JsonArray? src, int w, int h, Guid? photo)
    {
        var els = new JsonArray();
        if (src == null) return els;
        foreach (var e in src.OfType<JsonObject>().Take(14))
        {
            var type = Str(e, "type");
            var x = Num(e, "x", 0, w - 20, 0); var y = Num(e, "y", 0, h - 20, 0);
            var bw = Num(e, "w", 20, w - x, w - x); var bh = Num(e, "h", 8, h - y, 60);
            if (type == "shape")
            {
                els.Add(new JsonObject
                {
                    ["id"] = NewId(), ["type"] = "shape", ["kind"] = Str(e, "kind") == "circle" ? "circle" : "rect", ["x"] = x, ["y"] = y, ["w"] = bw, ["h"] = bh,
                    ["fill"] = Str(e, "fill") is { } f && Hex.IsMatch(f) ? f : "#3B82C4", ["stroke"] = "", ["sw"] = 6, ["radius"] = Num(e, "radius", 0, 300, 0), ["rot"] = 0, ["opacity"] = 1,
                });
            }
            else if (type == "text" && Str(e, "text") is { Length: > 0 } text)
            {
                text = text.Length > 400 ? text[..400] : text;
                var size = Num(e, "size", 16, 400, 60);
                var weight = Math.Clamp((int)Math.Round(Num(e, "weight", 300, 900, 500) / 100.0) * 100, 300, 900);
                var perLine = Math.Max(1, (int)(bw / (size * 0.55)));
                var lines = text.Split('\n').Sum(l => Math.Max(1, (int)Math.Ceiling(l.Length / (double)perLine)));
                var need = (int)Math.Ceiling(size * 1.15 * lines);
                bh = Math.Min(Math.Max(bh, need), h - y);
                els.Add(new JsonObject
                {
                    ["id"] = NewId(), ["type"] = "text", ["x"] = x, ["y"] = y, ["w"] = bw, ["h"] = bh, ["text"] = text,
                    ["font"] = Fonts.Contains(Str(e, "font") ?? "") ? Str(e, "font") : "Montserrat", ["size"] = size, ["weight"] = weight,
                    ["color"] = Str(e, "color") is { } c && Hex.IsMatch(c) ? c : "#0B1F3A", ["align"] = Str(e, "align") is "center" or "right" ? Str(e, "align") : "left",
                    ["ls"] = 0, ["lh"] = 1.15, ["italic"] = false, ["opacity"] = 1,
                });
            }
            else if (type == "image" && photo is { } media && els.OfType<JsonObject>().All(x0 => Str(x0, "type") != "image"))
            {
                els.Add(new JsonObject { ["id"] = NewId(), ["type"] = "image", ["media"] = media.ToString(), ["x"] = x, ["y"] = y, ["w"] = Math.Max(bw, 120), ["h"] = Math.Max(bh, 120), ["fit"] = "cover", ["radius"] = 0, ["opacity"] = 1 });
            }
        }
        return els;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..8];
    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int Num(JsonObject o, string k, int min, int max, int fallback)
    {
        if (o[k] is not JsonValue v) return Math.Clamp(fallback, min, Math.Max(min, max));
        double d;
        if (v.TryGetValue<double>(out var dd)) d = dd;
        else if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var ds)) d = ds;
        else return Math.Clamp(fallback, min, Math.Max(min, max));
        return (int)Math.Clamp(Math.Round(d), min, Math.Max(min, max));
    }
}
