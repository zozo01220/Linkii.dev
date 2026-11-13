using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

// Premiers pas avec l'assistant (DEV-POC §232, maquette maquettes/premiers-pas-assistant-ia.html) : l'IA écrit le contenu des pages
// (textes, couleurs, police, choix du logo et des photos) ; la mise en page vient des modèles ci-dessous, pour un rendu toujours propre.
// Les retouches (« Plus chaleureux », « Aussi en anglais », « Version portrait », demande libre) modifient ce contenu, puis les pages sont redessinées.
// §248 : chaque type de page a plusieurs mises en page (<see cref="StartBrief.Layouts"/>) et le fond sans photo plusieurs décors
// (<see cref="StartBrief.Decor"/>) ; les pages facultatives (prix, offre, chiffres, avis…) sont choisies par l'IA selon l'activité ;
// chaque page a sa propre photo (<see cref="StartBrief.PagePhotos"/>).

/// <summary>Le contenu de l'écran préparé par l'assistant : identité de l'organisation et textes des pages.</summary>
public class StartBrief
{
    public string Name { get; set; } = "";
    public string Activity { get; set; } = "";
    public string? City { get; set; }
    public string Goal { get; set; } = "";
    public string Tone { get; set; } = "";
    public string Font { get; set; } = "Montserrat";
    public StartColors Colors { get; set; } = new();
    public StartPages Pages { get; set; } = new();
    /// <summary>Les mêmes textes en anglais (« Aussi en anglais »), null tant qu'ils n'ont pas été demandés.</summary>
    public StartPages? En { get; set; }
    /// <summary>Décor des fonds sans photo (<see cref="StartTemplates.Decors"/>), choisi par l'IA selon le ton.</summary>
    public string Decor { get; set; } = "cercles";
    /// <summary>Mise en page de chaque type de page (numéro de variante ; absent : la première).</summary>
    public Dictionary<string, int> Layouts { get; set; } = new();

    /// <summary>Logo et photos de la médiathèque ; adresse du site (QR des pages, « horizon-sa.ch · à bientôt »). Jamais envoyés à l'IA.</summary>
    [JsonIgnore] public Guid? Logo { get; set; }
    /// <summary>Toutes les photos de l'écran, sans doublon (compteurs, vignettes de l'identité).</summary>
    [JsonIgnore] public List<Guid> Photos { get; set; } = new();
    /// <summary>La photo de chaque page (type de page → image) ; une page absente d'ici n'a pas de photo : elle reçoit le décor.</summary>
    [JsonIgnore] public Dictionary<string, Guid> PagePhotos { get; set; } = new();
    [JsonIgnore] public string Site { get; set; } = "";

    /// <summary>Pose la photo de chaque page ; <see cref="Photos"/> suit.</summary>
    public void SetPhotos(Dictionary<string, Guid> byPage)
    {
        PagePhotos = new(byPage);
        Photos = StartTemplates.PhotoKinds.Where(byPage.ContainsKey).Select(k => byPage[k]).Distinct().ToList();
    }

    public StartBrief Clone()
    {
        var c = System.Text.Json.JsonSerializer.Deserialize<StartBrief>(System.Text.Json.JsonSerializer.Serialize(this, StartTemplates.Json), StartTemplates.Json)!;
        c.Logo = Logo; c.Photos = new(Photos); c.PagePhotos = new(PagePhotos); c.Site = Site;
        return c;
    }
}

public class StartColors
{
    public string Deep { get; set; } = "#12403C";
    public string Accent { get; set; } = "#E2733D";
    public string Light { get; set; } = "#F6F1E7";
}

public class StartPages
{
    public StartText Welcome { get; set; } = new();
    public StartPoints Services { get; set; } = new();
    public StartPrices? Prices { get; set; }
    public StartOffer? Offer { get; set; }
    public StartInfo? Info { get; set; }
    public StartNumbers? Numbers { get; set; }
    public StartQuote? Quote { get; set; }
    public StartNews? News { get; set; }
    public StartText Thanks { get; set; } = new();
}

public class StartText
{
    public string Kicker { get; set; } = "";
    public string Title { get; set; } = "";
    public string Sub { get; set; } = "";
}

public class StartPoints
{
    public string Kicker { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Points { get; set; } = new();
}

public class StartInfo
{
    public string Kicker { get; set; } = "";
    public string Title { get; set; } = "";
    public List<StartCard> Cards { get; set; } = new();
}

public class StartCard
{
    public string Icon { get; set; } = "information";
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string Note { get; set; } = "";
}

public class StartNews
{
    public string Tag { get; set; } = "";
    public string Title { get; set; } = "";
    public string Sub { get; set; } = "";
    public string? Link { get; set; }
}

/// <summary>Liste avec prix : produits ou prestations, chacun avec son prix (entre crochets s'il n'est pas connu).</summary>
public class StartPrices
{
    public string Kicker { get; set; } = "";
    public string Title { get; set; } = "";
    public List<StartPrice> Items { get; set; } = new();
}

public class StartPrice
{
    public string Name { get; set; } = "";
    public string Price { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>Offre du moment : une pastille (« −20 % », « 2 pour 1 »), un titre, une précision.</summary>
public class StartOffer
{
    public string Tag { get; set; } = "";
    public string Badge { get; set; } = "";
    public string Title { get; set; } = "";
    public string Sub { get; set; } = "";
}

/// <summary>Chiffres clés : jusqu'à trois grands nombres et ce qu'ils disent.</summary>
public class StartNumbers
{
    public string Kicker { get; set; } = "";
    public string Title { get; set; } = "";
    public List<StartNumber> Items { get; set; } = new();
}

public class StartNumber
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}

/// <summary>Avis : une phrase réellement citée dans les informations, et son auteur.</summary>
public class StartQuote
{
    public string Text { get; set; } = "";
    public string Author { get; set; } = "";
}

/// <summary>Modèles des pages de l'assistant : documents de composition (1920 × 1080 ou 1080 × 1920) dessinés à partir du contenu.</summary>
public static class StartTemplates
{
    public static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Les pages, dans l'ordre de la liste (la météo, si elle existe, se place avant l'actualité).</summary>
    public static readonly string[] Kinds = { "welcome", "services", "prices", "offer", "info", "numbers", "quote", "news", "thanks" };
    /// <summary>Pages facultatives : l'IA garde celles qui conviennent à l'activité (<see cref="MaxOptional"/> au plus).</summary>
    public static readonly string[] Optional = { "prices", "offer", "info", "numbers", "quote", "news" };
    public const int MaxOptional = 4;
    /// <summary>Pages qui portent une photo.</summary>
    public static readonly string[] PhotoKinds = { "welcome", "services", "prices", "offer", "news", "thanks" };
    public static readonly string[] Decors = { "cercles", "bandes", "arches", "points", "cadre" };
    public static readonly string[] Icons = { "horloge", "position", "telephone", "courrier", "wifi", "parking", "accessibilite", "information", "calendrier" };
    public static readonly string[] Fonts = { "Montserrat", "Poppins", "Playfair Display", "Oswald", "IBM Plex Sans" };
    private static readonly Regex Hex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    /// <summary>Nombre de mises en page d'un type de page.</summary>
    public static int Variants(string kind) => kind is "welcome" or "services" ? 3 : 2;

