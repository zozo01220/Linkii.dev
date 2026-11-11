using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

// Premiers pas avec l'assistant (DEV-POC §232, maquette maquettes/premiers-pas-assistant-ia.html) : l'IA écrit le contenu des pages
// (textes, couleurs, police, choix du logo et des photos) ; la mise en page vient des modèles ci-dessous, pour un rendu toujours propre.
// Les retouches (« Plus chaleureux », « Aussi en anglais », « Version portrait », demande libre) modifient ce contenu, puis les pages sont redessinées.

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

    /// <summary>Logo et photos de la médiathèque ; adresse du site (QR des pages, « horizon-sa.ch · à bientôt »). Jamais envoyés à l'IA.</summary>
    [JsonIgnore] public Guid? Logo { get; set; }
    [JsonIgnore] public List<Guid> Photos { get; set; } = new();
    [JsonIgnore] public string Site { get; set; } = "";

    public StartBrief Clone()
    {
        var c = System.Text.Json.JsonSerializer.Deserialize<StartBrief>(System.Text.Json.JsonSerializer.Serialize(this, StartTemplates.Json), StartTemplates.Json)!;
        c.Logo = Logo; c.Photos = new(Photos); c.Site = Site;
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
    public StartInfo? Info { get; set; }
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
    public static readonly string[] Kinds = { "welcome", "services", "info", "news", "thanks" };
    public static readonly string[] Icons = { "horloge", "position", "telephone", "courrier", "wifi", "parking", "accessibilite", "information", "calendrier" };
    public static readonly string[] Fonts = { "Montserrat", "Poppins", "Playfair Display", "Oswald", "IBM Plex Sans" };
    private static readonly Regex Hex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    /// <summary>La page existe pour ce contenu (infos pratiques et actualité sont facultatives).</summary>
    public static bool Has(StartPages p, string kind) => kind switch
    {
        "info" => p.Info is { Cards.Count: > 0 } && p.Info.Title.Trim().Length > 0,
        "news" => p.News != null && p.News.Title.Trim().Length > 0,
        _ => true,
    };

    /// <summary>Nom de la composition dans la médiathèque et dans le fil.</summary>
    public static string Name(StartPages p, string kind, bool en)
    {
        var n = kind switch
        {
            "welcome" => en ? "Welcome" : "Accueil",
            "services" => Cut(p.Services.Kicker, 40) is { Length: > 0 } k ? k : en ? "Our services" : "Nos services",
            "info" => en ? "Practical information" : "Infos pratiques",
            "news" => Cut(p.News?.Tag ?? "", 40) is { Length: > 0 } t ? t : en ? "News" : "Actualité",
            _ => en ? "Thank you" : "Merci",
        };
        return en && kind is "services" or "news" ? n + " (EN)" : n;
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

    /// <summary>Document de composition d'une page (déjà propre pour <see cref="Compositions.Clean"/>).</summary>
    public static string Doc(StartBrief b, string kind, bool portrait, bool en)
    {
        var p = en && b.En != null ? b.En : b.Pages;
        var pal = Palette.Of(b.Colors);
        var font = Fonts.Contains(b.Font) ? b.Font : "Montserrat";
        var pg = new Page(portrait ? 1080 : 1920, portrait ? 1920 : 1080, font);
        Guid? Photo(int i) => b.Photos.Count == 0 ? null : b.Photos[i % b.Photos.Count];
        switch (kind)
        {
            case "welcome": Welcome(pg, b, p.Welcome, pal, Photo(0)); break;
            case "services": Services(pg, p.Services, pal, Photo(1)); break;
            case "info": Info(pg, b, p.Info ?? new StartInfo(), pal); break;
            case "news": News(pg, b, p.News ?? new StartNews(), pal, Photo(2), en); break;
            default: Thanks(pg, b, p.Thanks, pal, Photo(3)); break;
        }
        return pg.Json(kind is "services" ? pal.Light : pal.Deep);
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

    private static void Decor(Page pg, Palette pal)
    {
        if (pg.Portrait) { pg.Circle(420, -320, 1000, pal.Accent, 0.16); pg.Circle(560, -180, 720, pal.Deep); }
        else { pg.Circle(1360, -420, 1100, pal.Accent, 0.16); pg.Circle(1540, -240, 740, pal.Deep); }
    }

    private static void Welcome(Page pg, StartBrief b, StartText t, Palette pal, Guid? photo)
    {
        if (photo is { } ph)
        {
            pg.Image(0, 0, pg.W, pg.H, ph);
            pg.Rect(0, 0, pg.W, pg.H, pal.Deep, 0.42);
            if (pg.Portrait) pg.Rect(0, 900, pg.W, pg.H - 900, pal.Deep, 0.45); else pg.Rect(0, 0, 1250, pg.H, pal.Deep, 0.4);
        }
        else Decor(pg, pal);
        Brand(pg, b, pal, pg.Portrait ? 80 : 96, pg.Portrait ? 90 : 84);
        if (b.City != null)
        {
            var (cx, cw) = pg.Portrait ? (728, 272) : (1512, 312);
            pg.Rect(cx, pg.Portrait ? 90 : 84, cw, 100, "#FFFFFF", 0.16, 50);
            pg.Clock(cx, (pg.Portrait ? 90 : 84) + 18, cw, 56, "#FFFFFF");
        }
        var x = pg.Portrait ? 80 : 96;
        var w = pg.Portrait ? 920 : 1180;
        var block = new Block(pg, x, w);
        block.Add(Upper(t.Kicker), pg.Portrait ? 36 : 34, 700, pal.OnDark, ls: 0.16, maxLines: 1, gap: 18);
        block.Add(t.Title, pg.Portrait ? 108 : 116, 700, "#FFFFFF", lh: 1.04, maxLines: pg.Portrait ? 4 : 3, gap: 26);
        block.Add(t.Sub, pg.Portrait ? 46 : 44, 400, "#FFFFFF", opacity: 0.92, maxLines: pg.Portrait ? 3 : 2);
        block.Bottom(pg.H - (pg.Portrait ? 200 : 110));
    }

    private static void Services(Page pg, StartPoints t, Palette pal, Guid? photo)
    {
        int x, w, top;
        if (pg.Portrait)
        {
            if (photo is { } ph) pg.Image(0, 0, pg.W, 760, ph);
            else { pg.Rect(0, 0, pg.W, 760, pal.Deep); pg.Circle(560, 180, 640, pal.Accent, 0.3); }
            pg.Rect(0, 760, pg.W, 24, pal.Accent);
            (x, w, top) = (90, 900, 880);
        }
        else
        {
            if (photo is { } ph) pg.Image(1072, 0, 848, pg.H, ph);
            else { pg.Rect(1072, 0, 848, pg.H, pal.Deep); pg.Circle(1260, 260, 560, pal.Accent, 0.3); }
            pg.Rect(1050, 0, 22, pg.H, pal.Accent);
            (x, w, top) = (120, 860, 0);
        }
        var block = new Block(pg, x, w);
        block.Add(Upper(t.Kicker), pg.Portrait ? 32 : 30, 700, pal.OnLight, ls: 0.16, maxLines: 1, gap: 16);
        block.Add(t.Title, pg.Portrait ? 84 : 84, 700, pal.Deep, lh: 1.06, maxLines: 3, gap: pg.Portrait ? 56 : 48);
        var i = 0;
        foreach (var pt in t.Points.Where(s => s.Trim().Length > 0).Take(4))
            block.Point(++i, pt, pg.Portrait ? 44 : 40, pal, gap: pg.Portrait ? 36 : 30);
        if (pg.Portrait) block.Top(top); else block.Middle(0, pg.H);
    }

    private static void Info(Page pg, StartBrief b, StartInfo t, Palette pal)
    {
        Decor(pg, pal);
        Brand(pg, b, pal, pg.Portrait ? 80 : 120, 90);
        var x = pg.Portrait ? 80 : 120;
        var head = new Block(pg, x, pg.Portrait ? 920 : 1500);
        head.Add(Upper(t.Kicker), 32, 700, pal.OnDark, ls: 0.16, maxLines: 1, gap: 16);
        head.Add(t.Title, pg.Portrait ? 100 : 100, 700, "#FFFFFF", lh: 1.04, maxLines: 2);
        head.Top(pg.Portrait ? 420 : 300);
        var cards = t.Cards.Where(c => c.Value.Trim().Length > 0 || c.Label.Trim().Length > 0).Take(3).ToList();
        if (cards.Count == 0) return;
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
            return;
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
    }

    private static void News(Page pg, StartBrief b, StartNews t, Palette pal, Guid? photo, bool en)
    {
        if (photo is { } ph)
        {
            pg.Image(0, 0, pg.W, pg.H, ph);
            pg.Rect(0, 0, pg.W, pg.H, pal.Deep, 0.35);
            if (pg.Portrait) pg.Rect(0, 760, pg.W, pg.H - 760, pal.Deep, 0.5); else pg.Rect(0, 460, pg.W, pg.H - 460, pal.Deep, 0.5);
        }
        else Decor(pg, pal);
        var link = SafeLink(t.Link) ?? SafeLink(b.Site.Length > 0 ? "https://" + b.Site : null);
        var label = en ? "Find out more" : "En savoir plus";
        var x = pg.Portrait ? 80 : 96;
        var block = new Block(pg, x, pg.Portrait ? 920 : 1180);
        block.Tag(Upper(t.Tag), pg.Portrait ? 30 : 28, pal, gap: 26);
        block.Add(t.Title, pg.Portrait ? 96 : 92, 700, "#FFFFFF", lh: 1.05, maxLines: pg.Portrait ? 4 : 3, gap: 24);
        block.Add(t.Sub, pg.Portrait ? 44 : 40, 400, "#FFFFFF", opacity: 0.92, maxLines: 2);
        if (link == null) { block.Bottom(pg.H - (pg.Portrait ? 200 : 110)); return; }
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
    }

    private static void Thanks(Page pg, StartBrief b, StartText t, Palette pal, Guid? photo)
    {
        if (photo is { } ph) { pg.Image(0, 0, pg.W, pg.H, ph); pg.Rect(0, 0, pg.W, pg.H, pal.Deep, 0.55); }
        else Decor(pg, pal);
        var x = pg.Portrait ? 80 : 160;
        var w = pg.W - 2 * x;
        var block = new Block(pg, x, w, "center");
        if (b.Logo is { } logo) block.Logo(logo, gap: 48);
        block.Add(t.Title, pg.Portrait ? 104 : 110, 700, "#FFFFFF", lh: 1.04, maxLines: pg.Portrait ? 4 : 2, gap: 26);
        block.Add(t.Sub, pg.Portrait ? 44 : 40, 500, "#FFFFFF", opacity: 0.9, ls: 0.02, maxLines: 2);
        block.Middle(0, pg.H);
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
    private sealed class Page(int w, int h, string font)
    {
        public readonly int W = w, H = h;
        public readonly string Font = font;
        public bool Portrait => H > W;
        private readonly JsonArray els = new();

        private static string NewId() => Guid.NewGuid().ToString("N")[..8];

        public void Rect(int x, int y, int w, int h, string fill, double opacity = 1, int radius = 0) =>
            els.Add(new JsonObject { ["id"] = NewId(), ["type"] = "shape", ["kind"] = "rect", ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h, ["fill"] = fill, ["stroke"] = "", ["sw"] = 6, ["radius"] = radius, ["rot"] = 0, ["opacity"] = opacity });

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

        /// <summary>Un point numéroté : rond de couleur, chiffre, texte.</summary>
        public void Point(int n, string text, int size, Palette pal, int gap)
        {
            text = text.Trim();
            var d = (int)(size * 1.9);
            var tw = w - d - 30;
            var (s, lines) = pg.Fit(text, tw, size, 500, 0, 2);
            var th = (int)Math.Ceiling(lines * s * 1.15) + 4;
            var h = Math.Max(d, th);
            parts.Add((height, h, (ox, oy) =>
            {
                pg.Circle(ox, oy + (h - d) / 2, d, pal.Accent);
                pg.Text(ox, oy + (h - d) / 2 + (int)((d - size * 0.9 * 1.15) / 2), d, n.ToString(CultureInfo.InvariantCulture), (int)(size * 0.9), 700, pal.Ink, "center", 1.15, 0, 1, 1);
                pg.Text(ox + d + 30, oy + (h - th) / 2, tw, text, size, 500, pal.Deep, "left", 1.15, 0, 1, 2);
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
