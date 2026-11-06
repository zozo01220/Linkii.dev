using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>Règles de l'intégration Home Assistant, sans accès réseau (testables seules).</summary>
public static class HomeAssistantRules
{
    /// <summary>
    /// Identifiant de l'application envoyé à Home Assistant pendant la connexion par identifiant et mot de passe (affiché dans le profil Home Assistant,
    /// sous « Jetons de rafraîchissement »). Fixé au démarrage d'après Linkii:HomeAssistant:ClientId, sinon l'adresse du site (Linkii:SiteUrl).
    /// </summary>
    public static string ClientId { get; set; } = "https://www.linkivi.com/";

    public const string Sensor = "sensor", BinarySensor = "binary_sensor", Switch = "switch", InputBoolean = "input_boolean", Fan = "fan", Light = "light",
        Climate = "climate", Cover = "cover", Scene = "scene", Script = "script", Lock = "lock";

    /// <summary>Famille d'entités : un onglet dans les listes du back-office, un widget sur les écrans.</summary>
    public sealed record Family(string App, string Label, string Noun, string[] Domains);

    /// <summary>Les sept widgets Home Assistant et les domaines que chacun affiche.</summary>
    public static readonly Family[] Families =
    {
        new("ha-sensors", "Capteurs", "capteurs", new[] { Sensor, BinarySensor }),
        new("ha-switches", "Interrupteurs", "interrupteurs", new[] { Switch, InputBoolean, Fan }),
        new("ha-lights", "Lumières", "lumières", new[] { Light }),
        new("ha-climate", "Thermostats", "thermostats", new[] { Climate }),
        new("ha-covers", "Volets", "volets", new[] { Cover }),
        new("ha-scenes", "Scènes et scripts", "scènes et scripts", new[] { Scene, Script }),
        new("ha-locks", "Serrures", "serrures", new[] { Lock }),
    };

    /// <summary>Domaines qu'un compte Home Assistant expose aux widgets et aux jeux de données.</summary>
    public static readonly string[] Domains = Families.SelectMany(f => f.Domains).ToArray();

    /// <summary>Widgets Home Assistant par famille d'entités.</summary>
    public static readonly string[] AppIds = Families.Select(f => f.App).ToArray();

    /// <summary>Widget « Plan du bâtiment » (§193) : étages empilés, appareils de tous les domaines posés sur chaque plan. Hors <see cref="Families"/> : pas d'onglet dans les listes.</summary>
    public const string PlanApp = "ha-plan";

    /// <summary>Tous les widgets Home Assistant : les sept familles et le plan du bâtiment.</summary>
    public static readonly string[] AllApps = AppIds.Append(PlanApp).ToArray();

    /// <summary>
    /// Étage d'un plan : identifiant, nom, image du plan (adresse /media/…) et rapport largeur / hauteur.
    /// Kind (§195) : « image » (plan importé, appareils posés dessus) ou « rooms » (pièces dessinées sur une grille de W × H mètres ; l'image éventuelle n'est
    /// qu'un calque dans l'éditeur). Gaps : passages entre pièces (murs ouverts sans capteur).
    /// </summary>
    public sealed record PlanFloor(string Id, string Name, string? Img, double Ratio)
    {
        public string Kind { get; init; } = "image";
        public int W { get; init; } = PlanGeometry.DefaultW;
        public int H { get; init; } = PlanGeometry.DefaultH;
        public List<PlanRoom> Rooms { get; init; } = new();
        public List<PlanSeg> Gaps { get; init; } = new();
        public bool Drawn => Kind == "rooms";
    }

    /// <summary>Appareil posé sur un étage : position en fraction (0 à 1) de la largeur et de la hauteur du plan. Seg (étage dessiné) : mur où se colle une porte, une fenêtre ou un volet.</summary>
    public sealed record PlanDevice(string Id, string Floor, double X, double Y)
    {
        public PlanSeg? Seg { get; init; }
    }

    public sealed record Plan(List<PlanFloor> Floors, List<PlanDevice> Devices);

    public const int MaxPlanFloors = 8, MaxPlanDevices = 120;

