using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Comptage des scans de QR code : adresse courte, QR publié, journal des scans, filtre des robots.</summary>
public class QrTrackingTests
{
    private static PlaylistItem Item(bool on = true) => new()
    {
        QrOn = on, QrCode = "k7Xa2PqR", QrUrl = "https://www.unil.ch/inscription", QrBase = "https://affichage.unil.ch", QrLabel = "  Inscrivez-vous  "
    };

    [Fact]
    public void Code_court_aleatoire_et_valide()
    {
        var a = QrTracking.NewCode();
        Assert.Equal(8, a.Length);
        Assert.True(QrTracking.IsCode(a));
        Assert.NotEqual(a, QrTracking.NewCode());
        Assert.False(QrTracking.IsCode("abc"));          // trop court
        Assert.False(QrTracking.IsCode("ab/../../x1"));   // jamais de chemin
        Assert.False(QrTracking.IsCode(null));
    }

    [Theory]
    [InlineData("https://www.unil.ch/a?b=1", "https://www.unil.ch/a?b=1")]
    [InlineData("www.unil.ch", "https://www.unil.ch/")]
    [InlineData("  http://localhost:5080/x ", "http://localhost:5080/x")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///c:/secret", null)]
    [InlineData("data:text/html,x", null)]
    [InlineData("monsite", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Destination_http_ou_https_seulement(string? raw, string? expected) => Assert.Equal(expected, QrTracking.Destination(raw));

    [Fact]
    public void Adresse_courte_depuis_le_domaine_de_l_application()
    {
        Assert.Equal("https://affichage.unil.ch/q/k7Xa2PqR", QrTracking.ShortUrl("https://affichage.unil.ch/playlists/x?y=1", "k7Xa2PqR"));
        Assert.Equal("http://localhost:5080/q/k7Xa2PqR", QrTracking.ShortUrl("http://localhost:5080/", "k7Xa2PqR"));
        Assert.Null(QrTracking.ShortUrl("n'importe quoi", "k7Xa2PqR"));
        Assert.Null(QrTracking.ShortUrl("https://affichage.unil.ch", "../x"));
    }

    [Fact]
    public void Qr_publie_seulement_pleine_page_growth_et_active()
    {
        var q = QrTracking.Overlay(Item(), growth: true, "full");
        Assert.NotNull(q);
        Assert.Equal("Inscrivez-vous", q!.Label);
        Assert.Matches(@"^\d+\|[0-9MhvzH\- ]+$", q.Data);   // le player n'accepte que ce jeu de caractères
        Assert.Null(QrTracking.Overlay(Item(), growth: false, "full"));       // licence Base
        Assert.Null(QrTracking.Overlay(Item(on: false), growth: true, "full")); // suivi coupé
        Assert.Null(QrTracking.Overlay(Item(), growth: true, "top-left"));     // contenu en angle
        Assert.Null(QrTracking.Overlay(new PlaylistItem { QrOn = true, QrCode = "k7Xa2PqR", QrUrl = "pas une adresse", QrBase = "https://a.ch" }, true, "full"));
        Assert.Null(QrTracking.Overlay(new PlaylistItem { QrOn = true, QrUrl = "https://a.ch", QrBase = "https://a.ch" }, true, "full"));   // pas de code
    }

    [Fact]
    public void Position_et_taille_restent_dans_l_ecran()
    {
        var i = Item(); i.QrSize = 500; i.QrX = 300; i.QrY = -20;
        var q = QrTracking.Overlay(i, true, "full")!;
        Assert.Equal(QrTracking.MaxSize, q.Size);
        Assert.Equal(100 - QrTracking.MaxSize, q.X);
        Assert.Equal(0, q.Y);
        i.QrSize = double.NaN; i.QrX = double.NaN;
        var d = QrTracking.Overlay(i, true, "full")!;
        Assert.Equal(QrTracking.DefaultSize, d.Size);
        Assert.InRange(d.X, 0, 100 - d.Size);
    }

    [Fact]
    public void Le_player_recoit_le_qr_et_jamais_les_reglages_internes()
    {
        var pi = new PublishedItem { Qr = QrTracking.Overlay(Item(), true, "full") };
        var json = System.Text.Json.JsonSerializer.Serialize(pi.ForPlayer());
        Assert.Contains("\"qr\":{", json);
        Assert.DoesNotContain("unil.ch/inscription", json);   // la destination reste sur le serveur : seul le QR (adresse courte) part
        Assert.Contains("\"qr\":null", System.Text.Json.JsonSerializer.Serialize(new PublishedItem().ForPlayer()));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Safari/604.1", "GET", false)]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/120.0 Mobile Safari/537.36", "GET", false)]
    [InlineData("WhatsApp/2.23.20 A", "GET", true)]
    [InlineData("facebookexternalhit/1.1", "GET", true)]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1)", "GET", true)]
    [InlineData("Slackbot-LinkExpanding 1.0", "GET", true)]
    [InlineData("curl/8.0", "GET", true)]
    [InlineData("", "GET", true)]
    [InlineData(null, "GET", true)]
    [InlineData("Mozilla/5.0 (iPhone) Safari", "HEAD", true)]
    public void Robots_et_apercus_ne_sont_pas_des_visiteurs(string? ua, string method, bool bot) => Assert.Equal(bot, QrTracking.LooksLikeBot(ua, method));

    private static ScanLog NewLog(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "linkii-scan-" + Guid.NewGuid().ToString("N"));
        return new ScanLog(dir);
    }

