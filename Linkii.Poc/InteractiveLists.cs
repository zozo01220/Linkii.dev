using System.Text.Json;

namespace Linkii.Poc;

/// <summary>Ce qu'un élément de page fait au toucher : rien, ouvrir un contenu, ouvrir une autre page de la liste, retourner à l'accueil ou revenir en arrière.</summary>
public class KioskAction
{
    public string Type { get; set; } = "none";   // none | media | page | home | back
    public Guid? MediaId { get; set; }
    public Guid? PageId { get; set; }
    /// <summary>Identifiant publié du contenu ouvert et QR « Emporter sur mon téléphone » (mêmes champs et même comptage des scans que les listes de lecture).</summary>
    public PlaylistItem Item { get; set; } = new();
}

/// <summary>Un élément posé sur une page de liste interactive : tuile (touchable), texte ou image. Position et taille en % de la page (largeur pour X et W, hauteur pour Y et H).</summary>
public class KioskEl
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "tile";   // tile | text | image
    public double X { get; set; } = 10;
    public double Y { get; set; } = 10;
    public double W { get; set; } = 30;
    public double H { get; set; } = 15;
    public string Text { get; set; } = "";
    public string? Sub { get; set; }             // sous-titre d'une tuile
    public string Color { get; set; } = "#3B82C4";   // fond d'une tuile
    public Guid? ImageId { get; set; }           // image de la médiathèque : fond d'une tuile ou élément image
    public string TextColor { get; set; } = "#FFFFFF";
    public string? Font { get; set; }            // clé de Kiosk.Fonts ; null = police par défaut
    public double FontSize { get; set; } = 3.4;  // en % de la largeur de la page
    public bool Bold { get; set; }
    public string Align { get; set; } = "left";    // left | center | right
    public string Valign { get; set; } = "bottom"; // top | middle | bottom (tuile)
    public double Radius { get; set; } = 1.6;    // arrondi en % de la largeur de la page
    public string? ShowFrom { get; set; }        // aaaa-mm-jj : tuile affichée à partir de…
    public string? ShowTo { get; set; }          // …jusqu'à (inclus)
    public KioskAction Action { get; set; } = new();
}

public class KioskPage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Accueil";
    public string Bg { get; set; } = "#0B1F3A";
    public Guid? BgImageId { get; set; }
    /// <summary>Longueur de la page en écrans (1 à 6) : au-delà de 1, le visiteur la fait défiler vers le bas. Y et H des éléments restent en % de la hauteur d'UN écran.</summary>
    public int Len { get; set; } = 1;
    public List<KioskEl> Els { get; set; } = new();
}

/// <summary>
/// Liste interactive : l'environnement tactile d'une borne (pages, tuiles, textes, images, fond). Elle se crée dans Listes de lecture › Listes interactives
/// et s'assigne à une ou plusieurs bornes ; la première page est l'accueil. Les tuiles ouvrent des contenus de la médiathèque ou d'autres pages.
/// </summary>
public class InteractiveList : IClientOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public string Name { get; set; } = "";
    public string Orientation { get; set; } = "portrait";   // portrait | landscape
    public List<KioskPage> Pages { get; set; } = new();
    public int Revision { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public class PublishedKioskAction
{
    public string Type { get; set; } = "none";
    public Guid? PageId { get; set; }
    public PublishedItem? Item { get; set; }
}

public class PublishedKioskEl
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "tile";
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public string Text { get; set; } = "";
    public string? Sub { get; set; }
    public string Color { get; set; } = "#3B82C4";
    public string? ImageUrl { get; set; }
    public string TextColor { get; set; } = "#FFFFFF";
    public string? Font { get; set; }
    public double FontSize { get; set; }
    public bool Bold { get; set; }
    public string Align { get; set; } = "left";
    public string Valign { get; set; } = "bottom";
    public double Radius { get; set; }
    public string? ShowFrom { get; set; }
    public string? ShowTo { get; set; }
    public PublishedKioskAction Action { get; set; } = new();
}

public class PublishedKioskPage
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Bg { get; set; } = "#0B1F3A";
    public string? BgImageUrl { get; set; }
    public int Len { get; set; } = 1;
    public List<PublishedKioskEl> Els { get; set; } = new();
}

public static class InteractiveLists
{
    public const int MaxPages = 30, MaxEls = 80, MaxLists = 100, MaxLen = 6;
    public static readonly string[] Palette = { "#3B82C4", "#2E7D5B", "#C77700", "#8A5CF6", "#D0453C", "#5A6B80", "#0B1F3A", "#FFFFFF" };
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static bool Portrait(string? o) => o == "portrait";