    /// <summary>
    /// Lit un plan enregistré ; rend toujours un plan valide : étages et appareils inconnus ou en trop retirés, positions ramenées dans le plan,
    /// image gardée seulement si c'est une adresse /media/ du site. Étage dessiné : pièces hors de la grille ou qui en chevauchent une autre écartées,
    /// passages et ouvertures gardés seulement sur un mur de pièce.
    /// </summary>
    public static Plan ParsePlan(string? json)
    {
        var floors = new List<PlanFloor>();
        var devices = new List<PlanDevice>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("floors", out var fs) && fs.ValueKind == JsonValueKind.Array)
                    foreach (var f in fs.EnumerateArray().Take(MaxPlanFloors))
                    {
                        var id = Str(f, "id");
                        if (id.Length == 0 || floors.Any(x => x.Id == id)) continue;
                        var img = Str(f, "img");
                        if (!img.StartsWith("/media/", StringComparison.Ordinal) || img.Contains("..") || img.Contains('?') || img.Length > 200) img = "";
                        var ratio = f.TryGetProperty("ratio", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetDouble(out var rv) ? Math.Clamp(rv, 0.5, 3) : 1.6;
                        var name = Str(f, "name").Trim();
                        var floor = new PlanFloor(id, name.Length == 0 ? "Étage" : name.Length > 40 ? name[..40] : name, img.Length == 0 ? null : img, ratio);
                        if (Str(f, "kind") == "rooms")
                        {
                            int gw = Math.Clamp(Int(f, "w", PlanGeometry.DefaultW), PlanGeometry.MinSize, PlanGeometry.MaxSize);
                            int gh = Math.Clamp(Int(f, "h", PlanGeometry.DefaultH), PlanGeometry.MinSize, PlanGeometry.MaxSize);
                            var rooms = new List<PlanRoom>();
                            if (f.TryGetProperty("rooms", out var rs) && rs.ValueKind == JsonValueKind.Array)
                                foreach (var x in rs.EnumerateArray())
                                {
                                    if (rooms.Count >= PlanGeometry.MaxRooms) break;
                                    var rid = Str(x, "id");
                                    var rn = Str(x, "name").Trim();
                                    var room = new PlanRoom(rid, rn.Length == 0 ? "Pièce" : rn.Length > 30 ? rn[..30] : rn, Int(x, "x", -1), Int(x, "y", -1), Int(x, "w", 0), Int(x, "h", 0));
                                    if (rid.Length == 0 || rid.Length > 24 || rooms.Any(o => o.Id == rid) || room.X < 0 || room.Y < 0 || room.W < 1 || room.H < 1
                                        || room.X + room.W > gw || room.Y + room.H > gh || rooms.Any(o => PlanGeometry.Overlap(o, room))) continue;
                                    rooms.Add(room);
                                }
                            var edges = PlanGeometry.EdgeUnits(rooms);
                            var gaps = new List<PlanSeg>();
                            var used = new HashSet<PlanGeometry.Unit>();
                            if (f.TryGetProperty("gaps", out var gs) && gs.ValueKind == JsonValueKind.Array)
                                foreach (var g in gs.EnumerateArray())
                                {
                                    if (gaps.Count >= PlanGeometry.MaxGaps) break;
                                    if (Seg(g) is not { } s) continue;
                                    var units = PlanGeometry.Units(s).ToList();
                                    if (!units.All(edges.Contains) || units.Any(used.Contains)) continue;
                                    used.UnionWith(units);
                                    gaps.Add(s with { S = 1 });
                                }
                            floor = floor with { Kind = "rooms", W = gw, H = gh, Ratio = Math.Round((double)gw / gh, 4), Rooms = rooms, Gaps = gaps };
                        }
                        floors.Add(floor);
                    }
                if (root.TryGetProperty("devices", out var ds) && ds.ValueKind == JsonValueKind.Array)
                    foreach (var d in ds.EnumerateArray())
                    {
                        if (devices.Count >= MaxPlanDevices) break;
                        var id = Str(d, "id");
                        var fid = Str(d, "floor");
                        var floor = floors.FirstOrDefault(x => x.Id == fid);
                        if (!IsSupported(id) || devices.Any(x => x.Id == id) || floor == null) continue;
                        double Num(string k) => d.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && !double.IsNaN(n) ? Math.Clamp(n, 0, 1) : 0.5;
                        PlanSeg? seg = null;
                        // une ouverture n'existe que sur un étage dessiné, pour un volet ou un capteur, sur un mur libre (ni passage, ni autre ouverture)
                        if (floor.Drawn && Domain(id) is Cover or BinarySensor && d.TryGetProperty("seg", out var sv) && Seg(sv) is { } s0)
                        {
                            var free = PlanGeometry.EdgeUnits(floor.Rooms);
                            foreach (var u in floor.Gaps.Concat(devices.Where(o => o.Floor == floor.Id && o.Seg != null).Select(o => o.Seg!)).SelectMany(PlanGeometry.Units)) free.Remove(u);
                            if (PlanGeometry.Units(s0).All(free.Contains)) seg = s0;
                        }
                        devices.Add(new(id, floor.Id, Math.Round(Num("x"), 4), Math.Round(Num("y"), 4)) { Seg = seg });
                    }
            }
        }
        catch (JsonException) { /* plan illisible : plan vide */ }
        return new(floors, devices);

        static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static int Int(JsonElement e, string k, int def) =>
            e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && !double.IsNaN(n) && Math.Abs(n) < 10000 ? (int)Math.Round(n) : def;
        static PlanSeg? Seg(JsonElement g)
        {
            if (g.ValueKind != JsonValueKind.Object) return null;
            int x = Int(g, "x", -1), y = Int(g, "y", -1), dd = Int(g, "d", -1), len = Int(g, "len", 1), side = Int(g, "s", 1);
            if (x < 0 || y < 0 || dd is not (0 or 90) || len is < 1 or > PlanGeometry.MaxSize) return null;
            return new PlanSeg(x, y, dd, len, side < 0 ? -1 : 1);
        }
    }

    /// <summary>Forme JSON d'un étage, telle qu'elle est enregistrée (les écrans reçoivent en plus les murs, voir le fournisseur).</summary>
    public static object FloorJson(PlanFloor f) => f.Drawn
        ? new
        {
            id = f.Id, name = f.Name, img = f.Img ?? "", ratio = f.Ratio, kind = f.Kind, w = f.W, h = f.H,
            rooms = f.Rooms.Select(r => new { id = r.Id, name = r.Name, x = r.X, y = r.Y, w = r.W, h = r.H }),
            gaps = f.Gaps.Select(g => new { x = g.X, y = g.Y, d = g.D, len = g.Len }),
        }
        : new { id = f.Id, name = f.Name, img = f.Img ?? "", ratio = f.Ratio };

    public static object DeviceJson(PlanDevice d) => d.Seg is { } s
        ? new { id = d.Id, floor = d.Floor, x = d.X, y = d.Y, seg = new { x = s.X, y = s.Y, d = s.D, len = s.Len, s = s.S } }
        : new { id = d.Id, floor = d.Floor, x = d.X, y = d.Y };

    /// <summary>Murs d'un étage dessiné, une fois retirés les passages et les ouvertures posées.</summary>
    public static List<PlanSeg> WallsOf(PlanFloor f, IEnumerable<PlanDevice> devices) =>
        f.Drawn ? PlanGeometry.Walls(f.Rooms, f.Gaps, devices.Where(d => d.Floor == f.Id && d.Seg != null).Select(d => d.Seg!)) : new();

    /// <summary>Pièce où se trouve un appareil (étage dessiné), d'après sa position.</summary>
    public static PlanRoom? RoomOf(PlanFloor f, PlanDevice d) => f.Drawn ? PlanGeometry.RoomAt(f.Rooms, d.X * f.W, d.Y * f.H) : null;

    /// <summary>Plan choisi par un widget « Plan du bâtiment » (réglage planId) dans les plans de l'organisation ; null s'il n'en a pas ou s'il a été supprimé.</summary>
    public static Plan? PlanOf(Tenant t, IReadOnlyDictionary<string, string>? settings) =>
        settings != null && settings.TryGetValue("planId", out var v) && Guid.TryParse(v, out var id) && t.HaPlans.FirstOrDefault(p => p.Id == id) is { } p ? ParsePlan(p.Json) : null;

    /// <summary>Entités qu'un widget Home Assistant lit : celles de son plan (widget plan), sinon celles qu'on y a choisies.</summary>
    public static List<string> EntitiesOf(Tenant t, string? appId, IReadOnlyDictionary<string, string> settings) =>
        appId == PlanApp ? PlanOf(t, settings)?.Devices.Select(d => d.Id).ToList() ?? new() : ParseEntities(settings.GetValueOrDefault("entities"));

    /// <summary>Plan nettoyé, prêt à être enregistré, et entités qu'il utilise (séparées par des virgules).</summary>
    public static (string Json, string Entities) NormalizePlan(string? json)
    {
        var p = ParsePlan(json);
        var clean = JsonSerializer.Serialize(new
        {
            floors = p.Floors.Select(FloorJson),
            devices = p.Devices.Select(DeviceJson),
        });
        return (p.Floors.Count == 0 ? "" : clean, string.Join(',', p.Devices.Select(d => d.Id)));
    }

    /// <summary>Commandes possibles par domaine (rien pour les capteurs).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Actions = new Dictionary<string, string[]>
    {
        [Switch] = new[] { "on", "off" },
        [InputBoolean] = new[] { "on", "off" },
        [Fan] = new[] { "on", "off" },
        [Light] = new[] { "on", "off", "brightness", "color" },
        [Climate] = new[] { "temp" },
        [Cover] = new[] { "open", "close", "stop", "position" },
        [Scene] = new[] { "activate" },
        [Script] = new[] { "activate" },
        [Lock] = new[] { "lock", "unlock" },
    };

    private static readonly Regex EntityIdPattern = new("^[a-z_]+\\.[a-z0-9_]+$");
    private static readonly Regex HexColor = new("^#?([0-9a-fA-F]{6})$");
    private static readonly char[] Separators = { ',', ';', ' ', '\n', '\r', '\t' };

    public static bool IsHaApp(string? appId) => appId != null && AllApps.Contains(appId);

    public static Family? FamilyOf(string? appId) => Families.FirstOrDefault(f => f.App == appId);

    /// <summary>Famille d'un domaine (onglet des listes).</summary>
    public static Family? FamilyOfDomain(string domain) => Families.FirstOrDefault(f => f.Domains.Contains(domain));

    /// <summary>Entités saisies ou choisies (séparées par des virgules), sans doublon et au bon format.</summary>
    public static List<string> ParseEntities(string? value) =>
        (value ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(e => EntityIdPattern.IsMatch(e)).Distinct().ToList();

    public static string Domain(string entityId) => entityId.Contains('.') ? entityId[..entityId.IndexOf('.')] : "";

    /// <summary>Le domaine d'une entité est-il géré par Linkivi ?</summary>
    public static bool IsSupported(string entityId) => EntityIdPattern.IsMatch(entityId) && Domains.Contains(Domain(entityId));

    /// <summary>Commande autorisée : l'entité est d'un domaine du widget, et ce domaine accepte cette commande.</summary>
    public static bool Commandable(string appId, string entityId, string action = "on") =>
        EntityIdPattern.IsMatch(entityId) && (appId == PlanApp ? Domains.Contains(Domain(entityId)) : FamilyOf(appId) is { } f && f.Domains.Contains(Domain(entityId)))
        && Actions.TryGetValue(Domain(entityId), out var a) && a.Contains(action);

    /// <summary>Commandes à proposer à l'écran pour une entité (aucune si « Commande à l'écran » est coupée).</summary>
    public static string[] ControlsFor(string entityId, bool control) =>
        control && Actions.TryGetValue(Domain(entityId), out var a) ? a : Array.Empty<string>();

    public static string BaseUrl(string? url) => (url ?? "").Trim().TrimEnd('/');

    /// <summary>
    /// Adresse de l'instance : en direct, https joignable depuis internet ; par le relais, http ou https, adresse du réseau local acceptée
    /// (homeassistant.local:8123, 192.168.1.20:8123). Rend un message d'erreur, ou null.
    /// </summary>
    public static string? CheckUrl(string url, string via)
    {
        url = BaseUrl(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Host.Length == 0 || u.UserInfo.Length > 0)
            return via == "relay" ? "Saisissez l'adresse de Home Assistant (http://homeassistant.local:8123)." : "Saisissez l'adresse en https:// (https://maison.exemple.ch).";
        if (via != "relay" && u.Scheme != "https")
            return "En accès direct, l'adresse doit être en https:// et joignable depuis internet. Pour une adresse du réseau local, choisissez « Par le relais Linkii ».";
        return null;
    }

    /// <summary>Normalise une commande reçue de l'écran (ancien format : { entityId, on }).</summary>
    public static string ActionOf(HaCommandDto d) => (d.Action ?? "").Trim().ToLowerInvariant() is { Length: > 0 } a ? a : d.On == true ? "on" : "off";

    /// <summary>Appel de service Home Assistant d'une commande : domaine, service et corps JSON. Null si la commande n'a pas de sens pour l'entité.</summary>
    public static (string Domain, string Service, string Body)? ServiceCall(string entityId, string action, double? value = null, string? color = null)
    {
        if (!IsSupported(entityId)) return null;
        var domain = Domain(entityId);
        if (!Actions.TryGetValue(domain, out var allowed) || !allowed.Contains(action)) return null;
        var data = new Dictionary<string, object> { ["entity_id"] = entityId };
        string service;
        switch (action)
        {
            case "on": service = "turn_on"; break;
            case "off": service = "turn_off"; break;
            case "brightness":
                if (value is not { } b || double.IsNaN(b)) return null;
                var pct = (int)Math.Round(Math.Clamp(b, 0, 100));
                if (pct == 0) service = "turn_off";
                else { service = "turn_on"; data["brightness_pct"] = pct; }
                break;
            case "color":
                if (color == null || HexColor.Match(color.Trim()) is not { Success: true } m) return null;
                var hex = m.Groups[1].Value;
                service = "turn_on";
                data["rgb_color"] = new[] { Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex[2..4], 16), Convert.ToInt32(hex[4..], 16) };
                break;
            case "temp":
                if (value is not { } t || double.IsNaN(t)) return null;
                service = "set_temperature";
                data["temperature"] = Math.Round(Math.Clamp(t, 4, 35) * 2, MidpointRounding.AwayFromZero) / 2;
                break;
            case "open": service = "open_cover"; break;
            case "close": service = "close_cover"; break;
            case "stop": service = "stop_cover"; break;
            case "position":
                if (value is not { } p || double.IsNaN(p)) return null;
                service = "set_cover_position";
                data["position"] = (int)Math.Round(Math.Clamp(p, 0, 100));
                break;
            case "activate": service = "turn_on"; break;
            case "lock": service = "lock"; break;
            case "unlock": service = "unlock"; break;
            default: return null;
        }
        return (domain, service, JsonSerializer.Serialize(data));
    }

    // ---------- jeux de données Home Assistant (sources de données, §139) ----------

    /// <summary>Lecture d'un jeu Home Assistant : « states:ids » (états actuels ; * = toutes les entités) ou « history:ids:heures ».</summary>
    public sealed record DataPath(string Mode, List<string> Ids, bool All, int Hours);

    public static readonly int[] HistoryHours = { 6, 24, 48, 168, 720 };

    public static DataPath ParseDataPath(string? path)
    {
        var p = (path ?? "").Trim();
        if (p.StartsWith("history:", StringComparison.Ordinal))
        {
            var rest = p[8..];
            var cut = rest.LastIndexOf(':');
            var hours = cut > 0 && int.TryParse(rest[(cut + 1)..], out var h) ? h : 24;
            var ids = ParseEntities(cut > 0 ? rest[..cut] : rest).Where(IsSupported).Take(10).ToList();
            return new("history", ids, false, Math.Clamp(hours, 1, 720));
        }
        var list = p.StartsWith("states:", StringComparison.Ordinal) ? p[7..] : p;
        if (list.Trim() is "*" or "") return new("states", new(), true, 0);
        return new("states", ParseEntities(list).Where(IsSupported).ToList(), false, 0);
    }

    public static string DataPathOf(string mode, IEnumerable<string> ids, int hours = 24) =>
        mode == "history" ? $"history:{string.Join(',', ids)}:{Math.Clamp(hours, 1, 720)}" : ids.Any() ? "states:" + string.Join(',', ids) : "states:*";

    /// <summary>Table des états : une ligne par entité.</summary>
    public static RawTable StatesTable(IEnumerable<HaState> states) => new(
        new() { "entite", "nom", "type", "valeur", "nombre", "unite", "allume", "mis_a_jour" },
        states.Select(s => new List<string?>
        {
            s.Id, s.Name, Domain(s.Id), s.State,
            double.TryParse(s.State, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n.ToString(CultureInfo.InvariantCulture) : null,
            s.Unit ?? "", IsOn(s) ? "true" : "false", s.Changed?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        }).ToList());

    /// <summary>Historique ramené à 240 points au plus par entité (moyenne par intervalle) : heure, entité, nom, valeur.</summary>
    public static RawTable HistoryTable(IReadOnlyDictionary<string, List<(DateTime At, double Value)>> series, IReadOnlyDictionary<string, string> names, DateTime from, DateTime to)
    {
        var rows = new List<List<string?>>();
        var span = Math.Max(60, (to - from).TotalSeconds / 240);
        foreach (var (id, points) in series)
        {
            foreach (var g in points.Where(p => p.At >= from && p.At <= to).GroupBy(p => (long)((p.At - from).TotalSeconds / span)).OrderBy(g => g.Key))
            {
                var at = from.AddSeconds(g.Key * span);
                rows.Add(new() { at.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), id, names.GetValueOrDefault(id, id),
                    Math.Round(g.Average(p => p.Value), 3).ToString(CultureInfo.InvariantCulture) });
            }
        }
        return new(new() { "heure", "entite", "nom", "valeur" }, rows);
    }

    /// <summary>Allumé, ouvert, déverrouillé, en chauffe… selon le domaine.</summary>
    public static bool IsOn(HaState s) => Domain(s.Id) switch
    {
        Cover => s.State is "open" or "opening",
        Lock => s.State is "unlocked" or "unlocking" or "open",
        Climate => s.State is not ("off" or "unavailable" or "unknown" or ""),
        _ => s.State == "on",
    };
}

