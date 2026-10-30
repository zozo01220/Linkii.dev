using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Linkii.Poc;

/// <summary>
/// Intégrations personnalisées (DEV-POC.md §139, maquette maquettes/sources-de-donnees-architecture.html) : l'organisation branche ses propres données.
/// Trois niveaux : une <see cref="DataConnection"/> (où sont les données, comment y entrer : saisie une fois, secret chiffré), des <see cref="DataSet"/>
/// (ce qu'on lit : toujours une table aux colonnes typées) et leur liaison dans les compositions (champs, listes reliées).
/// L'écran ne lit jamais la source : le serveur la lit (<see cref="DataSetService"/>), garde la dernière copie valable sur disque
/// (datasets/{organisation}/) et l'écran reçoit cette copie par /api/data/{composition}/{jeu}.
/// Lot 1 : API web (JSON ou CSV) et fichiers (CSV, JSON, Excel), importés ou lus à une adresse web. Lot 3 : bases de données (<see cref="DataSql"/>).
/// Lot 4 : relais Linkii (<see cref="RelayBroker"/>) pour les API, fichiers et bases du réseau interne du client.
/// </summary>
public static class DataKinds
{
    public const string Api = "api";
    public const string File = "file";
    /// <summary>Base de données (lot 3) : SQL Server, PostgreSQL, MySQL, en direct ou par le relais.</summary>
    public const string Db = "db";

    public const int MaxConnections = 20, MaxDataSets = 25;
    public const int MaxRows = 5000, MaxColumns = 60, MaxCell = 2000, MaxBytes = 2_000_000;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Fréquences d'actualisation proposées (minutes).</summary>
    public static readonly (int Min, string Label)[] Refresh =
        { (1, "Toutes les minutes"), (5, "Toutes les 5 minutes"), (15, "Toutes les 15 minutes"), (60, "Toutes les heures"), (360, "Toutes les 6 heures"), (1440, "Une fois par jour") };

    public static readonly (string Id, string Label)[] Types =
        { ("text", "Texte"), ("number", "Nombre"), ("date", "Date et heure"), ("image", "Image (adresse)"), ("bool", "Oui / non"), ("link", "Lien") };

    public static readonly (string Id, string Label)[] Ops =
        { ("eq", "="), ("ne", "≠"), ("contains", "contient"), ("gt", ">"), ("lt", "<"), ("empty", "est vide"), ("notempty", "n'est pas vide") };

    public static string TypeLabel(string t) => Types.FirstOrDefault(x => x.Id == t).Label ?? "Texte";
    public static string RefreshLabel(int m) => Refresh.FirstOrDefault(x => x.Min == m).Label ?? $"Toutes les {m} minutes";
}

/// <summary>Une connexion : un système du client (API web, fichier). Secret : clé, jeton ou mot de passe, chiffré (SecretBox), jamais réaffiché ni envoyé aux écrans.</summary>
public class DataConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Kind { get; set; } = DataKinds.Api;
    // API web
    public string BaseUrl { get; set; } = "";
    /// <summary>none | key (clé dans un en-tête) | bearer | basic | login (utilisateur et mot de passe envoyés à une adresse de connexion, qui rend un jeton Bearer).</summary>
    public string Auth { get; set; } = "none";
    public string KeyHeader { get; set; } = "X-Api-Key";
    public string User { get; set; } = "";
    public string Secret { get; set; } = "";
    // Auth = login : POST {adresse de base}{LoginPath}, le jeton est lu dans la réponse puis envoyé en « Authorization: Bearer »
    public string LoginPath { get; set; } = "";
    /// <summary>json (corps {"username":…,"password":…}) | form (application/x-www-form-urlencoded).</summary>
    public string LoginFormat { get; set; } = "json";
    public string LoginUserField { get; set; } = "username";
    public string LoginPassField { get; set; } = "password";
    /// <summary>Où est le jeton dans la réponse JSON (« token », « data.access_token ») ; vide = détecté (token, access_token, accessToken, jwt, id_token).</summary>
    public string TokenPath { get; set; } = "";
    /// <summary>En-tête qui porte le jeton à chaque lecture (« Authorization », ou « X-Dashboard-Token » pour certaines API) et ce qui le précède (« Bearer » + espace ; vide = le jeton seul).</summary>
    public string TokenHeader { get; set; } = "Authorization";
    public string TokenPrefix { get; set; } = "Bearer ";
    // Fichier
    /// <summary>upload (fichier importé, gardé sur le serveur) | url (lu à une adresse web à chaque actualisation).</summary>
    public string FileMode { get; set; } = "upload";
    public string FileUrl { get; set; } = "";
    /// <summary>Fichier importé, dans datasets/{organisation}/files/ (nom donné par Linkii).</summary>
    public string FileName { get; set; } = "";
    /// <summary>Nom d'origine du fichier importé (affiché).</summary>
    public string FileLabel { get; set; } = "";
    public DateTime? FileUtc { get; set; }
    /// <summary>Séparateur des CSV : vide = détecté.</summary>
    public string Delimiter { get; set; } = "";
    // Base de données (lot 3) : User et Secret servent aussi ici
    public string Engine { get; set; } = DataSql.SqlServer;
    /// <summary>Serveur de la base, vu depuis le serveur Linkii (direct) ou depuis le relais (« sql-salles.local », « srv\SQLEXPRESS »).</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Database { get; set; } = "";
    /// <summary>SQL Server par le relais : authentification Windows (le compte du service du relais), sans mot de passe.</summary>
    public bool DbWindows { get; set; }
    /// <summary>Chiffrement accepté sans vérifier le certificat du serveur (certificat interne, autosigné).</summary>
    public bool TrustCert { get; set; } = true;
    // Accès (lot 4)
    /// <summary>direct (le serveur Linkii joint la source sur internet) | relay (un relais Linkii du réseau du client la lit).</summary>
    public string Via { get; set; } = "direct";
    public Guid? RelayId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? TestedUtc { get; set; }
    public string LastError { get; set; } = "";

    public DataConnection Copy() => (DataConnection)MemberwiseClone();
}

/// <summary>Un jeu de données : une lecture d'une connexion qui donne toujours une table. Colonnes : types choisis (détectés à la première lecture).</summary>
public class DataSet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConnectionId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>API : chemin ajouté à l'adresse de base (/salles?etage=2). Fichier Excel : nom de la feuille (vide = la première).</summary>
    public string Path { get; set; } = "";
    /// <summary>Réponse JSON : où sont les lignes (data.items) ; vide = détecté.</summary>
    public string RowsPath { get; set; } = "";
    /// <summary>Base de données : requête SELECT écrite (sinon la table de <see cref="Path"/> est lue entière).</summary>
    public string Sql { get; set; } = "";
    public List<DataColumn> Columns { get; set; } = new();
    public int RefreshMin { get; set; } = 5;
    /// <summary>Lignes masquées : celles qui remplissent une de ces conditions ne sont jamais envoyées.</summary>
    public List<DataFilter> Hide { get; set; } = new();
    /// <summary>Nombre de lignes gardées (0 = limite de la plateforme).</summary>
    public int MaxRows { get; set; }
    /// <summary>0 : la dernière copie reste affichée tant que la source ne répond pas ; sinon, au-delà de ce nombre d'heures, « Données indisponibles ».</summary>
    public int StaleHours { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DataSet Copy() => new()
    {
        Id = Id, ConnectionId = ConnectionId, Name = Name, Path = Path, RowsPath = RowsPath, Sql = Sql, RefreshMin = RefreshMin, MaxRows = MaxRows, StaleHours = StaleHours, CreatedUtc = CreatedUtc,
        Columns = Columns.Select(c => c.Copy()).ToList(),
        Hide =Hide.Select(f => new DataFilter { Key = f.Key, Op = f.Op, Value = f.Value }).ToList()
    };
}

