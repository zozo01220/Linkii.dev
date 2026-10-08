using Linkii.Poc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>App LinkedIn (V2) : lecture des réponses de l'API, jeton, et mise à jour des instantanés publiés. Aucun appel réseau.</summary>
[Collection("platform")]
public class LinkedinTests(Platform p)
{
    const string PostsJson = """
    { "elements": [
      { "id": "urn:li:share:7513248200611332097", "lifecycleState": "PUBLISHED", "visibility": "PUBLIC", "publishedAt": 1791300000000,
        "distribution": { "feedDistribution": "MAIN_FEED" },
        "commentary": "Drogue : comprendre un marché\nAvec @[Pierre Esseiva](urn:li:person:abc) {hashtag|\\#|unil}",
        "content": { "media": { "id": "urn:li:image:C4E10AQFn10iWtKexVA" } } },
      { "id": "urn:li:ugcPost:7395036066262556672", "lifecycleState": "PUBLISHED", "visibility": "PUBLIC", "publishedAt": 1791200000000,
        "distribution": { "feedDistribution": "MAIN_FEED" }, "commentary": "",
        "content": { "article": { "title": "Rapport annuel", "source": "https://exemple.ch" } } },
      { "id": "urn:li:share:1000000000000000001", "lifecycleState": "PUBLISHED", "visibility": "PUBLIC", "distribution": { "feedDistribution": "NONE" }, "commentary": "publicité" },
      { "id": "urn:li:share:1000000000000000002", "lifecycleState": "DRAFT", "visibility": "PUBLIC", "commentary": "brouillon" },
      { "id": "urn:li:share:1000000000000000003", "lifecycleState": "PUBLISHED", "visibility": "CONNECTIONS", "commentary": "privée" },
      { "id": "urn:li:autre:1", "lifecycleState": "PUBLISHED", "visibility": "PUBLIC", "commentary": "identifiant inattendu" }
    ] }
    """;

    [Fact]
    public void Posts_are_read_and_only_public_published_feed_posts_are_kept()
    {
        var posts = LinkedinService.ParsePosts(PostsJson);
        Assert.Equal(new[] { "urn:li:share:7513248200611332097", "urn:li:ugcPost:7395036066262556672" }, posts.Select(x => x.Id).ToArray());
        Assert.Equal("urn:li:image:C4E10AQFn10iWtKexVA", posts[0].ImageUrn);
        Assert.Equal("Rapport annuel", posts[1].ArticleTitle);
        Assert.Equal("https://www.linkedin.com/feed/update/urn:li:share:7513248200611332097/", posts[0].Url);
        Assert.NotNull(posts[0].PublishedUtc);
    }

    [Fact]
    public void Post_text_is_cleaned_and_split_into_title_and_body()
    {
        var first = LinkedinService.ParsePosts(PostsJson)[0];
        Assert.Contains("Pierre Esseiva #unil", first.Text);
        Assert.DoesNotContain("urn:li:person", first.Text);
        var (title, body) = LinkedinProvider.Split(first);
        Assert.Equal("Drogue : comprendre un marché", title);
        Assert.StartsWith("Avec Pierre Esseiva", body);

        var article = LinkedinProvider.Split(LinkedinService.ParsePosts(PostsJson)[1]);   // texte vide : titre de l'article
        Assert.Equal("Rapport annuel", article.Title);

        var long160 = LinkedinProvider.Split(new LinkedinPost("urn:li:share:1000000000000000009", new string('x', 300), null, null, null));
        Assert.True(long160.Title.Length <= 161);
        Assert.NotEmpty(long160.Body);
    }

    [Fact]
    public void Token_and_organizations_are_parsed()
    {
        var now = new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);
        var t = LinkedinService.ParseToken("""{"access_token":"AQX","expires_in":5184000}""", now);
        Assert.Equal("AQX", t.Access);
        Assert.Equal(now.AddDays(60), t.ExpiresUtc);
        Assert.Throws<InvalidOperationException>(() => LinkedinService.ParseToken("""{"error":"x"}""", now));

        var urns = LinkedinService.ParseOrganizationUrns("""{"elements":[{"organization":"urn:li:organization:123"},{"organizationTarget":"urn:li:organization:456"},{"organization":"urn:li:organization:123"},{"organization":"pas-un-urn"}]}""");
        Assert.Equal(new[] { "urn:li:organization:123", "urn:li:organization:456" }, urns);

