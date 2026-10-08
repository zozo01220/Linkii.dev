using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Linkii.Poc;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Fournisseur de remplacement : renvoie les valeurs que le serveur a lues (secrets déchiffrés compris), sans appel réseau.</summary>
public class EchoProvider(string id) : IAppProvider
{
    public string Id => id;
    public Task<object?> Fetch(AppRuntime rt) => Task.FromResult<object?>(new { echoed = rt.Values });
    public Task<string> Test(AppRuntime rt) => Task.FromResult("ok " + rt.Get("url"));
}

class FixedAuth(ClaimsPrincipal user) : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
}

[Collection("platform")]
public class AppsTests(Platform p)
{
    const string IcsSecret = "https://calendar.example.org/private-token-12345/basic.ics";

    // Le service des apps tel que le voit l'administrateur d'un client.
    AppService ServiceFor(Tenant client)
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(Claims.Client, client.Id.ToString()), new Claim(ClaimTypes.Role, Roles.ClientAdmin) }, "test"));
        var tenantStore = new TenantStore(store, new TenantContext(new FixedAuth(user), store));
        return new AppService(tenantStore, p.Services.GetRequiredService<AppCatalog>(), p.Services.GetRequiredService<AppProviders>(), p.Services.GetRequiredService<SecretBox>());
    }

    static Dictionary<string, string> Values(AppService s, string appId, params (string, string)[] over)
    {
        var v = AppCatalog.WithDefaults(s.Find(appId)!, null);
        foreach (var (k, val) in over) v[k] = val;
        return v;
    }

    Guid Playlist(Tenant client, params (Guid media, string placement)[] items)
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        return store.Write(db =>
        {
            var pl = new Playlist { ClientId = client.Id, Name = "Test " + Guid.NewGuid().ToString("N")[..6] };
            foreach (var (m, pos) in items) pl.Draft.Add(new PlaylistItem { MediaId = m, Placement = pos, DurationSec = 5 });
            db.Playlists.Add(pl);
            return pl.Id;
        });
    }

    PublishedItem Published(Guid playlistId, int index = 0) =>
        p.Services.GetRequiredService<JsonStore>().Read(db => db.Playlists.First(x => x.Id == playlistId).Published[index]);

    // ---------- Catalogue et manifestes ----------

    [Fact]
    public void Catalog_contains_the_phase_1_apps_with_valid_manifests()
    {
        var catalog = p.Services.GetRequiredService<AppCatalog>();
        Assert.Equal(new[] { "agenda", "clock", "image", "linkedin", "menu-api", "menu-csv", "room", "rss", "slideshow-images", "slideshow-videos", "text", "video", "weather", "weather-forecast", "webpage", "youtube" }, catalog.All.Select(a => a.Id).OrderBy(x => x).ToArray());
        Assert.All(catalog.All, a => Assert.Empty(AppCatalog.Check(a)));
        Assert.Equal(new[] { "linkedin", "rss", "text", "webpage" }, catalog.All.Where(a => a.IsNoCode).Select(a => a.Id).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Invalid_manifests_are_rejected()
    {
        Assert.NotEmpty(AppCatalog.Check(new AppManifest { Id = "Bad Id", Name = "x", Placements = { "full" } }));
        Assert.NotEmpty(AppCatalog.Check(new AppManifest { Id = "ok", Name = "x", Placements = { "middle" } }));
        Assert.NotEmpty(AppCatalog.Check(new AppManifest { Id = "ok", Name = "x", Placements = { "full" }, Data = true }));   // données sans fournisseur
        Assert.NotEmpty(AppCatalog.Check(new AppManifest { Id = "ok", Name = "x", Placements = { "full" }, Settings = { new AppField { Key = "a", Type = "magic" } } }));
    }

    // ---------- Validation des paramètres ----------

    [Fact]
    public void Settings_are_validated_against_the_manifest()
    {
        var s = ServiceFor(p.A);
        var text = s.Find("text")!;
        var none = new HashSet<string>();

        Assert.Contains(AppCatalog.Validate(text, Values(s, "text"), none), e => e.Contains("Texte"));                                      // obligatoire
        Assert.Contains(AppCatalog.Validate(text, Values(s, "text", ("body", "Bonjour"), ("bg", "rouge")), none), e => e.Contains("couleur"));
        Assert.Contains(AppCatalog.Validate(text, Values(s, "text", ("body", "Bonjour"), ("size", "enorme")), none), e => e.Contains("choix"));
        Assert.Contains(AppCatalog.Validate(text, Values(s, "text", ("body", "Bonjour"), ("validFrom", "2026-12-31"), ("validTo", "2026-01-01")), none), e => e.Contains("fin"));
        Assert.Contains(AppCatalog.Validate(text, Values(s, "text", ("body", "Bonjour"), ("validFrom", "31.12.2026")), none), e => e.Contains("date"));
        Assert.Empty(AppCatalog.Validate(text, Values(s, "text", ("body", "Bonjour")), none));

        var rss = s.Find("rss")!;
        Assert.NotEmpty(AppCatalog.Validate(rss, Values(s, "rss", ("url", "javascript:alert(1)")), none));
        Assert.NotEmpty(AppCatalog.Validate(rss, Values(s, "rss", ("url", "file:///etc/passwd")), none));
        Assert.NotEmpty(AppCatalog.Validate(rss, Values(s, "rss", ("url", "https://user:pw@example.org/feed")), none));
        Assert.NotEmpty(AppCatalog.Validate(rss, Values(s, "rss", ("url", "https://example.org/feed"), ("count", "99")), none));
        Assert.Empty(AppCatalog.Validate(rss, Values(s, "rss", ("url", "https://example.org/feed")), none));
    }

    [Fact]
    public void Linkedin_app_requires_posts_and_bounds_its_numbers()
    {
        var s = ServiceFor(p.A);
        var li = s.Find("linkedin")!;
        var none = new HashSet<string>();
        Assert.Contains(AppCatalog.Validate(li, Values(s, "linkedin"), none), e => e.Contains("Publications"));
        Assert.NotEmpty(AppCatalog.Validate(li, Values(s, "linkedin", ("posts", "Bonjour"), ("count", "9")), none));
        Assert.NotEmpty(AppCatalog.Validate(li, Values(s, "linkedin", ("posts", "Bonjour"), ("layout", "grille")), none));
        Assert.Empty(AppCatalog.Validate(li, Values(s, "linkedin", ("posts", "date: 2026-10-04\nNous recrutons\n---\nMerci")), none));
    }

    [Fact]
    public void Hidden_fields_are_neither_required_nor_stored()
    {
        var s = ServiceFor(p.A);
        s.Install("agenda");
        var agenda = s.Find("agenda")!;
        // source Microsoft 365 : le lien ICS (obligatoire par ailleurs) ne l'est plus, mais la salle l'est
        var errs = AppCatalog.Validate(agenda, Values(s, "agenda", ("source", "m365")), new HashSet<string>());
        Assert.Single(errs);
        Assert.Contains("Microsoft 365", errs[0]);
        var (item, errors) = s.SaveInstance("agenda", null, "Salle", Values(s, "agenda", ("source", "m365"), ("mailbox", "salle@x.ch"), ("icsUrl", "https://ignored.example")));
        Assert.Empty(errors);
        Assert.False(item!.Secrets.ContainsKey("icsUrl"));
        Assert.False(item.Settings.ContainsKey("icsUrl"));
    }

    // ---------- Installation, instances, secrets ----------

    [Fact]
    public void Apps_belong_to_the_client_who_added_them()
    {
        var a = ServiceFor(p.A);
        var b = ServiceFor(p.B);
        a.Install("clock");
        Assert.True(a.IsInstalled("clock"));
        Assert.False(b.IsInstalled("clock"));
        var (item, _) = a.SaveInstance("clock", null, "Horloge A", Values(a, "clock"));
        Assert.NotNull(item);
        Assert.Empty(b.Instances("clock"));
        // B ne peut pas créer d'instance sans avoir installé l'app, ni modifier celle d'A
        Assert.NotEmpty(b.SaveInstance("clock", null, "Horloge B", Values(b, "clock")).Errors);
        b.Install("clock");
        Assert.NotEmpty(b.SaveInstance("clock", item!.Id, "Piratée", Values(b, "clock")).Errors);
        Assert.Equal("Horloge A", a.Instances("clock").Single().Name);
    }

    [Fact]
    public void Secrets_are_encrypted_kept_when_left_empty_and_never_sent_to_the_player()
    {
        var s = ServiceFor(p.A);
        s.Install("agenda");
        var (item, errors) = s.SaveInstance("agenda", null, "Agenda secret", Values(s, "agenda", ("icsUrl", IcsSecret)));
        Assert.Empty(errors);
        Assert.StartsWith("dp:", item!.Secrets["icsUrl"]);
        Assert.DoesNotContain(IcsSecret, item.Secrets["icsUrl"]);
        Assert.False(item.Settings.ContainsKey("icsUrl"));
        Assert.DoesNotContain(IcsSecret, JsonSerializer.Serialize(item));

        // modification sans ressaisir le secret : il est conservé
        var before = item.Secrets["icsUrl"];
        var (again, errs2) = s.SaveInstance("agenda", item.Id, "Agenda secret", Values(s, "agenda", ("title", "Nouveau titre")));
        Assert.Empty(errs2);
        Assert.Equal(before, again!.Secrets["icsUrl"]);

        // publié : l'instantané garde le secret chiffré côté serveur, mais le player ne reçoit rien
        var pl = Playlist(p.A, (item.Id, "full"));
        p.Services.GetRequiredService<Notifier>().Publish(pl).GetAwaiter().GetResult();
        var pub = Published(pl);
        Assert.Equal(before, pub.Secrets["icsUrl"]);
        var forPlayer = JsonSerializer.Serialize(pub.ForPlayer());
        Assert.DoesNotContain(IcsSecret, forPlayer);
        Assert.DoesNotContain("dp:", forPlayer);
        Assert.DoesNotContain("icsUrl", forPlayer);
        Assert.Contains("Nouveau titre", forPlayer);
    }

    // ---------- « Les modifications doivent être publiées » ----------

    [Fact]
    public async Task Editing_an_instance_reaches_the_screens_only_after_publishing()
    {
        var s = ServiceFor(p.A);
        s.Install("text");
        var (item, _) = s.SaveInstance("text", null, "Annonce", Values(s, "text", ("body", "Version 1")));
        var pl = Playlist(p.A, (item!.Id, "full"));
        var notifier = p.Services.GetRequiredService<Notifier>();
        await notifier.Publish(pl);
        Assert.Equal("Version 1", Published(pl).Settings["body"]);

        s.SaveInstance("text", item.Id, "Annonce", Values(s, "text", ("body", "Version 2")));
        Assert.Equal("Version 1", Published(pl).Settings["body"]);   // l'écran verrait toujours la version 1
        Assert.True(p.Services.GetRequiredService<JsonStore>().Read(db => db.Playlists.First(x => x.Id == pl).DraftChanged));   // « brouillon non publié »

        await notifier.Publish(pl);
        Assert.Equal("Version 2", Published(pl).Settings["body"]);
        Assert.False(p.Services.GetRequiredService<JsonStore>().Read(db => db.Playlists.First(x => x.Id == pl).DraftChanged));
    }

    [Fact]
    public async Task Published_snapshot_carries_version_validity_and_a_valid_placement()
    {
        var s = ServiceFor(p.A);
        s.Install("text"); s.Install("webpage");
        var (note, _) = s.SaveInstance("text", null, "Période", Values(s, "text", ("body", "Soldes"), ("validFrom", "2026-11-01"), ("validTo", "2026-11-30")));
        var (web, _) = s.SaveInstance("webpage", null, "Site", Values(s, "webpage", ("url", "https://example.org")));
        var pl = Playlist(p.A, (note!.Id, "top"), (web!.Id, "top-left"));   // la page web n'accepte que la pleine page
        await p.Services.GetRequiredService<Notifier>().Publish(pl);

        var n = Published(pl, 0);
        Assert.Equal("top", n.Placement);
        Assert.Equal("text", n.AppId);
        Assert.Equal("1.0.0", n.AppVersion);
        Assert.Equal("2026-11-01", n.ValidFrom);
        Assert.Equal("2026-11-30", n.ValidTo);
        Assert.Null(n.DataId);   // pas de données serveur pour une annonce
        Assert.Equal("full", Published(pl, 1).Placement);
    }

    [Fact]
    public async Task Uninstalling_removes_instances_and_marks_playlists_unpublished()
    {
        var s = ServiceFor(p.A);
        s.Install("clock");
        var (clock, _) = s.SaveInstance("clock", null, "À retirer", Values(s, "clock"));
        var pl = Playlist(p.A, (clock!.Id, "full"));
        await p.Services.GetRequiredService<Notifier>().Publish(pl);

        Assert.True(s.Uninstall("clock") >= 1);
        Assert.False(s.IsInstalled("clock"));
        Assert.Empty(s.Instances("clock"));
        var store = p.Services.GetRequiredService<JsonStore>();
        Assert.Empty(store.Read(db => db.Playlists.First(x => x.Id == pl).Draft));
        Assert.True(store.Read(db => db.Playlists.First(x => x.Id == pl).DraftChanged));
        Assert.Single(store.Read(db => db.Playlists.First(x => x.Id == pl).Published));   // l'écran ne change qu'à la prochaine publication
    }

    // ---------- Données servies aux écrans ----------

    [Fact]
    public async Task Screens_read_the_published_snapshot_of_their_own_playlist_only()
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        var s = ServiceFor(p.A);
        s.Install("agenda");
        var (item, _) = s.SaveInstance("agenda", null, "Agenda data", Values(s, "agenda", ("icsUrl", IcsSecret), ("title", "Titre 1")));
        var pl = Playlist(p.A, (item!.Id, "full"));
        var other = Playlist(p.A, (item.Id, "full"));
        var notifier = p.Services.GetRequiredService<Notifier>();
        await notifier.Publish(pl); await notifier.Publish(other);
        store.Write(db => db.Screens.First(x => x.Token == p.TokenA).PlaylistId = pl);
        var itemId = Published(pl).Id;
        var c = p.Client();

        var ok = await c.SendAsync(Platform.Get("/api/data/" + itemId, p.TokenA));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadAsStringAsync();
        Assert.Contains(IcsSecret, body);     // le serveur a bien déchiffré le secret pour le fournisseur (ici de remplacement)
        Assert.Contains("Titre 1", body);

        // modification non publiée : sans effet sur ce que lit l'écran
        s.SaveInstance("agenda", item.Id, "Agenda data", Values(s, "agenda", ("title", "Titre 2")));
        Assert.Contains("Titre 1", await (await c.SendAsync(Platform.Get("/api/data/" + itemId, p.TokenA))).Content.ReadAsStringAsync());

        // l'élément d'une autre playlist, ou d'un autre client, n'est pas lisible
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Platform.Get("/api/data/" + Published(other).Id, p.TokenA))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(Platform.Get("/api/data/" + itemId, p.TokenB))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Platform.Get("/api/data/" + itemId))).StatusCode);

        // la playlist reçue par le player ne contient ni secret ni instantané privé
        var playlist = await (await c.SendAsync(Platform.Get("/api/player/playlist", p.TokenA))).Content.ReadAsStringAsync();
        Assert.Contains("\"appId\":\"agenda\"", playlist);
        Assert.DoesNotContain(IcsSecret, playlist);
        Assert.DoesNotContain("icsUrl", playlist);
        Assert.DoesNotContain("secrets", playlist, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test_button_uses_stored_secrets_and_reports_validation_errors()
    {
        var s = ServiceFor(p.A);
        s.Install("rss");
        var rss = s.Find("rss")!;
        var (ok, msg) = await s.Test(rss, Values(s, "rss", ("url", "https://example.org/feed")), null);
        Assert.True(ok);
        Assert.Contains("https://example.org/feed", msg);
        var (bad, why) = await s.Test(rss, Values(s, "rss", ("url", "ftp://example.org")), null);
        Assert.False(bad);
        Assert.Contains("http", why);
        Assert.False((await s.Test(s.Find("clock")!, Values(s, "clock"), null)).Ok);   // une app sans fournisseur n'a rien à tester
    }

    // ---------- YouTube ----------

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=10", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("youtube.com/watch?v=dQw4w9WgXcQ&list=PLabcdefghijklmnop", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]   // une vidéo précise l'emporte sur la playlist
    [InlineData("dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/playlist?list=PLabcdefghijklmnop", "https://www.youtube.com/playlist?list=PLabcdefghijklmnop")]
    public void YouTube_links_are_normalized(string input, string expected) => Assert.Equal(expected, YouTube.Canonical(input));

    [Theory]
    [InlineData("https://evilyoutube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com.evil.org/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://example.org/watch?v=dQw4w9WgXcQ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=trop-court")]
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com/playlist?list=x")]
    [InlineData("")]
    public void Other_links_are_not_youtube(string input) => Assert.Null(YouTube.Canonical(input));

    [Fact]
    public void YouTube_instance_stores_the_normalized_link_and_rejects_others()
    {
        var s = ServiceFor(p.A);
        s.Install("youtube");
        Assert.NotEmpty(s.SaveInstance("youtube", null, "Mauvais", Values(s, "youtube", ("url", "https://example.org/watch?v=dQw4w9WgXcQ"))).Errors);
        Assert.NotEmpty(s.SaveInstance("youtube", null, "Vide", Values(s, "youtube")).Errors);
        Assert.NotEmpty(s.SaveInstance("youtube", null, "Hors plage", Values(s, "youtube", ("url", "https://youtu.be/dQw4w9WgXcQ"), ("maxMinutes", "999"))).Errors);
        var (item, errors) = s.SaveInstance("youtube", null, "Clip", Values(s, "youtube", ("url", "https://youtu.be/dQw4w9WgXcQ?t=5")));
        Assert.Empty(errors);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", item!.Settings["url"]);
    }

    [Fact]
    public async Task YouTube_content_duration_is_capped_by_the_maximum_and_positions_are_limited()
    {
        var s = ServiceFor(p.A);
        s.Install("youtube");
        var (clip, _) = s.SaveInstance("youtube", null, "Clip court", Values(s, "youtube", ("url", "https://youtu.be/dQw4w9WgXcQ"), ("maxMinutes", "5"), ("sound", "true"), ("startAt", "12")));
        var (long1, _) = s.SaveInstance("youtube", null, "Clip défaut", Values(s, "youtube", ("url", "https://www.youtube.com/playlist?list=PLabcdefghijklmnop")));
        var pl = Playlist(p.A, (clip!.Id, "full"), (long1!.Id, "top"));   // un bandeau n'est pas prévu pour YouTube
        await p.Services.GetRequiredService<Notifier>().Publish(pl);

        var a = Published(pl, 0);
        Assert.Equal("content", a.DurationMode);
        Assert.Equal(300, a.DurationSec);
        Assert.Equal("true", a.Settings["sound"]);
        Assert.Equal("12", a.Settings["startAt"]);
        Assert.Null(a.DataId);   // pas de données serveur : le player parle directement à YouTube
        var b = Published(pl, 1);
        Assert.Equal("full", b.Placement);
        Assert.Equal(1800, b.DurationSec);
        var json = JsonSerializer.Serialize(a.ForPlayer());
        Assert.Contains("\"durationMode\":\"content\"", json);
        Assert.Contains("watch?v=dQw4w9WgXcQ", json);
    }

    [Fact]
    public async Task Each_playlist_item_can_choose_until_the_end_or_a_fixed_duration()
    {
        var s = ServiceFor(p.A);
        s.Install("youtube");
        var (clip, _) = s.SaveInstance("youtube", null, "Clip item", Values(s, "youtube", ("url", "https://youtu.be/dQw4w9WgXcQ"), ("maxMinutes", "10")));
        // la même vidéo dans trois éléments : jusqu'à la fin (défaut), durée fixe de 45 s, et durée fixe dans un angle (permanent)
        var pl = Playlist(p.A, (clip!.Id, "full"), (clip.Id, "full"), (clip.Id, "top-left"));
        p.Services.GetRequiredService<JsonStore>().Write(db =>
        {
            var items = db.Playlists.First(x => x.Id == pl).Draft;
            items[1].DurationMode = "fixed"; items[1].DurationSec = 45;
            items[2].DurationMode = "fixed"; items[2].DurationSec = 45;
        });
        await p.Services.GetRequiredService<Notifier>().Publish(pl);

        var untilEnd = Published(pl, 0);
        Assert.Equal("content", untilEnd.DurationMode);
        Assert.Equal(600, untilEnd.DurationSec);        // plafond = durée maximale de l'instance
        var fixedItem = Published(pl, 1);
        Assert.Equal("fixed", fixedItem.DurationMode);
        Assert.Equal(45, fixedItem.DurationSec);        // le choix de l'élément l'emporte
        Assert.Contains("\"durationMode\":\"fixed\"", JsonSerializer.Serialize(fixedItem.ForPlayer()));
        // la même instance garde sa configuration : seul l'élément de playlist change
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", fixedItem.Settings["url"]);
    }

    [Fact]
    public async Task Fixed_duration_apps_keep_the_playlist_duration()
    {
        var s = ServiceFor(p.A);
        s.Install("clock");
        var (clock, _) = s.SaveInstance("clock", null, "Horloge durée", Values(s, "clock"));
        var pl = Playlist(p.A, (clock!.Id, "full"));
        await p.Services.GetRequiredService<Notifier>().Publish(pl);
        Assert.Equal("fixed", Published(pl).DurationMode);
        Assert.Equal(5, Published(pl).DurationSec);
    }

    [Fact]
    public void YouTube_oembed_answers_are_explained()
    {
        var ok = YouTubeProvider.Describe(200, "{\"title\":\"Mon clip\",\"author_name\":\"Une chaîne\"}", false);
        Assert.Contains("Vidéo trouvée", ok); Assert.Contains("Mon clip", ok); Assert.Contains("Une chaîne", ok);
        Assert.Contains("Playlist trouvée", YouTubeProvider.Describe(200, "{\"title\":\"Liste\"}", true));
        Assert.StartsWith("⚠", YouTubeProvider.Describe(401, "", false));   // intégration désactivée : avertissement, pas une erreur
        Assert.StartsWith("⚠", YouTubeProvider.Describe(403, "", false));
        Assert.Throws<InvalidOperationException>(() => YouTubeProvider.Describe(404, "", false));
        Assert.Throws<InvalidOperationException>(() => YouTubeProvider.Describe(500, "", false));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59, "0:59")]
    [InlineData(212, "3:32")]
    [InlineData(3600, "1:00:00")]
    [InlineData(3727, "1:02:07")]
    public void Durations_are_formatted_for_thumbnails(int seconds, string expected) => Assert.Equal(expected, Helpers.FormatDuration(seconds));

    [Fact]
    public void YouTube_thumbnail_badge_shows_the_duration_and_resets_when_the_link_changes()
    {
        var s = ServiceFor(p.A);
        s.Install("youtube");
        var (video, _) = s.SaveInstance("youtube", null, "Badge", Values(s, "youtube", ("url", "https://youtu.be/dQw4w9WgXcQ")));
        Assert.Null(Helpers.YoutubeBadge(video));   // durée pas encore lue

        s.SetInfo(video!.Id, "durationSec", "212");
        video = s.Instances("youtube").First(m => m.Id == video.Id);
        Assert.Equal("3:32", Helpers.YoutubeBadge(video));

        // même lien (autre durée maximale) : la durée connue reste valable ; autre lien : elle est effacée
        s.SaveInstance("youtube", video.Id, "Badge", Values(s, "youtube", ("url", "https://youtu.be/dQw4w9WgXcQ")));
        Assert.Equal("3:32", Helpers.YoutubeBadge(s.Instances("youtube").First(m => m.Id == video.Id)));
        s.SaveInstance("youtube", video.Id, "Badge", Values(s, "youtube", ("url", "https://youtu.be/jNQXAC9IVRw")));
        Assert.Null(Helpers.YoutubeBadge(s.Instances("youtube").First(m => m.Id == video.Id)));

        var (list, _) = s.SaveInstance("youtube", null, "Liste", Values(s, "youtube", ("url", "https://www.youtube.com/playlist?list=PLabcdefghijklmnop")));
        Assert.Equal("Playlist", Helpers.YoutubeBadge(list));
    }

    // ---------- Appels sortants (SSRF) ----------

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Only_public_addresses_are_reachable(string ip, bool expected) =>
        Assert.Equal(expected, SafeHttp.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("http://127.0.0.1:9/feed")]
    [InlineData("http://localhost:9/feed")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]:9/")]
    public async Task Outgoing_calls_never_reach_the_internal_network(string url)
    {
        var http = p.Services.GetRequiredService<SafeHttp>();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => http.GetString(url));
        Assert.Contains("non autorisée", ex.ToString());   // refusée par le contrôle d'adresse, pas par une simple connexion échouée
    }

    [Theory]
    [InlineData("file:///c:/windows/win.ini")]
    [InlineData("ftp://example.org/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@example.org/")]
    [InlineData("not a url")]
    public void Only_plain_http_urls_are_accepted(string url) =>
        Assert.Throws<InvalidOperationException>(() => SafeHttp.ParseUrl(url));

    // ---------- Flux RSS ----------

    [Fact]
    public void Rss_and_atom_feeds_are_parsed_and_cleaned()
    {
        var rss = RssProvider.Parse("""
            <?xml version="1.0"?><rss version="2.0"><channel><title>Le Journal</title>
              <item><title>Premier</title><link>https://example.org/1</link><description>&lt;p&gt;Un &lt;b&gt;résumé&lt;/b&gt; &amp;amp; plus&lt;/p&gt;</description><pubDate>Mon, 05 Oct 2026 10:00:00 GMT</pubDate></item>
              <item><title>Lien dangereux</title><link>javascript:alert(1)</link></item>
              <item><title></title></item>
            </channel></rss>
            """);
        Assert.Equal("Le Journal", rss.Title);
        Assert.Equal(2, rss.Items.Count);
        Assert.Equal("Un résumé & plus", rss.Items[0].Summary);
        Assert.Equal("https://example.org/1", rss.Items[0].Link);
        Assert.Equal(new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc), rss.Items[0].Published);
        Assert.Null(rss.Items[1].Link);

        var atom = RssProvider.Parse("""
            <feed xmlns="http://www.w3.org/2005/Atom"><title>Atome</title>
              <entry><title>Article</title><link href="https://example.org/a"/><summary>Texte</summary><updated>2026-10-05T08:00:00Z</updated></entry>
            </feed>
            """);
        Assert.Equal("Atome", atom.Title);
        Assert.Equal("https://example.org/a", atom.Items.Single().Link);
    }

    [Fact]
    public void Feeds_with_external_entities_or_other_content_are_refused()
    {
        Assert.ThrowsAny<Exception>(() => RssProvider.Parse("""
            <?xml version="1.0"?><!DOCTYPE rss [<!ENTITY x SYSTEM "file:///c:/windows/win.ini">]><rss><channel><title>&x;</title></channel></rss>
            """));
        Assert.ThrowsAny<Exception>(() => RssProvider.Parse("<html><body>pas un flux</body></html>"));
        Assert.ThrowsAny<Exception>(() => RssProvider.Parse("pas du xml"));
    }

    // ---------- Menus de restaurant (CSV et API) ----------

    static AppRuntime MenuRuntime(params (string, string)[] values) =>
        new(new Tenant { Timezone = "Europe/Zurich" }, values.ToDictionary(v => v.Item1, v => v.Item2));

    [Fact]
    public void Menu_json_accepts_lists_wrappers_and_days_with_their_dishes()
    {
        // liste simple : noms français ou anglais, accents et casse ignorés, prix numérique, allergènes en liste, dates 2026-10-12 ou 12.10.2026
        var flat = MenuJson.Parse("""
            [{"date":"2026-10-12T00:00:00Z","categorie":"Plat","plat":"Filet de perche","prix":12.5,"allergenes":["poisson","lait"]},
             {"Date":"12.10.2026","Catégorie":"Dessert","Nom":"Tarte","Prix":4}]
            """);
        Assert.Empty(flat.Errors);
        Assert.Equal(("2026-10-12", "Plat", "Filet de perche", "12.50", "poisson, lait"), (flat.Rows[0].Date, flat.Rows[0].Category, flat.Rows[0].Name, flat.Rows[0].Price, flat.Rows[0].Allergens));
        Assert.Equal(("Dessert", "Tarte", "4"), (flat.Rows[1].Category, flat.Rows[1].Name, flat.Rows[1].Price));

        // des jours qui contiennent leurs plats : la date du jour est héritée
        var days = MenuJson.Parse("""
            {"days":[{"date":"2026-10-12","dishes":[{"category":"Plat","name":"Risotto","price":"11.00"},{"category":"Dessert","name":"Flan"}]},
                     {"date":"2026-10-13","meals":[{"name":"Soupe"}]}]}
            """);
        Assert.Empty(days.Errors);
        Assert.Equal(new[] { "2026-10-12", "2026-10-12", "2026-10-13" }, days.Rows.Select(r => r.Date).ToArray());
        Assert.Equal("Risotto", days.Rows[0].Name);

        // chemin de la liste et noms de champs indiqués
        var custom = MenuJson.Parse("""{"data":{"menus":[{"d":"2026-10-14","t":"Curry","p":10}]}}""", "data.menus", new MenuMapping("d", "", "t", "p", ""));
        Assert.Equal(("2026-10-14", "Curry", "10"), (custom.Rows.Single().Date, custom.Rows.Single().Name, custom.Rows.Single().Price));
    }

    [Fact]
    public void Menu_json_reports_unreadable_entries_and_refuses_other_content()
    {
        var res = MenuJson.Parse("""[{"date":"n'importe quoi","plat":"X"},{"plat":"Y"},"texte",{"date":"2026-10-12"}]""");
        Assert.Empty(res.Rows);
        Assert.Equal(4, res.Errors.Count);
        Assert.Throws<InvalidOperationException>(() => MenuJson.Parse("pas du json"));
        Assert.Throws<InvalidOperationException>(() => MenuJson.Parse("""{"a":1}"""));
        Assert.Throws<InvalidOperationException>(() => MenuJson.Parse("""{"a":[]}""", "b.c"));
    }

    [Fact]
    public void Menu_payload_covers_today_and_the_next_week_and_can_hide_prices_and_allergens()
    {
        var rows = new[] { "2026-10-11", "2026-10-12", "2026-10-19", "2026-10-20" }
            .Select(d => new MenuRow { Date = d, Category = "Plat", Name = "Plat du " + d, Price = "12.00", Allergens = "Gluten" }).ToList();
        var now = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

        var json = JsonSerializer.Serialize(MenuApp.Payload(MenuRuntime(("title", "Cantine")), rows, now));
        Assert.Contains("\"kind\":\"menu\"", json);
        Assert.Contains("Cantine", json);
        Assert.DoesNotContain("2026-10-11", json);   // passé
        Assert.Contains("2026-10-19", json);
        Assert.DoesNotContain("2026-10-20", json);   // au-delà de 8 jours
        Assert.Contains("12.00", json);

        var hidden = JsonSerializer.Serialize(MenuApp.Payload(MenuRuntime(("showPrice", "false"), ("showAllergens", "false")), rows, now));
        Assert.DoesNotContain("12.00", hidden);
        Assert.DoesNotContain("Gluten", hidden);
    }

    [Fact]
    public async Task Menu_csv_app_keeps_the_imported_file_on_the_server_and_serves_the_next_days()
    {
        var s = ServiceFor(p.A);
        s.Install("menu-csv");
        var tomorrow = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var csv = $"date;categorie;plat;prix;allergenes\n{tomorrow};Plat;Émincé de volaille;12.00;Gluten\n";
        var (item, errors) = s.SaveInstance("menu-csv", null, "Cantine", Values(s, "menu-csv", ("csv", csv)));
        Assert.Empty(errors);

        // le fichier est un secret : chiffré, absent des paramètres, jamais envoyé à l'écran
        Assert.False(item!.Settings.ContainsKey("csv"));
        Assert.StartsWith("dp:", item.Secrets["csv"]);
        Assert.DoesNotContain("volaille", JsonSerializer.Serialize(item));
        var pl = Playlist(p.A, (item.Id, "full"));
        await p.Services.GetRequiredService<Notifier>().Publish(pl);
        Assert.DoesNotContain("volaille", JsonSerializer.Serialize(Published(pl).ForPlayer()));

        // le serveur relit le fichier enregistré (modification sans réimporter : il est conservé)
        var m = s.Find("menu-csv")!;
        var rt = new AppRuntime(p.A, s.RuntimeValues(m, Values(s, "menu-csv", ("title", "Midi")), item));
        var provider = new MenuCsvProvider(new SafeHttp());
        var data = JsonSerializer.Serialize(await provider.Fetch(rt));
        Assert.Contains("volaille", data);
        Assert.Contains(tomorrow, data);
        Assert.Contains("à venir", await provider.Test(rt));
    }

    [Fact]
    public async Task Menu_csv_test_warns_when_nothing_is_left_to_display()
    {
        var csv = "date;plat\n2020-01-01;Soupe\n";
        var msg = await new MenuCsvProvider(new SafeHttp()).Test(MenuRuntime(("source", "file"), ("csv", csv)));
        Assert.StartsWith("⚠", msg);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MenuCsvProvider(new SafeHttp()).Test(MenuRuntime(("source", "file"), ("csv", "colonne;autre\n1;2\n"))));
    }

    [Fact]
    public async Task Menu_api_refuses_internal_addresses_and_unsafe_header_names()
    {
        var api = new MenuApiProvider(new SafeHttp());
        await Assert.ThrowsAnyAsync<Exception>(() => api.Test(MenuRuntime(("url", "http://127.0.0.1:1/menus"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.Test(MenuRuntime(("url", "https://api.example.org/menus"), ("auth", "header"), ("headerName", "X Bad\r\nHeader"), ("token", "k"))));
    }

    // ---------- Back-office ----------

    [Fact]
    public async Task Apps_pages_require_a_client_session()
    {
        var c = p.Client();
        foreach (var path in new[] { "/apps", "/apps/clock", "/apps/clock/new", "/apps/instance/" + Guid.NewGuid() })
        {
            var r = await c.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/login", r.Headers.Location!.ToString());
        }
    }
}
