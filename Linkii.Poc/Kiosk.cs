using System.Collections.Concurrent;
using System.Text.Json;

namespace Linkii.Poc;

/// <summary>Une tuile du menu de la borne : un contenu de la médiathèque (fichier, PDF, présentation, composition, page web…) ouvert au toucher.</summary>
public class KioskTile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MediaId { get; set; }
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public double TextScale { get; set; } = 1;     // taille du texte de la tuile : 0,75 petit · 1 normal · 1,3 grand · 1,7 très grand
    public string Color { get; set; } = "#3B82C4";
    public Guid? ImageMediaId { get; set; }     // image de la médiathèque en fond de tuile (sinon la couleur)
    public string? ShowFrom { get; set; }        // aaaa-mm-jj : affichée à partir de…
    public string? ShowTo { get; set; }          // …jusqu'à (inclus)
    /// <summary>Identifiant publié du contenu et QR « Emporter sur mon téléphone » (mêmes champs et même comptage des scans que les listes de lecture).</summary>
    public PlaylistItem Item { get; set; } = new();
}

/// <summary>
/// Réglages d'une borne interactive (écran Windows tactile) : menu de tuiles, retours automatiques, accessibilité, session du visiteur, verrouillage.
/// Un écran sans ce bloc (<see cref="Screen.Kiosk"/> null) est un écran de diffusion. L'orientation et la résolution sont celles de l'écran.
/// Partout où la borne n'est pas touchée, elle diffuse comme un écran ordinaire (liste, widgets, planning de l'écran).
/// </summary>
public class KioskConfig
{
    public string Title { get; set; } = "Bienvenue";
    public string Prompt { get; set; } = "Touchez une tuile pour commencer.";
    public string Bg { get; set; } = "#FFFFFF";
    public string? TitleFont { get; set; }        // clé de Kiosk.Fonts ; null = police par défaut
    public string? TitleColor { get; set; }       // #rrggbb ; null = couleur par défaut
    public string? PromptFont { get; set; }
    public string? PromptColor { get; set; }
    public Guid? LogoMediaId { get; set; }
    public int ReturnSec { get; set; } = 30;      // sans toucher, retour à l'accueil
    public int IdleSec { get; set; } = 120;       // sans toucher à l'accueil, retour à la diffusion
    public bool Accessible { get; set; }          // tuiles et bouton Retour dans la moitié basse de l'écran
    public bool BigText { get; set; }
    public bool Wipe { get; set; } = true;        // retour à l'accueil : tout remettre à zéro (page 1, vidéos arrêtées, historique effacé)
    public bool Sound { get; set; } = true;
    public int Volume { get; set; } = 60;
    public bool Lock { get; set; } = true;        // empêcher de quitter l'application
    public bool NoZoom { get; set; } = true;
    public bool HideCursor { get; set; } = true;
    public bool WakeOnTouch { get; set; } = true; // un toucher réveille la borne pendant la veille
    // Aspect commun à toutes les tuiles (paramètres avancés de l'onglet Tuiles) ; null / 0 = valeur par défaut du player
    public string? TileFont { get; set; }         // clé de Kiosk.Fonts
    public string? TileColor { get; set; }        // couleur du texte des tuiles (#rrggbb)
    public double TileScale { get; set; } = 1;    // taille du texte de toutes les tuiles (se cumule avec la taille propre à une tuile)
    public string? TileWeight { get; set; }       // normal | bold
    public string? TileAlign { get; set; }        // left | center | right
    public string? TileValign { get; set; }       // top | middle | bottom
    public string? TileRadius { get; set; }       // square | round | pill
    public string? TileGap { get; set; }          // tight | normal | wide
    public string? TileShade { get; set; }        // voile sous le texte des tuiles à image : none | light | normal | strong
    public int TileCols { get; set; }             // nombre de colonnes (0 = automatique)
    public List<KioskTile> Tiles { get; set; } = new();

    public static KioskConfig Starter() => new();
}

