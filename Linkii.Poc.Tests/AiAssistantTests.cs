using System.Reflection;
using System.Text.Json.Nodes;
using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>
/// Assistant : une liste ou un calendrier créé dans la réponse se vise, dans les actions suivantes, par le repère « ref » que l'IA lui donne
/// (ou par le numéro suivant). Avant, ces actions étaient écartées en silence : seule la liste vide était créée.
/// </summary>
public class AiAssistantTests
{
    static readonly Type CtxType = typeof(AiAssistant).GetNestedType("Ctx", BindingFlags.NonPublic)!;
    static readonly MethodInfo BuildM = typeof(AiAssistant).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static)!;

    readonly Guid screen = Guid.NewGuid(), list = Guid.NewGuid(), comp = Guid.NewGuid(), photo = Guid.NewGuid();
    readonly object ctx;

    public AiAssistantTests() => ctx = Ctx(web: true);

    object Ctx(bool web) => Activator.CreateInstance(CtxType,
        new Dictionary<string, Guid> { ["S1"] = screen }, new Dictionary<string, Guid> { ["L1"] = list }, new Dictionary<string, List<Guid>> { ["L1"] = new() },
        new Dictionary<string, Guid> { ["C1"] = comp }, new Dictionary<string, Guid> { ["M1"] = photo }, new Dictionary<string, Guid>(),
        new Dictionary<Guid, string> { [screen] = "Ecran Com", [list] = "Ma Liste", [comp] = "Chiffres clés", [photo] = "Lac au matin" }, "", web)!;

    /// <summary>Comme <c>Ask</c> : les actions de la réponse, construites dans l'ordre.</summary>
    List<AiAction> Build(string json, object? context = null) =>
        JsonNode.Parse(json)!.AsArray().OfType<JsonObject>().SelectMany(x => (IEnumerable<AiAction>)BuildM.Invoke(null, new object[] { x, context ?? ctx })!).ToList();

    [Fact]
    public void Une_composition_peut_porter_une_image_de_la_mediatheque_ou_d_internet()
    {
        var acts = Build("""
            [{"op":"composition_create","sentence":"Bienvenue au bord du lac","photo":"M1"},
             {"op":"composition_create","sentence":"Fermé lundi"},
             {"op":"composition_create","sentence":"Café offert","photo":"web"},
             {"op":"composition_create","sentence":"Texte seul","photo":null},
             {"op":"web_image","query":"tasse de café"}]
            """);
        Assert.Equal(photo.ToString(), acts[0].A["photo"]);
        Assert.Contains("avec l'image « Lac au matin »", acts[0].Label);
        Assert.Equal("auto", acts[1].A["photo"]);   // rien dit : une image si l'une convient
        Assert.Equal("web", acts[2].A["photo"]);
        Assert.Equal("", acts[3].A["photo"]);
        Assert.Equal("web_image", acts[4].Op);
        Assert.Equal("tasse de café", acts[4].A["query"]);

        // organisation sans images d'internet : « web » devient « sans image » et la recherche n'est pas proposée
        var off = Build("""[{"op":"composition_create","sentence":"Café offert","photo":"web"},{"op":"web_image","query":"café"}]""", Ctx(web: false));
        Assert.Single(off);
        Assert.Equal("", off[0].A["photo"]);
    }

    [Fact]
    public void Images_d_internet_seules_les_photos_impersonnelles_sont_gardees()
    {
        Assert.True(WebImages.Impersonal("Cup of coffee on a wooden table"));
        Assert.True(WebImages.Impersonal("Snowy mountain lake at sunrise"));
        Assert.False(WebImages.Impersonal("Woman drinking coffee in a cafe"));
        Assert.False(WebImages.Impersonal("Group of people at a meeting"));
        Assert.False(WebImages.Impersonal("Portrait of a smiling child"));
        Assert.False(WebImages.Impersonal(""));   // sans description : on ne sait pas ce qu'elle montre
        Assert.True(WebImages.Impersonal("Steam rising over a surface of water"));   // « surface » n'est pas « face »

        var found = WebImages.Parse("""
            {"page":1,"per_page":2,"total_results":2,"photos":[
              {"id":123,"width":6000,"height":4000,"url":"https://www.pexels.com/photo/cafe-123/","photographer":"Jeanne Martin","alt":"Cup of coffee",
               "src":{"original":"https://images.pexels.com/photos/123/a.jpeg","large2x":"https://images.pexels.com/photos/123/a.jpeg?w=1880","medium":"https://images.pexels.com/photos/123/a.jpeg?h=350"}},
              {"id":124,"photographer":"X","alt":"No file","src":{}}]}
            """);
        var one = Assert.Single(found);
        Assert.Equal("123", one.Id);
        Assert.Equal("https://images.pexels.com/photos/123/a.jpeg?w=1880", one.File);
        Assert.Equal("Jeanne Martin", one.Author);
        Assert.Equal("https://www.pexels.com/photo/cafe-123/", one.Page);
    }

    [Fact]
    public void Les_images_proposees_a_l_ia_sont_celles_qui_parlent_du_sujet()
    {
        MediaItem Img(string name, string? desc = null, params string[] tags) => new() { Name = name, Type = "image", FileName = name + ".jpg", AiDesc = desc, AiTags = tags.ToList(), AiDone = desc != null, AddedUtc = DateTime.UtcNow };
        var media = new[]
        {
            Img("IMG_0042", "Une tasse de café fumante sur une table", "café", "tasse"),
            Img("Lac au matin", "Un lac de montagne", "lac", "montagne"),
            Img("Café du hall"),
            new MediaItem { Name = "Café (vidéo)", Type = "video", FileName = "c.mp4" },
        };
        var c = AiCompose.Candidates(media, "Café offert dans le hall");
        Assert.Equal(new[] { "Café du hall", "IMG_0042" }, c.Select(x => media.First(m => m.Id == x.Id).Name));   // deux mots d'abord, puis le café décrit par l'IA ; l'IA juge ensuite
        Assert.Contains("tasse de café", c[1].Text);
        Assert.Empty(AiCompose.Candidates(media, "Fermé le lundi"));
    }

    [Fact]
    public void Programmer_une_composition_sur_un_ecran_garde_tout_l_enchainement()
    {
        var acts = Build("""
            [{"op":"list_create","name":"Chiffres clés","ref":"NL1"},
             {"op":"list_add","list":"NL1","content":"C1"},
             {"op":"calendar_create","name":"Planning Ecran Com","list":"L1","ref":"NK1"},
             {"op":"calendar_event","calendar":"NK1","kind":"play","list":"NL1","from":"09:00","to":"11:00"},
             {"op":"calendar_assign","screens":["S1"],"calendar":"NK1"}]
            """);

        Assert.Equal(new[] { "list_create", "list_add", "calendar_create", "calendar_event", "calendar_assign" }, acts.Select(a => a.Op));
        var newList = acts[0].A["id"]; var newCal = acts[2].A["id"];
        Assert.True(Guid.TryParse(newList, out _) && Guid.TryParse(newCal, out _));
        Assert.Equal(newList, acts[1].A["l"]);
        Assert.Equal(comp.ToString(), acts[1].A["content"]);
        Assert.Equal(list.ToString(), acts[2].A["l"]);
        Assert.Equal(newCal, acts[3].A["k"]);
        Assert.Equal(newList, acts[3].A["l"]);
        Assert.Equal(newCal, acts[4].A["k"]);
        Assert.Equal(screen.ToString(), acts[4].A["s"]);
        Assert.Contains("« Chiffres clés »", acts[1].Label);
        Assert.Contains("Planning Ecran Com", acts[4].Label);
    }

    [Fact]
    public void Le_numero_suivant_vise_aussi_la_liste_creee()
    {
        var acts = Build("""
            [{"op":"list_create","name":"Chiffres clés"},
             {"op":"list_add","list":"L2","content":"C1"}]
            """);
        Assert.Equal(2, acts.Count);
        Assert.Equal(acts[0].A["id"], acts[1].A["l"]);
    }

    [Fact]
    public void Suivi_lit_la_phrase_et_les_actions_d_une_reponse_incomplete()
    {
        Assert.Equal("", AiAssistant.PartialSay("{\"sa"));
        Assert.Equal("Je cr", AiAssistant.PartialSay("{\"say\": \"Je cr"));
        Assert.Equal("Je crée « la liste »\nensuite", AiAssistant.PartialSay("{\"say\":\"Je cr\\u00e9e \\u00ab la liste \\u00bb\\nensuite\",\"actions\":["));
        Assert.Equal("Il dit \"oui\"", AiAssistant.PartialSay("{\"say\":\"Il dit \\\"oui\\\"\",\"actions\":[]}"));
        Assert.Equal("Coupé ", AiAssistant.PartialSay("{\"say\":\"Coupé \\u00"));   // échappement pas encore arrivé
        Assert.Equal("Fin", AiAssistant.PartialSay("{\"say\":\"Fin\\"));

        var ops = AiAssistant.OpsSoFar("""{"say":"ok","actions":[{"op":"list_create","name":"A","ref":"NL1"},{"op":"list_add","list":"NL1"},{"op":"calendar_cre""");
        Assert.Equal(new[] { "Créer une liste", "Ajouter à une liste" }, ops);   // la troisième n'est pas encore complète
        Assert.Empty(AiAssistant.OpsSoFar("""{"actions":[{"op":"inconnue"}]}"""));
    }

    [Fact]
    public void Un_repere_inconnu_reste_ecarte_et_un_repere_existant_n_est_pas_vole()
    {
        var acts = Build("""
            [{"op":"list_add","list":"NL9","content":"C1"},
             {"op":"list_create","name":"Autre","ref":"S1"},
             {"op":"calendar_assign","screens":["S1"],"calendar":"NK1"}]
            """);
        Assert.Single(acts);
        Assert.Equal("list_create", acts[0].Op);
    }
}