/// <summary>
/// Colonne d'un jeu : clé (lettres, chiffres, _ ; celle des champs {{jeu:clé}}), libellé d'origine, type (text, number, date, image, bool, link).
/// Name et Hidden : nom lisible et champ masqué, donnés dans le panneau Données de l'éditeur de compositions ; gardés quand le jeu est relu.
/// </summary>
public class DataColumn
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text";
    public string Name { get; set; } = "";
    public bool Hidden { get; set; }

    public DataColumn Copy() => new() { Key = Key, Label = Label, Type = Type, Name = Name, Hidden = Hidden };

    /// <summary>Colonnes relues : nom lisible et masquage repris des colonnes connues de même clé.</summary>
    public static List<DataColumn> Merge(IEnumerable<DataColumn> fresh, IEnumerable<DataColumn> known) =>
        fresh.Select(c => new DataColumn { Key = c.Key, Label = c.Label, Type = c.Type, Name = known.FirstOrDefault(k => k.Key == c.Key)?.Name ?? "", Hidden = known.FirstOrDefault(k => k.Key == c.Key)?.Hidden ?? false }).ToList();
}

public class DataFilter
{
    public string Key { get; set; } = "";
    public string Op { get; set; } = "eq";
    public string Value { get; set; } = "";
}

/// <summary>Table lue : valeurs normalisées en texte (nombres en notation invariante, dates ISO, oui/non en true/false), mises en forme par l'écran.</summary>
public record DataTable(List<DataColumn> Columns, List<List<string?>> Rows, DateTime ReadUtc, string Hash);

/// <summary>Une lecture d'un jeu (historique) : ok, warn (lue mais copie conservée) ou bad (échec, copie conservée).</summary>
public record DataReadLog(DateTime Utc, string Level, string Message, int Rows, long Ms, bool Changed);

/// <summary>Table brute, avant les types : en-têtes et valeurs telles que lues.</summary>
public record RawTable(List<string> Headers, List<List<string?>> Rows);

