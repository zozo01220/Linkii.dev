using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Suivi de publication d'un écran : affichée, en cours d'arrivée, jamais arrivée.</summary>
public class DeliveryTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);
    private readonly Tenant t = new();

    private Screen Screen(int minutesSincePublish, int? seenSecondsAgo = 5) => new()
    {
        Token = "x", PublishedVersion = 1, PublishedUtc = Now.AddMinutes(-minutesSincePublish),
        LastSeenUtc = seenSecondsAgo is { } s ? Now.AddSeconds(-s) : null
    };

    private Helpers.DeliveryInfo? Run(Screen s) => Helpers.Delivery(s, null, t, null, "", Now);

    [Fact]
    public void Never_published_has_no_delivery() => Assert.Null(Helpers.Delivery(new Screen { Token = "x" }, null, t, null, "", Now));

    [Fact]
    public void Online_screen_without_news_is_being_sent() => Assert.Equal(("wip", "Envoi en cours"), Pair(Run(Screen(1))));

    [Fact]
    public void Stage_downloading_then_ready()
    {
        var s = Screen(1);
        var rev = Helpers.Revision(null, s, t, null, "");
        s.DeliveryRevision = rev; s.DeliveryStage = "downloading";
        Assert.Equal("Téléchargement en cours", Run(s)!.Label);
        s.DeliveryStage = "ready";
        Assert.Equal("Reçue, bientôt affichée", Run(s)!.Label);
    }

    [Fact]
    public void Applied_after_publish_is_ok()
    {
        var s = Screen(5);
        s.AppliedRevision = Helpers.Revision(null, s, t, null, ""); s.AppliedUtc = Now.AddMinutes(-4);
        Assert.Equal("ok", Run(s)!.State);
        Assert.StartsWith("Affichée à", Run(s)!.Label);
    }

    [Fact]
    public void Offline_screen_has_not_received() => Assert.Equal("late", Run(Screen(3, seenSecondsAgo: 600))!.State);

    [Fact]
    public void Online_but_silent_for_too_long_is_flagged() => Assert.Equal("late", Run(Screen(30))!.State);

    private static (string, string) Pair(Helpers.DeliveryInfo? d) => (d!.State, d.Label);
}
