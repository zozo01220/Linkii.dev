using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Intégrations personnalisées (§139) : lecture des formats, types, filtres, et jeux de données dans les compositions publiées.</summary>
public class DataSourcesTests
{
    [Fact]
    public void Csv_detecte_le_separateur_et_garde_les_guillemets()
    {
        var raw = DataTables.Csv("Salle;Statut;Note\r\nLéman;Libre;\"Grande; avec \"\"vue\"\"\"\r\nJura;Occupée;\r\n\r\n", null);
        Assert.Equal(new[] { "Salle", "Statut", "Note" }, raw.Headers);
        Assert.Equal(2, raw.Rows.Count);
        Assert.Equal("Grande; avec \"vue\"", raw.Rows[0][2]);
        Assert.Equal("", raw.Rows[1][2]);
    }

    [Fact]
    public void Json_trouve_les_lignes_seul_ou_par_chemin_et_aplatit_les_objets()
    {
        const string json = """{ "meta": { "n": 2 }, "data": { "items": [ { "nom": "A", "lieu": { "etage": 2 }, "tags": ["x", "y"] }, { "nom": "B", "ok": true } ] } }""";
        var auto = DataTables.Json(json, null);
        Assert.Equal(new[] { "nom", "lieu.etage", "tags", "ok" }, auto.Headers);
        Assert.Equal(new string?[] { "A", "2", "x, y", null }, auto.Rows[0]);
        var path = DataTables.Json(json, "data.items[*]");
        Assert.Equal(2, path.Rows.Count);
        Assert.Throws<InvalidOperationException>(() => DataTables.Json(json, "data.rien"));
    }

    [Fact]
    public void Excel_est_lu_avec_textes_partages_et_dates()
    {
        var raw = DataTables.Parse(Xlsx(), null, null, null);
        Assert.Equal(new[] { "Salle", "Places", "Date" }, raw.Headers);
        Assert.Equal(new string?[] { "Léman", "12", "2026-10-29" }, raw.Rows[0]);
    }

    [Theory]
    [InlineData("1 234,5", "1234.5")]
    [InlineData("1'234.50", "1234.5")]
    [InlineData("-3", "-3")]
    [InlineData("12,000", "12")]
    [InlineData("abc", null)]
    public void Nombres_normalises(string v, string? expected) => Assert.Equal(expected, DataTables.Number(v));

    [Theory]
    [InlineData("29.10.2026", "2026-10-29")]
    [InlineData("2026-10-29 14:00", "2026-10-29T14:00:00")]
    [InlineData("2026-10-29T14:00:00Z", "2026-10-29T14:00:00+00:00")]
    [InlineData("14:00", null)]
    public void Dates_normalisees(string v, string? expected) => Assert.Equal(expected, DataTables.Date(v));

    [Fact]
    public void Construction_types_detectes_lignes_masquees_et_colonnes_disparues()
    {
        var raw = new RawTable(new() { "Prochaine réunion", "Places", "Statut", "Statut" },
            new() { new() { "29.10.2026 14:00", "12", "Libre", "a" }, new() { "", "8", "Fermée", "b" }, new() { "30.10.2026 09:00", "1 000", "Occupée", "c" } });
        var ds = new DataSet { Hide = { new DataFilter { Key = "statut", Op = "eq", Value = "fermée" } }, Columns = { new DataColumn { Key = "ancienne", Label = "Ancienne" } } };
        var (t, missing) = DataTables.Build(raw, ds, DateTime.UtcNow);
        Assert.Equal(new[] { "prochaine_reunion", "places", "statut", "statut_2" }, t.Columns.Select(c => c.Key));
        Assert.Equal(new[] { "date", "number", "text", "text" }, t.Columns.Select(c => c.Type));
        Assert.Equal(2, t.Rows.Count);   // « Fermée » masquée (sans casse)
        Assert.Equal("1000", t.Rows[1][1]);
        Assert.Equal(new[] { "Ancienne" }, missing);
        Assert.Equal(t.Hash, DataTables.Hash(t.Columns, t.Rows));
    }

    [Fact]
    public void Ecran_recoit_seulement_les_colonnes_utilisees()
    {
        var t = new DataTable(new() { new() { Key = "a" }, new() { Key = "b" }, new() { Key = "c" } }, new() { new() { "1", "2", "3" } }, DateTime.UtcNow, "h");
        var json = JsonSerializer.Serialize(DataTables.ForScreen(t, new[] { "c", "a" }, false));
        Assert.Contains("\"rows\":[[\"1\",\"3\"]]", json);
        Assert.DoesNotContain("\"b\"", json);
    }