// ---------------------------------------------------------------------------------------------
/// <summary>Lecture des formats (CSV, JSON, Excel) et construction de la table : clés, types, normalisation, filtres et limites.</summary>
public static class DataTables
{
    /// <summary>Clé d'une colonne ou d'un paramètre : minuscules sans accents, lettres, chiffres et _ (« Prochaine réunion » → prochaine_reunion).</summary>
    public static string Slug(string? s)
    {
        var norm = (s ?? "").Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(ch);
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_');
        }
        var r = Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
        if (r.Length > 40) r = r[..40].TrimEnd('_');
        return r.Length == 0 ? "col" : char.IsDigit(r[0]) ? "c" + r : r;
    }

    /// <summary>Contenu d'un fichier ou d'une réponse → table brute, d'après son début (PK = Excel, { ou [ = JSON, sinon CSV).</summary>
    public static RawTable Parse(byte[] bytes, string? sheet, string? rowsPath, string? delimiter)
    {
        if (bytes.Length >= 2 && bytes[0] == 'P' && bytes[1] == 'K') return Xlsx(bytes, sheet);
        var text = Decode(bytes);
        var t = text.TrimStart();
        if (t.StartsWith('{') || t.StartsWith('[')) return Json(t, rowsPath);
        if (t.StartsWith('<')) throw new InvalidOperationException("La réponse est une page HTML ou du XML : seuls JSON, CSV et Excel sont lus.");
        return Csv(text, delimiter);
    }

    /// <summary>UTF-8 (avec ou sans BOM), sinon Latin-1 (exports Excel d'anciens postes Windows).</summary>
    public static string Decode(byte[] b)
    {
        try { return new UTF8Encoding(false, true).GetString(b).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(b); }
    }

    // ---------- CSV ----------
    public static RawTable Csv(string text, string? delimiter)
    {
        var d = delimiter is { Length: 1 } ? delimiter[0] : delimiter == "\\t" ? '\t' : Detect(text);
        var lines = new List<List<string?>>();
        var row = new List<string?>();
        var cell = new StringBuilder();
        bool quoted = false, any = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else quoted = false; }
                else cell.Append(c);
                continue;
            }
            if (c == '"' && cell.Length == 0) { quoted = true; any = true; }
            else if (c == d) { row.Add(cell.ToString()); cell.Clear(); any = true; }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); if (any || row.Any(x => x!.Length > 0)) lines.Add(row); row = new(); any = false; if (lines.Count > DataKinds.MaxRows + 1) break; }
            else { cell.Append(c); any = true; }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); lines.Add(row); }
        if (lines.Count == 0) throw new InvalidOperationException("Le fichier est vide.");
        var headers = lines[0].Select(h => (h ?? "").Trim()).ToList();
        return new RawTable(headers, lines.Skip(1).ToList());
    }

    /// <summary>Séparateur le plus fréquent de la première ligne, hors guillemets (; , tabulation |).</summary>
    private static char Detect(string text)
    {
        var first = text.Split('\n')[0];
        var best = ','; var max = -1;
        foreach (var c in new[] { ';', ',', '\t', '|' })
        {
            int n = 0; var q = false;
            foreach (var ch in first) { if (ch == '"') q = !q; else if (ch == c && !q) n++; }
            if (n > max) { max = n; best = c; }
        }
        return best;
    }

    // ---------- JSON ----------
    public static RawTable Json(string text, string? rowsPath)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 }); }
        catch (JsonException e) { throw new InvalidOperationException("JSON illisible : " + e.Message); }
        using (doc)
        {
            var node = string.IsNullOrWhiteSpace(rowsPath) ? FindRows(doc.RootElement, 0) ?? doc.RootElement : Navigate(doc.RootElement, rowsPath!);
            var items = node.ValueKind == JsonValueKind.Array ? node.EnumerateArray().Take(DataKinds.MaxRows + 1).ToList() : new List<JsonElement> { node };
            var headers = new List<string>();
            var flat = new List<Dictionary<string, string?>>();
            foreach (var it in items)
            {
                var row = new Dictionary<string, string?>();
                if (it.ValueKind == JsonValueKind.Object) Flatten(it, "", row, 0);
                else if (it.ValueKind == JsonValueKind.Array) { var j = 0; foreach (var v in it.EnumerateArray()) row["colonne " + (++j)] = Scalar(v); }
                else row["valeur"] = Scalar(it);
                foreach (var k in row.Keys) if (!headers.Contains(k)) headers.Add(k);
                flat.Add(row);
            }
            return new RawTable(headers, flat.Select(r => headers.Select(h => r.GetValueOrDefault(h)).ToList()).ToList());
        }
    }

    /// <summary>Premier tableau d'objets trouvé (racine, puis propriétés jusqu'à 3 niveaux) : là où sont généralement les lignes.</summary>
    private static JsonElement? FindRows(JsonElement e, int depth)
    {
        if (e.ValueKind == JsonValueKind.Array) return e;
        if (e.ValueKind != JsonValueKind.Object || depth > 3) return null;
        foreach (var p in e.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Array && p.Value.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.Object)) return p.Value;
        foreach (var p in e.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Object && FindRows(p.Value, depth + 1) is { } r) return r;
        return null;
    }

    /// <summary>Chemin « data.items », « result[0].rows », « data.items[*] » ([*] ignoré).</summary>
    public static JsonElement Navigate(JsonElement e, string path)
    {
        foreach (var raw in path.Trim().TrimStart('$', '.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(raw, @"^([^\[]*)((\[(\d+|\*)\])*)$");
            var name = m.Success ? m.Groups[1].Value : raw;
            if (name.Length > 0)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var next)) throw new InvalidOperationException($"« {name} » introuvable dans la réponse (chemin des lignes : {path}).");
                e = next;
            }
            foreach (Capture c in m.Groups[4].Captures)
            {
                if (c.Value == "*") continue;
                var i = int.Parse(c.Value, CultureInfo.InvariantCulture);
                if (e.ValueKind != JsonValueKind.Array || i >= e.GetArrayLength()) throw new InvalidOperationException($"Élément [{i}] introuvable (chemin des lignes : {path}).");
                e = e[i];
            }
        }
        return e;
    }

    private static void Flatten(JsonElement o, string prefix, Dictionary<string, string?> row, int depth)
    {
        foreach (var p in o.EnumerateObject())
        {
            var key = prefix + p.Name;
            if (p.Value.ValueKind == JsonValueKind.Object && depth < 2) Flatten(p.Value, key + ".", row, depth + 1);
            else if (p.Value.ValueKind == JsonValueKind.Array)
                row[key] = p.Value.EnumerateArray().All(v => v.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                    ? string.Join(", ", p.Value.EnumerateArray().Select(Scalar)) : p.Value.GetRawText();
            else row[key] = Scalar(p.Value);
        }
    }

    private static string? Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => v.GetRawText()
    };

    // ---------- Excel (.xlsx) : lu directement dans l'archive (feuille, textes partagés, formats de date) ----------
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static RawTable Xlsx(byte[] bytes, string? sheet)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new InvalidOperationException("Fichier Excel illisible."); }
        using (zip)
        {
            XDocument Load(string name)
            {
                var e = zip.GetEntry(name) ?? throw new InvalidOperationException("Fichier Excel incomplet (" + name + ").");
                if (e.Length > 20_000_000) throw new InvalidOperationException("Feuille Excel trop volumineuse.");
                using var s = e.Open();
                return XDocument.Load(s);
            }
            var wb = Load("xl/workbook.xml");
            var sheets = wb.Descendants(S + "sheet").Select(x => (Name: (string?)x.Attribute("name") ?? "", Rid: (string?)x.Attribute(Rel + "id") ?? "")).ToList();
            if (sheets.Count == 0) throw new InvalidOperationException("Le classeur n'a aucune feuille.");
            var pick = string.IsNullOrWhiteSpace(sheet) ? sheets[0] : sheets.FirstOrDefault(x => x.Name.Equals(sheet!.Trim(), StringComparison.CurrentCultureIgnoreCase));
            if (pick.Name == null) throw new InvalidOperationException($"Feuille « {sheet} » introuvable. Feuilles : {string.Join(", ", sheets.Select(x => x.Name))}.");
            var rels = Load("xl/_rels/workbook.xml.rels").Descendants(Pkg + "Relationship").ToDictionary(x => (string?)x.Attribute("Id") ?? "", x => (string?)x.Attribute("Target") ?? "");
            var target = rels.GetValueOrDefault(pick.Rid) ?? "worksheets/sheet1.xml";
            target = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

            var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? new List<string>()
                : Load("xl/sharedStrings.xml").Root!.Elements(S + "si").Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList();
            var dateStyles = DateStyles(zip.GetEntry("xl/styles.xml") is null ? null : Load("xl/styles.xml"));

            var grid = new List<List<string?>>();
            foreach (var r in Load(target).Descendants(S + "row"))
            {
                var row = new List<string?>();
                foreach (var c in r.Elements(S + "c"))
                {
                    var col = ColumnIndex((string?)c.Attribute("r"));
                    if (col < 0) col = row.Count;
                    if (col >= DataKinds.MaxColumns * 2) continue;
                    while (row.Count < col) row.Add(null);
                    var t = (string?)c.Attribute("t");
                    var v = c.Element(S + "v")?.Value;
                    string? val = t switch
                    {
                        "s" => int.TryParse(v, out var i) && i < shared.Count ? shared[i] : null,
                        "inlineStr" => string.Concat(c.Descendants(S + "t").Select(x => x.Value)),
                        "b" => v == "1" ? "true" : "false",
                        _ => v
                    };
                    if (t is null or "n" && v != null && int.TryParse((string?)c.Attribute("s"), out var st) && dateStyles.Contains(st)
                        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa) && oa is > 0 and < 2958466)
                    {
                        var dt = DateTime.FromOADate(oa);
                        val = dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd") : oa < 1 ? dt.ToString("HH:mm") : dt.ToString("yyyy-MM-ddTHH:mm:ss");
                    }
                    row.Add(val);
                }
                if (row.Any(x => !string.IsNullOrEmpty(x))) grid.Add(row);
                if (grid.Count > DataKinds.MaxRows + 1) break;
            }
            if (grid.Count == 0) throw new InvalidOperationException($"La feuille « {pick.Name} » est vide.");
            return new RawTable(grid[0].Select(h => (h ?? "").Trim()).ToList(), grid.Skip(1).ToList());
        }
    }

    /// <summary>Styles de cellule qui affichent une date ou une heure (formats intégrés 14-22, 45-47, ou format personnalisé avec j/m/a/h).</summary>
    private static HashSet<int> DateStyles(XDocument? styles)
    {
        var set = new HashSet<int>();
        if (styles?.Root == null) return set;
        var custom = styles.Root.Element(S + "numFmts")?.Elements(S + "numFmt")
            .Where(f => Regex.IsMatch(Regex.Replace((string?)f.Attribute("formatCode") ?? "", "\"[^\"]*\"|\\[[^\\]]*\\]", ""), "[dmyhsDMYHS]"))
            .Select(f => (int?)f.Attribute("numFmtId") ?? -1).ToHashSet() ?? new();
        var i = 0;
        foreach (var xf in styles.Root.Element(S + "cellXfs")?.Elements(S + "xf") ?? Enumerable.Empty<XElement>())
        {
            var id = (int?)xf.Attribute("numFmtId") ?? 0;
            if (id is >= 14 and <= 22 or >= 45 and <= 47 || custom.Contains(id)) set.Add(i);
            i++;
        }
        return set;
    }

    private static int ColumnIndex(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return -1;
        var n = 0; var any = false;
        foreach (var ch in cellRef) { if (ch is >= 'A' and <= 'Z') { n = n * 26 + (ch - 'A' + 1); any = true; } else break; }
        return any ? n - 1 : -1;
    }

    // ---------- types et normalisation ----------
    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yyyy HH:mm", "d.M.yyyy H:mm", "dd.MM.yyyy HH:mm:ss", "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss"
    };

    /// <summary>Date reconnue → texte ISO (avec fuseau s'il était donné, sans heure pour une date seule) ; null sinon.</summary>
    public static string? Date(string s)
    {
        s = s.Trim();
        if (s.Length < 8 || s.Length > 40 || !char.IsDigit(s[0])) return null;
        if (Regex.IsMatch(s, @"(Z|[+-]\d\d:?\d\d)$") && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var off))
            return off.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return s.Length <= 10 ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>Nombre reconnu (1234.5, 1 234,5, 1'234.50, -3) → notation invariante ; null sinon.</summary>
    public static string? Number(string s)
    {
        s = s.Trim().Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace("'", "").Replace("’", "");
        if (s.Length == 0 || s.Length > 30 || !Regex.IsMatch(s, @"^[+-]?(\d+([.,]\d{3})*([.,]\d+)?|[.,]\d+)$")) return null;
        int dot = s.LastIndexOf('.'), comma = s.LastIndexOf(',');
        if (dot >= 0 && comma >= 0) s = dot > comma ? s.Replace(",", "") : s.Replace(".", "").Replace(',', '.');
        else if (comma >= 0) s = Regex.IsMatch(s, @",\d{3}$") && s.Count(c => c == ',') > 1 ? s.Replace(",", "") : s.Replace(',', '.');
        else if (s.Count(c => c == '.') > 1) s = s.Replace(".", "");
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n.ToString("R", CultureInfo.InvariantCulture) : null;
    }

    public static string? Bool(string s) => s.Trim().ToLowerInvariant() switch
    {
        "true" or "vrai" or "oui" or "yes" or "1" or "x" => "true",
        "false" or "faux" or "non" or "no" or "0" => "false",
        _ => null
    };

    private static bool IsUrl(string s) => Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http");
    private static bool IsImage(string s) => IsUrl(s) && Regex.IsMatch(s, @"\.(jpe?g|png|gif|webp|svg|avif)(\?.*)?$", RegexOptions.IgnoreCase);

    /// <summary>Type le plus précis qui convient à toutes les valeurs non vides (50 premières) : oui/non, nombre, date, image, lien, sinon texte.</summary>
    public static string Detect(IEnumerable<string?> values)
    {
        var v = values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Take(50).ToList();
        if (v.Count == 0) return "text";
        if (v.All(x => x.Trim().ToLowerInvariant() is "true" or "false" or "oui" or "non" or "yes" or "no" or "vrai" or "faux")) return "bool";
        if (v.All(x => Number(x) != null)) return "number";
        if (v.All(x => Date(x) != null)) return "date";
        if (v.All(IsImage)) return "image";
        if (v.All(IsUrl)) return "link";
        return "text";
    }

    /// <summary>Valeur normalisée selon le type de la colonne ; une valeur qui ne s'y prête pas reste telle quelle (texte).</summary>
    public static string? Normalize(string? v, string type)
    {
        if (v == null) return null;
        v = v.Length > DataKinds.MaxCell ? v[..DataKinds.MaxCell] : v;
        if (v.Trim().Length == 0) return "";
        return type switch
        {
            "number" => Number(v) ?? v,
            "date" => Date(v) ?? v,
            "bool" => Bool(v) ?? v,
            _ => v
        };
    }

    /// <summary>
    /// Table d'un jeu à partir de la table brute : clés uniques, types (ceux du jeu pour les colonnes connues, détectés pour les nouvelles),
    /// valeurs normalisées, lignes masquées retirées, limite de lignes. Rend aussi les colonnes du jeu qui ont disparu de la source.
    /// </summary>
    public static (DataTable Table, List<string> Missing) Build(RawTable raw, DataSet ds, DateTime now)
    {
        if (raw.Headers.Count == 0) throw new InvalidOperationException("Aucune colonne trouvée.");
        var cols = new List<DataColumn>();
        var idx = new List<int>();
        for (var i = 0; i < raw.Headers.Count && cols.Count < DataKinds.MaxColumns; i++)
        {
            var label = raw.Headers[i].Length > 0 ? raw.Headers[i] : "colonne " + (i + 1);
            var key = Slug(label);
            for (var n = 2; cols.Any(c => c.Key == key); n++) key = Slug(label) + "_" + n;
            var known = ds.Columns.FirstOrDefault(c => c.Key == key);
            var type = known?.Type ?? Detect(raw.Rows.Select(r => i < r.Count ? r[i] : null));
            cols.Add(new DataColumn { Key = key, Label = label, Type = DataKinds.Types.Any(t => t.Id == type) ? type : "text" });
            idx.Add(i);
        }
        var limit = ds.MaxRows > 0 ? Math.Min(ds.MaxRows, DataKinds.MaxRows) : DataKinds.MaxRows;
        var rows = new List<List<string?>>();
        foreach (var r in raw.Rows)
        {
            var row = idx.Select((i, j) => Normalize(i < r.Count ? r[i] : null, cols[j].Type)).ToList();
            if (row.All(string.IsNullOrEmpty)) continue;
            if (ds.Hide.Any(f => Matches(f, cols, row))) continue;
            rows.Add(row);
            if (rows.Count >= limit) break;
        }
        var missing = ds.Columns.Where(c => !cols.Any(x => x.Key == c.Key)).Select(c => c.Label.Length > 0 ? c.Label : c.Key).ToList();
        return (new DataTable(cols, rows, now, Hash(cols, rows)), missing);
    }

    /// <summary>Condition d'une ligne (filtre « masquer »). Comparaison numérique si les deux côtés sont des nombres, sinon texte sans casse.</summary>
    public static bool Matches(DataFilter f, List<DataColumn> cols, List<string?> row)
    {
        var i = cols.FindIndex(c => c.Key == f.Key);
        if (i < 0) return false;
        var v = (row[i] ?? "").Trim();
        var w = (f.Value ?? "").Trim();
        int Cmp() => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && (Number(w) is { } ws) && double.TryParse(ws, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
            ? a.CompareTo(b) : string.Compare(v, w, StringComparison.CurrentCultureIgnoreCase);
        return f.Op switch
        {
            "eq" => Cmp() == 0,
            "ne" => Cmp() != 0,
            "contains" => w.Length > 0 && v.Contains(w, StringComparison.CurrentCultureIgnoreCase),
            "gt" => Cmp() > 0,
            "lt" => Cmp() < 0,
            "empty" => v.Length == 0,
            "notempty" => v.Length > 0,
            _ => false
        };
    }

    public static string Hash(List<DataColumn> cols, List<List<string?>> rows)
    {
        var json = JsonSerializer.Serialize(new { c = cols.Select(c => c.Key + ":" + c.Type), rows });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)), 0, 8).ToLowerInvariant();
    }

    /// <summary>Ce que reçoit l'écran : seulement les colonnes demandées (toutes si la liste est vide), et l'heure de la lecture.</summary>
    public static object ForScreen(DataTable t, IReadOnlyCollection<string>? keys, bool stale)
    {
        var pick = keys == null || keys.Count == 0 ? Enumerable.Range(0, t.Columns.Count).ToList()
            : t.Columns.Select((c, i) => (c, i)).Where(x => keys.Contains(x.c.Key)).Select(x => x.i).ToList();
        return new
        {
            kind = "table",
            columns = pick.Select(i => new { key = t.Columns[i].Key, label = t.Columns[i].Label, type = t.Columns[i].Type }),
            rows = t.Rows.Select(r => pick.Select(i => i < r.Count ? r[i] : null)),
            readAt = t.ReadUtc, hash = t.Hash, stale
        };
    }
}

