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
//  Microsoft 365 via Microsoft Graph — compte Microsoft connecté par l'organisation (MicrosoftAuth), permissions déléguées en lecture seule
//  Agendas : Calendars.Read, Calendars.Read.Shared ; fichiers : Files.Read.All (comptes professionnels, scolaires ou personnels)
// =====================================================================================
public class GraphService(IHttpClientFactory httpFactory, MicrosoftAuth auth)
{
    /// <summary>Préfixe d'une référence de calendrier du compte connecté (sinon : adresse e-mail d'une boîte ou d'une salle).</summary>
    public const string CalendarRefPrefix = "cal:";

    public static string Describe(string body)
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

    /// <summary>Lecture Graph avec le compte Microsoft connecté (calendriers, annuaire des salles, fichiers des Drives).</summary>
    public async Task<JsonDocument> Get(Tenant t, string url, bool utcPrefer = false)
    {
        string token;
        try { token = await auth.AccessToken(t); }
        catch (MicrosoftAuth.MicrosoftAuthException ex) { throw new InvalidOperationException(ex.Message); }
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(12);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new("Bearer", token);
        if (utcPrefer) req.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Microsoft Graph ({(int)resp.StatusCode}) : {Describe(body)}");
        return JsonDocument.Parse(body);
    }

    /// <param name="calendarRef">« cal:{id} » : un calendrier du compte connecté ; sinon l'adresse d'une boîte ou d'une salle.</param>
    public async Task<List<EventDto>> GetEvents(Tenant t, string calendarRef, DateTime fromUtc, DateTime toUtc)
    {
        var r = calendarRef.Trim();
        var root = r.StartsWith(CalendarRefPrefix, StringComparison.Ordinal)
            ? $"https://graph.microsoft.com/v1.0/me/calendars/{Uri.EscapeDataString(r[CalendarRefPrefix.Length..])}"
            : $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(r)}";
        var url = root + $"/calendarView?startDateTime={fromUtc:yyyy-MM-ddTHH:mm:ss}Z&endDateTime={toUtc:yyyy-MM-ddTHH:mm:ss}Z" +
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

    public record CalendarInfo(string Id, string Name, string? Owner, bool IsDefault);

    /// <summary>Agendas du compte connecté : les siens et ceux partagés avec lui.</summary>
    public async Task<List<CalendarInfo>> ListCalendars(Tenant t)
    {
        using var doc = await Get(t, "https://graph.microsoft.com/v1.0/me/calendars?$select=id,name,isDefaultCalendar,owner&$top=100");
        var list = new List<CalendarInfo>();
        foreach (var c in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var id = c.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (string.IsNullOrEmpty(id)) continue;
            var owner = c.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty("address", out var ad) ? ad.GetString() : null;
            list.Add(new CalendarInfo(id, c.TryGetProperty("name", out var n) ? n.GetString() ?? "Agenda" : "Agenda", owner,
                c.TryGetProperty("isDefaultCalendar", out var d) && d.ValueKind == JsonValueKind.True));
        }
        return list.OrderByDescending(c => c.IsDefault).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public record RoomInfo(string Name, string Email, int? Capacity, string? Building);

    /// <summary>Annuaire des salles de l'organisation. Demande Place.Read.All, que la connexion ne réclame plus (inexistant pour les comptes personnels) : sans lui, la liste reste vide et l'adresse de la salle se saisit.</summary>
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

    /// <summary>Vérifie la connexion : lecture des agendas du compte, puis de l'annuaire des salles.</summary>
    public async Task<string> Test(Tenant t)
    {
        var cals = await ListCalendars(t);
        var who = t.MsUser.Length > 0 ? t.MsUser : "le compte connecté";
        try
        {
            var rooms = await ListRooms(t);
            return $"Connexion réussie · {cals.Count} agenda{(cals.Count > 1 ? "s" : "")} pour {who}, {rooms.Count} salle{(rooms.Count > 1 ? "s" : "")} dans l'annuaire.";
        }
        catch (InvalidOperationException)
        {
            return $"Connexion réussie · {cals.Count} agenda{(cals.Count > 1 ? "s" : "")} pour {who}. L'annuaire des salles n'est pas accessible : saisissez l'adresse d'une salle.";
        }
    }
}