    public static string Cap(string? s, int max) { var t = (s ?? "").Trim(); return t.Length > max ? t[..max] : t; }

    /// <summary>Document de l'éditeur (JSON, noms en camelCase) : pages et éléments.</summary>
    public static string ToDoc(InteractiveList l) => JsonSerializer.Serialize(new { orientation = l.Orientation, pages = l.Pages }, Web);

    /// <summary>Couleur #rrggbb valide, sinon la valeur de repli.</summary>
    public static string Hex(string? c, string fallback) => Kiosk.CleanHex(c) is { Length: > 0 } h ? h : fallback;

    private static double Pct(double v, double min, double max, double fallback) => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

    /// <summary>Nouvelle liste avec une page d'accueil : un titre et un message d'invite.</summary>
    public static InteractiveList Starter(string name, string orientation)
    {
        var page = new KioskPage { Name = "Accueil" };
        var port = Portrait(orientation);
        page.Els.Add(new KioskEl { Kind = "text", X = 5, Y = port ? 4 : 6, W = 90, H = port ? 7 : 12, Text = "Bienvenue", TextColor = "#FFFFFF", FontSize = port ? 6 : 4.5, Bold = true, Align = "left" });
        page.Els.Add(new KioskEl { Kind = "text", X = 5, Y = port ? 11.5 : 19, W = 90, H = port ? 4 : 8, Text = "Touchez une tuile pour commencer.", TextColor = "#C5CED9", FontSize = port ? 2.6 : 2, Align = "left" });
        return new InteractiveList { Name = name, Orientation = port ? "portrait" : "landscape", Pages = { page } };
    }

    /// <summary>Valide et range un document venu de l'éditeur : tailles bornées, couleurs et dates valides, actions cohérentes. Rend le message d'erreur, ou null.</summary>
    public static string? Apply(InteractiveList l, string name, string doc, Func<Guid, MediaItem?> media, bool growth, string origin)
    {
        name = Cap(name, 80);
        if (name.Length == 0) return "Donnez un nom à la liste interactive.";
        if (doc.Length > 2_000_000) return "La liste est trop grande : elle n'a pas été enregistrée.";
        Doc? d;
        try { d = JsonSerializer.Deserialize<Doc>(doc, Web); } catch (JsonException) { return "La liste est illisible : elle n'a pas été enregistrée."; }
        if (d?.Pages == null || d.Pages.Count == 0) return "Une liste interactive a au moins une page.";
        if (d.Pages.Count > MaxPages) return $"Au plus {MaxPages} pages par liste.";
        var pages = d.Pages;
        var ids = new HashSet<Guid>();
        foreach (var p in pages)
        {
            if (p.Id == Guid.Empty || !ids.Add(p.Id)) { p.Id = Guid.NewGuid(); ids.Add(p.Id); }
            p.Name = Cap(p.Name, 40); if (p.Name.Length == 0) p.Name = "Page";
            p.Bg = Hex(p.Bg, "#0B1F3A");
            p.Len = Math.Clamp(p.Len, 1, MaxLen);
            if (p.BgImageId is { } bi && media(bi) is not { Type: "image" }) p.BgImageId = null;
            if (p.Els.Count > MaxEls) return $"Au plus {MaxEls} éléments par page.";
        }
        var elIds = new HashSet<Guid>();
        foreach (var p in pages)
            foreach (var e in p.Els)
            {
                var span = 100.0 * p.Len;   // hauteur de la page en % d'un écran
                if (e.Id == Guid.Empty || !elIds.Add(e.Id)) { e.Id = Guid.NewGuid(); elIds.Add(e.Id); }
                e.Kind = e.Kind is "text" or "image" ? e.Kind : "tile";
                e.W = Pct(e.W, 2, 100, 30); e.H = Pct(e.H, 1, span, 15);
                e.X = Pct(e.X, -50, 100, 10); e.Y = Pct(e.Y, -50, span, 10);
                e.Text = Cap(e.Text, 120); e.Sub = Cap(e.Sub, 160) is { Length: > 0 } sb ? sb : null;
                e.Color = Hex(e.Color, "#3B82C4"); e.TextColor = Hex(e.TextColor, "#FFFFFF");
                e.Font = Kiosk.Fonts.Any(f => f.Key == e.Font && f.Key != "") ? e.Font : null;
                e.FontSize = Pct(e.FontSize, 1, 30, 3.4);
                e.Align = e.Align is "center" or "right" ? e.Align : "left";
                e.Valign = e.Valign is "top" or "middle" ? e.Valign : "bottom";
                e.Radius = Pct(e.Radius, 0, 12, 1.6);
                if (e.ImageId is { } im && media(im) is not { Type: "image" }) e.ImageId = null;
                e.ShowFrom = Planning.IsDate(e.ShowFrom) ? e.ShowFrom : null;
                e.ShowTo = Planning.IsDate(e.ShowTo) ? e.ShowTo : null;
                if (e.ShowFrom != null && e.ShowTo != null && string.CompareOrdinal(e.ShowFrom, e.ShowTo) > 0) return $"Tuile « {e.Text} » : la date de fin précède celle de début.";
                var a = e.Action ??= new();
                if (e.Kind != "tile") { e.Action = new(); continue; }
                a.Type = a.Type is "media" or "page" or "home" or "back" ? a.Type : "none";
                if (a.Type == "media" && (a.MediaId is not { } mid || media(mid) is not { } mm || !Kiosk.CanOpen(mm))) return $"Tuile « {e.Text} » : choisissez le contenu qu'elle ouvre.";
                if (a.Type == "page" && (a.PageId is not { } pg || !ids.Contains(pg))) return $"Tuile « {e.Text} » : la page à ouvrir n'existe plus.";
                if (a.Type != "media") { a.MediaId = null; a.Item = new(); }
                if (a.Type != "page") a.PageId = null;
                if (a.Type == "media")
                {
                    var it = a.Item ??= new();
                    it.MediaId = a.MediaId!.Value;
                    if (it.QrOn)
                    {
                        if (!growth) it.QrOn = false;
                        else if (QrTracking.Destination(it.QrUrl) is not { } dest) return $"Tuile « {e.Text} » : saisissez l'adresse (https://…) que le QR code doit ouvrir.";
                        else
                        {
                            it.QrUrl = dest;
                            if (!QrTracking.IsCode(it.QrCode)) it.QrCode = QrTracking.NewCode();   // le code ne change plus : le QR affiché reste valable
                            it.QrBase = QrTracking.Origin(origin);
                            it.QrLabel = QrTracking.CleanLabel(it.QrLabel);
                        }
                    }
                }
            }
        l.Name = name;
        l.Orientation = Portrait(d.Orientation) ? "portrait" : "landscape";
        l.Pages = pages;
        l.Revision++;
        l.UpdatedUtc = DateTime.UtcNow;
        return null;
    }