// ---------------------------------------------------------------------------------------------
/// <summary>
/// Lecture des jeux de données : connecteurs (API web, fichier), copie de la dernière table valable et historique sur disque
/// (datasets/{organisation}/{jeu}.json et .log.json), gardés aussi en mémoire. Rien de tout cela n'est écrit dans data.json :
/// seule la configuration (connexions et jeux) y est.
/// </summary>
public class DataSetService
{
    public static string Root = "datasets";
    private const int HistoryMax = 100;

    private readonly JsonStore store;
    private readonly SafeHttp http;
    private readonly SecretBox box;
    private readonly RelayBroker relays;
    private readonly ConcurrentDictionary<(Guid, Guid), DataTable?> tables = new();
    private readonly ConcurrentDictionary<(Guid, Guid), List<DataReadLog>> logs = new();
    private readonly ConcurrentDictionary<(Guid, Guid), SemaphoreSlim> running = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DataSetService(JsonStore store, SafeHttp http, SecretBox box, RelayBroker relays) { this.store = store; this.http = http; this.box = box; this.relays = relays; }

    public static string Dir(Guid clientId) => Path.Combine(Root, clientId.ToString("N"));
    public static string FilesDir(Guid clientId) => Path.Combine(Dir(clientId), "files");
    private static string TableFile(Guid c, Guid d) => Path.Combine(Dir(c), d.ToString("N") + ".json");
    private static string LogFile(Guid c, Guid d) => Path.Combine(Dir(c), d.ToString("N") + ".log.json");

    /// <summary>Dernière table valable du jeu (null : jamais lue avec succès).</summary>
    public DataTable? Table(Guid clientId, Guid dataSetId) => tables.GetOrAdd((clientId, dataSetId), k =>
    {
        try { return File.Exists(TableFile(k.Item1, k.Item2)) ? JsonSerializer.Deserialize<DataTable>(File.ReadAllText(TableFile(k.Item1, k.Item2)), Json) : null; }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    });

    /// <summary>Lectures du jeu, de la plus récente à la plus ancienne.</summary>
    public List<DataReadLog> History(Guid clientId, Guid dataSetId)
    {
        var l = Log(clientId, dataSetId);
        lock (l) return l.AsEnumerable().Reverse().ToList();
    }

