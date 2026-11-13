using Microsoft.Extensions.Logging.Abstractions;
using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

public class RetiredAppsTests
{
    private static string Manifest(string id) => "{\"id\":\"" + id + "\",\"name\":\"Test\",\"version\":\"1.0.0\",\"category\":\"Test\",\"kind\":\"builtin\",\"placements\":[\"full\"],\"settings\":[]}";

    [Fact]
    public void A_manifest_left_on_the_server_does_not_bring_a_retired_app_back()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkii-retired-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // « weather-forecast » : retirée le 7 octobre 2026, son fichier peut rester sur un serveur dont le déploiement ne supprime rien
            File.WriteAllText(Path.Combine(dir, "weather-forecast.json"), Manifest("weather-forecast"));
            File.WriteAllText(Path.Combine(dir, "weather-slide.json"), Manifest("weather-slide"));
            var cat = new AppCatalog(dir, NullLogger.Instance);
            Assert.Null(cat.Find("weather-forecast"));
            Assert.NotNull(cat.Find("weather-slide"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
