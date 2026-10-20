using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Linkii.Poc;

/// <summary>Valeurs du widget d'écran (température du moment, minimum et maximum du jour, vent), dans l'unité demandée.</summary>
public record WeatherDto(string City, double Temp, int Code, double Min, double Max, double Wind, string Unit);

public record WeatherDay(string Date, int Code, double MinC, double MaxC, int RainChance, string Sunrise, string Sunset);
public record WeatherAlert(string Event, string Headline, string Severity, string Areas, DateTime? Expires);

/// <summary>Prévision complète d'une ville telle que la renvoie le service (températures en °C ; la conversion se fait à l'envoi).</summary>
public record WeatherForecast(
    string City, string Region, string Country, bool Europe,
    double TempC, double FeelC, int Code, string Text, double WindKph, int Humidity, double Uv, int Aqi,
    List<WeatherDay> Days, List<WeatherAlert> Alerts);

/// <summary>
/// Météo via WeatherAPI.com (offre gratuite, usage commercial permis avec mention de la source). Clé : <c>Linkii:Weather:ApiKey</c>.
/// Un cache serveur par ville : les écrans ne tapent jamais l'API externe, et le widget et le contenu « Météo » partagent les mêmes lectures.
/// </summary>
public class WeatherService(IHttpClientFactory httpFactory, IConfiguration config)
{
    private readonly ConcurrentDictionary<string, (DateTime At, WeatherForecast Data)> _cache = new();

    public const string MissingKey = "La météo n'est pas configurée sur ce serveur (clé Linkii:Weather:ApiKey).";
    private string ApiKey => config["Linkii:Weather:ApiKey"] ?? "";
    public bool IsConfigured => ApiKey.Length > 0;

    // ---------- Lecture ----------

    /// <summary>Prévision de la ville ; null si la ville est introuvable. Dernière valeur connue (même périmée) si le service ne répond pas.</summary>
    public virtual async Task<WeatherForecast?> Forecast(string city, TimeSpan maxAge)
    {
        city = city.Trim();
        if (city.Length == 0) return null;
        if (!IsConfigured) throw new InvalidOperationException(MissingKey);
        var key = city.ToLowerInvariant();
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < maxAge) return hit.Data;

