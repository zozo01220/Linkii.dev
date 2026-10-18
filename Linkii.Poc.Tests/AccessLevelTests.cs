using System.Security.Claims;
using Linkii.Poc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace Linkii.Poc.Tests;

/// <summary>Niveaux de droits : pages, apps, périmètre aires › zones › écrans, enregistrement, suppression, migration des anciens rôles par aire.</summary>
public class AccessLevelTests
{
    // Horizon SA : Siège (zones Défaut, Accueil, Mur) et Magasin (zones Défaut, Vitrines, Caisses).
    readonly Db db = new();
    readonly Tenant client = new() { Name = "Horizon SA" };
    readonly Area siege, magasin;
    readonly Zone accueil, mur, vitrines, caisses;
    readonly Screen hall, salle, murA, murB, vitrineG, vitrineD, caisse1, caisse2;
    readonly User admin, alice;
    readonly AppCatalog catalog = new(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Linkii.Poc", "Apps"), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    public AccessLevelTests()
    {
        db.Clients.Add(client);
        siege = new Area { ClientId = client.Id, Name = "Siège", CreatedUtc = DateTime.UtcNow.AddDays(-2) };
        magasin = new Area { ClientId = client.Id, Name = "Magasin", CreatedUtc = DateTime.UtcNow.AddDays(-1) };
        foreach (var a in new[] { siege, magasin }) { AreaOps.EnsureDefaultZone(a); db.Areas.Add(a); }
        accueil = new Zone { Name = "Accueil" }; mur = new Zone { Name = "Mur", Kind = Zone.Wall, WallCols = 2, WallRows = 1 };
        siege.Zones.AddRange([accueil, mur]);
        vitrines = new Zone { Name = "Vitrines" }; caisses = new Zone { Name = "Caisses" };
        magasin.Zones.AddRange([vitrines, caisses]);
        Screen S(string name, Area a, Zone z) { var s = new Screen { ClientId = client.Id, AreaId = a.Id, ZoneId = z.Id, Name = name, Token = Guid.NewGuid().ToString("N") }; db.Screens.Add(s); return s; }
        hall = S("Hall", siege, accueil); salle = S("Salle", siege, accueil);
        murA = S("Mur A", siege, mur); murB = S("Mur B", siege, mur);
        vitrineG = S("Vitrine gauche", magasin, vitrines); vitrineD = S("Vitrine droite", magasin, vitrines);
        caisse1 = S("Caisse 1", magasin, caisses); caisse2 = S("Caisse 2", magasin, caisses);
        admin = new User { Email = "luc@horizon.ch", Role = Roles.ClientAdmin, ClientId = client.Id };
        alice = new User { Email = "alice@horizon.ch", Role = Roles.ClientMember, ClientId = client.Id };
        db.Users.AddRange([admin, alice]);
    }

    ClientDb As(User u) => new(db, client.Id, AreaAccess.For(db, client.Id, u.Id));

    AccessLevel Give(User u, Action<AccessLevel> set)
    {
        var l = new AccessLevel { Name = "Niveau " + u.Email, Pages = [LevelPages.Screens, LevelPages.Playlists] };
        set(l);
        client.Levels.Add(l);
        u.LevelId = l.Id;
        return l;
    }

    static string[] Names(IEnumerable<Screen> s) => s.Select(x => x.Name).OrderBy(x => x).ToArray();

    [Fact]
    public void A_whole_area_gives_its_zones_and_screens_including_future_ones()
    {
        Give(alice, l => l.AreaIds.Add(magasin.Id));
        Assert.Equal(["Magasin"], As(alice).Access.Areas.Select(a => a.Name));
        Assert.Equal(["Caisse 1", "Caisse 2", "Vitrine droite", "Vitrine gauche"], Names(As(alice).Screens));

        var arrivee = new Screen { ClientId = client.Id, AreaId = magasin.Id, ZoneId = caisses.Id, Name = "Caisse 3", Token = "t3" };
        db.Screens.Add(arrivee);   // créé après le niveau : inclus
        Assert.Contains(As(alice).Screens, s => s.Name == "Caisse 3");
    }

    [Fact]
    public void Zones_and_single_screens_filter_inside_an_area()
    {
        Give(alice, l => { l.ZoneIds.Add(vitrines.Id); l.ScreenIds.Add(caisse2.Id); });
        Assert.Equal(["Caisse 2", "Vitrine droite", "Vitrine gauche"], Names(As(alice).Screens));
        var access = As(alice).Access;
        Assert.Equal(["Magasin"], access.Areas.Select(a => a.Name));   // le Siège reste invisible
        Assert.True(access.SeesZone(vitrines.Id));
        Assert.True(access.SeesZone(caisses.Id));    // une zone dont un écran est coché
        Assert.False(access.SeesZone(accueil.Id));
    }

    [Fact]
    public void One_screen_of_a_wall_gives_the_whole_wall()
    {
        Give(alice, l => l.ScreenIds.Add(murA.Id));
        Assert.Equal(["Mur A", "Mur B"], Names(As(alice).Screens));
        Assert.DoesNotContain(As(alice).Screens, s => s.Name == "Hall");
    }

    [Fact]
    public void All_areas_includes_an_area_created_later_and_an_unassigned_member_sees_nothing()
    {
        Give(alice, l => l.AllAreas = true);
        Assert.Equal(2, As(alice).Access.Areas.Count);
        db.Areas.Add(new Area { ClientId = client.Id, Name = "Atelier", CreatedUtc = DateTime.UtcNow });
        Assert.Equal(3, As(alice).Access.Areas.Count);

        alice.LevelId = null;
        Assert.Empty(As(alice).Access.Areas);
        Assert.Empty(As(alice).Screens);
    }

    [Fact]
    public void An_org_admin_sees_everything_without_filter()
    {
        var d = As(admin);
        Assert.True(d.Access.IsOrgAdmin);
        Assert.True(d.Access.CanSee(LevelPages.Planning, catalog));
        Assert.True(d.Access.CanSee(LevelPages.Integrations, catalog));
        Assert.True(d.Access.ManagesCurrent);
        Assert.False(d.Access.ReadOnly);
    }

    [Fact]
    public void Pages_follow_the_level_and_integrations_appear_with_an_allowed_app()
    {
        var l = Give(alice, l => { l.AllAreas = true; l.Pages = [LevelPages.Screens]; });
        var a = As(alice).Access;
        Assert.True(a.CanSee(LevelPages.Screens, catalog));
        Assert.False(a.CanSee(LevelPages.Planning, catalog));
        Assert.False(a.CanSee(LevelPages.Integrations, catalog));   // aucune app cochée
        Assert.False(a.CanSee(LevelPages.Widgets, catalog));

        var app = catalog.All.First(x => !x.ScreenWidget);
        var widget = catalog.All.First(x => x.ScreenWidget);
        l.Apps.Add(app.Id);
        a = As(alice).Access;
        Assert.True(a.CanSee(LevelPages.Integrations, catalog));
        Assert.True(a.CanUse(app));
        Assert.False(a.CanUse(catalog.All.First(x => !x.ScreenWidget && x.Id != app.Id)));
        Assert.False(a.CanSee(LevelPages.Widgets, catalog));

        l.AllWidgets = true;
        Assert.True(As(alice).Access.CanSee(LevelPages.Widgets, catalog));
        Assert.True(As(alice).Access.CanUse(widget));
    }

    [Fact]
    public void Read_only_is_reported_by_the_access()
    {
        Give(alice, l => { l.AllAreas = true; l.ReadOnly = true; });
        Assert.True(As(alice).Access.ReadOnly);
    }

    [Fact]
    public void Only_an_org_admin_saves_a_level_and_it_needs_a_name()
    {
        var l = new AccessLevel { Name = "Éditeur", Pages = [LevelPages.Screens, "admin"], AreaIds = { magasin.Id } };
        Assert.NotNull(AccessLevels.Save(As(alice), l));   // un membre ne gère pas les niveaux
        Assert.Null(AccessLevels.Save(As(admin), l));
        Assert.Equal([LevelPages.Screens], client.Levels.Single().Pages);   // une page inconnue est écartée

        Assert.NotNull(AccessLevels.Save(As(admin), new AccessLevel { Name = "  ", AllAreas = true }));
        Assert.NotNull(AccessLevels.Save(As(admin), new AccessLevel { Name = "éditeur", AllAreas = true }));   // nom déjà pris
        Assert.Null(AccessLevels.Save(As(admin), new AccessLevel { Name = "Sans rien" }));                     // un niveau n'a plus de périmètre : il ne dit que ce qu'on peut faire

        l.Name = "Éditeur Magasin";
        Assert.Null(AccessLevels.Save(As(admin), l));   // même Id : remplacé, pas dupliqué
        Assert.Equal(2, client.Levels.Count);           // « Éditeur Magasin » et « Sans rien »


    }

    [Fact]
    public void A_level_in_use_cannot_be_deleted()
    {
        var l = Give(alice, l => l.AllAreas = true);
        var err = AccessLevels.Delete(As(admin), l.Id);
        Assert.Contains("alice@horizon.ch", err);
        Assert.Single(client.Levels);

        alice.LevelId = null;
        Assert.Null(AccessLevels.Delete(As(admin), l.Id));
        Assert.Empty(client.Levels);
    }

    [Fact]
    public void Deleting_an_area_or_a_zone_removes_it_from_the_scopes()
    {
        var l = Give(alice, l => { l.AreaIds.Add(siege.Id); l.ZoneIds.Add(vitrines.Id); });
        AccessLevels.Forget(As(admin), [], [vitrines.Id]);
        Assert.Empty(l.ZoneIds);
        AccessLevels.Forget(As(admin), [siege.Id], []);
        Assert.Empty(l.AreaIds);
    }

    [Fact]
    public void Walls_and_zones_are_managed_by_org_admins_only()
    {
        Give(alice, l => l.AllAreas = true);
        Assert.NotNull(AreaOps.AddZone(As(alice), siege.Id, "Réserve").Error);
        Assert.Null(AreaOps.AddZone(As(admin), siege.Id, "Réserve").Error);
    }

    [Fact]
    public void Planning_is_limited_to_the_scope()
    {
        Give(alice, l => { l.Pages = [LevelPages.Planning]; l.ZoneIds.Add(vitrines.Id); });
        var slot = new Slot { Id = Guid.NewGuid(), PlaylistId = Guid.NewGuid(), Days = 0b1111111, From = 8 * 60, To = 18 * 60 };
        Assert.NotNull(PlanOps.Save(As(alice), siege.Id, accueil.Id, false, slot));   // aire hors périmètre
        Assert.NotNull(PlanOps.Save(As(alice), magasin.Id, caisses.Id, false, slot)); // zone hors périmètre
    }

    [Fact]
    public void Old_area_roles_become_levels_and_every_organisation_gets_the_default_level()
    {
        var bob = new User { Email = "bob@horizon.ch", Role = Roles.ClientMember, ClientId = client.Id, AreaRoles = { new AreaRole { AreaId = magasin.Id, Role = "admin" } } };
        var cleo = new User { Email = "cleo@horizon.ch", Role = Roles.ClientMember, ClientId = client.Id, AreaRoles = { new AreaRole { AreaId = magasin.Id, Role = "user" } } };
        var dan = new User { Email = "dan@horizon.ch", Role = Roles.ClientMember, ClientId = client.Id, AreaRoles = { new AreaRole { AreaId = siege.Id, Role = "user" }, new AreaRole { AreaId = magasin.Id, Role = "user" } } };
        db.Users.AddRange([bob, cleo, dan]);

        AccessLevels.Migrate(db);

        Assert.Contains(client.Levels, l => l.Name == AccessLevels.DefaultName && l.AllAreas && !l.Pages.Contains(LevelPages.Planning));
        Assert.Equal(bob.LevelId, cleo.LevelId);   // même jeu d'aires : même niveau
        var magasinLevel = client.Levels.Single(l => l.Id == bob.LevelId);
        Assert.Equal([magasin.Id], magasinLevel.AreaIds);
        Assert.Contains(LevelPages.Planning, magasinLevel.Pages);   // les anciens membres gardent ce qu'ils avaient
        var all = client.Levels.Single(l => l.Id == dan.LevelId);
        Assert.True(all.AllAreas);                                  // toutes les aires de l'organisation
        Assert.Empty(bob.AreaRoles);

        AccessLevels.Migrate(db);                                   // sans effet la seconde fois
        Assert.Equal(3, client.Levels.Count);
    }

    [Fact]
    public void The_default_level_has_no_planning_and_no_settings()
    {
        var (created, err) = Tenancy.CreateClient(db, new Reseller { Name = "Linkii" }, "Nouvelle SA", "Zoé", "zoe@nouvelle.ch", "motdepasse1");
        Assert.Null(err);
        var level = Assert.Single(created!.Levels);
        Assert.Equal([LevelPages.Screens, LevelPages.Playlists, LevelPages.Media], level.Pages);
        Assert.True(level.AllAreas && level.AllIntegrations && level.AllWidgets);
        Assert.False(level.ReadOnly);
    }

    [Fact]
    public void Manage_zones_right_lets_a_member_manage_zones_in_reached_areas_only()
    {
        Give(alice, l => { l.AllAreas = false; l.AreaIds.Add(magasin.Id); l.ManageZones = true; });
        var access = As(alice).Access;

        Assert.True(access.CanManageZones);
        Assert.True(access.CanSee(LevelPages.Zones, catalog));
        Assert.True(access.CanManage(magasin.Id));
        Assert.False(access.CanManage(siege.Id));   // aire non accessible

        var (zone, err) = AreaOps.AddZone(As(alice), magasin.Id, "Réserve");
        Assert.Null(err);
        Assert.NotNull(zone);
        Assert.Equal("Cette zone existe déjà.", AreaOps.AddZone(As(alice), magasin.Id, "réserve").Error);
        Assert.NotNull(AreaOps.AddZone(As(alice), siege.Id, "Bureau").Error);
    }

    [Fact]
    public void Without_the_right_or_read_only_a_member_cannot_manage_zones()
    {
        Give(alice, l => { l.AllAreas = true; l.ManageZones = false; });
        Assert.False(As(alice).Access.CanManageZones);
        Assert.False(As(alice).Access.CanSee(LevelPages.Zones, catalog));
        Assert.NotNull(AreaOps.AddZone(As(alice), magasin.Id, "Réserve").Error);

        client.Levels.Clear();
        Give(alice, l => { l.AllAreas = true; l.ManageZones = true; l.ReadOnly = true; });
        Assert.False(As(alice).Access.CanManageZones);
        Assert.NotNull(AreaOps.AddZone(As(alice), magasin.Id, "Réserve").Error);
    }

    [Fact]
    public void Administrators_manage_zones_everywhere()
    {
        Assert.True(As(admin).Access.CanManageZones);
        Assert.True(As(admin).Access.CanSee(LevelPages.Zones, catalog));
        Assert.Null(AreaOps.AddZone(As(admin), siege.Id, "Bureau").Error);
    }
}

/// <summary>Le niveau en lecture seule refuse tout enregistrement faisant passer par le magasin du client.</summary>
[Collection("platform")]
public class ReadOnlyLevelTests(Platform p)
{
    [Fact]
    public void A_read_only_member_reads_but_every_write_is_refused()
    {
        var store = p.Services.GetRequiredService<JsonStore>();
        var user = store.Write(db =>
        {
            var level = new AccessLevel { Name = "Lecture " + Guid.NewGuid().ToString("N")[..6], Pages = [LevelPages.Screens], AllAreas = true, ReadOnly = true };
            db.Clients.First(c => c.Id == p.A.Id).Levels.Add(level);
            var u = new User { Email = Guid.NewGuid().ToString("N")[..8] + "@lecture.ch", Role = Roles.ClientMember, ClientId = p.A.Id, LevelId = level.Id };
            db.Users.Add(u);
            return u;
        });
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(Claims.Client, p.A.Id.ToString()), new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Role, Roles.ClientMember)
        }, "test"));
        var tenantStore = new TenantStore(store, new TenantContext(new FixedAuth(principal), store));

        Assert.True(tenantStore.Read(d => d.Access.ReadOnly));
        Assert.Throws<ReadOnlyLevelException>(() => tenantStore.Write(d => d.Tenant.Name = d.Tenant.Name));
        Assert.Throws<ReadOnlyLevelException>(() => tenantStore.Write<int>(d => 1));
    }
}
