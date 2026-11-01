using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Linkii.Poc.Tests;

public sealed class FakePush : IEmergencyPush
{
    public static readonly JsonSerializerOptions Opt = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };   // les accents restent lisibles dans les vérifications
    public readonly List<(Guid Screen, string Json)> Sent = new();
    public Task Push(Guid screenId, object? payload) { lock (Sent) Sent.Add((screenId, payload == null ? "null" : JsonSerializer.Serialize(payload, Opt))); return Task.CompletedTask; }
    public string? Last(Guid screen) { lock (Sent) return Sent.LastOrDefault(s => s.Screen == screen).Json; }
}

public sealed class FakeNotices : IEmergencyNotices
{
    public readonly List<string> Log = new();
    public Task Started(Guid clientId, Emergency e, int screens) { lock (Log) Log.Add($"started:{e.Title}:{screens}"); return Task.CompletedTask; }
    public Task Ended(Guid clientId, Emergency e) { lock (Log) Log.Add("ended:" + e.Title); return Task.CompletedTask; }
    public Task Reminder(Guid clientId, Emergency e) { lock (Log) Log.Add("reminder:" + e.Title); return Task.CompletedTask; }
}

/// <summary>Intégration « Alerte d'urgence » (§159) : déclenchement, cibles, priorité, essai, rapport de réception, réveil, rappel de 12 h.</summary>
public class EmergencyTests : IDisposable
{
    private readonly ApiFixture f = new(new IApiModule[] { new ScreensApiModule(), new EmergencyApiModule() });
    private readonly FakePush push = new();
    private readonly FakeNotices notices = new();
    private readonly RecordingEvents events = new();
    private readonly EmergencyService svc;
    public void Dispose() => f.Dispose();

    public EmergencyTests()
    {
        svc = new EmergencyService(f.Store, push, events, notices, f.Time, NullLogger<EmergencyService>.Instance);
        Install();
    }

    private void Install() => f.Store.Write(db => db.AppInstalls.Add(new AppInstall { ClientId = f.Tenant.Id, AppId = Emergencies.AppId }));

    private Screen Add(string name, DateTime? seen = null, Guid? area = null, Guid? zone = null, bool paired = true)
    {
        var s = new Screen { ClientId = f.Tenant.Id, Name = name, Token = paired ? Guid.NewGuid().ToString("N") : null, LastSeenUtc = seen ?? f.Time.Now.UtcDateTime.AddSeconds(-5), AreaId = area ?? Guid.Empty, ZoneId = zone };
        f.Store.Write(db => db.Screens.Add(s));
        return s;
    }

    private EmergencyRequest All(string title = "Évacuation", string message = "Quittez le bâtiment B.", bool test = false) =>
        new(title, message, "all", new(), test, "backoffice", "Camille Rochat", null, null);

    [Fact]
    public void Templates_start_with_four_editable_defaults()
    {
        var list = svc.Templates(f.Tenant.Id);
        Assert.Equal(new[] { "Évacuation", "Confinement", "Alerte météo", "Exercice" }, list.Select(t => t.Name));
        Assert.Equal(list.Select(t => t.Id), svc.Templates(f.Tenant.Id).Select(t => t.Id));   // enregistrés dès la première lecture : les identifiants restent les mêmes

        Assert.Null(svc.SaveTemplate(f.Tenant.Id, list[0].Id, "Évacuation B", "Évacuation B", "Sortez par la gauche."));
        Assert.Equal("Évacuation B", svc.Templates(f.Tenant.Id)[0].Name);

        svc.DeleteTemplate(f.Tenant.Id, list[1].Id);
        Assert.Equal(3, svc.Templates(f.Tenant.Id).Count);
    }

