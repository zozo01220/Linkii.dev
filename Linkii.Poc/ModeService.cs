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

    /// <summary>Vrai hors espace client, en mode expert, pour un ancien compte, et pendant une consultation par l'équipe Linkii.</summary>
    public bool Expert
    {
        get
        {
            if (ctx.ClientId == null || ctx.IsImpersonating || ctx.UserId is not { } uid) return true;
            return root.Read(d => d.Users.FirstOrDefault(u => u.Id == uid)?.IsExpert() ?? true);
        }
    }

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
            if (!expert) u.ExpertIntroSeen = false;   // repasser en simple réactive la présentation pour le prochain passage en expert
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
