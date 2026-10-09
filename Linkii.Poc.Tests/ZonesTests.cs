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

    // ---------- Mur d'écrans ----------

    Playlist Pl(string name) { var p = new Playlist { ClientId = client.Id, AreaId = hall.Id, Name = name }; db.Playlists.Add(p); return p; }

    [Fact]
    public void Assembling_screens_creates_a_synchronized_wall_zone_in_selection_order()
    {
        var pl = Pl("Campagne");
        var s = new[] { S("H1"), S("H2"), S("H3"), S("H4") };
        s[2].Layout = "50-50"; s[2].ZonePlaylistIds.Add(Guid.NewGuid());   // un écran découpé repasse en écran plein
        var order = new[] { s[1].Id, s[0].Id, s[3].Id, s[2].Id };
        var (wall, err) = AreaOps.CreateWall(As(), hall.Id, order, 2, 2, pl.Id, "");
        Assert.Null(err);
        Assert.True(wall!.IsWall);
        Assert.True(wall.Sync);
        Assert.Equal("Mur 2 × 2", wall.Name);
        Assert.Equal((2, 2), (wall.WallCols, wall.WallRows));
        Assert.Equal(pl.Id, wall.PlaylistId);
        Assert.Equal(new int?[] { 1, 0, 3, 2 }, s.Select(x => x.WallPos));
        Assert.All(s, x => { Assert.Equal(wall.Id, x.ZoneId); Assert.Equal(pl.Id, x.PlaylistId); Assert.Equal(ScreenLayouts.Full, x.Layout); Assert.Empty(x.ZonePlaylistIds); });

        var place = Helpers.Wall(db.Areas, s[3])!;   // 3e place : 1re colonne, 2e ligne
        Assert.Equal((2, 2, 0, 1), (place.Cols, place.Rows, place.Col, place.Row));
        Assert.NotNull(Helpers.SyncEpoch(db.Areas, s[0]));
    }

    [Fact]
    public void Screens_lose_their_widgets_when_they_join_a_wall()
    {
        var s = new[] { S("H1"), S("H2") };
        s[0].Widgets.Add(new ScreenWidget { AppId = "clock", X = 80, Y = 10 });
        var rev = s[0].WidgetsRevision;
        AreaOps.CreateWall(As(), hall.Id, s.Select(x => x.Id).ToList(), 2, 1, null, "");
        Assert.Empty(s[0].Widgets);
        Assert.True(s[0].WidgetsRevision > rev);   // modification à publier (le mur publie aussitôt)
    }

    [Fact]
    public void A_wall_needs_a_matching_grid_and_free_screens()
    {
        var s = new[] { S("H1"), S("H2"), S("H3") };
        var ids = s.Select(x => x.Id).ToList();
        Assert.NotNull(AreaOps.CreateWall(As(), hall.Id, ids, 2, 2, null, "").Error);           // 3 écrans, grille de 4
        Assert.NotNull(AreaOps.CreateWall(As(), hall.Id, ids.Take(1).ToList(), 1, 1, null, "").Error);   // un seul écran
        Assert.Null(AreaOps.CreateWall(As(), hall.Id, ids, 3, 1, null, "").Error);
        var other = S("H4");
        Assert.Contains("fait déjà partie", AreaOps.CreateWall(As(), hall.Id, new[] { ids[0], other.Id }, 2, 1, null, "").Error);
        Assert.NotNull(AreaOps.SetZoneSync(As(), hall.Id, s[0].ZoneId!.Value, false));   // la synchro d'un mur ne se coupe pas
    }

    [Fact]
    public void Two_walls_get_distinct_names()
    {
        var a = new[] { S("A1"), S("A2") }.Select(x => x.Id).ToList();
        var b = new[] { S("B1"), S("B2") }.Select(x => x.Id).ToList();
        Assert.Equal("Mur 2 × 1", AreaOps.CreateWall(As(), hall.Id, a, 2, 1, null, "").Zone!.Name);
        Assert.Equal("Mur 2 × 1 (2)", AreaOps.CreateWall(As(), hall.Id, b, 2, 1, null, "").Zone!.Name);
    }

    [Fact]
    public void A_wall_can_be_rearranged_and_its_screens_reload()
    {
        var s = new[] { S("H1"), S("H2"), S("H3"), S("H4") };
        var wall = AreaOps.CreateWall(As(), hall.Id, s.Select(x => x.Id).ToList(), 2, 2, null, "").Zone!;
        var before = Helpers.SyncStamp(db.Areas, s[0]);
        Assert.Null(AreaOps.ArrangeWall(As(), hall.Id, wall.Id, new[] { s[3].Id, s[2].Id, s[1].Id, s[0].Id }, 4, 1));
        Assert.Equal((4, 1), (wall.WallCols, wall.WallRows));
        Assert.Equal("Mur 4 × 1", wall.Name);   // nom proposé : suit la grille
        Assert.Equal(3, s[0].WallPos);
        Assert.NotEqual(before, Helpers.SyncStamp(db.Areas, s[0]));
        Assert.NotNull(AreaOps.ArrangeWall(As(), hall.Id, wall.Id, new[] { s[0].Id, s[1].Id }, 2, 1));   // tous les écrans gardent une place
    }

    [Fact]
    public void The_wall_playlist_goes_to_every_screen_of_the_wall()
    {
        var s = new[] { S("H1"), S("H2") };
        var wall = AreaOps.CreateWall(As(), hall.Id, s.Select(x => x.Id).ToList(), 2, 1, null, "").Zone!;
        var pl = Pl("Rentrée");
        Assert.Null(AreaOps.SetWallPlaylist(As(), hall.Id, wall.Id, pl.Id));
        Assert.All(s, x => Assert.Equal(pl.Id, x.PlaylistId));
        Assert.NotNull(AreaOps.SetWallPlaylist(As(), hall.Id, wall.Id, Guid.NewGuid()));   // liste inconnue
    }

    [Fact]
    public void Dissolving_a_wall_sends_its_screens_back_to_the_default_zone_with_the_wall_playlist()
    {
        var pl = Pl("Campagne");
        var s = new[] { S("H1"), S("H2") };
        var wall = AreaOps.CreateWall(As(), hall.Id, s.Select(x => x.Id).ToList(), 2, 1, pl.Id, "").Zone!;
        Assert.Null(AreaOps.DissolveWall(As(), hall.Id, wall.Id));
        Assert.DoesNotContain(wall, hall.Zones);
        var def = hall.Zones.Single(z => z.IsDefault);
        Assert.All(s, x => { Assert.Equal(def.Id, x.ZoneId); Assert.Null(x.WallPos); Assert.Equal(pl.Id, x.PlaylistId); });
        Assert.Null(Helpers.Wall(db.Areas, s[0]));
    }

    [Fact]
    public void A_screen_of_a_wall_does_not_leave_for_another_area()
    {
        var other = new Area { ClientId = client.Id, Name = "Annexe" };
        db.Areas.Add(other);
        var s = new[] { S("H1"), S("H2") };
        AreaOps.CreateWall(As(), hall.Id, s.Select(x => x.Id).ToList(), 2, 1, null, "");
        Assert.Contains("dissolvez", AreaOps.MoveScreen(As(), s[0].Id, other.Id));
        Assert.Equal(hall.Id, s[0].AreaId);
    }

    [Fact]
    public void Wall_shapes_and_labels_are_readable()
    {
        Assert.Equal(new[] { (2, 2), (4, 1), (1, 4) }, Helpers.WallShapes(4));
        Assert.Equal((3, 2), Helpers.WallShapes(6)[0]);
        Assert.Equal((3, 1), Helpers.WallShapes(3)[0]);
        Assert.Equal("1 · haut gauche", Helpers.WallLabel(0, 2, 2));
        Assert.Equal("4 · bas droite", Helpers.WallLabel(3, 2, 2));
        Assert.Equal("2 · centre", Helpers.WallLabel(1, 3, 1));
        Assert.Equal("2 · bas", Helpers.WallLabel(1, 1, 2));
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
        items.Add(new() { Id = Guid.NewGuid(), Type = "app", AppId = "slideshow-images", DurationMode = "content", DurationSec = 1800,
            Urls = new() { "/media/a.png", "/media/b.png", "/media/c.png" }, Settings = { ["interval"] = "5" } });
        var fixedItems = Helpers.FixedForSync(items, new[] { video, unknown });
        Assert.Equal(new[] { 222, 60, 8, 15 }, fixedItems.Select(i => i.DurationSec));   // diaporama : 3 images × 5 s
        Assert.All(fixedItems, i => Assert.Equal("fixed", i.DurationMode));
        Assert.Equal("content", items[0].DurationMode);   // l'instantané publié n'est pas modifié
        Assert.Equal(1800, items[0].DurationSec);
    }
}