    public DataReadLog? Last(Guid clientId, Guid dataSetId)
    {
        var l = Log(clientId, dataSetId);
        lock (l) return l.Count > 0 ? l[^1] : null;
    }

    private List<DataReadLog> Log(Guid c, Guid d) => logs.GetOrAdd((c, d), k =>
    {
        try { return File.Exists(LogFile(k.Item1, k.Item2)) ? JsonSerializer.Deserialize<List<DataReadLog>>(File.ReadAllText(LogFile(k.Item1, k.Item2)), Json) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    });

    /// <summary>La copie est-elle trop vieille pour être montrée (réglage « Après N heures, afficher Données indisponibles ») ?</summary>
    public static bool IsStale(DataSet ds, DataTable t, DateTime now) => ds.StaleHours > 0 && now - t.ReadUtc > TimeSpan.FromHours(ds.StaleHours);

    /// <summary>Jeu à lire maintenant : jamais lu, ou dernière lecture plus ancienne que sa fréquence (fichier importé : seulement à l'import).</summary>
    public bool Due(Guid clientId, DataConnection c, DataSet ds, DateTime now)
    {
        var last = Last(clientId, ds.Id);
        if (last == null) return true;
        if (c.Kind == DataKinds.File && c.FileMode == "upload") return c.FileUtc is { } f && f > last.Utc;
        return now - last.Utc >= TimeSpan.FromMinutes(Math.Max(1, ds.RefreshMin)) - TimeSpan.FromSeconds(5);
    }

    // ---------- connecteurs ----------

    /// <summary>Lit la source d'une connexion pour ce jeu (table brute), en 10 secondes au plus (20 par le relais : aller-retour en plus).</summary>
    public async Task<RawTable> ReadRaw(Guid clientId, DataConnection c, DataSet ds, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var limit = (c.Via == "relay" ? DataKinds.Timeout * 2 : DataKinds.Timeout) * (c.Kind == DataKinds.Api && c.Auth == "login" ? 2 : 1);   // la connexion par identifiants est un appel de plus
        cts.CancelAfter(limit);
        try
        {
            if (c.Kind == DataKinds.Db)
            {
                var r = await Sql(clientId, c, "read", ds.Path, ds.Sql, cts.Token);
                return new RawTable(r.Headers ?? new(), r.Rows ?? new());
            }
            var bytes = await Bytes(clientId, c, ds.Path, cts.Token);
            return DataTables.Parse(bytes, c.Kind == DataKinds.File ? ds.Path : null, ds.RowsPath, c.Delimiter);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException($"Délai dépassé ({limit.TotalSeconds:0} s) : la source n'a pas répondu à temps."); }
    }

    /// <summary>Contenu brut : réponse de l'API (adresse de base + chemin), fichier importé, ou fichier lu à son adresse.</summary>
    private async Task<byte[]> Bytes(Guid clientId, DataConnection c, string path, CancellationToken ct)
    {
        if (c.Kind == DataKinds.File && c.FileMode == "upload")
        {
            var f = Path.Combine(FilesDir(clientId), Path.GetFileName(c.FileName));
            if (c.FileName.Length == 0 || !File.Exists(f)) throw new InvalidOperationException("Aucun fichier importé.");
            if (new FileInfo(f).Length > DataKinds.MaxBytes * 5) throw new InvalidOperationException("Fichier trop volumineux.");
            return await File.ReadAllBytesAsync(f, ct);
        }
        var url = c.Kind == DataKinds.File ? c.FileUrl : Join(c.BaseUrl, path);
        var headers = new Dictionary<string, string> { ["Accept"] = "application/json, text/csv, */*" };
        var secret = c.Secret.Length > 0 ? box.Unprotect(c.Secret) : "";
        var max = DataKinds.MaxBytes * (c.Kind == DataKinds.File ? 5 : 1);
        var login = c.Kind == DataKinds.Api && c.Auth == "login";
        if (c.Kind == DataKinds.Api)
            switch (c.Auth)
            {
                case "key" when secret.Length > 0: headers[c.KeyHeader.Trim().Length > 0 ? c.KeyHeader.Trim() : "X-Api-Key"] = secret; break;
                case "bearer" when secret.Length > 0: headers["Authorization"] = "Bearer " + secret; break;
                case "basic": headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(c.User + ":" + secret)); break;
            }
        for (var attempt = 0; ; attempt++)
        {
            if (login) headers[c.TokenHeader.Trim().Length > 0 ? c.TokenHeader.Trim() : "Authorization"] = c.TokenPrefix + await Token(clientId, c, attempt > 0, ct);
            var a = await Call(clientId, c, "GET", url, headers, null, null, max, ct);
            if (a.Status == 401 && login && attempt == 0) continue;   // jeton expiré ou révoqué : une nouvelle connexion, puis un seul nouvel essai
            Status(a.Status);
            return a.Body;
        }
    }

    private sealed record HttpAnswer(int Status, byte[] Body);