    private class Doc { public string? Orientation { get; set; } public List<KioskPage> Pages { get; set; } = new(); }

    /// <summary>Bornes qui affichent cette liste.</summary>
    public static List<Screen> UsedBy(IEnumerable<Screen> screens, Guid listId) => screens.Where(s => s.Kiosk?.ListId == listId).ToList();

    /// <summary>Un changement de la liste laisse ses bornes avec des modifications à publier.</summary>
    public static void Touch(IEnumerable<Screen> screens, Guid listId) { foreach (var s in UsedBy(screens, listId)) s.KioskRevision++; }

    /// <summary>
    /// Migration (3 novembre 2026) : le menu de chaque borne (titre, tuiles, logo, fond) devient une liste interactive de même nom que la borne,
    /// avec une page d'accueil aux mêmes tuiles. Une borne qui a déjà sa liste n'est pas touchée.
    /// </summary>
    public static int Migrate(Db db)
    {
        var n = 0;
        foreach (var s in db.Screens.Where(x => x.Kiosk is { ListId: null }))
        {
            var l = FromLegacy(s, s.Kiosk!, db.Media);
            l.ClientId = s.ClientId;
            db.InteractiveLists.Add(l);
            s.Kiosk!.ListId = l.Id;
            s.KioskRevision++;
            n++;
        }
        return n;
    }

