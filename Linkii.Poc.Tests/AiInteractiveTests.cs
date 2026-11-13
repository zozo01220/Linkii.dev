using System.Text.Json;
using System.Text.Json.Nodes;
using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>
/// L'assistant crée ou modifie une liste interactive (ilist_create, ilist_edit) : la réponse de l'IA est fusionnée avec la liste.
/// Ce que l'IA ne renvoie pas est gardé (y compris le code d'un QR), ce qui est absent est supprimé, images, contenus et pages ne viennent que
/// de repères connus, et le résultat passe le contrôle de l'éditeur (<see cref="InteractiveLists.Apply"/>).
/// </summary>
public class AiInteractiveTests
{
    static readonly Guid Logo = Guid.NewGuid(), Plan = Guid.NewGuid(), Menu = Guid.NewGuid();
    static readonly IListCatalog Cat = new(new[] { (Logo, "Logo") }, new[] { (Plan, "Plan du site · PDF"), (Menu, "Menu du jour · composition") }, true);

    static readonly MediaItem[] Media =
    {
        new() { Id = Logo, Type = "image", Name = "Logo" }, new() { Id = Plan, Type = "app", AppId = "pdf", Name = "Plan du site" },
        new() { Id = Menu, Type = "app", AppId = "compositions", Name = "Menu du jour" },
    };

    static InteractiveList List()
    {
        var home = new KioskPage { Name = "Accueil" };
        var sub = new KioskPage { Name = "Services", Bg = "#16335C", Len = 2 };
        home.Els.Add(new KioskEl { Kind = "text", X = 5, Y = 4, W = 90, H = 7, Text = "Bienvenue", FontSize = 6, Bold = true });
        home.Els.Add(new KioskEl
        {
            Kind = "tile", X = 5, Y = 30, W = 43.5, H = 24, Text = "Plan", Color = "#2E7D5B", Font = "georgia",
            Action = new KioskAction { Type = "media", MediaId = Plan, Item = new PlaylistItem { MediaId = Plan, QrOn = true, QrUrl = "https://exemple.ch/plan", QrCode = "abcdefghij12" } },
        });
        home.Els.Add(new KioskEl { Kind = "tile", X = 51.5, Y = 30, W = 43.5, H = 24, Text = "Services", Action = new KioskAction { Type = "page", PageId = sub.Id } });
        sub.Els.Add(new KioskEl { Kind = "image", X = 30, Y = 5, W = 40, H = 12, ImageId = Logo });
        return new InteractiveList { Name = "Accueil mairie", Orientation = "portrait", Pages = { home, sub } };
    }

    static JsonObject Answer(string json) => JsonNode.Parse(json)!.AsObject();