    [Fact]
    public void Templates_are_validated_and_capped()
    {
        Assert.NotNull(svc.SaveTemplate(f.Tenant.Id, null, "", "T", "M"));
        Assert.NotNull(svc.SaveTemplate(f.Tenant.Id, null, "N", new string('x', Emergencies.MaxTitle + 1), "M"));
        Assert.NotNull(svc.SaveTemplate(f.Tenant.Id, null, "N", "T", new string('x', Emergencies.MaxMessage + 1)));
        for (var i = svc.Templates(f.Tenant.Id).Count; i < Emergencies.MaxTemplates; i++) Assert.Null(svc.SaveTemplate(f.Tenant.Id, null, "M" + i, "T", "M"));
        Assert.NotNull(svc.SaveTemplate(f.Tenant.Id, null, "Un de trop", "T", "M"));
        Assert.Equal(Emergencies.MaxTemplates, svc.Templates(f.Tenant.Id).Count);
    }

    [Fact]
    public async Task An_emergency_goes_to_every_paired_screen_and_is_announced()
    {
        var a = Add("Hall"); var b = Add("Cafétéria"); var unpaired = Add("Pas appairé", paired: false);
        var r = svc.Trigger(f.Tenant.Id, All());
        Assert.NotNull(r.Emergency);
        await svc.Idle();
        Assert.Contains("\"title\":\"Évacuation\"", push.Last(a.Id));
        Assert.Contains("\"test\":false", push.Last(b.Id));
        Assert.Null(push.Last(unpaired.Id));   // un écran jamais appairé n'a personne devant
        Assert.Equal(new[] { "emergency.started" }, events.Items.Select(i => i.Type));
        Assert.Contains("started:Évacuation:2", notices.Log);
        Assert.Single(svc.Active(f.Tenant.Id));
    }

    [Fact]
    public void The_player_payload_carries_the_message_and_asks_for_a_wake_up_while_active()
    {
        var s = Add("Hall");
        var e = svc.Trigger(f.Tenant.Id, All()).Emergency!;
        var now = f.Time.Now.UtcDateTime;
        var json = JsonSerializer.Serialize(f.Store.Read(db => Emergencies.PayloadFor(db, s, now)), FakePush.Opt);
        Assert.Contains(e.Id.ToString(), json);
        Assert.Contains("\"message\":\"Quittez le bâtiment B.\"", json);
        Assert.True(f.Store.Read(db => Emergencies.WakeUntilMs(db, s, now)) > Helpers.EpochMs(now));
        svc.Lift(f.Tenant.Id, e.Id, "Camille");
        Assert.Null(f.Store.Read(db => Emergencies.PayloadFor(db, s, now)));
        Assert.Equal(0, f.Store.Read(db => Emergencies.WakeUntilMs(db, s, now)));
    }

    [Fact]
    public void Invalid_requests_and_missing_prerequisites_are_refused_with_a_clear_code()
    {
        Add("Hall");
        Assert.Equal(EmergencyService.Invalid, svc.Trigger(f.Tenant.Id, All(title: " ")).Code);
        Assert.Equal(EmergencyService.Invalid, svc.Trigger(f.Tenant.Id, All(message: "")).Code);
        Assert.Equal(EmergencyService.Invalid, svc.Trigger(f.Tenant.Id, All(title: new string('x', 41))).Code);
        Assert.Equal(EmergencyService.Invalid, svc.Trigger(f.Tenant.Id, All(message: new string('x', 301))).Code);
        Assert.Equal(EmergencyService.Invalid, svc.Trigger(f.Tenant.Id, All() with { TargetKind = "inconnu" }).Code);
        Assert.Equal(EmergencyService.Invalid, svc.Trigger(f.Tenant.Id, All() with { TargetKind = "screens", TargetIds = new() { Guid.NewGuid() } }).Code);
        Assert.Equal(EmergencyService.Invalid, svc.Trigger(f.Tenant.Id, All() with { TargetKind = "areas", TargetIds = new() }).Code);

        f.Store.Write(db => db.AppInstalls.RemoveAll(a => a.AppId == Emergencies.AppId));
        Assert.Equal(EmergencyService.NotInstalled, svc.Trigger(f.Tenant.Id, All()).Code);
        Install();
        f.Store.Write(_ => f.Tenant.Plan = Plans.Base);
        Assert.Equal(EmergencyService.PlanRequired, svc.Trigger(f.Tenant.Id, All()).Code);
        f.Store.Write(_ => { f.Tenant.Plan = Plans.Growth; f.Tenant.Suspended = true; });
        Assert.Equal(EmergencyService.Suspended, svc.Trigger(f.Tenant.Id, All()).Code);
        f.Store.Write(_ => f.Tenant.Suspended = false);
        Assert.Empty(svc.Active(f.Tenant.Id));
    }

