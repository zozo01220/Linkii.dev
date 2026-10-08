using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Linkii.Poc;

// =====================================================================================
//  Connecteurs de calendrier : un par fournisseur, tous derrière la même interface.
//  Ajouter un fournisseur = ajouter une classe et l'enregistrer dans Program.cs.
//  Tout est lu côté serveur : les adresses, mots de passe et clés ne vont jamais sur un écran.
// =====================================================================================

/// <summary>Ce qu'il faut pour lire un calendrier. Les champs inutiles au fournisseur choisi restent vides.</summary>
/// <param name="Provider">auto | m365 | ics | google | caldav | ews</param>
/// <param name="Address">Adresse e-mail de la salle (identifiant du calendrier)</param>
/// <param name="Url">Lien ICS, serveur CalDAV ou adresse EWS</param>
/// <param name="Key">Google : fichier JSON du compte de service</param>
public record CalendarSource(string Provider, string Address, string Url, string User, string Password, string Key)
{
    /// <summary>Clé de cache : les secrets n'y figurent qu'empreinte.</summary>
    public string CacheKey
    {
        get
        {
            var h = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Url + "\n" + User + "\n" + Password + "\n" + Key)))[..16];
            return $"{Provider}|{Address}|{h}";
        }
    }
}

public interface ICalendarConnector
{
    string Id { get; }
    /// <summary>Événements qui bloquent le calendrier dans la fenêtre : annulés et « disponible » exclus.</summary>
    Task<List<EventDto>> GetEvents(CalendarSource src, Tenant tenant, DateTime fromUtc, DateTime toUtc);
}

public static class CalendarProviders
{
    public static string Label(string id) => id switch
    {
        "m365" => "Microsoft 365", "ics" => "lien ICS", "google" => "Google Agenda", "caldav" => "CalDAV", "ews" => "Exchange", _ => id
    };

    internal static string BasicAuth(string user, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));

    internal static string Utc(DateTime d, string format = "yyyy-MM-ddTHH:mm:ssZ") => d.ToUniversalTime().ToString(format, CultureInfo.InvariantCulture);
}

// ---------------------------------------------------------------------------------------------
public class M365Connector(GraphService graph) : ICalendarConnector
{
    public string Id => "m365";

    public Task<List<EventDto>> GetEvents(CalendarSource src, Tenant tenant, DateTime fromUtc, DateTime toUtc)
    {
        if (string.IsNullOrWhiteSpace(src.Address)) throw new InvalidOperationException("Boîte aux lettres Microsoft 365 non renseignée.");
        return graph.GetEvents(tenant, src.Address, fromUtc, toUtc);
    }
}

// ---------------------------------------------------------------------------------------------
public class IcsConnector(SafeHttp http) : ICalendarConnector
{
    public string Id => "ics";

    public async Task<List<EventDto>> GetEvents(CalendarSource src, Tenant tenant, DateTime fromUtc, DateTime toUtc)
    {
        if (string.IsNullOrWhiteSpace(src.Url)) throw new InvalidOperationException("Lien ICS non renseigné.");
        var text = await http.GetString(src.Url);   // SafeHttp : jamais d'adresse du réseau interne
        return CalendarService.ParseIcs(text, fromUtc, toUtc);
    }
}

