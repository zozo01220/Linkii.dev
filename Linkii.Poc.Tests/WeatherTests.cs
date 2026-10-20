using System.Text.Json;
using Linkii.Poc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Lecture de la réponse de WeatherAPI.com (extrait réel raccourci), conversions et règles d'affichage du contenu « Météo ».</summary>
[Collection("platform")]
public class WeatherTests(Platform platform)
{
    const string Sample = """
    {
      "location": { "name": "Lausanne", "region": "Vaud", "country": "Switzerland", "tz_id": "Europe/Zurich" },
      "current": { "temp_c": 14.2, "feelslike_c": 12.4, "wind_kph": 13.7, "humidity": 68, "uv": 3.0,
                   "condition": { "text": "Partiellement nuageux", "code": 1003 },
                   "air_quality": { "us-epa-index": 2 } },
      "forecast": { "forecastday": [
        { "date": "2026-10-21", "day": { "maxtemp_c": 17.0, "mintemp_c": 9.1, "daily_chance_of_rain": 20, "condition": { "code": 1003 } }, "astro": { "sunrise": "07:42 AM", "sunset": "06:21 PM" } },
        { "date": "2026-10-22", "day": { "maxtemp_c": 13.0, "mintemp_c": 8.0, "daily_chance_of_rain": 80, "condition": { "code": 1189 } }, "astro": { "sunrise": "07:43 AM", "sunset": "06:19 PM" } },
        { "date": "2026-10-23", "day": { "maxtemp_c": 15.0, "mintemp_c": 6.0, "daily_chance_of_rain": 0, "condition": { "code": 1000 } }, "astro": { "sunrise": "07:45 AM", "sunset": "06:18 PM" } } ] },
      "alerts": { "alert": [
        { "event": "Orages", "headline": "Orages", "severity": "Moderate", "areas": "Vaud", "expires": "2026-10-21T22:00:00+02:00" },
        { "event": "Vent violent", "headline": "Vent", "severity": "Severe", "areas": "Vaud", "expires": "2026-10-21T20:00:00+02:00" } ] }
    }
    """;

    static WeatherForecast Parsed() { using var d = JsonDocument.Parse(Sample); return WeatherService.Parse(d.RootElement); }

    [Fact]
    public void Parse_reads_today_three_days_air_and_alerts()
    {
        var f = Parsed();
        Assert.Equal("Lausanne", f.City);
        Assert.Equal("Vaud", f.Region);
        Assert.True(f.Europe);
        Assert.Equal(2, f.Code);                 // « partiellement nuageux » → code WMO 2
        Assert.Equal(2, f.Aqi);
        Assert.Equal(3, f.Days.Count);
        Assert.Equal("07:42", f.Days[0].Sunrise);   // 12 h → 24 h
        Assert.Equal("18:21", f.Days[0].Sunset);
        Assert.Equal(61, f.Days[1].Code);           // pluie
        Assert.Equal("Vent violent", f.Alerts[0].Event);   // la plus grave d'abord
    }

    [Fact]
    public void Parse_without_forecast_air_or_alerts_still_works()
    {
        using var d = JsonDocument.Parse("""{ "location": { "name": "X", "country": "Peru" }, "current": { "temp_c": 20, "condition": { "text": "Ensoleillé", "code": 1000 } } }""");
        var f = WeatherService.Parse(d.RootElement);
        Assert.Empty(f.Days);
        Assert.Empty(f.Alerts);
        Assert.Equal(0, f.Aqi);
        Assert.False(f.Europe);
    }

    [Theory]
    [InlineData("Switzerland", true)]
    [InlineData("France", true)]
    [InlineData("United Kingdom", true)]
    [InlineData("united kingdom", true)]
    [InlineData("USA", false)]
    [InlineData("United States of America", false)]
    [InlineData("", false)]
    public void Alerts_are_for_europe_only(string country, bool europe) => Assert.Equal(europe, WeatherService.IsEurope(country));

    [Theory]
    [InlineData(1000, 0)]
    [InlineData(1006, 3)]
    [InlineData(1135, 45)]
    [InlineData(1183, 61)]
    [InlineData(1225, 71)]
    [InlineData(1273, 95)]
    [InlineData(9999, -1)]
    public void Condition_codes_become_the_codes_the_player_knows(int code, int wmo) => Assert.Equal(wmo, WeatherService.Wmo(code));

    [Fact]
    public void Fahrenheit_conversion() => Assert.Equal(57.2, WeatherService.Convert(14, "F"), 1);

    [Fact]
    public void Unknown_location_error_is_told_apart_from_an_outage()
    {
        Assert.True(WeatherService.IsUnknownLocation("""{ "error": { "code": 1006, "message": "No matching location found." } }"""));
        Assert.False(WeatherService.IsUnknownLocation("""{ "error": { "code": 2008, "message": "API key disabled." } }"""));
        Assert.False(WeatherService.IsUnknownLocation("pas du json"));
    }

    [Fact]
    public async Task Slide_provider_sends_everything_and_applies_the_unit()
    {
        var svc = new FakeWeather(Parsed());
        var provider = new WeatherSlideProvider(svc);
        var rt = new AppRuntime(new Tenant(), new Dictionary<string, string> { ["city"] = "Lausanne", ["unit"] = "F" });
        var json = JsonSerializer.SerializeToElement(await provider.Fetch(rt));
        Assert.Equal(58, json.GetProperty("temp").GetDouble());   // 14,2 °C = 57,6 °F
        Assert.Equal(3, json.GetProperty("days").GetArrayLength());
        Assert.Equal("Modérée", json.GetProperty("air").GetProperty("label").GetString());
        Assert.True(json.GetProperty("alertsAvailable").GetBoolean());
        Assert.Equal("Vent violent", json.GetProperty("alert").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Slide_provider_test_warns_when_alerts_are_wanted_outside_europe()
    {
        using var d = JsonDocument.Parse("""{ "location": { "name": "New York", "region": "New York", "country": "USA" }, "current": { "temp_c": 18, "condition": { "text": "Ensoleillé", "code": 1000 } } }""");
        var provider = new WeatherSlideProvider(new FakeWeather(WeatherService.Parse(d.RootElement)));
        var msg = await provider.Test(new AppRuntime(new Tenant(), new Dictionary<string, string> { ["city"] = "New York", ["alerts"] = "true" }));
        Assert.StartsWith("⚠", msg);
        var ok = await provider.Test(new AppRuntime(new Tenant(), new Dictionary<string, string> { ["city"] = "New York", ["alerts"] = "false" }));
        Assert.DoesNotContain("⚠", ok);
    }

    [Fact]
    public void Manifest_is_valid_and_city_is_the_source()
    {
        var m = platform.Services.GetRequiredService<AppCatalog>().Find("weather-slide")!;
        Assert.Empty(AppCatalog.Check(m));
        Assert.Equal("city", m.InZone("source").Single().Type);
        Assert.Contains("top-left", m.Placements);
        Assert.True(m.HasAdvanced);
    }

    /// <summary>Service de remplacement : renvoie toujours la même prévision, sans appel réseau.</summary>
    class FakeWeather(WeatherForecast f) : WeatherService(null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
    {
        public override Task<WeatherForecast?> Forecast(string city, TimeSpan maxAge) => Task.FromResult<WeatherForecast?>(f);
    }
}
