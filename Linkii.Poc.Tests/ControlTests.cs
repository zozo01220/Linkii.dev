using System.Net;
using System.Net.Http.Json;
using Linkii.Poc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Miroir (écran et mur), commandes du direct (Growth), alertes d'écran hors ligne.</summary>
public class ControlTests
{
    static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    static (Db Db, Tenant Client, Area Area) World(string plan = Plans.Growth)
    {
        var db = new Db();
        var c = new Tenant { Name = "Horizon SA", Plan = plan };
        var a = new Area { ClientId = c.Id, Name = "Campus" };
        AreaOps.EnsureDefaultZone(a);
        db.Clients.Add(c); db.Areas.Add(a);
        return (db, c, a);
    }

    static Screen Scr(Db db, Tenant c, Area a, string name, Guid? zone = null, int? pos = null)
    {
        var s = new Screen { ClientId = c.Id, AreaId = a.Id, ZoneId = zone ?? a.Zones[0].Id, WallPos = pos, Name = name, Token = Guid.NewGuid().ToString("N") };
        db.Screens.Add(s);
        return s;
    }

    // ---------- Miroir ----------

    [Fact]
    public void A_screen_mirror_is_its_own_and_changes_the_revision()
    {
        var (db, c, a) = World();
        var s = Scr(db, c, a, "Hall");
        var before = Notifier.Revision(db, s);
        Assert.Equal((false, false), Helpers.Mirror(db.Areas, s));
        s.MirrorH = true;
        Assert.Equal((true, false), Helpers.Mirror(db.Areas, s));
        Assert.NotEqual(before, Notifier.Revision(db, s));   // l'écran recharge sa configuration
    }

    [Fact]
    public void Both_mirrors_together_are_a_180_degree_turn_and_both_reach_the_player()
    {
        var (db, c, a) = World();
        var s = Scr(db, c, a, "Vitrine");
        s.MirrorH = s.MirrorV = true;
        var feed = PlayerFeed.ForScreen(db, s);
        var screen = feed["screen"]!;
        Assert.Equal(true, screen.GetType().GetProperty("mirrorH")!.GetValue(screen));
        Assert.Equal(true, screen.GetType().GetProperty("mirrorV")!.GetValue(screen));
    }

    [Fact]
    public void A_wall_mirror_swaps_the_columns_and_flips_each_screen_but_not_a_flipped_one_twice()
    {
        var (db, c, a) = World();
        var wall = new Zone { Name = "Mur", Kind = Zone.Wall, WallCols = 3, WallRows = 1 };
        a.Zones.Add(wall);
        var left = Scr(db, c, a, "G", wall.Id, 0);
        var right = Scr(db, c, a, "D", wall.Id, 2);
        Assert.Equal(0, Helpers.Wall(db.Areas, left)!.Col);
        wall.MirrorH = true;
        Assert.Equal(2, Helpers.Wall(db.Areas, left)!.Col);    // la colonne de gauche passe à droite
        Assert.Equal(0, Helpers.Wall(db.Areas, right)!.Col);
        Assert.Equal((true, false), Helpers.Mirror(db.Areas, left));
        right.MirrorH = true;                                    // écran monté à l'envers dans un mur déjà inversé : les deux s'annulent
        Assert.Equal((false, false), Helpers.Mirror(db.Areas, right));
    }

    [Fact]
    public void A_wall_mirror_does_not_touch_a_screen_outside_the_wall()
    {
        var (db, c, a) = World();
        var wall = new Zone { Name = "Mur", Kind = Zone.Wall, WallCols = 2, WallRows = 1, MirrorH = true, MirrorV = true };
        a.Zones.Add(wall);
        var free = Scr(db, c, a, "Libre");
        Assert.Equal((false, false), Helpers.Mirror(db.Areas, free));
    }

    // ---------- Commandes ----------

