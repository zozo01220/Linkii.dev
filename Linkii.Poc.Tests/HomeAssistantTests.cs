extern alias relay;
using System.Net.Http;
using System.Text.Json;
using Linkii.Poc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RelayWorker = relay::Linkii.Relay.RelayWorker;

namespace Linkii.Poc.Tests;

/// <summary>Home Assistant : règles (domaines, commandes, adresses, jeux de données), widgets du catalogue, et client face à une fausse instance
/// (connexion par jeton ou mot de passe, renouvellement sans nouvelle session, révocation, commandes, historique).</summary>
public class HomeAssistantTests
{
    readonly AppCatalog catalog = new(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Linkii.Poc", "Apps"), NullLogger.Instance);

    // ---------- plan du bâtiment (§193) ----------

    [Fact]
    public void Plan_app_is_a_home_assistant_widget_outside_the_families()
    {
        Assert.True(HomeAssistantRules.IsHaApp("ha-plan"));
        Assert.DoesNotContain(HomeAssistantRules.Families, f => f.App == "ha-plan");
        Assert.Contains("ha-plan", HomeAssistantRules.AllApps);
        var m = catalog.Find("ha-plan");
        Assert.NotNull(m);
        Assert.True(m!.ScreenWidget);
        Assert.Equal("homeassistant", m.Provider);
    }

    [Theory]
    [InlineData("light.bureau", "on", true)]
    [InlineData("cover.store_sud", "position", true)]
    [InlineData("climate.salon", "temp", true)]
    [InlineData("lock.entree", "unlock", true)]
    [InlineData("sensor.salon_temperature", "on", false)]
    [InlineData("binary_sensor.porte", "on", false)]
    [InlineData("light.bureau", "temp", false)]
    [InlineData("camera.jardin", "on", false)]
    public void Plan_widget_commands_any_domain_but_only_what_the_domain_accepts(string entity, string action, bool allowed) =>
        Assert.Equal(allowed, HomeAssistantRules.Commandable("ha-plan", entity, action));

    [Fact]
    public void Plan_is_cleaned_when_read()
    {
        var json = """
            {"floors":[{"id":"rdc","name":"  Rez ","img":"/media/a.png","ratio":1.5},{"id":"rdc","name":"Doublon"},
                       {"id":"e1","name":"Étage","img":"https://evil.example/x.png","ratio":99},{"id":"","name":"Sans id"}],
             "devices":[{"id":"light.salon","floor":"rdc","x":0.25,"y":1.7},{"id":"light.salon","floor":"e1","x":0.1,"y":0.1},
                        {"id":"camera.jardin","floor":"rdc","x":0.5,"y":0.5},{"id":"switch.tv","floor":"inconnu","x":0.5,"y":0.5},
                        {"id":"cover.volet","floor":"e1","x":-3,"y":0.4}]}
            """;
        var p = HomeAssistantRules.ParsePlan(json);
        Assert.Equal(new[] { "rdc", "e1" }, p.Floors.Select(f => f.Id));
        Assert.Equal("/media/a.png", p.Floors[0].Img);
        Assert.Null(p.Floors[1].Img);                 // seule une image /media/ du site est gardée
        Assert.Equal(3, p.Floors[1].Ratio);           // rapport ramené dans les bornes
        Assert.Equal(new[] { "light.salon", "cover.volet" }, p.Devices.Select(d => d.Id));   // doublon, domaine inconnu et étage inconnu écartés
        Assert.Equal(1, p.Devices[0].Y);              // position ramenée dans le plan
        Assert.Equal(0, p.Devices[1].X);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pas du json")]
    [InlineData("[1,2]")]
    public void Unreadable_plan_is_empty(string? json)
    {
        var p = HomeAssistantRules.ParsePlan(json);
        Assert.Empty(p.Floors);
        Assert.Empty(p.Devices);
        Assert.Equal(("", ""), HomeAssistantRules.NormalizePlan(json));
    }

    [Fact]
    public void Plan_is_limited_in_floors_and_devices()
    {
        var floors = string.Join(',', Enumerable.Range(0, 12).Select(i => $"{{\"id\":\"f{i}\",\"name\":\"E{i}\"}}"));
        var devices = string.Join(',', Enumerable.Range(0, 200).Select(i => $"{{\"id\":\"light.l{i}\",\"floor\":\"f0\",\"x\":0.5,\"y\":0.5}}"));
        var p = HomeAssistantRules.ParsePlan($"{{\"floors\":[{floors}],\"devices\":[{devices}]}}");
        Assert.Equal(HomeAssistantRules.MaxPlanFloors, p.Floors.Count);
        Assert.Equal(HomeAssistantRules.MaxPlanDevices, p.Devices.Count);
    }

    [Fact]
    public void Normalized_plan_gives_the_entities_it_uses_and_validates()
    {
        var (json, ents) = HomeAssistantRules.NormalizePlan("""{"floors":[{"id":"a","name":"RDC"}],"devices":[{"id":"light.salon","floor":"a","x":0.2,"y":0.3},{"id":"lock.porte","floor":"a","x":0.6,"y":0.3}]}""");
        Assert.Equal("light.salon,lock.porte", ents);
        Assert.Empty(AppCatalog.Validate(catalog.Find("ha-plan")!, new Dictionary<string, string> { ["plan"] = json, ["entities"] = ents }, new HashSet<string>()));
        Assert.NotEmpty(AppCatalog.Validate(catalog.Find("ha-plan")!, new Dictionary<string, string> { ["plan"] = "{\"floors\":[]}" }, new HashSet<string>()));
    }

    // ---------- règles ----------

    [Fact]
    public void Entity_list_keeps_valid_ids_once_and_drops_bad_ones()
    {
        var raw = "sensor.salon_temperature, switch.ventilateur;light.bureau\nsensor.salon_temperature  Bad Entry";
        Assert.Equal(new[] { "sensor.salon_temperature", "switch.ventilateur", "light.bureau" }, HomeAssistantRules.ParseEntities(raw));
    }

    [Theory]
    [InlineData("sensor.salon_temperature", true)]
    [InlineData("binary_sensor.porte_entree", true)]
    [InlineData("switch.ventilateur", true)]
    [InlineData("input_boolean.vacances", true)]
    [InlineData("fan.plafond", true)]
    [InlineData("light.bureau", true)]
    [InlineData("climate.salon", true)]
    [InlineData("cover.store_sud", true)]
    [InlineData("scene.soiree", true)]
    [InlineData("script.depart", true)]
    [InlineData("lock.entree", true)]
    [InlineData("camera.jardin", false)]
    [InlineData("pas une entité", false)]
    public void Supported_domains(string entity, bool expected) =>
        Assert.Equal(expected, HomeAssistantRules.IsSupported(entity));

    [Theory]
    [InlineData("ha-switches", "switch.ventilateur", "on", true)]
    [InlineData("ha-switches", "input_boolean.vacances", "off", true)]
    [InlineData("ha-switches", "fan.plafond", "on", true)]
    [InlineData("ha-lights", "light.bureau", "brightness", true)]
    [InlineData("ha-lights", "light.bureau", "color", true)]
    [InlineData("ha-climate", "climate.salon", "temp", true)]
    [InlineData("ha-covers", "cover.store_sud", "position", true)]
    [InlineData("ha-scenes", "scene.soiree", "activate", true)]
    [InlineData("ha-scenes", "script.depart", "activate", true)]
    [InlineData("ha-locks", "lock.entree", "unlock", true)]
    [InlineData("ha-switches", "light.bureau", "on", false)]      // un widget d'interrupteurs ne commande pas une lumière
    [InlineData("ha-lights", "light.bureau", "temp", false)]      // commande sans rapport avec le domaine
    [InlineData("ha-switches", "lock.entree", "unlock", false)]
    [InlineData("ha-sensors", "sensor.salon_temperature", "on", false)]   // les capteurs se lisent, ne se commandent pas
    public void A_widget_only_commands_its_own_domains(string app, string entity, string action, bool expected) =>
        Assert.Equal(expected, HomeAssistantRules.Commandable(app, entity, action));

    [Fact]
    public void No_control_when_the_option_is_off() =>
        Assert.Empty(HomeAssistantRules.ControlsFor("light.bureau", control: false));

    [Theory]
    [InlineData("light.bureau", "brightness", 40.4, null, "light", "turn_on", "\"brightness_pct\":40")]
    [InlineData("light.bureau", "brightness", 0.0, null, "light", "turn_off", "\"entity_id\":\"light.bureau\"")]
    [InlineData("light.bureau", "color", null, "#FF8000", "light", "turn_on", "\"rgb_color\":[255,128,0]")]
    [InlineData("climate.salon", "temp", 21.3, null, "climate", "set_temperature", "\"temperature\":21.5")]
    [InlineData("climate.salon", "temp", 80.0, null, "climate", "set_temperature", "\"temperature\":35")]
    [InlineData("cover.store_sud", "position", 33.6, null, "cover", "set_cover_position", "\"position\":34")]
    [InlineData("cover.store_sud", "stop", null, null, "cover", "stop_cover", "")]
    [InlineData("scene.soiree", "activate", null, null, "scene", "turn_on", "")]
    [InlineData("script.depart", "activate", null, null, "script", "turn_on", "")]
    [InlineData("lock.entree", "unlock", null, null, "lock", "unlock", "")]
    [InlineData("input_boolean.vacances", "on", null, null, "input_boolean", "turn_on", "")]
    public void Commands_become_home_assistant_services(string entity, string action, double? value, string? color, string domain, string service, string inBody)
    {
        var call = HomeAssistantRules.ServiceCall(entity, action, value, color);
        Assert.NotNull(call);
        Assert.Equal(domain, call!.Value.Domain);
        Assert.Equal(service, call.Value.Service);
        Assert.Contains($"\"entity_id\":\"{entity}\"", call.Value.Body);
        Assert.Contains(inBody, call.Value.Body);
    }

    [Theory]
    [InlineData("light.bureau", "color", null, "rouge")]
    [InlineData("light.bureau", "brightness", null, null)]
    [InlineData("sensor.salon", "on", null, null)]
    [InlineData("switch.ventilateur", "temp", 20.0, null)]
    public void Meaningless_commands_are_refused(string entity, string action, double? value, string? color) =>
        Assert.Null(HomeAssistantRules.ServiceCall(entity, action, value, color));

    [Fact]
    public void Old_screens_still_send_on_and_off() =>
        Assert.Equal("on", HomeAssistantRules.ActionOf(new HaCommandDto("switch.ventilateur", On: true)));

    [Theory]
    [InlineData("https://maison.exemple.ch", "direct", true)]
    [InlineData("http://maison.exemple.ch", "direct", false)]     // en direct, https seulement
    [InlineData("http://homeassistant.local:8123", "relay", true)]   // par le relais : adresse du réseau local, http accepté
    [InlineData("http://192.168.1.20:8123/", "relay", true)]
    [InlineData("ftp://maison.exemple.ch", "relay", false)]
    [InlineData("maison", "direct", false)]
    public void Address_depends_on_access(string url, string via, bool ok) =>
        Assert.Equal(ok, HomeAssistantRules.CheckUrl(url, via) == null);

    [Theory]
    [InlineData("/auth/login_flow", true)]
    [InlineData("/auth/login_flow/abc123", true)]
    [InlineData("/auth/token", true)]
    [InlineData("/auth/revoke", true)]
    [InlineData("/api/services/light/turn_on", true)]
    [InlineData("/api/config/core/check_config", false)]
    [InlineData("/api/states/sensor.x", false)]
    public void Relay_only_posts_connection_and_services(string path, bool ok) =>
        Assert.Equal(ok, RelayWorker.HaPostAllowed(path));

    // ---------- jeux de données ----------

    [Fact]
    public void Data_path_round_trip()
    {
        var s = HomeAssistantRules.ParseDataPath(HomeAssistantRules.DataPathOf("states", new[] { "sensor.a", "light.b" }));
        Assert.Equal(("states", false), (s.Mode, s.All));
        Assert.Equal(new[] { "sensor.a", "light.b" }, s.Ids);
        Assert.True(HomeAssistantRules.ParseDataPath("states:*").All);
        var h = HomeAssistantRules.ParseDataPath(HomeAssistantRules.DataPathOf("history", new[] { "sensor.a" }, 168));
        Assert.Equal(("history", 168), (h.Mode, h.Hours));
        Assert.Equal(new[] { "sensor.a" }, h.Ids);
    }

    [Fact]
    public void History_is_averaged_to_240_points_at_most()
    {
        var from = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddHours(24);
        var points = Enumerable.Range(0, 24 * 60).Select(i => (from.AddMinutes(i), (double)(i % 10))).ToList();   // une valeur par minute
        var t = HomeAssistantRules.HistoryTable(new Dictionary<string, List<(DateTime, double)>> { ["sensor.t"] = points }, new Dictionary<string, string> { ["sensor.t"] = "Salon" }, from, to);
        Assert.Equal(new[] { "heure", "entite", "nom", "valeur" }, t.Headers);
        Assert.InRange(t.Rows.Count, 200, 241);
        Assert.All(t.Rows, r => Assert.Equal("Salon", r[2]));
    }

    [Fact]
    public void History_minimal_response_is_read()
    {
        const string body = """
            [[{"entity_id":"sensor.t","state":"20.5","last_changed":"2026-11-01T10:00:00+00:00","attributes":{}},
              {"state":"unavailable","last_changed":"2026-11-01T10:05:00+00:00"},
              {"state":"21","last_changed":"2026-11-01T10:10:00+00:00"}],
             [{"entity_id":"light.b","state":"on","last_changed":"2026-11-01T09:00:00+00:00"},{"state":"off","last_changed":"2026-11-01T09:30:00+00:00"}]]
            """;
        var h = HomeAssistantClient.ParseHistory(body);
        Assert.Equal(new[] { 20.5, 21 }, h["sensor.t"].Select(p => p.Value));   // « unavailable » ignoré
        Assert.Equal(new[] { 1.0, 0.0 }, h["light.b"].Select(p => p.Value));    // allumé / éteint → 1 / 0
    }

    [Fact]
    public void States_table_gives_numbers_and_on_off()
    {
        var states = HomeAssistantClient.ParseStates(StatesJson);
        var t = HomeAssistantRules.StatesTable(states.Values.OrderBy(s => s.Id));
        var temp = t.Rows.Single(r => r[0] == "sensor.salon_temperature");
        Assert.Equal("21.37", temp[4]);
        Assert.Equal("°C", temp[5]);
        Assert.Equal("true", t.Rows.Single(r => r[0] == "light.bureau")[6]);
    }

    // ---------- catalogue ----------

    [Fact]
    public void Seven_widgets_read_the_organisation_account()
    {
        Assert.Equal(7, HomeAssistantRules.AppIds.Length);
        foreach (var id in HomeAssistantRules.AppIds)
        {
            var app = catalog.Find(id);
            Assert.NotNull(app);
            Assert.Equal("homeassistant", app!.Provider);
            Assert.True(app.Data);
            Assert.True(app.ScreenWidget);
            Assert.Equal("Maison connectée", app.Category);
            Assert.Empty(AppCatalog.Check(app));
        }
    }

    [Fact]
    public void Each_widget_chooses_entities_of_its_family()
    {
        foreach (var f in HomeAssistantRules.Families)
        {
            var field = catalog.Find(f.App)!.Settings.Single(x => x.Key == "entities");
            Assert.Equal("haentity", field.Type);
            Assert.Equal(f.Domains, field.Domains);
        }
    }

    [Fact]
    public void Commanding_widgets_offer_the_screen_control_option()
    {
        foreach (var f in HomeAssistantRules.Families.Where(f => f.App != "ha-sensors"))
            Assert.Contains(catalog.Find(f.App)!.Settings, x => x.Key == "control" && x.Type == "bool" && x.Default == "true");
    }

    [Fact]
    public void An_entity_field_without_a_valid_domain_is_rejected()
    {
        var bad = new AppManifest
        {
            Id = "ha-test", Name = "Test", Placements = { "full" }, Provider = "homeassistant", Data = true,
            Settings = { new AppField { Key = "entities", Type = "haentity", Domains = { "camera" } } },
        };
        Assert.NotEmpty(AppCatalog.Check(bad));
    }

    // ---------- ce que l'écran reçoit ----------

    [Fact]
    public void Screen_entity_describes_light_and_cover()
    {
        var states = HomeAssistantClient.ParseStates(StatesJson);
        var light = JsonSerializer.SerializeToElement(HomeAssistantProvider.Entity("light.bureau", states["light.bureau"], control: true));
        Assert.Equal(40, light.GetProperty("brightness").GetInt32());   // 102 / 255
        Assert.True(light.GetProperty("dimmable").GetBoolean());
        Assert.True(light.GetProperty("colorable").GetBoolean());
        Assert.Equal("#FF8000", light.GetProperty("color").GetString());
        var cover = JsonSerializer.SerializeToElement(HomeAssistantProvider.Entity("cover.store_sud", states["cover.store_sud"], control: true));
        Assert.True(cover.GetProperty("positionable").GetBoolean());
        Assert.Equal(60, cover.GetProperty("position").GetInt32());
        var off = JsonSerializer.SerializeToElement(HomeAssistantProvider.Entity("light.bureau", states["light.bureau"], control: false));
        Assert.Equal(0, off.GetProperty("ctrl").GetArrayLength());
        var gone = JsonSerializer.SerializeToElement(HomeAssistantProvider.Entity("sensor.disparu", null, control: true));
        Assert.Equal("absent", gone.GetProperty("state").GetString());
    }

    // ---------- compositions : source « Home Assistant » intégrée (modèles « Maison connectée ») ----------

    static string HaDoc()
    {
        var n = Compositions.HaSource.ToString("N");
        return "{\"w\":1920,\"h\":1080,\"els\":[" +
            "{\"id\":\"e1\",\"type\":\"text\",\"text\":\"Salon : {{" + n + ":e:sensor.salon_temperature}}\",\"x\":0,\"y\":0,\"w\":600,\"h\":80}," +
            "{\"id\":\"e2\",\"type\":\"app\",\"app\":\"ha-sensors\",\"source\":\"" + n + "\",\"ents\":[\"binary_sensor.porte\",\"lock.entree\"],\"x\":0,\"y\":100,\"w\":600,\"h\":300}]}";
    }

    [Fact]
    public void Composition_lists_the_home_assistant_entities_it_shows() =>
        Assert.Equal(new[] { "binary_sensor.porte", "lock.entree", "sensor.salon_temperature" }, Compositions.HaEntities(HaDoc()).OrderBy(x => x));

    [Fact]
    public void Published_composition_reads_its_entities_like_a_sensors_widget()
    {
        var r = Compositions.Publish(new Dictionary<string, string> { ["doc"] = HaDoc() }, _ => null, _ => null)!.Value;
        Assert.Contains("\"ents\"", r.Settings["doc"]);   // le bloc reste, sans contenu à créer
        var pub = new PublishedItem { Id = Guid.NewGuid(), AppId = Compositions.AppId, Settings = r.Settings };
        var src = Compositions.SourceItem(pub, Compositions.HaSource);
        Assert.NotNull(src);
        Assert.Equal("ha-sensors", src!.AppId);
        Assert.Equal("false", src.Settings["control"]);   // lecture seule
        Assert.Equal(new[] { "binary_sensor.porte", "lock.entree", "sensor.salon_temperature" }, HomeAssistantRules.ParseEntities(src.Settings["entities"]).OrderBy(x => x));
        Assert.Null(Compositions.SourceItem(new PublishedItem { AppId = Compositions.AppId, Settings = new() }, Compositions.HaSource));   // composition sans Home Assistant
    }

    [Fact]
    public void Screens_showing_a_composition_are_told_when_its_entities_change()
    {
        var db = new Db();
        var t = new Tenant { Name = "Maison" };
        db.Clients.Add(t);
        var r = Compositions.Publish(new Dictionary<string, string> { ["doc"] = HaDoc() }, _ => null, _ => null)!.Value;
        var item = new PublishedItem { Id = Guid.NewGuid(), Type = "app", AppId = Compositions.AppId, Settings = r.Settings };
        var screen = new Screen { ClientId = t.Id, Token = "jeton", PublishedVersion = 1, Published = { item } };
        db.Screens.Add(screen);
        var uses = HomeAssistantLive.Uses(db, t.Id);
        Assert.Equal((screen.Id, item.Id.ToString()), Assert.Single(uses["lock.entree"]));   // l'écran relit /api/data/{composition}/{source}
        Assert.Contains("sensor.salon_temperature", uses.Keys);
    }

    [Fact]
    public void Used_entities_cover_widgets_contents_and_data_sets()
    {
        var db = new Db();
        var t = new Tenant { Name = "Maison" };
        db.Clients.Add(t);
        var screen = new Screen { ClientId = t.Id };
        screen.Widgets.Add(new ScreenWidget { AppId = "ha-lights", Settings = { ["entities"] = "light.bureau" } });
        db.Screens.Add(screen);
        db.Media.Add(new MediaItem { ClientId = t.Id, AppId = "ha-sensors", Settings = { ["entities"] = "sensor.salon_temperature" } });
        var c = new DataConnection { Kind = DataKinds.Ha, Name = "Home Assistant" };
        t.DataConnections.Add(c);
        t.DataSets.Add(new DataSet { ConnectionId = c.Id, Name = "Historique", Path = "history:sensor.cave:24" });
        Assert.Equal(new[] { "light.bureau", "sensor.cave", "sensor.salon_temperature" }, HomeAssistantLive.Used(db, t).OrderBy(x => x));
    }

    [Fact]
    public void Setup_enables_the_widgets_and_creates_the_data_source_once()
    {
        var db = new Db();
        var t = new Tenant { Name = "Maison", HomeAssistant = new HomeAssistantAccount { Url = "https://maison.exemple.ch" } };
        db.Clients.Add(t);
        HomeAssistantSetup.Apply(db, t, catalog);
        HomeAssistantSetup.Apply(db, t, catalog);
        Assert.Equal(HomeAssistantRules.AllApps.OrderBy(x => x), db.AppInstalls.Where(a => a.ClientId == t.Id).Select(a => a.AppId).OrderBy(x => x));
        var conn = Assert.Single(t.DataConnections);
        Assert.Equal(DataKinds.Ha, conn.Kind);
        Assert.Equal("states:*", Assert.Single(t.DataSets).Path);
        Assert.Null(DataSourceOps.CheckConnection(conn.Copy()));
    }

    // ---------- client face à une fausse instance ----------

    sealed class FakeHa : IHaTransport
    {
        public readonly List<(string Method, string Path, string? Body, string? Auth)> Calls = new();
        public string Token = "access-1";
        public bool RefreshRevoked;
        public int Logins;
        public Func<string, (int, string)>? Override;

        public Task<(int Status, string Body)> Send(HaConn c, HttpMethod method, string url, string? body, string? contentType, Dictionary<string, string> headers, int maxBytes, CancellationToken ct)
        {
            var path = url[c.Url.Length..];
            Calls.Add((method.Method, path, body, headers.GetValueOrDefault("Authorization")));
            if (Override?.Invoke(path) is { } o && o.Item1 != 0) return Task.FromResult(o);
            (int, string) r = path switch
            {
                "/auth/login_flow" => (200, """{"flow_id":"f1","type":"form"}"""),
                "/auth/login_flow/f1" => body!.Contains("\"password\":\"bon\"") ? (200, """{"type":"create_entry","result":"code-1"}""") : (200, """{"type":"form","errors":{"base":"invalid_auth"}}"""),
                "/auth/token" when body!.StartsWith("grant_type=authorization_code") => Login(),
                "/auth/token" when body!.StartsWith("grant_type=refresh_token") => RefreshRevoked ? (400, """{"error":"invalid_grant"}""") : (200, $$"""{"access_token":"{{Token}}","expires_in":1800,"token_type":"Bearer"}"""),
                "/auth/revoke" => (200, ""),
                "/api/states" => headers.GetValueOrDefault("Authorization") == "Bearer " + Token ? (200, StatesJson) : (401, ""),
                _ when path.StartsWith("/api/services/") => (200, "[]"),
                _ => (404, ""),
            };
            return Task.FromResult(r);
        }

        private (int, string) Login() { Logins++; return (200, $$"""{"access_token":"{{Token}}","expires_in":1800,"refresh_token":"refresh-1","token_type":"Bearer"}"""); }
    }

    sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Linkivi";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    static (HomeAssistantClient Client, FakeHa Fake, JsonStore Store, SecretBox Box) Make()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkii-ha-" + Guid.NewGuid().ToString("N"));
        var store = new JsonStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Linkii:DataDir"] = dir }).Build(), new Env());
        var box = new SecretBox(new EphemeralDataProtectionProvider());
        var fake = new FakeHa();
        return (new HomeAssistantClient(fake, box, store), fake, store, box);
    }

