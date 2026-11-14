using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Application LinkedIn (§255) : lecture des réponses de l'API (pages, publications, réactions, jetons), validation, ancienneté et rappels, sans appel réseau.</summary>
public class LinkedinTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);
    private static long Ms(DateTime d) => new DateTimeOffset(d).ToUnixTimeMilliseconds();

    [Fact]
    public void Pages_keep_the_strongest_role_and_only_admins_and_content_editors_can_read()
    {
        var pages = LinkedinAuth.ParseAcls("""
            { "elements": [
              { "organization": "urn:li:organization:111", "role": "ANALYST", "state": "APPROVED" },
              { "organization": "urn:li:organization:111", "role": "ADMINISTRATOR", "state": "APPROVED" },
              { "organization": "urn:li:organization:222", "role": "CONTENT_ADMINISTRATOR", "state": "APPROVED" },
              { "organization": "urn:li:organization:333", "role": "ANALYST", "state": "APPROVED" },
              { "organization": "urn:li:organization:444", "role": "ADMINISTRATOR", "state": "REQUESTED" },
              { "organization": "urn:li:person:555", "role": "ADMINISTRATOR", "state": "APPROVED" },
              { "organizationTarget": "urn:li:organization:666", "role": "ADMINISTRATOR" }
            ] }
            """);
        Assert.Equal(["urn:li:organization:111", "urn:li:organization:222", "urn:li:organization:333", "urn:li:organization:666"], pages.Select(p => p.Urn));
        Assert.Equal("ADMINISTRATOR", pages[0].Role);
        Assert.Equal([true, true, false, true], pages.Select(p => p.CanRead));
        Assert.Equal("Éditeur de contenu", LinkedinAuth.RoleLabel(pages[1].Role));
    }

    [Fact]
    public void Reconnecting_keeps_choices_and_validations_of_known_pages()
    {
        var known = new List<LinkedinPageAccess> { new() { Urn = "urn:li:organization:1", Name = "Ancien nom", Off = true, Approved = ["urn:li:share:9"], Hidden = ["urn:li:share:8"] } };
        var merged = LinkedinAuth.Merge(known, [new() { Urn = "urn:li:organization:1", Name = "Nouveau nom", Role = "ADMINISTRATOR", CanRead = true }, new() { Urn = "urn:li:organization:2", Name = "Autre" }]);
        Assert.Equal("Nouveau nom", merged[0].Name);
        Assert.True(merged[0].Off);
        Assert.Equal(["urn:li:share:9"], merged[0].Approved);
        Assert.Equal(["urn:li:share:8"], merged[0].Hidden);
        Assert.False(merged[1].Off);
    }

    [Fact]
    public void Token_reads_lifetime_and_optional_refresh_token()
    {
        var t = LinkedinAuth.ParseToken("""{ "access_token": "AQX", "expires_in": 5184000, "refresh_token": "AQR", "refresh_token_expires_in": 31536000 }""", Now);
        Assert.Equal("AQX", t.Access);
        Assert.Equal(Now.AddDays(60), t.Expires);
        Assert.Equal("AQR", t.Refresh);
        Assert.Equal(Now.AddDays(365), t.RefreshExpires);
        var plain = LinkedinAuth.ParseToken("""{ "access_token": "AQX", "expires_in": 5184000 }""", Now);
        Assert.Null(plain.Refresh);
        Assert.Throws<LinkedinAuth.LinkedinAuthException>(() => LinkedinAuth.ParseToken("""{ "error": "invalid_grant" }""", Now));
    }

    [Fact]
    public void Posts_skip_reshares_drafts_dark_posts_and_keep_images_and_videos()
    {
        var json = $$"""
            { "elements": [
              { "id": "urn:li:share:1", "commentary": "Bienvenue à @[Léa Martin](urn:li:person:abc) ! {hashtag|\\#|bienvenue}", "lifecycleState": "PUBLISHED", "visibility": "PUBLIC",
                "publishedAt": {{Ms(Now.AddDays(-5))}}, "content": { "media": { "id": "urn:li:image:C4D22AQ" } } },
              { "id": "urn:li:ugcPost:2", "commentary": "Retour en vidéo", "lifecycleState": "PUBLISHED", "visibility": "PUBLIC",
                "publishedAt": {{Ms(Now.AddDays(-1))}}, "content": { "media": { "id": "urn:li:video:D5E33BR" } } },
              { "id": "urn:li:share:3", "commentary": "Partage d'une autre page", "lifecycleState": "PUBLISHED", "visibility": "PUBLIC",
                "publishedAt": {{Ms(Now)}}, "reshareContext": { "parent": "urn:li:share:99" } },
              { "id": "urn:li:share:4", "commentary": "Brouillon", "lifecycleState": "DRAFT", "visibility": "PUBLIC" },
              { "id": "urn:li:share:5", "commentary": "Publicité", "lifecycleState": "PUBLISHED", "visibility": "PUBLIC", "distribution": { "feedDistribution": "NONE" } },
              { "id": "urn:li:share:6", "lifecycleState": "PUBLISHED", "visibility": "PUBLIC",
                "publishedAt": {{Ms(Now.AddDays(-3))}}, "content": { "multiImage": { "images": [ { "id": "urn:li:image:A1" }, { "id": "urn:li:image:A2" } ] } } },
              { "id": "urn:li:share:7", "commentary": "Réservé aux membres", "lifecycleState": "PUBLISHED", "visibility": "CONNECTIONS" },
              { "id": "../../me", "commentary": "Identifiant inattendu" }
            ] }
            """;
        var posts = LinkedinProvider.ParsePosts(json);
        Assert.Equal(["urn:li:ugcPost:2", "urn:li:share:6", "urn:li:share:1"], posts.Select(p => p.Id));   // plus récentes d'abord
        Assert.Equal("urn:li:video:D5E33BR", posts[0].VideoUrn);
        Assert.Equal("urn:li:image:A1", posts[1].ImageUrn);
        Assert.Equal("", posts[1].Text);
        Assert.Equal("Bienvenue à Léa Martin ! #bienvenue", posts[2].Text);
        Assert.Equal("https://www.linkedin.com/feed/update/urn:li:share:1/", posts[2].Url);
    }

    [Fact]
    public void Reaction_and_comment_counts_are_read()
    {
        Assert.Equal((248, 31), LinkedinProvider.ParseCounts("""{ "likesSummary": { "totalLikes": 248 }, "commentsSummary": { "aggregatedTotalComments": 31, "totalFirstLevelComments": 20 } }"""));
        Assert.Equal((5, 2), LinkedinProvider.ParseCounts("""{ "likesSummary": { "totalLikes": 5 }, "commentsSummary": { "totalFirstLevelComments": 2 } }"""));
        Assert.Equal(((int?)null, (int?)null), LinkedinProvider.ParseCounts("pas du json"));
    }

    private static Tenant TenantWith(params LinkedinPost[] posts)
    {
        var t = new Tenant { LinkedinPages = [new LinkedinPageAccess { Urn = "urn:li:organization:1", Name = "Atelier Rivage", Role = "ADMINISTRATOR", CanRead = true }] };
        return t;
    }

    private static LinkedinFeedPost Post(string id, DateTime created) => new(new LinkedinPost(id, "Texte", created, null, null, null), null, null, 3, 1);

    [Fact]
    public void New_posts_wait_for_approval_and_masked_posts_never_show()
    {
        var t = TenantWith();
        const string urn = "urn:li:organization:1";
        var posts = new[] { Post("urn:li:share:1", Now.AddDays(-1)), Post("urn:li:share:2", Now.AddDays(-2)), Post("urn:li:share:3", Now.AddDays(-3)) };
        Assert.Empty(LinkedinProvider.Visible(t, urn, posts, 10, Now));   // validation activée par défaut

        LinkedinAuth.SetState(t, urn, "urn:li:share:2", LinkedinAuth.Shown);
        LinkedinAuth.SetState(t, urn, "urn:li:share:3", LinkedinAuth.Masked);
        Assert.Equal(["urn:li:share:2"], LinkedinProvider.Visible(t, urn, posts, 10, Now).Select(p => p.Post.Id));
        Assert.Equal(LinkedinAuth.Waiting, LinkedinAuth.StateOf(t, t.LinkedinPages[0], "urn:li:share:1"));

        t.LinkedinApproval = false;   // sans validation : tout passe, sauf les publications masquées
        Assert.Equal(["urn:li:share:1", "urn:li:share:2"], LinkedinProvider.Visible(t, urn, posts, 10, Now).Select(p => p.Post.Id));

        LinkedinAuth.SetState(t, urn, "urn:li:share:3", LinkedinAuth.Shown);   // réafficher une publication masquée
        Assert.Equal(3, LinkedinProvider.Visible(t, urn, posts, 10, Now).Count);
        Assert.Equal(2, LinkedinProvider.Visible(t, urn, posts, 2, Now).Count);
    }

    [Fact]
    public void Posts_older_than_the_maximum_age_leave_the_screens()
    {
        var t = TenantWith();
        t.LinkedinApproval = false;
        var posts = new[] { Post("urn:li:share:1", Now.AddDays(-2)), Post("urn:li:share:2", Now.AddDays(-40)) };
        Assert.Single(LinkedinProvider.Visible(t, "urn:li:organization:1", posts, 10, Now));
        t.LinkedinMaxAgeDays = 0;   // sans limite
        Assert.Equal(2, LinkedinProvider.Visible(t, "urn:li:organization:1", posts, 10, Now).Count);
    }

    [Fact]
    public void Unknown_ids_are_never_stored()
    {
        var t = TenantWith();
        LinkedinAuth.SetState(t, "urn:li:organization:1", "../../etc", LinkedinAuth.Shown);
        LinkedinAuth.SetState(t, "urn:li:organization:9", "urn:li:share:1", LinkedinAuth.Shown);
        Assert.Empty(t.LinkedinPages[0].Approved);
    }

    [Theory]
    [InlineData(30, null)]
    [InlineData(8, null)]
    [InlineData(7, 7)]
    [InlineData(2, 7)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    [InlineData(-3, 0)]
    public void Expiry_reminders_come_a_week_and_a_day_before(int days, int? step) => Assert.Equal(step, LinkedinWorker.Step(days));

    [Fact]
    public void Connection_is_usable_only_until_its_expiry()
    {
        var t = new Tenant { LinkedinConnectedUtc = Now.AddDays(-10), LinkedinToken = "enc", LinkedinExpiresUtc = DateTime.UtcNow.AddDays(3) };
        Assert.True(LinkedinAuth.Has(t));
        t.LinkedinExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
        Assert.False(LinkedinAuth.Has(t));
        Assert.Equal(-1, LinkedinAuth.DaysLeft(t, DateTime.UtcNow));
        t.LinkedinExpiresUtc = DateTime.UtcNow.AddDays(3); t.LinkedinLostUtc = DateTime.UtcNow;
        Assert.False(LinkedinAuth.Has(t));
    }

    [Theory]
    [InlineData("Atelier Rivage", "AR")]
    [InlineData("Atelier Rivage – Carrières", "AR")]
    [InlineData("unil", "U")]
    [InlineData("", "in")]
    public void Page_initials(string name, string expected) => Assert.Equal(expected, LinkedinProvider.Initials(name));

    [Fact]
    public void Manifest_is_back_in_the_catalog_and_validates_the_page()
    {
        var catalog = new AppCatalog(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Linkii.Poc", "Apps"), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var app = catalog.Find("linkedin");
        Assert.NotNull(app);
        Assert.Equal(["full"], app!.Placements);
        Assert.Equal("content", app.Duration);
        Assert.DoesNotContain("linkedin", Seed.RetiredApps);
        var ok = AppCatalog.Validate(app, new Dictionary<string, string> { ["page"] = "urn:li:organization:42", ["layout"] = "one", ["count"] = "5", ["interval"] = "12", ["qr"] = "true", ["posts"] = "" }, new HashSet<string>());
        Assert.Empty(ok);
        var bad = AppCatalog.Validate(app, new Dictionary<string, string> { ["page"] = "https://www.linkedin.com/company/x", ["layout"] = "one", ["count"] = "5", ["interval"] = "12", ["qr"] = "true", ["posts"] = "" }, new HashSet<string>());
        Assert.NotEmpty(bad);
    }

    [Fact]
    public void Media_copies_are_named_after_their_organisation()
    {
        var client = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var file = $"{client:N}-{LinkedinMedia.Key("urn:li:image:A1")}.jpg";
        Assert.Matches(LinkedinMedia.FilePattern, file);
        Assert.Equal(client, LinkedinMedia.OwnerOf(file));
        Assert.Null(LinkedinMedia.OwnerOf("../" + file));
        Assert.Null(LinkedinMedia.OwnerOf($"{client:N}-{LinkedinMedia.Key("x")}.exe"));
    }
}
