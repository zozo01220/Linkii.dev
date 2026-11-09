using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Commander les écrans depuis l'extérieur (Home Assistant) : POST /api/v1/screens/{id}/commands (§221).</summary>
[Collection("platform")]
public class ScreenCommandsApiTests(Platform p)
{
    private JsonStore Store => p.Services.GetRequiredService<JsonStore>();

    private (Tenant T, string Key, Screen S) Org(string device = "windows", bool online = true, string[]? scopes = null, string plan = Plans.Growth, Guid? owner = null)
    {
        var t = new Tenant { Name = "Org " + Guid.NewGuid().ToString("N")[..6], Plan = plan, ResellerId = p.R1.Id };
        var s = new Screen { ClientId = t.Id, Name = "Accueil", Token = Guid.NewGuid().ToString("N"), DeviceKind = device, LastSeenUtc = DateTime.UtcNow.AddSeconds(online ? -5 : -3600) };
        Store.Write(db => { db.Clients.Add(t); db.Screens.Add(s); });
        var k = p.Services.GetRequiredService<ApiKeyService>().Create(t.Id, owner, "Home Assistant", "Home Assistant", scopes ?? new[] { "screens:read", "screens:control" }, null, null);
        Assert.Null(k.Error);
        return (t, k.Secret!, s);
    }

