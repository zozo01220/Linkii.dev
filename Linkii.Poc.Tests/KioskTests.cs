using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Borne interactive : modifications à publier, tuiles datées, ce que reçoit le player, statistiques sans donnée personnelle.</summary>
public class KioskTests
{
    [Fact]
    public void A_broadcast_screen_has_no_kiosk_stamp() => Assert.Equal("", Kiosk.Stamp(new Screen()));

    [Fact]
    public void Changing_the_menu_leaves_the_kiosk_with_changes_to_publish()
    {
        var s = new Screen { Kiosk = KioskConfig.Starter(), KioskRevision = 1 };
        var playlists = new List<Playlist>();
        s.PublishedStamp = Helpers.PublishStamp(s, playlists);
        Assert.False(Helpers.HasPending(s, playlists));
        s.KioskRevision++;
        Assert.True(Helpers.HasPending(s, playlists));
    }

    [Fact]
    public void Tiles_are_shown_only_between_their_dates()
    {
        var t = new PublishedKioskTile { ShowFrom = "2026-11-01", ShowTo = "2026-11-30" };
        Assert.False(Kiosk.ShownOn(t, new DateOnly(2026, 10, 31)));
        Assert.True(Kiosk.ShownOn(t, new DateOnly(2026, 11, 1)));
        Assert.True(Kiosk.ShownOn(t, new DateOnly(2026, 11, 30)));
        Assert.False(Kiosk.ShownOn(t, new DateOnly(2026, 12, 1)));
        Assert.True(Kiosk.ShownOn(new PublishedKioskTile(), new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void A_bad_colour_falls_back_to_the_palette_and_delays_are_bounded()
    {
        Assert.Equal("#3B82C4", Kiosk.CleanColor("rouge"));
        Assert.Equal("#C77700", Kiosk.CleanColor("#c77700"));
        Assert.Equal(Kiosk.MinReturn, Kiosk.Clamp(1, Kiosk.MinReturn, Kiosk.MaxReturn));
        Assert.Equal(Kiosk.MaxIdle, Kiosk.Clamp(99999, Kiosk.MinIdle, Kiosk.MaxIdle));
    }

    [Fact]
    public void The_player_receives_the_menu_and_its_settings_but_no_secret()
    {
        var item = new PublishedItem { Id = Guid.NewGuid(), Type = "image", Url = "/media/a.jpg", Secrets = { ["token"] = "secret" } };
        var published = new PublishedKiosk
        {
            Config = new KioskConfig { Title = "Hall", ReturnSec = 45, Accessible = true },
            Tiles = { new PublishedKioskTile { Id = Guid.NewGuid(), Title = "Plan", Color = "#3B82C4", Item = item } }
        };
        var json = JsonSerializer.Serialize(published.ForPlayer());
        Assert.Contains("\"title\":\"Hall\"", json);
        Assert.Contains("\"returnSec\":45", json);
        Assert.Contains("\"accessible\":true", json);
        Assert.Contains("\"url\":\"/media/a.jpg\"", json);
        Assert.DoesNotContain("secret", json);
    }

    [Fact]
    public void Stats_count_visits_tiles_and_how_visits_end()
    {
        var screen = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var events = new[]
        {
            new KioskEvent(now, screen, "touch", null, null, 0, null),
            new KioskEvent(now, screen, "tile", plan, "Plan", 0, null),
            new KioskEvent(now, screen, "tile", plan, "Plan", 0, null),
            new KioskEvent(now, screen, "visit", null, null, 30, "auto"),
            new KioskEvent(now, screen, "visit", null, null, 60, "back"),
            new KioskEvent(now, screen, "qr", plan, "Plan", 0, null)
        };
        var s = KioskLog.Stats(events);
        Assert.Equal(2, s.Visits);
        Assert.Equal(45, s.AvgSeconds);
        Assert.Equal(1, s.Touches);
        Assert.Equal(1, s.Qr);
        Assert.Equal(("Plan", 2), (s.Tiles[0].Title, s.Tiles[0].Count));
        Assert.Equal((1, 1), (s.EndAuto, s.EndBack));
    }

    [Fact]
    public void The_log_keeps_each_screen_apart_and_reads_back_what_was_written()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiosk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Linkii:DataDir"] = dir }).Build();
            var log = new KioskLog(config, new FakeEnv(dir));
            var client = Guid.NewGuid();
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            log.Append(client, new[] { new KioskEvent(DateTime.UtcNow, a, "visit", null, null, 10, "auto"), new KioskEvent(DateTime.UtcNow, b, "visit", null, null, 20, "back") });
            Assert.Single(log.Read(client, a, DateTime.UtcNow.AddDays(-1)));
            Assert.Equal(20, log.Read(client, b, DateTime.UtcNow.AddDays(-1))[0].Seconds);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private sealed class FakeEnv(string root) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "t";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
