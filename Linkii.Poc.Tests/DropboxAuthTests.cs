using System.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>« Se connecter avec Dropbox » : adresse de consentement et retours d'erreur (sans appel réseau).</summary>
public class DropboxAuthTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "linkii-dbx-" + Guid.NewGuid().ToString("N"));
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

    public DropboxAuthTests()
    {
        store = new JsonStore(Config(true), new Env());
        store.Write(db => db.Clients.Add(tenant));
    }

    private IConfiguration Config(bool configured, string? redirect = null) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Linkii:DataDir"] = dir,
        ["Linkii:Dropbox:AppKey"] = configured ? "app-key" : null,
        ["Linkii:Dropbox:AppSecret"] = configured ? "app-secret" : null,
        ["Linkii:Dropbox:RedirectUri"] = redirect
    }).Build();

    private DropboxAuth Auth(bool configured = true, string? redirect = null) =>
        new(Config(configured, redirect), store, new SecretBox(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir, "keys")))),
            new SafeHttp(), NullLogger<DropboxAuth>.Instance);

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    [Fact]
    public void Sans_cle_ni_secret_la_connexion_n_est_pas_proposee()
    {
        Assert.False(Auth(configured: false).IsConfigured);
        Assert.True(Auth().IsConfigured);
    }

    [Fact]
    public void L_adresse_de_consentement_demande_la_lecture_hors_ligne_avec_PKCE()
    {
        var url = new Uri(Auth().StartAuthorization(tenant.Id, "http://localhost:5080/integrations", "http://localhost:5080"));
        var q = HttpUtility.ParseQueryString(url.Query);

        Assert.Equal("www.dropbox.com", url.Host);
        Assert.Equal("app-key", q["client_id"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("offline", q["token_access_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("http://localhost:5080/dropbox/callback", q["redirect_uri"]);
        Assert.Equal("files.metadata.read files.content.read account_info.read", q["scope"]);
        Assert.False(string.IsNullOrEmpty(q["state"]));
    }

    [Fact]
    public void L_adresse_de_retour_declaree_remplace_celle_du_back_office()
    {
        var url = Auth(redirect: "https://linkii.exemple.ch/dropbox/callback").StartAuthorization(tenant.Id, "/integrations", "http://localhost:5080");
        Assert.Equal("https://linkii.exemple.ch/dropbox/callback", HttpUtility.ParseQueryString(new Uri(url).Query)["redirect_uri"]);
    }

    [Fact]
    public async Task Un_retour_inconnu_ou_expire_est_refuse() =>
        Assert.Equal("/integrations?dropbox=expired", await Auth().CompleteAsync("n-importe-quoi", "code", null));

    [Fact]
    public async Task Un_refus_de_l_utilisateur_revient_sur_la_page_d_origine_sans_compte()
    {
        var auth = Auth();
        var state = HttpUtility.ParseQueryString(new Uri(auth.StartAuthorization(tenant.Id, "http://localhost:5080/integrations?tab=comptes", "http://localhost:5080")).Query)["state"];

        Assert.Equal("http://localhost:5080/integrations?tab=comptes&dropbox=denied", await auth.CompleteAsync(state, null, "access_denied"));
        Assert.Empty(store.Read(d => d.Clients.First(c => c.Id == tenant.Id).DriveAccounts));
        Assert.False(DropboxAuth.Has(store.Read(d => d.Clients.First(c => c.Id == tenant.Id))));
    }

    [Fact]
    public void Un_compte_avec_jeton_est_connecte()
    {
        store.Write(d => d.Clients.First(c => c.Id == tenant.Id).DriveAccounts.Add(new DriveAccount { Source = "dropbox", Secret = "dp:x" }));
        Assert.True(DropboxAuth.Has(store.Read(d => d.Clients.First(c => c.Id == tenant.Id))));
    }
}
