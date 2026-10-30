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

    /// <summary>
    /// Source « Cet écran » (§139) : nom, zone et paramètres de l'écran qui affiche la composition (champs {{…:name}}, {{…:p.etage}}, filtres des listes).
    /// Identifiant fixe, le même dans toutes les compositions.
    /// </summary>
    public static readonly Guid ScreenSource = new("5c2ee000-5c2e-4e00-a000-000000000000");

    /// <summary>Calques reliés à un jeu de données (§139) : liste reliée (« list », source = identifiant du jeu).</summary>
    public const string ListType = "list";

    /// <summary>Champ relié dans un texte : {{identifiant du contenu source (32 chiffres hexadécimaux)}:{clé du champ}}.</summary>
    /// <summary>Champ relié d'un texte : {{jeu:clé}} ou {{jeu:clé|d2,cCHF}} (format du nombre, §142). Les groupes 1 et 2 sont le jeu et la clé.</summary>
    public static readonly Regex FieldToken = new(@"{{([0-9a-f]{32}):([A-Za-z0-9_.:-]{1,120})(?:\|([A-Za-z0-9,]{1,30}))?}}", RegexOptions.Compiled);

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
            if (n is not JsonObject e || Str(e, "type") is not ("text" or "shape" or "image" or "qr" or "widget" or "app" or "list")) continue;
            if (Str(e, "type") == ListType && !Guid.TryParse(Str(e, "source"), out _)) continue;   // liste sans jeu de données
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
    public static (Dictionary<string, string> Settings, List<string> Urls, Dictionary<string, string> Secrets)? Publish(IReadOnlyDictionary<string, string> settings, Func<Guid, MediaItem?> file, Func<Guid, MediaItem?>? source = null,
        Func<Guid, DataSet?>? dataset = null)
    {
        if (Clean(settings.GetValueOrDefault("doc")) is not { } c) return null;
        var doc = JsonNode.Parse(c.Doc)!.AsObject();
        var els = doc["els"]!.AsArray();
        var urls = new List<string>();
        // Sources (blocs et champs) : réglages figés à la publication, comme un contenu d'intégration ; l'écran lit leurs données par /api/data/{élément}/{source}.
        // Une source introuvable (supprimée, d'un autre client) retire ses blocs ; ses champs affichent leur valeur de secours.
        var sources = new Dictionary<Guid, MediaItem?>();
        var sets = new Dictionary<Guid, DataSet>();   // jeux de données (§139) : l'écran lit leur dernière copie, réduite aux colonnes utilisées
        foreach (var id in Sources(els))
        {
            if (id == ScreenSource) continue;
            if (dataset?.Invoke(id) is { } ds) { sets[id] = ds; continue; }
            sources[id] = source?.Invoke(id) is { AppId: { } a } m && SourceApps.Contains(a) ? m : null;
        }
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
                case ListType:
                    if (!Guid.TryParse(Str(e, "source"), out var lid) || !sets.ContainsKey(lid)) els.Remove(e);   // jeu supprimé : la liste n'est pas diffusée
                    break;
            }
        }
        var s = new Dictionary<string, string>(settings) { ["doc"] = doc.ToJsonString() };
        foreach (var (id, _) in sets) s["ds:" + id.ToString("N")] = JsonSerializer.Serialize(UsedColumns(els, id));
        if (Sources(els).Contains(ScreenSource)) s["scr"] = "1";
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
            if (Str(e, "type") is "app" or ListType && Guid.TryParse(Str(e, "source"), out var id) && !ids.Contains(id)) ids.Add(id);
            if (Str(e, "type") == ListType && e["filter"] is JsonArray fl && fl.OfType<JsonObject>().Any(x => (Str(x, "val") ?? "").StartsWith('@')) && !ids.Contains(ScreenSource))
                ids.Add(ScreenSource);   // filtre sur un paramètre de l'écran (« = Cet écran · Étage »)
            if (Str(e, "type") == "text" && Str(e, "text") is { } t)
                foreach (Match f in FieldToken.Matches(t))
                    if (Guid.TryParse(f.Groups[1].Value, out var fid) && !ids.Contains(fid)) ids.Add(fid);
        }
        return ids;
    }

    /// <summary>
    /// Colonnes d'un jeu qu'utilise la composition (colonnes, tri, filtres et règles de ses listes ; champs des textes, « r2.col », « sum.col »…) :
    /// seules celles-ci partent vers l'écran.
    /// </summary>
    public static List<string> UsedColumns(JsonArray els, Guid dataSet)
    {
        var n = dataSet.ToString("N");
        var keys = new List<string>();
        void Add(string? k) { if (!string.IsNullOrEmpty(k) && k.Length <= 60 && !keys.Contains(k)) keys.Add(k); }
        foreach (var e in els.OfType<JsonObject>())
        {
            if (Str(e, "type") == ListType && Str(e, "source") is { } src && Guid.TryParse(src, out var g) && g == dataSet)
            {
                foreach (var arr in new[] { e["cols"], e["filter"], e["rules"] })
                    if (arr is JsonArray a) foreach (var x in a.OfType<JsonObject>()) Add(Str(x, "key"));
                if (e["sort"] is JsonObject so) Add(Str(so, "key"));
            }
            if (Str(e, "type") == "text" && Str(e, "text") is { } t)
                foreach (Match f in FieldToken.Matches(t))
                    if (f.Groups[1].Value == n) Add(Regex.Replace(f.Groups[2].Value, @"^(r\d+|sum|avg|min|max)\.", "") is var k && k != "_count" ? k : null);
        }
        return keys;
    }

    /// <summary>Jeu de données d'une composition publiée et colonnes à envoyer ; null si la composition ne l'utilisait pas.</summary>
    public static List<string>? DataRef(PublishedItem composition, Guid source)
    {
        if (composition.AppId != AppId || composition.Settings.GetValueOrDefault("ds:" + source.ToString("N")) is not { } raw) return null;
        try { return JsonSerializer.Deserialize<List<string>>(raw) ?? new(); }
        catch (JsonException) { return null; }
    }

    /// <summary>La composition publiée lit-elle « Cet écran » ?</summary>
    public static bool UsesScreen(PublishedItem composition, Guid source) =>
        source == ScreenSource && composition.AppId == AppId && composition.Settings.ContainsKey("scr");

    /// <summary>Données de la source « Cet écran » : nom, zone, aire et paramètres (clé → valeur, clé → nom).</summary>
    public static object ScreenData(Db db, Screen? s) => new
    {
        kind = "screen",
        name = s?.Name ?? "Aperçu",
        zone = s == null ? "" : db.Areas.FirstOrDefault(a => a.Id == s.AreaId)?.Zones.FirstOrDefault(z => z.Id == s.ZoneId)?.Name ?? "",
        area = s == null ? "" : db.Areas.FirstOrDefault(a => a.Id == s.AreaId)?.Name ?? "",
        @params = (s?.Params ?? new()).Where(p => p.Name.Trim().Length > 0).GroupBy(p => DataTables.Slug(p.Name)).ToDictionary(g => g.Key, g => g.First().Value),
        labels = (s?.Params ?? new()).Where(p => p.Name.Trim().Length > 0).GroupBy(p => DataTables.Slug(p.Name)).ToDictionary(g => g.Key, g => g.First().Name.Trim())
    };

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

    /// <summary>Dossiers qui contiennent une composition (du plus proche au plus haut) : vide si elle est à la racine.</summary>
    private static List<CompositionFolder> Parents(ClientDb d, Guid? folderId) =>
        folderId == null ? new() : Ownership.Parents(folderId, CompositionFolderOps.All(d).ToDictionary(f => f.Id));

    public static FolderRight RightOf(ClientDb d, MediaItem m) =>
        Is(m) ? Ownership.Right(d.Access, ShareKind.Composition, m.Id, m.OwnerId, Parents(d, m.FolderId)) : FolderRight.Owner;

    public static FolderRight RightOf(ClientDb d, CompositionTemplate t) =>
        Ownership.Right(d.Access, ShareKind.Template, t.Id, t.OwnerId, Array.Empty<IFolderNode>());

    public static LibraryTab.Info TabOf(ClientDb d, string kind, Guid id, Guid? ownerId, Guid? folderId = null) =>
        Ownership.TabInfo(d.Access, kind, id, ownerId, Parents(d, folderId));
}