/// <summary>Commande depuis l'écran tactile. Action : on, off, brightness (Value 0-100), color (Color #RRGGBB), temp (Value °), open, close, stop, position (Value 0-100), activate, lock, unlock.</summary>
public record HaCommandDto(string? EntityId, bool? On = null, string? Action = null, double? Value = null, string? Color = null);

/// <summary>Une entité telle que Home Assistant la décrit (GET /api/states, ou événement de la connexion temps réel). Attrs : attributs utiles seulement.</summary>
public sealed record HaState(string Id, string State, string Name, string? Unit, string? DeviceClass, IReadOnlyDictionary<string, JsonElement> Attrs, DateTime? Changed)
{
    private static readonly string[] Kept =
    {
        "brightness", "rgb_color", "supported_color_modes", "color_mode", "current_temperature", "temperature", "min_temp", "max_temp", "target_temp_step",
        "hvac_action", "current_position", "supported_features", "percentage",
    };

    public static HaState? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("entity_id", out var idEl) || idEl.GetString() is not { Length: > 0 } id) return null;
        var attrs = e.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        string? Text(string name) => attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var kept = new Dictionary<string, JsonElement>();
        if (attrs.ValueKind == JsonValueKind.Object)
            foreach (var k in Kept)
                if (attrs.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null) kept[k] = v.Clone();
        DateTime? changed = e.TryGetProperty("last_changed", out var lc) && lc.ValueKind == JsonValueKind.String && DateTime.TryParse(lc.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        var state = e.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() ?? "" : "";
        return new HaState(id, state, Text("friendly_name") ?? id, Text("unit_of_measurement"), Text("device_class"), kept, changed);
    }

    public double? Num(string attr) => Attrs.TryGetValue(attr, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
    public bool Has(string attr) => Attrs.ContainsKey(attr);
}

