using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Linkii.Poc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Linkii.Poc.Tests;

internal sealed class RoomEnv : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
{
    public string WebRootPath { get; set; } = "";
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    public string ApplicationName { get; set; } = "Linkii.Poc";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    public string ContentRootPath { get; set; } = Path.GetTempPath();
    public string EnvironmentName { get; set; } = "Development";
}

/// <summary>Faux agenda : les créneaux de chaque calendrier sont fixés par le test (aucun appel réseau).</summary>
public class FakeCalendar(string id) : ICalendarConnector
{
    public string Id => id;
    public List<EventDto> Events { get; } = new();
    public Task<List<EventDto>> GetEvents(CalendarSource src, Tenant tenant, DateTime fromUtc, DateTime toUtc) =>
        Task.FromResult(Events.Where(e => e.EndUtc > fromUtc && e.StartUtc < toUtc).ToList());
}

/// <summary>Faux écrivain Microsoft : garde ce qu'on lui demande, répond comme la salle le veut, ou refuse faute de droit.</summary>
public class FakeWriter(string id) : IWritableCalendarConnector
{
    public string Id => id;
    public List<BookRequest> Created { get; } = new();
    public List<string> Deleted { get; } = new();
    public string Answer { get; set; } = "accepted";
    public bool Refuse { get; set; }
    public Task<string> Create(CalendarSource src, Tenant t, BookRequest r)
    {
        if (Refuse) throw new CalendarWriteException("Microsoft (403) : accès refusé", rights: true);
        Created.Add(r);
        return Task.FromResult("evt-" + Created.Count);
    }
    public Task Delete(CalendarSource src, Tenant t, string eventId, string mode) { Deleted.Add(eventId); return Task.CompletedTask; }
    public Task<string> Response(CalendarSource src, Tenant t, string eventId) => Task.FromResult(Answer);
}

/// <summary>Une organisation avec l'application « Réservation de salle » : une salle ICS (réservations locales), une salle Microsoft (invitation), une salle en affichage seul, quatre tablettes.</summary>
public class RoomPlatform : WebApplicationFactory<Program>
{
    public readonly string Dir = Path.Combine(Path.GetTempPath(), "linkii-rooms-" + Guid.NewGuid().ToString("N"));
    public readonly FakeCalendar Ics = new("ics"), Ms = new("m365"), Dav = new("caldav");
    public readonly FakeWriter Writer = new("m365");
    public Tenant T = null!, Other = null!;
    public Guid IcsRoom, MsRoom, ReadRoom;
    public const string Door1 = "door-1", Door2 = "door-2", MsDoor = "door-ms", ReadDoor = "door-read", Plain = "plain-1", OtherDoor = "door-other";

    public RoomPlatform()
    {
        Environment.SetEnvironmentVariable("Linkii__DataDir", Dir);
        var store = Services.GetRequiredService<JsonStore>();
        var box = Services.GetRequiredService<SecretBox>();
        store.Write(db =>
        {
            var r = db.Resellers.First(x => x.IsDefault);
            T = new Tenant { ResellerId = r.Id, Name = "Salles SA", Timezone = "Europe/Zurich", MsRefreshToken = "x", MsScopes = "Calendars.Read Calendars.ReadWrite", MsUser = "it@acme.ch" };
            Other = new Tenant { ResellerId = r.Id, Name = "Autre" };
            T.RoomApp = new RoomAppSettings { Days = 127, FromMin = 0, ToMin = 24 * 60, Sources = { ["ics"] = RoomPerms.Local, ["m365"] = RoomPerms.Write } };
            var ics = new SharedCalendar { Source = "ics", Name = "Salle de conférence", Ref = box.Protect("https://exemple.test/salle.ics"), Room = true, RoomCap = 30, RoomWriteMode = RoomModes.Local };
            var ms = new SharedCalendar { Source = "m365", Name = "Salle A101", Ref = "salle-a101@acme.ch", Room = true, RoomWriteMode = RoomModes.Invite };
            var ro = new SharedCalendar { Source = "caldav", Name = "Salle Léman", Ref = "https://dav.exemple.test/salles/leman/", Room = true, RoomWriteMode = RoomModes.None };   // CalDAV : affichage seul en V1
            (IcsRoom, MsRoom, ReadRoom) = (ics.Id, ms.Id, ro.Id);
            T.SharedCalendars.AddRange(new[] { ics, ms, ro });
            T.CalendarAccounts.Add(new CalendarAccount { Source = "ics", Enabled = true });
            db.Clients.AddRange(new[] { T, Other });
            db.AppInstalls.Add(new AppInstall { ClientId = T.Id, AppId = RoomRules.AppId });
            db.Screens.Add(new Screen { ClientId = T.Id, Name = "Porte conférence", Token = Door1, Room = new RoomScreen(), RoomCalendarId = ics.Id });
            db.Screens.Add(new Screen { ClientId = T.Id, Name = "Accueil conférence", Token = Door2, Room = new RoomScreen(), RoomCalendarId = ics.Id });
            db.Screens.Add(new Screen { ClientId = T.Id, Name = "Porte A101", Token = MsDoor, Room = new RoomScreen(), RoomCalendarId = ms.Id });
            db.Screens.Add(new Screen { ClientId = T.Id, Name = "Porte Léman", Token = ReadDoor, Room = new RoomScreen(), RoomCalendarId = ro.Id });
            db.Screens.Add(new Screen { ClientId = T.Id, Name = "Hall", Token = Plain });
            db.Screens.Add(new Screen { ClientId = Other.Id, Name = "Porte d'ailleurs", Token = OtherDoor, Room = new RoomScreen(), RoomCalendarId = ics.Id });   // pointe sur une salle d'une autre organisation
        });
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseSetting("Linkii:DataDir", Dir).ConfigureTestServices(s =>
    {
        s.RemoveAll<ICalendarConnector>();
        s.AddSingleton<ICalendarConnector>(Ics);
        s.AddSingleton<ICalendarConnector>(Ms);
        s.AddSingleton<ICalendarConnector>(Dav);
        s.RemoveAll<IWritableCalendarConnector>();
        s.AddSingleton<IWritableCalendarConnector>(Writer);
        s.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>();   // pas de tâches de fond : le test appelle CheckPending lui-même
    });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Dir, true); } catch { }
    }
}