    [Fact]
    public void Chemin_d_api_reste_sur_l_adresse_de_base()
    {
        Assert.Equal("https://api.exemple.ch/v2/salles?x=1", DataSetService.Join("https://api.exemple.ch/v2/", "/salles?x=1"));
        Assert.Throws<InvalidOperationException>(() => DataSetService.Join("https://api.exemple.ch", "https://ailleurs.ch/x"));
    }

    [Fact]
    public void Composition_publiee_garde_ses_listes_et_les_colonnes_utilisees()
    {
        var set = new DataSet { Id = Guid.NewGuid(), Name = "Salles" };
        var gone = Guid.NewGuid();
        var n = set.Id.ToString("N");
        var doc = JsonSerializer.Serialize(new
        {
            v = 1, w = 1920, h = 1080, bg = "#0B1F3A",
            els = new object[]
            {
                new { id = "e1", type = "list", source = n, cols = new[] { new { key = "salle" }, new { key = "statut" } }, sort = new { key = "quand", dir = "asc" },
                      filter = new[] { new { key = "etage", op = "eq", val = "@p.etage" } } },
                new { id = "e2", type = "list", source = gone.ToString("N"), cols = new[] { new { key = "x" } } },
                new { id = "e3", type = "text", text = "{{" + n + ":sum.places}} places · {{" + n + ":_count}} salles" }
            }
        });
        var r = Compositions.Publish(new Dictionary<string, string> { ["doc"] = doc }, _ => null, _ => null, id => id == set.Id ? set : null)!.Value;
        var els = JsonNode.Parse(r.Settings["doc"])!["els"]!.AsArray();
        Assert.Equal(new[] { "e1", "e3" }, els.Select(e => (string)e!["id"]!));   // jeu introuvable : sa liste n'est pas diffusée
        Assert.Equal(new[] { "salle", "statut", "etage", "quand", "places" }, JsonSerializer.Deserialize<List<string>>(r.Settings["ds:" + n]));
        Assert.Equal("1", r.Settings["scr"]);   // le filtre cite l'écran : « Cet écran » est servi
        var pub = new PublishedItem { AppId = Compositions.AppId, Settings = r.Settings };
        Assert.NotNull(Compositions.DataRef(pub, set.Id));
        Assert.Null(Compositions.DataRef(pub, gone));
        Assert.True(Compositions.UsesScreen(pub, Compositions.ScreenSource));
    }

    [Fact]
    public void Donnees_de_cet_ecran_avec_ses_parametres()
    {
        var area = new Area { Name = "Bâtiment A" };
        var zone = new Zone { Name = "Hall" };
        area.Zones.Add(zone);
        var db = new Db();
        db.Areas.Add(area);
        var s = new Screen { Name = "Hall étage 2", AreaId = area.Id, ZoneId = zone.Id, Params = { new ScreenParam { Name = "Étage", Value = "2" } } };
        var json = JsonSerializer.Serialize(Compositions.ScreenData(db, s));
        Assert.Contains("\"params\":{\"etage\":\"2\"}", json);
        Assert.Contains("\"zone\":\"Hall\"", json);
        Assert.Contains("\"labels\":{\"etage\":\"\\u00C9tage\"}", json);
    }

    /// <summary>Classeur minimal : une feuille, textes partagés, une date (style 14).</summary>
    private static byte[] Xlsx()
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string xml) { using var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false)); w.Write(xml); }
            const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Add("xl/workbook.xml", $"""<workbook xmlns="{ns}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Feuil1" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/></Relationships>""");
            Add("xl/sharedStrings.xml", $"""<sst xmlns="{ns}"><si><t>Salle</t></si><si><t>Places</t></si><si><t>Date</t></si><si><t>Léman</t></si></sst>""");
            Add("xl/styles.xml", $"""<styleSheet xmlns="{ns}"><cellXfs><xf numFmtId="0"/><xf numFmtId="14"/></cellXfs></styleSheet>""");
            Add("xl/worksheets/sheet1.xml", $"""<worksheet xmlns="{ns}"><sheetData><row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c></row><row r="2"><c r="A2" t="s"><v>3</v></c><c r="B2"><v>12</v></c><c r="C2" s="1"><v>46324</v></c></row></sheetData></worksheet>""");
        }
        return ms.ToArray();
    }
}
