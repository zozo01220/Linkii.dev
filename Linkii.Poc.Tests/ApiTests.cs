using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Horloge réglable : les réessais se testent sans attendre.</summary>
public sealed class TestTime : TimeProvider
{
    public DateTimeOffset Now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan t) => Now += t;
}

public sealed class FakeReceiver : HttpMessageHandler
{
    public sealed record Call(string Url, string Body, Dictionary<string, string> Headers);
    public readonly List<Call> Calls = new();
    public Func<Call, HttpResponseMessage> Reply = _ => new HttpResponseMessage(HttpStatusCode.OK);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var body = await req.Content!.ReadAsStringAsync(ct);
        var headers = req.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        var call = new Call(req.RequestUri!.ToString(), body, headers);
        lock (Calls) Calls.Add(call);
        return Reply(call);
    }
}

public sealed class FakeApiAlerts : IApiAlerts
{
    public readonly List<string> Disabled = new(), Expiring = new();
    public Task WebhookDisabled(Guid clientId, WebhookEndpoint endpoint, string reason) { Disabled.Add(endpoint.Name + " : " + reason); return Task.CompletedTask; }
    public Task KeyExpiring(Guid clientId, ApiKey key) { Expiring.Add(key.Name); return Task.CompletedTask; }
}

/// <summary>Le socle de l'API (§154) sans aucune fonction métier : clés, registre, bus, livraison, signature, réessais.</summary>
public class ApiFixture : IDisposable
{
    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Linkivi";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public readonly string Dir = Path.Combine(Path.GetTempPath(), "linkii-api-" + Guid.NewGuid().ToString("N"));
    public readonly TestTime Time = new();
    public readonly JsonStore Store;
    public readonly ApiRegistry Registry;
    public readonly ApiKeyService Keys;
    public readonly EventBus Bus;
    public WebhookJournal Journal;
    public readonly FakeReceiver Receiver = new();
    public readonly FakeApiAlerts Alerts = new();
    public readonly WebhookService Hooks;
    public readonly WebhookDispatcher Dispatcher;
    public readonly Tenant Tenant = new() { Name = "Horizon SA", Plan = Plans.Growth };
    public readonly Reseller Reseller = new() { Name = "Linkivi", Slug = "linkivi", IsDefault = true };

    public ApiFixture(IEnumerable<IApiModule>? extra = null)
    {
        Store = new JsonStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Linkii:DataDir"] = Dir }).Build(), new Env());
        Tenant.ResellerId = Reseller.Id;
        Store.Write(db => { db.Resellers.Add(Reseller); db.Clients.Add(Tenant); });
        Registry = new ApiRegistry(new IApiModule[] { new CoreApiModule() }.Concat(extra ?? Array.Empty<IApiModule>()));
        Keys = new ApiKeyService(Store, Registry, Time);
        Bus = new EventBus(Registry, Time, NullLogger<EventBus>.Instance);
        Journal = new WebhookJournal(Path.Combine(Dir, "apidata", "webhooks"), Time);
        var sender = new WebhookSender(Receiver, allowLocal: false);
        var box = new SecretBox(new EphemeralDataProtectionProvider());
        Hooks = new WebhookService(Store, Registry, box, Journal, sender, Time, Alerts, NullLogger<WebhookService>.Instance);
        Dispatcher = new WebhookDispatcher(Bus, Journal, sender, Hooks, Store, Time, NullLogger<WebhookDispatcher>.Instance);
    }

    public (WebhookEndpoint Endpoint, string Secret) Hook(string url = "https://hooks.exemple.ch/linkivi", params string[] events)
    {
        var r = Hooks.Create(Tenant.Id, null, "Récepteur", url, events.Length == 0 ? new[] { "ping" } : events);
        Assert.Null(r.Error);
        return (r.Endpoint!, r.Secret!);
    }

    /// <summary>Publie un événement sur le bus et le répartit tout de suite (le répartiteur est un service d'arrière-plan, lancé à la main ici).</summary>
    public int Publish(string type, object? data = null)
    {
        Bus.Publish(Tenant.Id, type, data);
        var n = 0;
        while (Bus.Reader.TryRead(out var ev)) n += Dispatcher.FanOut(ev);
        return n;
    }

    public void Dispose() { try { Directory.Delete(Dir, true); } catch (IOException) { } }
}

public class ApiKeyTests : IDisposable
{
    private readonly ApiFixture f = new();
    public void Dispose() => f.Dispose();

    [Fact]
    public void Generated_keys_are_well_formed_and_a_typo_is_caught()
    {
        var k = ApiKeyFormat.Generate();
        Assert.StartsWith("lkv_", k);
        Assert.Equal(ApiKeyFormat.Length, k.Length);
        Assert.True(ApiKeyFormat.IsWellFormed(k));
        var typo = k[..20] + (k[20] == 'a' ? 'b' : 'a') + k[21..];
        Assert.False(ApiKeyFormat.IsWellFormed(typo));
        Assert.False(ApiKeyFormat.IsWellFormed("lkv_court"));
        Assert.NotEqual(k, ApiKeyFormat.Generate());
        Assert.Equal(ApiKeyFormat.Hash(k), ApiKeyFormat.Hash(k));
    }

    [Fact]
    public void Only_the_hash_is_stored_never_the_key()
    {
        var c = f.Keys.Create(f.Tenant.Id, null, "Zabbix", "Lecture seule", new[] { "account:read" }, null, null);
        Assert.Null(c.Error);
        var json = File.ReadAllText(f.Store.Path);
        Assert.DoesNotContain(c.Secret!, json);
        Assert.Contains(c.Key!.Hash, json);
        Assert.Matches(@"^lkv_\w{8}…\w{4}$", c.Key.Prefix);
    }

    [Fact]
    public void Default_limit_is_ten_keys_and_the_console_can_change_it()
    {
        for (var i = 0; i < ApiLimits.DefaultKeys; i++) Assert.Null(f.Keys.Create(f.Tenant.Id, null, "k" + i, null, new[] { "account:read" }, null, null).Error);
        var over = f.Keys.Create(f.Tenant.Id, null, "de trop", null, new[] { "account:read" }, null, null);
        Assert.Contains("Limite atteinte", over.Error);
        f.Store.Write(_ => f.Tenant.ApiMaxKeys = 11);
        Assert.Null(f.Keys.Create(f.Tenant.Id, null, "onzième", null, new[] { "account:read" }, null, null).Error);
        // une clé révoquée ne compte plus
        f.Store.Write(_ => f.Tenant.ApiMaxKeys = null);
        f.Keys.Revoke(f.Tenant.Id, f.Keys.List(f.Tenant.Id)[0].Id);
        f.Keys.Revoke(f.Tenant.Id, f.Keys.List(f.Tenant.Id)[1].Id);
        Assert.Null(f.Keys.Create(f.Tenant.Id, null, "après révocation", null, new[] { "account:read" }, null, null).Error);
    }

    [Fact]
    public void The_api_is_for_the_pro_plan_and_validation_is_strict()
    {
        f.Store.Write(_ => f.Tenant.Plan = Plans.Base);
        Assert.Contains("plan Pro", f.Keys.Create(f.Tenant.Id, null, "k", null, new[] { "account:read" }, null, null).Error);
        f.Store.Write(_ => f.Tenant.Plan = Plans.Growth);
        Assert.NotNull(f.Keys.Create(f.Tenant.Id, null, " ", null, new[] { "account:read" }, null, null).Error);
        Assert.NotNull(f.Keys.Create(f.Tenant.Id, null, "k", null, Array.Empty<string>(), null, null).Error);
        Assert.Contains("inconnu", f.Keys.Create(f.Tenant.Id, null, "k", null, new[] { "nimporte:quoi" }, null, null).Error);
        Assert.Contains("IP invalide", f.Keys.Create(f.Tenant.Id, null, "k", null, new[] { "account:read" }, null, new[] { "pas-une-ip" }).Error);
        Assert.NotNull(f.Keys.Create(f.Tenant.Id, null, "k", null, new[] { "account:read" }, f.Time.Now.UtcDateTime.AddDays(-1), null).Error);
    }