/// <summary>Réservation de salle de bout en bout : porte des tablettes, réservation par le serveur, invitations, droits perdus, flux du player.</summary>
[Collection("platform")]   // en série avec les autres bancs : le dossier de données passe aussi par l'environnement
public class RoomBookingTests : IClassFixture<RoomPlatform>
{
    private readonly RoomPlatform p;
    public RoomBookingTests(RoomPlatform p)
    {
        this.p = p;
        p.Ics.Events.Clear(); p.Ms.Events.Clear(); p.Writer.Created.Clear(); p.Writer.Deleted.Clear(); p.Writer.Refuse = false; p.Writer.Answer = "accepted";
        Rooms.ResetLimits();
        Store.Write(db =>
        {
            var t = db.Clients.First(c => c.Id == p.T.Id);
            t.RoomBookings.Clear();
            foreach (var c in t.SharedCalendars) c.Off = false;
            t.SharedCalendars.First(c => c.Id == p.MsRoom).RoomWriteMode = RoomModes.Invite;
            t.SharedCalendars.First(c => c.Id == p.IcsRoom).RoomWriteMode = RoomModes.Local;
            t.SharedCalendars.First(c => c.Id == p.ReadRoom).RoomWriteMode = RoomModes.None;
            t.RoomApp!.LostRights.Clear();
            t.MsRefreshToken = "x";
            if (!db.AppInstalls.Any(a => a.ClientId == t.Id && a.AppId == RoomRules.AppId)) db.AppInstalls.Add(new AppInstall { ClientId = t.Id, AppId = RoomRules.AppId });
        });
        foreach (var id in new[] { p.IcsRoom, p.MsRoom, p.ReadRoom }) Status.Forget(id);
    }

    private JsonStore Store => p.Services.GetRequiredService<JsonStore>();
    private RoomBookingService Rooms => p.Services.GetRequiredService<RoomBookingService>();
    private RoomStatusService Status => p.Services.GetRequiredService<RoomStatusService>();
    private HttpClient C => p.CreateClient();

    private static HttpRequestMessage Req(HttpMethod m, string url, string token, object? body = null)
    {
        var r = new HttpRequestMessage(m, url) { Content = body == null ? null : JsonContent.Create(body) };
        r.Headers.Add("X-Token", token);
        return r;
    }

