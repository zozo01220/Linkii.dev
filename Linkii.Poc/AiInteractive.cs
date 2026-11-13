using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;

namespace Linkii.Poc;

/// <summary>Liste interactive rendue par l'IA : nom, document (format de l'éditeur, à passer à <see cref="InteractiveLists.Apply"/>), ce qui a changé, et le nombre de tuiles restées sans action faute de cible connue.</summary>
public record AiIListResult(bool Ok, string Name, string Doc, string Message, string Summary = "", int Loose = 0)
{
    public static AiIListResult Fail(string message) => new(false, "", "", message);
}

/// <summary>Ce que l'IA peut placer dans une liste interactive : les images de la médiathèque, les contenus qu'une tuile peut ouvrir, et si le QR code « Emporter » est permis.</summary>
public record IListCatalog(IReadOnlyList<(Guid Id, string Text)> Images, IReadOnlyList<(Guid Id, string Text)> Contents, bool Qr);

/// <summary>
/// Listes interactives des bornes construites ou modifiées par l'assistant (ilist_create, ilist_edit) : tout ce que sait faire l'éditeur (§168) —
/// pages (ajout, nom, ordre, accueil, fond, image de fond, longueur), tuiles, textes, images, boutons de navigation, mise en forme, actions au toucher
/// (contenu, page, accueil, retour), QR code, dates d'affichage, format portrait ou paysage. L'IA ne voit que des repères (P pages, E éléments, I images,
/// O contenus) ; sa réponse est fusionnée avec la liste (<see cref="Merge"/>) puis contrôlée comme un enregistrement de l'éditeur.
/// </summary>
public class AiInteractive(AiService ai, ILogger<AiInteractive> log)
{
    public const int MaxChange = 600;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string EditRules = """
        MODIFIER
        - Un élément existant garde son « id » (E1…) : renvoie « id » et seulement les champs qui changent ; un élément absent de la réponse est supprimé.
        - Un « id » placé dans une autre page y déplace l'élément ; le même « id » donné une seconde fois en fait une copie. Un nouvel élément n'a pas d'« id » et donne tous ses champs.
        - Fais exactement ce qui est demandé et garde tout le reste (pages, éléments, actions, ordre).
        """;