        var round = LinkedinService.ReadAuth(LinkedinService.AuthJson(t));
        Assert.Equal(t.Access, round!.Access);
        Assert.Equal(t.ExpiresUtc, round.ExpiresUtc);
        Assert.Null(LinkedinService.ReadAuth("pas du json"));
        Assert.True(new LinkedinToken("x", DateTime.UtcNow.AddMinutes(-1)).Expired);
    }

    [Fact]
    public void Stored_token_follows_the_published_snapshots_and_stays_within_its_client()
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        var media = new MediaItem { ClientId = p.A.Id, Type = "app", AppId = "linkedin", Name = "LinkedIn" };
        var pl = new Playlist { ClientId = p.A.Id, Name = "li-test" };
        pl.Published.Add(new PublishedItem { Id = Guid.NewGuid(), Type = "app", AppId = "linkedin", MediaId = media.Id });
        store.Write(db => { db.Media.Add(media); db.Playlists.Add(pl); });

        Assert.False(LinkedinService.StoreAuth(store, p.B.Id, media.Id, "dp:x"));   // un autre client ne peut pas écrire
        Assert.True(LinkedinService.StoreAuth(store, p.A.Id, media.Id, "dp:abc"));
        Assert.Equal("dp:abc", store.Read(db => db.Media.First(m => m.Id == media.Id).Secrets[LinkedinService.AuthKey]));
        Assert.Equal("dp:abc", store.Read(db => db.Playlists.First(x => x.Id == pl.Id).Published[0].Secrets[LinkedinService.AuthKey]));

        Assert.True(LinkedinService.StoreAuth(store, p.A.Id, media.Id, null));   // déconnexion
        Assert.False(store.Read(db => db.Media.First(m => m.Id == media.Id).Secrets.ContainsKey(LinkedinService.AuthKey)));
        Assert.False(store.Read(db => db.Playlists.First(x => x.Id == pl.Id).Published[0].Secrets.ContainsKey(LinkedinService.AuthKey)));
    }

    [Fact]
    public void Manifest_switches_between_chosen_posts_and_page_mode()
    {
        var catalog = p.Services.GetRequiredService<AppCatalog>();
        var m = catalog.Find("linkedin")!;
        Assert.Equal("linkedin", m.Provider);
        var picked = AppCatalog.WithDefaults(m, null);
        Assert.Contains(AppCatalog.Validate(m, picked, new HashSet<string>()), e => e.Contains("Publications"));   // mode par défaut : liens à saisir
        picked["source"] = "page";
        Assert.Empty(AppCatalog.Validate(m, picked, new HashSet<string>()));   // mode page : rien à saisir ici, la connexion se fait dans le panneau
    }

    [Fact]
    public void Client_enters_their_own_linkedin_app_in_the_settings_and_the_secret_stays_encrypted()
    {
        var li = p.Services.GetRequiredService<LinkedinService>();
        var box = p.Services.GetRequiredService<SecretBox>();
        var none = new Dictionary<string, string>();
        Assert.Null(li.AppFor(none, none, box));   // rien saisi, rien configuré sur la plateforme

        var settings = new Dictionary<string, string> { ["clientId"] = " 86abc " };
        var secrets = new Dictionary<string, string> { ["clientSecret"] = box.Protect("s3cret") };
        var app = li.AppFor(settings, secrets, box)!;
        Assert.Equal("86abc", app.ClientId);
        Assert.Equal("s3cret", app.ClientSecret);
        Assert.DoesNotContain("s3cret", secrets["clientSecret"]);

        var url = li.AuthUrl("st", "https://exemple.ch/linkedin/callback", app);
        Assert.Contains("client_id=86abc", url);
        Assert.Contains("redirect_uri=https%3A%2F%2Fexemple.ch%2Flinkedin%2Fcallback", url);
        Assert.DoesNotContain("s3cret", url);   // la clé secrète ne passe jamais dans l adresse

        var m = p.Services.GetRequiredService<AppCatalog>().Find("linkedin")!;
        Assert.Contains(m.Settings, f => f.Key == "clientSecret" && f.IsSecret);   // saisie dans le formulaire, chiffrée à l enregistrement
    }
}
