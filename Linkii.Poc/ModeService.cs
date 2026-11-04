using Microsoft.AspNetCore.Components;

namespace Linkii.Poc;

/// <summary>
/// Mode d'utilisation de la personne connectée : simple (vue réduite) ou expert (toutes les fonctions).
/// Ce n'est qu'un affichage : les niveaux de droits ne changent jamais. Le choix est enregistré sur le compte.
/// </summary>
public class ModeService(JsonStore root, TenantContext ctx, NavigationManager nav)
{
    /// <summary>La présentation du mode expert doit s'afficher (premier passage en expert).</summary>
    public bool IntroDue { get; private set; }

    /// <summary>Le mode a changé : la barre latérale (mise en page) doit se redessiner.</summary>
    public event Action? Changed;

    /// <summary>Vrai hors espace client, en mode expert, pour un ancien compte, et pendant une consultation par l'équipe Linkivi.</summary>
    public bool Expert
    {
        get
        {
            if (ctx.ClientId == null || ctx.IsImpersonating || ctx.UserId is not { } uid) return true;
            return root.Read(d => d.Users.FirstOrDefault(u => u.Id == uid)?.IsExpert() ?? true);
        }
    }

    /// <summary>Mode simple (le contraire de <see cref="Expert"/>) : voir <see cref="SimpleMode"/> pour ce qu'il masque.</summary>
    public bool Simple => !Expert;

    /// <summary>Passe en mode simple ou expert. Au premier passage en expert, la présentation s'ouvre avant de recharger la page.</summary>
    public void Switch(bool expert)
    {
        if (ctx.UserId is not { } uid || ctx.IsImpersonating) return;
        var seen = true;
        root.Write(d =>
        {
            if (d.Users.FirstOrDefault(u => u.Id == uid) is not { } u) return;
            u.ExpertMode = expert;
            seen = u.ExpertIntroSeen;
        });
        IntroDue = expert && !seen;
        if (IntroDue) Changed?.Invoke();
        else nav.NavigateTo(nav.Uri, forceLoad: true);   // toute la page est relue dans le nouveau mode
    }

    /// <summary>Ferme la présentation. « Ne plus afficher » la retient sur le compte ; sinon elle reviendra au prochain passage en expert.</summary>
    public void DismissIntro(bool dontShowAgain)
    {
        var uid = ctx.UserId;
        if (dontShowAgain && uid is { } id)
            root.Write(d => { if (d.Users.FirstOrDefault(u => u.Id == id) is { } u) u.ExpertIntroSeen = true; });
        IntroDue = false;
        nav.NavigateTo(nav.Uri, forceLoad: true);
    }
}

/// <summary>
/// Règles du mode simple (4 novembre 2026), réunies ici pour les ajuster facilement : c'est un affichage, les droits ne changent pas.
/// Intégrations limitées à la liste ci-dessous, affichage en cartes seulement, pas de dossiers (tout est montré à plat),
/// ajout depuis l'ordinateur seulement, ni Réglages, ni Player, ni Abonnement, ni commandes de l'appareil, veille, luminosité ou planning.
/// Les contenus déjà créés en mode expert restent diffusés ; les plannings tournent sans être visibles.
/// </summary>
public static class SimpleMode
{
    /// <summary>Intégrations proposées en mode simple.</summary>
    public static readonly IReadOnlySet<string> Apps = new HashSet<string> { "image", "video", "slideshow-images", "slideshow-videos", "webpage", Compositions.AppId };

    /// <summary>L'intégration est proposée dans ce mode.</summary>
    public static bool Offers(bool expert, string? appId) => expert || appId != null && Apps.Contains(appId);
}