    [Fact]
    public void No_paired_screen_means_no_emergency()
    {
        Add("Pas appairé", paired: false);
        var r = svc.Trigger(f.Tenant.Id, All());
        Assert.Null(r.Emergency);
        Assert.Contains("Aucun écran appairé", r.Error);
    }

    [Fact]
    public async Task Targets_can_be_an_area_a_zone_or_chosen_screens()
    {
        var zone = new Zone { Name = "Entrée" };
        var area = new Area { ClientId = f.Tenant.Id, Name = "Bâtiment B", Zones = new() { zone } };
        var other = new Area { ClientId = f.Tenant.Id, Name = "Bâtiment A", Zones = new() { new Zone { Name = "Défaut" } } };
        f.Store.Write(db => { db.Areas.Add(area); db.Areas.Add(other); });
        var inZone = Add("Entrée B", area: area.Id, zone: zone.Id);
        var inArea = Add("Couloir B", area: area.Id, zone: Guid.NewGuid());
        var outside = Add("Hall A", area: other.Id);

        svc.Trigger(f.Tenant.Id, new("Zone", "m", "zones", new() { zone.Id }, false, "api", "x", null, null)); await svc.Idle();
        Assert.Contains("\"title\":\"Zone\"", push.Last(inZone.Id));
        Assert.Equal("null", push.Last(inArea.Id) ?? "null");

        f.Time.Advance(TimeSpan.FromSeconds(1));
        svc.Trigger(f.Tenant.Id, new("Aire", "m", "areas", new() { area.Id }, false, "api", "x", null, null)); await svc.Idle();
        Assert.Contains("\"title\":\"Aire\"", push.Last(inArea.Id));
        Assert.Contains("\"title\":\"Aire\"", push.Last(inZone.Id));   // la plus récente passe devant
        Assert.Null(push.Last(outside.Id));

        f.Time.Advance(TimeSpan.FromSeconds(1));
        svc.Trigger(f.Tenant.Id, new("Choix", "m", "screens", new() { outside.Id }, false, "api", "x", null, null)); await svc.Idle();
        Assert.Contains("\"title\":\"Choix\"", push.Last(outside.Id));
        Assert.Equal(3, svc.Active(f.Tenant.Id).Count);
    }

    [Fact]
    public async Task The_most_recent_emergency_shows_and_the_previous_one_resumes_when_it_is_lifted()
    {
        var s = Add("Hall");
        var first = svc.Trigger(f.Tenant.Id, All("Première", "m1")).Emergency!;
        f.Time.Advance(TimeSpan.FromMinutes(1));
        var second = svc.Trigger(f.Tenant.Id, All("Seconde", "m2")).Emergency!;
        await svc.Idle();
        Assert.Contains("Seconde", push.Last(s.Id));
        Assert.True(svc.Lift(f.Tenant.Id, second.Id, "Camille"));
        await svc.Idle();
        Assert.Contains("Première", push.Last(s.Id));
        Assert.True(svc.Lift(f.Tenant.Id, first.Id, "Camille"));
        await svc.Idle();
        Assert.Equal("null", push.Last(s.Id));
        Assert.False(svc.Lift(f.Tenant.Id, first.Id, "Camille"));   // déjà levée
        Assert.Equal(new[] { "emergency.started", "emergency.started", "emergency.ended", "emergency.ended" }, events.Items.Select(i => i.Type));
        Assert.Contains("ended:Seconde", notices.Log);
    }

