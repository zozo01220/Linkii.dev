using System.Net;
using System.Text.Json;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Installation sur l'écran d'accueil : manifeste par revendeur, icônes, service worker, accessibles sans être connecté.</summary>
[Collection("platform")]
public class PwaTests(Platform p)
{
    [Fact]
    public async Task Manifest_is_public_and_carries_the_brand_of_the_domain()
    {
        var c = p.Client();
        var r = await c.GetAsync("/manifest.webmanifest");   // pas de cookie : le navigateur le demande ainsi
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/manifest+json", r.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var m = doc.RootElement;
        Assert.Equal("Linkii", m.GetProperty("name").GetString());
        Assert.Equal("standalone", m.GetProperty("display").GetString());
        Assert.Equal("/", m.GetProperty("start_url").GetString());
        Assert.Contains(m.GetProperty("icons").EnumerateArray(), i => i.GetProperty("sizes").GetString() == "192x192");
        Assert.Contains(m.GetProperty("icons").EnumerateArray(), i => i.GetProperty("purpose").GetString() == "maskable");

        // revendeur en marque blanche : son nom et sa couleur
        var acme = p.Client("affichage.acme.ch");
        using var doc2 = JsonDocument.Parse(await acme.GetStringAsync("/manifest.webmanifest"));
        Assert.Equal("Acme Affichage", doc2.RootElement.GetProperty("name").GetString());
        Assert.Equal("Acme Afficha", doc2.RootElement.GetProperty("short_name").GetString());
        Assert.Equal("#ff0000", doc2.RootElement.GetProperty("theme_color").GetString());
    }

    [Theory]
    [InlineData("/icons/icon-192.png", "image/png")]
    [InlineData("/icons/icon-512.png", "image/png")]
    [InlineData("/icons/maskable-512.png", "image/png")]
    [InlineData("/icons/apple-touch-icon.png", "image/png")]
    [InlineData("/sw.js", "text/javascript")]
    [InlineData("/pwa.js", "text/javascript")]
    [InlineData("/offline.html", "text/html")]
    public async Task Install_assets_are_served_without_login(string url, string type)
    {
        var r = await p.Client().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(type, r.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/icons/player-192.png")]
    [InlineData("/icons/player-512.png")]
    [InlineData("/icons/player-maskable-512.png")]
    [InlineData("/icons/player-apple-touch-icon.png")]
    public async Task Player_icons_are_served_without_login(string url)
    {
        var r = await p.Client().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/png", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Player_is_installable_fullscreen_with_its_own_icon()
    {
        var c = p.Client();
        var r = await c.GetAsync("/player/manifest.webmanifest");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/manifest+json", r.Content.Headers.ContentType?.MediaType);
        using var player = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var m = player.RootElement;
        Assert.Equal("fullscreen", m.GetProperty("display").GetString());
        Assert.Equal("/player/", m.GetProperty("scope").GetString());
        Assert.Equal("/player/", m.GetProperty("start_url").GetString());
        Assert.Contains(m.GetProperty("icons").EnumerateArray(), i => i.GetProperty("purpose").GetString() == "maskable");

        // chaque icône déclarée existe, et le player n'a pas la même que le back-office
        var playerIcons = m.GetProperty("icons").EnumerateArray().Select(i => i.GetProperty("src").GetString()!).ToList();
        foreach (var src in playerIcons) Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(src)).StatusCode);
        using var bo = JsonDocument.Parse(await c.GetStringAsync("/manifest.webmanifest"));
        var boIcons = bo.RootElement.GetProperty("icons").EnumerateArray().Select(i => i.GetProperty("src").GetString()!).ToList();
        Assert.Empty(playerIcons.Intersect(boIcons));
        Assert.NotEqual(await c.GetByteArrayAsync("/icons/icon-512.png"), await c.GetByteArrayAsync("/icons/player-512.png"));

        var html = await c.GetStringAsync("/player/index.html");
        Assert.Contains("rel=\"manifest\" href=\"/player/manifest.webmanifest\"", html);
        Assert.Contains("rel=\"apple-touch-icon\" href=\"/icons/player-apple-touch-icon.png\"", html);
        Assert.Contains("apple-mobile-web-app-capable", html);
        Assert.Contains("viewport-fit=cover", html);
    }

    [Fact]
    public void Smartphone_is_a_screen_type_in_portrait()
    {
        var phone = ScreenOptions.Types.Single(t => t.Id == "phone");
        Assert.Equal("portrait", phone.Orientation);
        Assert.Equal("Smartphone", ScreenOptions.TypeLabel("phone"));
    }

    [Fact]
    public async Task Login_page_links_the_manifest_and_the_ios_home_screen_tags()
    {
        var html = await p.Client().GetStringAsync("/login");
        Assert.Contains("rel=\"manifest\" href=\"/manifest.webmanifest\"", html);
        Assert.Contains("rel=\"apple-touch-icon\"", html);
        Assert.Contains("apple-mobile-web-app-capable", html);
        Assert.Contains("id=\"pwa-install\"", html);
    }

    [Fact]
    public async Task Service_worker_is_revalidated_on_every_load()
    {
        var r = await p.Client().GetAsync("/sw.js");
        Assert.True(r.Headers.CacheControl?.NoCache);
    }
}
