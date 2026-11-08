namespace Linkii.Poc;

/// <summary>
/// Commandes de l'appareil (plan Pro) : éteindre l'écran (la mise en veille, qui coupe vraiment l'affichage), redémarrer l'appareil, l'éteindre.
/// Un administrateur les a sur tous les écrans ; un membre, seulement avec le droit correspondant (<see cref="MemberRights"/>) et sur les écrans qu'il a connectés
/// ou qu'on lui a attribués (zones, écrans) : « Voit tout · lecture seule » n'y change rien. Un mur d'écrans se commande en entier.
/// </summary>
public static class DevicePower
{
    /// <summary>Éteindre l'écran n'est pas une commande : il passe par la mise en veille manuelle (<see cref="Screen.ForcedSleep"/>).</summary>
    public const string ScreenOff = "screenoff";

    public static readonly string[] Kinds = [ScreenOff, ScreenCommand.Reboot, ScreenCommand.PowerOff];

    public static string Label(string kind) => kind switch
    {
        ScreenCommand.Unpair => "Réinitialiser l'appareil", ScreenOff => "Éteindre l'écran", ScreenCommand.Reboot => "Redémarrer l'appareil", ScreenCommand.PowerOff => "Éteindre l'appareil", _ => kind
    };

    /// <summary>La personne a-t-elle ce droit (sur au moins certains écrans) ?</summary>
    public static bool Has(AreaAccess a, string kind) => a.IsOrgAdmin || kind switch
    {
        ScreenOff => a.Rights?.ScreenOff == true,
        ScreenCommand.Reboot => a.Rights?.DeviceRestart == true,
        ScreenCommand.PowerOff => a.Rights?.DevicePowerOff == true,
        _ => false
    };

    /// <summary>Au moins une des trois commandes : la section existe pour elle.</summary>
    public static bool HasAny(AreaAccess a) => Kinds.Any(k => Has(a, k));

    /// <summary>Écran de la personne : connecté par elle ou attribué (zone ou écran). Un administrateur : tous.</summary>
    public static bool Mine(AreaAccess a, Screen s) =>
        a.IsOrgAdmin || a.UserId is { } uid && (s.OwnerId == uid || a.Rights is { } r && (s.ZoneId is { } z && r.ZoneIds.Contains(z) || r.ScreenIds.Contains(s.Id)));

    public static bool Can(AreaAccess a, Screen s, string kind) => Has(a, kind) && Mine(a, s);

    /// <summary>
    /// Pourquoi l'appareil de cet écran ne sait pas faire la commande (null : il sait). Windows et Raspberry : tout. Android : redémarrer seulement s'il est propriétaire
    /// de l'appareil (mise en service dédiée), jamais l'éteindre. Navigateur : aucune commande.
    /// </summary>
    public static string? Unsupported(Screen s, string kind) => kind is ScreenOff or ScreenCommand.Unpair ? null : s.DeviceKind switch   // réinitialiser : tout player sait oublier son appairage   // l'écran noir de la veille marche partout, navigateur compris
    {
        "windows" or "raspberrypi" => null,
        "android" or "androidtv" => kind switch
        {
            ScreenOff => null,
            ScreenCommand.Reboot => s.DeviceOwner ? null : "Android : l'application doit être propriétaire de l'appareil (mise en service dédiée) pour le redémarrer.",
            _ => "Android : une application ne peut pas éteindre l'appareil."
        },
        _ => "Cet appareil n'est pas piloté par Linkivi."
    };

    /// <summary>Ce que fera une commande demandée sur ces écrans (un écran de mur entraîne tout le mur).</summary>
    public record Plan(List<Screen> Targets, List<(Screen Screen, string Why)> Skipped, List<(Screen Screen, List<Screen> Others)> Shared)
    {
        public bool Any => Targets.Count > 0;
    }

    public static Plan PlanFor(ClientDb d, IEnumerable<Guid> ids, string kind)
    {
        var a = d.Access;
        var wanted = ids.ToHashSet();
        var all = d.Root.Screens.Where(s => s.ClientId == d.ClientId && s.Token != null).ToList();
        var targets = new List<Screen>();
        var skipped = new List<(Screen, string)>();
        var seen = new HashSet<Guid>();

        foreach (var s in all.Where(s => wanted.Contains(s.Id)))
        {
            if (!seen.Add(s.Id)) continue;
            // un mur se commande en entier : un seul écran demandé entraîne tous les autres
            var wall = Helpers.ZoneOf(d.Root.Areas, s) is { IsWall: true } z ? z : null;
            var group = wall == null ? [s] : all.Where(x => x.ZoneId == wall.Id).ToList();
            foreach (var g in group) seen.Add(g.Id);
            if (!Has(a, kind)) { skipped.AddRange(group.Select(g => (g, "Vous n'avez pas ce droit."))); continue; }
            if (!group.Any(g => Mine(a, g))) { skipped.AddRange(group.Select(g => (g, "Cet écran ne vous est pas attribué."))); continue; }
            foreach (var g in group)
            {
                if (Unsupported(g, kind) is { } why) skipped.Add((g, why));
                else targets.Add(g);
            }
        }

        // un PC peut afficher d'autres écrans : le redémarrer ou l'éteindre les coupe aussi ; une personne ne coupe pas ceux qui ne sont pas à elle
        var shared = new List<(Screen, List<Screen>)>();
        if (kind is ScreenCommand.Reboot or ScreenCommand.PowerOff)
        {
            foreach (var t in targets.ToList())
            {
                var others = DeviceConfig.SamePc(all, t).Where(o => !targets.Contains(o)).ToList();
                if (others.Count == 0) continue;
                if (!a.IsOrgAdmin && others.Any(o => !Mine(a, o) || !Has(a, kind)))
                {
                    targets.Remove(t);
                    skipped.Add((t, $"Ce PC affiche aussi {string.Join(", ", others.Where(o => !Mine(a, o)).Select(o => "« " + o.Name + " »"))}, qui ne vous {(others.Count(o => !Mine(a, o)) > 1 ? "sont" : "est")} pas attribué{(others.Count(o => !Mine(a, o)) > 1 ? "s" : "")}."));
                    continue;
                }
                shared.Add((t, others));
            }
        }
        return new(targets, skipped, shared);
    }
}
