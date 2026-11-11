using System.Text.Json.Nodes;
using Linkii.Poc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>
/// Premiers pas avec l'assistant (§232) : les pages dessinées par les modèles, le contrôle du contenu rendu par l'IA,
/// la lecture d'un site, l'ordre de la liste et le lien d'aperçu de 24 h pour le téléphone.
/// </summary>
public class AiStartTests
{
    static readonly Guid Logo = Guid.NewGuid(), P1 = Guid.NewGuid(), P2 = Guid.NewGuid();

    static StartBrief Brief(bool photos = true, bool logo = true, string? city = "Lausanne") => new()
    {
        Name = "Horizon SA", City = city, Font = "Montserrat", Site = "horizon-sa.ch",
        Colors = new StartColors { Deep = "#12403C", Accent = "#E2733D", Light = "#F6F1E7" },
        Logo = logo ? Logo : null, Photos = photos ? new() { P1, P2 } : new(),
        Pages = new StartPages
        {
            Welcome = new() { Kicker = "Lausanne · Bienvenue", Title = "Bienvenue chez Horizon SA", Sub = "Nous sommes ravis de vous accueillir." },
            Services = new() { Kicker = "Nos services", Title = "Ce que nous faisons pour vous", Points = new() { "Études et conseil", "Accompagnement de vos projets", "Suivi sur mesure" } },
            Info = new() { Kicker = "Bon à savoir", Title = "Infos pratiques", Cards = new() { new() { Icon = "horloge", Label = "Horaires", Value = "Lun – Ven · 8 h – 18 h" }, new() { Icon = "wifi", Label = "Wi-Fi", Value = "Réseau « Invités »", Note = "Mot de passe à l'accueil" } } },
            News = new() { Tag = "Actualité", Title = "Portes ouvertes vendredi", Sub = "Venez nous voir", Link = "https://horizon-sa.ch/actualites" },
            Thanks = new() { Title = "Merci de votre visite", Sub = "horizon-sa.ch" },
        },
    };

    static JsonArray Els(string doc) => (JsonNode.Parse(doc)!["els"] as JsonArray)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Chaque_page_est_une_composition_propre_qui_tient_dans_l_ecran(bool portrait)
    {
        var b = Brief();
        foreach (var kind in StartTemplates.Kinds)
        {
            var doc = StartTemplates.Doc(b, kind, portrait, false);
            var clean = Compositions.Clean(doc);
            Assert.NotNull(clean);
            var o = JsonNode.Parse(doc)!;
            int w = (int)o["w"]!, h = (int)o["h"]!;
            Assert.Equal(portrait ? (1080, 1920) : (1920, 1080), (w, h));
            foreach (var e in Els(doc).OfType<JsonObject>().Where(e => (string?)e["type"] is "text" or "qr" or "icon" or "widget"))
            {
                int x = (int)e["x"]!, y = (int)e["y"]!, ew = (int)e["w"]!, eh = (int)e["h"]!;
                Assert.True(x >= 0 && y >= 0 && x + ew <= w && y + eh <= h, $"{kind} : « {e["text"] ?? e["type"]} » sort de la page ({x},{y},{ew},{eh})");
            }
        }
    }

    [Fact]
    public void Les_textes_les_photos_le_logo_et_le_QR_sont_bien_places()
    {
        var b = Brief();
        var welcome = Els(StartTemplates.Doc(b, "welcome", false, false)).OfType<JsonObject>().ToList();
        Assert.Contains(welcome, e => (string?)e["text"] == "Bienvenue chez Horizon SA");
        Assert.Contains(welcome, e => (string?)e["text"] == "LAUSANNE · BIENVENUE");   // sur-titre en capitales
        Assert.Contains(welcome, e => (string?)e["media"] == P1.ToString());           // photo de fond
        Assert.Contains(welcome, e => (string?)e["media"] == Logo.ToString() && (string?)e["fit"] == "contain");
        Assert.Contains(welcome, e => (string?)e["kind"] == "clock");                  // la ville est connue : l'heure
        var services = Els(StartTemplates.Doc(b, "services", false, false)).OfType<JsonObject>().ToList();
        Assert.Contains(services, e => (string?)e["media"] == P2.ToString());
        Assert.Equal(3, services.Count(e => (string?)e["type"] == "text" && ((string?)e["text"] ?? "").Length == 1));   // numéros 1, 2, 3
        var news = Els(StartTemplates.Doc(b, "news", false, false)).OfType<JsonObject>().ToList();
        Assert.Contains(news, e => (string?)e["type"] == "qr" && (string?)e["url"] == "https://horizon-sa.ch/actualites");
        var info = Els(StartTemplates.Doc(b, "info", false, false)).OfType<JsonObject>().ToList();
        Assert.Contains(info, e => (string?)e["type"] == "icon" && (string?)e["icon"] == "horloge");
    }

