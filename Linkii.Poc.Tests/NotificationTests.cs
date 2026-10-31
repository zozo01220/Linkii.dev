using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Centre de notifications : ce qui y entre selon les réglages d'alertes, regroupement, résolution, lu / supprimé par personne.</summary>
public class NotificationTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "linkii-nt-" + Guid.NewGuid().ToString("N"));
    private readonly JsonStore store;
    private readonly NotificationCenter center;
    private readonly Tenant tenant = new() { Name = "Horizon SA", Plan = Plans.Growth };
    private readonly User alice = new(), bob = new();

    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Linkivi";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public NotificationTests()
    {
        store = new JsonStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Linkii:DataDir"] = dir }).Build(), new Env());
        center = new NotificationCenter(store);
        foreach (var u in new[] { alice, bob }) { u.ClientId = tenant.Id; u.Role = Roles.ClientAdmin; }
        store.Write(db => { db.Clients.Add(tenant); db.Users.Add(alice); db.Users.Add(bob); });
    }

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    [Fact]
    public void Billing_and_news_always_reach_the_center_other_types_follow_the_settings()
    {
        var essential = new Tenant { Plan = Plans.Base };
        Assert.True(Alerts.InCenter(essential, Alerts.Billing));
        Assert.True(Alerts.InCenter(essential, Alerts.News));
        Assert.False(Alerts.InCenter(essential, Alerts.Screens));

        var pro = new Tenant { Plan = Plans.Growth, AlertsOn = true, AlertPush = true, AlertTypes = new() { Alerts.Screens } };
        Assert.True(Alerts.InCenter(pro, Alerts.Screens));
        Assert.False(Alerts.InCenter(pro, Alerts.Sync));      // type non suivi
        pro.AlertPush = false;
        Assert.False(Alerts.InCenter(pro, Alerts.Screens));   // « par notification » coupé : ni cloche ni téléphone
        pro.AlertPush = true; pro.AlertsOn = false;
        Assert.False(Alerts.InCenter(pro, Alerts.Screens));
        Assert.True(Alerts.InCenter(pro, Alerts.Billing));    // l'abonnement ne se coupe pas
    }

    [Fact]
    public void Good_news_only_when_asked()
    {
        var c = new Tenant { Plan = Plans.Growth, AlertsOn = true, AlertPush = true, AlertTypes = new() { Alerts.Screens } };
        Assert.False(Alerts.InCenter(c, Alerts.Screens, good: true));
        Assert.False(Alerts.Wants(c, Alerts.Screens, good: true));
        c.AlertGood = true;
        Assert.True(Alerts.InCenter(c, Alerts.Screens, good: true));
        Assert.True(Alerts.Wants(c, Alerts.Screens, good: true));
    }

    [Fact]
    public void Read_and_delete_are_per_person()
    {
        center.Add(tenant.Id, Alerts.Sync, Notifs.Warn, "Synchronisation en échec", "Dossier Menus", "/media");
        var id = center.For(tenant.Id, alice.Id).Single().Id;
        Assert.Equal(1, center.Unread(tenant.Id, alice.Id));

        center.SetRead(tenant.Id, alice.Id, new[] { id }, true);
        Assert.Equal(0, center.Unread(tenant.Id, alice.Id));
        Assert.Equal(1, center.Unread(tenant.Id, bob.Id));

        center.Delete(tenant.Id, alice.Id, new[] { id });
        Assert.Empty(center.For(tenant.Id, alice.Id));
        Assert.Single(center.For(tenant.Id, bob.Id));

        center.Delete(tenant.Id, bob.Id, new[] { id });   // supprimée par tous les administrateurs : elle disparaît pour de bon
        Assert.Empty(store.Read(db => db.Notifications.ToList()));
    }

    [Fact]
    public void The_person_who_acted_does_not_have_to_read_it()
    {
        center.Add(tenant.Id, Alerts.Members, Notifs.Info, "Nouvelle personne", "Camille a été ajouté(e).", "/organisation", readBy: alice.Id);
        Assert.Equal(0, center.Unread(tenant.Id, alice.Id));
        Assert.Equal(1, center.Unread(tenant.Id, bob.Id));
    }

    [Fact]
    public void The_same_event_groups_into_one_notification_and_becomes_unread_again()
    {
        center.Add(tenant.Id, Alerts.Sync, Notifs.Warn, "Synchronisation en échec", "Dossier Menus", "/media", dedupe: "sync:Menus");
        center.SetRead(tenant.Id, alice.Id, new[] { center.For(tenant.Id, alice.Id).Single().Id }, true);
        center.Add(tenant.Id, Alerts.Sync, Notifs.Warn, "Synchronisation en échec", "Dossier Menus, encore", "/media", dedupe: "sync:Menus");

        var n = center.For(tenant.Id, alice.Id).Single();
        Assert.Equal(2, n.Count);
        Assert.Equal(1, center.Unread(tenant.Id, alice.Id));
    }

    [Fact]
    public void Screens_back_online_resolve_the_offline_notification()
    {
        center.Add(tenant.Id, Alerts.Screens, Notifs.Error, "2 écrans hors ligne", "Hall, Salle 2", "/", subjects: new[] { "Hall", "Salle 2" });

        center.Back(tenant.Id, new[] { "Hall" });
        var n = center.For(tenant.Id, alice.Id).Single();
        Assert.Null(n.ResolvedUtc);
        Assert.Equal("Écran hors ligne : Salle 2", n.Title);

        center.Back(tenant.Id, new[] { "Salle 2" });
        n = center.For(tenant.Id, alice.Id).Single();
        Assert.NotNull(n.ResolvedUtc);
        Assert.Equal(Notifs.Ok, n.Severity);
        Assert.Equal(0, center.Unread(tenant.Id, alice.Id));   // rangée dans les lues
    }

    [Fact]
    public void Notifications_stay_inside_their_organisation_and_old_ones_are_purged()
    {
        var other = new Tenant { Name = "Autre" };
        store.Write(db => db.Clients.Add(other));
        center.Add(other.Id, Alerts.Sync, Notifs.Warn, "Autre", "", "/");
        Assert.Empty(center.For(tenant.Id, alice.Id));

        center.Add(tenant.Id, Alerts.Sync, Notifs.Warn, "Ancienne", "", "/");
        store.Write(db => db.Notifications.First(n => n.Title == "Ancienne").CreatedUtc = DateTime.UtcNow.AddDays(-91));
        center.Add(tenant.Id, Alerts.Sync, Notifs.Warn, "Récente", "", "/");
        Assert.Equal(new[] { "Récente" }, center.For(tenant.Id, alice.Id).Select(n => n.Title));
    }

    [Fact]
    public void Welcome_message_is_added_once_per_organisation()
    {
        var db = new Db();
        db.Clients.Add(tenant);
        NotificationCenter.Seed(db);
        NotificationCenter.Seed(db);
        Assert.Single(db.Notifications);
        Assert.Equal(Alerts.News, db.Notifications[0].Kind);
    }
}
