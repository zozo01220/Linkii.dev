using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

// =====================================================================================
//  Apps « Menu restaurant » : deux sources (fichier / lien CSV, API JSON), un seul rendu (le moteur « menu » du player).
//  Les adresses, clés et jetons restent côté serveur ; l'écran ne reçoit que les plats des prochains jours.
// =====================================================================================
public static class MenuApp
{
    /// <summary>Fenêtre envoyée aux écrans : aujourd'hui (fuseau du client) et les 7 jours suivants, au format aaaa-mm-jj.</summary>
    public static (string From, string To) Window(AppRuntime rt, DateTime? nowUtc = null)
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(rt.Client.Timezone); } catch { tz = TimeZoneInfo.Utc; }
        var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc ?? DateTime.UtcNow, tz).Date;
        return (today.ToString("yyyy-MM-dd"), today.AddDays(8).ToString("yyyy-MM-dd"));
    }

    private static IEnumerable<MenuRow> Upcoming(AppRuntime rt, IEnumerable<MenuRow> rows, DateTime? nowUtc)
    {
        var (from, to) = Window(rt, nowUtc);
        return rows.Where(r => string.CompareOrdinal(r.Date, from) >= 0 && string.CompareOrdinal(r.Date, to) < 0).OrderBy(r => r.Date, StringComparer.Ordinal);
    }

    /// <summary>Données envoyées au player : même forme que l'ancien widget « menu » (kind = menu).</summary>
    public static object Payload(AppRuntime rt, IEnumerable<MenuRow> rows, DateTime? nowUtc = null)
    {
        var showPrice = rt.Get("showPrice", "true") != "false";
        var showAllergens = rt.Get("showAllergens", "true") != "false";
        return new
        {
            kind = "menu", title = rt.Get("title"), fetchedAt = DateTime.UtcNow,
            rows = Upcoming(rt, rows, nowUtc).Select(r => new
            {
                date = r.Date, category = r.Category, name = r.Name,
                price = showPrice ? r.Price : "", allergens = showAllergens ? r.Allergens : ""
            }).ToList()
        };
    }

    /// <summary>Résultat du test : ce qui a été lu, avec un avertissement (« ⚠ ») si l'écran n'aurait rien à afficher ou si des lignes sont ignorées.</summary>
    public static string Summary(AppRuntime rt, List<MenuRow> rows, List<string> errors, DateTime? nowUtc = null)
    {
        var upcoming = Upcoming(rt, rows, nowUtc).ToList();
        var days = upcoming.Select(r => r.Date).Distinct().ToList();
        string D(string iso) => DateTime.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd.MM.yyyy");
        var msg = $"Menu lu : {rows.Count} plat(s), dont {upcoming.Count} à venir sur {days.Count} jour(s)" + (days.Count > 0 ? $" (du {D(days[0])} au {D(days[^1])})" : "") + ".";
        var warn = new List<string>();
        if (upcoming.Count == 0) warn.Add("Aucun plat à partir d'aujourd'hui : l'écran affichera « Menu non renseigné ».");
        if (errors.Count > 0) warn.Add($"{errors.Count} ligne(s) ignorée(s). {errors[0]}");
        return warn.Count == 0 ? msg : "⚠ " + msg + " " + string.Join(" ", warn);
    }

    public static string Cut(string? s, int max = 200) { s = (s ?? "").Trim(); return s.Length > max ? s[..max] : s; }
}

// ---------------------------------------------------------------------------------------------
/// <summary>Menu restaurant CSV : fichier importé dans l'app, ou lien web d'un CSV publié (Google Sheets, intranet…).</summary>
public class MenuCsvProvider(SafeHttp http) : IAppProvider
{
    public string Id => "menu-csv";
    private readonly ConcurrentDictionary<string, (DateTime At, string Text)> _cache = new();

    private async Task<string> Text(AppRuntime rt)
    {
        if (rt.Get("source", "file") != "url") return rt.Get("csv");
        var url = rt.Get("csvUrl");
        if (_cache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(5)) return hit.Text;
        try
        {
            var text = await http.GetString(url);
            _cache[url] = (DateTime.UtcNow, text);
            return text;
        }
        catch when (hit.Text != null) { return hit.Text; }   // dernière version connue si la source est indisponible
    }

    private async Task<MenuCsv.Result> Read(AppRuntime rt)
    {
        var text = await Text(rt);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException(rt.Get("source", "file") == "url" ? "Le lien ne renvoie aucun contenu." : "Aucun fichier CSV importé.");
        var res = MenuCsv.Parse(text);
        if (res.Rows.Count == 0) throw new InvalidOperationException(res.Errors.FirstOrDefault() ?? "Aucun plat reconnu dans le fichier.");
        return res;
    }