    static string Url() => "https://ha-" + Guid.NewGuid().ToString("N")[..8] + ".exemple.ch";

    [Fact]
    public async Task Token_connection_reads_entities()
    {
        var (ha, fake, _, box) = Make();
        var a = await ha.Connect(Guid.NewGuid(), Url(), "direct", null, "token", "", "access-1", null);
        Assert.Equal("token", a.AuthMode);
        Assert.Equal("access-1", box.Unprotect(a.Secret));
        Assert.Equal("", a.Refresh);
        Assert.Contains(a.Entities, e => e.Id == "binary_sensor.porte" && e.DeviceClass == "door");
        Assert.DoesNotContain(a.Entities, e => e.Id == "camera.jardin");   // domaine non pris en charge
        Assert.All(fake.Calls, c => Assert.Equal("Bearer access-1", c.Auth));
    }

    [Fact]
    public async Task Password_connection_logs_in_once_then_renews_with_the_refresh_token()
    {
        var (ha, fake, _, box) = Make();
        var client = Guid.NewGuid();
        var a = await ha.Connect(client, Url(), "direct", null, "password", "linkivi", "bon", null);
        Assert.Equal(1, fake.Logins);
        Assert.Equal("refresh-1", box.Unprotect(a.Refresh));   // gardé, chiffré, dans le compte

        fake.Token = "access-2";   // jeton d'accès expiré côté Home Assistant : 401, puis renouvellement
        await ha.States(ha.Conn(client, a), fresh: true);
        Assert.Equal(1, fake.Logins);   // aucune nouvelle session dans le profil Home Assistant
        Assert.Contains(fake.Calls, c => c.Path == "/auth/token" && c.Body!.StartsWith("grant_type=refresh_token") && c.Body.Contains("refresh-1"));
        Assert.Contains(Uri.EscapeDataString(HomeAssistantRules.ClientId), fake.Calls.First(c => c.Path == "/auth/token").Body);
    }