    /// <summary>Un appel HTTP, direct ou par le relais (adresse du réseau interne : le relais l'appelle et rend la réponse). Le corps d'une réponse en erreur n'est pas lu.</summary>
    private async Task<HttpAnswer> Call(Guid clientId, DataConnection c, string method, string url, Dictionary<string, string> headers, string? body, string? contentType, int max, CancellationToken ct)
    {
        if (c.Via == "relay")
        {
            var rr = await relays.Run(clientId, c.RelayId ?? Guid.Empty, new RelayJob { Kind = "http", Op = method == "POST" ? "login" : "read", Method = method, Body = body, ContentType = contentType, Url = url, Headers = headers, MaxBytes = max }, ct);
            return new HttpAnswer(rr.Status, rr.Status >= 300 ? Array.Empty<byte>() : Convert.FromBase64String(rr.Body64 ?? ""));
        }
        if (method == "POST")
        {
            var (status, text) = await http.Request(HttpMethod.Post, url, body, contentType, headers, max, ct);
            return new HttpAnswer(status, Encoding.UTF8.GetBytes(text));
        }
        using var res = await http.Send(url, ct, headers);
        if ((int)res.StatusCode >= 300) return new HttpAnswer((int)res.StatusCode, Array.Empty<byte>());
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > max) throw new InvalidOperationException($"Réponse trop volumineuse (plus de {DataKinds.MaxBytes / 1_000_000} Mo).");
        }
        return new HttpAnswer((int)res.StatusCode, ms.ToArray());
    }

    // ---------- connexion par identifiants (Auth = login) ----------

    private readonly ConcurrentDictionary<Guid, (string Print, string Token, DateTime Until)> tokens = new();
    private static readonly string[] TokenNames = { "token", "access_token", "accessToken", "jwt", "id_token", "idToken", "bearer" };

    /// <summary>
    /// Jeton Bearer d'une connexion : celui gardé en mémoire s'il est encore valable (et si les réglages n'ont pas changé), sinon une nouvelle connexion
    /// (POST des identifiants à l'adresse de connexion). Durée : expires_in de la réponse, ou date d'expiration du jeton JWT, sinon 20 minutes ;
    /// une réponse 401 de l'API provoque de toute façon une nouvelle connexion.
    /// </summary>
    private async Task<string> Token(Guid clientId, DataConnection c, bool renew, CancellationToken ct)
    {
        var secret = c.Secret.Length > 0 ? box.Unprotect(c.Secret) : "";
        var print = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", c.BaseUrl, c.LoginPath, c.LoginFormat, c.LoginUserField, c.LoginPassField, c.TokenPath, c.User, secret, c.Via, c.RelayId))));
        if (!renew && tokens.TryGetValue(c.Id, out var known) && known.Print == print && known.Until > DateTime.UtcNow) return known.Token;

        string user = c.User.Trim(), uf = c.LoginUserField.Trim(), pf = c.LoginPassField.Trim();
        string body, type;
        if (c.LoginFormat == "form")
        {
            body = Uri.EscapeDataString(uf) + "=" + Uri.EscapeDataString(user) + "&" + Uri.EscapeDataString(pf) + "=" + Uri.EscapeDataString(secret);
            type = "application/x-www-form-urlencoded";
        }
        else
        {
            body = JsonSerializer.Serialize(new Dictionary<string, string> { [uf] = user, [pf] = secret });
            type = "application/json";
        }
        var headers = new Dictionary<string, string> { ["Accept"] = "application/json, text/plain, */*" };
        var a = await Call(clientId, c, "POST", Join(c.BaseUrl, c.LoginPath), headers, body, type, 200_000, ct);
        if (a.Status is 400 or 401 or 403 or 422) throw new InvalidOperationException($"Connexion refusée par l'API ({a.Status}) : vérifiez l'utilisateur, le mot de passe et le nom des champs.");
        if (a.Status == 404 || a.Status == 405) throw new InvalidOperationException($"Adresse de connexion introuvable ({a.Status}) : vérifiez le chemin de connexion ({c.LoginPath}).");
        Status(a.Status);
        var (token, seconds) = ReadToken(Encoding.UTF8.GetString(a.Body), c.TokenPath);
        tokens[c.Id] = (print, token, DateTime.UtcNow.AddSeconds(Math.Clamp(seconds - 60, 30, 12 * 3600)));
        return token;
    }

    /// <summary>Jeton de la réponse de connexion et sa durée en secondes (20 minutes si elle n'est pas donnée).</summary>
    public static (string Token, int Seconds) ReadToken(string text, string? tokenPath)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(text.Trim().Length == 0 ? "\"\"" : text).RootElement.Clone(); }
        catch (JsonException)
        {
            var plain = text.Trim();   // certaines API rendent le jeton seul, en texte brut
            if (string.IsNullOrWhiteSpace(tokenPath) && Regex.IsMatch(plain, @"^[\x21-\x7E]{20,8000}$") && !plain.StartsWith('<')) return (plain, 1200);
            throw new InvalidOperationException("La réponse de connexion n'est pas du JSON : jeton introuvable.");
        }
        JsonElement? hit = null;
        JsonElement owner = root;
        if (!string.IsNullOrWhiteSpace(tokenPath))
        {
            try { hit = DataTables.Navigate(root, tokenPath!); }
            catch (InvalidOperationException) { throw new InvalidOperationException($"Jeton introuvable dans la réponse de connexion (chemin : {tokenPath})."); }
        }
        else if (root.ValueKind == JsonValueKind.String) hit = root;
        else
            foreach (var scope in new[] { root }.Concat(new[] { "data", "result", "response" }.Select(n => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(n, out var x) ? x : default)))
            {
                if (scope.ValueKind != JsonValueKind.Object) continue;
                foreach (var name in TokenNames)
                    foreach (var p in scope.EnumerateObject())
                        if (hit == null && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) { hit = p.Value; owner = scope; }
                if (hit != null) break;
            }
        if (hit is not { ValueKind: JsonValueKind.String } h || (h.GetString() ?? "").Trim() is not { Length: > 0 } token)
            throw new InvalidOperationException("Jeton introuvable dans la réponse de connexion : indiquez son emplacement (ex. data.access_token).");
        if (!Regex.IsMatch(token, @"^[\x21-\x7E]{1,8000}$")) throw new InvalidOperationException("Le jeton rendu par l'API n'a pas un format valide.");
        var seconds = 1200;
        if (owner.ValueKind == JsonValueKind.Object)
            foreach (var p in owner.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var s) && s > 0 && (p.Name.Equals("expires_in", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("expiresIn", StringComparison.OrdinalIgnoreCase))) { seconds = s; break; }
        if (seconds == 1200 && JwtExpiry(token) is { } exp) seconds = (int)Math.Clamp((exp - DateTime.UtcNow).TotalSeconds, 0, int.MaxValue);
        return (token, seconds);
    }

    /// <summary>Date d'expiration (exp) d'un jeton JWT, si lisible.</summary>
    private static DateTime? JwtExpiry(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            return doc.RootElement.TryGetProperty("exp", out var e) && e.TryGetInt64(out var t) ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentOutOfRangeException) { return null; }
    }

    private static void Status(int code)
    {
        if (code is 401 or 403) throw new InvalidOperationException($"Accès refusé ({code}) : vérifiez l'authentification de la connexion.");
        if (code == 404) throw new InvalidOperationException("Adresse introuvable (404) : vérifiez l'adresse et le chemin.");
        if (code >= 300 && code < 400) throw new InvalidOperationException($"La source redirige ailleurs ({code}) : indiquez l'adresse finale.");
        if (code >= 400) throw new InvalidOperationException($"La source a répondu une erreur ({code}).");
    }

    /// <summary>Paramètres de la base d'une connexion (mot de passe déchiffré).</summary>
    private DataSql.Target Target(DataConnection c) =>
        new(c.Engine, c.Host.Trim(), c.Port, c.Database.Trim(), c.User.Trim(), c.Secret.Length > 0 ? box.Unprotect(c.Secret) : "",
            c.DbWindows && c.Via == "relay" && c.Engine == DataSql.SqlServer, c.TrustCert);

    /// <summary>
    /// Opération sur une base : par le relais, ou en direct depuis le serveur. En direct, la base doit avoir une adresse publique : elle est jointe par son
    /// adresse IP vérifiée (jamais le réseau interne de Linkii, comme pour les API).
    /// </summary>
    private async Task<RelayReply> Sql(Guid clientId, DataConnection c, string op, string? table, string? sql, CancellationToken ct)
    {
        var t = Target(c);
        if (c.Via == "relay")
            return await relays.Run(clientId, c.RelayId ?? Guid.Empty, new RelayJob { Kind = "sql", Op = op, Db = t, Table = table, Sql = sql, MaxRows = DataKinds.MaxRows }, ct);
        var cut = t.Host.IndexOf('\\');
        var name = cut < 0 ? t.Host : t.Host[..cut];
        System.Net.IPAddress[] ips;
        try { ips = await System.Net.Dns.GetHostAddressesAsync(name, ct); }
        catch (System.Net.Sockets.SocketException) { throw new InvalidOperationException($"Serveur « {name} » introuvable. Pour une base du réseau interne, passez par le relais Linkii."); }
        if (ips.Length == 0 || ips.Any(x => !SafeHttp.IsPublic(x))) throw new InvalidOperationException("Adresse du réseau interne : pour cette base, passez par le relais Linkii.");
        t = t with { Host = ips[0] + (cut < 0 ? "" : t.Host[cut..]) };
        switch (op)
        {
            case "test": return new RelayReply { Ok = true, Message = await DataSql.Test(t, ct) };
            case "discover": return new RelayReply { Ok = true, Tables = await DataSql.Discover(t, ct) };
            default:
                var r = await DataSql.Read(t, table, sql, DataKinds.MaxRows, ct);
                return new RelayReply { Ok = true, Headers = r.Headers, Rows = r.Rows };
        }
    }

    /// <summary>Tables et vues d'une base (« schema.table »).</summary>
    public async Task<List<string>> Discover(Guid clientId, DataConnection c, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(25));
        return (await Sql(clientId, c, "discover", null, null, cts.Token)).Tables ?? new();
    }

    /// <summary>Adresse de base + chemin (« /salles?x=1 ») ; le chemin ne peut pas changer d'hôte.</summary>
    public static string Join(string baseUrl, string path)
    {
        var b = SafeHttp.ParseUrl(baseUrl).ToString();
        path = (path ?? "").Trim();
        if (path.Length == 0) return b;
        if (path.Contains("://") || path.StartsWith("//")) throw new InvalidOperationException("Le chemin doit être relatif à l'adresse de base (ex. /salles).");
        return b.TrimEnd('/') + "/" + path.TrimStart('/');
    }

    /// <summary>Teste une connexion : rend un message lisible, ou lève une exception avec la cause.</summary>
    public async Task<string> Test(Guid clientId, DataConnection c, CancellationToken ct = default)
    {
        if (c.Kind == DataKinds.Db)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));
            return (await Sql(clientId, c, "test", null, null, cts.Token)).Message ?? "Connexion réussie.";
        }
        if (c.Kind == DataKinds.Api && c.Auth == "login")   // seule la connexion est essayée : l'adresse de base seule ne rend pas forcément des données
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(c.Via == "relay" ? DataKinds.Timeout * 2 : DataKinds.Timeout);
            try { await Token(clientId, c, true, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("Délai dépassé : l'API de connexion n'a pas répondu à temps."); }
            return "Connexion réussie · l'API a rendu un jeton. Ajoutez maintenant un jeu de données pour lire un chemin de l'API.";
        }
        var raw = await ReadRaw(clientId, c, new DataSet(), ct);
        return c.Kind == DataKinds.Api
            ? $"Connexion réussie · la réponse contient {raw.Rows.Count} ligne{(raw.Rows.Count > 1 ? "s" : "")} et {raw.Headers.Count} colonne{(raw.Headers.Count > 1 ? "s" : "")}."
            : $"Fichier lu · {raw.Rows.Count} ligne{(raw.Rows.Count > 1 ? "s" : "")}, {raw.Headers.Count} colonne{(raw.Headers.Count > 1 ? "s" : "")}.";
    }

    /// <summary>Aperçu d'un jeu (rien n'est enregistré) : la table telle qu'elle serait, et la durée de lecture.</summary>
    public async Task<(DataTable Table, long Ms)> Preview(Guid clientId, DataConnection c, DataSet ds, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var raw = await ReadRaw(clientId, c, ds, ct);
        return (DataTables.Build(raw, ds, DateTime.UtcNow).Table, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Lit un jeu enregistré et garde sa table si la lecture est bonne. Une colonne utilisée qui a disparu, ou un échec, laissent la copie
    /// précédente en place (les écrans ne voient pas d'erreur). Deux lectures du même jeu ne se chevauchent pas. Rend la ligne d'historique.
    /// </summary>
    public async Task<DataReadLog?> Refresh(Guid clientId, Guid dataSetId, CancellationToken ct = default)
    {
        var found = store.Read(db => db.Clients.FirstOrDefault(t => t.Id == clientId) is { } t && t.DataSets.FirstOrDefault(d => d.Id == dataSetId) is { } ds
            && t.DataConnections.FirstOrDefault(c => c.Id == ds.ConnectionId) is { } c ? (c.Copy(), ds.Copy()) : ((DataConnection, DataSet)?)null);
        if (found is not { } f) return null;
        var gate = running.GetOrAdd((clientId, dataSetId), _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return null;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var now = DateTime.UtcNow;
            DataReadLog entry;
            try
            {
                var raw = await ReadRaw(clientId, f.Item1, f.Item2, ct);
                var (table, missing) = DataTables.Build(raw, f.Item2, now);
                var old = Table(clientId, dataSetId);
                if (missing.Count > 0 && old != null)
                    entry = new DataReadLog(now, "warn", $"Lu, mais {(missing.Count > 1 ? "des colonnes manquent" : "une colonne manque")} : {string.Join(", ", missing)}. Les écrans gardent la copie précédente.", table.Rows.Count, sw.ElapsedMilliseconds, false);
                else
                {
                    var changed = old?.Hash != table.Hash;
                    if (changed) Save(clientId, dataSetId, table);
                    else if (old != null) Save(clientId, dataSetId, old with { ReadUtc = now });   // inchangé : seule l'heure de la copie avance
                    entry = new DataReadLog(now, "ok", changed ? $"{table.Rows.Count} ligne{(table.Rows.Count > 1 ? "s" : "")}{(old == null ? "" : " · données modifiées")}" : "Inchangé", table.Rows.Count, sw.ElapsedMilliseconds, changed);
                    if (f.Item2.Columns.Count == 0 || missing.Count > 0) KeepColumns(clientId, dataSetId, table.Columns);   // première lecture : types détectés gardés
                }
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or IOException or FormatException or ArgumentException or CryptographicException)
            {
                entry = new DataReadLog(now, "bad", (e is HttpRequestException ? "Source injoignable : " : "") + e.Message + (Table(clientId, dataSetId) != null ? " Copie précédente conservée." : ""), 0, sw.ElapsedMilliseconds, false);
            }
            Append(clientId, dataSetId, entry);
            return entry;
        }
        finally { gate.Release(); }
    }

    private void KeepColumns(Guid clientId, Guid dataSetId, List<DataColumn> cols) => store.Write(db =>
    {
        if (db.Clients.FirstOrDefault(t => t.Id == clientId)?.DataSets.FirstOrDefault(d => d.Id == dataSetId) is { } ds)
            ds.Columns = DataColumn.Merge(cols, ds.Columns);
    });

    private void Save(Guid clientId, Guid dataSetId, DataTable t)
    {
        Directory.CreateDirectory(Dir(clientId));
        var tmp = TableFile(clientId, dataSetId) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(t, Json));
        File.Move(tmp, TableFile(clientId, dataSetId), true);
        tables[(clientId, dataSetId)] = t;
    }

    private void Append(Guid clientId, Guid dataSetId, DataReadLog e)
    {
        var l = Log(clientId, dataSetId);
        string json;
        lock (l)
        {
            // les lectures « Inchangé » qui se suivent ne remplissent pas l'historique : la dernière remplace la précédente
            if (l.Count > 0 && e.Level == "ok" && !e.Changed && l[^1].Level == "ok" && !l[^1].Changed) l[^1] = e; else l.Add(e);
            if (l.Count > HistoryMax) l.RemoveRange(0, l.Count - HistoryMax);
            json = JsonSerializer.Serialize(l, Json);
        }
        try { Directory.CreateDirectory(Dir(clientId)); File.WriteAllText(LogFile(clientId, dataSetId), json); }
        catch (IOException) { }
    }

    /// <summary>Un jeu supprimé : sa copie et son historique aussi.</summary>
    public void Forget(Guid clientId, Guid dataSetId)
    {
        tables.TryRemove((clientId, dataSetId), out _);
        logs.TryRemove((clientId, dataSetId), out _);
        try { File.Delete(TableFile(clientId, dataSetId)); File.Delete(LogFile(clientId, dataSetId)); } catch (IOException) { }
    }

    /// <summary>Enregistre un fichier importé pour une connexion (remplace le précédent). Rend le nom donné par Linkii.</summary>
    public static async Task<string> StoreFile(Guid clientId, Guid connectionId, string originalName, Stream content, CancellationToken ct)
    {
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        if (ext is not (".csv" or ".txt" or ".json" or ".xlsx" or ".tsv")) throw new InvalidOperationException("Formats acceptés : CSV, JSON ou Excel (.xlsx).");
        Directory.CreateDirectory(FilesDir(clientId));
        foreach (var old in Directory.EnumerateFiles(FilesDir(clientId), connectionId.ToString("N") + ".*")) File.Delete(old);
        var name = connectionId.ToString("N") + ext;
        await using (var f = File.Create(Path.Combine(FilesDir(clientId), name))) await content.CopyToAsync(f, ct);
        return name;
    }

    public static void DeleteFile(Guid clientId, string fileName)
    {
        if (fileName.Length == 0) return;
        try { File.Delete(Path.Combine(FilesDir(clientId), Path.GetFileName(fileName))); } catch (IOException) { }
    }
}

