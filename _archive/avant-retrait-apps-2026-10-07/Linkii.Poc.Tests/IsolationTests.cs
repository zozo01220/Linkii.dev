using Xunit;
using System.Net;
using Linkii.Poc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Linkii.Poc.Tests;

/// <summary>Deux revendeurs, un client chacun : vérifie qu'un client ne lit aucune donnée d'un autre, liens de médias compris.</summary>
public class Platform : WebApplicationFactory<Program>, IDisposable
{
    public readonly string Dir = Path.Combine(Path.GetTempPath(), "linkii-tests-" + Guid.NewGuid().ToString("N"));
    public Reseller R1 = null!, R2 = null!;
    public Tenant A = null!, B = null!;
    public string TokenA = "token-a", TokenB = "token-b";
    public string FileA = "aaaa.png", FileB = "bbbb.png";
    public Guid MediaB;     // une image de B
    public Guid AppItemB;   // une app (« rss ») publiée dans la playlist de l'écran de B

    public Platform()
    {
        Environment.SetEnvironmentVariable("Linkii__DataDir", Dir);
        Directory.CreateDirectory(Path.Combine(Dir, "media"));
        File.WriteAllBytes(Path.Combine(Dir, "media", FileA), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(Dir, "media", FileB), new byte[] { 4, 5, 6 });
        var store = Services.GetRequiredService<JsonStore>();   // démarre l'application (seed compris)
        store.Write(db =>
        {
            R1 = db.Resellers.First(r => r.IsDefault);
            R2 = new Reseller { Name = "Acme", Slug = "acme", BrandName = "Acme Affichage", BrandColor = "#ff0000", CustomDomain = "affichage.acme.ch" };
            db.Resellers.Add(R2);
            A = new Tenant { ResellerId = R1.Id, Name = "Client A" };
            B = new Tenant { ResellerId = R2.Id, Name = "Client B" };
            db.Clients.AddRange(new[] { A, B });
            db.Screens.Add(new Screen { ClientId = A.Id, Name = "A1", Token = TokenA });
            var playlistB = new Playlist { ClientId = B.Id, Name = "Playlist B" };
            AppItemB = Guid.NewGuid();
            playlistB.Published.Add(new PublishedItem
            {
                Id = AppItemB, Type = "app", AppId = "rss", DataId = AppItemB.ToString(),
                Settings = new() { ["title"] = "Plat secret de B" }
            });
            db.Playlists.Add(playlistB);
            db.Screens.Add(new Screen { ClientId = B.Id, Name = "B1", Token = TokenB, PlaylistId = playlistB.Id });
            db.Media.Add(new MediaItem { ClientId = A.Id, Name = "a", Type = "image", FileName = FileA });
            var b = new MediaItem { ClientId = B.Id, Name = "b", Type = "image", FileName = FileB };
            MediaB = b.Id;
            db.Media.Add(b);
        });
    }

    // Pas d'appel réseau dans les tests : les fournisseurs « rss » et « calendar » sont remplacés par un écho des valeurs lues par le serveur.
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(s =>
    {
        s.RemoveAll<IAppProvider>();
        s.AddSingleton<IAppProvider>(new EchoProvider("rss"));
        s.AddSingleton<IAppProvider>(new EchoProvider("calendar"));
    });

    public HttpClient Client(string? host = null)
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (host != null) c.DefaultRequestHeaders.Host = host;
        return c;
    }

    public static HttpRequestMessage Get(string url, string? token = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        if (token != null) r.Headers.Add("X-Token", token);
        return r;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Dir, true); } catch { }
    }
}