    private async Task<(HttpStatusCode Status, JsonElement Json)> Post(Guid id, string key, object body)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/screens/{id}/commands") { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        r.Headers.Add("Authorization", "Bearer " + key);
        var res = await p.Client().SendAsync(r);
        return (res.StatusCode, JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    private Screen Reload(Guid id) => Store.Read(db => db.Screens.First(x => x.Id == id));

    [Fact]
    public async Task Restart_player_and_reboot_send_the_command_to_the_screen()
    {
        var (_, key, s) = Org();
        var a = await Post(s.Id, key, new { command = "restart-player" });
        Assert.Equal(HttpStatusCode.Accepted, a.Status);
        Assert.Equal(s.Id, a.Json.GetProperty("accepted")[0].GetGuid());
        Assert.Contains(Reload(s.Id).Commands, c => c.Kind == ScreenCommand.Restart && c.By.Contains("Home Assistant"));

        var b = await Post(s.Id, key, new { command = "reboot" });
        Assert.Equal(HttpStatusCode.Accepted, b.Status);
        Assert.Contains(Reload(s.Id).Commands, c => c.Kind == ScreenCommand.Reboot);
    }

    [Fact]
    public async Task Poweroff_is_remembered_so_the_silence_is_not_a_failure()
    {
        var (_, key, s) = Org("raspberrypi");
        Assert.Equal(HttpStatusCode.Accepted, (await Post(s.Id, key, new { command = "poweroff" })).Status);
        var after = Reload(s.Id);
        Assert.Contains(after.Commands, c => c.Kind == ScreenCommand.PowerOff);
        Assert.True(ScreenControl.PoweredOff(after));
    }

    [Fact]
    public async Task Screen_off_puts_the_screen_to_sleep_and_wake_wakes_it_for_an_hour()
    {
        var (_, key, s) = Org("android");
        Assert.Equal(HttpStatusCode.Accepted, (await Post(s.Id, key, new { command = "screen-off" })).Status);
        var asleep = Reload(s.Id);
        Assert.True(asleep.ForcedSleep);
        Assert.Null(asleep.WakeUntilUtc);

        Assert.Equal(HttpStatusCode.Accepted, (await Post(s.Id, key, new { command = "wake" })).Status);
        var awake = Reload(s.Id);
        Assert.False(awake.ForcedSleep);
        Assert.InRange(awake.WakeUntilUtc!.Value, DateTime.UtcNow.AddMinutes(Sleep.WakeMinutes - 2), DateTime.UtcNow.AddMinutes(Sleep.WakeMinutes + 1));
    }

    [Fact]
    public async Task Screen_off_works_even_when_the_screen_is_offline_but_restarts_need_it_online()
    {
        var (_, key, s) = Org("windows", online: false);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(s.Id, key, new { command = "screen-off" })).Status);   // la veille se lit au prochain contact
        var r = await Post(s.Id, key, new { command = "restart-player" });
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Equal("screen_unavailable", r.Json.GetProperty("code").GetString());
        Assert.Contains("hors ligne", r.Json.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Android_cannot_be_powered_off_and_reboots_only_as_device_owner()
    {
        var (_, key, s) = Org("android");
        var off = await Post(s.Id, key, new { command = "poweroff" });
        Assert.Equal(HttpStatusCode.Conflict, off.Status);
        Assert.Contains("Android", off.Json.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Post(s.Id, key, new { command = "reboot" })).Status);
        Store.Write(db => db.Screens.First(x => x.Id == s.Id).DeviceOwner = true);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(s.Id, key, new { command = "reboot" })).Status);
    }

    [Fact]
    public async Task A_command_already_running_is_refused_and_the_plan_must_be_pro()
    {
        var (_, key, s) = Org();
        Assert.Equal(HttpStatusCode.Accepted, (await Post(s.Id, key, new { command = "restart-player" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(s.Id, key, new { command = "restart-player" })).Status);   // un redémarrage est déjà en cours

        var (t, downgraded, f) = Org();   // une clé créée en Pro, puis l'organisation repasse en Essential : plus de commande
        Store.Write(db => db.Clients.First(c => c.Id == t.Id).Plan = Plans.Base);
        var r = await Post(f.Id, downgraded, new { command = "restart-player" });
        Assert.True(r.Status is HttpStatusCode.Forbidden or HttpStatusCode.Conflict);   // l'API et les commandes sont réservées au plan Pro
        Assert.Empty(Reload(f.Id).Commands);
    }

    [Fact]
    public async Task Unknown_command_other_organisation_and_missing_scope_are_rejected()
    {
        var (_, key, s) = Org();
        var bad = await Post(s.Id, key, new { command = "format-disk" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
        Assert.Equal("invalid_request", bad.Json.GetProperty("code").GetString());

        var (_, other, _) = Org();
        Assert.Equal(HttpStatusCode.NotFound, (await Post(s.Id, other, new { command = "wake" })).Status);

        var (_, readOnly, r) = Org(scopes: new[] { "screens:read" });
        var denied = await Post(r.Id, readOnly, new { command = "wake" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("scope_missing", denied.Json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_member_key_follows_the_member_rights()
    {
        var (t, _, s) = Org();
        var member = new User { Email = "m" + Guid.NewGuid().ToString("N")[..6] + "@exemple.ch", Role = Roles.ClientMember, ClientId = t.Id, Rights = new MemberRights { ScreenIds = new() { s.Id } } };
        Store.Write(db => db.Users.Add(member));
        var k = p.Services.GetRequiredService<ApiKeyService>().Create(t.Id, member.Id, "Clé de Camille", "Home Assistant", new[] { "screens:read", "screens:control" }, null, null);

        var denied = await Post(s.Id, k.Secret!, new { command = "screen-off" });
        Assert.Equal(HttpStatusCode.Conflict, denied.Status);
        Assert.False(Reload(s.Id).ForcedSleep);

        Store.Write(db => db.Users.First(u => u.Id == member.Id).Rights!.ScreenOff = true);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(s.Id, k.Secret!, new { command = "screen-off" })).Status);
        Assert.True(Reload(s.Id).ForcedSleep);
    }

    [Fact]
    public async Task The_screen_lists_its_supported_commands_and_the_template_exists()
    {
        var (_, key, s) = Org("android");
        var r = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/screens/{s.Id}");
        r.Headers.Add("Authorization", "Bearer " + key);
        var res = await p.Client().SendAsync(r);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        var commands = json.GetProperty("commands").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(new[] { "restart-player", "screen-off", "wake" }, commands);
        Assert.Equal("android", json.GetProperty("device").GetString());
        var registry = p.Services.GetRequiredService<ApiRegistry>();
        Assert.Contains("screens:control", registry.Templates.First(t => t.Name == "Home Assistant").Scopes);
    }
}