/// <summary>Actualisation des jeux de données des organisations au plan Pro : toutes les 20 secondes, les jeux arrivés à échéance sont relus, un par un.</summary>
public class DataSyncWorker(JsonStore store, DataSetService data, ILogger<DataSyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var due = store.Read(db => db.Clients.Where(t => t.IsGrowth && t.DataSets.Count > 0)
                    .SelectMany(t => t.DataSets.Select(ds => (t.Id, Ds: ds, C: t.DataConnections.FirstOrDefault(c => c.Id == ds.ConnectionId))))
                    .Where(x => x.C != null).Select(x => (x.Id, x.Ds.Id, C: x.C!.Copy(), Ds: x.Ds.Copy())).ToList())
                    .Where(x => data.Due(x.Item1, x.C, x.Ds, now)).Take(30).ToList();
                foreach (var (cid, dsid, _, _) in due)
                {
                    if (stop.IsCancellationRequested) break;
                    await data.Refresh(cid, dsid, stop);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { log.LogWarning(e, "Actualisation des jeux de données"); }
            try { await Task.Delay(TimeSpan.FromSeconds(20), stop); } catch (OperationCanceledException) { break; }
        }
    }
}

/// <summary>Opérations du back-office sur les connexions et les jeux (droits : administrateurs de l'organisation, plan Pro). Rendent l'erreur, ou null.</summary>
public static class DataSourceOps
{
    public static bool Allowed(Tenant t) => t.IsGrowth;