    private static string System(bool portrait, bool qr, bool edit) => $$$"""
        Tu construis une liste interactive : ce qu'un visiteur touche sur une borne tactile. Elle est faite de pages ; la première est l'accueil. Chaque page porte des tuiles (touchables), des textes et des images, placés librement.
        {{{(edit ? "Tu reçois la liste actuelle en JSON, puis la modification demandée." : "Tu reçois la description de la liste à créer.")}}}
        Réponds uniquement par un objet JSON, sans commentaire autour :
        {"name":"nom de la liste","orientation":"{{{(portrait ? "portrait" : "landscape")}}}","pages":[{"id":"P1","name":"Accueil","bg":"#0B1F3A","bgImage":null,"len":1,"els":[ ... ]}],"summary":"ce que tu as fait, en une phrase"}
        FORMAT : « orientation » vaut portrait (1080 × 1920) ou landscape (1920 × 1080) ; elle doit être celle de la borne. {{{(edit ? "Ne la change que si on le demande, et repositionne alors TOUS les éléments." : "")}}}
        PAGES
        - « pages » est la liste COMPLÈTE des pages, dans l'ordre ; la première est l'accueil (pour changer d'accueil, mets la page voulue en premier). Au plus 30 pages.
        - Une nouvelle page reçoit un « id » que tu inventes (« N1 », « N2 »…) pour que des tuiles puissent y mener.{{{(edit ? " Une page existante garde son « id » (P1…) ; une page absente de la réponse est supprimée." : "")}}}
        - « name » : nom court (40 caractères) ; « bg » : couleur de fond ; « bgImage » : repère I d'une image en fond de page, ou null ; « len » : longueur en écrans, de 1 à 6 (au-delà de 1, le visiteur fait défiler la page vers le bas).
        - Sur les pages autres que l'accueil, la borne ajoute elle-même un bouton « ← Retour » en bas au centre : n'y mets rien (laisse libre au-delà de y = 90 sur le dernier écran de la page).
        ÉLÉMENTS (« els », dessinés dans l'ordre, au plus 80 par page)
        Positions et tailles en pourcentages : « x » et « w » en % de la largeur de la page ; « y » et « h » en % de la hauteur d'UN écran (sur une page de longueur 2, y va de 0 à 200). « size » (taille du texte) et « radius » (arrondi) en % de la largeur de la page.
        {{{(portrait ? "En portrait, 1 % de largeur vaut 0,5625 % de hauteur : une tuile carrée de w = 40 a h = 22.5 ; un écart de 3 % en largeur vaut 1.7 % en hauteur."
            : "En paysage, 1 % de largeur vaut 1,778 % de hauteur : une tuile carrée de w = 20 a h = 35.6 ; un écart de 2 % en largeur vaut 3.6 % en hauteur.")}}}
        - tuile : {"kind":"tile","x":5,"y":30,"w":43.5,"h":24,"text":"Titre","sub":null,"color":"#3B82C4","image":null,"textColor":"#FFFFFF","font":null,"size":3.4,"bold":false,"align":"left","valign":"bottom","radius":1.6,"from":null,"to":null,"action":{"type":"none"}}
          « text » : titre (120 caractères) ; « sub » : sous-titre (160) ou null ; « color » : fond ; « image » : repère I d'une image en fond de tuile, ou null ; « font » : null (police par défaut), arial, verdana, trebuchet, georgia, times, courier ou impact ;
          « size » : 2.4 petit, 3.4 normal, 4.6 grand, 6 très grand ; « align » : left, center, right ; « valign » (place du texte dans la tuile) : top, middle, bottom ; « radius » : 0 carrée, 1.6 arrondie, 3.6 très arrondie ;
          « from » et « to » : dates AAAA-MM-JJ entre lesquelles la tuile s'affiche (incluses), ou null pour toujours.
          « action », ce que fait la tuile au toucher : {"type":"media","content":"O1"} ouvre un contenu (repère O de la liste « Contenus ») ; {"type":"page","page":"P2"} ouvre une autre page de la liste ; {"type":"home"} retourne à l'accueil ; {"type":"back"} revient à la page précédente ; {"type":"none"} ne fait rien.
          {{{(qr ? "Avec « media », on peut ajouter \"qr\":{\"url\":\"https://…\",\"label\":\"texte sous le code, ou null\"} : un QR code « Emporter sur mon téléphone » montré avec le contenu ; \"qr\":null le retire. L'adresse vient de la demande, jamais de toi."
            : "Le QR code « Emporter sur mon téléphone » n'est pas disponible dans cette organisation : ne mets pas de « qr ».")}}}
        - bouton de navigation : une tuile basse, au texte centré, par exemple {"kind":"tile","x":25,"y":84,"w":50,"h":{{{(portrait ? "5.6" : "9")}}},"text":"Accueil","align":"center","valign":"middle","radius":6,"size":3,"color":"#E8833A","action":{"type":"home"}} (de même « ← Retour » avec back, ou un bouton vers une page).
        - texte (ne se touche pas) : {"kind":"text","x":5,"y":4,"w":90,"h":7,"text":"...","textColor":"#FFFFFF","font":null,"size":6,"bold":true,"align":"left"} ; 120 caractères au plus ; « size » : 6 pour un titre, 3.4 pour un sous-titre, 2.4 pour du texte courant, 9 au plus.
        - image (ne se touche pas : logo, photo, pictogramme) : {"kind":"image","x":30,"y":5,"w":40,"h":12,"image":"I1"} ; seulement un repère I de la liste « Images ».
        {{{(edit ? EditRules : "Aucun élément n'a d'« id » : donne tous les champs de chacun.")}}}
        MISE EN PAGE
        - Marges de 5 % ; éléments alignés (mêmes x, mêmes largeurs, mêmes hauteurs dans une rangée) et écarts égaux ; rien ne se chevauche et rien ne dépasse de la page.
        - Dispositions usuelles : grille de 2 colonnes (w = 43.5 ; x = 5 et 51.5), grille de 3 colonnes (w = 28 ; x = 5, 36 et 67), liste verticale de tuiles larges (x = 5, w = 90), tuile large pleine largeur.
        - Un titre en haut de chaque page, des titres de tuile courts, un texte très contrasté avec son fond. Couleurs de tuile conseillées : #3B82C4, #2E7D5B, #C77700, #8A5CF6, #D0453C, #5A6B80, #E8833A ; fonds de page : #0B1F3A, #16335C, #101822, #FFFFFF, #F5F7FA (texte foncé #0B1F3A sur fond clair).
        - Une tuile qui mène à une sous-page : crée cette page et relie-la. Si le contenu qu'une tuile doit ouvrir n'est pas dans la liste « Contenus », mets {"type":"none"} et dis-le dans « summary ».
        N'invente rien : ni contenu, ni image, ni adresse, ni horaire, ni prix. Écris en français, sauf demande contraire.
        """;