    [Fact]
    public void Journal_des_scans_compte_scans_et_visiteurs_par_jour()
    {
        var log = NewLog(out var dir);
        try
        {
            var client = Guid.NewGuid(); var item = Guid.NewGuid(); var other = Guid.NewGuid();
            var t = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
            var phoneA = log.Visitor("10.0.0.1", "ua-A", t); var phoneB = log.Visitor("10.0.0.2", "ua-B", t);
            Assert.True(log.Append(client, new ScanEntry(t, item, phoneA)));
            Assert.False(log.Append(client, new ScanEntry(t.AddSeconds(3), item, phoneA)));   // double scan immédiat : une fois
            Assert.True(log.Append(client, new ScanEntry(t.AddMinutes(5), item, phoneA)));    // même téléphone plus tard : un scan de plus
            Assert.True(log.Append(client, new ScanEntry(t.AddMinutes(6), item, phoneB)));
            Assert.True(log.Append(client, new ScanEntry(t.AddMinutes(7), other, phoneB)));
            var c = log.Counts(client, t.AddDays(-1));
            Assert.Equal((3, 2), c[item]);    // 3 scans, 2 visiteurs ce jour-là
            Assert.Equal((1, 1), c[other]);
            Assert.Empty(log.Counts(Guid.NewGuid(), t.AddDays(-1)));   // un autre client ne voit rien
            Assert.Empty(log.Counts(client, t.AddDays(1)));            // avant la date : rien
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Empreinte_du_visiteur_change_chaque_jour_et_ne_contient_pas_l_ip()
    {
        var log = NewLog(out var dir);
        try
        {
            var t = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);
            var a = log.Visitor("192.168.1.77", "ua", t);
            Assert.Equal(a, log.Visitor("192.168.1.77", "ua", t.AddHours(5)));       // même jour : même empreinte
            Assert.NotEqual(a, log.Visitor("192.168.1.77", "ua", t.AddDays(1)));      // le lendemain : autre empreinte (pas de suivi d'un jour à l'autre)
            Assert.NotEqual(a, log.Visitor("192.168.1.78", "ua", t));
            Assert.DoesNotContain("192", a);
            var again = new ScanLog(dir);                                             // le sel est conservé au redémarrage
            Assert.Equal(a, again.Visitor("192.168.1.77", "ua", t));
        }
        finally { Directory.Delete(dir, true); }
    }
}
