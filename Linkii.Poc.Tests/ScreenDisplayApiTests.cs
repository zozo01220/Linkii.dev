using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Afficher un message un moment sur un écran depuis l'extérieur (Home Assistant) : POST et DELETE /api/v1/screens/{id}/display (§224).</summary>
[Collection("platform")]
public class ScreenDisplayApiTests(Platform p)
{
    private JsonStore Store => p.Services.GetRequiredService<JsonStore>();

    private (Tenant T, string Key, Screen S) Org(bool online = true, string[]? scopes = null, Guid? owner = null)
    {
        var t = new Tenant { Name = "Org " + Guid.NewGuid().ToString("N")[..6], Plan = Plans.Growth, ResellerId = p.R1.Id };
        var s = new Screen { ClientId = t.Id, Name = "Accueil", Token = Guid.NewGuid().ToString("N"), DeviceKind = "windows", LastSeenUtc = DateTime.UtcNow.AddSeconds(online ? -5 : -3600) };
        Store.Write(db => { db.Clients.Add(t); db.Screens.Add(s); });
        var k = p.Services.GetRequiredService<ApiKeyService>().Create(t.Id, owner, "Home Assistant", "Home Assistant", scopes ?? new[] { "screens:read", "screens:control" }, null, null);
        Assert.Null(k.Error);
        return (t, k.Secret!, s);
    }

    private async Task<(HttpStatusCode Status, JsonElement Json)> Call(HttpMethod method, Guid id, string key, object? body = null)
    {
        var r = new HttpRequestMessage(method, $"/api/v1/screens/{id}/display");
        if (body != null) r.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        r.Headers.Add("Authorization", "Bearer " + key);
        var res = await p.Client().SendAsync(r);
        return (res.StatusCode, JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    private Task<(HttpStatusCode Status, JsonElement Json)> Show(Guid id, string key, object body) => Call(HttpMethod.Post, id, key, body);

    private Screen Reload(Guid id) => Store.Read(db => db.Screens.First(x => x.Id == id));

    [Fact]
    public async Task A_message_is_stored_with_its_look_and_a_thirty_second_default_and_reaches_the_player()
    {
        var (_, key, s) = Org();
        var r = await Show(s.Id, key, new { title = "Un visiteur est à l'accueil", message = "Merci d'aller l'accueillir" });
        Assert.Equal(HttpStatusCode.Accepted, r.Status);
        Assert.Equal("displayed", r.Json.GetProperty("status").GetString());

        var n = Reload(s.Id).Notice!;
        Assert.Equal("Un visiteur est à l'accueil", n.Title);
        Assert.Equal(Notices.Information, n.Look);
        Assert.InRange((n.UntilUtc - n.StartedUtc).TotalSeconds, 29.9, 30.1);
        Assert.Contains("Home Assistant", n.By);

        var version = JsonDocument.Parse(await (await p.Client().SendAsync(Platform.Get("/api/player/version", s.Token!))).Content.ReadAsStringAsync()).RootElement;
        var notice = version.GetProperty("notice");
        Assert.Equal(n.Id, notice.GetProperty("id").GetGuid());
        Assert.Equal("Merci d'aller l'accueillir", notice.GetProperty("message").GetString());
        Assert.True(notice.GetProperty("until").GetInt64() > notice.GetProperty("startedAt").GetInt64());
    }

    [Fact]
    public async Task The_next_message_replaces_the_previous_one_and_an_expired_one_is_not_sent_any_more()
    {
        var (_, key, s) = Org();
        await Show(s.Id, key, new { title = "Premier", durationSeconds = 60, look = "attention" });
        var first = Reload(s.Id).Notice!;
        await Show(s.Id, key, new { title = "Second", durationSeconds = 10 });
        var second = Reload(s.Id).Notice!;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("Second", second.Title);

        Store.Write(db => db.Screens.First(x => x.Id == s.Id).Notice!.UntilUtc = DateTime.UtcNow.AddSeconds(-1));
        var version = await (await p.Client().SendAsync(Platform.Get("/api/player/version", s.Token!))).Content.ReadAsStringAsync();
        Assert.Contains("\"notice\":null", version.Replace(" ", ""));
    }

    [Fact]
    public async Task Invalid_messages_are_rejected_with_the_reason()
    {
        var (_, key, s) = Org();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Show(s.Id, key, new { })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Show(s.Id, key, new { title = new string('x', 61) })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Show(s.Id, key, new { message = new string('x', 301) })).Status);
        var look = await Show(s.Id, key, new { title = "Bonjour", look = "disco" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, look.Status);
        Assert.Contains("information", look.Json.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Show(s.Id, key, new { title = "Bonjour", durationSeconds = 2 })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Show(s.Id, key, new { title = "Bonjour", durationSeconds = 7200 })).Status);
        Assert.Null(Reload(s.Id).Notice);
    }