/// <summary>
/// Dossier des compositions (25 octobre 2026), comme ceux de la médiathèque et des listes de lecture : à son créateur, privé tant qu'il n'est pas partagé,
/// partage « Peut utiliser » / « Peut modifier » qui descend dans les sous-dossiers. Une composition y est rangée par son <see cref="MediaItem.FolderId"/>.
/// Les modèles ne sont pas rangés dans des dossiers.
/// </summary>
public class CompositionFolder : IClientOwned, IFolderNode, IOwned
{
    public Guid ClientId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AreaId { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public Guid? OwnerId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<FolderShare> Shares { get; set; } = new();
}

/// <summary>Un dossier de compositions vu par la personne, avec son droit, le nom de son propriétaire et s'il est partagé.</summary>
public record CompositionFolderRow(CompositionFolder Folder, FolderRight Right, string OwnerName, bool Shared, LibraryTab.Info Tab);

/// <summary>Création, renommage, suppression, partage et rangement des compositions dans les dossiers. Chaque méthode rend un message d'erreur, ou null si c'est fait.</summary>
public static class CompositionFolderOps
{
    public static List<CompositionFolder> All(ClientDb d) => d.Root.CompositionFolders.Where(f => f.ClientId == d.ClientId).ToList();

    public static CompositionFolder? Find(ClientDb d, Guid id) => d.CompositionFolders.FirstOrDefault(f => f.Id == id);