// ---------------------------------------------------------------------------------------------
/// <summary>
/// Google Agenda avec un compte de service : l'administrateur partage le calendrier de la salle avec l'adresse du compte
/// (« Voir tous les détails des événements » ou plus restreint), puis colle ici le fichier JSON de la clé.
/// </summary>
public class GoogleConnector(SafeHttp http) : ICalendarConnector
{
    public string Id => "google";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";   // fixe : l'adresse du fichier JSON n'est pas suivie
    private readonly ConcurrentDictionary<string, (string Token, DateTime Expires)> _tokens = new();

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<string> Token(string keyJson)
    {
        string email, pem;
        try
        {
            using var doc = JsonDocument.Parse(keyJson);
            email = doc.RootElement.GetProperty("client_email").GetString()!;
            pem = doc.RootElement.GetProperty("private_key").GetString()!;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException("Clé du compte de service Google illisible : collez le fichier JSON complet.");
        }
        if (_tokens.TryGetValue(email, out var hit) && hit.Expires > DateTime.UtcNow.AddMinutes(2)) return hit.Token;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = email, ["scope"] = "https://www.googleapis.com/auth/calendar.readonly", ["aud"] = TokenUrl, ["iat"] = now, ["exp"] = now + 3600
        }));
        string jwt;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            jwt = header + "." + claims + "." + B64(rsa.SignData(Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException("Clé privée du compte de service Google invalide.");
        }

        var body = "grant_type=" + Uri.EscapeDataString("urn:ietf:params:oauth:grant-type:jwt-bearer") + "&assertion=" + Uri.EscapeDataString(jwt);
        var (status, text) = await http.Request(HttpMethod.Post, TokenUrl, body, "application/x-www-form-urlencoded");
        if (status != 200) throw new InvalidOperationException("Authentification Google refusée : " + Describe(text));
        using var res = JsonDocument.Parse(text);
        var token = res.RootElement.GetProperty("access_token").GetString()!;
        var secs = res.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3000;
        _tokens[email] = (token, DateTime.UtcNow.AddSeconds(secs));
        return token;
    }

    internal static string Describe(string body)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error_description", out var ed)) return ed.GetString() ?? body;
            if (d.RootElement.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m)) return m.GetString() ?? body;
                if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? body;
            }
        }
        catch (JsonException) { }
        return body.Length > 200 ? body[..200] : body;
    }

    public async Task<List<EventDto>> GetEvents(CalendarSource src, Tenant tenant, DateTime fromUtc, DateTime toUtc)
    {
        if (string.IsNullOrWhiteSpace(src.Address)) throw new InvalidOperationException("Adresse du calendrier Google non renseignée.");
        if (string.IsNullOrWhiteSpace(src.Key)) throw new InvalidOperationException("Clé du compte de service Google non renseignée.");
        var token = await Token(src.Key);
        var url = $"https://www.googleapis.com/calendar/v3/calendars/{Uri.EscapeDataString(src.Address.Trim())}/events" +
                  $"?timeMin={Uri.EscapeDataString(CalendarProviders.Utc(fromUtc))}&timeMax={Uri.EscapeDataString(CalendarProviders.Utc(toUtc))}" +
                  "&singleEvents=true&orderBy=startTime&maxResults=100&fields=items(summary,start,end,status,transparency,location)";
        var (status, text) = await http.Request(HttpMethod.Get, url, headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + token });
        if (status == 404) throw new InvalidOperationException("Calendrier Google introuvable : vérifiez l'adresse et qu'il est partagé avec le compte de service.");
        if (status != 200) throw new InvalidOperationException($"Google Agenda ({status}) : {Describe(text)}");
        return ParseEvents(text);
    }

    public static List<EventDto> ParseEvents(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<EventDto>();
        if (!doc.RootElement.TryGetProperty("items", out var items)) return list;
        foreach (var e in items.EnumerateArray())
        {
            if (e.TryGetProperty("status", out var st) && st.GetString() == "cancelled") continue;
            if (e.TryGetProperty("transparency", out var tr) && tr.GetString() == "transparent") continue;   // « disponible » : ne bloque pas la salle
            if (!e.TryGetProperty("start", out var s) || !e.TryGetProperty("end", out var en)) continue;
            var allDay = s.TryGetProperty("date", out _);
            DateTime P(JsonElement x) => allDay
                ? DateTime.SpecifyKind(DateTime.ParseExact(x.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc)
                : DateTimeOffset.Parse(x.GetProperty("dateTime").GetString()!, CultureInfo.InvariantCulture).UtcDateTime;
            list.Add(new EventDto(e.TryGetProperty("summary", out var sum) ? sum.GetString() ?? "(sans titre)" : "(sans titre)", P(s), P(en), allDay,
                e.TryGetProperty("location", out var loc) ? loc.GetString() : null));
        }
        return list.OrderBy(x => x.StartUtc).ToList();
    }
}

// ---------------------------------------------------------------------------------------------
/// <summary>CalDAV (Nextcloud, Zimbra, iCloud, Synology, SOGo…) : requête « calendar-query » sur l'adresse de la collection.</summary>
public class CalDavConnector(SafeHttp http) : ICalendarConnector
{
    public string Id => "caldav";