    [Fact]
    public void A_command_goes_sent_received_done_and_never_back()
    {
        var s = new Screen();
        var cmd = new ScreenCommand { Kind = ScreenCommand.Restart };
        s.Commands.Add(cmd);
        Assert.True(ScreenControl.Ack(s, cmd.Id, ScreenCommand.Received, null));
        Assert.Equal(ScreenCommand.Received, cmd.Status);
        Assert.True(ScreenControl.Ack(s, cmd.Id, ScreenCommand.Done, null));
        Assert.Equal(ScreenCommand.Done, cmd.Status);
        Assert.True(ScreenControl.Ack(s, cmd.Id, ScreenCommand.Received, null));   // accusé tardif : sans effet
        Assert.Equal(ScreenCommand.Done, cmd.Status);
        Assert.False(ScreenControl.Ack(s, Guid.NewGuid(), ScreenCommand.Done, null));
        Assert.False(ScreenControl.Ack(s, cmd.Id, "n'importe quoi", null));
    }

    [Fact]
    public void A_failed_pause_leaves_the_screen_not_paused()
    {
        var s = new Screen { PausedUtc = Now, PausedUntilUtc = Now.AddHours(1) };
        var cmd = new ScreenCommand { Kind = ScreenCommand.Pause };
        s.Commands.Add(cmd);
        ScreenControl.Ack(s, cmd.Id, ScreenCommand.Failed, "refusée");
        Assert.False(s.IsPaused(Now));
        Assert.Equal("refusée", cmd.Detail);
    }

    [Fact]
    public void A_command_without_answer_fails_after_two_minutes_and_a_finished_pause_is_cleared()
    {
        var s = new Screen { PausedUtc = Now.AddHours(-2), PausedUntilUtc = Now.AddMinutes(-1) };
        var cmd = new ScreenCommand { Kind = ScreenCommand.Capture, SentUtc = Now.AddMinutes(-3) };
        var fresh = new ScreenCommand { Kind = ScreenCommand.Capture, SentUtc = Now.AddSeconds(-20) };
        s.Commands.Add(cmd); s.Commands.Add(fresh);
        ScreenControl.Expire(s, Now);
        Assert.Equal(ScreenCommand.Failed, cmd.Status);
        Assert.Equal(ScreenCommand.Sent, fresh.Status);
        Assert.Null(s.PausedUtc);
    }

    [Fact]
    public void A_manual_pause_lasts_until_resumed_a_timed_one_ends_by_itself()
    {
        var manual = new Screen { PausedUtc = Now };
        var timed = new Screen { PausedUtc = Now, PausedUntilUtc = Now.AddMinutes(30) };
        Assert.True(manual.IsPaused(Now.AddDays(3)));
        Assert.True(timed.IsPaused(Now.AddMinutes(29)));
        Assert.False(timed.IsPaused(Now.AddMinutes(31)));
    }

    [Fact]
    public void A_requested_restart_is_done_when_the_screen_reconnects()
    {
        var s = new Screen();
        var cmd = new ScreenCommand { Kind = ScreenCommand.Restart, Status = ScreenCommand.Received, SentUtc = Now.AddSeconds(-30) };
        var notYet = new ScreenCommand { Kind = ScreenCommand.Restart, Status = ScreenCommand.Sent, SentUtc = Now.AddSeconds(-5) };   // jamais reçue : la reconnexion ne prouve rien
        s.Commands.Add(cmd); s.Commands.Add(notYet);
        ScreenControl.CompleteRestarts(s, Now);
        Assert.Equal(ScreenCommand.Done, cmd.Status);
        Assert.Equal(ScreenCommand.Sent, notYet.Status);
    }

    [Fact]
    public void Only_recent_unanswered_commands_are_handed_over_by_polling()
    {
        var s = new Screen();
        s.Commands.Add(new ScreenCommand { Kind = ScreenCommand.Reload, SentUtc = Now.AddSeconds(-10) });
        s.Commands.Add(new ScreenCommand { Kind = ScreenCommand.Reload, SentUtc = Now.AddMinutes(-10) });
        s.Commands.Add(new ScreenCommand { Kind = ScreenCommand.Reload, SentUtc = Now.AddSeconds(-10), Status = ScreenCommand.Received });
        Assert.Single(ScreenControl.Pending(s, Now));
    }