/// <summary>Tuile figée à la publication : le contenu comme le player sait l'afficher, plus son habillage.</summary>
public class PublishedKioskTile
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public double TextScale { get; set; } = 1;
    public string Color { get; set; } = "#3B82C4";
    public string? ImageUrl { get; set; }
    public string? ShowFrom { get; set; }
    public string? ShowTo { get; set; }
    public PublishedItem Item { get; set; } = new();
}

/// <summary>Ce que la borne affiche depuis la dernière publication de l'écran.</summary>
public class PublishedKiosk
{
    public KioskConfig Config { get; set; } = new();
    public string? LogoUrl { get; set; }
    public List<PublishedKioskTile> Tiles { get; set; } = new();

    public object ForPlayer() => new
    {
        title = Config.Title, prompt = Config.Prompt, bg = Config.Bg, logo = LogoUrl,
        titleFont = Kiosk.FontCss(Config.TitleFont), titleColor = Kiosk.CleanHex(Config.TitleColor),
        promptFont = Kiosk.FontCss(Config.PromptFont), promptColor = Kiosk.CleanHex(Config.PromptColor),
        returnSec = Config.ReturnSec, idleSec = Config.IdleSec,
        accessible = Config.Accessible, bigText = Config.BigText, wipe = Config.Wipe,
        sound = Config.Sound, volume = Config.Volume,
        lockApp = Config.Lock, noZoom = Config.NoZoom, hideCursor = Config.HideCursor, wake = Config.WakeOnTouch,
        tileStyle = new
        {
            font = Kiosk.FontCss(Config.TileFont), color = Kiosk.CleanHex(Config.TileColor), scale = Kiosk.CleanScale(Config.TileScale),
            weight = Kiosk.Pick(Kiosk.Weights, Config.TileWeight, "normal").Value, align = Kiosk.Pick(Kiosk.Aligns, Config.TileAlign, "left").Key, valign = Kiosk.Pick(Kiosk.Valigns, Config.TileValign, "bottom").Key,
            radius = Kiosk.Pick(Kiosk.Radii, Config.TileRadius, "round").Value, gap = Kiosk.Pick(Kiosk.Gaps, Config.TileGap, "normal").Value, shade = Kiosk.Pick(Kiosk.Shades, Config.TileShade, "normal").Value,
            cols = Math.Clamp(Config.TileCols, 0, 4)
        },
        tiles = Tiles.Select(t => new
        {
            id = t.Id, title = t.Title, subtitle = t.Subtitle, scale = t.TextScale, color = t.Color, image = t.ImageUrl, from = t.ShowFrom, to = t.ShowTo,
            item = t.Item.ForPlayer()
        })
    };
}

public static class Kiosk
{
    public const int MinReturn = 10, MaxReturn = 300, MinIdle = 30, MaxIdle = 1800;
    public static readonly string[] Colors = { "#3B82C4", "#2E7D5B", "#C77700", "#8A5CF6", "#D0453C", "#5A6B80", "#0B1F3A" };

    public static bool Is(Screen s) => s.Kiosk != null;

    /// <summary>Un écran de borne se publie comme les autres : la borne a des modifications en attente quand son menu a changé.</summary>
    public static string Stamp(Screen s) => s.Kiosk == null ? "" : "|k" + s.KioskRevision;

    /// <summary>Une tuile est affichée aujourd'hui (dates « du / au »).</summary>
    public static bool ShownOn(PublishedKioskTile t, DateOnly day) =>
        (!Planning.IsDate(t.ShowFrom) || DateOnly.Parse(t.ShowFrom!) <= day) && (!Planning.IsDate(t.ShowTo) || DateOnly.Parse(t.ShowTo!) >= day);

    public static readonly (double Scale, string Label)[] TextSizes = { (0.75, "Petit"), (1, "Normal"), (1.3, "Grand"), (1.7, "Très grand") };

    /// <summary>Taille de texte valide parmi <see cref="TextSizes"/> (sinon normale).</summary>
    public static double CleanScale(double s) => TextSizes.Any(x => Math.Abs(x.Scale - s) < 0.01) ? s : 1;