    public async Task<List<EventDto>> GetEvents(CalendarSource src, Tenant tenant, DateTime fromUtc, DateTime toUtc)
    {
        if (string.IsNullOrWhiteSpace(src.Url)) throw new InvalidOperationException("Adresse du calendrier CalDAV non renseignée.");
        var f = "yyyyMMddTHHmmssZ";
        var body = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                   "<c:calendar-query xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop><c:calendar-data/></d:prop>" +
                   "<c:filter><c:comp-filter name=\"VCALENDAR\"><c:comp-filter name=\"VEVENT\">" +
                   $"<c:time-range start=\"{CalendarProviders.Utc(fromUtc, f)}\" end=\"{CalendarProviders.Utc(toUtc, f)}\"/>" +
                   "</c:comp-filter></c:comp-filter></c:filter></c:calendar-query>";
        var headers = new Dictionary<string, string> { ["Depth"] = "1" };
        if (!string.IsNullOrEmpty(src.User)) headers["Authorization"] = CalendarProviders.BasicAuth(src.User, src.Password);
        var (status, text) = await http.Request(new HttpMethod("REPORT"), src.Url, body, "application/xml", headers, maxBytes: 8_000_000);
        if (status is 401 or 403) throw new InvalidOperationException("Identifiants CalDAV refusés.");
        if (status == 404) throw new InvalidOperationException("Calendrier CalDAV introuvable : vérifiez l'adresse.");
        if (status != 207) throw new InvalidOperationException($"Serveur CalDAV : réponse inattendue ({status}).");
        return ParseMultiStatus(text, fromUtc, toUtc);
    }

    public static List<EventDto> ParseMultiStatus(string xml, DateTime fromUtc, DateTime toUtc)
    {
        var list = new List<EventDto>();
        foreach (var data in XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "calendar-data"))
        {
            if (string.IsNullOrWhiteSpace(data.Value)) continue;
            list.AddRange(CalendarService.ParseIcs(data.Value, fromUtc, toUtc));
        }
        return list.OrderBy(e => e.StartUtc).ToList();
    }
}

// ---------------------------------------------------------------------------------------------
/// <summary>Exchange sur site (EWS) : lecture du calendrier d'une boîte avec un compte de service. Le serveur doit être joignable depuis internet.</summary>
public class EwsConnector(SafeHttp http) : ICalendarConnector
{
    public string Id => "ews";

    public async Task<List<EventDto>> GetEvents(CalendarSource src, Tenant tenant, DateTime fromUtc, DateTime toUtc)
    {
        if (string.IsNullOrWhiteSpace(src.Url)) throw new InvalidOperationException("Adresse EWS non renseignée.");
        if (string.IsNullOrWhiteSpace(src.Address)) throw new InvalidOperationException("Adresse de la salle non renseignée.");
        var soap = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:t=\"http://schemas.microsoft.com/exchange/services/2006/types\" xmlns:m=\"http://schemas.microsoft.com/exchange/services/2006/messages\">" +
            "<soap:Header><t:RequestServerVersion Version=\"Exchange2013\"/></soap:Header><soap:Body>" +
            "<m:FindItem Traversal=\"Shallow\"><m:ItemShape><t:BaseShape>IdOnly</t:BaseShape><t:AdditionalProperties>" +
            "<t:FieldURI FieldURI=\"item:Subject\"/><t:FieldURI FieldURI=\"calendar:Start\"/><t:FieldURI FieldURI=\"calendar:End\"/>" +
            "<t:FieldURI FieldURI=\"calendar:IsAllDayEvent\"/><t:FieldURI FieldURI=\"calendar:LegacyFreeBusyStatus\"/><t:FieldURI FieldURI=\"calendar:Location\"/>" +
            "</t:AdditionalProperties></m:ItemShape>" +
            $"<m:CalendarView MaxEntriesReturned=\"100\" StartDate=\"{CalendarProviders.Utc(fromUtc)}\" EndDate=\"{CalendarProviders.Utc(toUtc)}\"/>" +
            $"<m:ParentFolderIds><t:DistinguishedFolderId Id=\"calendar\"><t:Mailbox><t:EmailAddress>{System.Security.SecurityElement.Escape(src.Address.Trim())}</t:EmailAddress></t:Mailbox></t:DistinguishedFolderId></m:ParentFolderIds>" +
            "</m:FindItem></soap:Body></soap:Envelope>";
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(src.User)) headers["Authorization"] = CalendarProviders.BasicAuth(src.User, src.Password);
        var (status, text) = await http.Request(HttpMethod.Post, src.Url, soap, "text/xml", headers, maxBytes: 8_000_000);
        if (status is 401 or 403) throw new InvalidOperationException("Identifiants Exchange refusés.");
        if (status != 200) throw new InvalidOperationException($"Serveur Exchange : réponse inattendue ({status}).");
        return ParseFindItem(text);
    }

    public static List<EventDto> ParseFindItem(string xml)
    {
        var doc = XDocument.Parse(xml);
        var code = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ResponseCode")?.Value;
        if (code != null && code != "NoError") throw new InvalidOperationException("Exchange : " + (code == "ErrorAccessDenied" ? "accès refusé à ce calendrier." : code));
        string? V(XElement item, string name) => item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        DateTime P(string s) => DateTime.SpecifyKind(DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), DateTimeKind.Utc);

        var list = new List<EventDto>();
        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "CalendarItem"))
        {
            if (V(item, "LegacyFreeBusyStatus") == "Free") continue;   // « disponible » : ne bloque pas la salle
            var start = V(item, "Start"); var end = V(item, "End");
            if (start == null || end == null) continue;
            list.Add(new EventDto(V(item, "Subject") ?? "(sans titre)", P(start), P(end), V(item, "IsAllDayEvent") == "true", V(item, "Location")));
        }
        return list.OrderBy(e => e.StartUtc).ToList();
    }
}