/// <summary>Paramètres de connexion en clair, le temps d'un appel : jamais stockés ni renvoyés. Refresh : jeton de renouvellement connu (connexion par mot de passe).</summary>
public sealed record HaConn(Guid ClientId, string Url, string Via, Guid? RelayId, string Mode, string User, string Secret, string Refresh = "");

/// <summary>Transport des appels : en direct (SafeHttp, adresses publiques seulement) ou par un relais Linkii du réseau local. Remplacé dans les tests.</summary>
public interface IHaTransport
{
    Task<(int Status, string Body)> Send(HaConn c, HttpMethod method, string url, string? body, string? contentType, Dictionary<string, string> headers, int maxBytes, CancellationToken ct);
}

public class HaTransport(SafeHttp http, RelayBroker relays) : IHaTransport
{
    public async Task<(int Status, string Body)> Send(HaConn c, HttpMethod method, string url, string? body, string? contentType, Dictionary<string, string> headers, int maxBytes, CancellationToken ct)
    {
        if (c.Via != "relay") return await http.Request(method, url, body, contentType, headers, maxBytes, ct);
        try
        {
            var r = await relays.Run(c.ClientId, c.RelayId ?? Guid.Empty, new RelayJob
            {
                Kind = "ha", Op = method == HttpMethod.Post ? "post" : "read", Method = method.Method, Url = url, Headers = headers, Body = body, ContentType = contentType, MaxBytes = maxBytes,
            }, ct);
            return (r.Status, r.Body64 == null ? "" : Encoding.UTF8.GetString(Convert.FromBase64String(r.Body64)));
        }
        catch (InvalidOperationException e) when (e.Message.Contains("Tâche inconnue"))
        {
            throw new InvalidOperationException("Ce relais Linkii est trop ancien pour Home Assistant : installez sa dernière version sur la machine du réseau.");
        }
    }
}

