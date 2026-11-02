using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>
/// Cartes écrans : le cadre d'aperçu (en overflow visible pour dessiner son pied) doit pouvoir rétrécir, sinon un écran en portrait
/// s'élargit à la largeur de l'image de sa liste et s'affiche en paysage.
/// </summary>
public class ScreenPreviewCssTests
{
    private static string Css()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Linkii.Poc", "wwwroot", "app.css"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "Linkii.Poc", "wwwroot", "app.css"));
    }

    [Fact]
    public void Le_cadre_d_apercu_peut_retrecir_et_les_vignettes_remplissent_la_zone()
    {
        var css = System.Text.RegularExpressions.Regex.Replace(Css(), @"\s+", " ");
        Assert.Contains(".gc-mon, .sc-monitor { min-width: 0;", css);
        Assert.Contains(".gc-mon.portrait, .sc-monitor.portrait { max-width: 100%; }", css);
        Assert.Contains(".sc-zone > img, .sc-zone > video { width: 100%; height: 100%; object-fit: cover;", css);
    }
}

/// <summary>Éditeur d'écran = écran : le widget Home Assistant a les mêmes dimensions dans l'éditeur et dans le player.</summary>
public class WidgetPreviewParityTests
{
    private static string Wwwroot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Linkii.Poc", "wwwroot", "app.css"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Linkii.Poc", "wwwroot");
    }

    [Fact]
    public void Les_interrupteurs_du_player_heritent_de_la_taille_du_texte()
    {
        // un <button> ne l'hérite pas : ses « em » partaient de 13 px et le widget devenait plus haut que dans l'éditeur
        var html = File.ReadAllText(Path.Combine(Wwwroot(), "player", "index.html"));
        Assert.Matches(@"\.ha-sw \{ font: inherit;", html);
    }

    [Fact]
    public void Le_widget_Home_Assistant_n_a_pas_d_encadre_en_double_dans_l_editeur()
    {
        var css = File.ReadAllText(Path.Combine(Wwwroot(), "app.css"));
        Assert.Contains(".wg-item.wg-item-ha { padding: 0;", css);
    }

    [Fact]
    public void L_editeur_garde_les_widgets_dans_l_ecran_comme_le_player()
    {
        var js = File.ReadAllText(Path.Combine(Wwwroot(), "widget-canvas.js"));
        Assert.Contains("fit: fit", js);
    }
}
