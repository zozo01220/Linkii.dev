using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Linkii.Poc;

public record WeatherDto(string City, double Temp, int Code, double Min, double Max, double Wind, string Unit);

/// <summary>Un jour de prévision : date locale (aaaa-mm-jj), code météo WMO, min / max, probabilité de pluie (%), vent maximal (km/h).</summary>
public record ForecastDay(string Date, int Code, double Min, double Max, int Rain, double Wind);
public record ForecastDto(string City, double Temp, int Code, string Unit, List<ForecastDay> Days);

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

    private readonly ConcurrentDictionary<string, (DateTime At, ForecastDto Data)> _fcache = new();

    /// <summary>Prévisions sur plusieurs jours (1 à 7), mêmes règles que <see cref="Get"/> : sans clé, cache serveur de 10 minutes, dernière valeur connue si la source est indisponible.</summary>
    public async Task<ForecastDto?> GetForecast(string city, string unit, int days)
    {
        unit = unit == "F" ? "F" : "C";
        days = Math.Clamp(days, 1, 7);
        var key = $"{city.Trim().ToLowerInvariant()}|{unit}|{days}";
        if (_fcache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10)) return hit.Data;

        try
        {
            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(8);
            var gkey = "geo|" + city.Trim().ToLowerInvariant();
            if (!_geo.TryGetValue(gkey, out var g))
            {
                var url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=fr&name=" + Uri.EscapeDataString(city);
                using var gd = JsonDocument.Parse(await http.GetStringAsync(url));
                if (!gd.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return null;
                var r0 = res[0];
                g = new Geo(r0.GetProperty("latitude").GetDouble(), r0.GetProperty("longitude").GetDouble(), r0.GetProperty("name").GetString() ?? city);
                _geo[gkey] = g;
            }
            var inv = CultureInfo.InvariantCulture;
            var furl = $"https://api.open-meteo.com/v1/forecast?latitude={g.Lat.ToString(inv)}&longitude={g.Lon.ToString(inv)}" +
                       "&current=temperature_2m,weather_code&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,wind_speed_10m_max" +
                       $"&timezone=auto&forecast_days={days}&temperature_unit=" + (unit == "F" ? "fahrenheit" : "celsius");
            var dto = ParseForecast(await http.GetStringAsync(furl), g.Name, unit);
            _fcache[key] = (DateTime.UtcNow, dto);
            return dto;
        }
        catch
        {
            return hit.Data;
        }
    }

    /// <summary>Lit la réponse d'Open-Meteo (actuel + quotidien).</summary>
    public static ForecastDto ParseForecast(string json, string city, string unit)
    {
        using var doc = JsonDocument.Parse(json);
        var cur = doc.RootElement.GetProperty("current");
        var d = doc.RootElement.GetProperty("daily");
        double N(string name, int i) => d.GetProperty(name)[i].ValueKind == JsonValueKind.Number ? d.GetProperty(name)[i].GetDouble() : 0;
        var days = new List<ForecastDay>();
        for (var i = 0; i < d.GetProperty("time").GetArrayLength(); i++)
            days.Add(new ForecastDay(d.GetProperty("time")[i].GetString()!, (int)N("weather_code", i), Math.Round(N("temperature_2m_min", i)),
                Math.Round(N("temperature_2m_max", i)), (int)Math.Round(N("precipitation_probability_max", i)), Math.Round(N("wind_speed_10m_max", i))));
        return new ForecastDto(city, Math.Round(cur.GetProperty("temperature_2m").GetDouble()), cur.GetProperty("weather_code").GetInt32(), unit, days);
    }}