    [Fact]
    public async Task A_test_runs_on_one_screen_stops_by_itself_and_leaves_no_trace()
    {
        var a = Add("Hall"); var b = Add("Cafétéria");
        Assert.Equal(EmergencyService.Invalid, svc.Trigger(f.Tenant.Id, All(test: true)).Code);   // un essai se fait sur un seul écran
        var r = svc.Trigger(f.Tenant.Id, new("Essai", "Message d'essai", "screens", new() { a.Id }, true, "backoffice", "Camille", null, null));
        Assert.NotNull(r.Emergency);
        await svc.Idle();
        Assert.Contains("\"test\":true", push.Last(a.Id));
        Assert.Contains("\"until\":", push.Last(a.Id));
        Assert.Null(push.Last(b.Id));
        Assert.Empty(events.Items);            // un essai ne dit rien à l'extérieur
        Assert.Empty(notices.Log);
        Assert.Single(svc.Active(f.Tenant.Id));

        f.Time.Advance(TimeSpan.FromSeconds(Emergencies.TestSeconds + 1));
        Assert.Empty(svc.Active(f.Tenant.Id));
        Assert.Null(f.Store.Read(db => Emergencies.PayloadFor(db, a, f.Time.Now.UtcDateTime)));
        svc.Tick();
        Assert.Empty(svc.History(f.Tenant.Id));   // l'historique ne garde pas les essais
    }

    [Fact]
    public async Task The_report_tells_which_screens_display_wait_wake_or_are_offline()
    {
        f.Store.Write(_ => f.Tenant.SleepEnabled = true);
        var shown = Add("Affichée"); var waiting = Add("En attente");
        var offline = Add("Hors ligne", seen: f.Time.Now.UtcDateTime.AddHours(-2));
        var asleep = Add("En veille", seen: f.Time.Now.UtcDateTime.AddHours(-3));
        f.Store.Write(_ => asleep.ForcedSleep = true);
        var e = svc.Trigger(f.Tenant.Id, All()).Emergency!;
        await svc.Idle();
        Assert.Contains(asleep.Id, e.AsleepAtStart);

        var r1 = svc.Report(f.Tenant.Id, e.Id)!;
        Assert.Equal("pending", r1.Lines.First(l => l.Name == "Affichée").State);
        Assert.Equal("waking", r1.Lines.First(l => l.Name == "En veille").State);
        Assert.Equal("offline", r1.Lines.First(l => l.Name == "Hors ligne").State);

        svc.Ack(shown.Id, e.Id);
        f.Store.Write(_ => asleep.LastSeenUtc = f.Time.Now.UtcDateTime);   // l'écran réveillé se reconnecte…
        svc.Ack(asleep.Id, e.Id);                                           // …et confirme
        var r2 = svc.Report(f.Tenant.Id, e.Id)!;
        Assert.Equal((4, 2, 0, 1, 1), (r2.Targeted, r2.Displayed, r2.Waking, r2.Pending, r2.Offline));
        Assert.True(r2.Lines.First(l => l.Name == "En veille").Woke);
        Assert.Equal(new[] { "emergency.started", "emergency.acknowledged", "emergency.acknowledged" }, events.Items.Select(i => i.Type));

        svc.Ack(shown.Id, e.Id);   // une confirmation répétée ne refait pas d'événement
        Assert.Equal(3, events.Items.Count);

        f.Time.Advance(TimeSpan.FromMinutes(Emergencies.WakeMinutes + 1));
        Assert.Equal("offline", svc.Report(f.Tenant.Id, e.Id)!.Lines.First(l => l.Name == "Hors ligne").State);
    }

    [Fact]
    public void An_emergency_active_for_12_hours_is_reminded_once()
    {
        Add("Hall");
        var e = svc.Trigger(f.Tenant.Id, All()).Emergency!;
        f.Time.Advance(TimeSpan.FromHours(11));
        svc.Tick(); svc.Idle().Wait();
        Assert.DoesNotContain(notices.Log, l => l.StartsWith("reminder"));
        f.Time.Advance(TimeSpan.FromHours(1.1));
        svc.Tick(); svc.Idle().Wait();
        svc.Tick(); svc.Idle().Wait();
        Assert.Equal(1, notices.Log.Count(l => l == "reminder:Évacuation"));
        Assert.Equal(e.Id, f.Store.Read(db => f.Tenant.Emergencies.Single(x => x.ReminderUtc != null).Id));
    }