    public async Task<object?> Fetch(AppRuntime rt) => MenuApp.Payload(rt, (await Read(rt)).Rows);

    public async Task<string> Test(AppRuntime rt)
    {
        var res = await Read(rt);
        return MenuApp.Summary(rt, res.Rows, res.Errors);
    }
}

// ---------------------------------------------------------------------------------------------
/// <summary>Menu restaurant API : une API JSON (liste de plats, ou jours contenant des plats), avec clé ou jeton facultatif.</summary>
public class MenuApiProvider(SafeHttp http) : IAppProvider
{
    public string Id => "menu-api";
    private readonly ConcurrentDictionary<string, (DateTime At, MenuCsv.Result Result)> _cache = new();

    private static readonly Regex HeaderName = new("^[A-Za-z0-9-]{1,40}$");

    private static Dictionary<string, string> Headers(AppRuntime rt)
    {
        var h = new Dictionary<string, string> { ["Accept"] = "application/json" };
        var token = rt.Get("token");
        switch (rt.Get("auth", "none"))
        {
            case "bearer": h["Authorization"] = "Bearer " + token; break;
            case "header":
                var name = rt.Get("headerName", "X-API-Key");
                if (!HeaderName.IsMatch(name)) throw new InvalidOperationException("Nom d'en-tête invalide (lettres, chiffres et tirets).");
                h[name] = token;
                break;
        }
        foreach (var v in h.Values) if (v.Contains('\r') || v.Contains('\n')) throw new InvalidOperationException("Clé ou jeton invalide.");
        return h;
    }

    private static MenuMapping? Mapping(AppRuntime rt) => rt.Get("mapping", "auto") == "custom"
        ? new MenuMapping(rt.Get("fDate"), rt.Get("fCategory"), rt.Get("fName"), rt.Get("fPrice"), rt.Get("fAllergens"))
        : null;

    private async Task<MenuCsv.Result> Load(AppRuntime rt)
    {
        var key = string.Join("|", rt.Get("url"), rt.Get("auth"), rt.Get("headerName"), rt.Get("token"), rt.Get("listPath"), rt.Get("mapping"), rt.Get("fDate"), rt.Get("fCategory"), rt.Get("fName"), rt.Get("fPrice"), rt.Get("fAllergens"));
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(5)) return hit.Result;
        try
        {
            var json = await http.GetString(rt.Get("url"), headers: Headers(rt));
            var res = MenuJson.Parse(json, rt.Get("listPath"), Mapping(rt));
            if (res.Rows.Count == 0) throw new InvalidOperationException(res.Errors.FirstOrDefault() ?? "La liste de plats est vide.");
            _cache[key] = (DateTime.UtcNow, res);
            return res;
        }
        catch when (hit.Result != null) { return hit.Result; }   // dernière version connue si l'API est indisponible
    }

    public async Task<object?> Fetch(AppRuntime rt) => MenuApp.Payload(rt, (await Load(rt)).Rows);

    public async Task<string> Test(AppRuntime rt)
    {
        var res = await Load(rt);
        return MenuApp.Summary(rt, res.Rows, res.Errors).Replace("Menu lu", "API lue");
    }
}

// ---------------------------------------------------------------------------------------------
/// <summary>Noms des champs de l'API quand ils ne sont pas reconnus automatiquement (vide : détection automatique).</summary>
public record MenuMapping(string Date, string Category, string Name, string Price, string Allergens);

/// <summary>
/// Lit la réponse JSON d'une API de menus. Formes acceptées : une liste de plats ; un objet contenant cette liste
/// (rows, menu, items, data…, ou « Chemin de la liste ») ; une liste de jours qui contiennent chacun leurs plats.
/// </summary>
public static class MenuJson
{
    private static readonly string[] ListNames = { "rows", "menu", "menus", "items", "data", "plats", "dishes", "meals", "results", "repas", "days", "jours" };
    private static readonly string[] NestedNames = { "items", "plats", "dishes", "meals", "repas", "menu", "courses", "lines", "plat" };
    private static readonly string[] DateKeys = { "date", "jour", "day", "datum" };
    private static readonly string[] CategoryKeys = { "categorie", "category", "type", "service", "course", "section" };
    private static readonly string[] NameKeys = { "plat", "nom", "name", "label", "libelle", "designation", "title", "dish", "description", "menu" };
    private static readonly string[] PriceKeys = { "prix", "price", "tarif", "cost" };
    private static readonly string[] AllergenKeys = { "allergenes", "allergens", "allergene", "allergen" };
    private const int MaxRows = 2000;

