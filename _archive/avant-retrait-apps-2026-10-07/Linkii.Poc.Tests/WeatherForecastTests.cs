using Linkii.Poc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Linkii.Poc.Tests;

public class WeatherForecastTests
{
    // Réponse type d'Open-Meteo : 3 jours, la pluie du 2e jour est absente (null).
    const string Sample = """
    {
      "current": { "temperature_2m": 11.6, "weather_code": 3 },
      "daily": {
        "time": ["2026-10-06", "2026-10-07", "2026-10-08"],
        "weather_code": [3, 61, 0],
        "temperature_2m_max": [14.4, 12.5, 16.0],
        "temperature_2m_min": [6.2, 7.5, 5.0],
        "precipitation_probability_max": [10, null, 0],
        "wind_speed_10m_max": [14.2, 22.0, 8.0]
      }
    }
    """;

    [Fact]
    public void Open_meteo_response_is_read_into_days_with_rounded_values()
    {
        var f = WeatherService.ParseForecast(Sample, "Lausanne", "C");
        Assert.Equal("Lausanne", f.City);
        Assert.Equal(12, f.Temp);
        Assert.Equal(3, f.Code);
        Assert.Equal(3, f.Days.Count);
        Assert.Equal(new ForecastDay("2026-10-06", 3, 6, 14, 10, 14), f.Days[0]);
        Assert.Equal(0, f.Days[1].Rain);   // valeur absente : 0, pas d'erreur
        Assert.Equal(61, f.Days[1].Code);
    }

    [Fact]
    public void The_forecast_manifest_is_valid()
    {
        var catalog = new AppCatalog(AppsDir(), NullLogger.Instance);
        var app = catalog.Find("weather-forecast");
        Assert.NotNull(app);
        Assert.Empty(AppCatalog.Check(app!));
        Assert.Equal("weather-forecast", app!.Provider);
        Assert.True(app.Data);

        // ville obligatoire ; nombre de jours borné à 7
        var v = AppCatalog.WithDefaults(app, null);
        Assert.Contains(AppCatalog.Validate(app, v, new HashSet<string>()), e => e.Contains("Ville"));
        v["city"] = "Lausanne"; v["days"] = "8";
        Assert.Contains(AppCatalog.Validate(app, v, new HashSet<string>()), e => e.Contains("Nombre de jours"));
        v["days"] = "7";
        Assert.Empty(AppCatalog.Validate(app, v, new HashSet<string>()));
    }

    static string AppsDir([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!, "Linkii.Poc", "Apps");
}
