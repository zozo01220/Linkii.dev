using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>Un paramètre d'une app : le formulaire de paramétrage est généré à partir de cette description.</summary>
public class AppField
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>text | textarea | number | select | bool | color | url | secret | date | mailbox | youtube | textfile (fichier texte importé) | hidden (valeur gérée par un composant, jamais affichée) | linkedin (panneau de connexion LinkedIn) | media (un fichier de la médiathèque) | medias (plusieurs, dans l'ordre)</summary>
    public string Type { get; set; } = "text";
    public string? Help { get; set; }
    public string? Placeholder { get; set; }
    public bool Required { get; set; }
    public string? Default { get; set; }
    public List<AppOption> Options { get; set; } = new();
    /// <summary>Champ affiché (et enregistré) seulement si la condition est vraie : « clé=valeur » ou « clé!=valeur ».</summary>
    public string? ShowIf { get; set; }
    public int? Min { get; set; }
    public int? Max { get; set; }
    public int MaxLength { get; set; } = 2000;
    /// <summary>textfile : adresse (du site) d'un fichier modèle à télécharger.</summary>
    public string? Template { get; set; }
    /// <summary>media / medias : type de fichier accepté (image ou video). Valeur : identifiants des médias, séparés par des virgules.</summary>
    public string? Accept { get; set; }

    public bool IsMedia => Type is "media" or "medias";

    /// <summary>Identifiants des médias choisis (champ media / medias), dans l'ordre.</summary>
    public static List<Guid> MediaIds(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();

    /// <summary>Chiffré, conservé sur le serveur, jamais envoyé au player ni renvoyé au formulaire (secret, ou fichier importé).</summary>
    public bool IsSecret => Type is "secret" or "textfile";
}

public record AppOption(string Value, string Label);

/// <summary>Description déclarative d'une app du catalogue (un fichier JSON dans Apps/).</summary>
public class AppManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string Category { get; set; } = "";
    public string Icon { get; set; } = "🧩";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>fixed = durée choisie dans la playlist ; content = l'app décide (fin de la vidéo), plafonnée par le paramètre maxMinutes.</summary>
    public string Duration { get; set; } = "fixed";
    /// <summary>builtin = écrite par Linkii ; nocode = configurée uniquement par son manifeste.</summary>
    public string Kind { get; set; } = "builtin";
    public List<string> Placements { get; set; } = new();
    /// <summary>Les paramètres validFrom / validTo (aaaa-mm-jj) limitent la période d'affichage.</summary>
    public bool Validity { get; set; }
    /// <summary>Fournisseur côté serveur (secrets, appels externes, test). Vide : l'app n'a pas besoin du serveur.</summary>
    public string? Provider { get; set; }
    /// <summary>Le player demande des données au serveur (/api/data) pour cette app.</summary>
    public bool Data { get; set; }
    public int RefreshSec { get; set; } = 60;
    /// <summary>Widget d'écran (horloge, météo) : posé librement dans l'édition de l'écran, il n'est pas proposé dans les playlists ni dans Réglages › Applications.</summary>
    public bool ScreenWidget { get; set; }
    /// <summary>Activée d'office pour chaque client (une seule fois : le client peut ensuite la désactiver).</summary>
    public bool DefaultOn { get; set; }
    public List<AppField> Settings { get; set; } = new();

    public bool IsNoCode => Kind == "nocode";
}

