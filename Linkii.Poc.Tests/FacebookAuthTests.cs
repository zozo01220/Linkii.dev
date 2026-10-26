using System.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>« Se connecter avec Facebook » : adresse de consentement, retours d'erreur, Pages et jetons du compte (sans appel réseau).</summary>
public class FacebookAuthTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "linkii-fb-" + Guid.NewGuid().ToString("N"));
    private readonly JsonStore store;
    private readonly SecretBox box;
    private readonly Tenant tenant = new() { Name = "Client" };

    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Linkivi";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public FacebookAuthTests()
    {
        store = new JsonStore(Config(true), new Env());
        box = new SecretBox(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(dir, "keys"))));
        store.Write(db => db.Clients.Add(tenant));
    }

    private IConfiguration Config(bool configured, string? redirect = null) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Linkii:DataDir"] = dir,
        ["Linkii:Facebook:AppId"] = configured ? "app-id" : null,
        ["Linkii:Facebook:AppSecret"] = configured ? "app-secret" : null,
        ["Linkii:Facebook:RedirectUri"] = redirect
    }).Build();

    private FacebookAuth Auth(bool configured = true, string? redirect = null) =>
        new(Config(configured, redirect), store, box, new SafeHttp(), NullLogger<FacebookAuth>.Instance);

    private Tenant Current() => store.Read(d => d.Clients.First(c => c.Id == tenant.Id));

    private void Connect(params (string Id, string Name, string Token)[] pages) => store.Write(d =>
    {
        var c = d.Clients.First(x => x.Id == tenant.Id);
        c.FacebookUser = "Ludovic Prieur"; c.FacebookConnectedUtc = DateTime.UtcNow; c.FacebookLostUtc = null;
        c.FacebookPages = pages.Select(p => new FacebookPageAccess { Id = p.Id, Name = p.Name, Token = box.Protect(p.Token) }).ToList();
    });

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    [Fact]
    public void Sans_identifiant_ni_secret_la_connexion_n_est_pas_proposee()
    {
        Assert.False(Auth(configured: false).IsConfigured);
        Assert.True(Auth().IsConfigured);
    }

    [Fact]
    public void L_adresse_de_consentement_demande_les_Pages_en_lecture()
    {
        var url = new Uri(Auth().StartAuthorization(tenant.Id, "http://localhost:5080/integrations", "http://localhost:5080"));
        var q = HttpUtility.ParseQueryString(url.Query);

        Assert.Equal("www.facebook.com", url.Host);
        Assert.EndsWith("/dialog/oauth", url.AbsolutePath);
        Assert.Equal("app-id", q["client_id"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("http://localhost:5080/facebook/callback", q["redirect_uri"]);
        Assert.Equal("pages_show_list,pages_read_engagement", q["scope"]);
        Assert.False(string.IsNullOrEmpty(q["state"]));
        Assert.DoesNotContain("app-secret", url.Query);   // le secret de l'application ne quitte jamais le serveur
    }

    [Fact]
    public void L_adresse_de_retour_declaree_remplace_celle_du_back_office()
    {
        var url = Auth(redirect: "https://linkii.exemple.ch/facebook/callback").StartAuthorization(tenant.Id, "/integrations", "http://localhost:5080");
        Assert.Equal("https://linkii.exemple.ch/facebook/callback", HttpUtility.ParseQueryString(new Uri(url).Query)["redirect_uri"]);
    }

    [Fact]
    public async Task Un_retour_inconnu_ou_expire_est_refuse() =>
        Assert.Equal("/integrations?facebook=expired", await Auth().CompleteAsync("n-importe-quoi", "code", null));

    [Fact]
    public async Task Un_refus_de_l_utilisateur_revient_sur_la_page_d_origine_sans_compte()
    {
        var auth = Auth();
        var state = HttpUtility.ParseQueryString(new Uri(auth.StartAuthorization(tenant.Id, "http://localhost:5080/integrations?tab=comptes", "http://localhost:5080")).Query)["state"];

        Assert.Equal("http://localhost:5080/integrations?tab=comptes&facebook=denied", await auth.CompleteAsync(state, null, "access_denied"));
        Assert.False(FacebookAuth.Has(Current()));
        Assert.Empty(Current().FacebookPages);
    }

    [Fact]
    public async Task Un_etat_cree_par_une_autre_instance_est_inconnu_et_ne_connecte_rien()
    {
        var state = HttpUtility.ParseQueryString(new Uri(Auth().StartAuthorization(tenant.Id, "http://localhost:5080/integrations", "http://localhost:5080")).Query)["state"];
        Assert.Equal("/integrations?facebook=expired", await Auth().CompleteAsync(state, "code", null));   // l'état n'est valable que pour le service qui l'a émis
        Assert.False(FacebookAuth.Has(Current()));
    }

    [Fact]
    public void Les_Pages_sont_lues_dans_la_reponse_de_me_accounts()
    {
        var pages = FacebookAuth.ParsePages("""
            { "data": [
              { "id": "109876543210", "name": "Commune de Lausanne", "access_token": "EAAB-page-token-1", "category": "Government" },
              { "id": "109876543211", "access_token": "EAAB-page-token-2" },
              { "id": "109876543212", "name": "Sans jeton" },
              { "id": "../me", "name": "Identifiant piégé", "access_token": "EAAB-page-token-3" }
            ] }
            """);
        Assert.Equal(new[] { "109876543210", "109876543211" }, pages.Select(p => p.Id));
        Assert.Equal("Commune de Lausanne", pages[0].Name);
        Assert.Equal("109876543211", pages[1].Name);   // sans nom : l'identifiant
        Assert.Empty(FacebookAuth.ParsePages("{}"));
    }

    [Fact]
    public void Le_jeton_de_Page_est_chiffre_dans_le_stockage_et_rendu_seulement_pour_une_Page_du_compte()
    {
        Connect(("109876543210", "Commune de Lausanne", "EAAB-page-token-1"));
        var auth = Auth();
        var t = Current();

        Assert.DoesNotContain("EAAB-page-token-1", t.FacebookPages[0].Token);
        Assert.Equal("EAAB-page-token-1", auth.PageToken(t, "109876543210"));
        Assert.Null(auth.PageToken(t, "999999999999"));
        Assert.Equal("Commune de Lausanne", FacebookAuth.Pages(t).Single().Name);
    }

    [Fact]
    public void Un_jeton_refuse_laisse_le_compte_a_reconnecter_sans_garder_les_jetons()
    {
        Connect(("109876543210", "Commune de Lausanne", "EAAB-page-token-1"));
        var auth = Auth();
        auth.MarkLost(tenant.Id);
        var t = Current();

        Assert.False(FacebookAuth.Has(t));
        Assert.Empty(FacebookAuth.Pages(t));
        Assert.Null(auth.PageToken(t, "109876543210"));
        var account = ConnectedAccounts.Of(t).Single(a => a.Id == ConnectedAccounts.Facebook);
        Assert.True(account.Lost);
        Assert.Equal("Ludovic Prieur", account.User);
    }

    [Fact]
    public void La_deconnexion_oublie_le_compte_et_les_jetons()
    {
        Connect(("109876543210", "Commune de Lausanne", "EAAB-page-token-1"));
        Auth().Disconnect(tenant.Id);
        var t = Current();

        Assert.Empty(t.FacebookPages);
        Assert.Null(t.FacebookConnectedUtc);
        Assert.DoesNotContain(ConnectedAccounts.Of(t), a => a.Id == ConnectedAccounts.Facebook);
    }

    [Fact]
    public void Le_compte_connecte_apparait_dans_Comptes_connectes_avec_ses_Pages()
    {
        Connect(("109876543210", "Commune de Lausanne", "EAAB-page-token-1"), ("109876543211", "Bibliothèque", "EAAB-page-token-2"));
        var a = ConnectedAccounts.Of(Current()).Single(x => x.Id == ConnectedAccounts.Facebook);

        Assert.False(a.Lost);
        Assert.Contains("2 Pages", a.Detail);
        Assert.Contains("Commune de Lausanne", a.Detail);
        Assert.Equal("/facebook/connect?tab=comptes", ConnectedAccounts.ConnectUrl(ConnectedAccounts.Facebook));
        Assert.Equal(ConnectedAccounts.Facebook, ConnectedAccounts.For("facebook"));
    }

    [Fact]
    public async Task Sans_compte_ni_jeton_la_lecture_explique_comment_se_connecter()
    {
        var provider = new FacebookProvider(new SafeHttp(), Auth());
        var rt = new AppRuntime(Current(), new Dictionary<string, string> { ["pageId"] = "109876543210" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.Test(rt));
        Assert.Contains("Aucun compte Facebook connecté", ex.Message);
    }

    [Fact]
    public async Task Une_Page_qui_n_est_pas_celle_du_compte_est_refusee_sans_appel_reseau()
    {
        Connect(("109876543210", "Commune de Lausanne", "EAAB-page-token-1"));
        var provider = new FacebookProvider(new SafeHttp(), Auth());
        var rt = new AppRuntime(Current(), new Dictionary<string, string> { ["pageId"] = "999999999999" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.Test(rt));
        Assert.Contains("n'est pas administrée par le compte", ex.Message);
    }
}