    [Fact]
    public void A_capture_must_be_a_jpeg_and_is_stored_with_the_mirror_of_that_moment()
    {
        var (db, c, a) = World();
        var s = Scr(db, c, a, "Hall");
        s.MirrorV = true;
        var cmd = new ScreenCommand { Kind = ScreenCommand.Capture };
        s.Commands.Add(cmd);
        var dir = Path.Combine(Path.GetTempPath(), "linkii-cap-" + Guid.NewGuid().ToString("N"));
        AppPaths.CaptureDir = dir;
        try
        {
            Assert.False(ScreenControl.SaveCapture(db, s, new byte[] { 1, 2, 3, 4, 5 }, cmd.Id, Now));
            Assert.Null(s.CaptureUtc);
            Assert.True(ScreenControl.SaveCapture(db, s, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1 }, cmd.Id, Now));
            Assert.Equal(Now, s.CaptureUtc);
            Assert.True(s.CaptureMirrorV);
            Assert.Equal(ScreenCommand.Done, cmd.Status);
            Assert.True(File.Exists(ScreenControl.CapturePath(s.Id)));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void A_capture_is_turned_back_into_the_configured_orientation()
    {
        // JPEG minimal : SOI, SOF0 (hauteur 540, largeur 960)
        byte[] Jpeg(int w, int h) => new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0, 17, 8, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 };
        Assert.Equal((960, 540), ScreenControl.JpegSize(Jpeg(960, 540)));
        Assert.Equal((0, 0), ScreenControl.JpegSize(new byte[] { 1, 2, 3 }));

        var (db, c, a) = World();
        var phone = Scr(db, c, a, "Téléphone");
        phone.Orientation = "portrait";
        (phone.CaptureW, phone.CaptureH) = (960, 540);           // téléphone tenu à plat : la scène est pivotée dans l'image
        Assert.Equal(-90, ScreenControl.CaptureTurn(phone));
        (phone.CaptureW, phone.CaptureH) = (540, 960);           // image déjà en portrait
        Assert.Equal(0, ScreenControl.CaptureTurn(phone));
        phone.Orientation = "landscape";
        Assert.Equal(90, ScreenControl.CaptureTurn(phone));      // écran paysage tenu debout
        (phone.CaptureW, phone.CaptureH) = (0, 0);
        Assert.Equal(0, ScreenControl.CaptureTurn(phone));       // aucune capture
    }

    // ---------- Alertes « écran hors ligne » ----------

    static Screen Seen(Db db, Tenant c, Area a, string name, DateTime lastSeen)
    {
        var s = Scr(db, c, a, name);
        s.LastSeenUtc = lastSeen;
        return s;
    }

    [Fact]
    public void An_offline_screen_is_reported_once_after_the_chosen_delay()
    {
        var (db, c, a) = World();
        c.AlertOffline = true; c.AlertOfflineMinutes = 10;
        var started = Now.AddHours(-1);
        var down = Seen(db, c, a, "Cafétéria", Now.AddMinutes(-12));
        Seen(db, c, a, "Hall", Now.AddSeconds(-20));
        Seen(db, c, a, "Bar", Now.AddMinutes(-5));   // pas encore 10 minutes

        var batch = Assert.Single(ScreenAlerts.Plan(db, Now, started));
        Assert.Equal(new[] { down }, batch.Down);
        Assert.Empty(batch.Back);
        Assert.NotNull(down.OfflineAlertUtc);
        Assert.Empty(ScreenAlerts.Plan(db, Now.AddMinutes(1), started));   // une seule alerte par panne
    }

    [Fact]
    public void A_screen_that_comes_back_is_reported_once_and_can_fall_again()
    {
        var (db, c, a) = World();
        c.AlertOffline = true; c.AlertOfflineMinutes = 5;
        var started = Now.AddHours(-1);
        var s = Seen(db, c, a, "Cafétéria", Now.AddMinutes(-8));
        ScreenAlerts.Plan(db, Now, started);
        s.LastSeenUtc = Now.AddSeconds(30);
        var batch = Assert.Single(ScreenAlerts.Plan(db, Now.AddSeconds(40), started));
        Assert.Equal(new[] { s }, batch.Back);
        Assert.Null(s.OfflineAlertUtc);
        Assert.Single(ScreenAlerts.Plan(db, Now.AddMinutes(10), started));   // tombe de nouveau : nouvelle alerte
    }

