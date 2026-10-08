using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Linkii.Poc;

public record WeatherDto(string City, double Temp, int Code, double Min, double Max, double Wind, string Unit);

/// <summary>Météo via Open-Meteo (sans clé API). Cache serveur de 10 minutes : les écrans ne tapent jamais l'API externe.</summary>
public class WeatherService(IHttpClientFactory httpFactory)
{
    private record Geo(double Lat, double Lon, string Name);

    private readonly ConcurrentDictionary<string, Geo> _geo = new();
    private readonly ConcurrentDictionary<string, (DateTime At, WeatherDto Data)> _cache = new();

    public async Task<WeatherDto?> Get(string city, string unit)
    {
        unit = unit == "F" ? "F" : "C";
        var key = $"{city.Trim().ToLowerInvariant()}|{unit}";
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10)) return hit.Data;

        try
        {
            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(8);

            if (!_geo.TryGetValue(key, out var g))
            {
                var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=fr&name=" + Uri.EscapeDataString(city);
                using var gd = JsonDocument.Parse(await http.GetStringAsync(url));
                if (!gd.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return null;
                var r0 = res[0];
                g = new Geo(r0.GetProperty("latitude").GetDouble(), r0.GetProperty("longitude").GetDouble(), r0.GetProperty("name").GetString() ?? city);
                _geo[key] = g;
            }

            var inv = CultureInfo.InvariantCulture;
            var furl = $"https://api.open-meteo.com/v1/forecast?latitude={g.Lat.ToString(inv)}&longitude={g.Lon.ToString(inv)}" +
                       "&current=temperature_2m,weather_code,wind_speed_10m&daily=temperature_2m_max,temperature_2m_min&timezone=auto" +
                       "&forecast_days=1&temperature_unit=" + (unit == "F" ? "fahrenheit" : "celsius");
            using var fd = JsonDocument.Parse(await http.GetStringAsync(furl));
            var cur = fd.RootElement.GetProperty("current");
            var daily = fd.RootElement.GetProperty("daily");
            var dto = new WeatherDto(
                g.Name,
                Math.Round(cur.GetProperty("temperature_2m").GetDouble()),
                cur.GetProperty("weather_code").GetInt32(),
                Math.Round(daily.GetProperty("temperature_2m_min")[0].GetDouble()),
                Math.Round(daily.GetProperty("temperature_2m_max")[0].GetDouble()),
                Math.Round(cur.GetProperty("wind_speed_10m").GetDouble()),
                unit);
            _cache[key] = (DateTime.UtcNow, dto);
            return dto;
        }
        catch
        {
            return hit.Data; // dernière valeur connue (même périmée), ou null
        }
    }
}
