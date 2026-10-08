using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace Linkii.Poc;

public record EventDto(string Title, DateTime StartUtc, DateTime EndUtc, bool AllDay, string? Location);

// =====================================================================================
//  Calendriers : lien ICS (Outlook « publier le calendrier », Google « adresse iCal ») ou Microsoft 365 (Graph)
//  Toujours lus côté serveur avec un cache court : les écrans ne voient jamais l'URL (souvent secrète).
// =====================================================================================
public class CalendarService(IEnumerable<ICalendarConnector> connectors, ProviderDetector detector)
{
    private readonly ConcurrentDictionary<string, (DateTime At, string Provider, List<EventDto> Events)> _cache = new();

    public async Task<List<EventDto>> GetEvents(CalendarSource src, Tenant tenant, DateTime fromUtc, DateTime toUtc) =>
        (await Read(src, tenant, fromUtc, toUtc)).Events;

    /// <summary>Lit le calendrier avec le connecteur choisi (ou détecté d'après l'adresse) et renvoie aussi le fournisseur utilisé. Cache court : les écrans ne déclenchent pas un appel externe chacun.</summary>
    public async Task<(string Provider, List<EventDto> Events)> Read(CalendarSource src, Tenant tenant, DateTime fromUtc, DateTime toUtc)
    {
        var key = $"{src.CacheKey}|{fromUtc:yyyyMMddHH}|{toUtc:yyyyMMddHH}";
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(20)) return (hit.Provider, hit.Events);

        var provider = src.Provider;
        if (provider is "" or "auto")
        {
            if (string.IsNullOrWhiteSpace(src.Address)) throw new InvalidOperationException("Adresse de la salle non renseignée.");
            provider = await detector.Detect(src.Address)
                ?? throw new InvalidOperationException("Fournisseur de calendrier non reconnu d'après cette adresse. Choisissez-le dans la liste.");
        }
        var connector = connectors.FirstOrDefault(c => c.Id == provider) ?? throw new InvalidOperationException($"Fournisseur de calendrier inconnu : {provider}.");
        var events = await connector.GetEvents(src, tenant, fromUtc, toUtc);
        _cache[key] = (DateTime.UtcNow, provider, events);
        return (provider, events);
    }

    /// <summary>Lit un fichier ICS (récurrences et fuseaux inclus) et renvoie les occurrences de la fenêtre demandée.</summary>
    public static List<EventDto> ParseIcs(string text, DateTime fromUtc, DateTime toUtc)
    {
        var cal = Ical.Net.Calendar.Load(text) ?? throw new InvalidOperationException("Fichier ICS illisible.");
        var list = new List<EventDto>();
        foreach (var occ in cal.GetOccurrences<CalendarEvent>(new CalDateTime(fromUtc, "UTC")).TakeWhile(o => o.Period.StartTime.AsUtc < toUtc))
        {
            if (occ.Source is not CalendarEvent ev) continue;
            var start = occ.Period.StartTime.AsUtc;
            var end = occ.Period.EndTime != null && occ.Period.EndTime.AsUtc > start ? occ.Period.EndTime.AsUtc : start + (ev.End != null && ev.End.AsUtc > ev.Start.AsUtc ? ev.End.AsUtc - ev.Start.AsUtc : TimeSpan.FromHours(1));
            if (end <= fromUtc) continue;
            if (string.Equals(ev.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(ev.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase)) continue;   // « libre » : ne bloque pas la salle
            list.Add(new EventDto(string.IsNullOrWhiteSpace(ev.Summary) ? "(sans titre)" : ev.Summary!, start, end, ev.IsAllDay, ev.Location));
        }
        return list.OrderBy(e => e.StartUtc).ToList();
    }
}

// =====================================================================================
//  Microsoft 365 via Microsoft Graph — application Entra ID, flux « client credentials »
//  Permissions d'application à accorder (consentement administrateur) : Calendars.Read, Place.Read.All
// =====================================================================================
public class GraphService(IHttpClientFactory httpFactory)
{
    private (string Key, string Token, DateTime Expires)? _token;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public static bool Configured(Tenant t) =>
        !string.IsNullOrWhiteSpace(t.MsTenantId) && !string.IsNullOrWhiteSpace(t.MsClientId) && !string.IsNullOrWhiteSpace(t.MsClientSecret);

