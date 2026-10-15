using Linkii.Poc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Règles de l'intégration Home Assistant (sans réseau) et présence de ses widgets dans le catalogue.</summary>
public class HomeAssistantTests
{
    readonly AppCatalog catalog = new(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Linkii.Poc", "Apps"), NullLogger.Instance);

    [Fact]
    public void Entity_list_keeps_valid_ids_once_and_drops_bad_ones()
    {
        var raw = "sensor.salon_temperature, switch.ventilateur;light.bureau\nsensor.salon_temperature  Bad Entry";
        Assert.Equal(new[] { "sensor.salon_temperature", "switch.ventilateur", "light.bureau" }, HomeAssistantRules.ParseEntities(raw));
    }

    [Theory]
    [InlineData("sensor.salon_temperature", true)]
    [InlineData("switch.ventilateur", true)]
    [InlineData("light.bureau", true)]
    [InlineData("input_boolean.vacances", false)]
    [InlineData("lock.entree", false)]
    [InlineData("pas une entité", false)]
    public void Only_sensor_switch_and_light_are_exposed(string entity, bool expected) =>
        Assert.Equal(expected, HomeAssistantRules.IsSupported(entity));

    [Theory]
    [InlineData("ha-switches", "switch.ventilateur", true)]
    [InlineData("ha-lights", "light.bureau", true)]
    [InlineData("ha-switches", "light.bureau", false)]     // un widget d'interrupteurs ne commande pas une lumière
    [InlineData("ha-lights", "switch.ventilateur", false)]
    [InlineData("ha-sensors", "sensor.salon_temperature", false)]   // les capteurs se lisent, ne se commandent pas
    public void A_widget_only_commands_its_own_domain(string app, string entity, bool expected) =>
        Assert.Equal(expected, HomeAssistantRules.Commandable(app, entity));

    [Theory]
    [InlineData("https://maison.exemple.ch/", "https://maison.exemple.ch")]
    [InlineData("  https://maison.exemple.ch  ", "https://maison.exemple.ch")]
    [InlineData(null, "")]
    public void Base_url_drops_spaces_and_trailing_slash(string? input, string expected) =>
        Assert.Equal(expected, HomeAssistantRules.BaseUrl(input));

    [Fact]
    public void Three_widgets_read_the_organisation_account()
    {
        foreach (var id in new[] { "ha-sensors", "ha-switches", "ha-lights" })
        {
            var app = catalog.Find(id);
            Assert.NotNull(app);
            Assert.Equal("homeassistant", app!.Provider);
            Assert.True(app.Data);
            Assert.True(app.ScreenWidget);
            Assert.Equal("Maison connectée", app.Category);
            Assert.Empty(AppCatalog.Check(app));
        }
    }

    [Theory]
    [InlineData("ha-sensors", "sensor")]
    [InlineData("ha-switches", "switch")]
    [InlineData("ha-lights", "light")]
    public void Each_widget_chooses_entities_of_its_own_type(string id, string domain)
    {
        var field = catalog.Find(id)!.Settings.Single(f => f.Key == "entities");
        Assert.Equal("haentity", field.Type);
        Assert.Equal(new[] { domain }, field.Domains);
    }

    [Fact]
    public void An_entity_field_without_a_valid_domain_is_rejected()
    {
        var bad = new AppManifest
        {
            Id = "ha-test", Name = "Test", Placements = { "full" }, Provider = "homeassistant", Data = true,
            Settings = { new AppField { Key = "entities", Type = "haentity", Domains = { "camera" } } },
        };
        Assert.NotEmpty(AppCatalog.Check(bad));
    }
}
