using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Intégration Facebook : lecture de l'adresse de la Page, de la réponse de l'API Graph et de ses erreurs (sans appel réseau).</summary>
public class FacebookTests
{
    [Theory]
    [InlineData("CommuneDeLausanne", "CommuneDeLausanne")]
    [InlineData("@CommuneDeLausanne", "CommuneDeLausanne")]
    [InlineData("123456789012345", "123456789012345")]
    [InlineData("https://www.facebook.com/CommuneDeLausanne", "CommuneDeLausanne")]
    [InlineData("https://www.facebook.com/CommuneDeLausanne/?ref=page_internal", "CommuneDeLausanne")]
    [InlineData("facebook.com/CommuneDeLausanne/posts/123", "CommuneDeLausanne")]
    [InlineData("https://m.facebook.com/Nom.De.Page", "Nom.De.Page")]
    [InlineData("https://www.facebook.com/profile.php?id=100012345678901", "100012345678901")]
    [InlineData("https://www.facebook.com/pages/Ma-Page/109876543210", "109876543210")]
    public void Page_reference_is_read_from_what_the_client_pastes(string input, string expected) => Assert.Equal(expected, FacebookProvider.PageRef(input));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ab")]
    [InlineData("https://evilfacebook.com/Page")]
    [InlineData("https://facebook.com.evil.org/Page")]
    [InlineData("https://example.org/Page")]
    [InlineData("https://www.facebook.com/")]
    [InlineData("https://www.facebook.com/pages/Ma-Page")]
    [InlineData("Page/../../me/accounts")]
    [InlineData("Page?fields=id")]
    [InlineData("Ma Page")]
    public void Page_reference_rejects_anything_that_could_leave_the_page_path(string input) => Assert.Null(FacebookProvider.PageRef(input));

    [Fact]
    public void Posts_are_read_from_the_graph_response()
    {
        var page = FacebookProvider.Parse("""
            { "data": [
              { "id": "1_10", "message": "Fête de quartier\n\nsamedi   14 h", "created_time": "2026-10-12T10:30:00+0000", "full_picture": "https://scontent.xx.fbcdn.net/v/a.jpg?x=1&y=2", "from": { "name": "Commune de Lausanne", "id": "1" } },
              { "id": "1_11", "created_time": "2026-10-11T08:00:00+0000", "full_picture": "http://insecure.example.org/b.jpg" },
              { "id": "1_12", "message": "Travaux rue du Midi" },
              { "id": "1_13", "created_time": "2026-10-10T08:00:00+0000" }
            ] }
            """);
        Assert.Equal("Commune de Lausanne", page.Name);
        Assert.Equal(new[] { "1_10", "1_12" }, page.Posts.Select(p => p.Id));   // la publication sans texte ni photo https est ignorée
        Assert.Equal("Fête de quartier samedi 14 h", page.Posts[0].Message);
        Assert.Equal(new DateTime(2026, 10, 12, 10, 30, 0, DateTimeKind.Utc), page.Posts[0].Created);
        Assert.Equal("https://scontent.xx.fbcdn.net/v/a.jpg?x=1&y=2", page.Posts[0].Picture);
        Assert.Null(page.Posts[1].Picture);
        Assert.Null(page.Posts[1].Created);
    }

    [Fact]
    public void Long_posts_are_shortened_and_an_empty_page_has_a_default_name()
    {
        var page = FacebookProvider.Parse("{ \"data\": [ { \"id\": \"1\", \"message\": \"" + new string('a', 900) + "\" } ] }");
        Assert.Equal("Facebook", page.Name);
        Assert.Equal(400, page.Posts[0].Message!.Length);
        Assert.EndsWith("…", page.Posts[0].Message);
        Assert.Empty(FacebookProvider.Parse("{ \"data\": [] }").Posts);
        Assert.Empty(FacebookProvider.Parse("{}").Posts);
    }

    [Theory]
    [InlineData(400, """{"error":{"message":"Error validating access token","type":"OAuthException","code":190}}""", "expiré")]
    [InlineData(403, """{"error":{"message":"(#200) Requires pages_read_engagement","code":200}}""", "autorisation")]
    [InlineData(400, """{"error":{"message":"Unsupported get request","code":100}}""", "introuvable")]
    [InlineData(400, """{"error":{"message":"Calls to this api have exceeded the rate limit","code":4}}""", "limite")]
    [InlineData(502, "<html>bad gateway</html>", "HTTP 502")]
    public void Graph_errors_are_explained_without_echoing_the_raw_message(int status, string body, string expected)
    {
        var text = FacebookProvider.Describe(status, body);
        Assert.Contains(expected, text);
        Assert.DoesNotContain("OAuthException", text);
    }
}
