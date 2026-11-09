using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Réservation par QR : code signé qui se renouvelle, commentaire, titre de la réservation, texte « Occupée ».</summary>
public class RoomQrTests
{
    private static Screen RoomScreen() => new() { Id = Guid.NewGuid(), Token = "jeton-secret", Room = new RoomScreen() };
    private static readonly DateTime T0 = new(2026, 11, 9, 13, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_code_resolves_to_its_screen_for_two_periods_then_expires()
    {
        var s = RoomScreen();
        var db = new Db { Screens = { s } };
        var code = RoomQr.Code(s, T0);
        Assert.Same(s, RoomQr.Resolve(db, code, T0));
        Assert.Same(s, RoomQr.Resolve(db, code, T0.AddMinutes(6)));    // le code précédent reste valable une période de plus
        Assert.Null(RoomQr.Resolve(db, code, T0.AddMinutes(16)));
        Assert.NotEqual(code, RoomQr.Code(s, T0.AddMinutes(10)));
    }

    [Fact]
    public void A_tampered_or_foreign_code_does_not_resolve()
    {
        var s = RoomScreen();
        var other = RoomScreen();
        var db = new Db { Screens = { s, other } };
        var code = RoomQr.Code(s, T0);
        Assert.Null(RoomQr.Resolve(db, code[..^1] + (code[^1] == 'a' ? 'b' : 'a'), T0));
        Assert.Null(RoomQr.Resolve(db, other.Id.ToString("N") + code[code.IndexOf('-')..], T0));   // signature d'un autre écran
        Assert.Null(RoomQr.Resolve(db, "n-importe-quoi", T0));
        Assert.Null(RoomQr.Resolve(db, null, T0));
    }

    [Fact]
    public void A_screen_without_a_room_or_a_token_has_no_valid_code()
    {
        var s = RoomScreen(); s.Room = null;
        Assert.Null(RoomQr.Resolve(new Db { Screens = { s } }, RoomQr.Code(s, T0), T0));
    }

    [Fact]
    public void The_comment_is_cleaned_and_limited()
    {
        Assert.Null(RoomQr.CleanComment("  \t "));
        Assert.Null(RoomQr.CleanComment(null));
        Assert.Equal("Point équipe", RoomQr.CleanComment("  Point\u0007 équipe \n"));
        Assert.Equal(RoomQr.MaxComment, RoomQr.CleanComment(new string('x', 200))!.Length);
    }

    [Fact]
    public void A_booking_with_a_comment_shows_it_instead_of_the_default_title()
    {
        var b = new RoomBooking { StartUtc = T0, EndUtc = T0.AddMinutes(30), State = RoomBooking.Confirmed, Title = "Point équipe" };
        var plain = new RoomBooking { StartUtc = T0.AddHours(2), EndUtc = T0.AddHours(2).AddMinutes(30), State = RoomBooking.Confirmed };
        var merged = RoomRules.Merge(Array.Empty<EventDto>(), new[] { b, plain }, false, "Réservé sur place");
        Assert.Equal("Point équipe", merged[0].Title);
        Assert.Equal("Réservé sur place", merged[1].Title);
    }

    [Fact]
    public void A_cancelled_booking_no_longer_holds_the_room()
    {
        var b = new RoomBooking { StartUtc = T0, EndUtc = T0.AddMinutes(30), State = RoomBooking.Cancelled };
        Assert.False(b.Holds);
        Assert.Empty(RoomRules.Merge(Array.Empty<EventDto>(), new[] { b }, false, "x"));
    }

    [Fact]
    public void The_busy_state_is_written_in_the_feminine()
    {
        var tz = RoomRules.Zone("Europe/Zurich");
        var now = RoomRules.State(T0, new[] { new RoomInterval(T0.AddMinutes(-10), T0.AddMinutes(20), "", false, false) }, 10, false, tz);
        Assert.StartsWith("Occupée jusqu'à", RoomRules.StateText(now, tz));
    }
}

/// <summary>Arrondi du début et de la fin d'une réservation faite maintenant (réglage « Début de la réservation »).</summary>
public class RoomRoundingTests
{
    private static readonly TimeZoneInfo Tz = RoomRules.Zone("Europe/Zurich");
    private static DateTime At(int h, int m) => RoomRules.Utc(new DateTime(2026, 11, 9, h, m, 0), Tz);   // lundi
    private static RoomOption Opt(string rounding, int minutes, int h = 14, int m = 5, params RoomInterval[] evs) =>
        RoomRules.Options(At(h, m), evs, new RoomAppSettings { Rounding = rounding }, false, Tz).First(o => o.Minutes == minutes);

    [Theory]
    [InlineData("real", 14, 5, 14, 35)]
    [InlineData("down", 14, 0, 14, 30)]
    [InlineData("up", 14, 15, 14, 45)]
    [InlineData("both", 14, 0, 14, 45)]
    public void The_start_and_end_follow_the_chosen_rounding(string r, int h1, int m1, int h2, int m2)
    {
        var o = Opt(r, 30);
        Assert.Equal(At(h1, m1), o.Start);
        Assert.Equal(At(h2, m2), o.End);
        Assert.True(o.Ok);
    }

    [Fact]
    public void An_exact_quarter_hour_is_not_moved()
    {
        foreach (var r in new[] { "down", "up" }) Assert.Equal(At(14, 15), Opt(r, 15, 14, 15).Start);
    }

    [Fact]
    public void Rounding_down_never_starts_before_a_meeting_that_just_ended()
    {
        var o = Opt("down", 30, 14, 5, new RoomInterval(At(13, 0), At(14, 3), "Avant", false, false));
        Assert.Equal(At(14, 3), o.Start);
    }

    [Fact]
    public void A_rounded_booking_that_overlaps_the_next_meeting_is_refused()
    {
        var next = new RoomInterval(At(14, 40), At(15, 30), "Suivant", false, false);
        Assert.True(Opt("real", 30, 14, 5, next).Ok);        // 14:05-14:35
        Assert.False(Opt("both", 30, 14, 5, next).Ok);       // 14:00-14:45 déborde sur 14:40
        Assert.Equal("Conflit à 14:40", Opt("both", 30, 14, 5, next).Why);
    }

    [Fact]
    public void An_unknown_rounding_falls_back_to_the_real_time()
    {
        Assert.Equal("real", RoomRules.Clean(new RoomAppSettings { Rounding = "n-importe-quoi" }).Rounding);
    }
}
