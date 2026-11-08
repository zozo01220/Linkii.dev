using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Application « Réservation de salle » : état d'une salle, durées proposées sur la tablette, fusion des réservations, mode d'écriture.</summary>
public class RoomsTests
{
    private static readonly TimeZoneInfo Tz = RoomRules.Zone("Europe/Zurich");
    // lundi 9 novembre 2026, heure de Zurich (UTC+1)
    private static DateTime At(int h, int m = 0, int day = 9) => RoomRules.Utc(new DateTime(2026, 11, day, h, m, 0), Tz);
    private static RoomInterval Ev(int h1, int m1, int h2, int m2, string title = "Réunion") => new(At(h1, m1), At(h2, m2), title, false, false);

    [Fact]
    public void A_room_is_busy_during_a_meeting_and_until_the_end_of_overlapping_ones()
    {
        var s = RoomRules.State(At(14, 10), new[] { Ev(13, 30, 15, 0), Ev(14, 45, 15, 30), Ev(16, 30, 17, 30) }, 10, false, Tz);
        Assert.Equal("busy", s.State);
        Assert.Equal(At(15, 30), s.UntilUtc);
        Assert.Single(s.Next);   // 16:30, le seul qui commence après la fin de la période occupée
    }

    [Fact]
    public void A_room_is_soon_busy_shortly_before_a_meeting_then_free_until_it()
    {
        var evs = new[] { Ev(14, 18, 15, 30) };
        Assert.Equal("soon", RoomRules.State(At(14, 10), evs, 10, false, Tz).State);
        var free = RoomRules.State(At(14, 0), evs, 10, false, Tz);
        Assert.Equal("free", free.State);
        Assert.Equal(At(14, 18), free.UntilUtc);
        Assert.Equal("Libre jusqu'à 14:18", RoomRules.StateText(free, Tz));
    }

    [Fact]
    public void A_free_room_without_meeting_today_is_free_all_day()
    {
        var s = RoomRules.State(At(14, 10), new[] { Ev(9, 0, 10, 0, "Demain") with { StartUtc = At(9, 0, 10), EndUtc = At(10, 0, 10) } }, 10, false, Tz);
        Assert.Equal("free", s.State);
        Assert.Null(s.UntilUtc);
        Assert.Equal("Libre toute la journée", RoomRules.StateText(s, Tz));
    }

    [Fact]
    public void All_day_events_count_only_when_the_room_says_so()
    {
        var day = new RoomInterval(At(0, 0), At(0, 0, 10), "Salon", true, false);
        Assert.Equal("free", RoomRules.State(At(14, 10), new[] { day }, 10, false, Tz).State);
        Assert.Equal("busy", RoomRules.State(At(14, 10), new[] { day }, 10, true, Tz).State);
    }

    [Fact]
    public void Durations_that_do_not_fit_before_the_next_meeting_are_greyed_with_the_reason()
    {
        var o = RoomRules.Options(At(14, 10), new[] { Ev(15, 0, 16, 0) }, new RoomAppSettings(), false, Tz);
        Assert.Equal(new[] { 15, 30, 45, 60, 120 }, o.Select(x => x.Minutes));
        Assert.True(o.Single(x => x.Minutes == 45).Ok);
        var h1 = o.Single(x => x.Minutes == 60);
        Assert.False(h1.Ok);
        Assert.Equal("Conflit à 15:00", h1.Why);
    }

    [Fact]
    public void The_buffer_keeps_time_free_before_the_next_meeting()
    {
        var rules = new RoomAppSettings { BufferMin = 10 };
        var o = RoomRules.Options(At(14, 10), new[] { Ev(15, 0, 16, 0) }, rules, false, Tz);
        Assert.True(o.Single(x => x.Minutes == 30).Ok);    // 14:40 ≤ 14:50
        Assert.False(o.Single(x => x.Minutes == 45).Ok);   // 14:55 > 14:50
    }

    [Fact]
    public void Nothing_is_bookable_outside_opening_hours_or_beyond_closing_time()
    {
        var rules = new RoomAppSettings();
        Assert.All(RoomRules.Options(At(19, 30), Array.Empty<RoomInterval>(), rules, false, Tz), x => Assert.Equal("Hors horaires", x.Why));
        Assert.All(RoomRules.Options(At(10, 0, 14), Array.Empty<RoomInterval>(), rules, false, Tz), x => Assert.False(x.Ok));   // samedi
        var late = RoomRules.Options(At(18, 0), Array.Empty<RoomInterval>(), rules, false, Tz);
        Assert.True(late.Single(x => x.Minutes == 60).Ok);    // 19:00 pile
        Assert.False(late.Single(x => x.Minutes == 120).Ok);  // 20:00 : après la fermeture
    }