    public static string? CheckConnection(DataConnection c)
    {
        c.Name = c.Name.Trim();
        if (c.Name.Length == 0) return "Donnez un nom à la connexion.";
        if (c.Name.Length > 80) return "Le nom est trop long (80 caractères au plus).";
        if (c.Kind == DataKinds.Api)
        {
            try { SafeHttp.ParseUrl(c.BaseUrl); } catch (InvalidOperationException e) { return e.Message; }
            if (c.Auth is not ("none" or "key" or "bearer" or "basic" or "login")) c.Auth = "none";
            if (c.Auth == "basic" && c.User.Trim().Length == 0) return "Indiquez l'utilisateur de l'authentification Basic.";
            if (c.Auth == "login")
            {
                c.User = c.User.Trim(); c.LoginPath = (c.LoginPath ?? "").Trim(); c.TokenPath = (c.TokenPath ?? "").Trim();
                c.LoginUserField = string.IsNullOrWhiteSpace(c.LoginUserField) ? "username" : c.LoginUserField.Trim();
                c.LoginPassField = string.IsNullOrWhiteSpace(c.LoginPassField) ? "password" : c.LoginPassField.Trim();
                if (c.LoginFormat is not ("json" or "form")) c.LoginFormat = "json";
                if (c.User.Length == 0) return "Indiquez l'utilisateur de la connexion.";
                if (c.LoginPath.Length == 0 || c.LoginPath.Length > 300) return "Indiquez le chemin de connexion (ex. /auth/login).";
                try { DataSetService.Join(c.BaseUrl, c.LoginPath); } catch (InvalidOperationException e) { return e.Message.Replace("(ex. /salles)", "(ex. /auth/login)"); }
                if (!Regex.IsMatch(c.LoginUserField, @"^[\w.\-\[\]]{1,60}$") || !Regex.IsMatch(c.LoginPassField, @"^[\w.\-\[\]]{1,60}$")) return "Nom de champ invalide (lettres, chiffres, point, tiret et souligné).";
                if (c.TokenPath.Length > 200) return "Chemin du jeton trop long.";
                c.TokenHeader = string.IsNullOrWhiteSpace(c.TokenHeader) ? "Authorization" : c.TokenHeader.Trim();
                c.TokenPrefix = string.IsNullOrWhiteSpace(c.TokenPrefix) ? "" : c.TokenPrefix;
                if (!Regex.IsMatch(c.TokenHeader, "^[A-Za-z0-9-]{1,60}$") || c.TokenHeader.ToLowerInvariant() is "host" or "content-length" or "content-type" or "transfer-encoding" or "connection")
                    return "Nom d'en-tête invalide (lettres, chiffres et tirets).";
                if (!Regex.IsMatch(c.TokenPrefix, @"^[\x20-\x7E]{0,20}$")) return "Préfixe du jeton invalide (20 caractères au plus).";
                if (c.TokenPrefix.Trim().Length > 0 && !c.TokenPrefix.EndsWith(' ')) c.TokenPrefix = c.TokenPrefix.TrimStart() + " ";   // « Bearer » devient « Bearer  » : l'espace avant le jeton
            }
            if (c.Auth == "key" && !Regex.IsMatch(c.KeyHeader.Trim(), "^[A-Za-z0-9-]{1,60}$")) return "Nom d'en-tête invalide (lettres, chiffres et tirets).";
        }
        else if (c.Kind == DataKinds.Db)
        {
            if (!DataSql.Engines.Any(e => e.Id == c.Engine)) return "Choisissez le moteur de la base.";
            c.Host = c.Host.Trim(); c.Database = c.Database.Trim(); c.User = c.User.Trim();
            if (!Regex.IsMatch(c.Host, @"^[A-Za-z0-9.\-_:\[\]]{1,200}(\\[A-Za-z0-9_$-]{1,60})?$")) return "Adresse du serveur invalide (nom ou adresse IP, instance SQL Server après une barre oblique inverse).";
            if (c.Port is < 0 or > 65535) return "Port invalide.";
            if (c.Database.Length == 0 && c.Engine != DataSql.SqlServer) return "Indiquez le nom de la base.";
            if (c.DbWindows && (c.Engine != DataSql.SqlServer || c.Via != "relay")) c.DbWindows = false;   // Windows : SQL Server par le relais seulement
            if (!c.DbWindows && c.User.Length == 0) return "Indiquez l'utilisateur de la base.";
            c.Auth = "none";
        }
        else if (c.Kind == DataKinds.File)
        {
            if (c.FileMode == "url") { try { SafeHttp.ParseUrl(c.FileUrl); } catch (InvalidOperationException e) { return e.Message; } }
            else if (c.FileName.Length == 0) return "Importez un fichier CSV, JSON ou Excel.";
        }
        else return "Type de connexion inconnu.";
        if (c.Kind == DataKinds.File && c.FileMode == "upload") c.Via = "direct";   // fichier importé : déjà sur le serveur
        if (c.Via != "relay") { c.Via = "direct"; c.RelayId = null; }
        else if (c.RelayId == null) return "Choisissez le relais qui lit cette source.";
        return null;
    }

    public static string? CheckDataSet(DataSet ds, IEnumerable<DataSet> others)
    {
        ds.Name = ds.Name.Trim();
        if (ds.Name.Length == 0) return "Donnez un nom au jeu de données.";
        if (ds.Name.Length > 80) return "Le nom est trop long (80 caractères au plus).";
        if (others.Any(o => o.Id != ds.Id && o.Name.Equals(ds.Name, StringComparison.CurrentCultureIgnoreCase))) return "Un jeu de données porte déjà ce nom.";
        if (!DataKinds.Refresh.Any(r => r.Min == ds.RefreshMin)) ds.RefreshMin = 5;
        ds.MaxRows = Math.Clamp(ds.MaxRows, 0, DataKinds.MaxRows);
        ds.StaleHours = Math.Clamp(ds.StaleHours, 0, 720);
        ds.Hide = ds.Hide.Where(f => f.Key.Length > 0 && DataKinds.Ops.Any(o => o.Id == f.Op)).Take(10).ToList();
        if (ds.Path.Length > 500 || ds.RowsPath.Length > 200) return "Chemin trop long.";
        ds.Sql = ds.Sql.Trim();
        if (ds.Sql.Length > 0 && DataSql.Check(ds.Sql) is { } bad) return bad;
        return null;
    }

    /// <summary>Compositions qui utilisent un jeu (document qui cite son identifiant).</summary>
    public static List<MediaItem> UsedBy(ClientDb d, Guid dataSetId)
    {
        var key = dataSetId.ToString("N");
        return d.Root.Media.Where(m => m.ClientId == d.ClientId && m.AppId == Compositions.AppId && (m.Settings.GetValueOrDefault("doc") ?? "").Contains(key)).ToList();
    }
}