/// <summary>
/// Appels à Home Assistant pour le compte de l'organisation : connexion et test, liste des entités, états, commandes, historique.
/// Connexion par jeton d'accès longue durée, ou par identifiant et mot de passe : une seule connexion complète (flux de connexion de Home Assistant),
/// puis le jeton de renouvellement rendu par Home Assistant (gardé chiffré dans le compte) fournit un nouveau jeton d'accès toutes les 30 minutes,
/// sans créer de nouvelle session dans le profil Home Assistant. Les états lus par l'API sont gardés quelques secondes.
/// </summary>
public class HomeAssistantClient(IHaTransport net, SecretBox box, JsonStore store)
{
    private static readonly ConcurrentDictionary<string, (string Token, DateTime Expires)> Sessions = new();
    private static readonly ConcurrentDictionary<string, string> Refreshes = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Logins = new();
    private static readonly ConcurrentDictionary<string, (DateTime At, Dictionary<string, HaState> States)> StatesCache = new();
    private static readonly TimeSpan StatesTtl = TimeSpan.FromSeconds(4);

    /// <summary>Paramètres de connexion d'un compte enregistré, secrets déchiffrés pour l'appel.</summary>
    public HaConn Conn(Guid clientId, HomeAssistantAccount a) =>
        new(clientId, a.Url, a.Via == "relay" ? "relay" : "direct", a.Via == "relay" ? a.RelayId : null, a.AuthMode, a.User, Plain(a.Secret), Plain(a.Refresh));

    private string Plain(string stored) => stored.Length > 0 ? box.Unprotect(stored) : "";

    /// <summary>
    /// Connexion : vérifie l'accès, puis rend le compte à enregistrer (secrets chiffrés) avec la liste de ses entités ; lève une exception si la connexion échoue
    /// (rien n'est enregistré). Mêmes identifiants qu'avant : la session existante est gardée ; sinon l'ancienne est fermée dans Home Assistant.
    /// </summary>
    public async Task<HomeAssistantAccount> Connect(Guid clientId, string url, string via, Guid? relayId, string mode, string user, string secret, HomeAssistantAccount? previous, CancellationToken ct = default)
    {
        via = via == "relay" ? "relay" : "direct";
        mode = mode == "password" ? "password" : "token";
        var keepSession = previous != null && previous.AuthMode == mode && previous.User == (mode == "password" ? user.Trim() : "") && HomeAssistantRules.BaseUrl(previous.Url) == HomeAssistantRules.BaseUrl(url)
            && Plain(previous.Secret) == secret;
        var conn = new HaConn(clientId, HomeAssistantRules.BaseUrl(url), via, via == "relay" ? relayId : null, mode, mode == "password" ? user.Trim() : "", secret,
            keepSession ? Plain(previous!.Refresh) : "");
        var states = await States(conn, fresh: true, ct);
        if (previous != null && !keepSession && previous.Refresh.Length > 0) await Revoke(Conn(clientId, previous));   // anciens identifiants : leur session est fermée
        var refresh = conn.Mode == "password" ? Refreshes.GetValueOrDefault(Key(conn)) ?? conn.Refresh : "";
        var account = new HomeAssistantAccount
        {
            Url = conn.Url, Via = conn.Via, RelayId = conn.RelayId, AuthMode = conn.Mode, User = conn.User,
            Secret = box.Protect(secret), Refresh = refresh.Length > 0 ? box.Protect(refresh) : "",
            ConnectedUtc = previous?.ConnectedUtc ?? DateTime.UtcNow, LastOkUtc = DateTime.UtcNow,
        };
        Fill(account, states);
        return account;
    }

    /// <summary>Recharge la liste des entités du compte (après un ajout dans Home Assistant).</summary>
    public async Task<HomeAssistantAccount> Refresh(Guid clientId, HomeAssistantAccount a, CancellationToken ct = default)
    {
        var copy = a.Copy();
        Fill(copy, await States(Conn(clientId, a), fresh: true, ct));
        return copy;
    }