    [Fact]
    public void Sans_ville_ni_photo_ni_logo_les_pages_restent_dessinees()
    {
        var b = Brief(photos: false, logo: false, city: null);
        var welcome = Els(StartTemplates.Doc(b, "welcome", false, false)).OfType<JsonObject>().ToList();
        Assert.DoesNotContain(welcome, e => (string?)e["kind"] == "clock");
        Assert.DoesNotContain(welcome, e => (string?)e["type"] == "image");
        Assert.Contains(welcome, e => (string?)e["text"] == "Horizon SA");   // le nom à la place du logo
        Assert.Contains(welcome, e => (string?)e["kind"] == "circle");       // décor
        foreach (var kind in StartTemplates.Kinds) Assert.NotNull(Compositions.Clean(StartTemplates.Doc(b, kind, true, false)));
    }

    [Fact]
    public void Un_titre_trop_long_rapetisse_au_lieu_de_deborder()
    {
        var b = Brief();
        b.Pages.Welcome.Title = "Bienvenue dans nos nouveaux locaux du centre-ville, au troisième étage du grand bâtiment";
        var title = Els(StartTemplates.Doc(b, "welcome", false, false)).OfType<JsonObject>().First(e => ((string?)e["text"] ?? "").StartsWith("Bienvenue dans"));
        Assert.True((int)title["size"]! < 116);
        Assert.True(StartTemplates.Lines("un deux trois quatre cinq six sept huit neuf dix", 300, 40) > 1);
        Assert.Equal(1, StartTemplates.Lines("Court", 900, 40));
    }

    [Fact]
    public void Les_couleurs_sont_lisibles()
    {
        var c = StartTemplates.Fix(new StartColors { Deep = "#3B82C4", Accent = "pas une couleur", Light = "#C0C0C0" });
        Assert.True(StartTemplates.Lum(c.Deep) <= 0.07);       // fond foncé vraiment foncé
        Assert.True(StartTemplates.Lum(c.Light) >= 0.82);      // fond clair vraiment clair
        Assert.Equal("#E2733D", c.Accent);                    // couleur illisible : celle par défaut
        Assert.True(StartTemplates.Contrast("#FFFFFF", c.Deep) > 7);
    }

    [Fact]
    public void Le_contenu_rendu_par_l_IA_est_controle()
    {
        var b = Brief();
        b.Site = "";   // une phrase : pas de site
        b.Font = "Comic Sans";
        b.Pages.Welcome.Title = "";
        b.Pages.Services.Points = new() { "a", "", "b", "c", "d", "e" };
        b.Pages.Info!.Cards = new() { new() { Icon = "licorne", Label = "Horaires", Value = new string('x', 80) } };
        b.Pages.News!.Link = "https://inventé.example/page";
        AiStarter.Sanitize(b);
        Assert.Equal("Montserrat", b.Font);
        Assert.Equal("Bienvenue chez Horizon SA", b.Pages.Welcome.Title);
        Assert.Equal(4, b.Pages.Services.Points.Count);
        Assert.Equal("information", b.Pages.Info!.Cards[0].Icon);
        Assert.True(b.Pages.Info.Cards[0].Value.Length <= 37);
        Assert.Null(b.Pages.News!.Link);   // sans site, pas de lien inventé

        var empty = Brief();
        empty.Pages.Info!.Cards = new();
        empty.Pages.News!.Title = " ";
        AiStarter.Sanitize(empty);
        Assert.Null(empty.Pages.Info);
        Assert.Null(empty.Pages.News);
        Assert.False(StartTemplates.Has(empty.Pages, "info"));
    }

    [Fact]
    public void Adresse_ou_phrase()
    {
        Assert.True(AiStarter.LooksLikeUrl("www.horizon-sa.ch"));
        Assert.True(AiStarter.LooksLikeUrl("https://horizon-sa.ch/accueil"));
        Assert.True(AiStarter.LooksLikeUrl("horizon-sa.ch"));
        Assert.False(AiStarter.LooksLikeUrl("Un bureau d'études à Lausanne."));
        Assert.False(AiStarter.LooksLikeUrl("accueillir nos visiteurs"));
    }

