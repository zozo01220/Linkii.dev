using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Période de diffusion d'un contenu : sans limite, début seul, fin seule, les deux ; libellés de la pastille.</summary>
public class ContentWindowTests
{
    private const string Now = "2026-12-10T12:00";

    [Fact]
    public void Sans_periode_le_contenu_passe_toujours()
    {
        Assert.True(ContentWindow.Plays(null, null, Now));
        Assert.Null(ContentWindow.Chip(null, null, Now));
    }

    [Fact]
    public void Periode_complete_diffuse_seulement_entre_les_bornes()
    {
        Assert.True(ContentWindow.Plays("2026-12-01T08:00", "2026-12-24T18:00", Now));
        Assert.False(ContentWindow.Plays("2026-12-11T08:00", "2026-12-24T18:00", Now));   // pas encore commencé
        Assert.False(ContentWindow.Plays("2026-12-01T08:00", "2026-12-10T11:59", Now));   // déjà terminé
    }

    [Fact]
    public void Debut_seul_ou_fin_seule()
    {
        Assert.True(ContentWindow.Plays("2026-12-01T08:00", null, Now));
        Assert.False(ContentWindow.Plays("2026-12-11T00:00", null, Now));
        Assert.True(ContentWindow.Plays(null, "2026-12-24T18:00", Now));
        Assert.False(ContentWindow.Plays(null, "2026-12-09T23:59", Now));
    }

    [Fact]
    public void Pastille_selon_l_etat()
    {
        Assert.Equal(("now", "Jusqu'au 24 déc. à 18:00"), ContentWindow.Chip("2026-12-01T08:00", "2026-12-24T18:00", Now));
        Assert.Equal(("soon", "Démarre le 1 déc. à 8:00"), ContentWindow.Chip("2026-12-01T08:00", "2026-12-24T18:00", "2026-11-30T12:00"));
        Assert.Equal(("done", "Terminé le 24 nov."), ContentWindow.Chip("2026-11-01T08:00", "2026-11-24T18:00", Now));
    }

    [Fact]
    public void Validation_fin_apres_debut_et_formats()
    {
        Assert.True(ContentWindow.Valid("2026-12-01T08:00", "2026-12-24T18:00", out _));
        Assert.True(ContentWindow.Valid(null, null, out _));
        Assert.False(ContentWindow.Valid("2026-12-24T18:00", "2026-12-01T08:00", out var err));
        Assert.Equal("La fin doit être après le début.", err);
        Assert.False(ContentWindow.Valid("2026-13-45T08:00", null, out _));
    }
}