    /// <summary>Ferme la session Linkivi dans Home Assistant (déconnexion du compte). Sans effet pour un jeton d'accès longue durée, qui se révoque dans Home Assistant.</summary>
    public async Task Revoke(HaConn c)
    {
        var refresh = Refreshes.GetValueOrDefault(Key(c)) ?? c.Refresh;
        Sessions.TryRemove(Key(c), out _);
        Refreshes.TryRemove(Key(c), out _);
        if (c.Mode != "password" || refresh.Length == 0) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var (status, _) = await net.Send(c, HttpMethod.Post, c.Url + "/auth/revoke", "token=" + Uri.EscapeDataString(refresh), "application/x-www-form-urlencoded", new(), 100_000, cts.Token);
            if (status == 404)   // anciennes versions de Home Assistant
                await net.Send(c, HttpMethod.Post, c.Url + "/auth/token", "action=revoke&token=" + Uri.EscapeDataString(refresh), "application/x-www-form-urlencoded", new(), 100_000, cts.Token);
        }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException) { }   // au mieux : l'instance peut être injoignable
    }

    /// <summary>Déconnexion du compte de l'organisation : session fermée dans Home Assistant (au mieux), puis compte retiré. Widgets et jeux de données restent en place.</summary>
    public async Task Disconnect(Guid clientId, TenantStore tenant)
    {
        if (tenant.Read(d => d.Tenant.HomeAssistant?.Copy()) is { } a) await Revoke(Conn(clientId, a));
        tenant.Write(d => { d.Tenant.HomeAssistant = null; });
    }

    /// <summary>Exécute une commande (interrupteur, lumière, thermostat, volet, scène, serrure) ; les états gardés sont oubliés pour relire le nouvel état.</summary>
    public async Task Command(HaConn conn, string entityId, string action, double? value = null, string? color = null, CancellationToken ct = default)
    {
        if (HomeAssistantRules.ServiceCall(entityId, action, value, color) is not { } call) throw new InvalidOperationException("Commande non prise en charge pour cette entité.");
        var (status, _) = await Call(conn, HttpMethod.Post, $"/api/services/{call.Domain}/{call.Service}", call.Body, "application/json", 200_000, ct);
        if (status is < 200 or >= 300) throw new InvalidOperationException($"Home Assistant a répondu {status}.");
        Invalidate(conn);
    }

    public void Invalidate(HaConn conn) => StatesCache.TryRemove(Key(conn), out _);

    /// <summary>États de toutes les entités. fresh : ignore le cache court (connexion, actualisation de la liste).</summary>
    public async Task<Dictionary<string, HaState>> States(HaConn conn, bool fresh = false, CancellationToken ct = default)
    {
        var key = Key(conn);
        if (!fresh && StatesCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < StatesTtl) return hit.States;
        var (status, body) = await Call(conn, HttpMethod.Get, "/api/states", null, null, 8_000_000, ct);
        if (status != 200) throw new InvalidOperationException($"Home Assistant a répondu {status}.");
        var map = ParseStates(body);
        StatesCache[key] = (DateTime.UtcNow, map);
        return map;
    }

    public static Dictionary<string, HaState> ParseStates(string body)
    {
        var map = new Dictionary<string, HaState>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { throw new InvalidOperationException("Réponse inattendue de Home Assistant : vérifiez l'adresse."); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Réponse inattendue de Home Assistant : vérifiez l'adresse.");
            foreach (var e in doc.RootElement.EnumerateArray())
                if (HaState.Parse(e) is { } s) map[s.Id] = s;
        }
        return map;
    }

    /// <summary>Historique numérique des entités sur les dernières heures (valeurs non numériques ignorées ; allumé / éteint donne 1 / 0).</summary>
    public async Task<Dictionary<string, List<(DateTime At, double Value)>>> History(HaConn conn, IReadOnlyList<string> ids, DateTime from, DateTime to, CancellationToken ct = default)
    {
        var result = ids.ToDictionary(i => i, _ => new List<(DateTime, double)>());
        if (ids.Count == 0) return result;
        var path = "/api/history/period/" + Uri.EscapeDataString(from.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
            + "?filter_entity_id=" + Uri.EscapeDataString(string.Join(',', ids)) + "&end_time=" + Uri.EscapeDataString(to.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
            + "&minimal_response&no_attributes";
        var (status, body) = await Call(conn, HttpMethod.Get, path, null, null, 8_000_000, ct);
        if (status != 200) throw new InvalidOperationException($"Home Assistant a répondu {status} pour l'historique.");
        foreach (var (id, points) in ParseHistory(body)) if (result.ContainsKey(id)) result[id] = points;
        return result;
    }

    /// <summary>Réponse de /api/history/period (minimal_response : seul le premier point de chaque série porte entity_id).</summary>
    public static Dictionary<string, List<(DateTime At, double Value)>> ParseHistory(string body)
    {
        var map = new Dictionary<string, List<(DateTime, double)>>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;
        foreach (var series in doc.RootElement.EnumerateArray())
        {
            if (series.ValueKind != JsonValueKind.Array) continue;
            string? id = null;
            var points = new List<(DateTime, double)>();
            foreach (var p in series.EnumerateArray())
            {
                if (id == null && p.TryGetProperty("entity_id", out var ide)) id = ide.GetString();
                var st = p.TryGetProperty("state", out var s) ? s.GetString() : null;
                var at = (p.TryGetProperty("last_changed", out var lc) ? lc : p.TryGetProperty("last_updated", out var lu) ? lu : default).GetString();
                if (st == null || at == null || !DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)) continue;
                double? v = double.TryParse(st, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : st switch { "on" or "open" or "unlocked" => 1, "off" or "closed" or "locked" => 0, _ => null };
                if (v is { } val) points.Add((when, val));
            }
            if (id != null) map[id] = points;
        }
        return map;
    }

    /// <summary>Entités prises en charge, par nom.</summary>
    private static void Fill(HomeAssistantAccount a, Dictionary<string, HaState> states)
    {
        a.Entities = states.Values
            .Where(s => HomeAssistantRules.IsSupported(s.Id))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new HomeAssistantEntity { Id = s.Id, Name = s.Name, Domain = HomeAssistantRules.Domain(s.Id), Unit = s.Unit ?? "", DeviceClass = s.DeviceClass ?? "" })
            .ToList();
        a.EntitiesUtc = DateTime.UtcNow;
        a.LastError = "";
        a.FailingSinceUtc = null;
        a.LastOkUtc = DateTime.UtcNow;
        var known = a.Entities.Select(e => e.Id).ToHashSet();
        a.Missing = a.Missing.Where(id => !known.Contains(id)).ToList();
    }

    private static string Key(HaConn c) => c.Url + "|" + c.Via + "|" + c.RelayId + "|" + c.Mode + "|" + c.User + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.Secret)));

    /// <summary>Appel authentifié à l'instance. Jeton d'accès refusé (401) en connexion par mot de passe : un nouveau jeton, puis un seul nouvel essai.</summary>
    private async Task<(int Status, string Body)> Call(HaConn c, HttpMethod method, string path, string? body, string? contentType, int max, CancellationToken ct, bool retry = true)
    {
        if (c.Url == "") throw new InvalidOperationException("Adresse de Home Assistant manquante.");
        var token = await AccessToken(c, ct);
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token, ["Accept"] = "application/json" };
        var res = await net.Send(c, method, c.Url + path, body, contentType, headers, max, ct);
        if (res.Status == 401 && retry && c.Mode == "password")
        {
            Sessions.TryRemove(Key(c), out _);
            return await Call(c, method, path, body, contentType, max, ct, retry: false);
        }
        if (res.Status == 401) throw new InvalidOperationException(c.Mode == "password" ? "Connexion refusée par Home Assistant." : "Jeton refusé par Home Assistant.");
        return res;
    }

    /// <summary>Jeton d'accès de l'appel : le jeton longue durée, ou (mot de passe) celui de la session en cours, renouvelé par le jeton de renouvellement.</summary>
    public async Task<string> AccessToken(HaConn c, CancellationToken ct = default)
    {
        if (c.Mode != "password")
            return c.Secret.Length > 0 ? c.Secret : throw new InvalidOperationException("Jeton d'accès manquant.");
        var key = Key(c);
        if (Sessions.TryGetValue(key, out var s) && s.Expires > DateTime.UtcNow) return s.Token;
        var gate = Logins.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Sessions.TryGetValue(key, out s) && s.Expires > DateTime.UtcNow) return s.Token;   // obtenu pendant l'attente
            var refresh = Refreshes.GetValueOrDefault(key) ?? c.Refresh;
            if (refresh.Length > 0)
            {
                var (status, body) = await net.Send(c, HttpMethod.Post, c.Url + "/auth/token",
                    "grant_type=refresh_token&refresh_token=" + Uri.EscapeDataString(refresh) + "&client_id=" + Uri.EscapeDataString(HomeAssistantRules.ClientId),
                    "application/x-www-form-urlencoded", new(), 1_000_000, ct);
                if (status == 200)
                {
                    var tok = Json(body);
                    var access = tok.TryGetProperty("access_token", out var at) ? at.GetString() : null;
                    if (access is { Length: > 0 })
                    {
                        Sessions[key] = (access, DateTime.UtcNow.AddSeconds(Math.Max(60, Seconds(tok) - 60)));
                        Refreshes[key] = refresh;
                        return access;
                    }
                }
                else if (status is not (400 or 401 or 403)) throw new InvalidOperationException($"Home Assistant a répondu {status} pendant la connexion.");
                Refreshes.TryRemove(key, out _);   // session fermée dans Home Assistant : une nouvelle connexion complète
            }
            var (token, seconds, newRefresh) = await Login(c, ct);
            Sessions[key] = (token, DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 60)));
            if (newRefresh.Length > 0)
            {
                Refreshes[key] = newRefresh;
                Persist(c, newRefresh);
            }
            return token;
        }
        finally { gate.Release(); }
    }

    /// <summary>Nouveau jeton de renouvellement : gardé (chiffré) dans le compte de l'organisation s'il correspond toujours à ces identifiants.</summary>
    private void Persist(HaConn c, string refresh)
    {
        if (c.ClientId == Guid.Empty) return;
        var stored = box.Protect(refresh);
        store.Write(db =>
        {
            if (db.Clients.FirstOrDefault(t => t.Id == c.ClientId)?.HomeAssistant is { } a && HomeAssistantRules.BaseUrl(a.Url) == c.Url && a.AuthMode == "password" && a.User == c.User)
                a.Refresh = stored;
        });
    }

    private static int Seconds(JsonElement tok) => tok.TryGetProperty("expires_in", out var ex) && ex.TryGetInt32(out var n) ? n : 1800;

    /// <summary>Connexion comme l'écran de connexion de Home Assistant : étape d'identifiants, code d'autorisation, jetons d'accès et de renouvellement.</summary>
    private async Task<(string Token, int Seconds, string Refresh)> Login(HaConn c, CancellationToken ct)
    {
        var flow = await Post(c, "/auth/login_flow", JsonSerializer.Serialize(new
        {
            client_id = HomeAssistantRules.ClientId, handler = new object?[] { "homeassistant", null }, redirect_uri = HomeAssistantRules.ClientId
        }), "application/json", ct);
        var flowId = Json(flow).TryGetProperty("flow_id", out var fid) ? fid.GetString() : null;
        if (flowId == null) throw new InvalidOperationException("Connexion à Home Assistant impossible.");

        var step = await Post(c, $"/auth/login_flow/{flowId}", JsonSerializer.Serialize(new
        {
            client_id = HomeAssistantRules.ClientId, username = c.User, password = c.Secret
        }), "application/json", ct);
        var result = Json(step);
        if (!result.TryGetProperty("type", out var type) || type.GetString() != "create_entry")
            throw new InvalidOperationException(result.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Object && errs.EnumerateObject().Any()
                ? "Identifiant ou mot de passe refusé par Home Assistant."
                : "Home Assistant demande une étape supplémentaire (double authentification) : utilisez un jeton d'accès.");
        var code = result.TryGetProperty("result", out var r) ? r.GetString() : null;

        var tok = Json(await Post(c, "/auth/token",
            "grant_type=authorization_code&code=" + Uri.EscapeDataString(code ?? "") + "&client_id=" + Uri.EscapeDataString(HomeAssistantRules.ClientId),
            "application/x-www-form-urlencoded", ct));
        var access = tok.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrEmpty(access)) throw new InvalidOperationException("Jeton non reçu de Home Assistant.");
        var refresh = tok.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
        return (access, Seconds(tok), refresh);
    }

    private async Task<string> Post(HaConn c, string path, string body, string contentType, CancellationToken ct)
    {
        var (status, text) = await net.Send(c, HttpMethod.Post, c.Url + path, body, contentType, new(), 1_000_000, ct);
        if (status is < 200 or >= 300) throw new InvalidOperationException($"Home Assistant a répondu {status} pendant la connexion.");
        return text;
    }

    private static JsonElement Json(string body)
    {
        try { return JsonDocument.Parse(body).RootElement.Clone(); }
        catch (JsonException) { throw new InvalidOperationException("Réponse inattendue de Home Assistant : vérifiez l'adresse."); }
    }

    /// <summary>Oublie les sessions et les états gardés (tests).</summary>
    public static void ResetForTests() { Sessions.Clear(); Refreshes.Clear(); StatesCache.Clear(); }
}

