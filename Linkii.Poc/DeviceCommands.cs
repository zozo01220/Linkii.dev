namespace Linkii.Poc;

/// <summary>Résultat d'une commande d'appareil demandée de l'extérieur (API, Home Assistant) : les écrans touchés, ceux qui l'ont refusée et pourquoi.</summary>
public record DeviceCommandResult(string? Error, List<Guid> Done, List<(Guid Id, string Why)> Skipped, bool NotFound = false)
{
    public bool Ok => Error == null;
}

/// <summary>
/// Les cinq commandes d'un écran pilotable de l'extérieur (module « Écrans » de l'API, intégration Home Assistant) : redémarrer le player, redémarrer l'appareil,
/// éteindre l'appareil, éteindre l'écran (veille manuelle) et le réveiller. Mêmes règles que le back-office (<see cref="DevicePower"/> : plan Pro, écran en ligne,
/// matériel compatible, un mur se commande en entier) ; une clé d'API agit « au nom » de son propriétaire, donc avec ses droits : un administrateur peut tout,
/// un membre seulement ce que sa fiche lui permet sur les écrans qui lui sont attribués.
/// </summary>
public class DeviceCommandService(JsonStore store, ScreenControl control, Notifier notifier, HistoryLog history)
{
    public const string RestartPlayer = "restart-player", Reboot = "reboot", PowerOff = "poweroff", ScreenOff = "screen-off", Wake = "wake";

    public static readonly string[] All = [RestartPlayer, Reboot, PowerOff, ScreenOff, Wake];

    public static bool IsKnown(string? command) => command != null && All.Contains(command);

    /// <summary>Droit et compatibilité de l'appareil : le droit « Éteindre l'écran » couvre ScreenOff et Wake.</summary>
    private static string RightKind(string command) => command switch
    {
        Reboot => ScreenCommand.Reboot,
        PowerOff => ScreenCommand.PowerOff,
        ScreenOff or Wake => DevicePower.ScreenOff,
        _ => ScreenCommand.Restart
    };

    /// <summary>Commandes que le matériel de cet écran sait faire (Android : redémarrer l'appareil seulement s'il en est propriétaire, jamais l'éteindre).</summary>
    public static string[] Supported(Screen s) => All.Where(c => c is RestartPlayer or ScreenOff or Wake || DevicePower.Unsupported(s, RightKind(c)) == null).ToArray();

    public async Task<DeviceCommandResult> Run(Guid clientId, Guid? ownerId, Guid screenId, string command, string by)
    {
        if (!IsKnown(command)) return new("Commande inconnue : " + string.Join(", ", All) + ".", [], []);
        var now = DateTime.UtcNow;
        var plan = store.Read(db =>
        {
            var t = db.Clients.FirstOrDefault(c => c.Id == clientId);
            if (t == null || !t.IsGrowth) return null;
            var d = new ClientDb(db, clientId, AreaAccess.For(db, clientId, ownerId));
            if (command != RestartPlayer) return DevicePower.PlanFor(d, [screenId], RightKind(command));
            var s = db.Screens.FirstOrDefault(x => x.Id == screenId && x.ClientId == clientId && x.Token != null);
            if (s == null) return new DevicePower.Plan([], [], []);
            return d.Access.Sees?.Invoke(s) == false ? new DevicePower.Plan([], [(s, "Cet écran ne vous est pas attribué.")], []) : new DevicePower.Plan([s], [], []);
        });
        if (plan == null) return new(Plans.LockedHint + ".", [], []);
        var skipped = plan.Skipped.Select(x => (x.Screen.Id, x.Why)).ToList();
        if (!plan.Any) return plan.Skipped.Count == 0 ? new("Écran introuvable.", [], [], true) : new(skipped[0].Why, [], skipped);

        var done = new List<Guid>();
        switch (command)
        {
            case ScreenOff or Wake:
            {
                var ids = plan.Targets.Select(s => s.Id).ToList();
                var until = now.AddMinutes(Sleep.WakeMinutes);
                store.Write(db =>
                {
                    foreach (var s in db.Screens.Where(x => ids.Contains(x.Id)))
                    {
                        if (command == ScreenOff) { s.ForcedSleep = true; s.WakeUntilUtc = null; }
                        else { s.ForcedSleep = false; s.WakeUntilUtc = until; }
                    }
                });
                foreach (var id in ids) await notifier.NotifyScreen(id);   // un écran éveillé s'éteint aussitôt ; un écran en veille relève le réveil à la minute suivante
                var names = plan.Targets.Select(s => s.Name).ToList();
                history.Record(clientId, History.Screens, History.Sleep,
                    $"« {names[0]} »{(names.Count > 1 ? $" et {names.Count - 1} autre{(names.Count > 2 ? "s" : "")}" : "")} {(command == ScreenOff ? "mis en veille jusqu'au réveil" : $"réveillé pour {Sleep.WakeMinutes} min")}",
                    Notifs.Info, names.Count > 1 ? string.Join(", ", names) : null, by, targetKind: "screen", targetId: ids.Count == 1 ? ids[0] : null, targetName: names[0]);
                done.AddRange(ids);
                break;
            }
            default:
                foreach (var s in plan.Targets)
                {
                    var r = await control.Send(clientId, s.Id, RightKind(command), by);
                    if (r.Ok) done.Add(s.Id);
                    else skipped.Add((s.Id, r.Error ?? "La commande n'a pas pu être envoyée."));
                }
                break;
        }
        return done.Count == 0 ? new(skipped.FirstOrDefault().Why ?? "La commande n'a pas pu être envoyée.", done, skipped) : new(null, done, skipped);
    }
}
