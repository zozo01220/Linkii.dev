using System.Text.Json;
using System.Text.Json.Nodes;

namespace Linkii.Poc;

/// <summary>
/// Premier écran d'un nouveau compte (DEV-POC.md §126) : un assistant en deux étapes remplace la page Écrans vide.
/// 1. « Que doit afficher votre écran ? » : un modèle (composition 1920×1080 : surtitre, titre, sous-titre, horloge), une photo en fond, ou une page web.
/// 2. « Allumez votre écran » : le code d'appairage, l'écran reçoit aussitôt « Ma première liste ».
/// L'état est gardé sur l'organisation (<see cref="Tenant.FirstScreen"/>) : remis à plus tard, la page Écrans propose de reprendre.
/// </summary>
public static class FirstScreen
{
    public const string Later = "later", Done = "done";
    public const string ListName = "Ma première liste", ContentName = "Mon premier écran";

    public record Template(string Id, string Name, string Bg, string Kick, string Title, string Sub);

    public static readonly IReadOnlyList<Template> Templates =
    [
        new("accueil", "Accueil", "#0B1F3A", "Bienvenue", "Bienvenue", "Nous sommes ravis de vous recevoir"),
        new("menu", "Menu du jour", "#2FA66A", "Au menu aujourd'hui", "Risotto aux champignons", "Plat du jour · 18 CHF"),
        new("evenement", "Événement", "#7B5CC4", "Cette semaine", "Portes ouvertes jeudi", "De 14 h à 18 h · entrée libre"),
        new("promotion", "Promotion", "#F2541B", "Ce week-end", "-20 % sur tout", "En magasin uniquement"),
        new("annonce", "Annonce", "#3B82C4", "À savoir", "Wi-Fi invités", "Réseau « Invités » · demandez le code à l'accueil"),
    ];

    /// <summary>Ce que la personne a préparé à l'étape 1, gardé pour reprendre plus tard (Tenant.FirstScreenDraft).</summary>
    public sealed class Draft
    {
        public string Mode { get; set; } = "tpl";      // tpl | photo | url
        public string Tpl { get; set; } = "accueil";
        public string Bg { get; set; } = "#0B1F3A";
        public string Kick { get; set; } = "";
        public string Title { get; set; } = "";
        public string Sub { get; set; } = "";
        public Guid? Photo { get; set; }
        public string? PhotoFile { get; set; }
        public string? Url { get; set; }

        public static Draft From(Template t) => new() { Tpl = t.Id, Bg = t.Bg, Kick = t.Kick, Title = t.Title, Sub = t.Sub };

        public static Draft Read(string? json)
        {
            try { if (!string.IsNullOrEmpty(json) && JsonSerializer.Deserialize<Draft>(json) is { } d) return d; }
            catch (JsonException) { }
            return From(Templates[0]);
        }

        public string Json() => JsonSerializer.Serialize(this);
    }

    /// <summary>Document de la composition (format de wwwroot/player/composition.js) : fond ou photo assombrie, textes en bas à gauche, horloge en haut à droite.</summary>
    public static string Doc(Draft d)
    {
        var els = new JsonArray();
        if (d.Mode == "photo" && d.Photo is { } photo)
        {
            els.Add(new JsonObject { ["id"] = "e1", ["type"] = "image", ["media"] = photo.ToString(), ["x"] = 0, ["y"] = 0, ["w"] = 1920, ["h"] = 1080, ["fit"] = "cover", ["radius"] = 0, ["opacity"] = 1 });
            els.Add(new JsonObject { ["id"] = "e2", ["type"] = "shape", ["kind"] = "rect", ["x"] = 0, ["y"] = 0, ["w"] = 1920, ["h"] = 1080, ["fill"] = "#000000", ["stroke"] = "", ["sw"] = 0, ["radius"] = 0, ["rot"] = 0, ["opacity"] = 0.4 });
        }
        els.Add(Text("e3", d.Kick, 150, 110, 1200, 70, 44, 400, 0.85));
        els.Add(new JsonObject { ["id"] = "e4", ["type"] = "widget", ["kind"] = "clock", ["x"] = 1370, ["y"] = 100, ["w"] = 400, ["h"] = 80, ["font"] = "IBM Plex Sans", ["size"] = 52, ["weight"] = 500, ["color"] = "#ffffff", ["align"] = "right", ["ls"] = 0, ["lh"] = 1.1, ["italic"] = false, ["opacity"] = 0.85 });
        // titre posé juste au-dessus du sous-titre, comme dans l'aperçu : environ 22 caractères par ligne à cette taille, deux lignes au plus
        var lines = Math.Clamp(d.Title.Split('\n').Sum(l => Math.Max(1, (int)Math.Ceiling(l.Trim().Length / 22.0))), 1, 2);
        els.Add(Text("e5", d.Title, 150, 840 - lines * 137, 1620, lines * 137 + 10, 130, 500, 1, 1.05));
        els.Add(Text("e6", d.Sub, 150, 860, 1620, 120, 52, 400, 0.85));
        return new JsonObject { ["v"] = 1, ["w"] = 1920, ["h"] = 1080, ["bg"] = d.Bg, ["els"] = els }.ToJsonString();
    }

    private static JsonObject Text(string id, string text, int x, int y, int w, int h, int size, int weight, double opacity, double lh = 1.15) => new()
    {
        ["id"] = id, ["type"] = "text", ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h, ["text"] = text.Trim(), ["font"] = "IBM Plex Sans", ["size"] = size, ["weight"] = weight,
        ["color"] = "#ffffff", ["align"] = "left", ["ls"] = 0, ["lh"] = lh, ["italic"] = false, ["opacity"] = opacity,
    };

    /// <summary>« Ma première liste » ne contient que le contenu de l'assistant (créée au premier passage, remplie à nouveau ensuite). Renvoie son identifiant.</summary>
    public static Guid PutInList(ClientDb d, Guid contentId)
    {
        var list = d.Tenant.FirstScreenListId is { } lid ? d.Playlists.FirstOrDefault(p => p.Id == lid) : null;
        if (list == null)
        {
            list = new Playlist { Name = ListName, UpdatedAt = DateTime.UtcNow };
            d.Playlists.Add(list);
            d.Tenant.FirstScreenListId = list.Id;
        }
        if (list.Draft.Count != 1 || list.Draft[0].MediaId != contentId)
        {
            list.Draft.Clear();
            list.Draft.Add(new PlaylistItem { MediaId = contentId, DurationSec = d.Tenant.DefaultDurationSec, Placement = "full" });
        }
        list.Touch();
        return list.Id;
    }

    /// <summary>L'assistant s'ouvre tout seul : rien n'a encore été décidé et l'organisation n'a aucun écran relié.</summary>
    public static bool ShouldOpen(ClientDb d) => d.Tenant.FirstScreen == null && !HasScreen(d);

    /// <summary>Au moins un écran relié dans toute l'organisation (quel que soit ce que la personne voit).</summary>
    public static bool HasScreen(ClientDb d) => d.Root.Screens.Any(s => s.ClientId == d.ClientId && s.Token != null);
}
