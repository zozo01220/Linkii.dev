namespace Linkii.Poc;

/// <summary>Une case d'un bandeau (gauche, centre ou droite) : un contenu court et ses réglages.</summary>
public class BandCell
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "";
    public Dictionary<string, string> Settings { get; set; } = new();
    /// <summary>Cette case reçoit les messages envoyés par l'API ou Home Assistant avec l'emplacement « bandeau » (une seule case par écran).</summary>
    public bool Ha { get; set; }
    // Remplis à la publication seulement (ce que reçoit le player) : app du catalogue qui dessine la case, adresse de l'image du logo.
    public PublishedItem? Item { get; set; }
    public string? ImageUrl { get; set; }

    public string S(string key, string dflt = "") => Settings.TryGetValue(key, out var v) && v != "" ? v : dflt;
}

/// <summary>Un bandeau : trois cases, une hauteur et un fond.</summary>
public class Band
{
    public bool On { get; set; }
    public string Size { get; set; } = "moyenne";   // fine | moyenne | large : part du petit côté de l'écran
    public string Bg { get; set; } = "nuit";        // nuit | blanc | signal | noir
    public BandCell? Left { get; set; }
    public BandCell? Center { get; set; }
    public BandCell? Right { get; set; }

    public IEnumerable<BandCell> Cells => new[] { Left, Center, Right }.OfType<BandCell>();
}

/// <summary>Bandeaux d'un écran (ou d'un mur) : un en haut, un en bas. Posés sur l'écran, ils restent affichés quelle que soit la liste de lecture.</summary>
public class ScreenBands
{
    public Band Top { get; set; } = new();
    public Band Bottom { get; set; } = new();

    public IEnumerable<Band> Both => new[] { Top, Bottom };
    public bool Any => Top.On || Bottom.On;
    /// <summary>Une case reçoit les messages de Home Assistant sur un bandeau actif.</summary>
    public BandCell? Receiver => Both.Where(b => b.On).SelectMany(b => b.Cells).FirstOrDefault(c => c.Ha);

    public ScreenBands Clone() => System.Text.Json.JsonSerializer.Deserialize<ScreenBands>(System.Text.Json.JsonSerializer.Serialize(this))!;
}

public static class BandKinds
{
    public record Kind(string Id, string Name, string Icon, string Group, string? AppId = null);

    public static readonly Kind[] All =
    {
        new("text", "Texte", "type", "Texte"),
        new("ticker", "Messages défilants", "arrow-right", "Texte"),
        new("label", "Étiquette", "tag", "Texte"),
        new("clock", "Heure et date", "clock", "Heure et météo"),
        new("weather", "Météo", "sun", "Heure et météo", "weather"),
        new("clockwx", "Heure et météo", "clock", "Heure et météo", "weather"),
        new("logo", "Logo", "image", "Image"),
        new("rss", "Actualités (RSS)", "rss", "Flux", "rss-widget"),
        new("agenda", "Agenda du jour", "calendar", "Flux", "agenda"),
        new("data", "Donnée en direct", "activity", "Données", "ha-sensors"),
        new("countdown", "Compte à rebours", "timer", "Données", "countdown"),
        new("qr", "QR code", "qr", "Données", "qrcode"),
    };

    public static readonly string[] Sizes = { "fine", "moyenne", "large" };
    public static readonly string[] Backgrounds = { "nuit", "blanc", "signal", "noir" };
    public static readonly string[] Colors = { "orange", "signal", "rouge" };
    public static readonly string[] Speeds = { "lente", "normal", "rapide" };

    /// <summary>Choix du menu qui n'est pas un type de case : une case de texte qui reçoit les messages de Home Assistant.</summary>
    public const string HaMessage = "ha-message";

    public static Kind? Find(string? id) => All.FirstOrDefault(k => k.Id == id);

    /// <summary>Nouvelle case, avec des réglages par défaut.</summary>
    public static BandCell NewCell(string kind)
    {
        var c = new BandCell { Kind = kind };
        switch (kind)
        {
            case "text": c.Settings["text"] = "Bienvenue"; break;
            case "ticker": c.Settings["items"] = "Premier message\nDeuxième message"; c.Settings["speed"] = "normal"; break;
            case "label": c.Settings["text"] = "Infos"; c.Settings["color"] = "orange"; break;
            case "weather": case "clockwx": c.Settings["city"] = "Lausanne"; break;
            case "logo": c.Settings["text"] = ""; break;
            case "rss": c.Settings["url"] = ""; break;
            case "countdown": c.Settings["label"] = "Événement"; break;
            case "qr": c.Settings["label"] = "Wi-Fi invité"; break;
            case "data": c.Settings["title"] = ""; break;
        }
        return c;
    }
}

public static class Bands
{
    public const int MaxText = 160, MaxItems = 600;
    /// <summary>Hauteur d'un bandeau, en part du petit côté de l'écran (comme la maquette : le même bandeau sur un téléviseur et sur un totem).</summary>
    public static double Fraction(string? size) => size switch { "fine" => .07, "large" => .12, _ => .09 };

    /// <summary>Rien ne s'affiche sur un smartphone : trop petit pour trois cases lisibles.</summary>
    public static bool Allowed(Screen s) => s.ScreenType != "phone";

