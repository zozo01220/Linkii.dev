using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic.Models.Messages;

namespace Linkii.Poc;

/// <summary>Une composition proposée par l'IA : son nom et son document (même format que l'éditeur de compositions, déjà nettoyé).</summary>
public record AiSlide(string Name, string Doc);

/// <summary>Résultat : les pages, ou un message d'erreur ; <paramref name="Photo"/> dit d'où vient la photo placée (« Photo de votre médiathèque : … », crédit Pexels), null sans photo.</summary>
/// <summary>Composition modifiée par l'IA : document propre, nom, crédit de la photo ajoutée, et ce qui a changé (dit par l'IA).</summary>
public record AiEditResult(bool Ok, string Doc, string Name, string Message, string? Photo = null, string Summary = "")
{
    public static AiEditResult Fail(string message) => new(false, "", "", message);
}

public record AiComposeResult(bool Ok, List<AiSlide> Slides, string Message, string? Photo = null);

/// <summary>
/// Photos que l'IA peut placer dans une composition créée depuis une phrase : des images de la médiathèque qui pourraient convenir (identifiant, ce qu'elles montrent),
/// une image imposée (<paramref name="MustUse"/> : la première de la liste), et la recherche d'une photo neutre sur internet (null : pas d'internet).
/// <paramref name="Web"/> cherche puis importe la photo dans la médiathèque de l'organisation ; il rend l'image créée et son crédit, ou une erreur.
/// </summary>
public record ComposePhotos(IReadOnlyList<(Guid Id, string Text)> Library, bool MustUse, Func<string, bool, CancellationToken, Task<(MediaItem? Item, string? Error)>>? Web)
{
    /// <summary>La personne veut une photo (d'internet) : l'IA ne doit pas s'en passer.</summary>
    public bool Wanted { get; init; }
    public bool Any => Library.Count > 0 || Web != null;
}

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

    private static string System(int w, int h, int count, bool photo, bool choose = false) => $$"""
        Tu crées des pages pour des écrans d'affichage dynamique (accueil, vitrines, salles d'attente), à lire de loin.
        La page fait {{w}} × {{h}} pixels (origine en haut à gauche). Réponds uniquement par un objet JSON, sans commentaire autour :
        {"slides":[{"name":"titre court de la page","bg":"#RRGGBB",{{(choose ? "\"photo\":null,\"photo_query\":null," : "")}}"els":[ ... ]}]}
        Fais {{count}} page{{(count > 1 ? "s" : "")}}. Chaque élément de « els » est l'un de :
        - texte : {"type":"text","x":0,"y":0,"w":0,"h":0,"text":"...","font":"Montserrat","size":64,"weight":600,"color":"#RRGGBB","align":"left"}
          polices permises : IBM Plex Sans, Montserrat, Poppins, Playfair Display, Oswald, Arial ; « weight » de 300 à 900 ; « align » : left, center ou right ; \n pour passer à la ligne.
        - forme : {"type":"shape","kind":"rect","x":0,"y":0,"w":0,"h":0,"fill":"#RRGGBB","radius":0} (kind : rect ou circle), pour des bandeaux, filets ou fonds de couleur.
        {{(choose ? "- photo : {\"type\":\"image\",\"slot\":\"photo\",\"x\":0,\"y\":0,\"w\":0,\"h\":0} : au plus une fois et seulement si « photo » n'est pas null, par exemple sur une moitié de la page ou en fond derrière un bandeau de couleur qui porte le texte."
          : photo ? "- photo : {\"type\":\"image\",\"slot\":\"photo\",\"x\":0,\"y\":0,\"w\":0,\"h\":0} : la photo du site, à utiliser au plus une fois, par exemple sur une moitié de la page." : "Pas de photo : on les ajoutera ensuite.")}}
        Règles : tout reste dans la page, avec une marge d'au moins 5 % ; au plus 12 éléments par page ; un titre fort (au moins {{h * 7 / 100}} px de haut de texte), le reste d'au moins {{h * 35 / 1000}} px ;
        un fond et des textes très contrastés ; une seule idée par page ; les éléments dessinés dans l'ordre (fond d'abord). Écris en français, simplement.
        N'invente rien : pas de date, d'heure, de prix, de téléphone ni d'adresse qui ne soient dans les informations données.
        """;

    /// <summary>
    /// Les photos possibles pour une phrase, vues par la personne connectée (<paramref name="store"/> : ses droits et son aire) : une image imposée
    /// (<paramref name="chosen"/>), sinon des images de la médiathèque qui pourraient convenir ; et, si <paramref name="web"/> et l'organisation l'autorise,
    /// la recherche d'une photo neutre sur internet, importée dans sa médiathèque à son nom.
    /// </summary>
    public static ComposePhotos PhotosFor(TenantStore store, WebImages webImages, string sentence, Guid? chosen, bool web)
    {
        var (lib, mustUse) = store.Read(d =>
        {
            if (chosen is { } id && d.AllMedia.FirstOrDefault(m => m.Id == id && m.Type == "image" && m.AppId == null && m.FileName != null && !m.Off) is { } one)
                return (new List<(Guid, string)> { (one.Id, Describe(one)) }, true);
            return (Candidates(d.Media, sentence), false);
        });
        var cid = store.ClientId; var area = store.AreaId; var owner = store.UserId;
        Func<string, bool, CancellationToken, Task<(MediaItem?, string?)>>? find = web && !mustUse && webImages.On(cid)
            ? (q, portrait, ct) => webImages.FindAndImport(cid, q, portrait, area, owner, ct)
            : null;
        return new ComposePhotos(lib, mustUse, find);
    }

    // ---------- À partir d'une phrase ----------

    /// <summary>
    /// Une composition depuis une phrase. Avec <paramref name="photos"/>, l'IA choisit dans la même demande une image de la médiathèque qui illustre vraiment
    /// la page (« P1 »…), ou demande une photo neutre d'internet (« web » et 2 à 4 mots de recherche), ou aucune ; la photo n'est cherchée qu'après sa réponse.
    /// </summary>
    public async Task<AiComposeResult> FromSentence(Guid clientId, string sentence, bool portrait, CancellationToken ct = default, ComposePhotos? photos = null)
    {
        sentence = (sentence ?? "").Trim();
        if (sentence.Length < 4) return new AiComposeResult(false, new(), "Décrivez en une phrase ce que l'écran doit annoncer.");
        if (sentence.Length > MaxSentence) return new AiComposeResult(false, new(), "Cette description est trop longue : une ou deux phrases suffisent.");
        var (w, h) = portrait ? (1080, 1920) : (1920, 1080);
        var choose = photos is { Any: true };
        var r = await ai.Complete(clientId, "compose", System(w, h, 1, false, choose), new UserMessageParam { Content = "Page à créer : " + sentence + (choose ? PhotoBrief(photos!) : "") }, 3000, ct);
        if (!r.Ok) return new AiComposeResult(false, new(), r.Message);
        Guid? photo = null; string? note = null;
        if (choose) (photo, note) = await PickPhoto(r.Text, photos!, portrait, ct);
        var slides = Slides(r.Text, w, h, photo);
        return slides.Count == 0 ? new AiComposeResult(false, new(), "L'IA n'a pas réussi cette page. Reformulez, ou choisissez un modèle.")
            : new AiComposeResult(true, slides, "", slides[0].Doc.Contains("\"image\"") ? note : null);
    }

    /// <summary>Ce que l'IA doit savoir des photos possibles, ajouté à la demande.</summary>
    private static string PhotoBrief(ComposePhotos p)
    {
        var sb = new global::System.Text.StringBuilder("\n\nPHOTO\n");
        if (p.MustUse && p.Library.Count > 0)
            sb.Append($"Photo choisie par la personne, à placer : P1 · {p.Library[0].Text}. Réponds \"photo\":\"P1\" et place un élément image slot photo.\n");
        else
        {
            if (p.Library.Count > 0)
            {
                sb.Append("Images de la médiathèque qui pourraient convenir (prends-en une seulement si elle illustre vraiment la page) :\n");
                for (var i = 0; i < p.Library.Count; i++) sb.Append($"P{i + 1} · {p.Library[i].Text}\n");
                sb.Append("Pour en placer une : \"photo\":\"P1\" (son repère) et un élément image slot photo.\n");
            }
            if (p.Web != null)
                sb.Append((p.Wanted && p.Library.Count == 0 ? "Une photo est demandée" : p.Library.Count > 0 ? "Si aucune ne convient et qu'une photo aide la page" : "Si une photo aide la page")
                    + " : \"photo\":\"web\" et \"photo_query\": 2 à 4 mots en français qui décrivent une photo d'illustration neutre "
                    + "(un objet, un lieu, une ambiance ; jamais de personne ni de visage, jamais de marque), puis un élément image slot photo. La photo sera cherchée sur internet.\n");
            sb.Append("Sans photo utile : \"photo\":null et aucun élément image.");
        }
        return sb.ToString();
    }

    /// <summary>La photo demandée par l'IA : une image de la médiathèque proposée, ou une photo neutre cherchée sur internet et importée. Rien si elle n'en veut pas ou si la recherche échoue.</summary>
    private async Task<(Guid? Id, string? Note)> PickPhoto(string answer, ComposePhotos p, bool portrait, CancellationToken ct)
    {
        var a = answer.IndexOf('{'); var b = answer.LastIndexOf('}');
        if (a < 0 || b <= a) return (null, null);
        JsonObject? slide;
        try { slide = (JsonNode.Parse(answer[a..(b + 1)]) as JsonObject)?["slides"] is JsonArray arr ? arr.OfType<JsonObject>().FirstOrDefault() : null; }
        catch (JsonException) { return (null, null); }
        if (slide == null) return (null, null);
        var pick = (Str(slide, "photo") ?? "").Trim();
        if (pick.Length > 1 && pick[0] == 'P' && int.TryParse(pick[1..], out var n) && n >= 1 && n <= p.Library.Count)
            return (p.Library[n - 1].Id, "Photo de votre médiathèque");
        if (p.MustUse && p.Library.Count > 0) return (p.Library[0].Id, "Photo de votre médiathèque");   // imposée : placée même si l'IA a oublié de la nommer
        if (pick == "web" && p.Web != null && Str(slide, "photo_query") is { Length: > 1 } q)
        {
            var (item, err) = await p.Web(q.Length > 60 ? q[..60] : q, portrait, ct);
            if (item != null) return (item.Id, item.Credit ?? "Photo d'internet");
            log.LogInformation("Photo d'internet non trouvée pour « {Query} » : {Error}", q, err);
        }
        return (null, null);
    }

    /// <summary>Images de la médiathèque qui pourraient illustrer la phrase : celles dont le nom, la description ou les mots-clés contiennent un de ses mots (les plus proches d'abord, 8 au plus).</summary>
    public static List<(Guid Id, string Text)> Candidates(IEnumerable<MediaItem> media, string sentence, int max = 8)
    {
        var terms = AiMedia.Terms(sentence).Where(t => t.Length > 3).ToList();
        if (terms.Count == 0) return new();
        return media.Where(m => m.Type == "image" && m.AppId == null && m.FileName != null && m.Status == null && !m.Off)
            .Select(m => (m, hay: AiMedia.Norm(string.Join(' ', new[] { m.Name, m.AiDesc ?? "" }.Concat(m.AiTags)))))
            .Select(x => (x.m, score: terms.Count(t => x.hay.Contains(t))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score).ThenByDescending(x => x.m.AddedUtc)
            .Take(max)
            .Select(x => (x.m.Id, Describe(x.m)))
            .ToList();
    }

    /// <summary>« Nom · ce que montre l'image · mots-clés », court, pour que l'IA juge si l'image convient.</summary>
    public static string Describe(MediaItem m)
    {
        var s = m.Name + (m.AiDesc is { Length: > 0 } d ? " · " + (d.Length > 140 ? d[..140] : d) : "") + (m.AiTags.Count > 0 ? " · " + string.Join(", ", m.AiTags.Take(6)) : "");
        return s.Replace('\n', ' ');
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

    // ---------- Modifier une composition existante (assistant : composition_edit) ----------

    public const int MaxChange = 600;

    private static string EditSystem(int w, int h, bool choose) => $$"""
        Tu modifies une page existante d'un écran d'affichage dynamique (à lire de loin). Tu reçois la page en JSON puis la modification demandée.
        Réponds uniquement par un objet JSON, sans commentaire autour :
        {"name":"nom de la page (garde l'ancien sauf si on demande de le changer)","w":{{w}},"h":{{h}},"bg":"#RRGGBB",{{(choose ? "\"photo\":null,\"photo_query\":null," : "")}}"els":[ ... ],"summary":"ce que tu as changé, en une phrase"}
        « els » est la liste COMPLÈTE des éléments après la modification, dans l'ordre de dessin (le premier est au fond) :
        - un élément existant garde son « id » ; ne renvoie que les champs qui changent (au moins « id ») ; un élément absent de la liste est supprimé ;
        - un nouvel élément n'a pas d'« id » et donne tous ses champs.
        Types que tu peux créer :
        - texte : {"type":"text","x":0,"y":0,"w":0,"h":0,"text":"...","font":"Montserrat","size":64,"weight":600,"color":"#RRGGBB","align":"left","italic":false,"lh":1.15,"ls":0,"opacity":1}
          polices permises : IBM Plex Sans, Montserrat, Poppins, Playfair Display, Oswald, Arial ; « weight » de 300 à 900 ; « align » : left, center ou right ; \n pour passer à la ligne.
        - forme : {"type":"shape","kind":"rect","x":0,"y":0,"w":0,"h":0,"fill":"#RRGGBB","stroke":"","sw":6,"radius":0,"rot":0,"opacity":1} (kind : rect, circle, line ou arrow)
        - image : {"type":"image","media":"I1","x":0,"y":0,"w":0,"h":0,"fit":"cover","radius":0,"opacity":1} (« media » : un repère I d'une image déjà dans la page ou proposée plus bas ; « fit » : cover ou contain){{(choose ? " ; ou {\"type\":\"image\",\"slot\":\"photo\",…} pour la photo choisie plus bas" : "")}}
        - icône : {"type":"icon","icon":"star","x":0,"y":0,"w":200,"h":200,"color":"#RRGGBB","opacity":1} (garde les icônes existantes ; n'en crée que si la page en a déjà une dont tu reprends « icon »)
        - code QR : {"type":"qr","url":"https://…","x":0,"y":0,"w":220,"h":220,"fg":"#000000","bg":"#FFFFFF"}
        - widget : {"type":"widget","kind":"clock|date|ticker|countdown","x":0,"y":0,"w":420,"h":90,"font":"IBM Plex Sans","size":56,"weight":500,"color":"#RRGGBB","align":"left"} (ticker : « text », « bg », « speed » en secondes ; countdown : « until » AAAA-MM-JJ ; clock : « sec » true/false)
        Les éléments reliés à des données (app, list, chart, room) se déplacent, se redimensionnent ou se suppriment, mais ne se créent pas ici ; garde leurs autres champs tels quels.
        La page fait {{w}} × {{h}} pixels (origine en haut à gauche). Pour passer en portrait ou en paysage, renvoie "w" et "h" (1080 × 1920 ou 1920 × 1080) et repositionne TOUS les éléments dans le nouveau format.
        Règles : fais exactement ce qui est demandé, garde le reste ; tout reste dans la page ; au plus 40 éléments ; textes très contrastés avec le fond ; garde tels quels les champs entre doubles accolades ({{"{{…}}"}}).
        N'invente rien : pas de date, d'heure, de prix, de téléphone ni d'adresse qui ne soient dans la page ou la demande. Écris en français, sauf si on te demande une autre langue.
        """;

    /// <summary>
    /// Modifie une composition selon une demande en langage courant : mise en page, couleurs, polices, textes, éléments ajoutés, déplacés ou supprimés,
    /// fond, format (portrait/paysage), images (celles de la page, de la médiathèque ou une photo neutre d'internet). Le serveur fusionne la réponse
    /// avec le document (<see cref="Merge"/>) : les éléments existants gardent les champs que l'IA ne touche pas, rien d'inconnu n'est accepté.
    /// </summary>
    public async Task<AiEditResult> Edit(Guid clientId, string docJson, string name, string change, Func<Guid, string?> mediaName, CancellationToken ct = default, ComposePhotos? photos = null)
    {
        change = (change ?? "").Trim();
        if (change.Length < 3) return AiEditResult.Fail("Dites ce qu'il faut changer dans la composition.");
        if (change.Length > MaxChange) return AiEditResult.Fail("Cette demande est trop longue : une ou deux phrases suffisent.");
        JsonObject doc;
        try { doc = JsonNode.Parse(docJson) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return AiEditResult.Fail("Composition illisible."); }
        if (doc["els"] is not JsonArray) return AiEditResult.Fail("Composition illisible.");
        var w = doc["w"] is JsonValue wv && wv.TryGetValue<double>(out var wd) ? (int)wd : 1920;
        var h = doc["h"] is JsonValue hv && hv.TryGetValue<double>(out var hd) ? (int)hd : 1080;

        // images de la page et de la médiathèque : des repères courts (I1…) plutôt que des identifiants
        var refs = new Dictionary<string, Guid>();
        var shown = (JsonObject)doc.DeepClone();
        foreach (var e in ((JsonArray)shown["els"]!).OfType<JsonObject>())
            if (Guid.TryParse(Str(e, "media"), out var mid))
            {
                var key = refs.FirstOrDefault(kv => kv.Value == mid).Key ?? "I" + (refs.Count + 1);
                refs[key] = mid; e["media"] = key;
            }
            else if (Str(e, "type") is "image" or "video") e["media"] = null;
        var sb = new global::System.Text.StringBuilder();
        sb.Append("Page actuelle (« ").Append(name).Append(" ») :\n").Append(shown.ToJsonString()).Append('\n');
        if (refs.Count > 0)
        {
            sb.Append("\nImages de la page :\n");
            foreach (var (k, id) in refs) sb.Append(k).Append(" · ").Append(mediaName(id) ?? "image").Append('\n');
        }
        var choose = photos is { Any: true };
        if (choose) sb.Append(PhotoBrief(photos!));
        sb.Append("\n\nModification demandée : ").Append(change);

        var r = await ai.Complete(clientId, "compose", EditSystem(w, h, choose), new UserMessageParam { Content = sb.ToString() }, 6000, ct);
        if (!r.Ok) return AiEditResult.Fail(r.Message);
        var a = r.Text.IndexOf('{'); var b = r.Text.LastIndexOf('}');
        JsonObject? answer = null;
        try { if (a >= 0 && b > a) answer = JsonNode.Parse(r.Text[a..(b + 1)]) as JsonObject; }
        catch (JsonException) { answer = null; }
        if (answer == null) return AiEditResult.Fail("L'IA n'a pas réussi cette modification. Reformulez, ou faites-la dans l'éditeur.");

        Guid? photo = null; string? note = null;
        if (choose)
        {
            var wrapped = new JsonObject { ["slides"] = new JsonArray(answer.DeepClone()) };
            (photo, note) = await PickPhoto(wrapped.ToJsonString(), photos!, (Num(answer, "h", 1, 8000, h) > Num(answer, "w", 1, 8000, w)), ct);
        }
        var merged = Merge(doc, answer, refs, photo);
        if (merged == null) return AiEditResult.Fail("L'IA a rendu une page vide ou illisible : rien n'a été changé.");
        if (Compositions.Clean(merged.ToJsonString()) is not { } clean) return AiEditResult.Fail("La composition modifiée est trop grande ou illisible : rien n'a été changé.");
        var newName = Str(answer, "name") is { } nn && nn.Trim().Length > 0 ? (nn.Trim().Length > 80 ? nn.Trim()[..80] : nn.Trim()) : name;
        var summary = Str(answer, "summary") is { } s ? (s.Length > 300 ? s[..300] : s) : "";
        return new AiEditResult(true, clean.Doc, newName, "", merged.ToJsonString().Contains(photo?.ToString() ?? "\u0000") ? note : null, summary);
    }

    private static readonly HashSet<string> NewTypes = new() { "text", "shape", "image", "icon", "qr", "widget" };

    /// <summary>
    /// Fusionne la réponse de l'IA avec le document : un élément existant (même « id ») garde tous ses champs et ne reçoit que ceux que l'IA renvoie
    /// (sauf « id » et « type ») ; un élément absent est supprimé ; un nouvel élément (types simples seulement) reçoit un identifiant neuf.
    /// Les images ne viennent que des repères connus (<paramref name="refs"/>) ou de la photo choisie (« slot »:"photo") ; un repère inconnu laisse l'image d'avant.
    /// Rend null si la page serait vide alors qu'elle ne l'était pas.
    /// </summary>
    public static JsonObject? Merge(JsonObject doc, JsonObject answer, IReadOnlyDictionary<string, Guid> refs, Guid? photo)
    {
        var old = (doc["els"] as JsonArray ?? new()).OfType<JsonObject>().Where(e => Str(e, "id") != null).ToDictionary(e => Str(e, "id")!, e => e);
        var w = doc["w"]?.DeepClone() ?? 1920; var h = doc["h"]?.DeepClone() ?? 1080;
        if (Num(answer, "w", 0, 8000, 0) is var nw and >= Compositions.MinSide && Num(answer, "h", 0, 8000, 0) is var nh and >= Compositions.MinSide) { w = nw; h = nh; }
        var bg = Str(answer, "bg") is { } c && Hex.IsMatch(c) ? c : Str(doc, "bg") ?? "#FFFFFF";
        var els = new JsonArray();
        var used = new HashSet<string>();
        foreach (var e in (answer["els"] as JsonArray ?? new()).OfType<JsonObject>().Take(Compositions.MaxElements))
        {
            JsonObject copy;
            if (Str(e, "id") is { } id && old.TryGetValue(id, out var prev) && used.Add(id))
            {
                copy = (JsonObject)prev.DeepClone();
                foreach (var (k, v) in e)
                {
                    if (k is "id" or "type" or "slot") continue;
                    if (k == "media") { if (Str(e, "media") is { } mr && refs.TryGetValue(mr, out var mg)) copy["media"] = mg.ToString(); continue; }
                    copy[k] = v?.DeepClone();
                }
                if (Str(e, "slot") == "photo" && photo is { } p && Str(copy, "type") is "image") copy["media"] = p.ToString();
            }
            else
            {
                var type = Str(e, "type") ?? "";
                if (!NewTypes.Contains(type)) continue;
                copy = new JsonObject();
                foreach (var (k, v) in e) if (k is not ("id" or "slot" or "media")) copy[k] = v?.DeepClone();
                copy["id"] = NewId();
                if (type == "image")
                {
                    Guid? m = Str(e, "slot") == "photo" ? photo : Str(e, "media") is { } mr && refs.TryGetValue(mr, out var mg) ? mg : null;
                    if (m == null) continue;
                    copy["media"] = m.ToString();
                    if (copy["fit"] == null) copy["fit"] = "cover";
                }
                if (type == "text" && (Str(copy, "text") ?? "").Length == 0) continue;
                if (type == "text" && !Fonts.Contains(Str(copy, "font") ?? "")) copy["font"] = "Montserrat";
                foreach (var (k, dflt) in new (string, int)[] { ("x", 0), ("y", 0), ("w", 300), ("h", 100) }) copy[k] = Num(copy, k, -8000, 8000, dflt);
                if (copy["opacity"] == null) copy["opacity"] = 1;
            }
            els.Add(copy);
        }
        if (els.Count == 0 && old.Count > 0) return null;
        return new JsonObject { ["v"] = 1, ["w"] = w, ["h"] = h, ["bg"] = bg, ["els"] = els };
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