    /// <summary>Tire d'autres mises en page et un autre décor : jamais ceux d'avant (« Autre mise en page »).</summary>
    public static void Shuffle(StartBrief b, Random rnd, bool decor = true)
    {
        foreach (var k in Kinds)
        {
            var n = Variants(k);
            b.Layouts[k] = b.Layouts.TryGetValue(k, out var cur) ? (Math.Abs(cur) % n + 1 + rnd.Next(n - 1)) % n : rnd.Next(n);
        }
        if (!decor) return;
        var i = Array.IndexOf(Decors, b.Decor);
        b.Decor = i < 0 ? Decors[rnd.Next(Decors.Length)] : Decors[(i + 1 + rnd.Next(Decors.Length - 1)) % Decors.Length];
    }

    /// <summary>La page existe pour ce contenu (accueil, services et merci toujours ; les autres sont facultatives).</summary>
    public static bool Has(StartPages p, string kind) => kind switch
    {
        "info" => p.Info is { Cards.Count: > 0 } && p.Info.Title.Trim().Length > 0,
        "news" => p.News != null && p.News.Title.Trim().Length > 0,
        "prices" => p.Prices is { Items.Count: > 0 } && p.Prices.Title.Trim().Length > 0,
        "offer" => p.Offer != null && p.Offer.Title.Trim().Length > 0,
        "numbers" => p.Numbers is { Items.Count: > 0 },
        "quote" => p.Quote != null && p.Quote.Text.Trim().Length > 0,
        _ => true,
    };

    /// <summary>Nom de la composition dans la médiathèque et dans le fil.</summary>
    public static string Name(StartPages p, string kind, bool en)
    {
        static string? K(string? s) => Cut(s ?? "", 40) is { Length: > 0 } k ? k : null;
        var n = kind switch
        {
            "welcome" => en ? "Welcome" : "Accueil",
            "services" => K(p.Services.Kicker) ?? (en ? "Our services" : "Nos services"),
            "prices" => K(p.Prices?.Kicker) ?? (en ? "Prices" : "Tarifs"),
            "offer" => K(p.Offer?.Tag) ?? (en ? "Offer" : "Offre du moment"),
            "info" => en ? "Practical information" : "Infos pratiques",
            "numbers" => en ? "Key figures" : "Chiffres clés",
            "quote" => en ? "Testimonial" : "Avis",
            "news" => K(p.News?.Tag) ?? (en ? "News" : "Actualité"),
            _ => en ? "Thank you" : "Merci",
        };
        return en && kind is "services" or "news" or "prices" or "offer" ? n + " (EN)" : n;
    }

    // ---------- Couleurs ----------

    /// <summary>Couleurs contrôlées : un fond foncé vraiment foncé, un fond clair vraiment clair, un accent qui se voit sur l'un ou l'autre.</summary>
    public static StartColors Fix(StartColors? c)
    {
        c ??= new StartColors();
        var deep = Norm(c.Deep, "#12403C");
        for (var i = 0; i < 10 && Lum(deep) > 0.07; i++) deep = Mix(deep, "#000000", 0.25);
        var light = Norm(c.Light, "#F6F1E7");
        for (var i = 0; i < 10 && Lum(light) < 0.82; i++) light = Mix(light, "#FFFFFF", 0.35);
        var accent = Norm(c.Accent, "#E2733D");
        if (Contrast(accent, deep) < 1.8 && Contrast(accent, light) < 1.8) accent = "#E2733D";
        return new StartColors { Deep = deep.ToUpperInvariant(), Accent = accent.ToUpperInvariant(), Light = light.ToUpperInvariant() };
    }

    public static string Norm(string? hex, string fallback) => hex != null && Hex.IsMatch(hex.Trim()) ? hex.Trim() : fallback;

