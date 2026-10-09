using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Widgets d'écran QR code, compte à rebours et citation du jour.</summary>
public class WidgetLogicTests
{
    [Fact]
    public void Qr_code_encode_un_trace_valide_ou_rien()
    {
        var q = QrCodes.Build("https://linkii.ch");
        Assert.NotNull(q);
        Assert.True(q!.Value.Size >= 21);
        Assert.Matches("^[0-9MhvzH\\- ]+$", q.Value.Path);   // le player n'accepte que ce jeu de caractères
        Assert.StartsWith(q.Value.Size + "|", QrCodes.Encode("https://linkii.ch"));
        Assert.Null(QrCodes.Build("   "));
        Assert.Null(QrCodes.Encode(null));
        Assert.Null(QrCodes.Build(new string('x', 5000)));   // trop long : pas de plantage
    }

    [Fact]
    public void Compte_a_rebours_en_jours()
    {
        var now = new DateTime(2026, 10, 8, 15, 0, 0);
        var r = Countdown.Compute(now, "2026-10-20", false, true, null);
        Assert.Equal(("12", "jours"), (r.Big, r.Unit));
        Assert.Equal(("1", "jour"), (Countdown.Compute(now, "2026-10-09", false, true, null).Big, Countdown.Compute(now, "2026-10-09", false, true, null).Unit));
    }

    [Fact]
    public void Compte_a_rebours_le_jour_J_puis_le_lendemain()
    {
        var date = "2026-10-08";
        var jour = Countdown.Compute(new DateTime(2026, 10, 8, 9, 0, 0), date, false, true, "Bravo !");
        Assert.Equal("Bravo !", jour.Big);
        Assert.False(jour.Hidden);
        Assert.True(Countdown.Compute(new DateTime(2026, 10, 9, 9, 0, 0), date, false, true, "Bravo !").Hidden);          // masqué le lendemain
        Assert.False(Countdown.Compute(new DateTime(2026, 10, 9, 9, 0, 0), date, false, false, "Bravo !").Hidden);        // message gardé
    }

    [Fact]
    public void Compte_a_rebours_detaille()
    {
        Assert.Equal("2 j 06 h 30 min", Countdown.Compute(new DateTime(2026, 10, 8, 17, 30, 0), "2026-10-11", true, true, null).Big);
        Assert.Equal("02:00:00", Countdown.Compute(new DateTime(2026, 10, 10, 22, 0, 0), "2026-10-11", true, true, null).Big);
    }

    [Fact]
    public void Compte_a_rebours_date_illisible() =>
        Assert.Equal("—", Countdown.Compute(DateTime.Now, "pas une date", false, true, null).Big);

    [Fact]
    public void Citations_avec_ou_sans_auteur()
    {
        var q = Quotes.Parse("Le succès est la somme de petits efforts. — R. Collier\n\n  Une astuce simple  \nA - B - Camus");
        Assert.Equal(3, q.Count);
        Assert.Equal(("Le succès est la somme de petits efforts.", "R. Collier"), (q[0].Text, q[0].Author));
        Assert.Equal(("Une astuce simple", (string?)null), (q[1].Text, q[1].Author));
        Assert.Equal(("A - B", "Camus"), (q[2].Text, q[2].Author));   // l'auteur est ce qui suit le dernier tiret
    }

    [Fact]
    public void Citation_du_jour_change_a_minuit_et_la_rotation_a_l_intervalle()
    {
        var a = Quotes.Index(new DateTime(2026, 10, 8, 8, 0, 0), "daily", 30, 5);
        Assert.Equal(a, Quotes.Index(new DateTime(2026, 10, 8, 23, 59, 0), "daily", 30, 5));
        Assert.Equal((a + 1) % 5, Quotes.Index(new DateTime(2026, 10, 9, 0, 1, 0), "daily", 30, 5));
        Assert.Equal(0, Quotes.Index(DateTime.Now, "daily", 30, 0));   // liste vide : pas de division par zéro
        var t = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal((Quotes.Index(t, "rotate", 30, 7) + 1) % 7, Quotes.Index(t.AddSeconds(30), "rotate", 30, 7));
    }

    [Fact]
    public void Les_trois_manifestes_sont_valides()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Linkii.Poc", "Apps");
        var cat = new AppCatalog(dir, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        foreach (var id in new[] { "qrcode", "countdown", "quote" })
        {
            var m = cat.Find(id);
            Assert.NotNull(m);
            Assert.True(m!.ScreenWidget);
        }
    }
}
