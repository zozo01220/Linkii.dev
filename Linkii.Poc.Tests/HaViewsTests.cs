using System.Text.Json;
using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Vues enregistrées d'un plan du bâtiment et contenu « Vue du bâtiment » (§200).</summary>
public class HaViewsTests
{
    static readonly string PlanJson = HomeAssistantRules.NormalizePlan("""
        {"floors":[{"id":"ss","name":"Sous-sol"},{"id":"rdc","name":"Rez"}],
         "devices":[{"id":"light.salon","floor":"rdc","x":0.2,"y":0.3},{"id":"lock.porte","floor":"rdc","x":0.5,"y":0.5},{"id":"sensor.temp","floor":"ss","x":0.5,"y":0.5},{"id":"switch.prise","floor":"ss","x":0.4,"y":0.4}]}
        """).Json;

    static (Tenant, HaPlan) Maison()
    {
        var p = new HaPlan { Name = "Maison", Json = PlanJson };
        return (new Tenant { Name = "Maison", HaPlans = { p } }, p);
    }

    [Fact]
    public void A_plan_without_views_gets_a_signage_and_a_kiosk_view()
    {
        var (_, p) = Maison();
        Assert.True(HaViews.Ensure(p));
        Assert.False(HaViews.Ensure(p));                                       // une seule fois
        Assert.Equal(new[] { ("Diffusion", "signage"), ("Borne", "interactive") }, p.Views.Select(v => (v.Name, v.Mode)));
        // visite proposée : vue d'ensemble, puis les étages du haut vers le bas
        Assert.Equal(new string?[] { null, "rdc", "ss" }, p.Views[0].Tour.Select(t => t.Floor));
        Assert.Equal(new[] { 12, 8, 8 }, p.Views[0].Tour.Select(t => t.Sec));
        Assert.Equal("all", p.Views[0].Labels);                                // valeur, ampoule, halo par défaut
    }

    [Fact]
    public void Views_are_cleaned()
    {
        var plan = HomeAssistantRules.ParsePlan(PlanJson);
        var v = HaViews.Normalize(new HaPlanView
        {
            Id = Guid.Empty, Name = "  ", Mode = "n'importe quoi", Labels = "rien", Summary = "haut",
            Tour = { new HaTourStep { Floor = "rdc", Sec = 1 }, new HaTourStep { Floor = "grenier", Sec = 10 }, new HaTourStep { Floor = null, Sec = 999 } },
            Control = new() { "light", "light", "camera", "lock" },
        }, plan);
        Assert.NotEqual(Guid.Empty, v.Id);
        Assert.Equal(("Vue", "signage", "all", "right"), (v.Name, v.Mode, v.Labels, v.Summary));
        Assert.Equal(new[] { ("rdc", HaViews.MinSec), ((string?)null, HaViews.MaxSec) }, v.Tour.Select(t => (t.Floor, t.Sec)));   // étage inconnu retiré, durées bornées
        Assert.Equal(new[] { "light", "lock" }, v.Control);
        // plus aucune étape valable : la visite proposée
        var empty = HaViews.Normalize(new HaPlanView { Tour = { new HaTourStep { Floor = "grenier" } } }, plan);
        Assert.Equal(3, empty.Tour.Count);
    }

    [Fact]
    public void A_content_reads_its_view_and_only_an_interactive_view_commands()
    {
        var (t, p) = Maison();
        HaViews.Ensure(p);
        var tv = p.Views[0]; var borne = p.Views[1];
        var settings = new Dictionary<string, string> { ["planId"] = p.Id.ToString(), ["viewId"] = borne.Id.ToString() };
        Assert.Same(borne, HaViews.Of(t, settings));
        Assert.Same(tv, HaViews.Of(t, new Dictionary<string, string> { ["planId"] = p.Id.ToString(), ["viewId"] = Guid.NewGuid().ToString() }));   // vue supprimée : la première
        Assert.Null(HaViews.Of(t, new Dictionary<string, string> { ["planId"] = Guid.NewGuid().ToString() }));

        Assert.True(HaViews.Allows(borne, "light.salon"));
        Assert.False(HaViews.Allows(borne, "lock.porte"));                    // serrures non cochées par défaut
        Assert.False(HaViews.Allows(borne, "sensor.temp"));                   // capteur : rien à commander
        Assert.False(HaViews.Allows(tv, "light.salon"));                      // diffusion : jamais
        borne.Control.Add("lock");
        Assert.True(HaViews.Allows(borne, "lock.porte"));
        Assert.Equal("switch", HaViews.FamilyOf("input_boolean"));

        // le contenu lit les appareils du plan, comme le widget
        Assert.Equal(4, HomeAssistantRules.EntitiesOf(t, HomeAssistantRules.BuildingApp, settings).Count);
        Assert.True(HomeAssistantRules.IsHaApp(HomeAssistantRules.BuildingApp));
        Assert.True(HomeAssistantRules.Commandable(HomeAssistantRules.BuildingApp, "light.salon", "on"));
    }

    [Fact]
    public void The_screen_receives_the_view_mode_tour_and_display()
    {
        var (_, p) = Maison();
        HaViews.Ensure(p);
        var v = p.Views[0];
        v.Heat = true; v.Labels = "room";
        var json = JsonSerializer.SerializeToElement(HomeAssistantProvider.PlanView(HomeAssistantRules.ParsePlan(p.Json), new Dictionary<string, HaState>(), HaViews.Options(v), v));
        Assert.Equal("signage", json.GetProperty("mode").GetString());
        Assert.Equal(3, json.GetProperty("tour").GetArrayLength());
        Assert.Equal("rdc", json.GetProperty("tour")[1].GetProperty("floor").GetString());
        Assert.True(json.GetProperty("heat").GetBoolean());
        Assert.Equal("room", json.GetProperty("labels").GetString());
        Assert.False(json.GetProperty("spinSwitch").GetBoolean());             // la visite décide de la rotation

        // widget « Plan du bâtiment » sans vue : ni mode ni visite
        var w = JsonSerializer.SerializeToElement(HomeAssistantProvider.PlanView(HomeAssistantRules.ParsePlan(p.Json), new Dictionary<string, HaState>(), _ => null));
        Assert.Equal(JsonValueKind.Null, w.GetProperty("mode").ValueKind);
    }

    [Fact]
    public void The_building_content_is_in_the_catalog_as_a_full_page_content()
    {
        var catalog = new AppCatalog(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Linkii.Poc", "Apps"), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var m = catalog.Find("ha-building")!;
        Assert.False(m.ScreenWidget);
        Assert.Equal(new[] { "full" }, m.Placements);
        Assert.Equal(new[] { "haplanpick", "haviewpick" }, m.Settings.Select(f => f.Type));
        Assert.Contains("ha-building", HomeAssistantRules.AllApps);
    }
}
