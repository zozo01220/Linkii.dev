using System.Text.Json;
using System.Text.Json.Nodes;

namespace Linkii.Poc;

// =====================================================================================
//  Écriture dans les agendas (application « Réservation de salle ») : seulement pour créer une réservation faite sur la tablette,
//  et supprimer les événements que Linkivi a créés lui-même (test de réservation, invitation refusée par la salle). Jamais un autre rendez-vous.
//  Microsoft 365 (Graph) et Google : « direct » = dans l'agenda de la salle ; « invite » = dans l'agenda du compte, la salle invitée comme ressource.
// =====================================================================================

/// <summary>Ce que Linkivi écrit : un rendez-vous marqué de l'identifiant de la réservation (reconnaissable, sans doublon si la requête est rejouée).</summary>
public record BookRequest(string Subject, string Body, DateTime StartUtc, DateTime EndUtc, Guid BookingId, string RoomName, string Mode);

/// <summary>Écriture refusée. Rights : le compte n'a pas (ou plus) le droit d'écrire ; sinon panne passagère ou refus du fournisseur.</summary>
public class CalendarWriteException(string message, bool rights = false) : Exception(message)
{
    public bool Rights { get; } = rights;
}

public interface IWritableCalendarConnector
{
    string Id { get; }
    /// <summary>Crée le rendez-vous ; rend l'identifiant de l'événement chez le fournisseur.</summary>
    Task<string> Create(CalendarSource src, Tenant t, BookRequest r);
    /// <summary>Supprime un événement créé par Linkivi (test, invitation refusée).</summary>
    Task Delete(CalendarSource src, Tenant t, string eventId, string mode);
    /// <summary>Réponse de la salle à une invitation : accepted | declined | tentative | none.</summary>
    Task<string> Response(CalendarSource src, Tenant t, string eventId);
}

public static class CalendarWrites
{
    /// <summary>Propriété nommée Graph (espace PS_PUBLIC_STRINGS) qui porte l'identifiant de la réservation Linkivi.</summary>
    public const string GraphProperty = "String {00020329-0000-0000-C000-000000000046} Name LinkiviBooking";
    public const string GoogleProperty = "linkiviBooking";

    internal static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Message lisible d'une réponse d'erreur (Graph ou Google).</summary>
    internal static string Describe(string body) => GoogleConnector.Describe(body);

    internal static CalendarWriteException Refused(string provider, int status, string body) =>
        new($"{provider} ({status}) : {Describe(body)}", rights: status is 401 or 403 || body.Contains("ErrorAccessDenied") || body.Contains("insufficientPermissions") || body.Contains("requiredAccessLevel"));