/// <summary>Le catalogue (« store ») : les manifestes chargés au démarrage depuis le dossier Apps/.</summary>
public class AppCatalog
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] AllPlacements = { "full", "top-left", "top-right", "bottom-left", "bottom-right", "top", "bottom" };
    private static readonly string[] FieldTypes = { "text", "textarea", "number", "select", "bool", "color", "url", "secret", "date", "mailbox", "youtube", "textfile", "hidden", "linkedin", "media", "medias" };
    private readonly List<AppManifest> _apps = new();

    public IReadOnlyList<AppManifest> All => _apps;
    public AppManifest? Find(string? id) => _apps.FirstOrDefault(a => a.Id == id);

    public AppCatalog(IWebHostEnvironment env, ILogger<AppCatalog> log) : this(Path.Combine(env.ContentRootPath, "Apps"), log) { }

    public AppCatalog(string dir, ILogger log)
    {
        if (!Directory.Exists(dir)) { log.LogWarning("Dossier du catalogue introuvable : {Dir}", dir); return; }
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
        {
            try
            {
                var m = JsonSerializer.Deserialize<AppManifest>(File.ReadAllText(file), Json) ?? throw new InvalidDataException("vide");
                var problems = Check(m);
                if (_apps.Any(a => a.Id == m.Id)) problems.Add("identifiant déjà utilisé");
                if (problems.Count > 0) { log.LogError("App « {File} » ignorée : {Problems}", Path.GetFileName(file), string.Join(" ; ", problems)); continue; }
                _apps.Add(m);
            }
            catch (Exception ex) { log.LogError("App « {File} » illisible : {Error}", Path.GetFileName(file), ex.Message); }
        }
    }

    /// <summary>Contrôle d'un manifeste : un manifeste invalide n'entre pas dans le catalogue.</summary>
    public static List<string> Check(AppManifest m)
    {
        var p = new List<string>();
        if (!Regex.IsMatch(m.Id, "^[a-z][a-z0-9-]{1,40}$")) p.Add("identifiant invalide");
        if (string.IsNullOrWhiteSpace(m.Name)) p.Add("nom manquant");
        if (m.Placements.Count == 0 || m.Placements.Any(x => !AllPlacements.Contains(x))) p.Add("positions invalides");
        if (m.Duration is not ("fixed" or "content")) p.Add("durée invalide");
        if (m.Data && string.IsNullOrEmpty(m.Provider)) p.Add("données demandées sans fournisseur");
        if (m.Validity && !(m.Settings.Any(f => f.Key == "validFrom") && m.Settings.Any(f => f.Key == "validTo"))) p.Add("période de validité sans validFrom / validTo");
        var keys = new HashSet<string>();
        foreach (var f in m.Settings)
        {
            if (!Regex.IsMatch(f.Key, "^[A-Za-z][A-Za-z0-9]{0,30}$") || !keys.Add(f.Key)) p.Add($"clé invalide ou en double : {f.Key}");
            if (!FieldTypes.Contains(f.Type)) p.Add($"type inconnu : {f.Type}");
            if (f.Type == "select" && f.Options.Count == 0) p.Add($"choix sans options : {f.Key}");
            if (f.IsMedia && f.Accept is not ("image" or "video")) p.Add($"médias sans type accepté (image ou video) : {f.Key}");
        }
        return p;
    }

    // ---------- Valeurs des paramètres ----------

    /// <summary>Valeurs de départ : celles de l'instance, complétées par les valeurs par défaut (une entrée par champ).</summary>
    public static Dictionary<string, string> WithDefaults(AppManifest m, IReadOnlyDictionary<string, string>? stored)
    {
        var v = new Dictionary<string, string>();
        foreach (var f in m.Settings)
        {
            if (f.IsSecret) { v[f.Key] = ""; continue; }   // un secret enregistré n'est jamais renvoyé au formulaire
            v[f.Key] = stored != null && stored.TryGetValue(f.Key, out var s) ? s : f.Default ?? (f.Type == "bool" ? "false" : "");
        }
        return v;
    }

    public static bool IsVisible(AppField f, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(f.ShowIf)) return true;
        var negate = f.ShowIf.Contains("!=");
        var parts = f.ShowIf.Split(negate ? "!=" : "=", 2);
        values.TryGetValue(parts[0].Trim(), out var cur);
        var equal = string.Equals(cur ?? "", parts.Length > 1 ? parts[1].Trim() : "", StringComparison.Ordinal);
        return negate ? !equal : equal;
    }

    /// <summary>
    /// Valide les valeurs saisies. <paramref name="storedSecrets"/> : clés des secrets déjà enregistrés (un secret laissé vide est conservé).
    /// </summary>
    public static List<string> Validate(AppManifest m, IReadOnlyDictionary<string, string> values, ISet<string> storedSecrets)
    {
        var errors = new List<string>();
        foreach (var f in m.Settings.Where(f => IsVisible(f, values)))
        {
            values.TryGetValue(f.Key, out var raw);
            var v = (raw ?? "").Trim();
            if (f.IsSecret && v.Length == 0 && storedSecrets.Contains(f.Key)) continue;
            if (v.Length == 0)
            {
                if (f.Required) errors.Add($"« {f.Label} » est obligatoire.");
                continue;
            }
            if (v.Length > f.MaxLength) { errors.Add($"« {f.Label} » est trop long ({f.MaxLength} caractères maximum)."); continue; }
            switch (f.Type)
            {
                case "number":
                    if (!int.TryParse(v, out var n)) errors.Add($"« {f.Label} » doit être un nombre entier.");
                    else if (f.Min is { } lo && n < lo || f.Max is { } hi && n > hi) errors.Add($"« {f.Label} » doit être compris entre {f.Min ?? int.MinValue} et {f.Max ?? int.MaxValue}.");
                    break;
                case "select":
                    if (!f.Options.Any(o => o.Value == v)) errors.Add($"« {f.Label} » : choix invalide.");
                    break;
                case "bool":
                    if (v is not ("true" or "false")) errors.Add($"« {f.Label} » : valeur invalide.");
                    break;
                case "color":
                    if (!Regex.IsMatch(v, "^#[0-9a-fA-F]{6}$")) errors.Add($"« {f.Label} » : couleur invalide.");
                    break;
                case "date":
                    if (!DateTime.TryParseExact(v, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _)) errors.Add($"« {f.Label} » : date invalide.");
                    break;
                case "youtube":
                    if (YouTube.Canonical(v) == null) errors.Add($"« {f.Label} » : lien YouTube non reconnu (vidéo ou playlist).");
                    break;
                case "media" or "medias":
                    var parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Any(x => !Guid.TryParse(x, out _))) errors.Add($"« {f.Label} » : sélection invalide.");
                    else if (f.Type == "media" && parts.Length > 1) errors.Add($"« {f.Label} » : un seul fichier attendu.");
                    break;
                case "url":
                    if (!IsHttpUrl(v)) errors.Add($"« {f.Label} » doit être une adresse http:// ou https://.");
                    break;
            }
        }
        // période de validité cohérente
        if (m.Validity && values.TryGetValue("validFrom", out var a) && values.TryGetValue("validTo", out var b)
            && a.Length > 0 && b.Length > 0 && string.CompareOrdinal(a, b) > 0)
            errors.Add("La date de fin précède la date de début.");
        return errors;
    }

    public static bool IsHttpUrl(string v) =>
        Uri.TryCreate(v, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && string.IsNullOrEmpty(u.UserInfo);
}
