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

    readonly Guid screen = Guid.NewGuid(), list = Guid.NewGuid(), comp = Guid.NewGuid();
    readonly object ctx;

    public AiAssistantTests()
    {
        ctx = Activator.CreateInstance(CtxType,
            new Dictionary<string, Guid> { ["S1"] = screen }, new Dictionary<string, Guid> { ["L1"] = list }, new Dictionary<string, List<Guid>> { ["L1"] = new() },
            new Dictionary<string, Guid> { ["C1"] = comp }, new Dictionary<string, Guid>(), new Dictionary<string, Guid>(),
            new Dictionary<Guid, string> { [screen] = "Ecran Com", [list] = "Ma Liste", [comp] = "Chiffres clés" }, "")!;
    }

    /// <summary>Comme <c>Ask</c> : les actions de la réponse, construites dans l'ordre.</summary>
    List<AiAction> Build(string json) =>
        JsonNode.Parse(json)!.AsArray().OfType<JsonObject>().SelectMany(x => (IEnumerable<AiAction>)BuildM.Invoke(null, new object[] { x, ctx })!).ToList();

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
