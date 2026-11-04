using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Mode simple : seules les intégrations de base sont proposées ; le mode expert propose tout.</summary>
public class SimpleModeTests
{
    [Theory]
    [InlineData("image")]
    [InlineData("video")]
    [InlineData("slideshow-images")]
    [InlineData("slideshow-videos")]
    [InlineData("webpage")]
    [InlineData("composition")]
    public void Le_mode_simple_propose_les_integrations_de_base(string appId) =>
        Assert.True(SimpleMode.Offers(false, appId));

    [Theory]
    [InlineData("youtube")]
    [InlineData("weather-slide")]
    [InlineData("pdf")]
    [InlineData("canva")]
    [InlineData(null)]
    public void Le_mode_simple_ne_propose_pas_les_autres(string? appId) =>
        Assert.False(SimpleMode.Offers(false, appId));

    [Fact]
    public void Le_mode_expert_propose_tout() => Assert.True(SimpleMode.Offers(true, "youtube"));

    [Fact]
    public void Les_integrations_de_base_existent_dans_le_catalogue()
    {
        // le dossier Apps du projet, retrouvé depuis ce fichier source (quel que soit le dossier de sortie des tests)
        var apps = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "Linkii.Poc", "Apps");
        var catalog = new AppCatalog(apps, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        foreach (var id in SimpleMode.Apps) Assert.NotNull(catalog.Find(id));
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
