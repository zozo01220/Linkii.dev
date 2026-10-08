using System.Text.Json;
using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Connecteur de remplacement : renvoie des événements fixes, avec des intitulés sensibles.</summary>
class FakeConnector(string id, params EventDto[] events) : ICalendarConnector
{
    public string Id => id;
    public Task<List<EventDto>> GetEvents(CalendarSource src, Tenant tenant, DateTime fromUtc, DateTime toUtc) => Task.FromResult(events.ToList());
}

public class RoomTests
{
    static readonly DateTime From = new(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime To = From.AddDays(2);

    const string Ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//EN\r\nBEGIN:VEVENT\r\nUID:1\r\nDTSTAMP:20261001T000000Z\r\nDTSTART:20261006T130000Z\r\nDTEND:20261006T140000Z\r\nSUMMARY:Réunion\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    // Les manifestes sont lus depuis les sources, quel que soit le dossier de sortie de la build.
    static string AppsDir([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!, "Linkii.Poc", "Apps");

    [Fact]
    public void CalDav_reads_every_calendar_data_of_the_multistatus()
    {
        var xml = "<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">" +
                  "<d:response><d:propstat><d:prop><c:calendar-data>" + Ics + "</c:calendar-data></d:prop></d:propstat></d:response>" +
                  "<d:response><d:propstat><d:prop><c:calendar-data></c:calendar-data></d:prop></d:propstat></d:response></d:multistatus>";
        var ev = CalDavConnector.ParseMultiStatus(xml, From, To);
        var e = Assert.Single(ev);
        Assert.Equal(new DateTime(2026, 10, 6, 13, 0, 0, DateTimeKind.Utc), e.StartUtc);
        Assert.Equal(new DateTime(2026, 10, 6, 14, 0, 0, DateTimeKind.Utc), e.EndUtc);
    }

    [Fact]
    public void Ews_skips_free_items_and_reports_errors()
    {
        const string ns = "xmlns:t=\"http://schemas.microsoft.com/exchange/services/2006/types\" xmlns:m=\"http://schemas.microsoft.com/exchange/services/2006/messages\"";
        var ok = $"<Envelope><Body><m:FindItemResponse {ns}><m:ResponseMessages><m:FindItemResponseMessage><m:ResponseCode>NoError</m:ResponseCode><m:RootFolder><t:Items>" +
                 "<t:CalendarItem><t:Subject>Secret</t:Subject><t:Start>2026-10-06T13:00:00Z</t:Start><t:End>2026-10-06T14:00:00Z</t:End><t:IsAllDayEvent>false</t:IsAllDayEvent><t:LegacyFreeBusyStatus>Busy</t:LegacyFreeBusyStatus></t:CalendarItem>" +
                 "<t:CalendarItem><t:Subject>Libre</t:Subject><t:Start>2026-10-06T15:00:00Z</t:Start><t:End>2026-10-06T16:00:00Z</t:End><t:IsAllDayEvent>false</t:IsAllDayEvent><t:LegacyFreeBusyStatus>Free</t:LegacyFreeBusyStatus></t:CalendarItem>" +
                 "</t:Items></m:RootFolder></m:FindItemResponseMessage></m:ResponseMessages></m:FindItemResponse></Body></Envelope>";
        var e = Assert.Single(EwsConnector.ParseFindItem(ok));
        Assert.Equal(new DateTime(2026, 10, 6, 13, 0, 0, DateTimeKind.Utc), e.StartUtc);

        var denied = ok.Replace("NoError", "ErrorAccessDenied");
        Assert.Contains("accès refusé", Assert.Throws<InvalidOperationException>(() => EwsConnector.ParseFindItem(denied)).Message);
    }

    [Fact]
    public void Google_skips_cancelled_and_transparent_and_handles_all_day()
    {
        var json = """
        { "items": [
          { "summary": "A", "start": { "dateTime": "2026-10-06T15:00:00+02:00" }, "end": { "dateTime": "2026-10-06T16:00:00+02:00" } },
          { "summary": "B", "status": "cancelled", "start": { "dateTime": "2026-10-06T10:00:00Z" }, "end": { "dateTime": "2026-10-06T11:00:00Z" } },
          { "summary": "C", "transparency": "transparent", "start": { "dateTime": "2026-10-06T10:00:00Z" }, "end": { "dateTime": "2026-10-06T11:00:00Z" } },
          { "summary": "D", "start": { "date": "2026-10-07" }, "end": { "date": "2026-10-08" } }
        ] }
        """;
        var ev = GoogleConnector.ParseEvents(json);
        Assert.Equal(2, ev.Count);
        Assert.Equal(new DateTime(2026, 10, 6, 13, 0, 0, DateTimeKind.Utc), ev[0].StartUtc);   // +02:00 converti en UTC
        Assert.True(ev[1].AllDay);
    }

    [Fact]
    public void Detector_recognises_google_and_microsoft()
    {
        Assert.Equal("google", ProviderDetector.FromMx("{\"Answer\":[{\"data\":\"1 aspmx.l.google.com.\"}]}"));
        Assert.Equal("m365", ProviderDetector.FromMx("{\"Answer\":[{\"data\":\"0 org-ch.mail.protection.outlook.com.\"}]}"));
        Assert.Null(ProviderDetector.FromMx("{\"Answer\":[{\"data\":\"10 mx.exemple.ch.\"}]}"));
        Assert.Null(ProviderDetector.FromMx("{\"Status\":3}"));
        Assert.Equal("m365", ProviderDetector.FromRealm("{\"NameSpaceType\":\"Managed\"}"));
        Assert.Null(ProviderDetector.FromRealm("{\"NameSpaceType\":\"Unknown\"}"));
        Assert.Null(ProviderDetector.FromRealm("pas du json"));
    }

    [Fact]
    public async Task Room_data_never_contains_meeting_titles()
    {
        var secret = "Entretien confidentiel Dupont";
        var now = DateTime.UtcNow;
        var fake = new FakeConnector("m365", new EventDto(secret, now.AddMinutes(-10), now.AddMinutes(20), false, "Bureau du directeur"));
        var cal = new CalendarService(new ICalendarConnector[] { fake }, new ProviderDetector(new SafeHttp()));
        var provider = new RoomProvider(cal);
        var rt = new AppRuntime(new Tenant { Timezone = "Europe/Zurich" },
            new Dictionary<string, string> { ["title"] = "Salle A101", ["source"] = "m365", ["address"] = "salle-a101@exemple.ch" });

        var json = JsonSerializer.Serialize(await provider.Fetch(rt));

        Assert.DoesNotContain("Dupont", json);
        Assert.DoesNotContain("directeur", json);
        Assert.Contains("Salle A101", json);
        Assert.Contains("serverNow", json);
        Assert.StartsWith("Salle lue (Microsoft 365) · occupée jusqu'à", await provider.Test(rt));
    }

    [Fact]
    public async Task Auto_source_without_a_recognised_provider_asks_the_user_to_choose()
    {
        var cal = new CalendarService(new ICalendarConnector[] { new FakeConnector("m365") }, new ProviderDetector(new SafeHttp()));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cal.Read(new CalendarSource("auto", "pas-une-adresse", "", "", "", ""), new Tenant(), From, To));
        Assert.Contains("Choisissez", ex.Message);
    }

    [Fact]
    public void The_room_manifest_is_valid()
    {
        var catalog = new AppCatalog(AppsDir(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var room = catalog.Find("room");
        Assert.NotNull(room);
        Assert.Empty(AppCatalog.Check(room!));
        Assert.Equal("room", room!.Provider);

        // sans valeur : nom et adresse sont exigés ; avec le fournisseur ICS, le lien remplace l'adresse
        var v = AppCatalog.WithDefaults(room, null);
        var errors = AppCatalog.Validate(room, v, new HashSet<string>());
        Assert.Contains(errors, e => e.Contains("Nom affiché"));
        Assert.Contains(errors, e => e.Contains("Adresse e-mail"));
        v["source"] = "ics";
        errors = AppCatalog.Validate(room, v, new HashSet<string>());
        Assert.DoesNotContain(errors, e => e.Contains("Adresse e-mail"));
        Assert.Contains(errors, e => e.Contains("Lien ICS"));
    }
}