    private async Task<JsonElement> Door(string token)
    {
        var r = await C.SendAsync(Req(HttpMethod.Get, "/api/player/room", token));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private Task<HttpResponseMessage> Book(string token, int minutes) => C.SendAsync(Req(HttpMethod.Post, "/api/player/room/book", token, new { minutes }));

    [Fact]
    public async Task The_door_gets_its_room_the_durations_that_fit_and_no_title_by_default()
    {
        var now = DateTime.UtcNow;
        p.Ics.Events.Add(new EventDto("Comité de direction", now.AddMinutes(40), now.AddMinutes(100), false, null));
        var d = await Door(RoomPlatform.Door1);
        Assert.Equal("ok", d.GetProperty("state").GetString());
        Assert.True(d.GetProperty("canBook").GetBoolean());
        Assert.Equal("Salle de conférence", d.GetProperty("room").GetProperty("name").GetString());
        var opts = d.GetProperty("options").EnumerateArray().ToDictionary(o => o.GetProperty("min").GetInt32(), o => o.GetProperty("ok").GetBoolean());
        Assert.True(opts[30]);
        Assert.False(opts[60]);   // le comité commence dans 40 min
        Assert.Equal("", d.GetProperty("events")[0].GetProperty("title").GetString());   // titres masqués : seulement « Occupé »
    }

    [Fact]
    public async Task Booking_a_linkivi_room_holds_it_for_every_screen_of_the_room_and_a_second_tap_loses()
    {
        var r = await Book(RoomPlatform.Door1, 30);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var b = Store.Read(db => db.Clients.First(c => c.Id == p.T.Id).RoomBookings.Single());
        Assert.Equal(RoomBooking.Confirmed, b.State);
        Assert.Equal(RoomModes.Local, b.Mode);
        Assert.Equal(30, (int)(b.EndUtc - b.StartUtc).TotalMinutes);

        var other = await Door(RoomPlatform.Door2);   // l'autre tablette de la même salle
        var ev = other.GetProperty("events").EnumerateArray().Single();
        Assert.True(ev.GetProperty("linkivi").GetBoolean());
        Assert.Equal("Réservé sur place", ev.GetProperty("title").GetString());   // titre générique, montré même titres masqués
        Assert.Empty(other.GetProperty("options").EnumerateArray());              // occupée : aucune durée

        var lost = await Book(RoomPlatform.Door2, 15);
        Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
    }

    [Fact]
    public async Task A_screen_books_at_most_once_every_ten_seconds()
    {
        Assert.Equal(HttpStatusCode.OK, (await Book(RoomPlatform.Door1, 15)).StatusCode);
        Store.Write(db => db.Clients.First(c => c.Id == p.T.Id).RoomBookings.Clear());
        Status.Forget(p.IcsRoom);
        Assert.Equal((HttpStatusCode)429, (await Book(RoomPlatform.Door1, 15)).StatusCode);
    }

    [Fact]
    public async Task Display_only_rooms_disabled_rooms_plain_screens_and_a_removed_app_refuse_bookings()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Book(RoomPlatform.ReadDoor, 30)).StatusCode);
        Assert.False((await Door(RoomPlatform.ReadDoor)).GetProperty("canBook").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await Book(RoomPlatform.Plain, 30)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Book(RoomPlatform.Door1, 7)).StatusCode);

        Store.Write(db => db.Clients.First(c => c.Id == p.T.Id).SharedCalendars.First(c => c.Id == p.IcsRoom).Off = true);
        Assert.Equal("off", (await Door(RoomPlatform.Door1)).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Book(RoomPlatform.Door1, 30)).StatusCode);

        Store.Write(db => db.AppInstalls.RemoveAll(a => a.ClientId == p.T.Id && a.AppId == RoomRules.AppId));
        Assert.Equal("retired", (await Door(RoomPlatform.Door2)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_screen_of_another_organisation_never_reaches_this_room()
    {
        var d = await Door(RoomPlatform.OtherDoor);
        Assert.NotEqual("ok", d.GetProperty("state").GetString());   // la salle n'est pas dans son organisation (et l'application n'y est pas)
        Assert.False(d.TryGetProperty("events", out _));
        Assert.Equal(HttpStatusCode.Forbidden, (await Book(RoomPlatform.OtherDoor, 30)).StatusCode);
        Assert.Empty(Store.Read(db => db.Clients.First(c => c.Id == p.T.Id).RoomBookings));
    }

    [Fact]
    public async Task A_microsoft_room_is_invited_and_a_refusal_removes_linkivi_s_own_event()
    {
        Assert.Equal(HttpStatusCode.OK, (await Book(RoomPlatform.MsDoor, 45)).StatusCode);
        var req = Assert.Single(p.Writer.Created);
        Assert.Equal(RoomModes.Invite, req.Mode);
        Assert.Equal(RoomRules.DefaultTitle, req.Subject);
        Assert.Equal(RoomBooking.Pending, Store.Read(db => db.Clients.First(c => c.Id == p.T.Id).RoomBookings.Single().State));

        Store.Write(db => db.Clients.First(c => c.Id == p.T.Id).RoomBookings.Single().CreatedUtc = DateTime.UtcNow.AddMinutes(-1));
        p.Writer.Answer = "declined";
        await Rooms.CheckPending();
        Assert.Equal(RoomBooking.Refused, Store.Read(db => db.Clients.First(c => c.Id == p.T.Id).RoomBookings.Single().State));
        Assert.Equal("evt-1", Assert.Single(p.Writer.Deleted));
        Assert.Empty((await Door(RoomPlatform.MsDoor)).GetProperty("events").EnumerateArray());   // la salle est de nouveau libre
    }

    [Fact]
    public async Task An_accepted_invitation_is_confirmed()
    {
        Assert.Equal(HttpStatusCode.OK, (await Book(RoomPlatform.MsDoor, 30)).StatusCode);
        Store.Write(db => db.Clients.First(c => c.Id == p.T.Id).RoomBookings.Single().CreatedUtc = DateTime.UtcNow.AddMinutes(-1));
        await Rooms.CheckPending();
        Assert.Equal(RoomBooking.Confirmed, Store.Read(db => db.Clients.First(c => c.Id == p.T.Id).RoomBookings.Single().State));
        Assert.Empty(p.Writer.Deleted);
    }

    [Fact]
    public async Task A_refused_write_never_says_booked_and_a_revoked_account_turns_the_source_to_display_only()
    {
        p.Writer.Refuse = true;
        Store.Write(db => db.Clients.First(c => c.Id == p.T.Id).MsRefreshToken = "");   // jeton retiré chez Microsoft
        var r = await Book(RoomPlatform.MsDoor, 30);
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        var t = Store.Read(db => db.Clients.First(c => c.Id == p.T.Id));
        Assert.Equal(RoomBooking.Failed, t.RoomBookings.Single().State);
        Assert.Contains("m365", t.RoomApp!.LostRights);
        Assert.Equal(RoomModes.None, t.SharedCalendars.First(c => c.Id == p.MsRoom).RoomWriteMode);
        Assert.Equal(RoomModes.Local, t.SharedCalendars.First(c => c.Id == p.IcsRoom).RoomWriteMode);   // les autres sources ne bougent pas
    }

    [Fact]
    public async Task The_player_feed_carries_the_door_and_the_tablet_reports_its_touch_screen()
    {
        var feed = JsonDocument.Parse(await (await C.SendAsync(Req(HttpMethod.Get, "/api/player/playlist", RoomPlatform.Door1))).Content.ReadAsStringAsync()).RootElement;
        var room = feed.GetProperty("room");
        Assert.Equal("ok", room.GetProperty("state").GetString());
        Assert.Equal("Salle de conférence", room.GetProperty("name").GetString());
        var plain = JsonDocument.Parse(await (await C.SendAsync(Req(HttpMethod.Get, "/api/player/playlist", RoomPlatform.Plain))).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("room").ValueKind);

        await C.SendAsync(Req(HttpMethod.Post, "/api/player/device", RoomPlatform.Door1, new { kind = "android", os = "Android 14", touch = true }));
        Assert.True(Store.Read(db => db.Screens.First(s => s.Token == RoomPlatform.Door1).DeviceTouch));
    }

    [Fact]
    public void A_room_screen_never_has_anything_to_publish_and_its_revision_follows_the_app()
    {
        var (s, t) = Store.Read(db => (db.Screens.First(x => x.Token == RoomPlatform.Door1), db.Clients.First(c => c.Id == p.T.Id)));
        Assert.False(Helpers.HasPending(s, Array.Empty<Playlist>()));
        var before = Helpers.Revision(null, s, t, null);
        t.RoomApp!.Revision++;
        Assert.NotEqual(before, Helpers.Revision(null, s, t, null));
    }

    [Fact]
    public void Organisations_that_already_had_rooms_receive_the_app_in_display_only()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkii-roommig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new JsonStore(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Linkii:DataDir"] = dir }).Build(), new RoomEnv());
            var t = new Tenant { Name = "Ancienne" };
            store.Write(db => { db.Clients.Add(t); t.SharedCalendars.Add(new SharedCalendar { Source = "google", Name = "Salle", Room = true }); });
            RoomSetup.Migrate(store, p.Services.GetRequiredService<AppCatalog>());
            store.Read(db =>
            {
                var c = db.Clients.First(x => x.Id == t.Id);
                Assert.Contains(db.AppInstalls, a => a.ClientId == t.Id && a.AppId == RoomRules.AppId);
                Assert.Equal(RoomPerms.Read, c.RoomApp!.Sources["google"]);
                Assert.Equal(RoomModes.None, c.SharedCalendars.Single().RoomWriteMode);
                return 0;
            });
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