    // ---------- Ce que l'IA peut utiliser ----------

    private static readonly Dictionary<string, string> CatLabels = new()
    { ["media"] = "média", ["pdf"] = "PDF", ["pres"] = "présentation", ["compo"] = "composition", ["web"] = "page web", ["other"] = "application" };

    /// <summary>
    /// Images et contenus proposés à l'IA, vus par la personne connectée : d'abord ceux que la liste utilise déjà, puis ceux dont le nom répond à la demande,
    /// puis les autres (images récentes, contenus par nom).
    /// </summary>
    public static IListCatalog Catalog(ClientDb d, InteractiveList? list, string request, int maxImages = 30, int maxContents = 70)
    {
        var terms = AiMedia.Terms(request).Where(t => t.Length > 2).ToList();
        var usedImages = new HashSet<Guid>(); var usedContents = new HashSet<Guid>();
        foreach (var p in list?.Pages ?? new())
        {
            if (p.BgImageId is { } b) usedImages.Add(b);
            foreach (var e in p.Els)
            {
                if (e.ImageId is { } i) usedImages.Add(i);
                if (e.Action?.MediaId is { } m) usedContents.Add(m);
            }
        }
        int Score(MediaItem m) { var hay = AiMedia.Norm(string.Join(' ', new[] { m.Name, m.AiDesc ?? "" }.Concat(m.AiTags))); return terms.Count(hay.Contains); }
        var images = d.Media.Where(m => m.Type == "image" && m.Status == null && m.FileName != null)
            .OrderByDescending(m => usedImages.Contains(m.Id)).ThenByDescending(Score).ThenByDescending(m => m.AddedUtc).Take(maxImages)
            .Select(m => (m.Id, AiCompose.Describe(m))).ToList();
        var contents = d.Media.Where(m => Kiosk.CanOpen(m) && !Pres.IsPage(m))
            .OrderByDescending(m => usedContents.Contains(m.Id)).ThenByDescending(Score).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).Take(maxContents)
            .Select(m => (m.Id, m.Name.Replace('\n', ' ') + " · " + CatLabels.GetValueOrDefault(InteractiveCats.Of(m), "contenu"))).ToList();
        return new IListCatalog(images, contents, d.Tenant.IsGrowth);
    }

    /// <summary>Repères donnés à l'IA : pages (P), éléments (E), images (I), contenus (O).</summary>
    public class Refs
    {
        public Dictionary<string, Guid> Pages { get; } = new();
        public Dictionary<string, KioskEl> Els { get; } = new();
        public Dictionary<string, Guid> Images { get; } = new();
        public Dictionary<string, Guid> Contents { get; } = new();

        public static Refs Of(InteractiveList? list, IListCatalog cat)
        {
            var r = new Refs();
            foreach (var (id, _) in cat.Images) r.Images["I" + (r.Images.Count + 1)] = id;
            foreach (var (id, _) in cat.Contents) r.Contents["O" + (r.Contents.Count + 1)] = id;
            foreach (var p in list?.Pages ?? new())
            {
                r.Pages["P" + (r.Pages.Count + 1)] = p.Id;
                foreach (var e in p.Els) r.Els["E" + (r.Els.Count + 1)] = e;
            }
            return r;
        }

        internal string? Image(Guid? id) => id is { } g ? Images.FirstOrDefault(kv => kv.Value == g).Key : null;
        internal string? Content(Guid? id) => id is { } g ? Contents.FirstOrDefault(kv => kv.Value == g).Key : null;
        internal string? Page(Guid? id) => id is { } g ? Pages.FirstOrDefault(kv => kv.Value == g).Key : null;
    }

    /// <summary>La liste telle que l'IA la lit : repères à la place des identifiants, champs utiles à chaque sorte d'élément.</summary>
    public static JsonObject Show(InteractiveList l, Refs refs)
    {
        var pages = new JsonArray();
        foreach (var p in l.Pages)
        {
            var els = new JsonArray();
            foreach (var e in p.Els)
            {
                var o = new JsonObject { ["id"] = refs.Els.FirstOrDefault(kv => ReferenceEquals(kv.Value, e)).Key, ["kind"] = e.Kind, ["x"] = R(e.X), ["y"] = R(e.Y), ["w"] = R(e.W), ["h"] = R(e.H) };
                if (e.Kind == "image") { o["image"] = refs.Image(e.ImageId); els.Add(o); continue; }
                o["text"] = e.Text;
                if (e.Kind == "tile") { o["sub"] = e.Sub; o["color"] = e.Color; o["image"] = refs.Image(e.ImageId); }
                o["textColor"] = e.TextColor; o["font"] = e.Font; o["size"] = R(e.FontSize); o["bold"] = e.Bold; o["align"] = e.Align;
                if (e.Kind == "tile")
                {
                    o["valign"] = e.Valign; o["radius"] = R(e.Radius); o["from"] = e.ShowFrom; o["to"] = e.ShowTo;
                    var a = new JsonObject { ["type"] = e.Action.Type };
                    if (e.Action.Type == "media")
                    {
                        a["content"] = refs.Content(e.Action.MediaId);
                        if (e.Action.Item is { QrOn: true } it) a["qr"] = new JsonObject { ["url"] = it.QrUrl, ["label"] = it.QrLabel };
                    }
                    if (e.Action.Type == "page") a["page"] = refs.Page(e.Action.PageId);
                    o["action"] = a;
                }
                els.Add(o);
            }
            pages.Add(new JsonObject { ["id"] = refs.Page(p.Id), ["name"] = p.Name, ["bg"] = p.Bg, ["bgImage"] = refs.Image(p.BgImageId), ["len"] = p.Len, ["els"] = els });
        }
        return new JsonObject { ["name"] = l.Name, ["orientation"] = l.Orientation, ["pages"] = pages };
    }

    private static double R(double v) => Math.Round(v, 2);

    private static string Lists(IListCatalog cat, Refs refs)
    {
        var sb = new StringBuilder("\n\nImages (médiathèque) :\n");
        if (cat.Images.Count == 0) sb.Append("(aucune image : n'utilise ni « image » ni « bgImage »)\n");
        foreach (var (id, text) in cat.Images) sb.Append(refs.Image(id)).Append(" · ").Append(text).Append('\n');
        sb.Append("\nContenus qu'une tuile peut ouvrir :\n");
        if (cat.Contents.Count == 0) sb.Append("(aucun contenu : pas d'action « media »)\n");
        foreach (var (id, text) in cat.Contents) sb.Append(refs.Content(id)).Append(" · ").Append(text).Append('\n');
        return sb.ToString();
    }

    // ---------- Créer, modifier ----------

    /// <summary>Une nouvelle liste interactive depuis une description (pages, tuiles, navigation, contenus à ouvrir).</summary>
    public async Task<AiIListResult> Create(Guid clientId, string name, string sentence, bool portrait, IListCatalog cat, CancellationToken ct = default)
    {
        sentence = (sentence ?? "").Trim();
        if (sentence.Length < 4) return AiIListResult.Fail("Décrivez en une phrase ce que la borne doit proposer.");
        if (sentence.Length > MaxChange) return AiIListResult.Fail("Cette description est trop longue : une ou deux phrases suffisent.");
        var refs = Refs.Of(null, cat);
        var prompt = $"Liste à créer (format {(portrait ? "portrait" : "paysage")}), nommée « {name} » : {sentence}{Lists(cat, refs)}";
        var r = await ai.Complete(clientId, "compose", System(portrait, cat.Qr, false), new UserMessageParam { Content = prompt }, 8000, ct);
        if (!r.Ok) return AiIListResult.Fail(r.Message);
        if (Parse(r.Text) is not { } answer) return AiIListResult.Fail("L'IA n'a pas réussi cette liste. Reformulez, ou créez-la dans l'éditeur.");
        answer["orientation"] = portrait ? "portrait" : "landscape";   // le format demandé fait foi
        return Done(null, answer, refs, name, "Liste interactive créée.");
    }

    /// <summary>Modifie une liste interactive selon une demande en langage courant. Rien n'est enregistré ici : le document rendu passe par <see cref="InteractiveLists.Apply"/>.</summary>
    public async Task<AiIListResult> Edit(Guid clientId, InteractiveList list, string change, IListCatalog cat, CancellationToken ct = default)
    {
        change = (change ?? "").Trim();
        if (change.Length < 3) return AiIListResult.Fail("Dites ce qu'il faut changer dans la liste interactive.");
        if (change.Length > MaxChange) return AiIListResult.Fail("Cette demande est trop longue : une ou deux phrases suffisent.");
        var refs = Refs.Of(list, cat);
        var prompt = "Liste actuelle :\n" + Show(list, refs).ToJsonString() + Lists(cat, refs) + "\n\nModification demandée : " + change;
        var r = await ai.Complete(clientId, "compose", System(InteractiveLists.Portrait(list.Orientation), cat.Qr, true), new UserMessageParam { Content = prompt }, 8000, ct);
        if (!r.Ok) return AiIListResult.Fail(r.Message);
        if (Parse(r.Text) is not { } answer) return AiIListResult.Fail("L'IA n'a pas réussi cette modification. Reformulez, ou faites-la dans l'éditeur.");
        return Done(list, answer, refs, list.Name, "Liste interactive modifiée.");
    }

    private AiIListResult Done(InteractiveList? list, JsonObject answer, Refs refs, string name, string dflt)
    {
        try
        {
            if (Merge(list, answer, refs) is not { } m) return AiIListResult.Fail("L'IA a rendu une liste vide ou illisible : rien n'a été changé.");
            var newName = Str(answer, "name") is { } nn && nn.Trim().Length > 0 ? InteractiveLists.Cap(nn, 80) : name;
            var summary = Str(answer, "summary") is { Length: > 0 } s ? (s.Length > 300 ? s[..300] : s) : dflt;
            return new AiIListResult(true, list == null ? name : newName, JsonSerializer.Serialize(new { orientation = m.Orientation, pages = m.Pages }, Web), "", summary, m.Loose);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            log.LogInformation(e, "Réponse de l'IA illisible pour la liste interactive.");
            return AiIListResult.Fail("L'IA a rendu une liste illisible : rien n'a été changé.");
        }
    }

    private static JsonObject? Parse(string text)
    {
        var a = text.IndexOf('{'); var b = text.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try { return JsonNode.Parse(text[a..(b + 1)]) as JsonObject; } catch (JsonException) { return null; }
    }

    // ---------- Fusion de la réponse avec la liste ----------

    public record Merged(string Orientation, List<KioskPage> Pages, int Loose);

    /// <summary>
    /// Fusionne la réponse de l'IA avec la liste (null : nouvelle liste). Une page ou un élément existant (même repère) garde ce que l'IA ne renvoie pas,
    /// y compris le code du QR ; ce qui est absent est supprimé ; un repère d'élément répété donne une copie. Images, contenus et pages ne viennent que
    /// de repères connus : une tuile dont la cible est inconnue ne fait rien (comptée dans <see cref="Merged.Loose"/>). Rend null si la réponse n'a aucune
    /// page, ou viderait une liste qui avait des éléments.
    /// </summary>
    public static Merged? Merge(InteractiveList? list, JsonObject answer, Refs refs)
    {
        var src = (answer["pages"] as JsonArray ?? new()).OfType<JsonObject>().Take(InteractiveLists.MaxPages).ToList();
        if (src.Count == 0) return null;
        var oldPages = (list?.Pages ?? new()).ToDictionary(p => p.Id);
        // identifiants des pages d'abord : une tuile peut mener à une page placée plus loin, ou créée dans la même réponse
        var keys = new Dictionary<string, Guid>(); var pageIds = new List<Guid>(); var taken = new HashSet<Guid>();
        foreach (var p in src)
        {
            var key = Str(p, "id");
            var id = key != null && refs.Pages.TryGetValue(key, out var g) && taken.Add(g) ? g : Guid.NewGuid();
            if (key != null) keys.TryAdd(key, id);
            pageIds.Add(id);
        }
        var loose = 0; var usedEls = new HashSet<string>();
        var pages = new List<KioskPage>();
        for (var i = 0; i < src.Count; i++)
        {
            var p = src[i];
            var prev = oldPages.GetValueOrDefault(pageIds[i]);
            var page = new KioskPage { Id = pageIds[i], Name = prev?.Name ?? (i == 0 ? "Accueil" : "Page " + (i + 1)), Bg = prev?.Bg ?? "#0B1F3A", BgImageId = prev?.BgImageId, Len = prev?.Len ?? 1 };
            if (Str(p, "name") is { Length: > 0 } n) page.Name = n;
            if (Str(p, "bg") is { } bg) page.Bg = bg;
            if (p.ContainsKey("bgImage")) page.BgImageId = Str(p, "bgImage") is { } bi ? (refs.Images.TryGetValue(bi, out var big) ? big : page.BgImageId) : null;
            if (Dbl(p, "len") is { } len) page.Len = (int)Math.Clamp(Math.Round(len), 1, InteractiveLists.MaxLen);
            foreach (var e in (p["els"] as JsonArray ?? new()).OfType<JsonObject>().Take(InteractiveLists.MaxEls))
                if (El(e, refs, keys, usedEls, ref loose) is { } el) page.Els.Add(el);
            pages.Add(page);
        }
        if (list != null && list.Pages.Any(p => p.Els.Count > 0) && pages.All(p => p.Els.Count == 0)) return null;
        // une page supprimée : les tuiles qui y menaient ne font plus rien
        var alive = pages.Select(p => p.Id).ToHashSet();
        foreach (var e in pages.SelectMany(p => p.Els).Where(e => e.Action.Type == "page" && (e.Action.PageId is not { } pg || !alive.Contains(pg)))) { e.Action = new(); loose++; }
        var o = Str(answer, "orientation");
        var orientation = o is "portrait" or "landscape" ? o : o == "paysage" ? "landscape" : list?.Orientation ?? "portrait";
        return new Merged(orientation, pages, loose);
    }

    private static KioskEl? El(JsonObject e, Refs refs, Dictionary<string, Guid> pageKeys, HashSet<string> used, ref int loose)
    {
        KioskEl el;
        var prev = Str(e, "id") is { } id && refs.Els.TryGetValue(id, out var pe) ? pe : null;
        if (prev != null)
        {
            el = JsonSerializer.Deserialize<KioskEl>(JsonSerializer.Serialize(prev))!;
            if (!used.Add(Str(e, "id")!)) { el.Id = Guid.NewGuid(); el.Action.Item.Id = Guid.NewGuid(); el.Action.Item.QrCode = null; }   // repère répété : une copie, avec son propre compteur de QR
        }
        else
        {
            var kind = Str(e, "kind") is "text" or "image" ? Str(e, "kind")! : "tile";
            el = new KioskEl { Kind = kind };
            if (kind == "image" && (Str(e, "image") is not { } ik || !refs.Images.ContainsKey(ik))) return null;   // une image inconnue ne se pose pas
            if (kind == "text" && (Str(e, "text") ?? "").Trim().Length == 0) return null;
        }
        if (Dbl(e, "x") is { } x) el.X = x;
        if (Dbl(e, "y") is { } y) el.Y = y;
        if (Dbl(e, "w") is { } w) el.W = w;
        if (Dbl(e, "h") is { } h) el.H = h;
        if (Str(e, "text") is { } text) el.Text = text.Replace("\r", "");
        if (e.ContainsKey("sub")) el.Sub = Str(e, "sub");
        if (Str(e, "color") is { } c) el.Color = c;
        if (Str(e, "textColor") is { } tc) el.TextColor = tc;
        if (e.ContainsKey("font")) el.Font = Str(e, "font") is { Length: > 0 } f ? f.ToLowerInvariant() : null;
        if (Dbl(e, "size") is { } size) el.FontSize = size;
        if (e["bold"] is JsonValue bv && bv.TryGetValue<bool>(out var bold)) el.Bold = bold;
        if (Str(e, "align") is { } al) el.Align = al;
        if (Str(e, "valign") is { } va) el.Valign = va;
        if (Dbl(e, "radius") is { } rad) el.Radius = rad;
        if (e.ContainsKey("from")) el.ShowFrom = Str(e, "from");
        if (e.ContainsKey("to")) el.ShowTo = Str(e, "to");
        if (e.ContainsKey("image")) el.ImageId = Str(e, "image") is { } im ? (refs.Images.TryGetValue(im, out var ig) ? ig : el.ImageId) : null;
        if (el.Kind == "tile" && e["action"] is JsonObject a) Action(el, a, refs, pageKeys, ref loose);
        return el;
    }

    private static void Action(KioskEl el, JsonObject a, Refs refs, Dictionary<string, Guid> pageKeys, ref int loose)
    {
        var before = el.Action;
        switch (Str(a, "type"))
        {
            case "media":
                if (Str(a, "content") is { } ck && refs.Contents.TryGetValue(ck, out var media))
                {
                    var item = before.Type == "media" && before.MediaId == media ? before.Item : new PlaylistItem();   // même contenu : son QR et son compteur restent
                    el.Action = new KioskAction { Type = "media", MediaId = media, Item = item };
                }
                else if (before.Type != "media") { el.Action = new(); loose++; return; }   // contenu inconnu : la tuile garde ce qu'elle ouvrait, sinon ne fait rien
                if (a.ContainsKey("qr"))
                {
                    var it = el.Action.Item;
                    if (a["qr"] is JsonObject q && Str(q, "url") is { Length: > 0 } url) { it.QrOn = true; it.QrUrl = url.Trim(); it.QrLabel = Str(q, "label"); }
                    else it.QrOn = false;
                }
                break;
            case "page":
                if (Str(a, "page") is { } pk && (pageKeys.TryGetValue(pk, out var pg) || refs.Pages.TryGetValue(pk, out pg))) el.Action = new KioskAction { Type = "page", PageId = pg };
                else { el.Action = new(); loose++; }
                break;
            case "home": el.Action = new KioskAction { Type = "home" }; break;
            case "back": el.Action = new KioskAction { Type = "back" }; break;
            case "none": el.Action = new(); break;
        }
    }

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Dbl(JsonObject o, string k)
    {
        if (o[k] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : null;
        return v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, Inv, out var ds) && double.IsFinite(ds) ? ds : null;
    }
}
