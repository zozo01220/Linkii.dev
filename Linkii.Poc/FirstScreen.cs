using System.Text.Json;
using System.Text.Json.Nodes;

namespace Linkii.Poc;

/// <summary>
/// Premiers pas d'un nouveau compte (DEV-POC.md §126, refait au §190) : l'assistant « Premiers pas » (<see cref="GettingStarted"/>) remplit la
/// médiathèque et crée « Ma première liste » ; l'assistant de l'écran (code d'appairage) vient ensuite, proposé par la page Écrans tant qu'il n'y a aucun écran.
/// Les modèles ci-dessous (composition 1920×1080 : surtitre, titre, sous-titre, horloge) sont les points de départ des Premiers pas.
/// L'état est gardé sur l'organisation (<see cref="Tenant.FirstScreen"/>) : null = Premiers pas pas encore vus (ils s'ouvrent tout seuls),
/// « later » = fermés, « done » = un écran est relié.
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

    /// <summary>Les Premiers pas s'ouvrent tout seuls : rien n'a encore été décidé et l'organisation n'a aucun écran relié.</summary>
    public static bool ShouldOpen(ClientDb d) => d.Tenant.FirstScreen == null && !HasScreen(d);

    /// <summary>La liste des Premiers pas existe encore et contient au moins un contenu : la page Écrans la propose pour le premier écran.</summary>
    public static Guid? ReadyList(ClientDb d) =>
        d.Tenant.FirstScreenListId is { } lid && d.Playlists.FirstOrDefault(p => p.Id == lid) is { Draft.Count: > 0 } pl ? pl.Id : null;

    /// <summary>Au moins un écran relié dans toute l'organisation (quel que soit ce que la personne voit).</summary>
    public static bool HasScreen(ClientDb d) => d.Root.Screens.Any(s => s.ClientId == d.ClientId && s.Token != null);
}
