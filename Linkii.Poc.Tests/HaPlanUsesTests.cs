using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Page du plan (§202) : affichages proposés d'office (Diffusion, Borne, Compact), widget en « Auto » ou sur un affichage, « Où il s'affiche » et « Ajouter à… ».</summary>
public class HaPlanUsesTests
{
    static readonly string PlanJson = HomeAssistantRules.NormalizePlan("""
        {"floors":[{"id":"ss","name":"Sous-sol"},{"id":"rdc","name":"Rez"}],
         "devices":[{"id":"light.salon","floor":"rdc","x":0.2,"y":0.3},{"id":"sensor.temp","floor":"ss","x":0.5,"y":0.5}]}
        """).Json;

    static AppCatalog Catalog() =>
        new(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Linkii.Poc", "Apps"), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    /// <summary>Une organisation avec un plan, Home Assistant installé, une liste, un écran, une borne avec sa liste interactive et une borne sans liste.</summary>
    static (Db Db, ClientDb D, HaPlan Plan, Playlist List, Screen Screen, Screen Kiosk, Screen Bare, InteractiveList IList) Org()
    {
        var t = new Tenant { Name = "Maison" };
        var plan = new HaPlan { Name = "Maison Gex", Json = PlanJson };
        t.HaPlans.Add(plan);
        HaViews.Ensure(plan);
        var db = new Db();
        db.Clients.Add(t);
        foreach (var a in new[] { HomeAssistantRules.BuildingApp, HomeAssistantRules.PlanApp }) db.AppInstalls.Add(new AppInstall { ClientId = t.Id, AppId = a });
        var list = new Playlist { ClientId = t.Id, Name = "Accueil" };
        var il = new InteractiveList { ClientId = t.Id, Name = "Hall", Pages = { new KioskPage { Els = { new KioskEl { Y = 10, H = 30 } } } } };
        var screen = new Screen { ClientId = t.Id, Name = "Cuisine" };
        var kiosk = new Screen { ClientId = t.Id, Name = "Entrée", Kiosk = new KioskConfig { ListId = il.Id } };
        var bare = new Screen { ClientId = t.Id, Name = "Garage", Kiosk = new KioskConfig() };
        db.Playlists.Add(list); db.InteractiveLists.Add(il); db.Screens.AddRange(new[] { screen, kiosk, bare });
        return (db, new ClientDb(db, t.Id), plan, list, screen, kiosk, bare, il);
    }

    [Fact]
    public void An_older_plan_gets_the_compact_display_once()
    {
        var plan = new HaPlan { Name = "Ancien", Json = PlanJson };
        plan.Views.Add(new HaPlanView { Name = "Diffusion", Mode = "signage" });
        plan.Views.Add(new HaPlanView { Name = "Borne", Mode = "interactive" });
        Assert.True(HaViews.Ensure(plan));
        Assert.Equal(new[] { "signage", "interactive", "compact" }, plan.Views.Select(v => v.Mode));
        plan.Views.RemoveAt(2);                                                // supprimé exprès…
        Assert.False(HaViews.Ensure(plan));
        Assert.Equal(2, plan.Views.Count);                                     // … il ne revient pas
    }

    [Fact]
    public void Displays_keep_their_usage_and_name_it()
    {
        var p = HomeAssistantRules.ParsePlan(PlanJson);
        Assert.Equal("compact", HaViews.Normalize(new HaPlanView { Name = "Petit", Mode = "compact" }, p).Mode);
        Assert.Equal("signage", HaViews.Normalize(new HaPlanView { Name = "X", Mode = "n'importe quoi" }, p).Mode);
        Assert.Equal(new[] { "Diffusion", "Borne", "Compact" }, new[] { "signage", "interactive", "compact" }.Select(HaViews.ModeLabel));
        Assert.Equal(28, HaViews.TourSeconds(new HaPlanView { Mode = "signage", Tour = { new() { Sec = 12 }, new() { Sec = 8 }, new() { Sec = 8 } } }));
        Assert.Null(HaViews.TourSeconds(new HaPlanView { Mode = "interactive", Tour = { new() { Sec = 12 } } }));   // pas de visite à la borne
    }

    [Fact]
    public void The_widget_follows_a_display_only_when_one_is_chosen()
    {
        var v = Guid.NewGuid().ToString();
        Assert.True(HaViews.Chosen(HomeAssistantRules.BuildingApp, new Dictionary<string, string>()));
        Assert.False(HaViews.Chosen(HomeAssistantRules.PlanApp, new Dictionary<string, string> { ["viewId"] = "" }));   // « Auto » : ses propres réglages
        Assert.True(HaViews.Chosen(HomeAssistantRules.PlanApp, new Dictionary<string, string> { ["viewId"] = v }));
        // les réglages propres du widget ne s'affichent qu'en « Auto »
        var m = Catalog().Find(HomeAssistantRules.PlanApp)!;
        var labels = m.Settings.First(f => f.Key == "labels");
        Assert.True(AppCatalog.IsVisible(labels, new Dictionary<string, string> { ["viewId"] = "" }));
        Assert.False(AppCatalog.IsVisible(labels, new Dictionary<string, string> { ["viewId"] = v }));
        Assert.Empty(AppCatalog.Validate(m, AppCatalog.WithDefaults(m, new Dictionary<string, string> { ["planId"] = Guid.NewGuid().ToString(), ["viewId"] = "" }), new HashSet<string>()));
    }

    [Fact]
    public void Add_to_a_list_a_screen_and_a_kiosk_then_list_where_it_shows()
    {
        var (_, d, plan, list, screen, kiosk, bare, il) = Org();
        var catalog = Catalog();
        var signage = plan.Views.First(v => v.Mode == "signage");
        var borne = plan.Views.First(v => v.Mode == "interactive");

        // liste de diffusion : contenu relié, nommé « Plan · Affichage », durée de la visite
        var c = HaPlanUses.NewContent(d, catalog, plan, signage)!;
        Assert.Equal("Maison Gex · Diffusion", c.Name);
        Assert.Equal(plan.Id.ToString(), c.Settings["planId"]);
        Assert.Null(HaPlanUses.AddToList(d, list.Id, c, signage));
        Assert.Equal(HaViews.TourSeconds(signage), list.Draft.Single().DurationSec);
        Assert.True(list.DraftChanged);

        // écran : widget en « Auto », à publier
        Assert.Null(HaPlanUses.AddWidget(d, catalog, screen.Id, plan.Id, null));
        var w = screen.Widgets.Single();
        Assert.Equal((HomeAssistantRules.PlanApp, plan.Id.ToString(), ""), (w.AppId, w.Settings["planId"], w.Settings["viewId"]));
        Assert.Equal(1, screen.WidgetsRevision);

        // borne : tuile sous les éléments déjà posés, qui ouvre un contenu « Borne »
        var k = HaPlanUses.NewContent(d, catalog, plan, borne)!;
        Assert.Null(HaPlanUses.AddTile(d, kiosk.Id, k, plan.Name));
        var tile = il.Pages[0].Els.Last();
        Assert.Equal(("media", k.Id, 43.0), (tile.Action.Type, tile.Action.MediaId!.Value, tile.Y));
        Assert.Equal(1, il.Revision);
        Assert.Contains("liste interactive", HaPlanUses.AddTile(d, bare.Id, k, plan.Name));   // borne sans liste : on le dit

        var uses = HaPlanUses.Of(d, plan.Id);
        Assert.Equal(new[] { "list", "tile", "screen" }, uses.Select(u => u.Kind).OrderBy(x => x == "list" ? 0 : x == "tile" ? 1 : 2));
        Assert.Equal("« Accueil »", uses.First(u => u.Kind == "list").Where);
        Assert.Null(uses.First(u => u.Kind == "screen").ViewId);               // widget en « Auto »
        Assert.Equal(1, HaPlanUses.ByView(d, plan.Id)[signage.Id]);
        Assert.Equal(1, HaPlanUses.ByView(d, plan.Id)[borne.Id]);
        Assert.Equal(3, HaPlanUses.CountByPlan(d)[plan.Id]);
    }

    [Fact]
    public void Nothing_is_added_without_home_assistant()
    {
        var (db, d, plan, list, _, _, _, _) = Org();
        db.AppInstalls.Clear();
        Assert.Null(HaPlanUses.NewContent(d, Catalog(), plan, plan.Views[0]));
        Assert.NotNull(HaPlanUses.AddWidget(d, Catalog(), db.Screens[0].Id, plan.Id, null));
        Assert.Empty(list.Draft);
    }
}
