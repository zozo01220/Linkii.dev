namespace Linkii.Poc;

/// <summary>État d'un client tel que la console Linkii l'affiche : un seul état à la fois, du plus bloquant au plus ordinaire.</summary>
public static class ClientStates
{
    public const string Active = "Actif", Trial = "Essai", Ended = "Essai terminé", Pending = "À valider", Suspended = "Suspendu", Blocked = "Bloqué", Cancelled = "Résilié";
    public static readonly string[] All = { Active, Trial, Pending, Ended, Cancelled, Suspended, Blocked };

    public static string Of(Tenant c, User? owner, DateTime now) =>
        c.Blocked ? Blocked : c.Suspended ? Suspended : !Signup.IsVerified(owner) ? Pending : c.SubscriptionEnded(now) ? Cancelled
        : c.TrialEnded(now) && !c.Subscribed ? Ended : c.InTrial(now) ? Trial : Active;

    /// <summary>Classe de pastille : ok | ac | warn | bad | mute.</summary>
    public static string Css(string state) => state switch
    {
        Active => "ok", Trial => "ac", Suspended => "warn", Ended => "warn", Cancelled => "warn", Blocked => "bad", _ => "mute"
    };

    public static string Color(string state) => state switch
    {
        Active => "#1E8E5A", Trial => "#3B82C4", Pending => "#9AA6B6", Ended => "#E0A93B", Cancelled => "#E0A93B", Suspended => "#D79A2B", _ => "#C8372D"
    };
}