    [Fact]
    public void Authentication_tells_unknown_revoked_expired_and_wrong_address_apart()
    {
        var c = f.Keys.Create(f.Tenant.Id, null, "Alarme", null, new[] { "account:read" }, f.Time.Now.UtcDateTime.AddDays(30), new[] { "203.0.113.0/24" });
        var bearer = "Bearer " + c.Secret;
        var ok = IPAddress.Parse("203.0.113.77");

        Assert.Equal(ApiCodes.MissingKey, f.Keys.Authenticate(null, ok, null).Code);
        Assert.Equal(ApiCodes.InvalidKey, f.Keys.Authenticate("Bearer lkv_nimportequoi", ok, null).Code);
        Assert.Equal(ApiCodes.InvalidKey, f.Keys.Authenticate("Bearer " + ApiKeyFormat.Generate(), ok, null).Code);   // bien formée mais inconnue
        Assert.True(f.Keys.Authenticate(bearer, ok, f.Reseller).Ok);
        Assert.Equal(ApiCodes.IpNotAllowed, f.Keys.Authenticate(bearer, IPAddress.Parse("198.51.100.1"), null).Code);
        Assert.Equal(ApiCodes.InvalidKey, f.Keys.Authenticate(bearer, ok, new Reseller()).Code);   // autre revendeur : la clé n'existe pas là

        f.Time.Advance(TimeSpan.FromDays(31));
        Assert.Equal(ApiCodes.KeyExpired, f.Keys.Authenticate(bearer, ok, null).Code);
        f.Time.Advance(-TimeSpan.FromDays(31));
        f.Keys.Revoke(f.Tenant.Id, c.Key!.Id);
        Assert.Equal(ApiCodes.KeyRevoked, f.Keys.Authenticate(bearer, ok, null).Code);
    }

    [Fact]
    public void Renewing_a_key_keeps_the_old_one_for_24_hours()
    {
        var c = f.Keys.Create(f.Tenant.Id, null, "Zapier", "Intégration", new[] { "account:read" }, null, null);
        var r = f.Keys.Renew(f.Tenant.Id, c.Key!.Id);
        Assert.NotNull(r.Key);
        Assert.Equal("Zapier", r.Key!.Name);
        Assert.NotEqual(c.Secret, r.Secret);
        Assert.True(f.Keys.Authenticate("Bearer " + c.Secret, null, null).Ok);
        Assert.True(f.Keys.Authenticate("Bearer " + r.Secret, null, null).Ok);
        f.Time.Advance(TimeSpan.FromHours(25));
        Assert.Equal(ApiCodes.KeyExpired, f.Keys.Authenticate("Bearer " + c.Secret, null, null).Code);
        Assert.True(f.Keys.Authenticate("Bearer " + r.Secret, null, null).Ok);
        Assert.NotNull(f.Keys.Renew(f.Tenant.Id, c.Key.Id).Error);   // déjà renouvelée
    }

    [Fact]
    public void Last_use_is_kept_in_memory_and_written_once_in_a_batch()
    {
        var c = f.Keys.Create(f.Tenant.Id, null, "Z", null, new[] { "account:read" }, null, null);
        Assert.Equal(ApiKey.Unused, c.Key!.State(f.Time.Now.UtcDateTime));
        var before = File.ReadAllText(f.Store.Path);
        for (var i = 0; i < 5; i++) f.Keys.Authenticate("Bearer " + c.Secret, null, null);
        Assert.Equal(before, File.ReadAllText(f.Store.Path));   // aucune écriture de data.json par requête
        Assert.NotNull(f.Keys.LastUsed(c.Key));
        Assert.Equal(1, f.Keys.FlushUsage());
        Assert.NotNull(c.Key.LastUsedUtc);
        Assert.Equal(0, f.Keys.FlushUsage());
    }

    [Fact]
    public void A_key_that_expires_soon_is_flagged_once()
    {
        f.Keys.Create(f.Tenant.Id, null, "Bientôt", null, new[] { "account:read" }, f.Time.Now.UtcDateTime.AddDays(9), null);
        f.Keys.Create(f.Tenant.Id, null, "Loin", null, new[] { "account:read" }, f.Time.Now.UtcDateTime.AddDays(200), null);
        var due = f.Keys.TakeExpiryNotices();
        Assert.Equal("Bientôt", Assert.Single(due).Key.Name);
        Assert.Empty(f.Keys.TakeExpiryNotices());
    }

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.0/24", true)]
    [InlineData("203.0.114.5", "203.0.113.0/24", false)]
    [InlineData("203.0.113.5", "203.0.113.5", true)]
    [InlineData("203.0.113.6", "203.0.113.5", false)]
    [InlineData("2001:db8::1", "2001:db8::/32", true)]
    [InlineData("2001:db9::1", "2001:db8::/32", false)]
    public void Ip_rules_accept_addresses_and_ranges(string ip, string rule, bool allowed) =>
        Assert.Equal(allowed, IpRule.Allows(new[] { rule }, IPAddress.Parse(ip)));
}

public class ApiRegistryTests
{
    private sealed class Mod(string id, ApiScope[] scopes, ApiTemplate[] templates, EventType[] events, ApiRoute[] routes) : IApiModule
    {
        public string Id => id;
        public IEnumerable<ApiScope> Scopes => scopes;
        public IEnumerable<ApiTemplate> Templates => templates;
        public IEnumerable<EventType> Events => events;
        public IEnumerable<ApiRoute> Routes => routes;
        public void Map(ApiRouter router) { }
    }

    [Fact]
    public void A_new_module_adds_scopes_templates_and_events_without_touching_the_core()
    {
        var urgence = new Mod("emergency",
            new[] { new ApiScope("emergency:write", "Urgence", "Déclencher et lever une urgence") },
            new[] { new ApiTemplate("Alerte d'urgence seule", "Urgence", new[] { "emergency:write" }, 20), new ApiTemplate("Lecture seule", "", new[] { "emergency:write" }) },
            new[] { new EventType("emergency.started", "Urgence", "Une urgence est affichée") },
            new[] { new ApiRoute("POST", "/emergency", "emergency:write", RateClass.Urgent, "Déclencher") });
        var reg = new ApiRegistry(new IApiModule[] { new CoreApiModule(), urgence });
        Assert.Contains(reg.Scopes, s => s.Id == "emergency:write");
        Assert.Contains(reg.Events, e => e.Id == "emergency.started");
        Assert.Contains(reg.Routes, r => r.Path == "/emergency");
        Assert.Contains("Alerte d'urgence seule", reg.Templates.Select(t => t.Name));
        // les gabarits de même nom s'additionnent
        Assert.Equal(new[] { "account:read", "emergency:write" }, reg.Templates.First(t => t.Name == "Lecture seule").Scopes.OrderBy(s => s));
        Assert.True(reg.IsValidSubscription("emergency.*"));
        Assert.True(reg.IsValidSubscription("*"));
        Assert.False(reg.IsValidSubscription("inconnu.*"));
    }

    [Fact]
    public void Inconsistent_modules_are_refused_at_startup()
    {
        var dup = new Mod("x", new[] { new ApiScope("account:read", "Compte", "doublon") }, Array.Empty<ApiTemplate>(), Array.Empty<EventType>(), Array.Empty<ApiRoute>());
        Assert.Throws<InvalidOperationException>(() => new ApiRegistry(new IApiModule[] { new CoreApiModule(), dup }));
        var badRoute = new Mod("y", Array.Empty<ApiScope>(), Array.Empty<ApiTemplate>(), Array.Empty<EventType>(), new[] { new ApiRoute("GET", "/x", "absent:read", RateClass.Read, "x") });
        Assert.Throws<InvalidOperationException>(() => new ApiRegistry(new IApiModule[] { new CoreApiModule(), badRoute }));
        var badTemplate = new Mod("z", Array.Empty<ApiScope>(), new[] { new ApiTemplate("T", "", new[] { "absent:read" }) }, Array.Empty<EventType>(), Array.Empty<ApiRoute>());
        Assert.Throws<InvalidOperationException>(() => new ApiRegistry(new IApiModule[] { new CoreApiModule(), badTemplate }));
    }

    [Theory]
    [InlineData("screen.*", "screen.offline", true)]
    [InlineData("screen.*", "content.published", false)]
    [InlineData("*", "anything", true)]
    [InlineData("ping", "ping", true)]
    [InlineData("ping", "pingue", false)]
    public void Subscriptions_support_wildcards(string pattern, string type, bool match) => Assert.Equal(match, ApiRegistry.Matches(pattern, type));

    [Fact]
    public void Limits_default_to_the_agreed_values_and_scale_by_class()
    {
        var t = new Tenant();
        Assert.Equal(10, ApiLimits.Keys(t));
        Assert.Equal(5, ApiLimits.Webhooks(t));
        Assert.Equal(60, ApiLimits.RatePerMinute(t));
        Assert.Equal(30, ApiLimits.JournalDays(t));
        Assert.Equal(60, ApiLimits.Permits(t, RateClass.Read));
        Assert.Equal(30, ApiLimits.Permits(t, RateClass.Write));
        Assert.Equal(10, ApiLimits.Permits(t, RateClass.Urgent));
        t.ApiRatePerMinute = 120; t.ApiMaxKeys = 0; t.ApiJournalDays = 7;
        Assert.Equal(60, ApiLimits.Permits(t, RateClass.Write));
        Assert.Equal(0, ApiLimits.Keys(t));
        Assert.Equal(7, ApiLimits.JournalDays(t));
    }
}

public class WebhookTests : IDisposable
{
    private readonly ApiFixture f = new();
    public void Dispose() => f.Dispose();

    private Delivery Only() => Assert.Single(f.Journal.Query(f.Tenant.Id));

