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

    // ---------- §248 : variantes, décors, pages facultatives, une photo par page ----------

    /// <summary>Un contenu avec toutes les pages, des textes à la longueur maximale et une photo par page.</summary>
    static StartBrief Full(bool photos)
    {
        var b = Brief(photos);
        b.Pages.Welcome.Title = "Le vrai kebab grillé au feu de bois, depuis 1998";
        b.Pages.Welcome.Sub = "Viande marinée maison, pain cuit sur place et sauces préparées chaque matin par l'équipe.";
        b.Pages.Services.Points = new() { "Kebab, dürüm et assiettes à emporter ou sur place", "Options végétariennes : falafel maison", "Menus du midi servis en dix minutes", "Commande par téléphone, prête à l'heure dite" };
        b.Pages.Prices = new()
        {
            Kicker = "La carte", Title = "Nos classiques, faits minute",
            Items = new() { new() { Name = "Kebab pain maison", Price = "12.50 CHF", Note = "Veau ou poulet, crudités, sauce au choix" }, new() { Name = "Dürüm galette fine", Price = "[Prix]" }, new() { Name = "Assiette kebab frites salade", Price = "19.– CHF", Note = "Avec boisson" },
                new() { Name = "Falafel maison", Price = "11.–" }, new() { Name = "Menu enfant", Price = "9.50" }, new() { Name = "Baklava pistache", Price = "4.–", Note = "Deux pièces" } },
        };
        b.Pages.Offer = new() { Tag = "Offre du midi", Badge = "2 pour 1", Title = "Le deuxième dürüm offert chaque mardi midi", Sub = "Sur place ou à emporter, de 11 h 30 à 14 h, sur présentation de ce message." };
        b.Pages.Numbers = new() { Kicker = "En chiffres", Title = "Une maison de quartier", Items = new() { new() { Value = "25 ans", Label = "au service du quartier" }, new() { Value = "1 200", Label = "kebabs servis chaque semaine" }, new() { Value = "98 %", Label = "de clients qui reviennent" } } };
        b.Pages.Quote = new() { Text = "Le meilleur kebab de la ville, et de loin : la viande est grillée devant vous, le pain sort du four, et l'accueil est toujours aussi chaleureux après toutes ces années.", Author = "Camille, cliente depuis 2010" };
        if (photos) b.SetPhotos(StartTemplates.PhotoKinds.ToDictionary(k => k, _ => Guid.NewGuid()));
        return b;
    }

    public static IEnumerable<object[]> Dessins() =>
        from portrait in new[] { false, true } from photos in new[] { false, true } from font in new[] { "Montserrat", "Oswald", "Playfair Display" } select new object[] { portrait, photos, font };

    [Theory]
    [MemberData(nameof(Dessins))]
    public void Toutes_les_mises_en_page_et_tous_les_decors_tiennent_dans_l_ecran(bool portrait, bool photos, string font)
    {
        var b = Full(photos);
        b.Font = font;
        int w = portrait ? 1080 : 1920, h = portrait ? 1920 : 1080;
        foreach (var decor in StartTemplates.Decors)
            foreach (var kind in StartTemplates.Kinds)
                for (var v = 0; v < StartTemplates.Variants(kind); v++)
                {
                    b.Decor = decor; b.Layouts[kind] = v;
                    var doc = StartTemplates.Doc(b, kind, portrait, false);
                    Assert.NotNull(Compositions.Clean(doc));
                    var texts = new List<(int X, int Y, int W, int H, string T)>();
                    foreach (var e in Els(doc).OfType<JsonObject>().Where(e => (string?)e["type"] is "text" or "qr" or "icon" or "widget"))
                    {
                        int x = (int)e["x"]!, y = (int)e["y"]!, ew = (int)e["w"]!, eh = (int)e["h"]!;
                        Assert.True(x >= 0 && y >= 0 && x + ew <= w && y + eh <= h, $"{kind} {v} {decor} : « {e["text"] ?? e["type"]} » sort de la page ({x},{y},{ew},{eh})");
                        if ((string?)e["type"] == "text" && ((string)e["text"]!).Length > 1 && (string?)e["text"] != "“") texts.Add((x, y, ew, eh, (string)e["text"]!));
                    }
                    // deux textes ne se recouvrent pas
                    for (var i = 0; i < texts.Count; i++)
                        for (var j = i + 1; j < texts.Count; j++)
                        {
                            var (p, q) = (texts[i], texts[j]);
                            var over = p.X < q.X + q.W && q.X < p.X + p.W && p.Y < q.Y + q.H - 6 && q.Y < p.Y + p.H - 6;
                            Assert.False(over, $"{kind} {v} {(portrait ? "portrait" : "paysage")} : « {p.T} » et « {q.T} » se recouvrent");
                        }
                }
    }

    [Fact]
    public void Chaque_page_a_sa_photo_et_une_page_sans_photo_recoit_le_decor()
    {
        var b = Full(true);
        Guid welcome = b.PagePhotos["welcome"], thanks = b.PagePhotos["thanks"];
        Assert.Contains(Els(StartTemplates.Doc(b, "welcome", false, false)).OfType<JsonObject>(), e => (string?)e["media"] == welcome.ToString());
        Assert.Contains(Els(StartTemplates.Doc(b, "thanks", false, false)).OfType<JsonObject>(), e => (string?)e["media"] == thanks.ToString());
        Assert.Equal(6, b.Photos.Count);
        // la photo de la page « Merci » n'a pas été trouvée : pas d'image d'une autre page à la place, le décor
        var some = new Dictionary<string, Guid>(b.PagePhotos); some.Remove("thanks");
        b.SetPhotos(some);
        var page = Els(StartTemplates.Doc(b, "thanks", false, false)).OfType<JsonObject>().ToList();
        Assert.DoesNotContain(page, e => (string?)e["type"] == "image" && (string?)e["fit"] != "contain");
        Assert.Contains(page, e => (string?)e["type"] == "shape");
        Assert.Null(StartTemplates.PhotoOf(b, "info"));   // une page sans photo par nature
        var copy = b.Clone();
        Assert.Equal(b.PagePhotos, copy.PagePhotos);
        Assert.Equal(b.Layouts, copy.Layouts);
    }

    [Fact]
    public void Autre_mise_en_page_change_chaque_page_et_le_decor()
    {
        var b = Full(true);
        var rnd = new Random(7);
        StartTemplates.Shuffle(b, rnd);
        Assert.All(StartTemplates.Kinds, k => Assert.InRange(b.Layouts[k], 0, StartTemplates.Variants(k) - 1));
        for (var i = 0; i < 20; i++)
        {
            var before = new Dictionary<string, int>(b.Layouts); var decor = b.Decor;
            StartTemplates.Shuffle(b, rnd);
            Assert.All(StartTemplates.Kinds, k => Assert.NotEqual(before[k], b.Layouts[k]));
            Assert.NotEqual(decor, b.Decor);
            Assert.Contains(b.Decor, StartTemplates.Decors);
        }
        // les variantes dessinent vraiment des pages différentes
        b.Layouts["welcome"] = 0; var one = Els(StartTemplates.Doc(b, "welcome", false, false)).Count;
        b.Layouts["welcome"] = 1; var two = Els(StartTemplates.Doc(b, "welcome", false, false)).OfType<JsonObject>().First(e => (string?)e["type"] == "image");
        Assert.Equal(960, (int)two["x"]!);   // photo sur la moitié droite
        Assert.True(one > 0);
    }

    [Fact]
    public void Les_pages_facultatives_sont_controlees_et_limitees_a_quatre()
    {
        var b = Full(false);
        b.Pages.Prices!.Items.Add(new() { Name = "Septième ligne", Price = "1.–" });
        b.Pages.Prices.Items[0].Name = new string('x', 60);
        b.Pages.Offer!.Badge = "Beaucoup trop long pour une pastille";
        b.Pages.Numbers!.Items.Add(new() { Value = "4", Label = "quatrième" });
        AiStarter.Sanitize(b);
        Assert.Equal(6, b.Pages.Prices!.Items.Count);
        Assert.True(b.Pages.Prices.Items[0].Name.Length <= 31);
        Assert.True(b.Pages.Offer!.Badge.Length <= 13);
        // six pages facultatives écrites : quatre gardées, l'avis et les chiffres partent d'abord
        Assert.Null(b.Pages.Quote);
        Assert.Null(b.Pages.Numbers);
        Assert.Equal(4, StartTemplates.Optional.Count(k => StartTemplates.Has(b.Pages, k)));

        var phrase = Full(false);
        phrase.Site = ""; phrase.Pages.Numbers = null; phrase.Pages.Offer = null;
        AiStarter.Sanitize(phrase);
        Assert.Null(phrase.Pages.Quote);   // sans site, pas d'avis : il serait inventé
        Assert.NotNull(phrase.Pages.Prices);

        var empty = Full(false);
        empty.Pages.Prices!.Items = new(); empty.Pages.Offer!.Title = ""; empty.Pages.Numbers!.Items = new() { new() { Value = "", Label = "rien" } };
        AiStarter.Sanitize(empty);
        Assert.Null(empty.Pages.Prices);
        Assert.Null(empty.Pages.Offer);
        Assert.Null(empty.Pages.Numbers);
        Assert.Equal("Tarifs", StartTemplates.Name(empty.Pages, "prices", false));
        Assert.Equal("La carte", StartTemplates.Name(Full(false).Pages, "prices", false));
    }

    [Fact]
    public void L_IA_dit_quelle_photo_chercher_pour_chaque_page()
    {
        var o = (JsonObject)JsonNode.Parse("""{"images":{"welcome":{"fr":"kebab grillé","en":"grilled kebab meat"},"services":{"fr":"pain pita garni"},"info":{"fr":"horloge","en":"clock"},"thanks":{"fr":"","en":""}}}""")!;
        var q = AiStarter.Queries(o);
        Assert.Equal(new PhotoQuery("kebab grillé", "grilled kebab meat"), q["welcome"]);
        Assert.Equal(new PhotoQuery("pain pita garni", "pain pita garni"), q["services"]);   // sans anglais : les mots français
        Assert.False(q.ContainsKey("info"));     // une page sans photo
        Assert.False(q.ContainsKey("thanks"));   // rien à chercher
        // ancienne forme : une liste, répartie dans l'ordre des pages
        var old = AiStarter.Queries((JsonObject)JsonNode.Parse("""{"photo_queries":["lac","forêt"]}""")!);
        Assert.Equal("lac", old["welcome"].Fr);
        Assert.Equal("forêt", old["services"].En);
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