    private static (double R, double G, double B) Rgb(string hex) =>
        (int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber), int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber), int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber));

    public static double Lum(string hex)
    {
        static double Ch(double v) { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        var (r, g, b) = Rgb(hex);
        return 0.2126 * Ch(r) + 0.7152 * Ch(g) + 0.0722 * Ch(b);
    }

    public static double Contrast(string a, string b)
    {
        double la = Lum(a), lb = Lum(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static string Mix(string a, string b, double t)
    {
        var (r1, g1, b1) = Rgb(a); var (r2, g2, b2) = Rgb(b);
        int M(double x, double y) => (int)Math.Round(x + (y - x) * t);
        return $"#{M(r1, r2):X2}{M(g1, g2):X2}{M(b1, b2):X2}";
    }

    /// <summary>Teintes dérivées : accent lisible sur le fond foncé, sur le fond clair, et couleur d'un texte posé sur l'accent.</summary>
    private sealed record Palette(string Deep, string Accent, string Light, string OnDark, string OnLight, string Ink)
    {
        public static Palette Of(StartColors c0)
        {
            var c = Fix(c0);
            var onDark = c.Accent;
            for (var i = 0; i < 8 && Contrast(onDark, c.Deep) < 3.2; i++) onDark = Mix(onDark, "#FFFFFF", 0.2);
            var onLight = c.Accent;
            for (var i = 0; i < 8 && Contrast(onLight, c.Light) < 3.2; i++) onLight = Mix(onLight, "#000000", 0.2);
            var ink = Contrast("#FFFFFF", c.Accent) >= Contrast(c.Deep, c.Accent) ? "#FFFFFF" : c.Deep;
            return new Palette(c.Deep, c.Accent, c.Light, onDark, onLight, ink);
        }
    }

    // ---------- Pages ----------

    /// <summary>La photo d'une page : la sienne (<see cref="StartBrief.PagePhotos"/>) ; sans attribution par page, les photos se répartissent dans l'ordre.</summary>
    public static Guid? PhotoOf(StartBrief b, string kind)
    {
        var i = Array.IndexOf(PhotoKinds, kind);
        if (i < 0) return null;
        if (b.PagePhotos.Count > 0) return b.PagePhotos.TryGetValue(kind, out var g) ? g : null;
        return b.Photos.Count == 0 ? null : b.Photos[i % b.Photos.Count];
    }

    /// <summary>Document de composition d'une page (déjà propre pour <see cref="Compositions.Clean"/>).</summary>
    public static string Doc(StartBrief b, string kind, bool portrait, bool en)
    {
        var p = en && b.En != null ? b.En : b.Pages;
        var pal = Palette.Of(b.Colors);
        var font = Fonts.Contains(b.Font) ? b.Font : "Montserrat";
        var pg = new Page(portrait ? 1080 : 1920, portrait ? 1920 : 1080, font, Decors.Contains(b.Decor) ? b.Decor : "cercles");
        var v = b.Layouts.TryGetValue(kind, out var lv) ? Math.Abs(lv) % Variants(kind) : 0;
        var photo = PhotoOf(b, kind);
        var bg = kind switch
        {
            "welcome" => Welcome(pg, b, p.Welcome, pal, v, photo),
            "services" => Services(pg, p.Services, pal, v, photo),
            "prices" => Prices(pg, b, p.Prices ?? new StartPrices(), pal, v, photo),
            "offer" => Offer(pg, b, p.Offer ?? new StartOffer(), pal, v, photo),
            "info" => Info(pg, b, p.Info ?? new StartInfo(), pal, v),
            "numbers" => Numbers(pg, b, p.Numbers ?? new StartNumbers(), pal, v),
            "quote" => Quote(pg, b, p.Quote ?? new StartQuote(), pal, v),
            "news" => News(pg, b, p.News ?? new StartNews(), pal, v, photo, en),
            _ => Thanks(pg, b, p.Thanks, pal, v, photo),
        };
        return pg.Json(bg);
    }

    private static void Brand(Page pg, StartBrief b, Palette pal, int x, int y)
    {
        if (b.Logo is { } logo)
        {
            pg.Rect(x, y, 300, 112, "#FFFFFF", radius: 20);
            pg.Image(x + 22, y + 14, 256, 84, logo, "contain");
        }
        else if (b.Name.Trim().Length > 0) pg.Text(x, y + 20, pg.W - x - 400, b.Name.Trim(), 40, 700, "#FFFFFF", maxLines: 1);
    }

    /// <summary>L'heure dans une pastille, en haut à droite : seulement si la ville est connue.</summary>
    private static void ClockPill(Page pg, StartBrief b)
    {
        if (b.City == null) return;
        var (cx, cw) = pg.Portrait ? (728, 272) : (1512, 312);
        var y = pg.Portrait ? 90 : 84;
        pg.Rect(cx, y, cw, 100, "#FFFFFF", 0.16, 50);
        pg.Clock(cx, y + 18, cw, 56, "#FFFFFF");
    }

    /// <summary>Décor d'un fond foncé sans photo : formes discrètes de la couleur d'accent.</summary>
    private static void Decor(Page pg, Palette pal)
    {
        var po = pg.Portrait;
        switch (pg.Decor)
        {
            case "bandes":
                for (var i = 0; i < 3; i++)
                    pg.Rect((po ? 520 : 1260) + i * (po ? 200 : 230), po ? -520 : -320, po ? 100 : 116, po ? 1500 : 1720, pal.Accent, 0.22 - i * 0.06, rot: 28);
                break;
            case "arches":
                if (po) { pg.Circle(330, -560, 1200, pal.Accent, 0.09); pg.Circle(530, -360, 800, pal.Accent, 0.11); pg.Circle(730, -160, 400, pal.Accent, 0.15); }
                else { pg.Circle(1160, 320, 1400, pal.Accent, 0.09); pg.Circle(1380, 540, 960, pal.Accent, 0.11); pg.Circle(1600, 760, 520, pal.Accent, 0.15); }
                break;
            case "points":
                for (var r = 0; r < 5; r++)
                    for (var c = 0; c < 7; c++)
                        pg.Circle((po ? 640 : 1470) + c * 56, (po ? 250 : 300) + r * 56, 14, pal.Accent, 0.45);
                if (po) pg.Circle(-260, 1500, 700, pal.Accent, 0.1); else pg.Circle(1500, 700, 720, pal.Accent, 0.1);
                break;
            case "cadre":
                pg.Rect(40, 40, pg.W - 80, 4, pal.Accent, 0.6); pg.Rect(40, pg.H - 44, pg.W - 80, 4, pal.Accent, 0.6);
                pg.Rect(40, 40, 4, pg.H - 80, pal.Accent, 0.6); pg.Rect(pg.W - 44, 40, 4, pg.H - 80, pal.Accent, 0.6);
                pg.Rect(pg.W - 44 - 150, pg.H - 44 - 150, 150, 150, pal.Accent, 0.16);
                break;
            default:
                if (po) { pg.Circle(420, -320, 1000, pal.Accent, 0.16); pg.Circle(560, -180, 720, pal.Deep); }
                else { pg.Circle(1360, -420, 1100, pal.Accent, 0.16); pg.Circle(1540, -240, 740, pal.Deep); }
                break;
        }
    }

    /// <summary>Un panneau d'image : la photo, ou sans photo un aplat et un rond de la couleur d'accent.</summary>
    private static void Panel(Page pg, int x, int y, int w, int h, Guid? photo, Palette pal, bool onDark = false)
    {
        if (photo is { } ph) { pg.Image(x, y, w, h, ph); return; }
        pg.Rect(x, y, w, h, onDark ? Mix(pal.Deep, pal.Accent, 0.22) : pal.Deep);
        var d = Math.Min(w, h) * 2 / 3;
        pg.Circle(x + (w - d) / 2, y + (h - d) / 2, d, pal.Accent, 0.3);
    }

    private static string Welcome(Page pg, StartBrief b, StartText t, Palette pal, int v, Guid? photo)
    {
        int mx = pg.Portrait ? 80 : 96, top = pg.Portrait ? 90 : 84;
        if (v == 1 && photo is { } side)
        {
            // photo d'un côté, texte sur l'aplat
            if (pg.Portrait)
            {
                pg.Image(0, 0, pg.W, 900, side);
                pg.Rect(0, 0, pg.W, 320, pal.Deep, 0.5);
                pg.Rect(0, 900, pg.W, 16, pal.Accent);
            }
            else
            {
                pg.Image(960, 0, 960, pg.H, side);
                pg.Rect(960, 0, 960, 280, pal.Deep, 0.4);
                pg.Rect(960, 0, 14, pg.H, pal.Accent);
            }
            Brand(pg, b, pal, mx, top);
            ClockPill(pg, b);
            var sb = new Block(pg, mx, pg.Portrait ? 920 : 780);
            sb.Add(Upper(t.Kicker), pg.Portrait ? 36 : 32, 700, pal.OnDark, ls: 0.16, maxLines: 1, gap: 18);
            sb.Add(t.Title, pg.Portrait ? 100 : 92, 700, "#FFFFFF", lh: 1.04, maxLines: 4, gap: 26);
            sb.Add(t.Sub, pg.Portrait ? 46 : 40, 400, "#FFFFFF", opacity: 0.92, maxLines: 3);
            if (pg.Portrait) sb.Top(1010); else sb.Bottom(pg.H - 110);
            return pal.Deep;
        }
        if (photo is { } ph)
        {
            pg.Image(0, 0, pg.W, pg.H, ph);
            if (v == 2) pg.Rect(0, 0, pg.W, pg.H, pal.Deep, 0.6);
            else
            {
                pg.Rect(0, 0, pg.W, pg.H, pal.Deep, 0.42);
                if (pg.Portrait) pg.Rect(0, 900, pg.W, pg.H - 900, pal.Deep, 0.45); else pg.Rect(0, 0, 1250, pg.H, pal.Deep, 0.4);
            }
        }
        else Decor(pg, pal);
        Brand(pg, b, pal, mx, top);
        ClockPill(pg, b);
        if (v == 2)
        {
            // tout au centre, un trait d'accent entre le titre et le texte
            var cx = pg.Portrait ? 80 : 240;
            var cb = new Block(pg, cx, pg.W - 2 * cx, "center");
            cb.Add(Upper(t.Kicker), pg.Portrait ? 36 : 34, 700, pal.OnDark, ls: 0.16, maxLines: 1, gap: 22);
            cb.Add(t.Title, pg.Portrait ? 104 : 116, 700, "#FFFFFF", lh: 1.04, maxLines: pg.Portrait ? 4 : 3, gap: 34);
            cb.Rule(160, 8, pal.Accent, gap: 34);
            cb.Add(t.Sub, pg.Portrait ? 46 : 44, 400, "#FFFFFF", opacity: 0.92, maxLines: pg.Portrait ? 3 : 2);
            cb.Middle(pg.Portrait ? 260 : 200, pg.H - (pg.Portrait ? 160 : 60));
            return pal.Deep;
        }
        var block = new Block(pg, mx, pg.Portrait ? 920 : 1180);
        block.Add(Upper(t.Kicker), pg.Portrait ? 36 : 34, 700, pal.OnDark, ls: 0.16, maxLines: 1, gap: 18);
        block.Add(t.Title, pg.Portrait ? 108 : 116, 700, "#FFFFFF", lh: 1.04, maxLines: pg.Portrait ? 4 : 3, gap: 26);
        block.Add(t.Sub, pg.Portrait ? 46 : 44, 400, "#FFFFFF", opacity: 0.92, maxLines: pg.Portrait ? 3 : 2);
        block.Bottom(pg.H - (pg.Portrait ? 200 : 110));
        return pal.Deep;
    }

    private static string Services(Page pg, StartPoints t, Palette pal, int v, Guid? photo)
    {
        var pts = t.Points.Where(s => s.Trim().Length > 0).Take(4).Select(s => s.Trim()).ToList();
        if (v == 2)
        {
            // sans photo : un bandeau d'accent, le titre, puis une carte par point
            pg.Rect(0, 0, pg.W, 22, pal.Accent);
            var hx = pg.Portrait ? 80 : 120;
            var head = new Block(pg, hx, pg.W - 2 * hx);
            head.Add(Upper(t.Kicker), pg.Portrait ? 32 : 30, 700, pal.OnLight, ls: 0.16, maxLines: 1, gap: 16);
            head.Add(t.Title, 84, 700, pal.Deep, lh: 1.06, maxLines: pg.Portrait ? 3 : 2);
            head.Top(pg.Portrait ? 200 : 130);
            if (pts.Count == 0) return pal.Light;
            if (pg.Portrait)
            {
                var y = Math.Max(640, head.End + 80);
                for (var i = 0; i < pts.Count; i++, y += 266)
                {
                    pg.Rect(hx, y, 920, 230, "#FFFFFF", radius: 28);
                    pg.Circle(hx + 40, y + 65, 100, pal.Accent);
                    pg.Text(hx + 40, y + 65 + 22, 100, (i + 1).ToString(CultureInfo.InvariantCulture), 48, 700, pal.Ink, "center", maxLines: 1);
                    var (s, lines) = pg.Fit(pts[i], 700, 44, 600, 0, 3);
                    var th = (int)Math.Ceiling(lines * s * 1.15) + 4;
                    pg.Text(hx + 180, y + Math.Max(10, (230 - th) / 2), 700, pts[i], 44, 600, pal.Deep, maxLines: 3);
                }
                return pal.Light;
            }
            var n = pts.Count;
            var cw = (1680 - (n - 1) * 40) / n;
            var cy = Math.Max(470, head.End + 70);
            var ch = pg.H - 110 - cy;
            for (var i = 0; i < n; i++)
            {
                var cx = 120 + i * (cw + 40);
                pg.Rect(cx, cy, cw, ch, "#FFFFFF", radius: 28);
                pg.Circle(cx + 40, cy + 40, 96, pal.Accent);
                pg.Text(cx + 40, cy + 40 + 20, 96, (i + 1).ToString(CultureInfo.InvariantCulture), 48, 700, pal.Ink, "center", maxLines: 1);
                pg.Text(cx + 40, cy + 180, cw - 80, pts[i], 44, 600, pal.Deep, maxLines: 5);
            }
            return pal.Light;
        }
        // 0 : texte à gauche sur fond clair, photo à droite ; 1 : photo à gauche, texte à droite sur fond foncé
        var dark = v == 1;
        int x, w;
        if (pg.Portrait)
        {
            var py = dark ? 1160 : 0;
            Panel(pg, 0, py, pg.W, 760, photo, pal, dark);
            pg.Rect(0, dark ? 1136 : 760, pg.W, 24, pal.Accent);
            (x, w) = (90, 900);
        }
        else
        {
            Panel(pg, dark ? 0 : 1072, 0, 848, pg.H, photo, pal, dark);
            pg.Rect(dark ? 848 : 1050, 0, 22, pg.H, pal.Accent);
            (x, w) = dark ? (990, 810) : (120, 860);
        }
        var block = new Block(pg, x, w);
        block.Add(Upper(t.Kicker), pg.Portrait ? 32 : 30, 700, dark ? pal.OnDark : pal.OnLight, ls: 0.16, maxLines: 1, gap: 16);
        block.Add(t.Title, 84, 700, dark ? "#FFFFFF" : pal.Deep, lh: 1.06, maxLines: 3, gap: pg.Portrait ? 56 : 48);
        var k = 0;
        foreach (var pt in pts)
            block.Point(++k, pt, pg.Portrait ? 44 : 40, pal, gap: pg.Portrait ? 36 : 30, color: dark ? "#FFFFFF" : pal.Deep);
        if (pg.Portrait) block.Top(dark ? 170 : 880); else block.Middle(0, pg.H);
        return dark ? pal.Deep : pal.Light;
    }

    /// <summary>Lignes « nom … prix » (avec une précision sous le nom si la ligne est assez haute), séparées par un filet.</summary>
    private static void Rows(Page pg, List<StartPrice> items, int x, int y, int w, int rh, string ink, string priceInk, string line, double lineOpacity)
    {
        var size = rh >= 140 ? 44 : rh >= 110 ? 38 : 32;
        var pw = w * 3 / 10;
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var ry = y + i * rh;
            var note = it.Note.Trim().Length > 0 && rh >= 120;
            var th = (int)Math.Ceiling(size * 1.15) + 4;
            var ns = size * 5 / 8;
            var nh = note ? (int)Math.Ceiling(ns * 1.15) + 8 : 0;
            var ty = ry + (rh - th - nh) / 2;
            pg.Text(x, ty, w - pw - 30, it.Name.Trim(), size, 600, ink, maxLines: 1);
            if (it.Price.Trim().Length > 0) pg.Text(x + w - pw, ty, pw, it.Price.Trim(), size, 700, priceInk, "right", maxLines: 1);
            if (note) pg.Text(x, ty + th + 4, w - pw - 30, it.Note.Trim(), ns, 400, ink, opacity: 0.75, maxLines: 1);
            if (i < items.Count - 1) pg.Rect(x, ry + rh - 1, w, 2, line, lineOpacity);
        }
    }

    private static string Prices(Page pg, StartBrief b, StartPrices t, Palette pal, int v, Guid? photo)
    {
        var items = t.Items.Where(i => i.Name.Trim().Length > 0).Take(6).ToList();
        var n = Math.Max(1, items.Count);
        if (v == 1)
        {
            // fond clair : la photo en bande, le titre puis la liste
            int hx, hw, start;
            if (pg.Portrait)
            {
                Panel(pg, 0, 0, pg.W, 520, photo, pal);
                pg.Rect(0, 520, pg.W, 20, pal.Accent);
                (hx, hw, start) = (80, 920, 620);
            }
            else
            {
                Panel(pg, 0, 0, 640, pg.H, photo, pal);
                pg.Rect(640, 0, 20, pg.H, pal.Accent);
                (hx, hw, start) = (770, 1030, 110);
            }
            var lh = new Block(pg, hx, hw);
            lh.Add(Upper(t.Kicker), 30, 700, pal.OnLight, ls: 0.16, maxLines: 1, gap: 14);
            lh.Add(t.Title, pg.Portrait ? 84 : 76, 700, pal.Deep, lh: 1.06, maxLines: 2);
            lh.Top(start);
            var ly = lh.End + (pg.Portrait ? 60 : 50);
            var lrh = Math.Min(pg.Portrait ? 170 : 130, (pg.H - (pg.Portrait ? 120 : 70) - ly) / n);
            Rows(pg, items, hx, ly, hw, lrh, pal.Deep, pal.OnLight, pal.Deep, 0.15);
            return pal.Light;
        }
        Decor(pg, pal);
        if (pg.Portrait)
        {
            Brand(pg, b, pal, 80, 90);
            var ph = new Block(pg, 80, 920);
            ph.Add(Upper(t.Kicker), 32, 700, pal.OnDark, ls: 0.16, maxLines: 1, gap: 16);
            ph.Add(t.Title, 88, 700, "#FFFFFF", lh: 1.05, maxLines: 2);
            ph.Top(300);
            var y = Math.Max(640, ph.End + 70);
            var prh = Math.Min(170, (pg.H - 120 - y) / n);
            pg.Rect(40, y - 30, 1000, prh * n + 60, "#FFFFFF", 0.06, 32);
            Rows(pg, items, 80, y, 920, prh, "#FFFFFF", pal.OnDark, "#FFFFFF", 0.15);
            return pal.Deep;
        }
        Brand(pg, b, pal, 120, 90);
        var head = new Block(pg, 120, 640);
        head.Add(Upper(t.Kicker), 30, 700, pal.OnDark, ls: 0.16, maxLines: 1, gap: 16);
        head.Add(t.Title, 84, 700, "#FFFFFF", lh: 1.05, maxLines: 3);
        head.Middle(240, pg.H - 60);
        pg.Rect(860, 90, 940, 900, "#FFFFFF", 0.06, 32);
        var rh = Math.Min(150, 840 / n);
        Rows(pg, items, 920, 90 + (900 - rh * n) / 2, 820, rh, "#FFFFFF", pal.OnDark, "#FFFFFF", 0.15);
        return pal.Deep;
    }

    /// <summary>Un texte centré en hauteur dans une zone (pastille, grand chiffre).</summary>
    private static void Centered(Page pg, int x, int y, int w, int h, string text, int size, string color, int maxLines = 2)
    {
        var (s, lines) = pg.Fit(text, w, size, 700, 0, maxLines);
        var th = (int)Math.Ceiling(lines * s * 1.05) + 4;
        pg.Text(x, y + Math.Max(0, (h - th) / 2), w, text, size, 700, color, "center", 1.05, 0, 1, maxLines);
    }

    private static string Offer(Page pg, StartBrief b, StartOffer t, Palette pal, int v, Guid? photo)
    {
        var badge = t.Badge.Trim();
        int mx = pg.Portrait ? 80 : 96;
        if (v == 1 && badge.Length > 0)
        {
            // un grand aplat d'accent pour la pastille, la photo et le texte à côté
            if (pg.Portrait)
            {
                if (photo is { } pp) { pg.Image(0, 640, pg.W, pg.H - 640, pp); pg.Rect(0, 640, pg.W, pg.H - 640, pal.Deep, 0.5); }
                pg.Rect(0, 0, pg.W, 640, pal.Accent);
                Centered(pg, 80, 60, 920, 520, badge, 220, pal.Ink);
            }
            else
            {
                if (photo is { } lp) { pg.Image(820, 0, 1100, pg.H, lp); pg.Rect(820, 0, 1100, pg.H, pal.Deep, 0.5); }
                pg.Rect(0, 0, 820, pg.H, pal.Accent);
                Centered(pg, 60, 140, 700, 800, badge, 210, pal.Ink);
            }
            var sb = new Block(pg, pg.Portrait ? 80 : 900, 920);
            sb.Tag(Upper(t.Tag), pg.Portrait ? 30 : 28, pal, gap: 26);
            sb.Add(t.Title, pg.Portrait ? 92 : 84, 700, "#FFFFFF", lh: 1.05, maxLines: 4, gap: 24);
            sb.Add(t.Sub, pg.Portrait ? 44 : 40, 400, "#FFFFFF", opacity: 0.92, maxLines: 3);
            sb.Bottom(pg.H - (pg.Portrait ? 200 : 110));
            return pal.Deep;
        }
        if (photo is { } ph) { pg.Image(0, 0, pg.W, pg.H, ph); pg.Rect(0, 0, pg.W, pg.H, pal.Deep, 0.55); }
        else Decor(pg, pal);
        Brand(pg, b, pal, mx, pg.Portrait ? 90 : 84);
        if (badge.Length > 0)
        {
            var (bx, by, d) = pg.Portrait ? (580, 300, 420) : (1380, 320, 440);
            pg.Circle(bx, by, d, pal.Accent);
            Centered(pg, bx + 40, by, d - 80, d, badge, d / 4, pal.Ink);
        }
        var block = new Block(pg, mx, pg.Portrait ? 920 : 1180);
        block.Tag(Upper(t.Tag), pg.Portrait ? 30 : 28, pal, gap: 26);
        block.Add(t.Title, pg.Portrait ? 96 : 104, 700, "#FFFFFF", lh: 1.05, maxLines: pg.Portrait ? 4 : 3, gap: 24);
        block.Add(t.Sub, pg.Portrait ? 44 : 42, 400, "#FFFFFF", opacity: 0.92, maxLines: 2);
        block.Bottom(pg.H - (pg.Portrait ? 200 : 110));
        return pal.Deep;
    }

    private static string Info(Page pg, StartBrief b, StartInfo t, Palette pal, int v)
    {
        Decor(pg, pal);
        Brand(pg, b, pal, pg.Portrait ? 80 : 120, 90);
        var cards = t.Cards.Where(c => c.Value.Trim().Length > 0 || c.Label.Trim().Length > 0).Take(3).ToList();
        var x = pg.Portrait ? 80 : 120;
        if (v == 1 && !pg.Portrait)
        {
            // le titre à gauche, les informations en lignes à droite
            var side = new Block(pg, 120, 640);
            side.Add(Upper(t.Kicker), 32, 700, pal.OnDark, ls: 0.16, maxLines: 1, gap: 16);
            side.Add(t.Title, 88, 700, "#FFFFFF", lh: 1.05, maxLines: 3);
            side.Middle(240, pg.H - 60);
            const int rh = 250, gap = 36;
            var ry = (pg.H - (cards.Count * rh + (cards.Count - 1) * gap)) / 2;
            foreach (var c in cards)
            {
                pg.Rect(860, ry, 940, rh, "#FFFFFF", 0.08, 28);
                pg.Icon(908, ry + (rh - 84) / 2, 84, Icon(c.Icon), pal.OnDark);
                var inner = new Block(pg, 1040, 710);
                inner.Add(Upper(c.Label), 26, 600, "#FFFFFF", ls: 0.1, opacity: 0.75, maxLines: 1, gap: 12);
                inner.Add(c.Value, 44, 600, "#FFFFFF", maxLines: 2, gap: 8);
                inner.Add(c.Note, 28, 400, "#FFFFFF", opacity: 0.8, maxLines: 1);
                inner.Middle(ry, ry + rh);
                ry += rh + gap;
            }
            return pal.Deep;
        }
        var head = new Block(pg, x, pg.Portrait ? 920 : 1500);
        head.Add(Upper(t.Kicker), 32, 700, pal.OnDark, ls: 0.16, maxLines: 1, gap: 16);
        head.Add(t.Title, pg.Portrait ? 100 : 100, 700, "#FFFFFF", lh: 1.04, maxLines: 2);
        head.Top(pg.Portrait ? 420 : 300);
        if (cards.Count == 0) return pal.Deep;
        if (pg.Portrait)
        {
            var y = Math.Max(820, head.End + 70);
            foreach (var c in cards)
            {
                var inner = new Block(pg, x + 160, 920 - 200);
                inner.Add(Upper(c.Label), 26, 600, "#FFFFFF", ls: 0.1, opacity: 0.75, maxLines: 1, gap: 10);
                inner.Add(c.Value, 44, 600, "#FFFFFF", maxLines: 2, gap: 8);
                inner.Add(c.Note, 30, 400, "#FFFFFF", opacity: 0.8, maxLines: 2);
                var h = Math.Max(240, inner.Height + 88);
                pg.Rect(x, y, 920, h, "#FFFFFF", 0.08, 30);
                pg.Icon(x + 44, y + 48, 76, Icon(c.Icon), pal.OnDark);
                inner.Top(y + 44);
                y += h + 36;
            }
            return pal.Deep;
        }
        var n = cards.Count;
        var cw = n == 1 ? 900 : (1920 - 240 - (n - 1) * 40) / n;
        var blocks = cards.Select(c =>
        {
            var inner = new Block(pg, 0, cw - 80);
            inner.Add(Upper(c.Label), 26, 600, "#FFFFFF", ls: 0.1, opacity: 0.75, maxLines: 1, gap: 12);
            inner.Add(c.Value, 40, 600, "#FFFFFF", maxLines: 2, gap: 8);
            inner.Add(c.Note, 28, 400, "#FFFFFF", opacity: 0.8, maxLines: 2);
            return inner;
        }).ToList();
        var ch = Math.Max(300, blocks.Max(bk => bk.Height) + 132 + 40);
        var cy = Math.Max(pg.H - 110 - ch, head.End + 60);
        for (var i = 0; i < n; i++)
        {
            var cx = 120 + i * (cw + 40);
            pg.Rect(cx, cy, cw, ch, "#FFFFFF", 0.08, 28);
            pg.Icon(cx + 40, cy + 40, 64, Icon(cards[i].Icon), pal.OnDark);
            blocks[i].Move(cx + 40);
            blocks[i].Top(cy + 132);
        }
        return pal.Deep;
    }

    private static string Numbers(Page pg, StartBrief b, StartNumbers t, Palette pal, int v)
    {
        var items = t.Items.Where(i => i.Value.Trim().Length > 0).Take(3).ToList();
        var n = Math.Max(1, items.Count);
        var light = v == 1;
        var x = pg.Portrait ? 80 : 120;
        if (light) pg.Rect(0, 0, pg.W, 22, pal.Accent);
        else { Decor(pg, pal); Brand(pg, b, pal, x, 90); }
        var head = new Block(pg, x, pg.W - 2 * x, light ? "center" : "left");
        head.Add(Upper(t.Kicker), 32, 700, light ? pal.OnLight : pal.OnDark, ls: 0.16, maxLines: 1, gap: 16);
        head.Add(t.Title, pg.Portrait ? 88 : 92, 700, light ? pal.Deep : "#FFFFFF", lh: 1.05, maxLines: 2);
        head.Top(light ? (pg.Portrait ? 240 : 150) : (pg.Portrait ? 420 : 300));
        string ink = light ? pal.Deep : "#FFFFFF", big = light ? pal.OnLight : pal.OnDark;
        if (pg.Portrait)
        {
            var y = Math.Max(light ? 700 : 820, head.End + 80);
            foreach (var it in items)
            {
                if (light) pg.Rect(x, y, 920, 300, "#FFFFFF", radius: 28); else pg.Rect(x, y, 920, 6, pal.Accent);
                pg.Text(x + 40, y + 40, 840, it.Value.Trim(), 130, 700, big, light ? "center" : "left", 1.05, maxLines: 1);
                pg.Text(x + 40, y + 190, 840, it.Label.Trim(), 36, 400, ink, light ? "center" : "left", opacity: 0.85, maxLines: 2);
                y += 336;
            }
            return light ? pal.Light : pal.Deep;
        }
        var cw = (1680 - (n - 1) * 40) / n;
        var cy = Math.Max(light ? 470 : 620, head.End + 70);
        for (var i = 0; i < items.Count; i++)
        {
            var cx = 120 + i * (cw + 40);
            if (light) pg.Rect(cx, cy, cw, pg.H - 110 - cy, "#FFFFFF", radius: 28); else pg.Rect(cx, cy, cw, 6, pal.Accent);
            var pad = light ? 40 : 0;
            pg.Text(cx + pad, cy + 50, cw - 2 * pad, items[i].Value.Trim(), 140, 700, big, light ? "center" : "left", 1.05, maxLines: 1);
            pg.Text(cx + pad, cy + 225, cw - 2 * pad, items[i].Label.Trim(), 36, 400, ink, light ? "center" : "left", opacity: 0.85, maxLines: 2);
        }
        return light ? pal.Light : pal.Deep;
    }

    private static string Quote(Page pg, StartBrief b, StartQuote t, Palette pal, int v)
    {
        var light = v == 1;
        var x = pg.Portrait ? 80 : 200;
        var author = t.Author.Trim().Length > 0 ? "— " + t.Author.Trim() : "";
        if (light)
        {
            if (pg.Portrait) pg.Rect(0, 0, pg.W, 36, pal.Accent); else pg.Rect(0, 0, 36, pg.H, pal.Accent);
            pg.Text((pg.W - 300) / 2, pg.Portrait ? 260 : 90, 300, "“", 300, 700, pal.OnLight, "center", maxLines: 1);
        }
        else
        {
            Decor(pg, pal);
            Brand(pg, b, pal, pg.Portrait ? 80 : 120, 90);
            pg.Text(x - 20, pg.Portrait ? 300 : 170, 300, "“", 300, 700, pal.OnDark, maxLines: 1);
        }
        var block = new Block(pg, x, pg.W - 2 * x, light ? "center" : "left");
        block.Add(t.Text, pg.Portrait ? 64 : 68, 600, light ? pal.Deep : "#FFFFFF", lh: 1.25, maxLines: pg.Portrait ? 8 : 5, gap: 40);
        block.Add(author, 40, 500, light ? pal.OnLight : pal.OnDark, maxLines: 1);
        block.Middle(pg.Portrait ? 600 : 410, pg.H - (pg.Portrait ? 200 : 70));
        return light ? pal.Light : pal.Deep;
    }

    private static string News(Page pg, StartBrief b, StartNews t, Palette pal, int v, Guid? photo, bool en)
    {
        var link = SafeLink(t.Link) ?? SafeLink(b.Site.Length > 0 ? "https://" + b.Site : null);
        var label = en ? "Find out more" : "En savoir plus";
        if (v == 1 && photo is { } side && !pg.Portrait)
        {
            // la photo à gauche, l'actualité à droite
            pg.Image(0, 0, 800, pg.H, side);
            pg.Rect(800, 0, 14, pg.H, pal.Accent);
            var sb = new Block(pg, 900, 920);
            sb.Tag(Upper(t.Tag), 28, pal, gap: 26);
            sb.Add(t.Title, 84, 700, "#FFFFFF", lh: 1.05, maxLines: 3, gap: 24);
            sb.Add(t.Sub, 40, 400, "#FFFFFF", opacity: 0.92, maxLines: 2);
            if (link == null) { sb.Middle(0, pg.H); return pal.Deep; }
            sb.Top(130);
            pg.Rect(900, 730, 560, 250, "#FFFFFF", radius: 26);
            pg.Qr(925, 755, 200, link, pal.Deep);
            pg.Text(1150, 830, 290, label, 32, 600, pal.Deep, maxLines: 2);
            return pal.Deep;
        }
        if (photo is { } ph)
        {
            pg.Image(0, 0, pg.W, pg.H, ph);
            pg.Rect(0, 0, pg.W, pg.H, pal.Deep, 0.35);
            if (pg.Portrait) pg.Rect(0, 760, pg.W, pg.H - 760, pal.Deep, 0.5); else pg.Rect(0, 460, pg.W, pg.H - 460, pal.Deep, 0.5);
        }
        else Decor(pg, pal);
        var x = pg.Portrait ? 80 : 96;
        var block = new Block(pg, x, pg.Portrait ? 920 : 1180);
        block.Tag(Upper(t.Tag), pg.Portrait ? 30 : 28, pal, gap: 26);
        block.Add(t.Title, pg.Portrait ? 96 : 92, 700, "#FFFFFF", lh: 1.05, maxLines: pg.Portrait ? 4 : 3, gap: 24);
        block.Add(t.Sub, pg.Portrait ? 44 : 40, 400, "#FFFFFF", opacity: 0.92, maxLines: 2);
        if (link == null) { block.Bottom(pg.H - (pg.Portrait ? 200 : 110)); return pal.Deep; }
        if (pg.Portrait)
        {
            pg.Rect(80, 1500, 920, 300, "#FFFFFF", radius: 28);
            pg.Qr(120, 1530, 240, link, pal.Deep);
            pg.Text(400, 1610, 560, label, 40, 600, pal.Deep, maxLines: 2);
            block.Bottom(1420);
        }
        else
        {
            pg.Rect(1500, 600, 320, 370, "#FFFFFF", radius: 26);
            pg.Qr(1540, 630, 240, link, pal.Deep);
            pg.Text(1510, 895, 300, label, 28, 600, pal.Deep, align: "center", maxLines: 2);
            block.Bottom(pg.H - 110);
        }
        return pal.Deep;
    }

    private static string Thanks(Page pg, StartBrief b, StartText t, Palette pal, int v, Guid? photo)
    {
        if (photo is { } ph) { pg.Image(0, 0, pg.W, pg.H, ph); pg.Rect(0, 0, pg.W, pg.H, pal.Deep, 0.55); }
        else Decor(pg, pal);
        if (v == 1)
        {
            // en bas à gauche, sous un trait d'accent
            var lx = pg.Portrait ? 80 : 120;
            Brand(pg, b, pal, lx, 90);
            var lb = new Block(pg, lx, pg.Portrait ? 920 : 1400);
            lb.Rule(180, 10, pal.Accent, gap: 40);
            lb.Add(t.Title, pg.Portrait ? 110 : 124, 700, "#FFFFFF", lh: 1.04, maxLines: pg.Portrait ? 4 : 2, gap: 26);
            lb.Add(t.Sub, pg.Portrait ? 46 : 44, 500, "#FFFFFF", opacity: 0.9, ls: 0.02, maxLines: 2);
            lb.Bottom(pg.H - (pg.Portrait ? 220 : 130));
            return pal.Deep;
        }
        var x = pg.Portrait ? 80 : 160;
        var w = pg.W - 2 * x;
        var block = new Block(pg, x, w, "center");
        if (b.Logo is { } logo) block.Logo(logo, gap: 48);
        block.Add(t.Title, pg.Portrait ? 104 : 110, 700, "#FFFFFF", lh: 1.04, maxLines: pg.Portrait ? 4 : 2, gap: 26);
        block.Add(t.Sub, pg.Portrait ? 44 : 40, 500, "#FFFFFF", opacity: 0.9, ls: 0.02, maxLines: 2);
        block.Middle(0, pg.H);
        return pal.Deep;
    }

    // ---------- Outils ----------

    private static string Icon(string? i) => Icons.Contains(i ?? "") ? i! : "information";
    private static string Upper(string s) => s.Trim().ToUpper(new CultureInfo("fr-CH"));

    public static string Cut(string? s, int max)
    {
        s = Regex.Replace((s ?? "").Replace("\r", ""), @"[ \t]+", " ").Trim();
        if (s.Length <= max) return s;
        var cut = s[..max];
        var sp = cut.LastIndexOf(' ');
        return (sp > max / 2 ? cut[..sp] : cut).TrimEnd(' ', ',', ';', ':', '-', '·') + "…";
    }

    public static string? SafeLink(string? u) =>
        u != null && Uri.TryCreate(u.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.AbsoluteUri.Length <= 300 ? uri.AbsoluteUri : null;

    /// <summary>Largeur moyenne d'un caractère, en fraction de la taille de la police.</summary>
    private static double Glyph(string font, int weight, bool upper)
    {
        var k = font switch { "Oswald" => 0.46, "Playfair Display" => 0.52, "IBM Plex Sans" => 0.55, "Poppins" => 0.6, _ => 0.62 };
        if (weight >= 700) k *= 1.05;
        if (upper) k *= 1.12;
        return k;
    }

    /// <summary>Nombre de lignes d'un texte coupé aux espaces dans la largeur donnée.</summary>
    public static int Lines(string text, int width, int size, string font = "Montserrat", int weight = 400, double ls = 0)
    {
        var upper = text.Length > 0 && text == text.ToUpperInvariant() && text.Any(char.IsLetter);
        var cw = size * (Glyph(font, weight, upper) + ls);
        var per = Math.Max(1, (int)(width / cw));
        var total = 0;
        foreach (var para in text.Split('\n'))
        {
            var lines = 1; var len = 0;
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var wl = word.Length;
                if (len == 0) { len = wl; while (len > per) { lines++; len -= per; } }
                else if (len + 1 + wl <= per) len += 1 + wl;
                else { lines++; len = wl; while (len > per) { lines++; len -= per; } }
            }
            total += lines;
        }
        return Math.Max(1, total);
    }

    /// <summary>Une page en cours de dessin.</summary>
    private sealed class Page(int w, int h, string font, string decor)
    {
        public readonly int W = w, H = h;
        public readonly string Font = font, Decor = decor;
        public bool Portrait => H > W;
        private readonly JsonArray els = new();

        private static string NewId() => Guid.NewGuid().ToString("N")[..8];

        public void Rect(int x, int y, int w, int h, string fill, double opacity = 1, int radius = 0, int rot = 0) =>
            els.Add(new JsonObject { ["id"] = NewId(), ["type"] = "shape", ["kind"] = "rect", ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h, ["fill"] = fill, ["stroke"] = "", ["sw"] = 6, ["radius"] = radius, ["rot"] = rot, ["opacity"] = opacity });

        public void Circle(int x, int y, int d, string fill, double opacity = 1) =>
            els.Add(new JsonObject { ["id"] = NewId(), ["type"] = "shape", ["kind"] = "circle", ["x"] = x, ["y"] = y, ["w"] = d, ["h"] = d, ["fill"] = fill, ["stroke"] = "", ["sw"] = 6, ["radius"] = 0, ["rot"] = 0, ["opacity"] = opacity });

        public void Image(int x, int y, int w, int h, Guid media, string fit = "cover") =>
            els.Add(new JsonObject { ["id"] = NewId(), ["type"] = "image", ["media"] = media.ToString(), ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h, ["fit"] = fit, ["radius"] = 0, ["opacity"] = 1 });

        public void Icon(int x, int y, int s, string icon, string color) =>
            els.Add(new JsonObject { ["id"] = NewId(), ["type"] = "icon", ["icon"] = icon, ["x"] = x, ["y"] = y, ["w"] = s, ["h"] = s, ["color"] = color, ["opacity"] = 1 });

        public void Qr(int x, int y, int s, string url, string fg) =>
            els.Add(new JsonObject { ["id"] = NewId(), ["type"] = "qr", ["url"] = url, ["x"] = x, ["y"] = y, ["w"] = s, ["h"] = s, ["fg"] = fg, ["bg"] = "#FFFFFF", ["opacity"] = 1 });

        public void Clock(int x, int y, int w, int size, string color) =>
            els.Add(new JsonObject { ["id"] = NewId(), ["type"] = "widget", ["kind"] = "clock", ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = (int)(size * 1.2), ["font"] = Font, ["size"] = size, ["weight"] = 500, ["color"] = color, ["align"] = "center", ["sec"] = false, ["opacity"] = 1 });

        /// <summary>Un texte ; la taille baisse (jusqu'aux deux tiers) tant qu'il dépasse <paramref name="maxLines"/>. Rend la hauteur occupée.</summary>
        public int Text(int x, int y, int w, string text, int size, int weight, string color, string align = "left", double lh = 1.15, double ls = 0, double opacity = 1, int maxLines = 3)
        {
            var (s, lines) = Fit(text, w, size, weight, ls, maxLines);
            var h = (int)Math.Ceiling(lines * s * lh) + 4;
            els.Add(new JsonObject
            {
                ["id"] = NewId(), ["type"] = "text", ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h, ["text"] = text, ["font"] = Font, ["size"] = s, ["weight"] = weight,
                ["color"] = color, ["align"] = align, ["ls"] = ls, ["lh"] = lh, ["italic"] = false, ["opacity"] = opacity,
            });
            return h;
        }

        public (int Size, int Lines) Fit(string text, int w, int size, int weight, double ls, int maxLines)
        {
            var min = Math.Max(16, size * 2 / 3);
            var s = size;
            var lines = Lines(text, w, s, Font, weight, ls);
            while (lines > maxLines && s > min) { s = Math.Max(min, s - 4); lines = Lines(text, w, s, Font, weight, ls); }
            return (s, lines);
        }

        public string Json(string bg) => new JsonObject { ["v"] = 1, ["w"] = W, ["h"] = H, ["bg"] = bg, ["els"] = els }.ToJsonString();
    }

    /// <summary>Un bloc d'éléments empilés (sur-titre, titre, texte, points…), placé ensuite en haut, au milieu ou en bas d'une zone.</summary>
    private sealed class Block(Page pg, int x, int w, string align = "left")
    {
        private readonly List<(int Dy, int H, Action<int, int> Draw)> parts = new();
        private int height, left = x;
        /// <summary>Hauteur du bloc, sans l'espace qui suit sa dernière ligne.</summary>
        public int Height => Net;
        public int End { get; private set; }

        public void Move(int newX) => left = newX;

        public void Add(string text, int size, int weight, string color, double lh = 1.15, double ls = 0, double opacity = 1, int maxLines = 3, int gap = 0)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            var (s, lines) = pg.Fit(text, w, size, weight, ls, maxLines);
            var h = (int)Math.Ceiling(lines * s * lh) + 4;
            parts.Add((height, h, (ox, oy) => pg.Text(ox, oy, w, text, size, weight, color, align, lh, ls, opacity, maxLines)));
            height += h + gap;
        }

        /// <summary>Pastille de couleur avec un mot en capitales (« ACTUALITÉ »).</summary>
        public void Tag(string text, int size, Palette pal, int gap)
        {
            if (text.Length == 0) return;
            var tw = (int)Math.Min(w, text.Length * size * 0.78 + size * 1.8);
            var th = (int)(size * 2.1);
            parts.Add((height, th, (ox, oy) =>
            {
                pg.Rect(ox, oy, tw, th, pal.Accent, radius: size / 3);
                pg.Text(ox, oy + (int)(size * 0.45), tw, text, size, 700, pal.Ink, "center", 1.15, 0.1, 1, 1);
            }));
            height += th + gap;
        }

        /// <summary>Un trait de couleur (à gauche, ou centré si le bloc l'est).</summary>
        public void Rule(int rw, int rh, string color, int gap)
        {
            parts.Add((height, rh, (ox, oy) => pg.Rect(align == "center" ? ox + (w - rw) / 2 : ox, oy, rw, rh, color, radius: rh / 2)));
            height += rh + gap;
        }

        /// <summary>Un point numéroté : rond de couleur, chiffre, texte.</summary>
        public void Point(int n, string text, int size, Palette pal, int gap, string? color = null)
        {
            text = text.Trim();
            var ink = color ?? pal.Deep;
            var d = (int)(size * 1.9);
            var tw = w - d - 30;
            var (s, lines) = pg.Fit(text, tw, size, 500, 0, 2);
            var th = (int)Math.Ceiling(lines * s * 1.15) + 4;
            var h = Math.Max(d, th);
            parts.Add((height, h, (ox, oy) =>
            {
                pg.Circle(ox, oy + (h - d) / 2, d, pal.Accent);
                pg.Text(ox, oy + (h - d) / 2 + (int)((d - size * 0.9 * 1.15) / 2), d, n.ToString(CultureInfo.InvariantCulture), (int)(size * 0.9), 700, pal.Ink, "center", 1.15, 0, 1, 1);
                pg.Text(ox + d + 30, oy + (h - th) / 2, tw, text, size, 500, ink, "left", 1.15, 0, 1, 2);
            }));
            height += h + gap;
        }

        /// <summary>Logo sur une plaque blanche, centré dans le bloc.</summary>
        public void Logo(Guid media, int gap)
        {
            parts.Add((height, 112, (ox, oy) =>
            {
                var lx = ox + (w - 300) / 2;
                pg.Rect(lx, oy, 300, 112, "#FFFFFF", radius: 20);
                pg.Image(lx + 22, oy + 14, 256, 84, media, "contain");
            }));
            height += 112 + gap;
        }

        private int Net => parts.Count == 0 ? 0 : parts[^1].Dy + parts[^1].H;

        public void Top(int y) { foreach (var p in parts) p.Draw(left, y + p.Dy); End = y + Net; }
        public void Bottom(int bottom) => Top(bottom - Net);
        public void Middle(int top, int bottom) => Top(top + (bottom - top - Net) / 2);
    }
}
