using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Linkii.Poc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Éditeur de compositions (§80) : document vérifié à l'enregistrement, images et QR codes résolus à la publication.</summary>
[Collection("platform")]
public class CompositionsTests(Platform p)
{
    static string Doc(params object[] els) => JsonSerializer.Serialize(new { v = 1, w = 1920, h = 1080, bg = "#0B1F3A", els });

    [Fact]
    public void Clean_refuse_un_document_illisible_ou_hors_limites()
    {
        Assert.Null(Compositions.Clean(null));
        Assert.Null(Compositions.Clean("pas du json"));
        Assert.Null(Compositions.Clean("[]"));
        Assert.Null(Compositions.Clean(JsonSerializer.Serialize(new { w = 100, h = 1080, els = Array.Empty<object>() })));   // trop petit
        Assert.Null(Compositions.Clean(JsonSerializer.Serialize(new { w = 1920, h = 1080 })));                             // pas de calques
        Assert.Null(Compositions.Clean(Doc(Enumerable.Range(0, Compositions.MaxElements + 1).Select(i => new { type = "text", text = "x" }).ToArray())));
        Assert.Null(Compositions.Clean(new string(' ', 10) + Doc(new { type = "text", text = new string('x', Compositions.MaxDocLength) })));
    }

    [Fact]
    public void Clean_garde_les_calques_connus_retire_les_adresses_et_liste_les_images()
    {
        var a = Guid.NewGuid();
        var c = Compositions.Clean(JsonSerializer.Serialize(new
        {
            w = 1080.4, h = 1920, bg = "red;background:url(x)",
            els = new object[]
            {
                new { type = "image", media = a.ToString(), src = "https://ailleurs.example/x.jpg" },
                new { type = "image", media = a.ToString() },
                new { type = "script", text = "?" },
                new { type = "text", text = "Bonjour" }
            }
        }));
        Assert.NotNull(c);
        Assert.Equal(new[] { a }, c!.Value.Images);
        var doc = JsonNode.Parse(c.Value.Doc)!;
        Assert.Equal(1080, (int)doc["w"]!);
        Assert.Equal("#FFFFFF", (string)doc["bg"]!);   // couleur invalide remplacée
        Assert.Equal(3, doc["els"]!.AsArray().Count);  // type inconnu retiré
        Assert.DoesNotContain("ailleurs.example", c.Value.Doc);
    }

    [Fact]
    public void Publish_resout_les_images_et_les_qr_codes_et_retire_le_reste()
    {
        var ok = new MediaItem { Id = Guid.NewGuid(), Type = "image", FileName = "abc.jpg" };
        var gone = Guid.NewGuid();
        var settings = new Dictionary<string, string>
        {
            ["doc"] = Doc(new { id = "e1", type = "image", media = ok.Id.ToString() },
                          new { id = "e2", type = "image", media = gone.ToString() },
                          new { id = "e3", type = "image" },
                          new { id = "e4", type = "qr", url = "https://linkii.ch", qr = "1|<script>" },
                          new { id = "e5", type = "text", text = "Masqué", hidden = true },
                          new { id = "e6", type = "text", text = "Visible" }),
            ["images"] = ok.Id + "," + gone
        };
        var r = Compositions.Publish(settings, id => id == ok.Id ? ok : null);
        Assert.NotNull(r);
        Assert.Equal(new[] { "/media/abc.jpg" }, r!.Value.Urls);
        var els = JsonNode.Parse(r.Value.Settings["doc"])!["els"]!.AsArray();
        Assert.Equal(new[] { "e1", "e4", "e6" }, els.Select(e => (string)e!["id"]!));
        Assert.Equal("/media/abc.jpg", (string)els[0]!["src"]!);
        Assert.Null(els[0]!["media"]);
        Assert.Equal(QrCodes.Encode("https://linkii.ch"), (string)els[1]!["qr"]!);   // tracé recalculé : celui du document est ignoré
        Assert.Null(Compositions.Publish(new Dictionary<string, string> { ["doc"] = "" }, _ => null));
    }