    public static FolderRight RightOf(ClientDb d, CompositionFolder f) => FolderAccess.For(d.Access, f, All(d));

    /// <summary>Les compositions suivent la médiathèque : créer à la racine demande d'y écrire.</summary>
    public static bool CanCreateAtRoot(ClientDb d) => FolderAccess.CanCreateAtRoot(d.Access, LevelPages.Media);

    /// <summary>Dossiers que la personne voit, avec son droit sur chacun et leur place dans les onglets.</summary>
    public static List<CompositionFolderRow> Rows(ClientDb d)
    {
        var all = All(d);
        var shared = ItemShareOps.Counts(d, ShareKind.CompositionFolder);
        var rows = new List<CompositionFolderRow>();
        foreach (var f in d.CompositionFolders)
        {
            var right = FolderAccess.For(d.Access, f, all);
            if (right == FolderRight.None) continue;
            var owner = d.Users.FirstOrDefault(u => u.Id == f.OwnerId)?.Label ?? "—";
            rows.Add(new CompositionFolderRow(f, right, owner, shared.ContainsKey(f.Id), FolderAccess.TabInfo(d.Access, f, all)));
        }
        return rows.OrderBy(r => r.Folder.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static (string? Error, CompositionFolder? Folder) Create(ClientDb d, string name, Guid? parentId)
    {
        name = name.Trim();
        if (FolderOps.CheckName(name) is { } e) return (e, null);
        var all = All(d);
        CompositionFolder? parent = null;
        var depth = 1;
        if (parentId is { } pid)
        {
            parent = Find(d, pid);
            if (parent == null) return ("Dossier introuvable.", null);
            if (RightOf(d, parent) < FolderRight.Write) return ("Vous ne pouvez pas créer de dossier dans celui-ci.", null);
            depth = FolderAccess.Chain(parent, all).Count() + 1;
        }
        else if (!CanCreateAtRoot(d)) return ("Vous ne pouvez pas créer de dossier ici (lecture seule).", null);
        if (depth > FolderAccess.MaxDepth) return ($"Les dossiers ne s'imbriquent pas au-delà de {FolderAccess.MaxDepth} niveaux.", null);

        if (all.Any(f => f.ParentId == parentId && (parentId != null || f.OwnerId == d.Me) && f.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
            return ("Un dossier porte déjà ce nom ici.", null);
        var folder = new CompositionFolder { Name = name, ParentId = parentId, AreaId = parent?.AreaId ?? d.Area?.Id ?? Guid.Empty };
        d.CompositionFolders.Add(folder);
        return (null, folder);
    }

    public static string? Rename(ClientDb d, Guid id, string name)
    {
        if (Find(d, id) is not { } f) return "Dossier introuvable.";
        if (RightOf(d, f) != FolderRight.Owner) return "Seul le propriétaire du dossier peut le renommer.";
        name = name.Trim();
        if (FolderOps.CheckName(name) is { } e) return e;
        if (All(d).Any(x => x.Id != id && x.ParentId == f.ParentId && (f.ParentId != null || x.OwnerId == f.OwnerId) && x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
            return "Un dossier porte déjà ce nom ici.";
        f.Name = name;
        return null;
    }

    /// <summary>Dossier et sous-dossiers à supprimer, avec les compositions qu'ils contiennent. Refusé si un sous-dossier appartient à quelqu'un d'autre.</summary>
    public static string? PlanDelete(ClientDb d, Guid id, out HashSet<Guid> folderIds, out List<Guid> compositionIds)
    {
        folderIds = new(); compositionIds = new();
        if (Find(d, id) is not { } f) return "Dossier introuvable.";
        if (RightOf(d, f) != FolderRight.Owner) return "Seul le propriétaire du dossier peut le supprimer.";
        var all = All(d);
        var tree = new HashSet<Guid>();
        var queue = new Queue<Guid>(new[] { id });
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!tree.Add(cur)) continue;
            foreach (var child in all.Where(x => x.ParentId == cur)) queue.Enqueue(child.Id);
        }
        foreach (var sub in all.Where(x => tree.Contains(x.Id)))
            if (FolderAccess.For(d.Access, sub, all) != FolderRight.Owner) return $"Le sous-dossier « {sub.Name} » appartient à un autre membre : retirez-le d'abord.";
        folderIds = tree;
        compositionIds = d.Root.Media.Where(m => m.ClientId == d.ClientId && CompositionRights.Is(m) && m.FolderId is { } fid && tree.Contains(fid)).Select(m => m.Id).ToList();
        return null;
    }

    /// <summary>Retire les dossiers planifiés par <see cref="PlanDelete"/> (après leurs compositions).</summary>
    public static void Forget(ClientDb d, IEnumerable<Guid> folderIds)
    {
        var set = folderIds.ToHashSet();
        d.Root.CompositionFolders.RemoveAll(f => f.ClientId == d.ClientId && set.Contains(f.Id));
        ItemShareOps.Forget(d, ShareKind.CompositionFolder, set);
    }

    // ---------- Déplacer un dossier dans un autre (voir FolderMove) ----------

    public static FolderContents ContentsOf(ClientDb d, Guid id) =>
        new(d.Root.Media.Count(m => m.ClientId == d.ClientId && CompositionRights.Is(m) && m.FolderId == id), All(d).Count(f => f.ParentId == id));

    public static List<FolderTarget> MoveTargets(ClientDb d, Guid id)
    {
        var all = All(d);
        if (Find(d, id) is not { } f) return new();
        var list = new List<FolderTarget> { new(null, "Racine des compositions", 0, f.ParentId == null ? "déjà à la racine" : null) };
        foreach (var g in d.CompositionFolders.Select(x => (Folder: x, Path: string.Join(" › ", FolderAccess.Chain(x, all).Reverse().Select(c => c.Name)))).OrderBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase))
        {
            if (RightOf(d, g.Folder) == FolderRight.None) continue;
            var reason = g.Folder.Id == f.ParentId ? "déjà ici" : FolderMove.CheckTarget(all, f, g.Folder, RightOf(d, g.Folder)) is { } r ? FolderMove.Explain(r) : null;
            list.Add(new(g.Folder.Id, g.Path, g.Path.Count(c => c == '›'), reason));
        }
        return list;
    }

    /// <summary>Range un dossier dans un autre, ou le dissout (destination : la racine) : ses compositions et ses sous-dossiers vont à la racine.</summary>
    public static string? MoveFolder(ClientDb d, Guid id, Guid? newParent)
    {
        if (Find(d, id) is not { } f) return "Dossier introuvable.";
        if (RightOf(d, f) != FolderRight.Owner) return "Seul le propriétaire du dossier peut le déplacer.";
        var all = All(d);
        if (newParent == null)
        {
            if (f.ParentId == null) return null;
            if (FolderMove.RootNameClash(all, f) is { } clash) return $"Un dossier « {clash} » existe déjà à la racine : renommez-le d'abord.";
            foreach (var m in d.Root.Media.Where(m => m.ClientId == d.ClientId && CompositionRights.Is(m) && m.FolderId == id)) { m.FolderId = null; m.OwnerId ??= f.OwnerId; }
            foreach (var c in all.Where(x => x.ParentId == id)) c.ParentId = null;
            Forget(d, new[] { id });
            return null;
        }
        if (Find(d, newParent.Value) is not { } target) return "Dossier introuvable.";
        if (f.ParentId == target.Id) return null;
        if (FolderMove.CheckTarget(all, f, target, RightOf(d, target)) is { } reason) return FolderMove.Explain(reason);
        f.ParentId = target.Id;
        return null;
    }

    /// <summary>Range des compositions dans un dossier, ou à la racine (null). Il faut pouvoir écrire dans le dossier d'arrivée et modifier chaque composition.</summary>
    public static (int Moved, List<string> Refused) Move(ClientDb d, IEnumerable<Guid> compositionIds, Guid? folderId)
    {
        var moved = 0; var refused = new List<string>();
        var wanted = compositionIds.ToHashSet();
        if (folderId is { } tid)
        {
            if (Find(d, tid) is not { } target || RightOf(d, target) < FolderRight.Write) return (0, new() { "Vous ne pouvez pas ranger de composition dans ce dossier." });
        }
        else if (!CanCreateAtRoot(d)) return (0, new() { "Vous ne pouvez pas ranger de composition à la racine (lecture seule)." });

        foreach (var m in d.Root.Media.Where(x => x.ClientId == d.ClientId && wanted.Contains(x.Id) && CompositionRights.Is(x)).ToList())
        {
            if (m.FolderId == folderId) continue;
            if (CompositionRights.RightOf(d, m) < FolderRight.Write) { refused.Add($"« {m.Name} » : vous ne pouvez pas la ranger (lecture seule)."); continue; }
            m.FolderId = folderId;   // ranger ne change pas le contenu : rien à republier
            moved++;
        }
        return (moved, refused);
    }

    /// <summary>Range une composition qui vient d'être créée dans le dossier ouvert (si on peut y écrire) ; sans effet sinon, elle reste à la racine.</summary>
    public static void PlaceNew(ClientDb d, Guid compositionId, Guid? folderId)
    {
        if (folderId is not { } fid || Find(d, fid) is not { } f || RightOf(d, f) < FolderRight.Write) return;
        if (d.Root.Media.FirstOrDefault(m => m.Id == compositionId && m.ClientId == d.ClientId && CompositionRights.Is(m)) is { } m) m.FolderId = fid;
    }
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