    [Fact]
    public void Removing_the_integration_ends_everything_and_the_history_is_kept_for_a_year()
    {
        var s = Add("Hall");
        var e = svc.Trigger(f.Tenant.Id, All()).Emergency!;
        f.Store.Write(db => { db.AppInstalls.RemoveAll(a => a.AppId == Emergencies.AppId); Emergencies.EndAll(f.Tenant, f.Time.Now.UtcDateTime, "intégration retirée"); });
        Assert.Null(f.Store.Read(db => Emergencies.PayloadFor(db, s, f.Time.Now.UtcDateTime)));
        Assert.Equal("intégration retirée", svc.Get(f.Tenant.Id, e.Id)!.EndedBy);
        Assert.Single(svc.History(f.Tenant.Id));
        f.Time.Advance(TimeSpan.FromDays(366));
        svc.Tick();
        Assert.Empty(svc.History(f.Tenant.Id));
    }

    [Fact]
    public void The_manifest_is_a_valid_pro_only_feature_kept_out_of_content_lists()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Linkii.Poc", "Apps");
        var cat = new AppCatalog(dir, NullLogger.Instance);
        var m = cat.Find("emergency");
        if (m == null) return;   // le dossier des manifestes n'est pas à cet endroit (dossier de sortie à part)
        Assert.True(m.IsFeature);
        Assert.Equal(Plans.Growth, m.Plan);
        Assert.False(m.DefaultOn);
        Assert.Empty(AppCatalog.Check(m));
    }

    [Fact]
    public void Api_access_and_events_of_the_integration_are_hidden_until_it_is_installed()
    {
        bool none(string app) => false;
        bool has(string app) => app == Emergencies.AppId;
        Assert.DoesNotContain(f.Registry.ScopesFor(none), s => s.Id == "emergency:write");
        Assert.Contains(f.Registry.ScopesFor(has), s => s.Id == "emergency:write");
        Assert.DoesNotContain(f.Registry.EventsFor(none), e => e.Id.StartsWith("emergency."));
        Assert.Contains(f.Registry.EventsFor(has), e => e.Id == "emergency.started");
        Assert.DoesNotContain(f.Registry.TemplatesFor(none), t => t.Name == "Alerte d'urgence seule");
        Assert.Contains(f.Registry.TemplatesFor(has), t => t.Name == "Alerte d'urgence seule");
        Assert.DoesNotContain("emergency:read", f.Registry.TemplatesFor(none).First(t => t.Name == "Lecture seule").Scopes);

        // une clé ou une adresse de webhook ne peut pas les prendre sans l'intégration
        f.Store.Write(db => db.AppInstalls.RemoveAll(a => a.AppId == Emergencies.AppId));
        Assert.Contains("installez-la", f.Keys.Create(f.Tenant.Id, null, "Alarme", null, new[] { "emergency:write" }, null, null).Error);
        Assert.Contains("installez-la", f.Hooks.Create(f.Tenant.Id, null, "Hook", "https://hooks.exemple.ch/x", new[] { "emergency.*" }).Error);
        Assert.Null(f.Hooks.Create(f.Tenant.Id, null, "Hook", "https://hooks.exemple.ch/x", new[] { "*" }).Error);   // « * » couvre aussi ping : accepté
        Install();
        Assert.Null(f.Keys.Create(f.Tenant.Id, null, "Alarme", "Alerte d'urgence seule", new[] { "emergency:write" }, null, null).Error);
        Assert.Null(f.Hooks.Create(f.Tenant.Id, null, "Hook 2", "https://hooks2.exemple.ch/x", new[] { "emergency.*" }).Error);
    }
}