    [Fact]
    public void Nothing_is_reported_right_after_the_server_starts_nor_without_growth_nor_when_disabled()
    {
        var (db, c, a) = World();
        c.AlertOffline = true; c.AlertOfflineMinutes = 10;
        Seen(db, c, a, "Cafétéria", Now.AddMinutes(-30));
        Assert.Empty(ScreenAlerts.Plan(db, Now, Now.AddMinutes(-3)));   // les écrans n'ont pas eu le temps de se reconnecter

        c.AlertOffline = false;
        Assert.Empty(ScreenAlerts.Plan(db, Now, Now.AddHours(-1)));

        c.AlertOffline = true; c.Plan = Plans.Base;
        Assert.Empty(ScreenAlerts.Plan(db, Now, Now.AddHours(-1)));
    }

    [Fact]
    public void A_screen_never_seen_is_not_reported()
    {
        var (db, c, a) = World();
        c.AlertOffline = true;
        Scr(db, c, a, "Neuf");
        Assert.Empty(ScreenAlerts.Plan(db, Now, Now.AddHours(-1)));
    }

    [Fact]
    public void Alerts_go_to_the_chosen_address_or_to_the_administrators()
    {
        var (db, c, _) = World();
        db.Users.Add(new User { Email = "claire@horizon.ch", Name = "Claire", Role = Roles.ClientAdmin, ClientId = c.Id, EmailVerifiedUtc = Now });
        db.Users.Add(new User { Email = "off@horizon.ch", Role = Roles.ClientAdmin, ClientId = c.Id, Disabled = true });
        db.Users.Add(new User { Email = "membre@horizon.ch", Role = Roles.ClientMember, ClientId = c.Id });
        Assert.Equal(new[] { "claire@horizon.ch" }, ScreenAlerts.Recipients(db, c).Select(r => r.Email));
        c.AlertEmail = "alertes@horizon.ch";
        Assert.Equal(new[] { "alertes@horizon.ch" }, ScreenAlerts.Recipients(db, c).Select(r => r.Email));
        c.AlertEmail = "pas une adresse";
        Assert.Equal(new[] { "claire@horizon.ch" }, ScreenAlerts.Recipients(db, c).Select(r => r.Email));
    }
}