    [Fact]
    public void Durations_above_the_maximum_are_not_offered_and_a_busy_room_offers_none()
    {
        var rules = new RoomAppSettings { Durations = new() { 15, 30, 60, 120, 180 }, MaxMin = 60 };
        Assert.Equal(new[] { 15, 30, 60 }, RoomRules.Options(At(9, 0), Array.Empty<RoomInterval>(), rules, false, Tz).Select(x => x.Minutes));
        Assert.Empty(RoomRules.Options(At(14, 10), new[] { Ev(14, 0, 15, 0) }, rules, false, Tz));
        Assert.Equal(3, RoomRules.Options(At(9, 0), Array.Empty<RoomInterval>(), new RoomAppSettings { Durations = new() { 60, 120, 180 }, MaxMin = 0 }, false, Tz).Count(x => x.Ok));
    }

    [Fact]
    public void A_booking_already_read_in_the_calendar_is_not_counted_twice_and_keeps_its_generic_title()
    {
        var b = new RoomBooking { StartUtc = At(14, 10), EndUtc = At(14, 40), State = RoomBooking.Pending };
        var other = new RoomBooking { StartUtc = At(16, 0), EndUtc = At(16, 30), State = RoomBooking.Confirmed };
        var refused = new RoomBooking { StartUtc = At(17, 0), EndUtc = At(17, 30), State = RoomBooking.Refused };
        var events = new[] { new EventDto("Organisateur : IT", At(14, 10).AddSeconds(30), At(14, 40), false, null) };
        var merged = RoomRules.Merge(events, new[] { b, other, refused }, false, "Réservé sur place");
        Assert.Equal(2, merged.Count);
        Assert.All(merged, m => { Assert.True(m.Linkivi); Assert.Equal("Réservé sur place", m.Title); });
    }

    [Fact]
    public void Titles_are_removed_when_the_room_hides_them()
    {
        var merged = RoomRules.Merge(new[] { new EventDto("Comité de direction", At(9, 0), At(10, 0), false, null) }, Array.Empty<RoomBooking>(), false, "x");
        Assert.Equal("", merged.Single().Title);
    }

    [Theory]
    [InlineData("m365", "cal:AAMk", "write", null, RoomModes.Direct)]
    [InlineData("m365", "salle-a101@acme.ch", "write", null, RoomModes.Invite)]
    [InlineData("m365", "salle-a101@acme.ch", "read", null, RoomModes.None)]
    [InlineData("google", "c_188@resource.calendar.google.com", "write", "reader", RoomModes.Invite)]
    [InlineData("google", "abc@group.calendar.google.com", "write", "writer", RoomModes.Direct)]
    [InlineData("google", "abc@group.calendar.google.com", "write", "reader", RoomModes.None)]
    [InlineData("ics", "chiffré", null, null, RoomModes.Local)]
    [InlineData("caldav", "salle/", "write", null, RoomModes.None)]
    [InlineData("ews", "salle@acme.ch", "write", null, RoomModes.None)]
    public void The_write_mode_follows_the_source_and_the_calendar(string source, string calRef, string? perm, string? role, string expected) =>
        Assert.Equal(expected, RoomRules.WriteModeOf(source, calRef, perm, role));

    [Fact]
    public void A_lost_right_turns_the_source_back_to_display_only() =>
        Assert.Equal(RoomModes.None, RoomRules.WriteModeOf("m365", "salle@acme.ch", RoomPerms.Write, null, rightsLost: true));

    [Fact]
    public void Settings_are_cleaned_and_summarised()
    {
        var c = RoomRules.Clean(new RoomAppSettings { Durations = new() { 7, 30, 15, 30 }, MaxMin = 999, FromMin = 19 * 60, ToMin = 8 * 60, BufferMin = 3, BookTitle = "  " });
        Assert.Equal(new[] { 15, 30 }, c.Durations);
        Assert.Equal(120, c.MaxMin);
        Assert.True(c.ToMin > c.FromMin);
        Assert.Equal(0, c.BufferMin);
        Assert.Equal(RoomRules.DefaultTitle, c.BookTitle);
        Assert.Equal("15 min · 30 min · 45 min · 1 h · 2 h — max 2 h — lun-ven 07:00-19:00", RoomRules.Summary(new RoomAppSettings()));
    }

    [Fact]
    public void The_now_sentence_never_shows_a_title()
    {
        var evs = new[] { Ev(13, 30, 15, 0, "Comité de direction") };
        var s = RoomRules.State(At(14, 10), evs, 10, false, Tz);
        Assert.Equal("Occupée de 13:30 à 15:00", RoomRules.NowText(s, evs, At(14, 10), false, Tz));
    }
}
