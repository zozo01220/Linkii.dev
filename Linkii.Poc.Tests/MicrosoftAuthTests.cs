using Xunit;
using System.Web;
using Linkii.Poc;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Linkii.Poc.Tests;

/// <summary>« Se connecter avec Microsoft » : adresse de consentement, retours d'erreur, accès accordés et déconnexion (sans appel réseau).</summary>
public class MicrosoftAuthTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "linkii-ms-" + Guid.NewGuid().ToString("N"));
    private readonly JsonStore store;
    private readonly Tenant tenant = new() { Name = "Client" };

    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Linkii";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public MicrosoftAuthTests()
    {
        store = new JsonStore(Config(), new Env());
        store.Write(db => db.Clients.Add(tenant));
    }

    private IConfiguration Config(string? redirect = null) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Linkii:DataDir"] = dir,
        ["Linkii:Microsoft:ClientId"] = "app-id",
        ["Linkii:Microsoft:ClientSecret"] = "secret",
        ["Linkii:Microsoft:RedirectUri"] = redirect
    }).Build();

    private MicrosoftAuth Auth(IConfiguration? config = null) =>
        new(config ?? Config(), store, new SecretBox(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir, "keys")))),
            new TestHttpFactory(), NullLogger<MicrosoftAuth>.Instance);

    private sealed class TestHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    private static System.Collections.Specialized.NameValueCollection Query(string url) => HttpUtility.ParseQueryString(new Uri(url).Query);

    [Fact]
    public void Consent_url_asks_read_only_calendar_access_with_pkce()
    {
        var url = Auth().StartAuthorization(tenant.Id, "calendar", "https://app.test/integrations", "https://app.test/");
        Assert.StartsWith("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?", url);   // comptes personnels acceptés
        var q = Query(url);
        Assert.Equal("app-id", q["client_id"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("https://app.test/microsoft/callback", q["redirect_uri"]);
        var scopes = q["scope"]!.Split(' ');
        Assert.Contains("https://graph.microsoft.com/Calendars.Read", scopes);
        Assert.Contains("offline_access", scopes);
        Assert.DoesNotContain(scopes, s => s.Contains("Files."));
        Assert.DoesNotContain(scopes, s => s.Contains("ReadWrite"));
    }

    [Theory]
    [InlineData("calendar")]
    [InlineData("drive")]
    public void Consent_url_asks_only_scopes_personal_accounts_support(string purpose)
    {
        // Place.Read.All et Sites.Read.All n'existent pas pour les comptes outlook.com : les demander ferait refuser la connexion
        var scopes = Query(Auth().StartAuthorization(tenant.Id, purpose, "https://app.test/integrations", "https://app.test"))["scope"]!.Split(' ');
        Assert.DoesNotContain(scopes, s => s.Contains("Place.") || s.Contains("Sites."));
    }

    [Fact]
    public void Consent_url_for_drive_asks_files_not_calendars()
    {
        var scopes = Query(Auth().StartAuthorization(tenant.Id, "drive", "https://app.test/integrations", "https://app.test")) ["scope"]!.Split(' ');
        Assert.Contains("https://graph.microsoft.com/Files.Read.All", scopes);
        Assert.DoesNotContain(scopes, s => s.Contains("Calendars."));
    }

    [Fact]
    public void Configured_redirect_uri_wins_over_request_address()
    {
        var auth = Auth(Config("https://linkii.example/microsoft/callback"));
        var q = Query(auth.StartAuthorization(tenant.Id, "drive", "https://app.test/integrations", "https://app.test"));
        Assert.Equal("https://linkii.example/microsoft/callback", q["redirect_uri"]);
    }

    [Fact]
    public void Not_configured_without_client_id_and_secret()
    {
        var empty = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Linkii:DataDir"] = dir }).Build();
        Assert.False(Auth(empty).IsConfigured);
        Assert.True(Auth().IsConfigured);
    }

    [Fact]
    public void Has_needs_a_refresh_token_and_the_scope()
    {
        var t = new Tenant { MsScopes = "Calendars.Read User.Read" };
        Assert.False(MicrosoftAuth.Has(t, MicrosoftAuth.CalendarScope));   // pas de jeton : pas connecté
        t.MsRefreshToken = "x";
        Assert.True(MicrosoftAuth.Has(t, MicrosoftAuth.CalendarScope));
        Assert.False(MicrosoftAuth.Has(t, MicrosoftAuth.DriveScope));
    }

    [Theory]
    [InlineData("access_denied", "AADSTS65001: The user or administrator has not consented", true)]
    [InlineData("access_denied", "AADSTS90094: admin consent required", true)]
    [InlineData("consent_required", null, true)]
    [InlineData("access_denied", "AADSTS65004: The user declined to consent", false)]
    [InlineData("access_denied", null, false)]
    public void Admin_approval_is_told_apart_from_a_refusal(string error, string? description, bool admin) =>
        Assert.Equal(admin, MicrosoftAuth.NeedsAdmin(error, description));

    [Fact]
    public async Task Return_with_unknown_state_is_expired()
    {
        Assert.Equal("/integrations?microsoft=expired", await Auth().CompleteAsync("inconnu", "code", null));
        Assert.Equal("/integrations?microsoft=expired", await Auth().CompleteAsync(null, null, null));
    }

    [Theory]
    [InlineData("AADSTS65001: not consented", "admin")]
    [InlineData("AADSTS65004: declined", "denied")]
    public async Task Return_with_error_tells_admin_from_denied(string description, string expected)
    {
        var auth = Auth();
        var state = Query(auth.StartAuthorization(tenant.Id, "calendar", "https://app.test/integrations", "https://app.test"))["state"];
        var back = await auth.CompleteAsync(state, null, "access_denied", description);
        Assert.Equal($"https://app.test/integrations?for=calendar&microsoft={expected}", back);
    }

    [Fact]
    public async Task State_is_single_use()
    {
        var auth = Auth();
        var state = Query(auth.StartAuthorization(tenant.Id, "drive", "https://app.test/integrations", "https://app.test"))["state"];
        await auth.CompleteAsync(state, null, "access_denied");
        Assert.Equal("/integrations?microsoft=expired", await auth.CompleteAsync(state, null, "access_denied"));
    }

    [Fact]
    public void Disconnect_forgets_the_account_for_both_integrations()
    {
        store.Write(db =>
        {
            var c = db.Clients.First(x => x.Id == tenant.Id);
            c.MsRefreshToken = "chiffré"; c.MsScopes = "Calendars.Read Files.Read.All"; c.MsUser = "marie@entreprise.ch"; c.MsConnectedUtc = DateTime.UtcNow;
        });
        Auth().Disconnect(tenant.Id);
        var c = store.Read(db => db.Clients.First(x => x.Id == tenant.Id));
        Assert.False(MicrosoftAuth.ConnectionOf(c).Connected);
        Assert.Equal("", c.MsScopes);
        Assert.False(MicrosoftAuth.Has(c, MicrosoftAuth.CalendarScope));
        Assert.False(MicrosoftAuth.Has(c, MicrosoftAuth.DriveScope));
    }

    [Fact]
    public async Task Access_token_without_connected_account_asks_to_connect()
    {
        var ex = await Assert.ThrowsAsync<MicrosoftAuth.MicrosoftAuthException>(() => Auth().AccessToken(tenant));
        Assert.Contains("Aucun compte Microsoft connecté", ex.Message);
    }
}