    [Fact]
    public void La_lecture_du_site_trouve_logo_photos_et_couleurs()
    {
        const string html = """
            <html><head><title>Horizon SA – Bureau d'études</title>
            <meta property="og:image" content="/img/hero.jpg"><meta name="theme-color" content="#12403C">
            <link rel="apple-touch-icon" href="/apple-touch-icon.png"><link rel="icon" href="/favicon.ico">
            <style>.btn{background:#E2733D}.x{color:#E2733D}.g{color:#EEEEEE}</style></head>
            <body><header><img src="/img/logo-horizon.svg" alt="Horizon"></header>
            <h1>Bienvenue</h1><p>Nous accompagnons vos projets à Lausanne.</p>
            <img src="/img/equipe.jpg" alt="Nos bureaux" width="1200"><img src="/img/picto.png" class="icon"><img src="/img/mini.jpg" width="80">
            <div style="background-image:url('/img/fond.jpg')"></div>
            <script>var x = "<h1>pas ça</h1>";</script></body></html>
            """;
        var p = AiStarter.Page(html, new Uri("https://horizon-sa.ch/"));
        Assert.Equal("Horizon SA – Bureau d'études", p.Title);
        Assert.Equal("#12403C", p.Color);
        Assert.Contains("#E2733D", p.Colors);
        Assert.DoesNotContain("#EEEEEE", p.Colors);   // gris : pas une couleur de marque
        Assert.Contains("https://horizon-sa.ch/img/logo-horizon.svg", p.Logos);
        Assert.Contains("https://horizon-sa.ch/apple-touch-icon.png", p.Logos);
        Assert.DoesNotContain(p.Logos, l => l.EndsWith(".ico"));
        var urls = p.Images.Select(i => i.Url).ToList();
        Assert.Equal("https://horizon-sa.ch/img/hero.jpg", urls[0]);   // image de partage d'abord
        Assert.Contains("https://horizon-sa.ch/img/equipe.jpg", urls);
        Assert.Contains("https://horizon-sa.ch/img/fond.jpg", urls);
        Assert.DoesNotContain("https://horizon-sa.ch/img/picto.png", urls);
        Assert.DoesNotContain("https://horizon-sa.ch/img/mini.jpg", urls);
        Assert.Contains("Bienvenue", p.Headings);
        Assert.DoesNotContain("pas ça", p.Text);
    }

    [Fact]
    public void La_liste_met_la_meteo_avant_l_actualite_et_l_anglais_apres_chaque_page()
    {
        var st = new StartState { Bilingual = true, Weather = Guid.NewGuid() };
        Guid W = Guid.NewGuid(), WE = Guid.NewGuid(), N = Guid.NewGuid(), T = Guid.NewGuid();
        st.Pages["welcome"] = W; st.EnPages["welcome"] = WE; st.Pages["news"] = N; st.Pages["thanks"] = T;
        Assert.Equal(new[] { W, WE, st.Weather.Value, N, T }, st.Order());
        st.Bilingual = false;
        Assert.Equal(new[] { W, st.Weather.Value, N, T }, st.Order());
        st.Pages.Remove("news");
        Assert.Equal(new[] { W, st.Weather.Value, T }, st.Order());   // sans actualité, la météo garde sa place
    }

    [Fact]
    public void Retouche_annulee_le_contenu_d_avant_revient()
    {
        var st = new StartState { Brief = Brief() };
        var snap = st.Save();
        st.Brief.Pages.Welcome.Sub = "Autre texte";
        st.Portrait = true;
        st.Restore(snap);
        Assert.Equal("Nous sommes ravis de vous accueillir.", st.Brief.Pages.Welcome.Sub);
        Assert.False(st.Portrait);
        Assert.Equal(Logo, st.Brief.Logo);   // logo et photos ne sont pas perdus par la copie
        Assert.Equal(2, st.Brief.Photos.Count);
    }

    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Linkivi";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void Le_lien_du_telephone_dure_24_h_et_ne_montre_que_sa_liste()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkii-start-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Linkii:DataDir"] = dir }).Build(), new Env());
            var grants = new PreviewGrants(store);
            Guid client = Guid.NewGuid(), list = Guid.NewGuid();
            var token = grants.Share(client, list, "portrait");
            Assert.InRange(token.Length, 16, 32);   // court : il tient dans un QR lisible
            var g = grants.Find(token);
            Assert.NotNull(g);
            Assert.Equal((client, list, "portrait"), (g!.ClientId, g.PlaylistId, g.Orientation));
            Assert.Null(g.ScreenId);
            Assert.Null(grants.Find("tAutreJeton123456789"));
            // gardé dans la base : un autre service (redémarrage) le retrouve
            Assert.NotNull(new PreviewGrants(store).Find(token));
            // passé 24 h, plus rien
            store.Write(d => d.PreviewShares.ForEach(s => s.Until = DateTime.UtcNow.AddMinutes(-1)));
            Assert.Null(grants.Find(token));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