/// <summary>
/// Fournisseur des widgets Home Assistant : lit le compte de l'organisation, et les seules entités choisies dans le widget. Les états viennent de la
/// connexion temps réel quand elle est ouverte (<see cref="HomeAssistantLive"/>), sinon de l'API (cache de 4 s).
/// </summary>
public class HomeAssistantProvider(HomeAssistantClient ha, HomeAssistantLive live) : IAppProvider
{
    public string Id => "homeassistant";

    public async Task<object?> Fetch(AppRuntime rt)
    {
        var account = rt.Client.HomeAssistant ?? throw new InvalidOperationException("Compte Home Assistant non connecté.");
        // plan du bâtiment : les entités sont celles posées sur le plan, et le plan est renvoyé nettoyé (l'écran ne lit jamais le réglage brut)
        var isPlan = rt.Values.ContainsKey("planId");
        var plan = isPlan ? HomeAssistantRules.PlanOf(rt.Client, rt.Values) ?? new HomeAssistantRules.Plan(new(), new()) : null;
        var configured = plan != null ? plan.Devices.Select(d => d.Id).ToList() : HomeAssistantRules.ParseEntities(rt.Get("entities"));
        var control = rt.Get("control", "true") != "false";
        var (states, isLive) = await live.States(rt.Client.Id, account);
        return new
        {
            title = rt.Get("title"), fetchedAt = DateTime.UtcNow, live = isLive,
            decimals = rt.Get("decimals", "auto"), dimmer = rt.Get("dimmer", "true") != "false", colors = rt.Get("colors") == "true",
            entities = configured.Select(id => Entity(id, states.GetValueOrDefault(id), control)).ToList(),
            plan = plan == null ? null : PlanView(plan, states, k => rt.Values.GetValueOrDefault(k)),
        };
    }