    [Fact]
    public void Blocs_et_champs_relies_figent_leurs_sources_a_la_publication()
    {
        var meteo = new MediaItem { Id = Guid.NewGuid(), Type = "app", AppId = "weather-slide", Settings = { ["city"] = "Lausanne" }, Secrets = { ["cle"] = "chiffre" } };
        var autre = new MediaItem { Id = Guid.NewGuid(), Type = "app", AppId = "webpage" };   // pas une source de données
        var absente = Guid.NewGuid();
        var doc = Doc(new { id = "e1", type = "app", app = "weather-slide", source = meteo.Id.ToString("N") },
                      new { id = "e2", type = "app", app = "webpage", source = autre.Id.ToString("N") },
                      new { id = "e3", type = "app", app = "agenda", source = absente.ToString("N") },
                      new { id = "e4", type = "text", text = $"Il fait {{{{{meteo.Id:N}:temp}}}}° · {{{{{absente:N}:next.title}}}}" });

        Assert.Equal(new[] { meteo.Id, autre.Id, absente }, Compositions.Sources(doc));
        var cleaned = JsonNode.Parse(Compositions.Clean(doc)!.Value.Doc)!["els"]!.AsArray();
        Assert.Equal(new[] { "e1", "e3", "e4" }, cleaned.Select(e => (string)e!["id"]!));   // bloc d'une intégration sans données : refusé

        var r = Compositions.Publish(new Dictionary<string, string> { ["doc"] = doc }, _ => null, id => id == meteo.Id ? meteo : id == autre.Id ? autre : null)!.Value;
        var els = JsonNode.Parse(r.Settings["doc"])!["els"]!.AsArray();
        Assert.Equal(new[] { "e1", "e4" }, els.Select(e => (string)e!["id"]!));   // source introuvable : son bloc n'est pas diffusé, ses champs restent (valeur de secours)
        Assert.Contains("src:" + meteo.Id.ToString("N"), r.Settings.Keys);
        Assert.DoesNotContain("src:" + absente.ToString("N"), r.Settings.Keys);
        Assert.DoesNotContain("chiffre", string.Join("", r.Settings.Values));   // secrets : jamais dans les paramètres envoyés à l'écran
        Assert.Equal("chiffre", r.Secrets["src:" + meteo.Id.ToString("N") + ":cle"]);

        var pub = new PublishedItem { Id = Guid.NewGuid(), AppId = Compositions.AppId, Settings = r.Settings, Secrets = r.Secrets };
        var s = Compositions.SourceItem(pub, meteo.Id);
        Assert.NotNull(s);
        Assert.Equal("weather-slide", s!.AppId);
        Assert.Equal("Lausanne", s.Settings["city"]);
        Assert.Equal("chiffre", s.Secrets["cle"]);
        Assert.Null(Compositions.SourceItem(pub, absente));   // une source non utilisée par la composition publiée ne se lit pas
        Assert.Null(Compositions.SourceItem(new PublishedItem { AppId = "rss", Settings = r.Settings }, meteo.Id));
    }

    [Fact]
    public void Le_menu_Compositions_depend_de_l_integration_et_du_niveau_de_droits()
    {
        var catalog = p.Services.GetRequiredService<AppCatalog>();
        var app = catalog.Find(Compositions.AppId)!;
        Assert.False(app.DefaultOn);   // à marquer « Utiliser » dans le Store, pas active d'office
        Assert.Contains(LevelPages.Compositions, LevelPages.All);
        Assert.Equal("/compositions", LevelPages.Path(LevelPages.Compositions));

        var allowed = new AccessLevel { Name = "Toutes intégrations", AllIntegrations = true, AllAreas = true };
        var denied = new AccessLevel { Name = "Aucune intégration", AllAreas = true };
        Assert.True(new AreaAccess { IsOrgAdmin = false, Level = allowed }.CanSee(LevelPages.Compositions, catalog));
        Assert.False(new AreaAccess { IsOrgAdmin = false, Level = denied }.CanSee(LevelPages.Compositions, catalog));
    }