[Collection("platform")]
public class IsolationTests(Platform p)
{
    [Fact]
    public void Scoped_lists_only_see_their_own_client()
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        store.Read(db =>
        {
            var a = new ClientDb(db, p.A.Id);
            var b = new ClientDb(db, p.B.Id);
            Assert.All(a.Media, m => Assert.Equal(p.A.Id, m.ClientId));
            Assert.DoesNotContain(a.Media, m => m.Id == p.MediaB);
            Assert.Contains(b.Media, m => m.Id == p.MediaB);
            Assert.Equal("Client A", a.Tenant.Name);
            return 0;
        });
    }

    [Fact]
    public void Scoped_remove_never_touches_another_client()
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        store.Write(db => { new ClientDb(db, p.A.Id).Media.RemoveAll(_ => true); });
        Assert.True(store.Read(db => db.Media.Any(m => m.ClientId == p.B.Id)));
        store.Write(db => { new ClientDb(db, p.A.Id).Media.Add(new MediaItem { Name = "a", Type = "image", FileName = p.FileA }); });   // remet la donnée pour les autres tests
    }

    [Fact]
    public async Task Media_link_of_another_client_is_not_readable()
    {
        var c = p.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Platform.Get("/media/" + p.FileA, p.TokenA))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Platform.Get("/media/" + p.FileB, p.TokenA))).StatusCode);   // écran de A, fichier de B
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Platform.Get("/media/" + p.FileA))).StatusCode);             // anonyme
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Platform.Get("/media/" + p.FileB + "?t=" + p.TokenA))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Platform.Get("/media/" + p.FileB + "?t=" + p.TokenB))).StatusCode);
    }

    [Fact]
    public async Task Media_path_traversal_is_rejected()
    {
        var c = p.Client();
        var r = await c.SendAsync(Platform.Get("/media/..%2Fdata.json", p.TokenA));
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Screen_cannot_read_app_data_of_another_client()
    {
        var c = p.Client();
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Platform.Get("/api/data/" + p.AppItemB, p.TokenA))).StatusCode);
        var own = await c.SendAsync(Platform.Get("/api/data/" + p.AppItemB, p.TokenB));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Contains("Plat secret de B", await own.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Plat secret de B", await (await c.SendAsync(Platform.Get("/api/data/" + p.AppItemB, p.TokenA))).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Player_api_requires_a_valid_token_and_returns_the_resellers_brand()
    {
        var c = p.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Platform.Get("/api/player/playlist"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Platform.Get("/api/player/playlist", "nope"))).StatusCode);
        var b = await (await c.SendAsync(Platform.Get("/api/player/playlist", p.TokenB))).Content.ReadAsStringAsync();
        Assert.Contains("Acme Affichage", b);
        Assert.Contains("#ff0000", b);
    }

    [Fact]
    public async Task Suspending_a_client_or_a_reseller_stops_its_screens()
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        var c = p.Client();
        store.Write(db => db.Clients.First(x => x.Id == p.B.Id).Suspended = true);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Platform.Get("/api/player/playlist", p.TokenB))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Platform.Get("/api/player/playlist", p.TokenA))).StatusCode);   // les autres clients ne sont pas touchés
        store.Write(db => { db.Clients.First(x => x.Id == p.B.Id).Suspended = false; db.Resellers.First(r => r.Id == p.R2.Id).Active = false; });
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Platform.Get("/api/player/playlist", p.TokenB))).StatusCode);
        store.Write(db => db.Resellers.First(r => r.Id == p.R2.Id).Active = true);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Platform.Get("/api/player/playlist", p.TokenB))).StatusCode);
    }

    [Fact]
    public async Task Reseller_is_resolved_from_the_domain_name()
    {
        var def = await (await p.Client().GetAsync("/login")).Content.ReadAsStringAsync();
        Assert.Contains("Linkii", def);
        Assert.DoesNotContain("Acme Affichage", def);

        foreach (var host in new[] { "acme.localhost", "affichage.acme.ch" })
        {
            var html = await (await p.Client(host).GetAsync("/login")).Content.ReadAsStringAsync();
            Assert.Contains("Acme Affichage", html);
            Assert.Contains("#ff0000", html);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await p.Client("inconnu.localhost").GetAsync("/login")).StatusCode);
    }

    [Fact]
    public async Task Pairing_screen_is_branded_by_the_domain()
    {
        var body = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        var json = await (await p.Client("acme.localhost").PostAsync("/api/pairing/start", body)).Content.ReadAsStringAsync();
        Assert.Contains("Acme Affichage", json);
    }

    [Fact]
    public async Task Tls_is_only_allowed_for_declared_domains()
    {
        var c = p.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/internal/tls-allow?domain=affichage.acme.ch")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/internal/tls-allow?domain=acme.linkii.com")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/internal/tls-allow?domain=evil.example.org")).StatusCode);
    }

    [Fact]
    public async Task Backoffice_requires_a_session_and_admin_pages_redirect_to_login()
    {
        var c = p.Client();
        foreach (var path in new[] { "/", "/admin", "/reseller", "/media" })
        {
            var r = await c.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/login", r.Headers.Location!.ToString());
        }
        Assert.Equal(HttpStatusCode.Redirect, (await c.PostAsync("/space/enter", new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("clientId", p.A.Id.ToString()) }))).StatusCode);
    }

    [Fact]
    public void Users_may_only_sign_in_on_their_resellers_domain()
    {
        var customer = new User { Role = Roles.ClientAdmin, ResellerId = p.R2.Id };
        var platform = new User { Role = Roles.PlatformAdmin };
        Assert.True(Tenancy.HostAllows(customer, p.R2));
        Assert.False(Tenancy.HostAllows(customer, p.R1));
        Assert.True(Tenancy.HostAllows(platform, p.R1));
        Assert.False(Tenancy.HostAllows(platform, p.R2));
    }
}

[CollectionDefinition("platform")]
public class PlatformCollection : ICollectionFixture<Platform> { }
