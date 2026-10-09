using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Zones : zone par défaut dans chaque aire, tout écran dans une zone, lecture synchronisée (zone libre).</summary>
public class ZonesTests
{
    readonly Db db = new();
    readonly Tenant client = new() { Name = "Horizon SA" };
    readonly Area hall;
    readonly User admin;

    public ZonesTests()
    {
        db.Clients.Add(client);
        hall = new Area { ClientId = client.Id, Name = "Campus" };
        db.Areas.Add(hall);
        admin = new User { Email = "claire@horizon.ch", Role = Roles.ClientAdmin, ClientId = client.Id };
        db.Users.Add(admin);
    }

    ClientDb As() => new(db, client.Id, AreaAccess.For(db, client.Id, admin.Id));
    Screen S(string name, Guid? zone = null) { var s = new Screen { ClientId = client.Id, AreaId = hall.Id, ZoneId = zone, Name = name, Token = Guid.NewGuid().ToString("N") }; db.Screens.Add(s); return s; }

    [Fact]
    public void Migration_gives_every_area_a_default_zone_and_puts_zoneless_screens_in_it()
    {
        var a = S("Hall");
        var b = S("Guichet", Guid.NewGuid());   // zone qui n'existe plus
        Seed.EnsureAreas(db);
        Seed.EnsureAreas(db);                   // idempotent
        var def = Assert.Single(hall.Zones);
        Assert.True(def.IsDefault);
        Assert.Equal(Zone.DefaultName, def.Name);
        Assert.False(def.Sync);
        Assert.Equal(Zone.Free, def.Kind);
        Assert.Equal(def.Id, a.ZoneId);
        Assert.Equal(def.Id, b.ZoneId);
    }

    [Fact]
    public void An_existing_zone_named_default_becomes_the_default_zone()
    {
        var mine = new Zone { Name = "défaut" };
        hall.Zones.Add(mine);
        Seed.EnsureAreas(db);
        Assert.Single(hall.Zones);
        Assert.True(mine.IsDefault);
    }

    [Fact]
    public void New_areas_start_with_a_default_zone()
    {
        var (area, err) = AreaOps.Create(As(), "Cafétéria");
        Assert.Null(err);
        Assert.True(Assert.Single(area!.Zones).IsDefault);
    }

    [Fact]
    public void The_default_zone_cannot_be_deleted_but_can_be_renamed()
    {
        var def = AreaOps.EnsureDefaultZone(hall);
        Assert.NotNull(AreaOps.DeleteZone(As(), hall.Id, def.Id));
        Assert.Contains(def, hall.Zones);
        Assert.Null(AreaOps.RenameZone(As(), hall.Id, def.Id, "Général"));
        Assert.Equal("Général", def.Name);
        Assert.True(def.IsDefault);
    }

    [Fact]
    public void A_moved_screen_lands_in_the_default_zone_of_the_target_area()
    {
        var other = new Area { ClientId = client.Id, Name = "Annexe" };
        db.Areas.Add(other);
        var s = S("Hall");
        Assert.Null(AreaOps.MoveScreen(As(), s.Id, other.Id));
        Assert.Equal(other.Zones.Single(z => z.IsDefault).Id, s.ZoneId);
    }

    [Fact]
    public void Sync_is_off_by_default_and_the_epoch_is_set_when_it_is_turned_on()
    {
        var def = AreaOps.EnsureDefaultZone(hall);
        Assert.False(def.Sync);
        Assert.Null(Helpers.SyncEpoch(db.Areas, S("Hall", def.Id)));

        var before = DateTime.UtcNow;
        Assert.Null(AreaOps.SetZoneSync(As(), hall.Id, def.Id, true));
        Assert.True(def.Sync);
        Assert.True(def.SyncEpochUtc >= before);
        var epoch = def.SyncEpochUtc;
        Assert.Null(AreaOps.SetZoneSync(As(), hall.Id, def.Id, true));   // déjà actif : l'origine ne bouge pas
        Assert.Equal(epoch, def.SyncEpochUtc);

        Assert.Null(AreaOps.SetZoneSync(As(), hall.Id, def.Id, false));
        Assert.False(def.Sync);
    }

