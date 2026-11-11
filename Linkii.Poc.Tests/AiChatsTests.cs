using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>
/// Discussions de l'assistant : gardées 30 jours pour leur auteur seul (ni l'administrateur ni un autre membre), 50 au plus,
/// les épinglées toujours ; ce que l'IA reçoit d'une longue discussion (résumé des anciens échanges, les 6 derniers en entier).
/// </summary>
public class AiChatsTests
{
    readonly Guid client = Guid.NewGuid(), other = Guid.NewGuid(), me = Guid.NewGuid(), admin = Guid.NewGuid();
    readonly DateTime now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    AiChat Chat(Guid user, int daysAgo, bool pinned = false, Guid? cid = null) =>
        new() { ClientId = cid ?? client, UserId = user, Title = "t", Updated = now.AddDays(-daysAgo), Pinned = pinned };

    [Fact]
    public void Seul_l_auteur_voit_ses_discussions()
    {
        var d = new Db();
        var mine = Chat(me, 1);
        d.AiChats.AddRange([mine, Chat(admin, 1), Chat(me, 1, cid: other)]);
        Assert.Equal([mine], AiChats.Mine(d, client, me));
        Assert.Null(AiChats.Get(d, client, admin, mine.Id));
        Assert.Null(AiChats.Get(d, other, me, mine.Id));
        Assert.Same(mine, AiChats.Get(d, client, me, mine.Id));
    }

    [Fact]
    public void Effacees_apres_30_jours_sauf_epinglees()
    {
        var d = new Db();
        var young = Chat(me, 29); var old = Chat(me, 31); var pinned = Chat(me, 200, pinned: true); var others = Chat(admin, 90);
        d.AiChats.AddRange([young, old, pinned, others]);
        AiChats.Purge(d, client, me, now);
        Assert.Contains(young, d.AiChats);
        Assert.Contains(pinned, d.AiChats);
        Assert.DoesNotContain(old, d.AiChats);
        Assert.Contains(others, d.AiChats);   // la purge d'une personne ne touche pas aux discussions des autres
    }

    [Fact]
    public void Au_plus_50_par_personne_les_plus_recentes()
    {
        var d = new Db();
        for (var i = 0; i < 55; i++) d.AiChats.Add(new AiChat { ClientId = client, UserId = me, Updated = now.AddMinutes(-i) });
        var pin = Chat(me, 2, pinned: true);
        d.AiChats.Add(pin);
        AiChats.Purge(d, client, me, now);
        Assert.Equal(51, d.AiChats.Count);
        Assert.Contains(pin, d.AiChats);
        Assert.DoesNotContain(d.AiChats, c => c.Updated < now.AddMinutes(-49) && !c.Pinned);
    }

    [Fact]
    public void Titre_coupe_proprement()
    {
        Assert.Equal("Quels écrans sont hors ligne ?", AiChats.TitleOf("  Quels écrans   sont hors ligne ?"));
        var t = AiChats.TitleOf("Affiche un message d'accueil pour la visite des clients jeudi, dans le hall, de 9 h à 12 h");
        Assert.EndsWith("…", t);
        Assert.True(t.Length <= 61);
        Assert.Equal("Discussion", AiChats.TitleOf(""));
    }

    [Fact]
    public void Rubriques_de_la_liste()
    {
        var n = new DateTime(2026, 11, 11, 15, 0, 0);
        Assert.Equal("Aujourd'hui", AiChats.Group(new AiChat(), n, n.AddHours(-3)));
        Assert.Equal("Hier", AiChats.Group(new AiChat(), n, n.AddDays(-1)));
        Assert.Equal("Cette semaine", AiChats.Group(new AiChat(), n, n.AddDays(-4)));
        Assert.Equal("Plus ancien", AiChats.Group(new AiChat(), n, n.AddDays(-10)));
        Assert.Equal("Épinglées", AiChats.Group(new AiChat { Pinned = true }, n, n.AddDays(-10)));
    }

    [Fact]
    public void Longue_discussion_resumee_pour_l_IA()
    {
        var lines = Enumerable.Range(1, 9).Select(i => new AiChatLine { Ask = "demande " + i, Say = "réponse " + i, Acts = i == 1 ? [new AiChatAct { Label = "Créer la liste", State = "fait" }] : [] }).ToList();
        var c = AiChats.Conversation(lines);
        Assert.Contains("Plus tôt dans la discussion (résumé)", c);
        Assert.Contains("demande 1 (1 action faite)", c);
        Assert.DoesNotContain("réponse 3", c);            // anciens échanges : la demande seule
        Assert.Contains("Assistant : réponse 9", c);      // les 6 derniers en entier
        Assert.Contains("Personne : demande 4", c);
    }

    [Fact]
    public void Carte_d_une_page_gardee_par_son_resume()
    {
        var t = new PanelTurn { Ask = "Améliorer ce texte", From = "Bandeau · Texte", Card = _ => { }, Summary = () => "Texte remplacé par : « Bonjour »" };
        var line = t.ToLine();
        Assert.Equal("Texte remplacé par : « Bonjour »", line.Say);
        Assert.Equal("Bandeau · Texte", line.From);
        var back = PanelTurn.FromLine(line);
        Assert.True(back.Old);
        Assert.False(back.Live);
        Assert.Equal(line.Say, back.Said);
    }
}