    [Fact]
    public async Task Revoked_session_logs_in_again()
    {
        var (ha, fake, _, _) = Make();
        var client = Guid.NewGuid();
        var a = await ha.Connect(client, Url(), "direct", null, "password", "linkivi", "bon", null);
        fake.Token = "access-3";
        fake.RefreshRevoked = true;   // session supprimée dans le profil Home Assistant
        await ha.States(ha.Conn(client, a), fresh: true);
        Assert.Equal(2, fake.Logins);
    }

    [Fact]
    public async Task Wrong_password_is_reported()
    {
        var (ha, _, _, _) = Make();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ha.Connect(Guid.NewGuid(), Url(), "direct", null, "password", "linkivi", "faux", null));
        Assert.Contains("mot de passe refusé", ex.Message);
    }

    [Fact]
    public async Task Disconnect_revokes_the_session()
    {
        var (ha, fake, _, _) = Make();
        var client = Guid.NewGuid();
        var a = await ha.Connect(client, Url(), "direct", null, "password", "linkivi", "bon", null);
        await ha.Revoke(ha.Conn(client, a));
        var revoke = Assert.Single(fake.Calls, c => c.Path == "/auth/revoke");
        Assert.Equal("token=refresh-1", revoke.Body);
    }

    [Fact]
    public async Task Command_calls_the_service_and_forgets_cached_states()
    {
        var (ha, fake, _, _) = Make();
        var client = Guid.NewGuid();
        var a = await ha.Connect(client, Url(), "direct", null, "token", "", "access-1", null);
        var conn = ha.Conn(client, a);
        await ha.States(conn);
        await ha.Command(conn, "light.bureau", "brightness", 55);
        var call = Assert.Single(fake.Calls, c => c.Path.StartsWith("/api/services/"));
        Assert.Equal("/api/services/light/turn_on", call.Path);
        Assert.Contains("\"brightness_pct\":55", call.Body);
        var before = fake.Calls.Count(c => c.Path == "/api/states");
        await ha.States(conn);   // cache oublié : l'état est relu
        Assert.Equal(before + 1, fake.Calls.Count(c => c.Path == "/api/states"));
    }

    [Fact]
    public async Task Refused_token_is_explained()
    {
        var (ha, _, _, _) = Make();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ha.Connect(Guid.NewGuid(), Url(), "direct", null, "token", "", "mauvais", null));
        Assert.Equal("Jeton refusé par Home Assistant.", ex.Message);
    }

    const string StatesJson = """
        [
          {"entity_id":"sensor.salon_temperature","state":"21.37","last_changed":"2026-11-01T10:00:00+00:00","attributes":{"friendly_name":"Salon","unit_of_measurement":"°C","device_class":"temperature"}},
          {"entity_id":"binary_sensor.porte","state":"off","attributes":{"friendly_name":"Porte d'entrée","device_class":"door"}},
          {"entity_id":"light.bureau","state":"on","attributes":{"friendly_name":"Bureau","brightness":102,"rgb_color":[255,128,0],"supported_color_modes":["hs"]}},
          {"entity_id":"cover.store_sud","state":"open","attributes":{"friendly_name":"Store sud","current_position":60,"supported_features":15}},
          {"entity_id":"climate.salon","state":"heat","attributes":{"friendly_name":"Thermostat","current_temperature":20.5,"temperature":21,"min_temp":7,"max_temp":30,"hvac_action":"heating"}},
          {"entity_id":"camera.jardin","state":"idle","attributes":{"friendly_name":"Jardin"}}
        ]
        """;
}