/// <summary>Le contrôle du direct de bout en bout : licence, API du player, aperçu protégé.</summary>
[Collection("platform")]
public class ControlApiTests(Platform p)
{
    static HttpRequestMessage Post(string url, string token, object body)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        r.Headers.Add("X-Token", token);
        return r;
    }

    JsonStore Store => p.Services.GetRequiredService<JsonStore>();
    ScreenControl Control => p.Services.GetRequiredService<ScreenControl>();
    Guid ScreenOf(string token) => Store.Read(db => db.Screens.First(s => s.Token == token).Id);

    [Fact]
    public async Task Commands_need_the_growth_licence_and_an_online_screen()
    {
        var id = ScreenOf(p.TokenA);
        Store.Write(db => { db.Clients.First(c => c.Id == p.A.Id).Plan = Plans.Base; db.Screens.First(s => s.Id == id).LastSeenUtc = DateTime.UtcNow; });
        var refused = await Control.Send(p.A.Id, id, ScreenCommand.Restart, "test");
        Assert.False(refused.Ok);
        Assert.Contains("Growth", refused.Error);

        Store.Write(db => { db.Clients.First(c => c.Id == p.A.Id).Plan = Plans.Growth; db.Screens.First(s => s.Id == id).LastSeenUtc = DateTime.UtcNow.AddMinutes(-10); });
        Assert.Contains("hors ligne", (await Control.Send(p.A.Id, id, ScreenCommand.Restart, "test")).Error);

        Store.Write(db => db.Screens.First(s => s.Id == id).LastSeenUtc = DateTime.UtcNow);
        Assert.False((await Control.Send(p.B.Id, id, ScreenCommand.Restart, "test")).Ok);   // jamais l'écran d'une autre organisation
        var ok = await Control.Send(p.A.Id, id, ScreenCommand.Restart, "test");
        Assert.True(ok.Ok);
        Assert.False((await Control.Send(p.A.Id, id, ScreenCommand.Restart, "test")).Ok);    // déjà en cours

        // le sondage du player la lui remet, il accuse réception puis exécution
        var c = p.Client();
        var version = await (await c.SendAsync(Platform.Get("/api/player/version", p.TokenA))).Content.ReadAsStringAsync();
        Assert.Contains(ok.Id.ToString()!, version);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Post("/api/player/command-ack", p.TokenA, new { id = ok.Id, status = "received" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Post("/api/player/command-ack", p.TokenA, new { id = ok.Id, status = "done" }))).StatusCode);
        Assert.Equal(ScreenCommand.Done, Store.Read(db => db.Screens.First(s => s.Id == id).Commands.First(x => x.Id == ok.Id).Status));
        Assert.DoesNotContain(ok.Id.ToString()!, await (await c.SendAsync(Platform.Get("/api/player/version", p.TokenA))).Content.ReadAsStringAsync());
        Store.Write(db => db.Clients.First(x => x.Id == p.A.Id).Plan = Plans.Base);
    }

    [Fact]
    public async Task A_pause_is_remembered_with_its_resume_time_and_cleared_by_resume()
    {
        var id = ScreenOf(p.TokenA);
        Store.Write(db => { db.Clients.First(c => c.Id == p.A.Id).Plan = Plans.Growth; var s = db.Screens.First(x => x.Id == id); s.LastSeenUtc = DateTime.UtcNow; s.Commands.Clear(); s.PausedUtc = null; });
        var pause = await Control.Send(p.A.Id, id, ScreenCommand.Pause, "test", 30);
        Assert.True(pause.Ok);
        var s = Store.Read(db => db.Screens.First(x => x.Id == id));
        Assert.True(s.IsPaused(DateTime.UtcNow));
        Assert.InRange((s.PausedUntilUtc!.Value - DateTime.UtcNow).TotalMinutes, 29, 30.1);
        var cmd = s.Commands.Single(x => x.Id == pause.Id);
        Assert.NotEqual("0", cmd.Args["until"]);

        Store.Write(db => db.Screens.First(x => x.Id == id).Commands.Clear());
        Assert.True((await Control.Send(p.A.Id, id, ScreenCommand.Resume, "test")).Ok);
        Assert.False(Store.Read(db => db.Screens.First(x => x.Id == id).IsPaused(DateTime.UtcNow)));
        Store.Write(db => db.Clients.First(x => x.Id == p.A.Id).Plan = Plans.Base);
    }

    [Fact]
    public async Task The_player_reports_its_device_and_the_capture_is_kept_and_protected()
    {
        var id = ScreenOf(p.TokenA);
        var c = p.Client();
        var r = await c.SendAsync(Post("/api/player/device", p.TokenA, new { kind = "androidtv", os = "Android 14", model = "Leap S1", app = "1.2.0", bootMs = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds() }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var s = Store.Read(db => db.Screens.First(x => x.Id == id));
        Assert.Equal(("androidtv", "Android 14", "Leap S1", "1.2.0"), (s.DeviceKind, s.DeviceOs, s.DeviceModel, s.DeviceApp));
        Assert.InRange((DateTime.UtcNow - s.DeviceBootUtc!.Value).TotalDays, 1.9, 2.1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Post("/api/player/device", "faux-jeton", new { kind = "web" }))).StatusCode);

        // aperçu : refus d'un fichier qui n'est pas un JPEG, puis acceptation
        using var bad = new HttpRequestMessage(HttpMethod.Post, "/api/player/capture") { Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }) };
        bad.Headers.Add("X-Token", p.TokenA);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(bad)).StatusCode);
        using var good = new HttpRequestMessage(HttpMethod.Post, "/api/player/capture") { Content = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 9, 9 }) };
        good.Headers.Add("X-Token", p.TokenA);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(good)).StatusCode);
        Assert.NotNull(Store.Read(db => db.Screens.First(x => x.Id == id).CaptureUtc));

        // le back-office ne la sert qu'à une personne connectée
        var anonymous = await c.GetAsync($"/screens/{id}/capture.jpg");
        Assert.NotEqual(HttpStatusCode.OK, anonymous.StatusCode);
    }

    [Fact]
    public async Task Mirror_reaches_the_player_through_its_playlist()
    {
        var id = ScreenOf(p.TokenA);
        Store.Write(db => { var s = db.Screens.First(x => x.Id == id); s.MirrorH = true; s.MirrorV = false; });
        var body = await (await p.Client().SendAsync(Platform.Get("/api/player/playlist", p.TokenA))).Content.ReadAsStringAsync();
        Assert.Contains("\"mirrorH\":true", body);
        Assert.Contains("\"mirrorV\":false", body);
        Store.Write(db => db.Screens.First(x => x.Id == id).MirrorH = false);
    }
}