    private async Task<string> Token(Tenant t)
    {
        if (!Configured(t)) throw new InvalidOperationException("Connecteur Microsoft 365 non configuré (page Données).");
        var key = t.MsTenantId + t.MsClientId + t.MsClientSecret;
        await _lock.WaitAsync();
        try
        {
            if (_token is { } tk && tk.Key == key && tk.Expires > DateTime.UtcNow.AddMinutes(2)) return tk.Token;
            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var resp = await http.PostAsync($"https://login.microsoftonline.com/{Uri.EscapeDataString(t.MsTenantId.Trim())}/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = t.MsClientId.Trim(),
                    ["client_secret"] = t.MsClientSecret.Trim(),
                    ["scope"] = "https://graph.microsoft.com/.default",
                    ["grant_type"] = "client_credentials",
                }));
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("Authentification Microsoft refusée : " + Describe(body));
            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.GetProperty("access_token").GetString()!;
            var secs = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3000;
            _token = (key, token, DateTime.UtcNow.AddSeconds(secs));
            return token;
        }
        finally { _lock.Release(); }
    }

    private static string Describe(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error_description", out var ed)) return ed.GetString()?.Split('\n')[0] ?? body;
            if (d.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m)) return m.GetString() ?? body;
        }
        catch { }
        return body.Length > 200 ? body[..200] : body;
    }

    private async Task<JsonDocument> Get(Tenant t, string url, bool utcPrefer = false)
    {
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(12);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new("Bearer", await Token(t));
        if (utcPrefer) req.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Microsoft Graph ({(int)resp.StatusCode}) : {Describe(body)}");
        return JsonDocument.Parse(body);
    }

    public async Task<List<EventDto>> GetEvents(Tenant t, string mailbox, DateTime fromUtc, DateTime toUtc)
    {
        var url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(mailbox.Trim())}/calendarView" +
                  $"?startDateTime={fromUtc:yyyy-MM-ddTHH:mm:ss}Z&endDateTime={toUtc:yyyy-MM-ddTHH:mm:ss}Z" +
                  "&$select=subject,start,end,isAllDay,isCancelled,showAs,location&$orderby=start/dateTime&$top=100";
        using var doc = await Get(t, url, utcPrefer: true);
        var list = new List<EventDto>();
        foreach (var e in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (e.TryGetProperty("isCancelled", out var c) && c.GetBoolean()) continue;
            if (e.TryGetProperty("showAs", out var sa) && sa.GetString() == "free") continue;
            DateTime P(string name) => DateTime.SpecifyKind(DateTime.Parse(e.GetProperty(name).GetProperty("dateTime").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc);
            var loc = e.TryGetProperty("location", out var l) && l.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
            list.Add(new EventDto(e.TryGetProperty("subject", out var s) ? s.GetString() ?? "(sans titre)" : "(sans titre)", P("start"), P("end"),
                e.TryGetProperty("isAllDay", out var ad) && ad.GetBoolean(), loc));
        }
        return list;
    }

    public record RoomInfo(string Name, string Email, int? Capacity, string? Building);

    /// <summary>Annuaire des salles de l'organisation (Place.Read.All).</summary>
    public async Task<List<RoomInfo>> ListRooms(Tenant t)
    {
        using var doc = await Get(t, "https://graph.microsoft.com/v1.0/places/microsoft.graph.room?$top=200");
        var rooms = new List<RoomInfo>();
        foreach (var r in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var email = r.TryGetProperty("emailAddress", out var em) ? em.GetString() : null;
            if (string.IsNullOrEmpty(email)) continue;
            rooms.Add(new RoomInfo(r.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? email : email, email,
                r.TryGetProperty("capacity", out var cap) && cap.ValueKind == JsonValueKind.Number ? cap.GetInt32() : null,
                r.TryGetProperty("building", out var b) ? b.GetString() : null));
        }
        return rooms.OrderBy(r => r.Name).ToList();
    }

    /// <summary>Vérifie la configuration : obtention du jeton, puis lecture de l'annuaire des salles.</summary>
    public async Task<string> Test(Tenant t)
    {
        await Token(t);
        try
        {
            var rooms = await ListRooms(t);
            return $"Connexion réussie · {rooms.Count} salle(s) trouvée(s).";
        }
        catch (Exception ex)
        {
            return "Jeton obtenu, mais l'annuaire des salles est inaccessible (" + ex.Message + "). Les calendriers peuvent fonctionner si Calendars.Read est accordé.";
        }
    }
}

// =====================================================================================
//  Menu du restaurant : import CSV (séparateur ; ou , — dates 2026-10-05 ou 05.10.2026)
// =====================================================================================
public static class MenuCsv
{
    public record Result(List<MenuRow> Rows, List<string> Errors);

    public static Result Parse(string text)
    {
        var rows = new List<MenuRow>();
        var errors = new List<string>();
        text = text.TrimStart('﻿');
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count == 0) return new(rows, new() { "Fichier vide." });

        var delim = lines[0].Count(c => c == ';') >= lines[0].Count(c => c == ',') ? ';' : ',';
        var header = Split(lines[0], delim).Select(h => Norm(h)).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        int iDate = Col("date", "jour"), iCat = Col("categorie", "category", "type", "service"),
            iName = Col("plat", "nom", "name", "libelle", "designation", "menu"),
            iPrice = Col("prix", "price", "tarif"), iAll = Col("allergenes", "allergens", "allergene");
        if (iDate < 0 || iName < 0) return new(rows, new() { "En-têtes obligatoires manquants : « date » et « plat » (colonnes facultatives : categorie, prix, allergenes)." });

        for (var i = 1; i < lines.Count; i++)
        {
            var c = Split(lines[i], delim);
            string At(int ix) => ix >= 0 && ix < c.Count ? c[ix].Trim() : "";
            var d = At(iDate);
            if (!TryDate(d, out var date))
            {
                errors.Add($"Ligne {i + 1} : date illisible « {d} ».");
                continue;
            }
            var name = At(iName);
            if (name.Length == 0) { errors.Add($"Ligne {i + 1} : plat vide."); continue; }
            rows.Add(new MenuRow { Date = date.ToString("yyyy-MM-dd"), Category = At(iCat), Name = name, Price = At(iPrice), Allergens = At(iAll) });
        }
        return new(rows, errors);
    }

    private static readonly string[] DateFormats = { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yy" };

    /// <summary>Dates acceptées : 2026-10-12 (avec ou sans heure), 12.10.2026, 12/10/2026.</summary>
    internal static bool TryDate(string s, out DateTime date)
    {
        s = s.Trim();
        if (s.Length > 10 && Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}[T ]")) s = s[..10];
        return DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    internal static string Norm(string h)
    {
        var s = h.Trim().Trim('"').ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(s.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray());
    }

    private static List<string> Split(string line, char delim)
    {
        var res = new List<string>();
        var sb = new StringBuilder();
        var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"') { if (q && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = !q; }
            else if (ch == delim && !q) { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        res.Add(sb.ToString());
        return res;
    }
}