    [Fact]
    public void Signature_has_a_known_vector_and_rejects_forgeries_and_old_messages()
    {
        var sig = WebhookSignature.Compute("whsec_test", 1798810560, "{\"a\":1}");
        Assert.Equal(64, sig.Length);
        Assert.Equal(sig, WebhookSignature.Compute("whsec_test", 1798810560, "{\"a\":1}"));
        Assert.NotEqual(sig, WebhookSignature.Compute("whsec_test", 1798810561, "{\"a\":1}"));
        var now = DateTimeOffset.FromUnixTimeSeconds(1798810560);
        var header = WebhookSignature.Header(new[] { "whsec_test" }, 1798810560, "{\"a\":1}");
        Assert.True(WebhookSignature.Verify(header, "{\"a\":1}", "whsec_test", now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(header, "{\"a\":2}", "whsec_test", now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(header, "{\"a\":1}", "autre", now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(header, "{\"a\":1}", "whsec_test", now.AddMinutes(6), TimeSpan.FromMinutes(5)));   // rejeu trop ancien
    }

    [Fact]
    public async Task An_event_is_delivered_signed_and_journaled()
    {
        var (ep, secret) = f.Hook();
        Assert.Equal(1, f.Publish("ping", new { hello = "monde" }));
        await f.Dispatcher.TickAndWait();

        var call = Assert.Single(f.Receiver.Calls);
        Assert.Equal("ping", call.Headers["Linkivi-Event"]);
        Assert.StartsWith("dlv_", call.Headers["Linkivi-Delivery"]);
        Assert.True(WebhookSignature.Verify(call.Headers["Linkivi-Signature"], call.Body, secret, f.Time.Now, TimeSpan.FromMinutes(5)));
        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal("ping", body.RootElement.GetProperty("type").GetString());
        Assert.Equal(ApiRegistry.Version, body.RootElement.GetProperty("apiVersion").GetString());
        Assert.Equal(f.Tenant.Id, body.RootElement.GetProperty("organisationId").GetGuid());
        Assert.Equal("monde", body.RootElement.GetProperty("data").GetProperty("hello").GetString());
        var d = Only();
        Assert.Equal(Delivery.Delivered, d.Status);
        Assert.Equal(ep.Id, d.EndpointId);
    }

    [Fact]
    public async Task Retries_follow_1_minute_5_minutes_30_minutes_2_hours_then_give_up()
    {
        f.Hook();
        f.Receiver.Reply = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        f.Publish("ping");
        var expected = new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2) };
        for (var i = 0; i < 4; i++)
        {
            var before = f.Time.Now;
            await f.Dispatcher.TickAndWait();
            var d = Only();
            Assert.Equal(Delivery.Pending, d.Status);
            Assert.Equal(i + 1, d.Attempts.Count);
            Assert.Equal(before.UtcDateTime + expected[i], d.NextUtc);
            f.Time.Advance(expected[i] - TimeSpan.FromSeconds(1));
            await f.Dispatcher.TickAndWait();   // pas encore l'heure
            Assert.Equal(i + 1, Only().Attempts.Count);
            f.Time.Advance(TimeSpan.FromSeconds(1));
        }
        await f.Dispatcher.TickAndWait();
        var last = Only();
        Assert.Equal(Delivery.Abandoned, last.Status);
        Assert.Equal(WebhookDispatcher.MaxAttempts, last.Attempts.Count);
        Assert.Null(last.NextUtc);
        Assert.Equal(5, f.Receiver.Calls.Count);
        Assert.Single(f.Receiver.Calls.Select(c => c.Headers["Linkivi-Delivery"]).Distinct());   // même identifiant de livraison à chaque tentative
    }

    [Fact]
    public async Task A_receiver_that_succeeds_on_retry_ends_delivered()
    {
        f.Hook();
        var n = 0;
        f.Receiver.Reply = _ => new HttpResponseMessage(++n < 2 ? HttpStatusCode.BadGateway : HttpStatusCode.NoContent);
        f.Publish("ping");
        await f.Dispatcher.TickAndWait();
        f.Time.Advance(TimeSpan.FromMinutes(1));
        await f.Dispatcher.TickAndWait();
        var d = Only();
        Assert.Equal(Delivery.Delivered, d.Status);
        Assert.Equal(2, d.Attempts.Count);
    }

    [Fact]
    public async Task Ten_consecutive_failures_disable_the_address_and_warn_the_admins()
    {
        var (ep, _) = f.Hook();
        f.Receiver.Reply = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        for (var i = 0; i < 2; i++) f.Publish("ping");
        for (var round = 0; round < 8 && f.Hooks.Get(f.Tenant.Id, ep.Id)!.Active; round++)
        {
            await f.Dispatcher.TickAndWait();
            f.Time.Advance(TimeSpan.FromHours(3));
        }
        var after = f.Hooks.Get(f.Tenant.Id, ep.Id)!;
        Assert.False(after.Active);
        Assert.Contains("10 échecs consécutifs (HTTP 503)", after.DisabledReason);
        await Task.Delay(100);
        Assert.Single(f.Alerts.Disabled);

        // plus rien n'est envoyé à une adresse désactivée, et rien ne se perd
        var calls = f.Receiver.Calls.Count;
        f.Publish("ping");
        await f.Dispatcher.TickAndWait();
        Assert.Equal(calls, f.Receiver.Calls.Count);
        Assert.All(f.Journal.Query(f.Tenant.Id), d => Assert.Equal(Delivery.Abandoned, d.Status));
    }

    [Fact]
    public async Task Reactivating_replays_the_last_24_hours()
    {
        var (ep, _) = f.Hook();
        f.Receiver.Reply = _ => new HttpResponseMessage(HttpStatusCode.Gone);
        f.Publish("ping");
        await f.Dispatcher.TickAndWait();
        Assert.False(f.Hooks.Get(f.Tenant.Id, ep.Id)!.Active);   // 410 : désactivée tout de suite
        Assert.Equal(Delivery.Abandoned, Only().Status);

        f.Receiver.Reply = _ => new HttpResponseMessage(HttpStatusCode.OK);
        Assert.Equal(1, f.Hooks.Reactivate(f.Tenant.Id, ep.Id));
        await f.Dispatcher.TickAndWait();
        Assert.Contains(f.Journal.Query(f.Tenant.Id), d => d.Status == Delivery.Delivered && d.Note == "rejouée");
        Assert.True(f.Hooks.Get(f.Tenant.Id, ep.Id)!.Active);
    }

    [Fact]
    public async Task Only_subscribed_addresses_get_an_event_and_unknown_types_are_dropped()
    {
        f.Hook("https://a.exemple.ch/", "ping");
        Assert.Equal(0, f.Publish("jamais.declare"));
        Assert.Equal(1, f.Publish("ping"));
        var other = f.Hooks.Create(f.Tenant.Id, null, "Tous", "https://b.exemple.ch/", new[] { "*" });
        Assert.Null(other.Error);
        Assert.Equal(2, f.Publish("ping"));
        await f.Dispatcher.TickAndWait();
        Assert.Equal(3, f.Receiver.Calls.Count);
    }

    [Fact]
    public async Task Another_organisation_never_receives_the_event()
    {
        f.Hook();
        var other = new Tenant { Name = "Autre", Plan = Plans.Growth, ResellerId = f.Reseller.Id };
        f.Store.Write(db => db.Clients.Add(other));
        f.Bus.Publish(other.Id, "ping");
        while (f.Bus.Reader.TryRead(out var ev)) Assert.Equal(0, f.Dispatcher.FanOut(ev));
        await f.Dispatcher.TickAndWait();
        Assert.Empty(f.Receiver.Calls);
    }

    [Fact]
    public async Task The_test_button_sends_once_without_retry_and_is_journaled()
    {
        var (ep, secret) = f.Hook();
        var ok = await f.Hooks.SendTest(f.Tenant.Id, ep.Id);
        Assert.Equal(Delivery.Delivered, ok!.Status);
        Assert.True(ok.Test);
        f.Receiver.Reply = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var bad = await f.Hooks.SendTest(f.Tenant.Id, ep.Id);
        Assert.Equal(Delivery.Failed, bad!.Status);
        Assert.Null(bad.NextUtc);
        Assert.Equal(2, f.Journal.Query(f.Tenant.Id).Count);
        Assert.Empty(f.Journal.Pending());
        Assert.Equal(0, f.Hooks.Consecutive(ep.Id));   // un essai ne compte pas dans la désactivation automatique
    }

    [Fact]
    public async Task Rotating_the_secret_signs_with_both_during_24_hours()
    {
        var (ep, oldSecret) = f.Hook();
        var fresh = f.Hooks.RotateSecret(f.Tenant.Id, ep.Id)!;
        Assert.NotEqual(oldSecret, fresh);
        await f.Hooks.SendTest(f.Tenant.Id, ep.Id);
        var header = f.Receiver.Calls[^1].Headers["Linkivi-Signature"];
        var body = f.Receiver.Calls[^1].Body;
        Assert.Equal(2, header.Split(',').Count(p => p.StartsWith("v1=")));
        Assert.True(WebhookSignature.Verify(header, body, oldSecret, f.Time.Now, TimeSpan.FromMinutes(5)));
        Assert.True(WebhookSignature.Verify(header, body, fresh, f.Time.Now, TimeSpan.FromMinutes(5)));

        f.Time.Advance(TimeSpan.FromHours(25));
        await f.Hooks.SendTest(f.Tenant.Id, ep.Id);
        var later = f.Receiver.Calls[^1];
        Assert.Single(later.Headers["Linkivi-Signature"].Split(',').Where(p => p.StartsWith("v1=")));
        Assert.True(WebhookSignature.Verify(later.Headers["Linkivi-Signature"], later.Body, fresh, f.Time.Now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSignature.Verify(later.Headers["Linkivi-Signature"], later.Body, oldSecret, f.Time.Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void The_secret_is_encrypted_at_rest_and_never_listed()
    {
        var (_, secret) = f.Hook();
        Assert.DoesNotContain(secret, File.ReadAllText(f.Store.Path));
    }

    [Theory]
    [InlineData("http://exemple.ch/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://192.168.1.10/hook")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://user:pass@exemple.ch/hook")]
    [InlineData("ftp://exemple.ch/")]
    [InlineData("pas une adresse")]
    [InlineData("")]
    public void Receiver_addresses_must_be_public_https(string url) => Assert.NotNull(f.Hooks.ValidateUrl(url));

    [Fact]
    public void A_public_https_address_is_accepted()
    {
        Assert.Null(f.Hooks.ValidateUrl("https://hooks.exemple.ch/linkivi?x=1"));
        Assert.Null(f.Hooks.ValidateUrl("https://203.0.113.9/hook"));
    }

    [Fact]
    public void Default_limit_is_five_addresses_and_events_must_exist()
    {
        for (var i = 0; i < ApiLimits.DefaultWebhooks; i++) Assert.Null(f.Hooks.Create(f.Tenant.Id, null, "h" + i, $"https://h{i}.exemple.ch/", new[] { "ping" }).Error);
        Assert.Contains("Limite atteinte", f.Hooks.Create(f.Tenant.Id, null, "de trop", "https://z.exemple.ch/", new[] { "ping" }).Error);
        f.Store.Write(_ => f.Tenant.ApiMaxWebhooks = 6);
        Assert.Null(f.Hooks.Create(f.Tenant.Id, null, "sixième", "https://z.exemple.ch/", new[] { "ping" }).Error);
        Assert.Contains("inconnu", f.Hooks.Create(f.Tenant.Id, null, "x", "https://y.exemple.ch/", new[] { "nimporte.quoi" }).Error);
        Assert.NotNull(f.Hooks.Create(f.Tenant.Id, null, "x", "https://y.exemple.ch/", Array.Empty<string>()).Error);
    }

    [Fact]
    public void The_journal_survives_a_restart_and_pending_deliveries_resume()
    {
        f.Hook();
        f.Publish("ping");
        Assert.Single(f.Journal.Pending());
        var again = new WebhookJournal(Path.Combine(f.Dir, "apidata", "webhooks"), f.Time);
        again.LoadAll();
        var d = Assert.Single(again.Pending());
        Assert.Equal("ping", d.EventType);
        Assert.Equal(Delivery.Pending, d.Status);
    }

    [Fact]
    public async Task Old_deliveries_are_purged_after_the_retention_of_the_organisation()
    {
        f.Hook();
        f.Publish("ping");
        await f.Dispatcher.TickAndWait();
        f.Time.Advance(TimeSpan.FromDays(29));
        f.Journal.Prune(_ => ApiLimits.JournalDays(f.Tenant));
        Assert.Single(f.Journal.Query(f.Tenant.Id));
        f.Time.Advance(TimeSpan.FromDays(2));
        f.Journal.Prune(_ => ApiLimits.JournalDays(f.Tenant));
        Assert.Empty(f.Journal.Query(f.Tenant.Id));
        var again = new WebhookJournal(Path.Combine(f.Dir, "apidata", "webhooks"), f.Time);
        again.LoadAll();
        Assert.Empty(again.Query(f.Tenant.Id));
    }

    [Fact]
    public async Task Stats_report_the_delivery_rate_and_median_delay()
    {
        var (ep, _) = f.Hook();
        for (var i = 0; i < 3; i++) f.Publish("ping");
        await f.Dispatcher.TickAndWait();
        var s = f.Hooks.Stats(f.Tenant.Id, ep.Id);
        Assert.Equal(3, s.Delivered);
        Assert.Equal(100, s.RatePercent);
        Assert.Equal(0, s.Consecutive);
    }

    [Fact]
    public void Publishing_never_throws_and_never_blocks_under_the_store_lock()
    {
        f.Hook();
        // appelé depuis un Write de JsonStore (comme ScreenWatchWorker) : aucun accès disque ni réseau
        f.Store.Write(db => f.Bus.Publish(f.Tenant.Id, "ping", new { a = 1 }));
        Assert.True(f.Bus.Reader.TryRead(out var ev));
        Assert.Equal("ping", ev!.Type);
        f.Bus.Publish(f.Tenant.Id, "inconnu");
        Assert.False(f.Bus.Reader.TryRead(out _));
    }
}

/// <summary>Chaîne HTTP complète (/api/v1) sur l'application de test : clé, accès, licence, suspension, isolation, débit, idempotence, erreurs, documentation.</summary>
[Collection("platform")]
public class ApiHttpTests(Platform p)
{
    private (Tenant T, string Key) Org(string plan = Plans.Growth, string? template = null, string[]? scopes = null, int? rate = null, Reseller? reseller = null)
    {
        var t = new Tenant { Name = "Org " + Guid.NewGuid().ToString("N")[..6], Plan = plan, ResellerId = (reseller ?? p.R1).Id, ApiRatePerMinute = rate };
        p.Services.GetRequiredService<JsonStore>().Write(db => db.Clients.Add(t));
        var keys = p.Services.GetRequiredService<ApiKeyService>();
        // une organisation Essential ne peut pas créer de clé : on la crée Pro puis on change le plan
        p.Services.GetRequiredService<JsonStore>().Write(_ => t.Plan = Plans.Growth);
        var c = keys.Create(t.Id, null, "Test", template, scopes ?? new[] { "account:read", "webhooks:manage" }, null, null);
        Assert.Null(c.Error);
        p.Services.GetRequiredService<JsonStore>().Write(_ => t.Plan = plan);
        return (t, c.Secret!);
    }

    private static HttpRequestMessage Req(HttpMethod m, string url, string? key, object? body = null, string? idem = null)
    {
        var r = new HttpRequestMessage(m, url);
        if (key != null) r.Headers.Add("Authorization", "Bearer " + key);
        if (idem != null) r.Headers.Add("Idempotency-Key", idem);
        if (body != null) r.Content = JsonContent.Create(body);
        return r;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json, HttpResponseMessage Res)> Send(HttpClient c, HttpRequestMessage r)
    {
        var res = await c.SendAsync(r);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length > 0 && text[0] == '{' ? JsonDocument.Parse(text).RootElement.Clone() : default, res);
    }

    [Fact]
    public async Task Without_a_valid_key_the_answer_is_a_stable_problem_json()
    {
        var c = p.Client();
        var none = await Send(c, Req(HttpMethod.Get, "/api/v1/me", null));
        Assert.Equal(HttpStatusCode.Unauthorized, none.Status);
        Assert.Equal("application/problem+json", none.Res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("missing_key", none.Json.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(none.Json.GetProperty("requestId").GetString()));
        Assert.True(none.Res.Headers.Contains("X-Request-Id"));

        var bad = await Send(c, Req(HttpMethod.Get, "/api/v1/me", "lkv_n_importe_quoi"));
        Assert.Equal("invalid_key", bad.Json.GetProperty("code").GetString());

        var unknown = await Send(c, Req(HttpMethod.Get, "/api/v1/introuvable", ApiKeyFormat.Generate()));
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
    }

    [Fact]
    public async Task A_cookie_login_is_never_accepted_on_the_api()
    {
        var r = await p.Client().GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Me_describes_the_key_the_organisation_and_the_limits()
    {
        var (t, key) = Org();
        var r = await Send(p.Client(), Req(HttpMethod.Get, "/api/v1/me", key));
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(t.Name, r.Json.GetProperty("organisation").GetProperty("name").GetString());
        Assert.Equal(60, r.Json.GetProperty("limits").GetProperty("requestsPerMinute").GetProperty("read").GetInt32());
        Assert.Equal(10, r.Json.GetProperty("limits").GetProperty("requestsPerMinute").GetProperty("urgent").GetInt32());
        Assert.Equal(10, r.Json.GetProperty("limits").GetProperty("keys").GetInt32());
        Assert.DoesNotContain(key, r.Json.GetRawText());
    }

    [Fact]
    public async Task A_key_only_works_on_the_domain_of_its_reseller()
    {
        var (_, key) = Org();   // revendeur par défaut
        var acme = await Send(p.Client("affichage.acme.ch"), Req(HttpMethod.Get, "/api/v1/me", key));
        Assert.Equal(HttpStatusCode.Unauthorized, acme.Status);
        Assert.Equal("invalid_key", acme.Json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Missing_scope_essential_plan_and_suspended_space_are_refused_with_their_own_code()
    {
        var (_, readOnly) = Org(scopes: new[] { "account:read" });
        var noScope = await Send(p.Client(), Req(HttpMethod.Get, "/api/v1/webhooks", readOnly));
        Assert.Equal(HttpStatusCode.Forbidden, noScope.Status);
        Assert.Equal("scope_missing", noScope.Json.GetProperty("code").GetString());

        var (_, ess) = Org(plan: Plans.Base);
        var plan = await Send(p.Client(), Req(HttpMethod.Get, "/api/v1/me", ess));
        Assert.Equal("plan_required", plan.Json.GetProperty("code").GetString());

        var (susp, k) = Org();
        p.Services.GetRequiredService<JsonStore>().Write(_ => susp.Suspended = true);
        var suspended = await Send(p.Client(), Req(HttpMethod.Get, "/api/v1/me", k));
        Assert.Equal("space_suspended", suspended.Json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_revoked_key_stops_working_immediately()
    {
        var (t, key) = Org();
        Assert.Equal(HttpStatusCode.OK, (await Send(p.Client(), Req(HttpMethod.Get, "/api/v1/me", key))).Status);
        var keys = p.Services.GetRequiredService<ApiKeyService>();
        keys.Revoke(t.Id, keys.List(t.Id)[0].Id);
        var r = await Send(p.Client(), Req(HttpMethod.Get, "/api/v1/me", key));
        Assert.Equal(HttpStatusCode.Unauthorized, r.Status);
        Assert.Equal("key_revoked", r.Json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_organisation_cannot_see_or_touch_the_webhooks_of_another()
    {
        var (a, ka) = Org(); var (b, kb) = Org();
        var c = p.Client();
        var created = await Send(c, Req(HttpMethod.Post, "/api/v1/webhooks", kb, new { name = "B", url = "https://b.exemple.ch/h", events = new[] { "ping" } }));
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var idB = created.Json.GetProperty("id").GetString();
        Assert.StartsWith("whsec_", created.Json.GetProperty("secret").GetString());

        var listA = await Send(c, Req(HttpMethod.Get, "/api/v1/webhooks", ka));
        Assert.Empty(listA.Json.GetProperty("data").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, Req(HttpMethod.Get, $"/api/v1/webhooks/{idB}", ka))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, Req(HttpMethod.Delete, $"/api/v1/webhooks/{idB}", ka))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, Req(HttpMethod.Post, $"/api/v1/webhooks/{idB}/test", ka))).Status);
        var listB = await Send(c, Req(HttpMethod.Get, "/api/v1/webhooks", kb));
        Assert.Single(listB.Json.GetProperty("data").EnumerateArray());
        Assert.DoesNotContain("secret", listB.Json.GetRawText());   // le secret n'est rendu qu'à la création
    }

    [Fact]
    public async Task Webhooks_can_be_managed_through_the_api()
    {
        var (_, key) = Org();
        var c = p.Client();
        var bad = await Send(c, Req(HttpMethod.Post, "/api/v1/webhooks", key, new { name = "x", url = "http://exemple.ch", events = new[] { "ping" } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
        Assert.Equal("invalid_request", bad.Json.GetProperty("code").GetString());

        var ok = await Send(c, Req(HttpMethod.Post, "/api/v1/webhooks", key, new { name = "Supervision", url = "https://hooks.exemple.ch/x", events = new[] { "ping" } }));
        var id = ok.Json.GetProperty("id").GetString();
        var patched = await Send(c, Req(HttpMethod.Patch, $"/api/v1/webhooks/{id}", key, new { name = "Renommée" }));
        Assert.Equal("Renommée", patched.Json.GetProperty("name").GetString());
        Assert.Equal("https://hooks.exemple.ch/x", patched.Json.GetProperty("url").GetString());
        var rotated = await Send(c, Req(HttpMethod.Post, $"/api/v1/webhooks/{id}/rotate-secret", key));
        Assert.StartsWith("whsec_", rotated.Json.GetProperty("secret").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await Send(c, Req(HttpMethod.Delete, $"/api/v1/webhooks/{id}", key))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(c, Req(HttpMethod.Get, $"/api/v1/webhooks/{id}", key))).Status);
    }

    [Fact]
    public async Task Requests_beyond_the_rate_limit_get_429_with_retry_after()
    {
        var (_, key) = Org(rate: 3);
        var c = p.Client();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await Send(c, Req(HttpMethod.Get, "/api/v1/me", key))).Status);
        var r = await Send(c, Req(HttpMethod.Get, "/api/v1/me", key));
        Assert.Equal(HttpStatusCode.TooManyRequests, r.Status);
        Assert.Equal("rate_limited", r.Json.GetProperty("code").GetString());
        Assert.True(r.Res.Headers.Contains("Retry-After"));
        // une autre clé n'est pas touchée
        var (_, other) = Org();
        Assert.Equal(HttpStatusCode.OK, (await Send(c, Req(HttpMethod.Get, "/api/v1/me", other))).Status);
    }

    [Fact]
    public async Task A_replayed_post_with_the_same_idempotency_key_does_nothing_more()
    {
        var (t, key) = Org();
        var c = p.Client();
        var body = new { name = "Une fois", url = "https://hooks.exemple.ch/once", events = new[] { "ping" } };
        var first = await Send(c, Req(HttpMethod.Post, "/api/v1/webhooks", key, body, idem: "alarme-42"));
        var second = await Send(c, Req(HttpMethod.Post, "/api/v1/webhooks", key, body, idem: "alarme-42"));
        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.Created, second.Status);
        Assert.Equal(first.Json.GetProperty("id").GetString(), second.Json.GetProperty("id").GetString());
        Assert.Equal("true", second.Res.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Single(p.Services.GetRequiredService<WebhookService>().List(t.Id));

        var other = await Send(c, Req(HttpMethod.Post, "/api/v1/webhooks", key, new { name = "Autre", url = "https://hooks.exemple.ch/other", events = new[] { "ping" } }, idem: "alarme-42"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.Status);
        Assert.Equal("idempotency_conflict", other.Json.GetProperty("code").GetString());
        Assert.Single(p.Services.GetRequiredService<WebhookService>().List(t.Id));
    }

    [Fact]
    public async Task Documentation_and_openapi_come_from_the_registry()
    {
        var c = p.Client();
        var spec = await c.GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, spec.StatusCode);
        using var doc = JsonDocument.Parse(await spec.Content.ReadAsStringAsync());
        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/me", out var me));
        Assert.Equal("account:read", me.GetProperty("get").GetProperty("x-linkivi-scope").GetString());
        Assert.True(paths.TryGetProperty("/webhooks/{id}/test", out _));

        var html = await c.GetStringAsync("/docs/api");
        Assert.Contains("Premier appel", html);
        Assert.Contains("Linkivi-Signature", html);
        Assert.Contains("<code>ping</code>", html);
        Assert.Contains("scope_missing", html);
        var acme = await p.Client("affichage.acme.ch").GetStringAsync("/docs/api");
        Assert.Contains("Acme Affichage", acme);
        Assert.Contains("#ff0000", acme);
    }

    [Fact]
    public void Every_registered_route_is_mapped_with_its_declaration()
    {
        var reg = p.Services.GetRequiredService<ApiRegistry>();
        var endpoints = p.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().ToList();
        foreach (var r in reg.Routes)
            Assert.Contains(endpoints, e => e.Metadata.GetMetadata<ApiRoute>() == r && e.RoutePattern.RawText == "/api/v1" + r.Path);
    }
}

/// <summary>Supervision (§153) : événements screen.offline / screen.online et lecture des écrans par l'API.</summary>
public class ScreenEventsTests : IDisposable
{
    private readonly ApiFixture f = new(new IApiModule[] { new ScreensApiModule() });
    private readonly DateTime now = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    private readonly DateTime started = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);
    public void Dispose() => f.Dispose();

    private Screen Paired(string name = "Hall d'entrée", DateTime? seen = null)
    {
        var s = new Screen { ClientId = f.Tenant.Id, Name = name, Token = Guid.NewGuid().ToString("N"), LastSeenUtc = seen ?? now.AddSeconds(-5) };
        f.Store.Write(db => db.Screens.Add(s));
        return s;
    }

    private List<ScreenEvent> Plan(DateTime at) => f.Store.Write(db => ScreenEvents.Plan(db, at, started));

    [Fact]
    public void An_offline_screen_is_signalled_once_then_again_when_it_is_back()
    {
        f.Hook(events: "screen.*");
        f.Store.Write(_ => f.Tenant.AlertOfflineMinutes = 5);
        var s = Paired(seen: now.AddMinutes(-10));

        var down = Assert.Single(Plan(now));
        Assert.Equal(ScreenEvents.Offline, down.Type);
        Assert.Empty(Plan(now.AddMinutes(1)));   // une seule fois par panne

        f.Store.Write(_ => s.LastSeenUtc = now.AddMinutes(4).AddSeconds(-5));
        var up = Assert.Single(Plan(now.AddMinutes(4)));
        Assert.Equal(ScreenEvents.Online, up.Type);
        Assert.Contains("downtimeMinutes", JsonSerializer.Serialize(up.Data));
        Assert.Empty(Plan(now.AddMinutes(5)));
    }

    [Fact]
    public void A_short_loss_of_signal_is_not_a_failure()
    {
        f.Hook(events: "screen.offline");
        f.Store.Write(_ => f.Tenant.AlertOfflineMinutes = 15);
        Paired(seen: now.AddMinutes(-10));
        Assert.Empty(Plan(now));
        Assert.Single(Plan(now.AddMinutes(6)));
    }

    [Fact]
    public void Nothing_is_sent_during_the_grace_period_after_a_server_start()
    {
        f.Hook(events: "screen.*");
        f.Store.Write(_ => f.Tenant.AlertOfflineMinutes = 15);
        Paired(seen: now.AddHours(-3));
        Assert.Empty(f.Store.Write(db => ScreenEvents.Plan(db, started.AddMinutes(5), started)));
    }

    [Fact]
    public void Events_follow_the_webhooks_not_the_alert_settings()
    {
        f.Store.Write(_ => { f.Tenant.AlertsOn = false; f.Tenant.AlertMail = false; f.Tenant.AlertPush = false; f.Tenant.AlertOfflineMinutes = 5; });
        var s = Paired(seen: now.AddMinutes(-10));
        Assert.Empty(Plan(now));   // personne n'écoute : rien, et pas de repère posé
        Assert.Null(s.ApiOfflineUtc);
        f.Hook(events: "screen.*");
        Assert.Single(Plan(now));  // alertes coupées, mais un webhook actif : l'événement part
    }

    [Fact]
    public void Essential_organisations_and_cut_spaces_send_nothing()
    {
        f.Hook(events: "screen.*");
        f.Store.Write(_ => f.Tenant.AlertOfflineMinutes = 5);
        Paired(seen: now.AddMinutes(-10));
        f.Store.Write(_ => f.Tenant.Plan = Plans.Base);
        Assert.Empty(Plan(now));
        f.Store.Write(_ => { f.Tenant.Plan = Plans.Growth; f.Tenant.Suspended = true; });
        Assert.Empty(Plan(now));
    }

    [Fact]
    public void A_sleeping_screen_is_not_a_failure_and_unpaired_screens_are_ignored()
    {
        f.Hook(events: "screen.*");
        f.Store.Write(_ => f.Tenant.AlertOfflineMinutes = 5);
        var asleep = Paired("Veille", now.AddHours(-2));
        f.Store.Write(_ => { f.Tenant.SleepEnabled = true; asleep.ForcedSleep = true; });
        f.Store.Write(db => db.Screens.Add(new Screen { ClientId = f.Tenant.Id, Name = "Jamais appairé", LastSeenUtc = now.AddHours(-2) }));
        Assert.Empty(Plan(now));
    }

    [Fact]
    public async Task The_event_reaches_a_subscribed_webhook_signed_end_to_end()
    {
        var (_, secret) = f.Hook(events: "screen.offline");
        f.Store.Write(_ => f.Tenant.AlertOfflineMinutes = 5);
        Paired("Salle d'attente", now.AddMinutes(-10));
        foreach (var e in Plan(now)) f.Bus.Publish(e.ClientId, e.Type, e.Data);
        while (f.Bus.Reader.TryRead(out var ev)) f.Dispatcher.FanOut(ev);
        await f.Dispatcher.TickAndWait();
        var call = Assert.Single(f.Receiver.Calls);
        Assert.Equal("screen.offline", call.Headers["Linkivi-Event"]);
        Assert.True(WebhookSignature.Verify(call.Headers["Linkivi-Signature"], call.Body, secret, f.Time.Now, TimeSpan.FromMinutes(5)));
        using var doc = JsonDocument.Parse(call.Body);
        Assert.Equal("Salle d'attente", doc.RootElement.GetProperty("data").GetProperty("screen").GetProperty("name").GetString());
    }

    [Fact]
    public void The_module_adds_its_scope_events_routes_and_joins_the_read_only_template()
    {
        Assert.Contains(f.Registry.Scopes, s => s.Id == "screens:read");
        Assert.Contains(f.Registry.Events, e => e.Id == "screen.offline");
        Assert.Contains("screens:read", f.Registry.Templates.First(t => t.Name == "Lecture seule").Scopes);
        Assert.True(f.Registry.IsValidSubscription("screen.*"));
    }
}

[Collection("platform")]
public class ScreensApiHttpTests(Platform p)
{
    private (Tenant T, string Key, Screen S) Org(string[]? scopes = null)
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        var t = new Tenant { Name = "Org " + Guid.NewGuid().ToString("N")[..6], Plan = Plans.Growth, ResellerId = p.R1.Id };
        var on = new Screen { ClientId = t.Id, Name = "Accueil", Token = Guid.NewGuid().ToString("N"), LastSeenUtc = DateTime.UtcNow.AddSeconds(-5), Params = new() { new() { Name = "Étage", Value = "2" } } };
        var off = new Screen { ClientId = t.Id, Name = "Réfectoire", Token = Guid.NewGuid().ToString("N"), LastSeenUtc = DateTime.UtcNow.AddHours(-3) };
        store.Write(db => { db.Clients.Add(t); db.Screens.Add(on); db.Screens.Add(off); db.Screens.Add(new Screen { ClientId = t.Id, Name = "Non appairé" }); });
        var c = p.Services.GetRequiredService<ApiKeyService>().Create(t.Id, null, "Supervision", "Lecture seule", scopes ?? new[] { "screens:read" }, null, null);
        Assert.Null(c.Error);
        return (t, c.Secret!, on);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> Get(HttpClient c, string url, string key)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        r.Headers.Add("Authorization", "Bearer " + key);
        var res = await c.SendAsync(r);
        return (res.StatusCode, JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    [Fact]
    public async Task The_list_shows_paired_screens_with_their_state_and_parameters()
    {
        var (_, key, on) = Org();
        var r = await Get(p.Client(), "/api/v1/screens", key);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        var data = r.Json.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(new[] { "Accueil", "Réfectoire" }, data.Select(d => d.GetProperty("name").GetString()));   // le non appairé n'y est pas
        Assert.True(data[0].GetProperty("online").GetBoolean());   // « updating » tant que la dernière publication n'est pas affichée
        Assert.Equal("offline", data[1].GetProperty("status").GetString());
        Assert.Equal("Étage", data[0].GetProperty("params")[0].GetProperty("name").GetString());
        Assert.Equal(on.Id, data[0].GetProperty("id").GetGuid());
        Assert.DoesNotContain(on.Token!, r.Json.GetRawText());
    }

    [Fact]
    public async Task The_list_filters_by_status_and_pages_with_a_cursor()
    {
        var (_, key, _) = Org();
        var c = p.Client();
        var off = await Get(c, "/api/v1/screens?status=offline", key);
        Assert.Equal("Réfectoire", Assert.Single(off.Json.GetProperty("data").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Get(c, "/api/v1/screens?status=nimporte", key)).Status);

        var first = await Get(c, "/api/v1/screens?limit=1", key);
        Assert.Equal("Accueil", Assert.Single(first.Json.GetProperty("data").EnumerateArray()).GetProperty("name").GetString());
        var next = first.Json.GetProperty("next").GetString();
        var second = await Get(c, "/api/v1/screens?limit=1&cursor=" + next, key);
        Assert.Equal("Réfectoire", Assert.Single(second.Json.GetProperty("data").EnumerateArray()).GetProperty("name").GetString());
        Assert.False(second.Json.TryGetProperty("next", out _));
    }

    [Fact]
    public async Task An_organisation_only_sees_its_own_screens_and_needs_the_screens_scope()
    {
        var (_, ka, a) = Org(); var (_, kb, b) = Org();
        var c = p.Client();
        Assert.Equal(HttpStatusCode.OK, (await Get(c, $"/api/v1/screens/{a.Id}", ka)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(c, $"/api/v1/screens/{a.Id}", kb)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(c, $"/api/v1/screens/{b.Id}", ka)).Status);
        var (_, noScope, _) = Org(new[] { "account:read" });
        var denied = await Get(c, "/api/v1/screens", noScope);
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("scope_missing", denied.Json.GetProperty("code").GetString());
    }
}

public sealed class RecordingEvents : IEventPublisher
{
    public readonly List<(Guid Client, string Type, string Json)> Items = new();
    public void Publish(Guid clientId, string type, object? data = null) { lock (Items) Items.Add((clientId, type, JsonSerializer.Serialize(data))); }
}

/// <summary>Événements de publication (screen.published, screen.applied) et de panne de données (data.failed, data.recovered).</summary>
public class DataAndPublishEventsTests : IDisposable
{
    private readonly ApiFixture f = new(new IApiModule[] { new ScreensApiModule(), new DataApiModule() });
    public void Dispose() => f.Dispose();

    [Fact]
    public void The_modules_declare_their_events_and_scopes()
    {
        foreach (var e in new[] { "screen.published", "screen.applied", "data.failed", "data.recovered" }) Assert.True(f.Registry.IsKnownEvent(e), e);
        Assert.Contains("data:write", f.Registry.Templates.First(t => t.Name == "Envoi de données").Scopes);
        Assert.Contains("data:read", f.Registry.Templates.First(t => t.Name == "Lecture seule").Scopes);
    }

    private (DataSetService Svc, RecordingEvents Rec, DataSet Ds) Failing()
    {
        var rec = new RecordingEvents();
        var svc = new DataSetService(f.Store, new SafeHttp(), new SecretBox(new EphemeralDataProtectionProvider()), new RelayBroker(f.Store), rec);
        var conn = new DataConnection { Kind = DataKinds.File, FileMode = "upload", FileName = "", Name = "Fichier" };   // aucun fichier : la lecture échoue
        var ds = new DataSet { ConnectionId = conn.Id, Name = "Salles" };
        f.Store.Write(_ => { f.Tenant.DataConnections.Add(conn); f.Tenant.DataSets.Add(ds); });
        return (svc, rec, ds);
    }

    [Fact]
    public async Task A_failing_source_is_signalled_once_not_at_every_reading()
    {
        var (svc, rec, ds) = Failing();
        await svc.Refresh(f.Tenant.Id, ds.Id);
        await svc.Refresh(f.Tenant.Id, ds.Id);
        await svc.Refresh(f.Tenant.Id, ds.Id);
        var failed = Assert.Single(rec.Items);
        Assert.Equal("data.failed", failed.Type);
        Assert.Contains("Salles", failed.Json);
        svc.Forget(f.Tenant.Id, ds.Id);
    }

    [Fact]
    public void Direct_send_datasets_are_never_read_and_never_due()
    {
        var (svc, _, _) = Failing();
        var conn = new DataConnection { Kind = DataKinds.Push, Name = "Envoi" };
        Assert.False(svc.Due(f.Tenant.Id, conn, new DataSet(), DateTime.UtcNow));
        Assert.Null(svc.Refresh(f.Tenant.Id, Guid.NewGuid()).Result);
        Assert.Null(DataSourceOps.CheckConnection(conn));
    }

    [Fact]
    public void Pushed_rows_replace_append_and_upsert_by_key()
    {
        var rec = new RecordingEvents();
        var svc = new DataSetService(f.Store, new SafeHttp(), new SecretBox(new EphemeralDataProtectionProvider()), new RelayBroker(f.Store), rec);
        var conn = new DataConnection { Kind = DataKinds.Push, Name = "Envoi direct" };
        var ds = new DataSet { ConnectionId = conn.Id, Name = "Occupation" };
        f.Store.Write(_ => { f.Tenant.DataConnections.Add(conn); f.Tenant.DataSets.Add(ds); });
        var cid = f.Tenant.Id;
        static RawTable Raw(params string[][] rows) => new(new() { "Salle", "Occupée", "Personnes" }, rows.Select(r => r.Select(x => (string?)x).ToList()).ToList());

        var first = svc.Push(cid, ds.Id, Raw(new[] { "A1", "oui", "3" }, new[] { "A2", "non", "0" }), DataSetService.PushMode.Replace);
        Assert.True(first.Ok, first.Error);
        Assert.Equal(2, first.Rows);
        var t = svc.Table(cid, ds.Id)!;
        Assert.Equal(new[] { "salle", "occupee", "personnes" }, t.Columns.Select(c => c.Key));
        Assert.Equal("number", t.Columns[2].Type);
        Assert.Equal("bool", t.Columns[1].Type);

        var add = svc.Push(cid, ds.Id, Raw(new[] { "A3", "non", "1" }), DataSetService.PushMode.Append);
        Assert.Equal((3, 1, 0), (add.Rows, add.Added, add.Updated));

        var up = svc.Push(cid, ds.Id, new RawTable(new() { "Salle", "Personnes" }, new() { new() { "A1", "9" }, new() { "A4", "2" } }), DataSetService.PushMode.Upsert, "Salle");
        Assert.True(up.Ok, up.Error);
        Assert.Equal((4, 1, 1), (up.Rows, up.Added, up.Updated));
        t = svc.Table(cid, ds.Id)!;
        var a1 = t.Rows.First(r => r[0] == "A1");
        Assert.Equal("9", a1[2]);
        Assert.Equal("true", a1[1]);   // la colonne absente de l'envoi garde sa valeur

        Assert.Contains("Colonne clé inconnue", svc.Push(cid, ds.Id, Raw(new[] { "A5", "non", "1" }), DataSetService.PushMode.Upsert, "inconnue").Error);
        Assert.Contains("absente", svc.Push(cid, ds.Id, new RawTable(new() { "Salle" }, new() { new() { "B1" } }), DataSetService.PushMode.Replace).Error);
        Assert.Equal(4, svc.Table(cid, ds.Id)!.Rows.Count);   // un envoi refusé ne change rien

        Assert.Equal(1, svc.RemoveRows(cid, ds.Id, "Salle", "A2").Removed);
        Assert.Equal(3, svc.RemoveRows(cid, ds.Id, null, null).Removed);
        Assert.Empty(svc.Table(cid, ds.Id)!.Rows);
        Assert.Contains("Envoi par l'API", svc.History(cid, ds.Id)[0].Message);
        svc.Forget(cid, ds.Id);
    }

    [Fact]
    public void The_row_limit_is_never_exceeded_silently()
    {
        var svc = new DataSetService(f.Store, new SafeHttp(), new SecretBox(new EphemeralDataProtectionProvider()), new RelayBroker(f.Store));
        var conn = new DataConnection { Kind = DataKinds.Push, Name = "Envoi" };
        var ds = new DataSet { ConnectionId = conn.Id, Name = "Petit", MaxRows = 3 };
        f.Store.Write(_ => { f.Tenant.DataConnections.Add(conn); f.Tenant.DataSets.Add(ds); });
        RawTable Rows(int n) => new(new() { "n" }, Enumerable.Range(1, n).Select(i => new List<string?> { i.ToString() }).ToList());
        Assert.True(svc.Push(f.Tenant.Id, ds.Id, Rows(2), DataSetService.PushMode.Replace).Ok);
        Assert.Contains("Limite de 3 lignes", svc.Push(f.Tenant.Id, ds.Id, Rows(2), DataSetService.PushMode.Append).Error);
        svc.Forget(f.Tenant.Id, ds.Id);
    }

    [Fact]
    public async Task Publishing_a_screen_emits_screen_published_through_the_notifier()
    {
        // même contrat que l'événement émis par Notifier.PublishScreen : charge utile avec l'écran et la version
        var s = new Screen { ClientId = f.Tenant.Id, Name = "Hall", Token = "t" };
        f.Store.Write(db => db.Screens.Add(s));
        f.Hook(events: "screen.*");
        f.Bus.Publish(f.Tenant.Id, ScreenEvents.Published, new { screen = f.Store.Read(db => ScreenEvents.Brief(db, s)), version = 3, publishedAt = DateTime.UtcNow });
        while (f.Bus.Reader.TryRead(out var ev)) Assert.Equal(1, f.Dispatcher.FanOut(ev));
        await f.Dispatcher.TickAndWait();
        Assert.Contains("\"version\":3", Assert.Single(f.Receiver.Calls).Body);
    }
}

[Collection("platform")]
public class DataApiHttpTests(Platform p)
{
    private (Tenant T, string Key) Org(string template = "Envoi de données", string[]? scopes = null)
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        var t = new Tenant { Name = "Org " + Guid.NewGuid().ToString("N")[..6], Plan = Plans.Growth, ResellerId = p.R1.Id };
        store.Write(db => db.Clients.Add(t));
        var c = p.Services.GetRequiredService<ApiKeyService>().Create(t.Id, null, "ERP", template, scopes ?? new[] { "data:read", "data:write" }, null, null);
        Assert.Null(c.Error);
        return (t, c.Secret!);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> Call(HttpClient c, HttpMethod m, string url, string key, string? body = null, string type = "application/json")
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("Authorization", "Bearer " + key);
        if (body != null) r.Content = new StringContent(body, System.Text.Encoding.UTF8, type);
        var res = await c.SendAsync(r);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, text.Length > 0 && text[0] == '{' ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private static async Task<string> Dataset(HttpClient c, string key, string name = "Occupation")
    {
        var r = await Call(c, HttpMethod.Post, "/api/v1/datasets", key, JsonSerializer.Serialize(new { name, columns = new object[] { new { name = "Salle" }, new { name = "Étage", type = "number" } } }));
        Assert.Equal(HttpStatusCode.Created, r.Status);
        return r.Json.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task A_direct_send_dataset_is_created_filled_read_and_emptied_through_the_api()
    {
        var (_, key) = Org();
        var c = p.Client();
        var created = await Call(c, HttpMethod.Post, "/api/v1/datasets", key, "{\"name\":\"Occupation\",\"columns\":[{\"name\":\"Salle\"},{\"name\":\"Étage\",\"type\":\"number\"}]}");
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("push", created.Json.GetProperty("source").GetString());
        Assert.True(created.Json.GetProperty("writable").GetBoolean());
        Assert.Equal(new[] { "salle", "etage" }, created.Json.GetProperty("columns").EnumerateArray().Select(x => x.GetProperty("key").GetString()));
        var id = created.Json.GetProperty("id").GetString();

        var put = await Call(c, HttpMethod.Put, $"/api/v1/datasets/{id}/rows", key, "{\"rows\":[{\"Salle\":\"A1\",\"Étage\":1},{\"Salle\":\"A2\",\"Étage\":2}]}");
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Equal(2, put.Json.GetProperty("rows").GetInt32());

        var rows = await Call(c, HttpMethod.Get, $"/api/v1/datasets/{id}/rows", key);
        var first = rows.Json.GetProperty("data")[0];
        Assert.Equal("A1", first.GetProperty("salle").GetString());
        Assert.Equal(JsonValueKind.Number, first.GetProperty("etage").ValueKind);   // typé, pas en texte
        Assert.Equal(2, rows.Json.GetProperty("total").GetInt32());

        var csv = await Call(c, HttpMethod.Put, $"/api/v1/datasets/{id}/rows", key, "Salle;Étage\nB1;3\nB2;4\nB3;5", "text/csv");
        Assert.Equal(3, csv.Json.GetProperty("rows").GetInt32());

        var up = await Call(c, HttpMethod.Post, $"/api/v1/datasets/{id}/rows?key=Salle", key, "[{\"Salle\":\"B1\",\"Étage\":9},{\"Salle\":\"C1\",\"Étage\":1}]");
        Assert.Equal((1, 1), (up.Json.GetProperty("added").GetInt32(), up.Json.GetProperty("updated").GetInt32()));

        var del = await Call(c, HttpMethod.Delete, $"/api/v1/datasets/{id}/rows?key=Salle&value=B2", key);
        Assert.Equal(1, del.Json.GetProperty("removed").GetInt32());
        var clear = await Call(c, HttpMethod.Delete, $"/api/v1/datasets/{id}/rows", key);
        Assert.Equal(3, clear.Json.GetProperty("removed").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await Call(c, HttpMethod.Delete, $"/api/v1/datasets/{id}", key)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(c, HttpMethod.Get, $"/api/v1/datasets/{id}", key)).Status);
    }

    [Fact]
    public async Task Bad_bodies_names_and_columns_are_refused_with_a_clear_error()
    {
        var (_, key) = Org();
        var c = p.Client();
        var id = await Dataset(c, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(c, HttpMethod.Put, $"/api/v1/datasets/{id}/rows", key, "", "application/json")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(c, HttpMethod.Put, $"/api/v1/datasets/{id}/rows", key, "<html>pas des données</html>", "text/html")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(c, HttpMethod.Post, "/api/v1/datasets", key, "{\"name\":\"Occupation\"}")).Status);   // nom déjà pris
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(c, HttpMethod.Post, "/api/v1/datasets", key, "{\"name\":\"X\",\"columns\":[{\"name\":\"a\",\"type\":\"inconnu\"}]}")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(c, HttpMethod.Delete, $"/api/v1/datasets/{id}/rows?key=Salle", key)).Status);   // key sans value
    }

    [Fact]
    public async Task Datasets_are_isolated_and_writes_need_the_write_scope()
    {
        var (_, ka) = Org(); var (_, kb) = Org();
        var c = p.Client();
        var id = await Dataset(c, ka);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(c, HttpMethod.Get, $"/api/v1/datasets/{id}", kb)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(c, HttpMethod.Put, $"/api/v1/datasets/{id}/rows", kb, "[{\"a\":1}]")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(c, HttpMethod.Delete, $"/api/v1/datasets/{id}", kb)).Status);
        Assert.Empty((await Call(c, HttpMethod.Get, "/api/v1/datasets", kb)).Json.GetProperty("data").EnumerateArray());

        var (_, readOnly) = Org("Lecture seule", new[] { "data:read" });
        var denied = await Call(c, HttpMethod.Put, "/api/v1/datasets/" + Guid.NewGuid() + "/rows", readOnly, "[{\"a\":1}]");
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("scope_missing", denied.Json.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Call(c, HttpMethod.Get, "/api/v1/datasets", readOnly)).Status);
    }

    [Fact]
    public async Task Datasets_read_from_a_source_are_listed_but_cannot_be_written_and_a_used_one_cannot_be_deleted()
    {
        var (t, key) = Org();
        var c = p.Client();
        var store = p.Services.GetRequiredService<JsonStore>();
        var api = new DataConnection { Kind = DataKinds.Api, Name = "API", BaseUrl = "https://exemple.ch" };
        var ext = new DataSet { ConnectionId = api.Id, Name = "Lu depuis une API" };
        store.Write(_ => { t.DataConnections.Add(api); t.DataSets.Add(ext); });
        var list = await Call(c, HttpMethod.Get, "/api/v1/datasets", key);
        var item = Assert.Single(list.Json.GetProperty("data").EnumerateArray());
        Assert.False(item.GetProperty("writable").GetBoolean());
        Assert.Equal("api", item.GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(c, HttpMethod.Put, $"/api/v1/datasets/{ext.Id}/rows", key, "[{\"a\":1}]")).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(c, HttpMethod.Delete, $"/api/v1/datasets/{ext.Id}", key)).Status);

        var id = await Dataset(c, key, "Utilisé");
        store.Write(db => db.Media.Add(new MediaItem { ClientId = t.Id, Name = "Compo", Type = "app", AppId = Compositions.AppId, Settings = new() { ["doc"] = "{\"ds\":\"" + Guid.Parse(id).ToString("N") + "\"}" } }));
        var used = await Call(c, HttpMethod.Delete, $"/api/v1/datasets/{id}", key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, used.Status);
        Assert.Contains("utilisé", used.Json.GetProperty("detail").GetString());
    }
}

/// <summary>Descriptif OpenAPI enrichi et page « Essayer » (Swagger UI).</summary>
[Collection("platform")]
public class SwaggerTests(Platform p)
{
    [Fact]
    public async Task The_openapi_describes_parameters_bodies_tags_and_errors()
    {
        using var doc = JsonDocument.Parse(await p.Client().GetStringAsync("/api/v1/openapi.json"));
        var paths = doc.RootElement.GetProperty("paths");
        var list = paths.GetProperty("/screens").GetProperty("get");
        Assert.Contains(list.GetProperty("parameters").EnumerateArray(), x => x.GetProperty("name").GetString() == "status" && x.GetProperty("in").GetString() == "query");
        Assert.Equal("Écrans", list.GetProperty("tags")[0].GetString());

        var one = paths.GetProperty("/screens/{id}").GetProperty("get");
        Assert.Contains(one.GetProperty("parameters").EnumerateArray(), x => x.GetProperty("name").GetString() == "id" && x.GetProperty("in").GetString() == "path" && x.GetProperty("required").GetBoolean());

        var create = paths.GetProperty("/datasets").GetProperty("post");
        Assert.Equal("Occupation des salles", create.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("example").GetProperty("name").GetString());
        Assert.Contains(create.GetProperty("parameters").EnumerateArray(), x => x.GetProperty("name").GetString() == "Idempotency-Key" && x.GetProperty("in").GetString() == "header");
        Assert.True(create.GetProperty("responses").TryGetProperty("422", out _));
        Assert.True(doc.RootElement.GetProperty("components").GetProperty("schemas").TryGetProperty("Problem", out _));
        Assert.Equal("bearer", doc.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("bearerAuth").GetProperty("scheme").GetString());
    }

    [Fact]
    public async Task The_try_page_is_public_branded_and_points_at_this_servers_openapi()
    {
        var html = await p.Client().GetStringAsync("/docs/api/try");
        Assert.Contains("SwaggerUIBundle", html);
        Assert.Contains("/api/v1/openapi.json", html);
        Assert.Contains("swagger-ui-dist@" + ApiDocs.SwaggerVersion, html);
        var acme = await p.Client("affichage.acme.ch").GetStringAsync("/docs/api/try");
        Assert.Contains("Acme Affichage", acme);
        Assert.Contains("#ff0000", acme);
        Assert.Contains("/docs/api/try", await p.Client().GetStringAsync("/docs/api"));   // lien depuis la documentation
    }
}