    /// <summary>Menu d'une borne (ancien format) en liste interactive : la grille de tuiles garde ses colonnes, son espacement et son aspect.</summary>
    public static InteractiveList FromLegacy(Screen s, KioskConfig k, IEnumerable<MediaItem> media)
    {
        var port = Portrait(s.Orientation);
        var l = new InteractiveList { Name = Cap("Accueil — " + s.Name, 80), Orientation = port ? "portrait" : "landscape" };
        var page = new KioskPage { Name = "Accueil", Bg = Hex(k.Bg, "#FFFFFF") };
        l.Pages.Add(page);
        var ratio = port ? 9.0 / 16 : 16.0 / 9;   // largeur / hauteur de la page : convertit une longueur en % de la largeur en % de la hauteur
        double Hh(double wPct) => wPct * ratio;
        var titleColor = Hex(k.TitleColor, Kiosk.TitleDefault);
        var promptColor = Hex(k.PromptColor, Kiosk.PromptDefault);
        double top = 5;
        if (k.LogoMediaId is { } lg && media.FirstOrDefault(m => m.Id == lg && m.Type == "image") is { } lm)
        {
            var w = k.LogoW ?? 14;
            var aspect = lm.Width is > 0 && lm.Height is > 0 ? (double)lm.Height!.Value / lm.Width!.Value : 0.4;
            var h = Math.Clamp(Hh(w * aspect), 2, 20);
            page.Els.Add(new KioskEl { Kind = "image", ImageId = lg, X = k.LogoX ?? 5, Y = k.LogoY ?? 3, W = w, H = h });
            if (k.LogoX == null) top = 3 + h + 1;
        }
        var tsize = (k.BigText ? 4.2 : 3.4);
        page.Els.Add(new KioskEl { Kind = "text", X = 5, Y = top, W = 90, H = Hh(7), Text = k.Title, TextColor = titleColor, Font = FontKey(k.TitleFont), FontSize = 6, Align = "left" });
        page.Els.Add(new KioskEl { Kind = "text", X = 5, Y = top + Hh(7.5), W = 90, H = Hh(3.2), Text = k.Prompt, TextColor = promptColor, Font = FontKey(k.PromptFont), FontSize = 2.4, Align = "left" });
        var shown = k.Tiles.Where(t => media.FirstOrDefault(m => m.Id == t.MediaId) is { } mm && Kiosk.CanOpen(mm)).ToList();
        var cols = k.TileCols is > 0 and <= 4 ? k.TileCols : shown.Count <= 1 ? 1 : port ? 2 : shown.Count <= 4 ? 2 : 3;
        var rows = Math.Max(1, (int)Math.Ceiling(shown.Count / (double)cols));
        var gap = Kiosk.Pick(Kiosk.Gaps, k.TileGap, "normal").Value;
        var radius = Kiosk.Pick(Kiosk.Radii, k.TileRadius, "round").Value;
        var y0 = top + Hh(12.5); var y1 = 97.0;
        var cw = (90 - gap * (cols - 1)) / cols;
        var ch = Math.Min((y1 - y0 - Hh(gap) * (rows - 1)) / rows, Hh(cw) * 1.25);
        for (var i = 0; i < shown.Count; i++)
        {
            var t = shown[i];
            var r = i / cols; var c = i % cols;
            var tc = Kiosk.CleanHex(t.TitleColor) is { Length: > 0 } x ? x : Hex(k.TileColor, "#FFFFFF");
            page.Els.Add(new KioskEl
            {
                Kind = "tile", X = 5 + c * (cw + gap), Y = y0 + r * (ch + Hh(gap)), W = cw, H = ch,
                Text = t.Title, Sub = t.Subtitle, Color = Kiosk.CleanColor(t.Color), ImageId = t.ImageMediaId, TextColor = tc, Font = FontKey(k.TileFont),
                FontSize = tsize * t.TextScale * Kiosk.CleanScale(k.TileScale), Bold = k.TileWeight == "bold", Align = Kiosk.Pick(Kiosk.Aligns, k.TileAlign, "left").Key,
                Valign = Kiosk.Pick(Kiosk.Valigns, k.TileValign, "bottom").Key, Radius = radius, ShowFrom = t.ShowFrom, ShowTo = t.ShowTo,
                Action = new KioskAction { Type = "media", MediaId = t.MediaId, Item = t.Item }
            });
        }
        return l;
    }

    private static string? FontKey(string? k) => Kiosk.Fonts.Any(f => f.Key == k && f.Key != "") ? k : null;
}

public static class InteractiveCats
{
    /// <summary>Catégorie d'un contenu dans le sélecteur d'une tuile.</summary>
    public static string Of(MediaItem m) =>
        m.Type is "image" or "video" || m.AppId == "image" ? "media"
        : m.AppId == "pdf" ? "pdf"
        : m.AppId is "ms-powerpoint" or "google-slides" or "google-docs" or "canva" ? "pres"
        : m.AppId == Compositions.AppId ? "compo"
        : m.AppId == "webpage" ? "web" : "other";
}