    [Fact]
    public void Only_area_admins_turn_sync_on()
    {
        var def = AreaOps.EnsureDefaultZone(hall);
        var member = new User { Email = "sofia@horizon.ch", Role = Roles.ClientMember, ClientId = client.Id, AreaRoles = { new AreaRole { AreaId = hall.Id, Role = AreaRoles.User } } };
        db.Users.Add(member);
        Assert.NotNull(AreaOps.SetZoneSync(new ClientDb(db, client.Id, AreaAccess.For(db, client.Id, member.Id)), hall.Id, def.Id, true));
        Assert.False(def.Sync);
    }

    [Fact]
    public void Toggling_sync_changes_the_screen_revision_so_the_player_reloads()
    {
        var def = AreaOps.EnsureDefaultZone(hall);
        var s = S("Hall", def.Id);
        var off = Helpers.Revision(null, s, client, null, Helpers.SyncStamp(db.Areas, s));
        AreaOps.SetZoneSync(As(), hall.Id, def.Id, true);
        var on = Helpers.Revision(null, s, client, null, Helpers.SyncStamp(db.Areas, s));
        Assert.NotEqual(off, on);
        AreaOps.SetZoneSync(As(), hall.Id, def.Id, false);
        Assert.Equal(off, Helpers.Revision(null, s, client, null, Helpers.SyncStamp(db.Areas, s)));   // coupée : retour à la révision d'origine
    }

    [Fact]
    public void Only_screens_on_the_same_published_loop_are_aligned()
    {
        var pl = Guid.NewGuid();
        var item = new PublishedItem { Id = Guid.NewGuid(), Type = "image", DurationSec = 10 };
        Screen P(string n, Guid? playlist, int version = 1, int sec = 10)
        {
            var s = S(n);
            s.PlaylistId = playlist; s.PublishedVersion = version;
            s.Published = new() { new PublishedItem { Id = item.Id, Type = "image", DurationSec = sec } };
            return s;
        }
        var a = P("A", pl); var b = P("B", pl);
        var other = P("C", Guid.NewGuid());      // autre liste
        var longer = P("D", pl, sec: 20);        // même liste mais boucle différente (publication pas à jour)
        var never = P("E", pl, version: 0);      // jamais publié
        var none = P("F", null);                 // sans liste
        var synced = Helpers.SyncedScreens(new[] { a, b, other, longer, never, none });
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), synced.OrderBy(x => x));
    }

    [Fact]
    public void Content_driven_durations_are_fixed_in_a_synchronized_zone()
    {
        var video = new MediaItem { Name = "Visite", Type = "app", AppId = "youtube" };
        video.Info["durationSec"] = "222";
        var unknown = new MediaItem { Name = "Autre", Type = "app", AppId = "youtube" };
        var items = new List<PublishedItem>
        {
            new() { Id = Guid.NewGuid(), Type = "app", MediaId = video.Id, DurationMode = "content", DurationSec = 1800 },
            new() { Id = Guid.NewGuid(), Type = "app", MediaId = unknown.Id, DurationMode = "content", DurationSec = 1800 },
            new() { Id = Guid.NewGuid(), Type = "image", DurationSec = 8 }
        };
        var fixedItems = Helpers.FixedForSync(items, new[] { video, unknown });
        Assert.Equal(new[] { 222, 60, 8 }, fixedItems.Select(i => i.DurationSec));
        Assert.All(fixedItems, i => Assert.Equal("fixed", i.DurationMode));
        Assert.Equal("content", items[0].DurationMode);   // l'instantané publié n'est pas modifié
        Assert.Equal(1800, items[0].DurationSec);
    }
}