    /// <summary>
    /// Plan tel que l'écran le reçoit (aussi pour l'aperçu de l'éditeur, §196) : étages, pièces et murs calculés, appareils avec leur pièce et leur ouverture,
    /// options d'affichage lues par <paramref name="get"/> (réglages du widget).
    /// </summary>
    public static object PlanView(HomeAssistantRules.Plan plan, IReadOnlyDictionary<string, HaState> states, Func<string, string?> get)
    {
        string Opt(string k, string def) => get(k) is { Length: > 0 } v ? v : def;
        return new
            {
                // étage dessiné (§195) : pièces et murs déjà calculés (passages et ouvertures retirés) ; l'image n'y est qu'un calque de l'éditeur
                floors = plan.Floors.Select(f => f.Drawn
                    ? (object)new
                    {
                        id = f.Id, name = f.Name, img = (string?)null, ratio = f.Ratio, kind = f.Kind, w = f.W, h = f.H,
                        rooms = f.Rooms.Select(r => new { id = r.Id, name = r.Name, x = r.X, y = r.Y, w = r.W, h = r.H }),
                        walls = HomeAssistantRules.WallsOf(f, plan.Devices).Select(w => new { x = w.X, y = w.Y, d = w.D, len = w.Len }),
                    }
                    : new { id = f.Id, name = f.Name, img = f.Img, ratio = f.Ratio, kind = "image" }),
                devices = plan.Devices.Select(d =>
                {
                    var f = plan.Floors.First(x => x.Id == d.Floor);
                    var op = d.Seg == null ? null : PlanGeometry.OpeningKind(HomeAssistantRules.Domain(d.Id), states.GetValueOrDefault(d.Id)?.DeviceClass);
                    return new
                    {
                        id = d.Id, floor = d.Floor, x = d.X, y = d.Y, room = HomeAssistantRules.RoomOf(f, d)?.Id,
                        seg = d.Seg is { } s && op != null ? new { x = s.X, y = s.Y, d = s.D, len = s.Len, s = s.S, kind = op } : null,
                    };
                }),
                heat = Opt("heat", "false") == "true",
                spin = Opt("spin", "true") != "false", explode = Opt("explode", "true") != "false", focus = Opt("focus", "true") != "false", labels = Opt("labels", "all"),
            };
    }

    /// <summary>Ce que l'écran reçoit pour une entité : état, valeurs utiles à son affichage, commandes permises. Absente : state « absent », rien à commander.</summary>
    public static object Entity(string id, HaState? s, bool control)
    {
        var domain = HomeAssistantRules.Domain(id);
        if (s == null) return new { id, name = id, domain, state = "absent", available = false, ctrl = Array.Empty<string>() };
        var available = s.State is not ("unavailable" or "unknown");
        var modes = s.Attrs.TryGetValue("supported_color_modes", out var cm) && cm.ValueKind == JsonValueKind.Array ? cm.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
        var features = (int)(s.Num("supported_features") ?? 0);
        string? color = s.Attrs.TryGetValue("rgb_color", out var rgb) && rgb.ValueKind == JsonValueKind.Array && rgb.GetArrayLength() == 3
            ? "#" + string.Concat(rgb.EnumerateArray().Select(x => Math.Clamp(x.TryGetInt32(out var n) ? n : 0, 0, 255).ToString("X2"))) : null;
        return new
        {
            id, name = s.Name, domain, state = s.State, unit = s.Unit, deviceClass = s.DeviceClass, available, on = HomeAssistantRules.IsOn(s),
            changedAt = s.Changed,
            ctrl = available ? HomeAssistantRules.ControlsFor(id, control) : Array.Empty<string>(),
            brightness = s.Num("brightness") is { } b ? (int?)Math.Round(b / 255 * 100) : null,
            dimmable = domain == HomeAssistantRules.Light && modes.Any(m => m is not ("onoff" or "")),
            colorable = domain == HomeAssistantRules.Light && modes.Any(m => m is "hs" or "rgb" or "rgbw" or "rgbww" or "xy"),
            color,
            current = s.Num("current_temperature"), target = s.Num("temperature"), min = s.Num("min_temp"), max = s.Num("max_temp"), step = s.Num("target_temp_step"),
            hvac = s.Attrs.TryGetValue("hvac_action", out var hv) && hv.ValueKind == JsonValueKind.String ? hv.GetString() : null,
            position = s.Num("current_position") is { } p ? (int?)Math.Round(p) : null,
            positionable = domain == HomeAssistantRules.Cover && (features & 4) != 0,
            stoppable = domain == HomeAssistantRules.Cover && (features & 8) != 0,
        };
    }

    public async Task<string> Test(AppRuntime rt)
    {
        var account = rt.Client.HomeAssistant ?? throw new InvalidOperationException("Connectez d'abord le compte Home Assistant dans Intégrations.");
        var isPlan = rt.Values.ContainsKey("planId");
        var configured = isPlan ? HomeAssistantRules.PlanOf(rt.Client, rt.Values)?.Devices.Select(d => d.Id).ToList() ?? new() : HomeAssistantRules.ParseEntities(rt.Get("entities"));
        if (configured.Count == 0) throw new InvalidOperationException(isPlan ? "Choisissez un plan où des appareils sont posés." : "Choisissez au moins une entité.");
        var states = await ha.States(ha.Conn(rt.Client.Id, account), fresh: true);
        var missing = configured.Where(id => !states.ContainsKey(id)).ToList();
        var msg = $"{configured.Count - missing.Count} entité(s) disponible(s).";
        if (missing.Count > 0) msg += $" ⚠ Introuvable(s) : {string.Join(", ", missing)} (supprimée(s) ou renommée(s) dans Home Assistant).";
        return msg;
    }
}