    public static MenuCsv.Result Parse(string json, string? listPath = null, MenuMapping? map = null)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new InvalidOperationException("La réponse n'est pas du JSON valide."); }

        using (doc)
        {
            var list = FindList(doc.RootElement, listPath)
                ?? throw new InvalidOperationException("Aucune liste de plats dans la réponse : renseignez « Chemin de la liste » (ex. : data.menus).");
            var rows = new List<MenuRow>();
            var errors = new List<string>();
            var i = 0;
            foreach (var e in list.EnumerateArray())
            {
                i++;
                if (rows.Count >= MaxRows) break;
                if (e.ValueKind != JsonValueKind.Object) { errors.Add($"Élément {i} ignoré : un objet était attendu."); continue; }
                Collect(e, i, null, null, map, rows, errors, 0);
            }
            return new(rows, errors);
        }
    }

    private static JsonElement? FindList(JsonElement root, string? path)
    {
        var el = root;
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var seg in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (el.ValueKind != JsonValueKind.Object) return null;
                var next = el.EnumerateObject().Where(p => string.Equals(p.Name, seg, StringComparison.OrdinalIgnoreCase)).Select(p => (JsonElement?)p.Value).FirstOrDefault();
                if (next == null) return null;
                el = next.Value;
            }
            return el.ValueKind == JsonValueKind.Array ? el : null;
        }
        if (el.ValueKind == JsonValueKind.Array) return el;
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in ListNames)
            foreach (var p in el.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Array && MenuCsv.Norm(p.Name) == n) return p.Value;
        foreach (var p in el.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Array) return p.Value;
        return null;
    }

    private static void Collect(JsonElement e, int index, string? parentDate, string? parentCategory, MenuMapping? map, List<MenuRow> rows, List<string> errors, int depth)
    {
        var date = Get(e, DateKeys, map?.Date) ?? parentDate;
        var category = Get(e, CategoryKeys, map?.Category) ?? parentCategory;
        var name = Get(e, NameKeys, map?.Name);

        if (name == null)   // pas de plat ici : un jour qui contient ses plats ?
        {
            var nested = depth < 3 ? e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array && NestedNames.Contains(MenuCsv.Norm(p.Name))).Select(p => (JsonElement?)p.Value).FirstOrDefault() : null;
            if (nested != null)
            {
                foreach (var child in nested.Value.EnumerateArray())
                    if (child.ValueKind == JsonValueKind.Object && rows.Count < MaxRows) Collect(child, index, date, category, map, rows, errors, depth + 1);
                return;
            }
            errors.Add($"Élément {index} ignoré : nom du plat introuvable.");
            return;
        }
        if (date == null) { errors.Add($"Élément {index} ignoré : date manquante."); return; }
        if (!MenuCsv.TryDate(date, out var d)) { errors.Add($"Élément {index} ignoré : date illisible « {MenuApp.Cut(date, 30)} »."); return; }
        rows.Add(new MenuRow
        {
            Date = d.ToString("yyyy-MM-dd"), Category = MenuApp.Cut(category, 60), Name = MenuApp.Cut(name),
            Price = MenuApp.Cut(Get(e, PriceKeys, map?.Price, price: true), 30), Allergens = MenuApp.Cut(Get(e, AllergenKeys, map?.Allergens))
        });
    }

    /// <summary>Valeur d'un champ : nom fixé par l'administrateur, sinon premier nom reconnu (casse et accents ignorés).</summary>
    private static string? Get(JsonElement e, string[] keys, string? custom, bool price = false)
    {
        JsonElement? found = null;
        if (!string.IsNullOrWhiteSpace(custom))
            found = e.EnumerateObject().Where(p => string.Equals(p.Name, custom.Trim(), StringComparison.OrdinalIgnoreCase)).Select(p => (JsonElement?)p.Value).FirstOrDefault();
        else
        {
            var arrayOk = ReferenceEquals(keys, AllergenKeys);   // seuls les allergènes peuvent être une liste
            foreach (var k in keys)
            {
                found = e.EnumerateObject().Where(p => MenuCsv.Norm(p.Name) == k && (arrayOk || p.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)))
                    .Select(p => (JsonElement?)p.Value).FirstOrDefault();
                if (found != null) break;
            }
        }
        return found == null ? null : Text(found.Value, price);
    }

    private static string? Text(JsonElement v, bool price)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.String: var s = v.GetString()?.Trim(); return string.IsNullOrEmpty(s) ? null : s;
            case JsonValueKind.Number:
                return price && v.TryGetDouble(out var d) && d != Math.Floor(d) ? d.ToString("0.00", CultureInfo.InvariantCulture) : v.GetRawText();
            case JsonValueKind.Array:   // allergènes : ["gluten", "lait"]
                var parts = v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()?.Trim() : null).Where(x => !string.IsNullOrEmpty(x)).ToList();
                return parts.Count == 0 ? null : string.Join(", ", parts);
            default: return null;
        }
    }
}
