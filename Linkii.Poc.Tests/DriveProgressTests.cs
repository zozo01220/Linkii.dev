using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Avancement affiché pendant l'import d'un dossier Drive.</summary>
public class DriveProgressTests
{
    static DriveService.SyncProgress P(string phase, int done, int total, long bytes, long bytesTotal) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Affiches", phase, done, total, bytes, bytesTotal, 0, "", DateTime.UtcNow);

    [Fact] public void Lecture_du_dossier_duree_inconnue() => Assert.Null(P("listing", 0, 0, 0, 0).Fraction);
    [Fact] public void Mise_a_jour_des_ecrans_duree_inconnue() => Assert.Null(P("updating", 5, 5, 10, 10).Fraction);
    [Fact] public void Import_a_l_octet_pres() => Assert.Equal(0.25, P("importing", 1, 4, 250, 1000).Fraction);
    [Fact] public void Import_au_nombre_de_fichiers_si_tailles_inconnues() => Assert.Equal(0.5, P("importing", 2, 4, 0, 0).Fraction);
    [Fact] public void Jamais_plus_de_100_pour_cent() => Assert.Equal(1.0, P("importing", 4, 4, 1200, 1000).Fraction);
}
