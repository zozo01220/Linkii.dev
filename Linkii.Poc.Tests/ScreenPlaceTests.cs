using System.Security.Claims;
using Linkii.Poc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Page Écrans, vue « Par aire » : déplacer un écran d'une zone ou d'une aire à l'autre (glisser-déposer), et l'annuler.</summary>
[Collection("platform")]
public class ScreenPlaceTests
{
    readonly JsonStore store;
    readonly Tenant client;
    readonly Area siege, magasin;
    readonly Zone vitrine, mur;
    readonly User camille, marc, lea;   // administratrice ; membre qui gère les zones ; membre qui peut déplacer les écrans
    readonly Screen borne;
    readonly Playlist hall;

    public ScreenPlaceTests(Platform p)
    {
        store = p.Services.GetRequiredService<JsonStore>();
        (client, siege, magasin, vitrine, mur, camille, marc, lea, borne, hall) = store.Write(db =>
        {
            var c = new Tenant { ResellerId = p.R1.Id, Name = "Déplacement SA" };
            var a1 = new Area { ClientId = c.Id, Name = "Siège", CreatedUtc = DateTime.UtcNow.AddDays(-2) };
            var a2 = new Area { ClientId = c.Id, Name = "Magasin", CreatedUtc = DateTime.UtcNow.AddDays(-1) };
            AreaOps.EnsureDefaultZone(a1); AreaOps.EnsureDefaultZone(a2);
            var v = new Zone { Name = "Vitrine" }; a2.Zones.Add(v);
            var w = new Zone { Name = "Mur du hall", Kind = Zone.Wall }; a2.Zones.Add(w);
            db.Clients.Add(c); db.Areas.AddRange([a1, a2]);
            var level = new AccessLevel { Name = "Gestion", Pages = [LevelPages.Screens], AllAreas = true, ManageZones = true };
            var mover = new AccessLevel { Name = "Déplacement", Pages = [LevelPages.Screens], AllAreas = true, MoveScreens = true };
            c.Levels.AddRange([level, mover]);
            var admin = new User { Email = "camille@deplace.ch", Role = Roles.ClientAdmin, ClientId = c.Id };
            var member = new User { Email = "marc@deplace.ch", Role = Roles.ClientMember, ClientId = c.Id, LevelId = level.Id };
            var mover1 = new User { Email = "lea@deplace.ch", Role = Roles.ClientMember, ClientId = c.Id, LevelId = mover.Id };
            db.Users.AddRange([admin, member, mover1]);
            var pl = new Playlist { ClientId = c.Id, AreaId = a1.Id, Name = "Hall" };
            db.Playlists.Add(pl);
            var sc = new Screen { ClientId = c.Id, AreaId = a1.Id, ZoneId = a1.Zones[0].Id, Name = "Borne", Token = "tk-" + Guid.NewGuid().ToString("N")[..6], PlaylistId = pl.Id, OwnPlanning = true };
            sc.Slots.Add(new Slot { PlaylistId = pl.Id });
            db.Screens.Add(sc);
            return (c, a1, a2, v, w, admin, member, mover1, sc, pl);
        });
    }

    ClientDb As(Db db, User u, Area area)
    {
        db.Users.First(x => x.Id == u.Id).LastAreas[client.Id] = area.Id;
        return new ClientDb(db, client.Id, AreaAccess.For(db, client.Id, u.Id));
    }


    [Fact]
    public void An_administrator_moves_a_screen_to_another_area_and_zone_planning_is_removed_and_undo_restores_everything()
    {
        var before = store.Read(db => AreaOps.PlaceOf(db.Screens.First(s => s.Id == borne.Id)));
        Assert.Null(store.Write(db => AreaOps.PlaceScreen(As(db, camille, siege), borne.Id, magasin.Id, vitrine.Id)));

        store.Read(db =>
        {
            var s = db.Screens.First(x => x.Id == borne.Id);
            Assert.Equal(magasin.Id, s.AreaId);
            Assert.Equal(vitrine.Id, s.ZoneId);
            Assert.Empty(s.Slots);               // planning propre retiré
            Assert.Null(s.PlaylistId);           // liste de l'ancienne aire plus choisie
            Assert.False(s.OwnPlanning);
            return true;
        });

        store.Write(db => AreaOps.Restore(As(db, camille, siege), before));
        store.Read(db =>
        {
            var s = db.Screens.First(x => x.Id == borne.Id);
            Assert.Equal(siege.Id, s.AreaId);
            Assert.Equal(hall.Id, s.PlaylistId);
            Assert.Single(s.Slots);
            Assert.True(s.OwnPlanning);
            return true;
        });
    }

    [Fact]
    public void A_member_changes_zone_but_cannot_move_a_screen_to_another_area()
    {
        var err = store.Write(db => AreaOps.PlaceScreen(As(db, marc, siege), borne.Id, magasin.Id, vitrine.Id));
        Assert.NotNull(err);   // seuls les administrateurs déplacent entre aires
        Assert.Equal(siege.Id, store.Read(db => db.Screens.First(s => s.Id == borne.Id).AreaId));

        var zone = store.Write(db => AreaOps.AddZone(As(db, camille, siege), siege.Id, "Accueil").Zone!.Id);
        Assert.Null(store.Write(db => AreaOps.PlaceScreen(As(db, marc, siege), borne.Id, siege.Id, zone)));   // changement de zone : permis
        Assert.Single(store.Read(db => db.Screens.First(s => s.Id == borne.Id).Slots));                       // planning conservé
    }

    [Fact]
    public void The_move_right_lets_a_member_move_screens_between_zones_and_areas_without_being_an_administrator()
    {
        Assert.Null(store.Write(db => AreaOps.PlaceScreen(As(db, lea, siege), borne.Id, magasin.Id, vitrine.Id)));
        Assert.Equal(magasin.Id, store.Read(db => db.Screens.First(s => s.Id == borne.Id).AreaId));
        Assert.True(store.Read(db => As(db, lea, siege).Access.CanMoveScreens));
        Assert.False(store.Read(db => As(db, marc, siege).Access.CanMoveScreens));   // la gestion des zones seule ne le donne pas
    }

    [Fact]
    public void A_wall_zone_is_not_a_drop_target()
    {
        var err = store.Write(db => AreaOps.PlaceScreen(As(db, camille, siege), borne.Id, magasin.Id, mur.Id));
        Assert.NotNull(err);
    }
}