    /// <summary>Réglages remis en ordre : valeurs connues, textes bornés, une seule case réceptrice. Les cases d'un bandeau éteint restent gardées.</summary>
    public static ScreenBands Clean(ScreenBands? b)
    {
        b = b?.Clone() ?? new ScreenBands();
        var ha = false;
        foreach (var band in b.Both)
        {
            if (!BandKinds.Sizes.Contains(band.Size)) band.Size = "moyenne";
            if (!BandKinds.Backgrounds.Contains(band.Bg)) band.Bg = "nuit";
            band.Left = CleanCell(band.Left); band.Center = CleanCell(band.Center); band.Right = CleanCell(band.Right);
            foreach (var c in band.Cells)
            {
                if (c.Ha && (ha || c.Kind is not ("text" or "ticker"))) c.Ha = false;
                ha |= c.Ha;
            }
        }
        return b;
    }

    private static BandCell? CleanCell(BandCell? c)
    {
        if (c == null || BandKinds.Find(c.Kind) == null) return null;
        var s = new Dictionary<string, string>();
        foreach (var (k, v) in c.Settings) s[k] = (v ?? "")[..Math.Min((v ?? "").Length, k == "items" ? MaxItems : MaxText)];
        c.Settings = s; c.Item = null; c.ImageUrl = null;
        return c;
    }

    /// <summary>Même contenu (sans ce qui n'est calculé qu'à la publication) : sert à repérer une modification à publier.</summary>
    public static bool Same(ScreenBands? a, ScreenBands? b) => Json(a) == Json(b);

    private static string Json(ScreenBands? b)
    {
        if (b == null || !b.Any && !b.Both.Any(x => x.Cells.Any())) return "";
        var c = b.Clone();
        foreach (var cell in c.Both.SelectMany(x => x.Cells)) { cell.Item = null; cell.ImageUrl = null; }
        return System.Text.Json.JsonSerializer.Serialize(c);
    }

    /// <summary>Empreinte stable (FNV-1a) : change quand les bandeaux d'un mur changent, pour que ses écrans rechargent leur configuration.</summary>
    public static string Stamp(ScreenBands b)
    {
        uint h = 2166136261;
        foreach (var by in System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(b))) h = (h ^ by) * 16777619;
        return h.ToString("x");
    }

    /// <summary>Contenus du catalogue portés par les cases (leurs données sont demandées au serveur par le player).</summary>
    public static IEnumerable<PublishedItem> Items(ScreenBands? b) =>
        b?.Both.Where(x => x.On).SelectMany(x => x.Cells).Select(c => c.Item).OfType<PublishedItem>() ?? Enumerable.Empty<PublishedItem>();

    /// <summary>Ce que reçoit le player ; null : aucun bandeau actif (ou smartphone).</summary>
    public static object? ForPlayer(ScreenBands? b)
    {
        if (b == null || !b.Any) return null;
        object? Band(Band x, string place) => !x.On ? null : new
        {
            size = x.Size, bg = x.Bg, h = Fraction(x.Size),
            cells = new Dictionary<string, object?> { ["left"] = Cell(x.Left, place), ["center"] = Cell(x.Center, place), ["right"] = Cell(x.Right, place) }
        };
        object? Cell(BandCell? c, string place) => c == null ? null : new
        {
            id = c.Id, k = c.Kind, s = c.Settings, ha = c.Ha, img = c.ImageUrl,
            item = c.Item == null ? null : ForItem(c.Item, place)
        };
        return new { top = Band(b.Top, "top"), bottom = Band(b.Bottom, "bottom"), hasHa = b.Receiver != null };
    }

    private static object ForItem(PublishedItem i, string place) { var p = new PublishedItem { Id = i.Id, Type = i.Type, AppId = i.AppId, AppVersion = i.AppVersion, Placement = place, Settings = i.Settings, DataId = i.DataId }; return p.ForPlayer(); }

    /// <summary>Bandeaux publiés d'un écran : les siens, ou ceux de son mur (un seul fil sur toute la largeur). Rien sur un smartphone.</summary>
    public static ScreenBands? PublishedFor(Db db, Screen s)
    {
        if (!Allowed(s)) return null;
        if (Helpers.Wall(db.Areas, s) != null) return Helpers.ZoneOf(db.Areas, s)?.Bands;
        return s.PublishedVersion > 0 ? s.PublishedBands : null;
    }

    /// <summary>Un message demandé avec l'emplacement « bandeau » : possible seulement si l'écran a une case réceptrice.</summary>
    public static bool CanTakeMessage(Db db, Screen s) => PublishedFor(db, s)?.Receiver != null;
}

/// <summary>État partagé entre l'aperçu de l'écran (bandeaux dessinés) et l'onglet « Bandeaux » : copie de travail, case réglée, case en cours de choix.</summary>
public class BandsState
{
    public ScreenBands Bands { get; set; } = new();
    public string? SelBand { get; set; }
    public string? SelCell { get; set; }
    public string? PickBand { get; set; }
    public string? PickCell { get; set; }
    /// <summary>Home Assistant est relié à l'organisation : le choix « Message Home Assistant » est proposé.</summary>
    public bool HaReady { get; set; }

    public Band Get(string band) => band == "top" ? Bands.Top : Bands.Bottom;

    public BandCell? Cell(string band, string cell) { var b = Get(band); return cell == "left" ? b.Left : cell == "right" ? b.Right : b.Center; }

    public void SetCell(string band, string cell, BandCell? c)
    {
        var b = Get(band);
        if (cell == "left") b.Left = c; else if (cell == "right") b.Right = c; else b.Center = c;
    }

    public bool IsSel(string band, string cell) => SelBand == band && SelCell == cell;
}