    /// <summary>Couleur valide (#rrggbb) ou la première de la palette.</summary>
    public static string CleanColor(string? c) => c is { Length: 7 } && c[0] == '#' && c.Skip(1).All(Uri.IsHexDigit) ? c.ToUpperInvariant() : Colors[0];

    // Options de l'aspect commun des tuiles : (clé, libellé, valeur numérique pour le player : em, opacité ou graisse)
    public static readonly (string Key, string Label, double Value)[] Weights = { ("normal", "Normal", 500), ("bold", "Gras", 700) };
    public static readonly (string Key, string Label, double Value)[] Aligns = { ("left", "Gauche", 0), ("center", "Centre", 0), ("right", "Droite", 0) };
    public static readonly (string Key, string Label, double Value)[] Valigns = { ("top", "Haut", 0), ("middle", "Milieu", 0), ("bottom", "Bas", 0) };
    public static readonly (string Key, string Label, double Value)[] Radii = { ("square", "Carré", 0), ("round", "Arrondi", 1.6), ("pill", "Très arrondi", 3.6) };
    public static readonly (string Key, string Label, double Value)[] Gaps = { ("tight", "Serré", 1.2), ("normal", "Normal", 2.4), ("wide", "Large", 4) };
    public static readonly (string Key, string Label, double Value)[] Shades = { ("none", "Aucun", 0), ("light", "Léger", 0.35), ("normal", "Normal", 0.62), ("strong", "Fort", 0.82) };

    /// <summary>Option choisie dans une liste d'options (la valeur par défaut si la clé est absente ou inconnue).</summary>
    public static (string Key, string Label, double Value) Pick((string Key, string Label, double Value)[] opts, string? key, string def) =>
        opts.FirstOrDefault(o => o.Key == key) is { Key: not null } o ? o : opts.First(o2 => o2.Key == def);

    /// <summary>Couleurs par défaut du titre et du message d'invite (celles du player).</summary>
    public const string TitleDefault = "#0B1F3A", PromptDefault = "#5A6B80";

    /// <summary>Polices proposées : des polices présentes sur tout poste Windows (clé, libellé, pile CSS).</summary>
    public static readonly (string Key, string Label, string Css)[] Fonts =
    {
        ("", "Par défaut", ""),
        ("arial", "Arial", "Arial, Helvetica, sans-serif"),
        ("verdana", "Verdana", "Verdana, Geneva, sans-serif"),
        ("trebuchet", "Trebuchet MS", "'Trebuchet MS', Tahoma, sans-serif"),
        ("georgia", "Georgia", "Georgia, 'Times New Roman', serif"),
        ("times", "Times New Roman", "'Times New Roman', Times, serif"),
        ("courier", "Courier New", "'Courier New', Courier, monospace"),
        ("impact", "Impact", "Impact, 'Arial Narrow Bold', sans-serif")
    };

    /// <summary>Pile CSS d'une police choisie (chaîne vide = police par défaut).</summary>
    public static string FontCss(string? key) => Fonts.FirstOrDefault(f => f.Key == key).Css ?? "";

    /// <summary>Couleur #rrggbb valide, sinon chaîne vide (= couleur par défaut).</summary>
    public static string CleanHex(string? c) => c is { Length: 7 } && c[0] == '#' && c.Skip(1).All(Uri.IsHexDigit) ? c.ToUpperInvariant() : "";

    /// <summary>Remet une durée dans ses bornes.</summary>
    public static int Clamp(int v, int min, int max) => Math.Clamp(v, min, max);

    /// <summary>Contenus de la médiathèque qu'une tuile peut ouvrir : prêts, ni image de fond, ni page de présentation.</summary>
    public static bool CanOpen(MediaItem m) => m.Status == null && (m.Type != "app" || !string.IsNullOrEmpty(m.AppId));
}

/// <summary>Un événement remonté par la borne (visite, tuile ouverte, fin de visite, QR scanné).</summary>
public record KioskEvent(DateTime AtUtc, Guid ScreenId, string Type, Guid? TileId, string? Title, int Seconds, string? How);

