using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Programmations : calendriers (liste par défaut, diffusions, extinctions), compilation en créneaux, priorités, player, migration de l'ancien planning.</summary>
public class CalendarsTests
{
    readonly Db db = new();
    readonly Tenant client = new() { Name = "Les Deux Rives", Timezone = "UTC" };
    readonly Area lac;
    readonly Zone stand;
    readonly Playlist menu, pub, soir;

    public CalendarsTests()
    {
        db.Clients.Add(client);
        lac = new Area { ClientId = client.Id, Name = "Restaurant Lac" };
        AreaOps.EnsureDefaultZone(lac);
        stand = new Zone { Name = "Stand service" };
        lac.Zones.Add(stand);
        db.Areas.Add(lac);
        menu = Pl("Menu + prix"); pub = Pl("Menu + pub"); soir = Pl("Carte du soir");
    }

    Playlist Pl(string name) { var p = new Playlist { ClientId = client.Id, AreaId = lac.Id, Name = name }; db.Playlists.Add(p); return p; }
    Screen Sc(string name) { var s = new Screen { ClientId = client.Id, AreaId = lac.Id, ZoneId = stand.Id, Name = name, Token = Guid.NewGuid().ToString("N") }; db.Screens.Add(s); return s; }
    ScreenCalendar Cal(params CalEvent[] events) { var c = new ScreenCalendar { ClientId = client.Id, Name = "Horaires", DefaultPlaylistId = menu.Id, Events = events.ToList() }; db.Calendars.Add(c); return c; }
    static CalEvent Weekly(Playlist p, int days, string from, string to, DateTime? created = null) =>
        new() { Kind = CalEvent.Play, PlaylistId = p.Id, Repeat = true, Days = days, StartTime = from, EndTime = to, CreatedUtc = created ?? DateTime.UtcNow };
    const int Weekdays = 0b0011111;
    static readonly DateOnly Wed = new(2026, 10, 14), Thu = new(2026, 10, 15);

    [Fact]
    public void Outside_events_the_default_list_plays_and_an_event_takes_over_during_its_hours()
    {
        var s = Sc("Grill");
        Calendars.Stamp(s, Cal(Weekly(soir, Weekdays, "18:00", "22:00")));
        var eff = Planning.Effective(lac, s);
        Assert.Equal(menu.Id, Planning.Pick(eff, Wed, 12 * 60)!.PlaylistId);   // liste par défaut du calendrier
        Assert.Equal(soir.Id, Planning.Pick(eff, Wed, 19 * 60)!.PlaylistId);
        Assert.Equal(menu.Id, Planning.Pick(eff, new DateOnly(2026, 10, 17), 19 * 60)!.PlaylistId);   // samedi : pas de récurrence
    }

    [Fact]
    public void A_dated_event_beats_a_recurring_one_then_the_most_recent_wins()
    {
        var old = Weekly(soir, 127, "11:00", "14:00", DateTime.UtcNow.AddHours(-2));
        var recent = Weekly(pub, 127, "12:00", "13:00", DateTime.UtcNow.AddHours(-1));
        var dated = new CalEvent { Kind = CalEvent.Play, PlaylistId = menu.Id, StartDate = "2026-10-14", StartTime = "11:00", EndDate = "2026-10-14", EndTime = "14:00", CreatedUtc = DateTime.UtcNow.AddHours(-5) };
        var c = Cal(old, recent);
        c.DefaultPlaylistId = soir.Id;
        var slots = Calendars.PlaySlots(c);
        Assert.Equal(pub.Id, Planning.Pick(slots, Thu, 12 * 60 + 30)!.PlaylistId);   // la plus récente
        Assert.Equal(soir.Id, Planning.Pick(slots, Thu, 11 * 60 + 30)!.PlaylistId);
        c.Events.Add(dated);
        slots = Calendars.PlaySlots(c);
        Assert.Equal(menu.Id, Planning.Pick(slots, Wed, 12 * 60 + 30)!.PlaylistId);   // la période datée d'abord
        Assert.Equal(c.Id, slots[^1].Id);   // la liste par défaut en dernier, toute la semaine
    }

    [Fact]
    public void An_extinction_crossing_midnight_is_split_and_bounded_by_its_dates()
    {
        var off = new CalEvent { Kind = CalEvent.Off, Repeat = true, Days = Weekdays, StartTime = "19:00", EndTime = "07:00", StartDate = "2026-10-14" };
        Assert.Null(Calendars.Check(off));
        var slots = Calendars.Compile(off);
        Assert.Equal(2, slots.Count);
        Assert.True(Planning.Matches(slots[0], Wed, 20 * 60));
        Assert.Contains(slots, x => Planning.Matches(x, Thu, 6 * 60));          // le lendemain matin
        Assert.DoesNotContain(slots, x => Planning.Matches(x, Wed, 6 * 60));    // avant la date de début
        Assert.DoesNotContain(slots, x => Planning.Matches(x, new DateOnly(2026, 10, 17), 20 * 60));   // samedi soir
        Assert.Contains(slots, x => Planning.Matches(x, new DateOnly(2026, 10, 17), 6 * 60));          // mais samedi matin (fin du vendredi)
    }

    [Fact]
    public void A_one_off_period_over_several_days_covers_every_minute_between_start_and_end()
    {
        var e = new CalEvent { Kind = CalEvent.Play, PlaylistId = pub.Id, StartDate = "2026-10-14", StartTime = "18:00", EndDate = "2026-10-16", EndTime = "09:00" };
        var slots = Calendars.Compile(e);
        Assert.True(slots.Any(x => Planning.Matches(x, Wed, 18 * 60)));
        Assert.False(slots.Any(x => Planning.Matches(x, Wed, 17 * 60)));
        Assert.True(slots.Any(x => Planning.Matches(x, Thu, 3 * 60)));
        Assert.True(slots.Any(x => Planning.Matches(x, new DateOnly(2026, 10, 16), 8 * 60 + 59)));
        Assert.False(slots.Any(x => Planning.Matches(x, new DateOnly(2026, 10, 16), 9 * 60)));
    }