    /// <summary>Corps Graph d'un rendez-vous Linkivi (Graph : https://learn.microsoft.com/graph/api/user-post-events).</summary>
    public static string GraphEvent(BookRequest r, string? roomEmail)
    {
        var o = new JsonObject
        {
            ["subject"] = r.Subject,
            ["body"] = new JsonObject { ["contentType"] = "text", ["content"] = r.Body },
            ["start"] = new JsonObject { ["dateTime"] = Iso(r.StartUtc), ["timeZone"] = "UTC" },
            ["end"] = new JsonObject { ["dateTime"] = Iso(r.EndUtc), ["timeZone"] = "UTC" },
            ["showAs"] = "busy",
            ["isReminderOn"] = false,
            ["transactionId"] = r.BookingId.ToString(),   // une requête rejouée ne crée pas de doublon
            ["singleValueExtendedProperties"] = new JsonArray(new JsonObject { ["id"] = GraphProperty, ["value"] = r.BookingId.ToString("N") })
        };
        if (roomEmail != null)
        {
            o["attendees"] = new JsonArray(new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = roomEmail, ["name"] = r.RoomName }, ["type"] = "resource" });
            o["location"] = new JsonObject { ["displayName"] = r.RoomName, ["locationEmailAddress"] = roomEmail, ["locationType"] = "conferenceRoom" };
            o["responseRequested"] = true;
        }
        return o.ToJsonString();
    }

    /// <summary>Corps Google d'un rendez-vous Linkivi (identifiant d'événement dérivé de la réservation : base32hex accepte les chiffres hexadécimaux).</summary>
    public static string GoogleEvent(BookRequest r, string? roomEmail)
    {
        var o = new JsonObject
        {
            ["id"] = "lk" + r.BookingId.ToString("N"),
            ["summary"] = r.Subject,
            ["description"] = r.Body,
            ["start"] = new JsonObject { ["dateTime"] = Iso(r.StartUtc) + "Z" },
            ["end"] = new JsonObject { ["dateTime"] = Iso(r.EndUtc) + "Z" },
            ["transparency"] = "opaque",
            ["reminders"] = new JsonObject { ["useDefault"] = false },
            ["extendedProperties"] = new JsonObject { ["private"] = new JsonObject { [GoogleProperty] = r.BookingId.ToString("N") } }
        };
        if (roomEmail != null)
        {
            o["attendees"] = new JsonArray(new JsonObject { ["email"] = roomEmail, ["resource"] = true, ["displayName"] = r.RoomName });
            o["location"] = r.RoomName;
        }
        return o.ToJsonString();
    }

    /// <summary>Réponse de la salle lue dans un événement Graph (participants) : accepted | declined | tentative | none.</summary>
    public static string GraphResponse(string json, string roomEmail)
    {
        using var d = JsonDocument.Parse(json);
        if (!d.RootElement.TryGetProperty("attendees", out var a)) return "none";
        foreach (var x in a.EnumerateArray())
        {
            var addr = x.TryGetProperty("emailAddress", out var em) && em.TryGetProperty("address", out var ad) ? ad.GetString() : null;
            var type = x.TryGetProperty("type", out var ty) ? ty.GetString() : null;
            if (!string.Equals(addr, roomEmail, StringComparison.OrdinalIgnoreCase) && type != "resource") continue;
            var resp = x.TryGetProperty("status", out var st) && st.TryGetProperty("response", out var r) ? r.GetString() : null;
            return resp switch { "accepted" => "accepted", "declined" => "declined", "tentativelyAccepted" => "tentative", _ => "none" };
        }
        return "none";
    }

    /// <summary>Réponse de la ressource lue dans un événement Google : accepted | declined | tentative | none.</summary>
    public static string GoogleResponse(string json, string roomEmail)
    {
        using var d = JsonDocument.Parse(json);
        if (!d.RootElement.TryGetProperty("attendees", out var a)) return "none";
        foreach (var x in a.EnumerateArray())
        {
            var email = x.TryGetProperty("email", out var em) ? em.GetString() : null;
            var resource = x.TryGetProperty("resource", out var rs) && rs.ValueKind == JsonValueKind.True;
            if (!resource && !string.Equals(email, roomEmail, StringComparison.OrdinalIgnoreCase)) continue;
            return (x.TryGetProperty("responseStatus", out var r) ? r.GetString() : null) switch { "accepted" => "accepted", "declined" => "declined", "tentative" => "tentative", _ => "none" };
        }
        return "none";
    }
}

/// <summary>Microsoft 365 : agenda du compte (« cal:… ») = direct ; adresse d'une boîte de salle = invitation de la salle depuis l'agenda du compte.</summary>
public class M365Writer(GraphService graph) : IWritableCalendarConnector
{
    public string Id => "m365";
    private const string Me = "https://graph.microsoft.com/v1.0/me";

    public async Task<string> Create(CalendarSource src, Tenant t, BookRequest r)
    {
        var a = src.Address.Trim();
        var invite = r.Mode == RoomModes.Invite;
        if (!invite && !a.StartsWith(GraphService.CalendarRefPrefix, StringComparison.Ordinal)) throw new CalendarWriteException("Cet agenda Microsoft ne s'écrit pas directement.");
        var url = invite ? Me + "/events" : $"{Me}/calendars/{Uri.EscapeDataString(a[GraphService.CalendarRefPrefix.Length..])}/events";
        var (status, body) = await graph.Send(t, HttpMethod.Post, url, CalendarWrites.GraphEvent(r, invite ? a : null));
        if (status is not (200 or 201)) throw CalendarWrites.Refused("Microsoft", status, body);
        using var d = JsonDocument.Parse(body);
        return d.RootElement.GetProperty("id").GetString() ?? throw new CalendarWriteException("Réponse de Microsoft sans identifiant.");
    }