/// <summary>Statistiques de la borne : un fichier JSON Lines par client (kiosk/{client}.jsonl), conservation 90 jours.
/// Rien de personnel : seulement des touchers, des tuiles ouvertes et des durées.</summary>
public class KioskLog
{
    public const int RetentionDays = 90;
    private readonly string dir;
    private readonly ConcurrentDictionary<Guid, object> locks = new();
    private readonly ConcurrentDictionary<Guid, DateTime> lastPrune = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public KioskLog(IConfiguration config, IWebHostEnvironment env)
    {
        dir = Path.Combine(config["Linkii:DataDir"] ?? env.ContentRootPath, "kiosk");
        Directory.CreateDirectory(dir);
    }

    private string FileOf(Guid clientId) => Path.Combine(dir, clientId.ToString("N") + ".jsonl");

    public void Append(Guid clientId, IEnumerable<KioskEvent> entries)
    {
        var lines = entries.Select(e => JsonSerializer.Serialize(e, Json)).ToList();
        if (lines.Count == 0) return;
        lock (locks.GetOrAdd(clientId, _ => new object()))
        {
            File.AppendAllLines(FileOf(clientId), lines);
            if (DateTime.UtcNow - lastPrune.GetOrAdd(clientId, DateTime.MinValue) > TimeSpan.FromDays(1))
            {
                lastPrune[clientId] = DateTime.UtcNow;
                var keep = ReadUnlocked(clientId, DateTime.UtcNow.AddDays(-RetentionDays)).Select(e => JsonSerializer.Serialize(e, Json)).ToList();
                File.WriteAllLines(FileOf(clientId), keep);
            }
        }
    }

    public List<KioskEvent> Read(Guid clientId, Guid screenId, DateTime sinceUtc)
    {
        lock (locks.GetOrAdd(clientId, _ => new object())) return ReadUnlocked(clientId, sinceUtc).Where(e => e.ScreenId == screenId).ToList();
    }

    private List<KioskEvent> ReadUnlocked(Guid clientId, DateTime sinceUtc)
    {
        var path = FileOf(clientId);
        var list = new List<KioskEvent>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonSerializer.Deserialize<KioskEvent>(line, Json) is { } e && e.AtUtc >= sinceUtc) list.Add(e); }
            catch (JsonException) { /* ligne tronquée : ignorée */ }
        }
        return list.OrderBy(e => e.AtUtc).ToList();
    }

    /// <summary>Chiffres de la période : visites, durée moyenne, touchers, QR scannés, tuiles ouvertes, visites par heure (heure du serveur), fins de visite.</summary>
    public static KioskStats Stats(IEnumerable<KioskEvent> events)
    {
        var list = events.ToList();
        var visits = list.Where(e => e.Type == "visit").ToList();
        return new KioskStats
        {
            Visits = visits.Count,
            AvgSeconds = visits.Count == 0 ? 0 : (int)Math.Round(visits.Average(v => v.Seconds)),
            Touches = list.Count(e => e.Type == "touch"),
            Qr = list.Count(e => e.Type == "qr"),
            Tiles = list.Where(e => e.Type == "tile").GroupBy(e => e.TileId).Select(g => (g.Key, g.First().Title ?? "(tuile supprimée)", g.Count())).OrderByDescending(x => x.Item3).ToList(),
            Hours = Enumerable.Range(0, 24).Select(h => visits.Count(v => v.AtUtc.ToLocalTime().Hour == h)).ToArray(),
            EndAuto = visits.Count(v => v.How == "auto"),
            EndBack = visits.Count(v => v.How == "back")
        };
    }
}

public class KioskStats
{
    public int Visits { get; set; }
    public int AvgSeconds { get; set; }
    public int Touches { get; set; }
    public int Qr { get; set; }
    public List<(Guid? Id, string Title, int Count)> Tiles { get; set; } = new();
    public int[] Hours { get; set; } = new int[24];
    public int EndAuto { get; set; }
    public int EndBack { get; set; }
}
