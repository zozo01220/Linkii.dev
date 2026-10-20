using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>
/// Compositions (éditeur de compositions, voir DEV-POC.md §80) : une page faite de calques (textes, formes, images, QR codes, horloge…),
/// enregistrée dans le paramètre « doc » d'un contenu de l'app « composition ». Document : { v, w, h, bg, els: [ … ] }, positions en pixels
/// de la page (w × h). Le rendu est commun à l'éditeur et au player : wwwroot/player/composition.js.
/// </summary>
public static class Compositions
{
    public const string AppId = "composition";
    public const int MaxDocLength = 400_000, MaxElements = 400, MinSide = 320, MaxSide = 7680;

    /// <summary>Document vérifié (format, taille, nombre d'éléments) et les images de la médiathèque qu'il utilise, dans l'ordre. Null si illisible.</summary>
    public static (string Doc, List<Guid> Images)? Clean(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxDocLength) return null;
        JsonObject? doc;
        try { doc = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
        if (doc == null || Side(doc["w"]) is not { } w || Side(doc["h"]) is not { } h) return null;
        if (doc["els"] is not JsonArray els || els.Count > MaxElements) return null;

        var images = new List<Guid>();
        var clean = new JsonArray();
        foreach (var n in els)
        {
            if (n is not JsonObject e || Str(e, "type") is not ("text" or "shape" or "image" or "qr" or "widget")) continue;
            var copy = (JsonObject)e.DeepClone();
            copy.Remove("src");   // adresse de fichier : posée seulement à la publication
            if (Str(copy, "type") == "image" && Guid.TryParse(Str(copy, "media"), out var id) && !images.Contains(id)) images.Add(id);
            clean.Add(copy);
        }
        var result = new JsonObject { ["v"] = 1, ["w"] = w, ["h"] = h, ["bg"] = Color(Str(doc, "bg")) ?? "#FFFFFF", ["els"] = clean };
        return (result.ToJsonString(), images);
    }

    /// <summary>
    /// Paramètres envoyés à l'écran : les images deviennent des adresses /media/… (fichier prêt du même client, sinon l'image est retirée),
    /// les QR codes leur tracé calculé ici, les calques masqués disparaissent. Urls : fichiers que l'écran précharge (lecture hors ligne).
    /// Null si le document est illisible (contenu jamais publié).
    /// </summary>
    public static (Dictionary<string, string> Settings, List<string> Urls)? Publish(IReadOnlyDictionary<string, string> settings, Func<Guid, MediaItem?> file)
    {
        if (Clean(settings.GetValueOrDefault("doc")) is not { } c) return null;
        var doc = JsonNode.Parse(c.Doc)!.AsObject();
        var els = doc["els"]!.AsArray();
        var urls = new List<string>();
        foreach (var e in els.OfType<JsonObject>().ToList())
        {
            if (e["hidden"] is JsonValue hv && hv.TryGetValue<bool>(out var hidden) && hidden) { els.Remove(e); continue; }
            switch (Str(e, "type"))
            {
                case "image":
                    var f = Guid.TryParse(Str(e, "media"), out var id) ? file(id) : null;
                    if (f?.FileName == null) { els.Remove(e); continue; }
                    var url = "/media/" + f.FileName;
                    e["src"] = url;
                    e.Remove("media");
                    if (!urls.Contains(url)) urls.Add(url);
                    break;
                case "qr":
                    if (QrCodes.Encode(Str(e, "url")) is { } q) e["qr"] = q;
                    else els.Remove(e);
                    break;
            }
        }
        var s = new Dictionary<string, string>(settings) { ["doc"] = doc.ToJsonString() };
        return (s, urls);
    }

    /// <summary>Taille de la page d'une composition (celle de son document) ; null si le document est illisible.</summary>
    public static (int W, int H)? Size(IReadOnlyDictionary<string, string> settings)
    {
        try { return JsonNode.Parse(settings.GetValueOrDefault("doc") ?? "") is JsonObject d && Side(d["w"]) is { } w && Side(d["h"]) is { } h ? (w, h) : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Cadre d'aperçu d'une composition (CSS en ligne) : celui de sa page, pas celui de l'écran. Le QR de comptage se place et se mesure en % de cette page
    /// (comme le player, qui le dessine dans la page mise à l'échelle) : la même position à l'aperçu et sur tous les écrans, quel que soit leur format.
    /// </summary>
    public static string StageStyle(IReadOnlyDictionary<string, string> settings)
    {
        var s = Size(settings) ?? (1920, 1080);
        return s.Item2 > s.Item1
            ? $"aspect-ratio:auto;width:calc(360px * {s.Item1} / {s.Item2});height:360px;max-height:none;margin:0 auto"
            : $"aspect-ratio:{s.Item1} / {s.Item2}";
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Side(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) && d >= MinSide && d <= MaxSide ? (int)Math.Round(d) : null;

    private static string? Color(string? v) => v != null && Regex.IsMatch(v, "^#[0-9a-fA-F]{6}$") ? v : null;
}