// ---------------------------------------------------------------------------------------------
/// <summary>
/// Devine le fournisseur d'après le domaine de l'adresse : enregistrement MX (Google, Microsoft) puis, à défaut,
/// annuaire d'espace de noms Microsoft (couvre les passerelles de messagerie qui masquent le MX).
/// Renvoie null si rien n'est reconnu : l'utilisateur choisit alors lui-même.
/// </summary>
public class ProviderDetector(SafeHttp http)
{
    private readonly ConcurrentDictionary<string, (DateTime At, string? Provider)> _cache = new();

    public async Task<string?> Detect(string address)
    {
        var at = address.LastIndexOf('@');
        var domain = at < 0 ? "" : address[(at + 1)..].Trim().ToLowerInvariant();
        if (!Regex.IsMatch(domain, @"^[a-z0-9]([a-z0-9.-]*[a-z0-9])?\.[a-z]{2,}$")) return null;

        if (_cache.TryGetValue(domain, out var hit) && DateTime.UtcNow - hit.At < (hit.Provider == null ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(1))) return hit.Provider;
        var found = FromMx(await Try(() => http.GetString($"https://dns.google/resolve?name={Uri.EscapeDataString(domain)}&type=MX", 100_000)))
                    ?? FromRealm(await Try(() => http.GetString($"https://login.microsoftonline.com/getuserrealm.srf?login={Uri.EscapeDataString(address.Trim())}&json=1", 100_000)));
        _cache[domain] = (DateTime.UtcNow, found);
        return found;
    }

    private static async Task<string?> Try(Func<Task<string>> f)
    {
        try { return await f(); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { return null; }
    }

    public static string? FromMx(string? json)
    {
        if (json == null) return null;
        try
        {
            using var d = JsonDocument.Parse(json);
            if (!d.RootElement.TryGetProperty("Answer", out var answers)) return null;
            var hosts = answers.EnumerateArray().Select(a => a.TryGetProperty("data", out var x) ? x.GetString() ?? "" : "").ToList();
            if (hosts.Any(h => h.Contains("google.com", StringComparison.OrdinalIgnoreCase) || h.Contains("googlemail.com", StringComparison.OrdinalIgnoreCase))) return "google";
            if (hosts.Any(h => h.Contains("outlook.com", StringComparison.OrdinalIgnoreCase))) return "m365";
        }
        catch (JsonException) { }
        return null;
    }

    public static string? FromRealm(string? json)
    {
        if (json == null) return null;
        try
        {
            using var d = JsonDocument.Parse(json);
            var type = d.RootElement.TryGetProperty("NameSpaceType", out var t) ? t.GetString() : null;
            return type is "Managed" or "Federated" ? "m365" : null;
        }
        catch (JsonException) { return null; }
    }
}
