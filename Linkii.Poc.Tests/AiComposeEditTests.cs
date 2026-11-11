using System.Text.Json.Nodes;
using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>
/// L'assistant modifie une composition (composition_edit) : la réponse de l'IA est fusionnée avec le document.
/// Un élément gardé ne perd aucun champ que l'IA ne renvoie pas (données reliées, réglages de l'éditeur), un élément absent est supprimé,
/// les images ne viennent que des repères connus, et une réponse vide ne vide pas la page.
/// </summary>
public class AiComposeEditTests
{
    static readonly Guid Lake = Guid.NewGuid(), Other = Guid.NewGuid(), Source = Guid.NewGuid();

    static JsonObject Doc() => JsonNode.Parse($$"""
        {"v":1,"w":1920,"h":1080,"bg":"#FFFFFF","els":[
          {"id":"t1","type":"text","x":100,"y":100,"w":800,"h":120,"text":"Bienvenue","font":"Poppins","size":80,"color":"#0B1F3A","ls":2},
          {"id":"i1","type":"image","media":"{{Lake}}","x":1000,"y":0,"w":920,"h":1080,"fit":"cover"},
          {"id":"l1","type":"list","source":"{{Source}}","x":100,"y":400,"w":800,"h":500,"cols":[{"key":"nom"}]},
          {"id":"s1","type":"shape","kind":"rect","x":0,"y":1000,"w":1920,"h":80,"fill":"#3B82C4"}]}
        """)!.AsObject();

    static readonly Dictionary<string, Guid> Refs = new() { ["I1"] = Lake, ["I2"] = Other };

    static JsonObject Answer(string json) => JsonNode.Parse(json)!.AsObject();
    static JsonObject El(JsonObject doc, string id) => doc["els"]!.AsArray().OfType<JsonObject>().First(e => (string?)e["id"] == id);

    [Fact]
    public void Champs_non_renvoyes_gardes_et_element_absent_supprime()
    {
        var m = AiCompose.Merge(Doc(), Answer("""
            {"bg":"#0B1F3A","els":[{"id":"t1","color":"#FFFFFF","size":96},{"id":"i1"},{"id":"l1","x":120}]}
            """), Refs, null)!;
        Assert.Equal("#0B1F3A", (string?)m["bg"]);
        var t = El(m, "t1");
        Assert.Equal("#FFFFFF", (string?)t["color"]);
        Assert.Equal(96, (int)t["size"]!);
        Assert.Equal("Poppins", (string?)t["font"]);   // gardé
        Assert.Equal(2, (int)t["ls"]!);                // réglage de l'éditeur gardé
        var l = El(m, "l1");
        Assert.Equal(120, (int)l["x"]!);
        Assert.Equal(Source.ToString(), (string?)l["source"]);   // liste reliée : sa source est gardée
        Assert.NotNull(l["cols"]);
        Assert.Equal(3, m["els"]!.AsArray().Count);   // la forme s1, absente de la réponse, est supprimée
        Assert.NotNull(Compositions.Clean(m.ToJsonString()));
    }

    [Fact]
    public void Ordre_de_la_reponse_et_nouveaux_elements()
    {
        var m = AiCompose.Merge(Doc(), Answer("""
            {"els":[{"id":"s1"},{"id":"i1"},{"id":"t1"},{"id":"l1"},
              {"type":"widget","kind":"clock","x":1600,"y":980,"w":280,"h":90,"color":"#FFFFFF"},
              {"type":"text","x":100,"y":900,"w":600,"h":60,"text":"Bonne journée","font":"Comic Sans"},
              {"type":"text","x":0,"y":0,"w":10,"h":10,"text":""},
              {"type":"app","app":"rss","source":"x"},
              {"type":"list","source":"x"}]}
            """), Refs, null)!;
        var ids = m["els"]!.AsArray().Select(e => (string?)e!["id"]).ToList();
        Assert.Equal(new[] { "s1", "i1", "t1", "l1" }, ids.Take(4));   // ordre de dessin de la réponse
        Assert.Equal(6, ids.Count);   // texte vide, bloc de données et liste nouveaux écartés
        Assert.Equal("clock", (string?)m["els"]![4]!["kind"]);
        Assert.Equal("Montserrat", (string?)m["els"]![5]!["font"]);   // police inconnue remplacée
        Assert.All(ids, id => Assert.False(string.IsNullOrEmpty(id)));
    }

    [Fact]
    public void Images_seulement_par_repere_connu_ou_photo_choisie()
    {
        var photo = Guid.NewGuid();
        var m = AiCompose.Merge(Doc(), Answer($$"""
            {"els":[{"id":"t1"},{"id":"i1","media":"I2"},
              {"type":"image","media":"I1","x":0,"y":0,"w":400,"h":300},
              {"type":"image","media":"{{Guid.NewGuid()}}","x":0,"y":0,"w":400,"h":300},
              {"type":"image","slot":"photo","x":0,"y":0,"w":400,"h":300}]}
            """), Refs, photo)!;
        var imgs = m["els"]!.AsArray().OfType<JsonObject>().Where(e => (string?)e["type"] == "image").Select(e => (string?)e["media"]).ToList();
        Assert.Equal(new[] { Other.ToString(), Lake.ToString(), photo.ToString() }, imgs);   // identifiant inventé écarté
    }

    [Fact]
    public void Repere_inconnu_garde_l_image_et_passage_en_portrait()
    {
        var m = AiCompose.Merge(Doc(), Answer("""{"w":1080,"h":1920,"els":[{"id":"i1","media":"I9","y":900},{"id":"t1"}]}"""), Refs, null)!;
        Assert.Equal(1080, (int)m["w"]!);
        Assert.Equal(1920, (int)m["h"]!);
        Assert.Equal(Lake.ToString(), (string?)El(m, "i1")["media"]);
        Assert.Equal(900, (int)El(m, "i1")["y"]!);
    }

    [Fact]
    public void Reponse_vide_ne_vide_pas_la_page()
    {
        Assert.Null(AiCompose.Merge(Doc(), Answer("""{"els":[]}"""), Refs, null));
        Assert.Null(AiCompose.Merge(Doc(), Answer("""{"say":"oups"}"""), Refs, null));
        // format refusé (trop petit) : on garde l'ancien
        var m = AiCompose.Merge(Doc(), Answer("""{"w":10,"h":10,"els":[{"id":"t1"}]}"""), Refs, null)!;
        Assert.Equal(1920, (int)m["w"]!);
    }
}