/// <summary>Chaîne HTTP complète : l'API d'urgence et ce que reçoit le player.</summary>
[Collection("platform")]
public class EmergencyHttpTests(Platform p)
{
    private (Tenant T, string Key, Screen S, Screen S2) Org(bool installed = true, string plan = Plans.Growth, string template = "Alerte d'urgence seule", string[]? scopes = null)
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        var t = new Tenant { Name = "Org " + Guid.NewGuid().ToString("N")[..6], Plan = Plans.Growth, ResellerId = p.R1.Id };
        var s1 = new Screen { ClientId = t.Id, Name = "Hall", Token = Guid.NewGuid().ToString("N"), LastSeenUtc = DateTime.UtcNow };
        var s2 = new Screen { ClientId = t.Id, Name = "Réfectoire", Token = Guid.NewGuid().ToString("N"), LastSeenUtc = DateTime.UtcNow.AddHours(-2) };
        store.Write(db => { db.Clients.Add(t); db.Screens.Add(s1); db.Screens.Add(s2); if (installed) db.AppInstalls.Add(new AppInstall { ClientId = t.Id, AppId = Emergencies.AppId }); });
        var c = p.Services.GetRequiredService<ApiKeyService>().Create(t.Id, null, "Centrale incendie", template, scopes ?? new[] { "emergency:write", "emergency:read" }, null, null);
        Assert.Null(c.Error);
        store.Write(_ => t.Plan = plan);
        return (t, c.Secret!, s1, s2);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> Call(HttpClient c, HttpMethod m, string url, string key, object? body = null, string? idem = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("Authorization", "Bearer " + key);
        if (idem != null) r.Headers.Add("Idempotency-Key", idem);
        if (body != null) r.Content = JsonContent.Create(body);
        var res = await c.SendAsync(r);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length > 0 && text[0] == '{' ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private static HttpRequestMessage Player(string url, string token, object? body = null)
    {
        var r = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, url);
        r.Headers.Add("X-Token", token);
        if (body != null) r.Content = JsonContent.Create(body);
        return r;
    }