    [Fact]
    public void Installer_ou_retirer_l_integration_previent_le_menu()
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(Claims.Client, p.A.Id.ToString()), new Claim(ClaimTypes.Role, Roles.ClientAdmin) }, "test"));
        var s = new AppService(new TenantStore(store, new TenantContext(new FixedAuth(user), store)), p.Services.GetRequiredService<AppCatalog>(), p.Services.GetRequiredService<AppProviders>(), p.Services.GetRequiredService<SecretBox>());
        s.Disable(Compositions.AppId);
        var changes = 0;
        s.InstallsChanged += () => changes++;
        s.Install(Compositions.AppId);
        s.Install(Compositions.AppId);   // déjà ajoutée : pas de changement
        Assert.Equal(1, changes);
        Assert.True(s.IsInstalled(Compositions.AppId));
        s.Disable(Compositions.AppId);
        Assert.Equal(2, changes);
        Assert.False(s.IsInstalled(Compositions.AppId));
    }

    [Fact]
    public async Task Une_composition_accepte_les_memes_positions_que_les_autres_medias()
    {
        var catalog = p.Services.GetRequiredService<AppCatalog>();
        Assert.Equal(catalog.Find("image")!.Placements, catalog.Find(Compositions.AppId)!.Placements);

        var store = p.Services.GetRequiredService<JsonStore>();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(Claims.Client, p.A.Id.ToString()), new Claim(ClaimTypes.Role, Roles.ClientAdmin) }, "test"));
        var s = new AppService(new TenantStore(store, new TenantContext(new FixedAuth(user), store)), catalog, p.Services.GetRequiredService<AppProviders>(), p.Services.GetRequiredService<SecretBox>());
        s.Install(Compositions.AppId);
        var c = Compositions.Clean(Doc(new { id = "e1", type = "text", text = "Coin" }))!.Value;
        var (item, errors) = s.SaveInstance(Compositions.AppId, null, "En angle", new Dictionary<string, string> { ["doc"] = c.Doc, ["images"] = "" });
        Assert.Empty(errors);
        var pl = store.Write(db =>
        {
            var list = new Playlist { ClientId = p.A.Id, Name = "Angle " + Guid.NewGuid().ToString("N")[..6] };
            list.Draft.Add(new PlaylistItem { MediaId = item!.Id, DurationSec = 5, Placement = "top-left" });
            db.Playlists.Add(list);
            return list.Id;
        });
        await p.Services.GetRequiredService<Notifier>().Publish(pl);
        Assert.Equal("top-left", store.Read(db => db.Playlists.First(x => x.Id == pl).Published[0].Placement));
    }

    [Fact]
    public void Le_cadre_d_apercu_suit_la_page_de_la_composition()
    {
        Assert.Equal("aspect-ratio:1920 / 1080", Compositions.StageStyle(new Dictionary<string, string> { ["doc"] = Doc() }));
        var portrait = JsonSerializer.Serialize(new { v = 1, w = 1080, h = 1920, els = Array.Empty<object>() });
        Assert.Contains("height:360px", Compositions.StageStyle(new Dictionary<string, string> { ["doc"] = portrait }));
        Assert.Equal("aspect-ratio:1920 / 1080", Compositions.StageStyle(new Dictionary<string, string>()));   // document illisible : cadre 16:9
        Assert.Equal(3, QrTracking.MinSize);
    }

    [Fact]
    public void Mes_modeles_se_creent_se_renomment_et_se_suppriment_dans_l_aire()
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        var img = new MediaItem { ClientId = p.A.Id, Type = "image", Name = "Logo", FileName = Guid.NewGuid().ToString("N") + ".png" };
        store.Write(db => db.Media.Add(img));
        var doc = Doc(new { id = "e1", type = "image", media = img.Id.ToString() }, new { id = "e2", type = "text", text = "Modèle" });

        var t = store.Write(db =>
        {
            var d = new ClientDb(db, p.A.Id);
            Assert.Equal("Donnez un nom au modèle.", CompositionTemplates.Save(d, "  ", doc).Error);
            Assert.NotNull(CompositionTemplates.Save(d, "Illisible", "{").Error);
            var (tpl, err) = CompositionTemplates.Save(d, "Accueil hiver", doc);
            Assert.Null(err);
            return tpl!;
        });
        Assert.Equal(img.Id.ToString(), t.Images);
        Assert.Contains("\"e2\"", t.Doc);
        // les images d'un modèle comptent comme utilisées dans la médiathèque
        Assert.Contains(t.Id, store.Read(db => db.CompositionTemplates.Select(x => x.Id).ToList()));
        Assert.DoesNotContain(store.Read(db => new ClientDb(db, p.B.Id).CompositionTemplates.ToList()), x => x.Id == t.Id);   // autre client : invisible

        store.Write(db =>
        {
            var d = new ClientDb(db, p.A.Id);
            Assert.Null(CompositionTemplates.Rename(d, t.Id, "Accueil été"));
            Assert.NotNull(CompositionTemplates.Rename(d, Guid.NewGuid(), "x"));
        });
        Assert.Equal("Accueil été", store.Read(db => db.CompositionTemplates.First(x => x.Id == t.Id).Name));
        Assert.False(store.Write(db => CompositionTemplates.Delete(new ClientDb(db, p.B.Id), t.Id)));   // un autre client ne peut pas le supprimer
        Assert.True(store.Write(db => CompositionTemplates.Delete(new ClientDb(db, p.A.Id), t.Id)));
    }

    [Fact]
    public void Renommer_une_composition()
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(Claims.Client, p.A.Id.ToString()), new Claim(ClaimTypes.Role, Roles.ClientAdmin) }, "test"));
        var s = new AppService(new TenantStore(store, new TenantContext(new FixedAuth(user), store)), p.Services.GetRequiredService<AppCatalog>(), p.Services.GetRequiredService<AppProviders>(), p.Services.GetRequiredService<SecretBox>());
        s.Install(Compositions.AppId);
        var c = Compositions.Clean(Doc(new { id = "e1", type = "text", text = "A" }))!.Value;
        var (item, _) = s.SaveInstance(Compositions.AppId, null, "Avant", new Dictionary<string, string> { ["doc"] = c.Doc, ["images"] = "" });
        Assert.Equal("Donnez un nom.", s.RenameInstance(item!.Id, " "));
        Assert.Null(s.RenameInstance(item.Id, "Après"));
        Assert.Equal("Après", s.Instance(item.Id)!.Name);
        Assert.Equal(c.Doc, s.Instance(item.Id)!.Settings["doc"]);   // le document ne change pas
    }

    [Fact]
    public async Task Une_composition_enregistree_puis_publiee_arrive_sur_l_ecran_avec_ses_images()
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(Claims.Client, p.A.Id.ToString()), new Claim(ClaimTypes.Role, Roles.ClientAdmin) }, "test"));
        var s = new AppService(new TenantStore(store, new TenantContext(new FixedAuth(user), store)), p.Services.GetRequiredService<AppCatalog>(), p.Services.GetRequiredService<AppProviders>(), p.Services.GetRequiredService<SecretBox>());
        Assert.NotNull(s.Find(Compositions.AppId));   // manifeste valide : présent dans le catalogue
        s.Install(Compositions.AppId);

        var img = new MediaItem { ClientId = p.A.Id, Type = "image", Name = "Photo", FileName = Guid.NewGuid().ToString("N") + ".jpg" };
        var other = new MediaItem { ClientId = p.B.Id, Type = "image", Name = "Autre client", FileName = Guid.NewGuid().ToString("N") + ".jpg" };
        store.Write(db => { db.Media.Add(img); db.Media.Add(other); });

        var empty = s.CreateEmpty(Compositions.AppId)!;
        var c = Compositions.Clean(Doc(new { id = "e1", type = "image", media = img.Id.ToString() }, new { id = "e2", type = "image", media = other.Id.ToString() }, new { id = "e3", type = "text", text = "Bienvenue" }))!.Value;
        var (item, errors) = s.SaveInstance(Compositions.AppId, empty.Id, "Accueil", new Dictionary<string, string> { ["doc"] = c.Doc, ["images"] = string.Join(",", c.Images) });
        Assert.Empty(errors);

        var pl = store.Write(db =>
        {
            var list = new Playlist { ClientId = p.A.Id, Name = "Compo " + Guid.NewGuid().ToString("N")[..6] };
            list.Draft.Add(new PlaylistItem { MediaId = empty.Id, DurationSec = 10 });   // ajoutée vide, complétée ensuite dans l'éditeur
            db.Playlists.Add(list);
            return list.Id;
        });
        await p.Services.GetRequiredService<Notifier>().Publish(pl);
        var pub = store.Read(db => db.Playlists.First(x => x.Id == pl).Published[0]);
        Assert.Equal(Compositions.AppId, pub.AppId);
        Assert.Equal(new[] { "/media/" + img.FileName }, pub.Urls);   // l'image d'un autre client n'est jamais publiée
        Assert.Contains("Bienvenue", pub.Settings["doc"]);
        Assert.DoesNotContain(other.FileName!, pub.Settings["doc"]);
        Assert.Equal(item!.Id, empty.Id);
    }
}