    [Fact]
    public async Task An_offline_screen_refuses_the_message()
    {
        var (_, key, s) = Org(online: false);
        var r = await Show(s.Id, key, new { title = "Bonjour" });
        Assert.Equal(HttpStatusCode.Conflict, r.Status);
        Assert.Equal("screen_unavailable", r.Json.GetProperty("code").GetString());
        Assert.Contains("hors ligne", r.Json.GetProperty("detail").GetString());
        Assert.Null(Reload(s.Id).Notice);
    }

    [Fact]
    public async Task A_sleeping_screen_refuses_unless_asked_to_wake_and_then_stays_awake_until_the_end()
    {
        var (_, key, s) = Org();
        Store.Write(db => db.Screens.First(x => x.Id == s.Id).ForcedSleep = true);
        var refused = await Show(s.Id, key, new { title = "Bonjour" });
        Assert.Equal(HttpStatusCode.Conflict, refused.Status);
        Assert.Contains("veille", refused.Json.GetProperty("detail").GetString());
        Assert.Null(Reload(s.Id).Notice);

        var ok = await Show(s.Id, key, new { title = "Bonjour", durationSeconds = 45, wake = true });
        Assert.Equal(HttpStatusCode.Accepted, ok.Status);
        var after = Reload(s.Id);
        Assert.False(after.ForcedSleep);
        Assert.Equal(after.Notice!.UntilUtc, after.WakeUntilUtc);
        Assert.True(after.Notice.Wake);
    }

    [Fact]
    public async Task Clear_removes_the_message_even_when_there_is_none()
    {
        var (_, key, s) = Org();
        await Show(s.Id, key, new { title = "Bonjour" });
        var r = await Call(HttpMethod.Delete, s.Id, key);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal("cleared", r.Json.GetProperty("status").GetString());
        Assert.Null(Reload(s.Id).Notice);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Delete, s.Id, key)).Status);
    }

    [Fact]
    public async Task The_plan_the_scope_the_organisation_and_the_member_rights_are_enforced()
    {
        var (t, downgradedKey, f) = Org();
        Store.Write(db => db.Clients.First(c => c.Id == t.Id).Plan = Plans.Base);
        Assert.True((await Show(f.Id, downgradedKey, new { title = "Bonjour" })).Status is HttpStatusCode.Forbidden or HttpStatusCode.Conflict);
        Assert.Null(Reload(f.Id).Notice);

        var (_, key, s) = Org();
        var (_, other, _) = Org();
        Assert.Equal(HttpStatusCode.NotFound, (await Show(s.Id, other, new { title = "Bonjour" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, s.Id, other)).Status);

        var (_, readOnly, r) = Org(scopes: new[] { "screens:read" });
        var denied = await Show(r.Id, readOnly, new { title = "Bonjour" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("scope_missing", denied.Json.GetProperty("code").GetString());

        var (mt, _, ms) = Org();
        var member = new User { Email = "m" + Guid.NewGuid().ToString("N")[..6] + "@exemple.ch", Role = Roles.ClientMember, ClientId = mt.Id, Rights = new MemberRights { ScreenIds = new() } };
        Store.Write(db => db.Users.Add(member));
        var mk = p.Services.GetRequiredService<ApiKeyService>().Create(mt.Id, member.Id, "Clé de Camille", "Home Assistant", new[] { "screens:read", "screens:control" }, null, null);
        Assert.Equal(HttpStatusCode.Conflict, (await Show(ms.Id, mk.Secret!, new { title = "Bonjour" })).Status);   // écran non attribué à ce membre
        Assert.Null(Reload(ms.Id).Notice);
    }
}