        try
        {
            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(8);
            var url = "https://api.weatherapi.com/v1/forecast.json?days=3&aqi=yes&alerts=yes&lang=fr&key=" + Uri.EscapeDataString(ApiKey) + "&q=" + Uri.EscapeDataString(city);
            using var res = await http.GetAsync(url);
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                if (IsUnknownLocation(body)) return null;
                throw new HttpRequestException($"WeatherAPI : réponse {(int)res.StatusCode}.");
            }
            using var doc = JsonDocument.Parse(body);
            var data = Parse(doc.RootElement);
            _cache[key] = (DateTime.UtcNow, data);
            return data;
        }
        catch (Exception) when (hit.Data != null)
        {
            return hit.Data;
        }
    }

    /// <summary>Widget d'écran : valeurs du jour dans l'unité demandée (cache de 10 minutes).</summary>
    public async Task<WeatherDto?> Get(string city, string unit)
    {
        unit = unit == "F" ? "F" : "C";
        if (await Forecast(city, TimeSpan.FromMinutes(10)) is not { } f) return null;
        var today = f.Days.FirstOrDefault();
        double T(double c) => Math.Round(Convert(c, unit));
        return new WeatherDto(f.City, T(f.TempC), f.Code, T(today?.MinC ?? f.TempC), T(today?.MaxC ?? f.TempC), Math.Round(f.WindKph), unit);
    }

    public static double Convert(double celsius, string unit) => unit == "F" ? celsius * 9 / 5 + 32 : celsius;

    // ---------- Lecture de la réponse (publique pour les tests) ----------

    /// <summary>« No matching location found » (code 1006) : la ville n'existe pas, ce n'est pas une panne.</summary>
    public static bool IsUnknownLocation(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            return d.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c) && c.GetInt32() == 1006;
        }
        catch { return false; }
    }

    public static WeatherForecast Parse(JsonElement root)
    {
        var loc = root.GetProperty("location");
        var cur = root.GetProperty("current");
        var country = Str(loc, "country");
        var days = new List<WeatherDay>();
        if (root.TryGetProperty("forecast", out var fc) && fc.TryGetProperty("forecastday", out var fd))
            foreach (var d in fd.EnumerateArray())
            {
                var day = d.GetProperty("day");
                var astro = d.TryGetProperty("astro", out var a) ? a : default;
                days.Add(new WeatherDay(Str(d, "date"), Wmo(day.GetProperty("condition").GetProperty("code").GetInt32()),
                    day.GetProperty("mintemp_c").GetDouble(), day.GetProperty("maxtemp_c").GetDouble(),
                    day.TryGetProperty("daily_chance_of_rain", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0,
                    astro.ValueKind == JsonValueKind.Object ? Hm(Str(astro, "sunrise")) : "", astro.ValueKind == JsonValueKind.Object ? Hm(Str(astro, "sunset")) : ""));
            }
        var alerts = new List<WeatherAlert>();
        if (root.TryGetProperty("alerts", out var al) && al.TryGetProperty("alert", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var x in list.EnumerateArray())
                alerts.Add(new WeatherAlert(Str(x, "event"), Str(x, "headline"), Str(x, "severity"), Str(x, "areas"),
                    DateTimeOffset.TryParse(Str(x, "expires"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ex) ? ex.UtcDateTime : null));
        var aqi = cur.TryGetProperty("air_quality", out var aq) && aq.TryGetProperty("us-epa-index", out var ix) && ix.ValueKind == JsonValueKind.Number ? ix.GetInt32() : 0;
        return new WeatherForecast(
            Str(loc, "name"), Str(loc, "region"), country, IsEurope(country),
            cur.GetProperty("temp_c").GetDouble(), cur.TryGetProperty("feelslike_c", out var fl) ? fl.GetDouble() : cur.GetProperty("temp_c").GetDouble(),
            Wmo(cur.GetProperty("condition").GetProperty("code").GetInt32()), Str(cur.GetProperty("condition"), "text"),
            cur.TryGetProperty("wind_kph", out var w) ? w.GetDouble() : 0, cur.TryGetProperty("humidity", out var h) ? (int)Math.Round(h.GetDouble()) : 0,
            cur.TryGetProperty("uv", out var uv) ? uv.GetDouble() : 0, aqi,
            days, alerts.OrderByDescending(a => SeverityRank(a.Severity)).ToList());
    }

    /// <summary>« 07:42 AM » → « 07:42 » (24 heures).</summary>
    private static string Hm(string v) => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : v;

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static int SeverityRank(string s) => s.ToLowerInvariant() switch { "extreme" => 4, "severe" => 3, "moderate" => 2, "minor" => 1, _ => 0 };

    // ---------- Pays d'Europe (alertes météo : Europe seulement) ----------

    private static readonly HashSet<string> EuropeCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        "Albania", "Andorra", "Austria", "Belarus", "Belgium", "Bosnia and Herzegovina", "Bulgaria", "Croatia", "Cyprus", "Czech Republic", "Czechia",
        "Denmark", "Estonia", "Finland", "France", "Germany", "Greece", "Hungary", "Iceland", "Ireland", "Italy", "Kosovo", "Latvia", "Liechtenstein",
        "Lithuania", "Luxembourg", "Malta", "Moldova", "Monaco", "Montenegro", "Netherlands", "North Macedonia", "Macedonia", "Norway", "Poland", "Portugal",
        "Romania", "San Marino", "Serbia", "Slovakia", "Slovenia", "Spain", "Sweden", "Switzerland", "Ukraine", "United Kingdom", "UK", "Vatican City", "Vatican"
    };

    public static bool IsEurope(string country) => EuropeCountries.Contains(country.Trim());

    // ---------- Pictogrammes et indice de qualité de l'air ----------

    /// <summary>
    /// Code de condition de WeatherAPI → code WMO (celui qu'attend le player : <c>wmoIcon</c> dans apps.js). 0 clair, 2 peu nuageux, 3 couvert,
    /// 45 brouillard, 51 bruine, 61 pluie, 65 forte pluie, 71 neige, 95 orage ; -1 : inconnu.
    /// </summary>
    public static int Wmo(int code) => code switch
    {
        1000 => 0,
        1003 => 2,
        1006 or 1009 => 3,
        1030 or 1135 or 1147 => 45,
        1150 or 1153 or 1168 or 1171 => 51,
        1063 or 1180 or 1183 or 1186 or 1189 or 1240 => 61,
        1192 or 1195 or 1198 or 1201 or 1243 or 1246 => 65,
        1069 or 1204 or 1207 or 1249 or 1252 => 66,
        1066 or 1114 or 1117 or 1210 or 1213 or 1216 or 1219 or 1222 or 1225 or 1237 or 1255 or 1258 or 1261 or 1264 => 71,
        1087 or 1273 or 1276 or 1279 or 1282 => 95,
        _ => -1
    };

    public static string Icon(int wmo) => wmo switch
    {
        0 => "☀️",
        1 or 2 => "🌤️",
        3 => "☁️",
        45 or 48 => "🌫️",
        51 or 53 or 55 or 56 or 57 => "🌦️",
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "🌧️",
        71 or 73 or 75 or 77 or 85 or 86 => "❄️",
        95 or 96 or 99 => "⛈️",
        _ => "🌡️"
    };

    /// <summary>Indice américain (1 à 6) en mots ; 0 : donnée absente.</summary>
    public static string AqiLabel(int index) => index switch
    {
        1 => "Bonne", 2 => "Modérée", 3 => "Médiocre", 4 => "Mauvaise", 5 => "Très mauvaise", 6 => "Dangereuse", _ => ""
    };
}