    public async Task Delete(CalendarSource src, Tenant t, string eventId, string mode)
    {
        var (status, body) = await graph.Send(t, HttpMethod.Delete, $"{Me}/events/{Uri.EscapeDataString(eventId)}");
        if (status is not (200 or 204 or 404)) throw CalendarWrites.Refused("Microsoft", status, body);
    }

    public async Task<string> Response(CalendarSource src, Tenant t, string eventId)
    {
        var (status, body) = await graph.Send(t, HttpMethod.Get, $"{Me}/events/{Uri.EscapeDataString(eventId)}?$select=attendees,isCancelled");
        if (status == 404) return "declined";   // l'invitation n'existe plus (annulée)
        if (status != 200) throw CalendarWrites.Refused("Microsoft", status, body);
        return CalendarWrites.GraphResponse(body, src.Address.Trim());
    }
}

/// <summary>Google : agenda en écriture = direct ; ressource (salle) = invitation depuis l'agenda principal du compte.</summary>
public class GoogleWriter(SafeHttp http, GoogleAuth auth) : IWritableCalendarConnector
{
    public string Id => "google";
    private const string Api = "https://www.googleapis.com/calendar/v3";

    private async Task<Dictionary<string, string>> Bearer(Tenant t)
    {
        if (!GoogleAuth.Has(t, GoogleAuth.EventsScope)) throw new CalendarWriteException("Le compte Google n'a pas autorisé la création de rendez-vous.", rights: true);
        try { return new() { ["Authorization"] = "Bearer " + await auth.AccessToken(t) }; }
        catch (GoogleAuth.GoogleAuthException ex) { throw new CalendarWriteException(ex.Message, rights: true); }
    }

    private static string CalOf(CalendarSource src, string mode) => mode == RoomModes.Invite ? "primary" : src.Address.Trim();

    public async Task<string> Create(CalendarSource src, Tenant t, BookRequest r)
    {
        var invite = r.Mode == RoomModes.Invite;
        var url = $"{Api}/calendars/{Uri.EscapeDataString(CalOf(src, r.Mode))}/events?sendUpdates={(invite ? "all" : "none")}";
        var (status, body) = await http.Request(HttpMethod.Post, url, CalendarWrites.GoogleEvent(r, invite ? src.Address.Trim() : null), "application/json", await Bearer(t));
        if (status == 409) return "lk" + r.BookingId.ToString("N");   // requête rejouée : l'événement existe déjà
        if (status is not (200 or 201)) throw CalendarWrites.Refused("Google", status, body);
        using var d = JsonDocument.Parse(body);
        return d.RootElement.GetProperty("id").GetString() ?? throw new CalendarWriteException("Réponse de Google sans identifiant.");
    }

    public async Task Delete(CalendarSource src, Tenant t, string eventId, string mode)
    {
        var url = $"{Api}/calendars/{Uri.EscapeDataString(CalOf(src, mode))}/events/{Uri.EscapeDataString(eventId)}?sendUpdates={(mode == RoomModes.Invite ? "all" : "none")}";
        var (status, body) = await http.Request(HttpMethod.Delete, url, headers: await Bearer(t));
        if (status is not (200 or 204 or 404 or 410)) throw CalendarWrites.Refused("Google", status, body);
    }

    public async Task<string> Response(CalendarSource src, Tenant t, string eventId)
    {
        var (status, body) = await http.Request(HttpMethod.Get, $"{Api}/calendars/primary/events/{Uri.EscapeDataString(eventId)}?fields=attendees,status", headers: await Bearer(t));
        if (status is 404 or 410) return "declined";
        if (status != 200) throw CalendarWrites.Refused("Google", status, body);
        return CalendarWrites.GoogleResponse(body, src.Address.Trim());
    }
}