    static string? Apply(InteractiveList l, AiInteractive.Merged m) =>
        InteractiveLists.Apply(l, l.Name, JsonSerializer.Serialize(new { orientation = m.Orientation, pages = m.Pages }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            id => Media.FirstOrDefault(x => x.Id == id), true, "https://app.exemple.ch/");

    [Fact]
    public void La_liste_montree_a_l_IA_n_a_que_des_reperes()
    {
        var l = List();
        var shown = AiInteractive.Show(l, AiInteractive.Refs.Of(l, Cat)).ToJsonString();
        Assert.DoesNotContain(Plan.ToString(), shown);
        Assert.DoesNotContain(l.Pages[1].Id.ToString(), shown);
        Assert.Contains("\"content\":\"O1\"", shown);
        Assert.Contains("\"page\":\"P2\"", shown);
        Assert.Contains("\"image\":\"I1\"", shown);
        Assert.Contains("\"id\":\"E4\"", shown);
    }

    [Fact]
    public void Champs_non_renvoyes_gardes_elements_absents_supprimes()
    {
        var l = List();
        var refs = AiInteractive.Refs.Of(l, Cat);
        var m = AiInteractive.Merge(l, Answer("""
            {"pages":[{"id":"P1","bg":"#101822","els":[{"id":"E2","color":"#D0453C","x":10},{"id":"E3"}]},{"id":"P2","els":[{"id":"E4"}]}]}
            """), refs)!;
        Assert.Equal(0, m.Loose);
        var home = m.Pages[0];
        Assert.Equal(l.Pages[0].Id, home.Id);
        Assert.Equal("Accueil", home.Name);            // nom gardé
        Assert.Equal("#101822", home.Bg);
        Assert.Equal(2, home.Els.Count);               // le titre E1, absent, est supprimé
        var plan = home.Els[0];
        Assert.Equal(l.Pages[0].Els[1].Id, plan.Id);
        Assert.Equal("#D0453C", plan.Color);
        Assert.Equal(10, plan.X);
        Assert.Equal("georgia", plan.Font);            // gardé
        Assert.Equal("abcdefghij12", plan.Action.Item.QrCode);   // le QR affiché reste valable
        Assert.True(plan.Action.Item.QrOn);
        Assert.Equal(2, m.Pages[1].Len);
        Assert.Null(Apply(l, m));
        Assert.Equal("abcdefghij12", l.Pages[0].Els[0].Action.Item.QrCode);
    }

    [Fact]
    public void Nouvelle_page_reliee_boutons_et_page_d_accueil_changee()
    {
        var l = List();
        var refs = AiInteractive.Refs.Of(l, Cat);
        var m = AiInteractive.Merge(l, Answer("""
            {"pages":[
              {"id":"P2","els":[{"id":"E4"},{"kind":"tile","x":5,"y":40,"w":90,"h":14,"text":"Horaires","action":{"type":"page","page":"N1"}}]},
              {"id":"P1","els":[{"id":"E1"},{"id":"E2","action":{"type":"media","content":"O2","qr":null}},{"id":"E3"}]},
              {"id":"N1","name":"Horaires","bg":"#FFFFFF","bgImage":"I1","len":9,"els":[
                {"kind":"text","x":5,"y":4,"w":90,"h":7,"text":"Horaires","textColor":"#0B1F3A","size":6,"bold":true},
                {"kind":"tile","x":25,"y":84,"w":50,"h":5.6,"text":"Accueil","align":"center","valign":"middle","radius":6,"action":{"type":"home"}},
                {"kind":"tile","x":25,"y":70,"w":50,"h":5.6,"text":"Inconnu","action":{"type":"media","content":"O9"}},
                {"kind":"image","x":0,"y":0,"w":10,"h":10,"image":"I7"}]}]}
            """), refs)!;
        Assert.Equal(l.Pages[1].Id, m.Pages[0].Id);    // « Services » devient l'accueil
        var created = m.Pages[2];
        Assert.Equal("Horaires", created.Name);
        Assert.Equal(Logo, created.BgImageId);
        Assert.Equal(InteractiveLists.MaxLen, created.Len);
        Assert.Equal(3, created.Els.Count);            // l'image au repère inconnu n'est pas posée
        Assert.Equal("home", created.Els[1].Action.Type);
        Assert.Equal("none", created.Els[2].Action.Type);   // contenu inconnu : la tuile ne fait rien
        Assert.Equal(1, m.Loose);
        Assert.Equal(created.Id, m.Pages[0].Els[1].Action.PageId);   // la tuile mène à la page créée dans la même réponse
        var plan = m.Pages[1].Els[1];
        Assert.Equal(Menu, plan.Action.MediaId);       // autre contenu : nouveau compteur, sans QR
        Assert.False(plan.Action.Item.QrOn);
        Assert.Null(plan.Action.Item.QrCode);
        Assert.Null(Apply(l, m));
        Assert.Equal(3, l.Pages.Count);
    }

    [Fact]
    public void Page_supprimee_les_tuiles_qui_y_menaient_ne_font_plus_rien_et_copie_d_element()
    {
        var l = List();
        var refs = AiInteractive.Refs.Of(l, Cat);
        var m = AiInteractive.Merge(l, Answer("""
            {"orientation":"landscape","pages":[{"id":"P1","els":[{"id":"E1"},{"id":"E2"},{"id":"E2","x":51.5},{"id":"E3"}]}]}
            """), refs)!;
        Assert.Equal("landscape", m.Orientation);
        Assert.Single(m.Pages);
        var els = m.Pages[0].Els;
        Assert.Equal(4, els.Count);
        Assert.NotEqual(els[1].Id, els[2].Id);         // repère répété : une copie
        Assert.Equal(51.5, els[2].X);
        Assert.Null(els[2].Action.Item.QrCode);        // la copie a son propre compteur
        Assert.Equal("none", els[3].Action.Type);
        Assert.Equal(1, m.Loose);
        Assert.Null(Apply(l, m));
    }

    [Fact]
    public void Reponse_vide_ne_vide_pas_la_liste_et_creation_depuis_rien()
    {
        var l = List();
        Assert.Null(AiInteractive.Merge(l, Answer("""{"pages":[]}"""), AiInteractive.Refs.Of(l, Cat)));
        Assert.Null(AiInteractive.Merge(l, Answer("""{"pages":[{"id":"P1","els":[]}]}"""), AiInteractive.Refs.Of(l, Cat)));

        var m = AiInteractive.Merge(null, Answer("""
            {"orientation":"portrait","pages":[
              {"id":"N1","name":"Accueil","els":[{"kind":"text","x":5,"y":4,"w":90,"h":7,"text":"Bienvenue","size":6},
                {"kind":"tile","x":5,"y":30,"w":43.5,"h":24,"text":"Plan","from":"2026-12-01","to":"2026-12-31","action":{"type":"media","content":"O1","qr":{"url":"https://exemple.ch/plan","label":"Le plan"}}},
                {"kind":"tile","x":51.5,"y":30,"w":43.5,"h":24,"text":"Menu","image":"I1","action":{"type":"page","page":"N2"}}]},
              {"id":"N2","name":"Menu","els":[{"kind":"tile","x":5,"y":20,"w":90,"h":14,"text":"Menu du jour","action":{"type":"media","content":"O2"}}]}]}
            """), AiInteractive.Refs.Of(null, Cat))!;
        var fresh = new InteractiveList { Name = "Accueil visiteurs" };
        Assert.Null(Apply(fresh, m));
        Assert.Equal(2, fresh.Pages.Count);
        var plan = fresh.Pages[0].Els[1];
        Assert.Equal("2026-12-01", plan.ShowFrom);
        Assert.True(plan.Action.Item.QrOn);
        Assert.Equal("https://exemple.ch/plan", plan.Action.Item.QrUrl);
        Assert.Equal(Logo, fresh.Pages[0].Els[2].ImageId);
        Assert.Equal(fresh.Pages[1].Id, fresh.Pages[0].Els[2].Action.PageId);
    }
}
