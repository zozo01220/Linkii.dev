using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Bandeaux haut et bas d'un écran (maquettes/ecran-bandeaux.html).</summary>
public class BandsTests
{
    [Fact]
    public void Une_seule_case_recoit_les_messages_et_seulement_un_texte()
    {
        var b = new ScreenBands();
        b.Top.On = b.Bottom.On = true;
        b.Top.Center = BandKinds.NewCell("text"); b.Top.Center.Ha = true;
        b.Bottom.Center = BandKinds.NewCell("ticker"); b.Bottom.Center.Ha = true;
        b.Bottom.Left = BandKinds.NewCell("clock"); b.Bottom.Left.Ha = true;   // une horloge ne reçoit pas de message
        var c = Bands.Clean(b);
        Assert.True(c.Top.Center!.Ha);
        Assert.False(c.Bottom.Center!.Ha);
        Assert.False(c.Bottom.Left!.Ha);
        Assert.Same(c.Top.Center, c.Receiver);
    }

    [Fact]
    public void Un_bandeau_eteint_ne_recoit_rien_et_ne_part_pas_au_player()
    {
        var b = new ScreenBands();
        b.Top.Center = BandKinds.NewCell("text"); b.Top.Center.Ha = true;   // bandeau éteint
        Assert.Null(b.Receiver);
        Assert.Null(Bands.ForPlayer(Bands.Clean(b)));
        b.Top.On = true;
        Assert.NotNull(Bands.ForPlayer(Bands.Clean(b)));
    }

    [Fact]
    public void Un_smartphone_n_a_pas_de_bandeau()
    {
        Assert.False(Bands.Allowed(new Screen { ScreenType = "phone" }));
        Assert.True(Bands.Allowed(new Screen { ScreenType = "tablet" }));
    }

    [Fact]
    public void Valeurs_inconnues_et_textes_trop_longs_sont_remis_en_ordre()
    {
        var b = new ScreenBands();
        b.Top.On = true; b.Top.Size = "énorme"; b.Top.Bg = "rose";
        b.Top.Left = BandKinds.NewCell("text"); b.Top.Left.Settings["text"] = new string('x', 500);
        b.Top.Right = new BandCell { Kind = "inconnu" };
        var c = Bands.Clean(b);
        Assert.Equal("moyenne", c.Top.Size);
        Assert.Equal("nuit", c.Top.Bg);
        Assert.Equal(Bands.MaxText, c.Top.Left!.S("text").Length);
        Assert.Null(c.Top.Right);
    }

    [Fact]
    public void Modification_reperee_sans_tenir_compte_de_ce_qui_est_calcule_a_la_publication()
    {
        var a = new ScreenBands(); a.Top.On = true; a.Top.Left = BandKinds.NewCell("logo");
        var b = a.Clone(); b.Top.Left!.ImageUrl = "/media/x.png";
        Assert.True(Bands.Same(a, b));
        b.Top.Size = "large";
        Assert.False(Bands.Same(a, b));
        Assert.True(Bands.Same(null, new ScreenBands()));
    }
}