    [Fact]
    public void Validation_rejects_missing_list_days_or_inverted_periods()
    {
        Assert.NotNull(Calendars.Check(new CalEvent { Kind = CalEvent.Play, Always = true }));   // pas de liste
        Assert.NotNull(Calendars.Check(new CalEvent { Kind = CalEvent.Off, Repeat = true, Days = 0, StartTime = "19:00", EndTime = "07:00" }));
        Assert.NotNull(Calendars.Check(new CalEvent { Kind = CalEvent.Off, StartDate = "2026-10-15", StartTime = "10:00", EndDate = "2026-10-14", EndTime = "12:00" }));
        Assert.NotNull(Calendars.Check(new CalEvent { Kind = CalEvent.Off, Always = true }));   // une extinction a toujours une période
        Assert.Null(Calendars.Check(new CalEvent { Kind = CalEvent.Play, PlaylistId = menu.Id, Always = true }));
    }

    [Fact]
    public void The_player_gets_the_extinctions_and_the_stamp_changes_with_them()
    {
        var s = Sc("Grill");
        Assert.Equal("", Planning.Stamp(db.Areas, s));   // sans calendrier : rien
        var c = Cal();
        Calendars.Stamp(s, c);
        var a = Planning.Stamp(db.Areas, s);
        Assert.NotEqual("", a);
        Assert.Null(Planning.OffForPlayer(client, lac, s));
        c.Events.Add(new CalEvent { Kind = CalEvent.Off, Repeat = true, Days = 127, StartTime = "22:00", EndTime = "06:00" });
        Assert.True(Calendars.Stamp(s, c));
        Assert.NotEqual(a, Planning.Stamp(db.Areas, s));
        var json = System.Text.Json.JsonSerializer.Serialize(Planning.OffForPlayer(client, lac, s));
        Assert.Contains("\"from\":1320", json);
        Assert.True(Planning.IsOff(client, lac, s, new DateTime(2026, 10, 14, 23, 0, 0, DateTimeKind.Utc)));
        Assert.False(Planning.IsOff(client, lac, s, new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Player_feed_only_sends_lists_that_were_snapshotted()
    {
        var s = Sc("Grill");
        Calendars.Stamp(s, Cal(Weekly(soir, 127, "18:00", "22:00")));
        s.PublishedLists[menu.Id] = new() { new PublishedItem { Id = Guid.NewGuid(), Type = "image", Url = "/media/x.jpg", DurationSec = 8 } };
        var json = System.Text.Json.JsonSerializer.Serialize(Planning.ForPlayer(client, lac, s, l => l));
        Assert.Contains("/media/x.jpg", json);
        Assert.Single(System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("slots").EnumerateArray());   // « soir » pas encore figée
        Assert.Null(Planning.ForPlayer(client, lac, Sc("Wok"), l => l));   // sans calendrier
    }

    [Fact]
    public void Removing_a_calendar_from_a_screen_clears_its_slots()
    {
        var s = Sc("Grill");
        Calendars.Stamp(s, Cal(Weekly(soir, 127, "18:00", "22:00")));
        Assert.NotEmpty(s.Slots);
        Assert.True(Calendars.Stamp(s, null));
        Assert.Null(s.CalendarId);
        Assert.Empty(Planning.Effective(lac, s));
    }

    [Fact]
    public void Deleting_a_list_removes_its_events_and_empties_a_default_list()
    {
        var s = Sc("Grill");
        var c = Cal(Weekly(soir, 127, "18:00", "22:00"));
        c.DefaultPlaylistId = soir.Id;
        Calendars.Stamp(s, c);
        PlaylistOps.Remove(new ClientDb(db, client.Id), new[] { soir.Id });
        Assert.Empty(c.Events);
        Assert.Equal(Guid.Empty, c.DefaultPlaylistId);
        Assert.Empty(s.Slots);   // plus de liste par défaut : l'écran retombe sur sa propre liste
    }

    [Fact]
    public void The_old_screen_and_zone_planning_becomes_calendars()
    {
        var own = Sc("Grill"); own.PlaylistId = menu.Id; own.OwnPlanning = true;
        own.Slots.Add(new Slot { PlaylistId = soir.Id, Days = Weekdays, From = 18 * 60, To = 22 * 60 });
        stand.Slots.Add(new Slot { PlaylistId = pub.Id, Days = 127, From = 11 * 60, To = 14 * 60 });
        var a = Sc("Wok"); a.PlaylistId = menu.Id;
        var b = Sc("Bar"); b.PlaylistId = menu.Id;
        Calendars.Migrate(db);
        Assert.Equal(2, db.Calendars.Count);                      // celui de « Grill », et celui de la zone partagé par « Wok » et « Bar »
        Assert.Equal(a.CalendarId, b.CalendarId);
        Assert.Empty(stand.Slots);
        var eff = Planning.Effective(lac, own);
        Assert.Equal(soir.Id, Planning.Pick(eff, Wed, 19 * 60)!.PlaylistId);
        Assert.Equal(pub.Id, Planning.Pick(eff, Wed, 12 * 60)!.PlaylistId);   // ce que la zone lui donnait reste là
        Assert.Equal(menu.Id, Planning.Pick(eff, Wed, 16 * 60)!.PlaylistId);  // sa liste devient la liste par défaut
        Calendars.Migrate(db);
        Assert.Equal(2, db.Calendars.Count);                      // une seule fois
    }
}
