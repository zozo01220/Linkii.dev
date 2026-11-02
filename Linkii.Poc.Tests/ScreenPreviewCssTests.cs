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