    [Fact]
    public async Task An_alarm_triggers_follows_and_lifts_an_emergency_through_the_api()
    {
        var (t, key, hall, _) = Org();
        var c = p.Client();
        var start = await Call(c, HttpMethod.Post, "/api/v1/emergency", key, new { title = "Évacuation", message = "Quittez le bâtiment B.", targets = new { all = true }, source = "Centrale incendie bâtiment B" }, idem: "alarme-1");
        Assert.Equal(HttpStatusCode.Created, start.Status);
        Assert.Equal("active", start.Json.GetProperty("status").GetString());
        Assert.Equal(2, start.Json.GetProperty("report").GetProperty("targeted").GetInt32());
        var id = start.Json.GetProperty("id").GetString();

        // le player de l'écran reçoit le message au prochain sondage, et la demande de réveil
        var v = await c.SendAsync(Player("/api/player/version", hall.Token!));
        var vj = JsonDocument.Parse(await v.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Évacuation", vj.GetProperty("emergency").GetProperty("title").GetString());
        Assert.True(vj.GetProperty("wakeUntil").GetInt64() > Helpers.EpochMs(DateTime.UtcNow));

        // l'écran confirme : le rapport le dit
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Player("/api/player/emergency-ack", hall.Token!, new { id = Guid.Parse(id!) }))).StatusCode);
        var rep = await Call(c, HttpMethod.Get, "/api/v1/emergency/" + id, key);
        Assert.Equal(1, rep.Json.GetProperty("report").GetProperty("displayed").GetInt32());
        Assert.Equal(1, rep.Json.GetProperty("report").GetProperty("offline").GetInt32());
        Assert.Contains(rep.Json.GetProperty("screens").EnumerateArray(), s => s.GetProperty("name").GetString() == "Hall" && s.GetProperty("state").GetString() == "displayed");

        // un POST rejoué avec la même clé d'idempotence ne déclenche pas une deuxième urgence
        var again = await Call(c, HttpMethod.Post, "/api/v1/emergency", key, new { title = "Évacuation", message = "Quittez le bâtiment B.", targets = new { all = true }, source = "Centrale incendie bâtiment B" }, idem: "alarme-1");
        Assert.Equal(id, again.Json.GetProperty("id").GetString());
        Assert.Single((await Call(c, HttpMethod.Get, "/api/v1/emergency", key)).Json.GetProperty("data").EnumerateArray());

        var lift = await Call(c, HttpMethod.Delete, "/api/v1/emergency/" + id, key);
        Assert.Equal("ended", lift.Json.GetProperty("status").GetString());
        var after = await c.SendAsync(Player("/api/player/version", hall.Token!));
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(await after.Content.ReadAsStringAsync()).RootElement.GetProperty("emergency").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await Call(c, HttpMethod.Delete, "/api/v1/emergency/" + id, key)).Status);   // lever deux fois : sans effet
        Assert.Empty((await Call(c, HttpMethod.Get, "/api/v1/emergency", key)).Json.GetProperty("data").EnumerateArray());
        Assert.Single((await Call(c, HttpMethod.Get, "/api/v1/emergency?status=all", key)).Json.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task The_api_refuses_without_the_installed_integration_the_pro_plan_or_the_right_scope()
    {
        var c = p.Client();
        var body = new { title = "T", message = "M", targets = new { all = true } };
        var (_, installedKey, _, _) = Org();
        // clé créée avec l'intégration, puis intégration retirée
        var (t2, key2, _, _) = Org();
        p.Services.GetRequiredService<JsonStore>().Write(db => db.AppInstalls.RemoveAll(a => a.ClientId == t2.Id));
        var missing = await Call(c, HttpMethod.Post, "/api/v1/emergency", key2, body);
        Assert.Equal(HttpStatusCode.Forbidden, missing.Status);
        Assert.Equal("integration_missing", missing.Json.GetProperty("code").GetString());

        var (_, ess, _, _) = Org(plan: Plans.Base);
        Assert.Equal("plan_required", (await Call(c, HttpMethod.Post, "/api/v1/emergency", ess, body)).Json.GetProperty("code").GetString());

        var (_, readOnly, _, _) = Org(template: "Lecture seule", scopes: new[] { "emergency:read" });
        var denied = await Call(c, HttpMethod.Post, "/api/v1/emergency", readOnly, body);
        Assert.Equal("scope_missing", denied.Json.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Call(c, HttpMethod.Get, "/api/v1/emergency", readOnly)).Status);

        Assert.Equal(HttpStatusCode.Created, (await Call(c, HttpMethod.Post, "/api/v1/emergency", installedKey, body)).Status);
    }

    [Fact]
    public async Task Bad_requests_get_a_clear_422_and_an_organisation_never_touches_anothers_emergency()
    {
        var (_, ka, _, _) = Org(); var (_, kb, _, _) = Org();
        var c = p.Client();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(c, HttpMethod.Post, "/api/v1/emergency", ka, new { title = "T", message = "M" })).Status);   // pas de cibles
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(c, HttpMethod.Post, "/api/v1/emergency", ka, new { title = "T", message = "M", targets = new { all = true, zones = new[] { Guid.NewGuid() } } })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(c, HttpMethod.Post, "/api/v1/emergency", ka, new { title = "", message = "M", targets = new { all = true } })).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(c, HttpMethod.Post, "/api/v1/emergency", ka, new { title = "T", message = "M", targets = new { screens = new[] { Guid.NewGuid() } } })).Status);

        var id = (await Call(c, HttpMethod.Post, "/api/v1/emergency", ka, new { title = "T", message = "M", targets = new { all = true } })).Json.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await Call(c, HttpMethod.Get, "/api/v1/emergency/" + id, kb)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(c, HttpMethod.Delete, "/api/v1/emergency/" + id, kb)).Status);
        Assert.Empty((await Call(c, HttpMethod.Get, "/api/v1/emergency", kb)).Json.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task A_screen_of_another_organisation_never_receives_it_and_an_unknown_token_is_refused()
    {
        var (_, key, _, _) = Org(); var (_, _, other, _) = Org();
        var c = p.Client();
        await Call(c, HttpMethod.Post, "/api/v1/emergency", key, new { title = "T", message = "M", targets = new { all = true } });
        var v = await c.SendAsync(Player("/api/player/version", other.Token!));
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(await v.Content.ReadAsStringAsync()).RootElement.GetProperty("emergency").ValueKind);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Player("/api/player/emergency-ack", "jeton-inconnu", new { id = Guid.NewGuid() }))).StatusCode);
    }
}
