using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>
/// Compositions (éditeur de compositions, voir DEV-POC.md §80) : une page faite de calques (textes, formes, images, QR codes, horloge…),
/// enregistrée dans le paramètre « doc » d'un contenu de l'app « composition ». Document : { v, w, h, bg, els: [ … ] }, positions en pixels
/// de la page (w × h). Le rendu est commun à l'éditeur et au player : wwwroot/player/composition.js.
/// Étape 3 (§85) : un calque « app » pose un bloc d'une intégration de Mes intégrations (source : identifiant du contenu), et un texte peut contenir
/// des champs « {{source:clé}} » (température, prochain événement…) remplis sur l'écran avec les données de cette source.
/// </summary>
public static class Compositions
{
    public const string AppId = "composition";
    public const int MaxDocLength = 400_000, MaxElements = 400, MinSide = 320, MaxSide = 7680;

    /// <summary>Intégrations utilisables comme source d'un bloc ou de champs : celles qui fournissent des données à l'écran.</summary>
    public static readonly IReadOnlySet<string> SourceApps = new HashSet<string> { "weather-slide", "agenda", "rss", "ha-sensors" };

    /// <summary>Champ relié dans un texte : {{identifiant du contenu source (32 chiffres hexadécimaux)}:{clé du champ}}.</summary>
    public static readonly Regex FieldToken = new(@"{{([0-9a-f]{32}):([A-Za-z0-9_.:-]{1,120})}}", RegexOptions.Compiled);

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
            if (n is not JsonObject e || Str(e, "type") is not ("text" or "shape" or "image" or "qr" or "widget" or "app")) continue;
            if (Str(e, "type") == "app" && (!Guid.TryParse(Str(e, "source"), out _) || !SourceApps.Contains(Str(e, "app") ?? ""))) continue;   // bloc sans source valable
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
    public static (Dictionary<string, string> Settings, List<string> Urls, Dictionary<string, string> Secrets)? Publish(IReadOnlyDictionary<string, string> settings, Func<Guid, MediaItem?> file, Func<Guid, MediaItem?>? source = null)
    {
        if (Clean(settings.GetValueOrDefault("doc")) is not { } c) return null;
        var doc = JsonNode.Parse(c.Doc)!.AsObject();
        var els = doc["els"]!.AsArray();
        var urls = new List<string>();
        // Sources (blocs et champs) : réglages figés à la publication, comme un contenu d'intégration ; l'écran lit leurs données par /api/data/{élément}/{source}.
        // Une source introuvable (supprimée, d'un autre client) retire ses blocs ; ses champs affichent leur valeur de secours.
        var sources = new Dictionary<Guid, MediaItem?>();
        foreach (var id in Sources(els)) sources[id] = source?.Invoke(id) is { AppId: { } a } m && SourceApps.Contains(a) ? m : null;
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
                case "app":
                    if (!Guid.TryParse(Str(e, "source"), out var sid) || sources.GetValueOrDefault(sid) is not { } src || src.AppId != Str(e, "app")) els.Remove(e);
                    break;
            }
        }
        var s = new Dictionary<string, string>(settings) { ["doc"] = doc.ToJsonString() };
        var secrets = new Dictionary<string, string>();
        foreach (var (id, m) in sources)
        {
            if (m == null) continue;
            s["src:" + id.ToString("N")] = JsonSerializer.Serialize(new SourceSnapshot(m.AppId!, new Dictionary<string, string>(m.Settings)));
            foreach (var (k, v) in m.Secrets) secrets["src:" + id.ToString("N") + ":" + k] = v;   // déjà chiffrés ; jamais envoyés à l'écran
        }
        return (s, urls, secrets);
    }

    private record SourceSnapshot(string App, Dictionary<string, string> Settings);

    /// <summary>Sources utilisées par un document : blocs (« source ») et champs des textes, dans l'ordre d'apparition.</summary>
    public static List<Guid> Sources(string? json)
    {
        try { return JsonNode.Parse(json ?? "") is JsonObject d && d["els"] is JsonArray a ? Sources(a) : new(); }
        catch (JsonException) { return new(); }
    }

    private static List<Guid> Sources(JsonArray els)
    {
        var ids = new List<Guid>();
        foreach (var e in els.OfType<JsonObject>())
        {
            if (Str(e, "type") == "app" && Guid.TryParse(Str(e, "source"), out var id) && !ids.Contains(id)) ids.Add(id);
            if (Str(e, "type") == "text" && Str(e, "text") is { } t)
                foreach (Match f in FieldToken.Matches(t))
                    if (Guid.TryParse(f.Groups[1].Value, out var fid) && !ids.Contains(fid)) ids.Add(fid);
        }
        return ids;
    }

    /// <summary>
    /// Élément publié d'une source d'une composition publiée (réglages et secrets figés à la publication), pour lire ses données comme un contenu
    /// d'intégration ordinaire. Null si la composition n'utilisait pas cette source.
    /// </summary>
    public static PublishedItem? SourceItem(PublishedItem composition, Guid source)
    {
        var key = "src:" + source.ToString("N");
        if (composition.AppId != AppId || composition.Settings.GetValueOrDefault(key) is not { } raw) return null;
        SourceSnapshot? snap;
        try { snap = JsonSerializer.Deserialize<SourceSnapshot>(raw); }
        catch (JsonException) { return null; }
        if (snap == null || !SourceApps.Contains(snap.App)) return null;
        return new PublishedItem
        {
            Id = composition.Id, Type = "app", AppId = snap.App, Settings = snap.Settings,
            Secrets = composition.Secrets.Where(x => x.Key.StartsWith(key + ":")).ToDictionary(x => x.Key[(key.Length + 1)..], x => x.Value)
        };
    }

    /// <summary>Taille de la page d'une composition (celle de son document) ; null si le document est illisible.</summary>
    public static (int W, int H)? Size(IReadOnlyDictionary<string, string> settings)
    {
        try { return JsonNode.Parse(settings.GetValueOrDefault("doc") ?? "") is JsonObject d && Side(d["w"]) is { } w && Side(d["h"]) is { } h ? (w, h) : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Nombre de calques d'une composition (0 si le document est illisible).</summary>
    public static int ElementCount(IReadOnlyDictionary<string, string> settings)
    {
        try { return JsonNode.Parse(settings.GetValueOrDefault("doc") ?? "") is JsonObject d && d["els"] is JsonArray a ? a.Count : 0; }
        catch (JsonException) { return 0; }
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

/// <summary>
/// « Mes modèles » (§84) : une composition enregistrée comme point de départ réutilisable. Rangé dans l'aire (comme les compositions), visible de ses membres ;
/// partir d'un modèle crée une nouvelle composition, le modèle ne change pas. Document au même format que <see cref="Compositions"/>.
/// </summary>
public class CompositionTemplate : IClientOwned, IAreaOwned, IOwned
{
    public Guid ClientId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AreaId { get; set; }
    public string Name { get; set; } = "";
    public string Doc { get; set; } = "";
    /// <summary>Images de la médiathèque utilisées (identifiants, séparés par des virgules) : elles comptent comme « utilisées ».</summary>
    public string Images { get; set; } = "";
    public Guid? OwnerId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Droits sur les compositions et « Mes modèles » (24 octobre 2026) : comme les médias, chacun a les siens, plus ceux qu'on lui partage
/// (« Peut utiliser » : les mettre dans ses listes, partir d'un modèle ; « Peut modifier » : les changer). Aucun filtre d'aire.
/// Les autres contenus d'intégrations ne sont pas concernés.
/// </summary>
public static class CompositionRights
{
    public static bool Is(MediaItem m) => m.AppId == Compositions.AppId;

    public static FolderRight RightOf(ClientDb d, MediaItem m) =>
        Is(m) ? Ownership.Right(d.Access, ShareKind.Composition, m.Id, m.OwnerId, Array.Empty<IFolderNode>()) : FolderRight.Owner;

    public static FolderRight RightOf(ClientDb d, CompositionTemplate t) =>
        Ownership.Right(d.Access, ShareKind.Template, t.Id, t.OwnerId, Array.Empty<IFolderNode>());

    public static LibraryTab.Info TabOf(ClientDb d, string kind, Guid id, Guid? ownerId) => Ownership.TabInfo(d.Access, kind, id, ownerId, Array.Empty<IFolderNode>());
}

/// <summary>Opérations sur « Mes modèles » : enregistrer, renommer, supprimer (ceux de la personne, ou partagés « Peut modifier »).</summary>
public static class CompositionTemplates
{
    public const int MaxPerOwner = 200;

    /// <summary>Modèles que la personne voit : les siens et ceux qu'on lui partage (tous, en lecture seule).</summary>
    public static List<CompositionTemplate> Visible(ClientDb d) => d.CompositionTemplates.Where(t => CompositionRights.RightOf(d, t) != FolderRight.None).ToList();

    /// <summary>Enregistre un document comme modèle (à la personne connectée) ; rend l'erreur, ou null.</summary>
    public static (CompositionTemplate? Template, string? Error) Save(ClientDb d, string name, string doc)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return (null, "Donnez un nom au modèle.");
        if (name.Length > 80) return (null, "Le nom est trop long (80 caractères maximum).");
        if (Compositions.Clean(doc) is not { } c) return (null, "La composition est trop grande ou illisible : le modèle n'a pas été enregistré.");
        if (!d.Access.CanWrite(LevelPages.Media)) return (null, "Vous êtes en lecture seule : vous ne pouvez pas enregistrer de modèle.");
        if (d.CompositionTemplates.Count(t => t.OwnerId == d.Me) >= MaxPerOwner) return (null, $"Vous avez déjà {MaxPerOwner} modèles : supprimez-en avant d'en ajouter.");
        var t = new CompositionTemplate { Name = name, Doc = c.Doc, Images = string.Join(",", c.Images) };
        d.CompositionTemplates.Add(t);
        return (t, null);
    }

    public static string? Rename(ClientDb d, Guid id, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return "Donnez un nom au modèle.";
        if (name.Length > 80) return "Le nom est trop long (80 caractères maximum).";
        if (d.CompositionTemplates.FirstOrDefault(t => t.Id == id) is not { } t) return "Modèle introuvable.";
        if (CompositionRights.RightOf(d, t) < FolderRight.Write) return "Ce modèle ne vous appartient pas : vous ne pouvez pas le renommer.";
        t.Name = name;
        return null;
    }

    /// <summary>Supprime un modèle : son propriétaire (ou un administrateur) seulement.</summary>
    public static bool Delete(ClientDb d, Guid id)
    {
        if (d.CompositionTemplates.FirstOrDefault(t => t.Id == id) is not { } t || CompositionRights.RightOf(d, t) != FolderRight.Owner) return false;
        d.CompositionTemplates.RemoveAll(x => x.Id == id);
        ItemShareOps.Forget(d, ShareKind.Template, [id]);
        return true;
    }
}
