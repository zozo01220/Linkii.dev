using Linkii.Poc;
using Xunit;
namespace Linkii.Poc.Tests;

/// <summary>Page Organisation : plusieurs accès par membre (rôle + périmètre), rôles de départ, accès donnés depuis l'arbre, migration « tous administrateurs ».</summary>
public class OrganisationAccessTests
{
    // Horizon SA : Siège (zones Défaut, Accueil) et Magasin (zones Défaut, Vitrines, Caisses).
    readonly Db db = new();
    readonly Tenant client = new() { Name = "Horizon SA" };
    readonly Area siege, magasin;
    readonly Zone accueil, vitrines, caisses;
    readonly Screen hall, vitrine, caisse;
    readonly User admin, alice;
    readonly AccessLevel editor, reader;

    public OrganisationAccessTests()
    {
        db.Clients.Add(client);
        AccessLevels.EnsureRoles(client);
        editor = client.Levels.Single(l => l.Kind == LevelKinds.Editor);
        reader = client.Levels.Single(l => l.Kind == LevelKinds.Reader);
        siege = new Area { ClientId = client.Id, Name = "Siège", CreatedUtc = DateTime.UtcNow.AddDays(-2) };
        magasin = new Area { ClientId = client.Id, Name = "Magasin", CreatedUtc = DateTime.UtcNow.AddDays(-1) };
        foreach (var a in new[] { siege, magasin }) { AreaOps.EnsureDefaultZone(a); db.Areas.Add(a); }
        accueil = new Zone { Name = "Accueil" }; siege.Zones.Add(accueil);
        vitrines = new Zone { Name = "Vitrines" }; caisses = new Zone { Name = "Caisses" }; magasin.Zones.AddRange([vitrines, caisses]);
        Screen S(string name, Area a, Zone z) { var s = new Screen { ClientId = client.Id, AreaId = a.Id, ZoneId = z.Id, Name = name, Token = Guid.NewGuid().ToString("N") }; db.Screens.Add(s); return s; }
        hall = S("Hall", siege, accueil); vitrine = S("Vitrine", magasin, vitrines); caisse = S("Caisse", magasin, caisses);
        admin = new User { Email = "luc@horizon.ch", Role = Roles.ClientAdmin, ClientId = client.Id };
        alice = new User { Email = "alice@horizon.ch", Role = Roles.ClientMember, ClientId = client.Id };
        db.Users.AddRange([admin, alice]);
    }

    ClientDb As(User u) => new(db, client.Id, AreaAccess.For(db, client.Id, u.Id));
    ClientDb AsAdmin() => As(admin);
    void Open(User u, Area a) => u.LastAreas[client.Id] = a.Id;
    static MemberAccess Acc(AccessLevel l, Action<Perimeter> set) { var p = new Perimeter(); set(p); return new MemberAccess { LevelId = l.Id, Perimeter = p }; }

    [Fact]
    public void Each_access_brings_its_own_role_to_the_area_it_covers()
    {
        alice.SetAccesses([Acc(editor, p => p.AreaIds.Add(siege.Id)), Acc(reader, p => p.AreaIds.Add(magasin.Id))]);

        Open(alice, siege);
        var atSiege = As(alice).Access;
        Assert.Equal(["Siège", "Magasin"], atSiege.Areas.Select(a => a.Name));   // les deux aires sont visibles
        Assert.True(atSiege.CanWrite(LevelPages.Screens));

        Open(alice, magasin);
        var atMagasin = As(alice).Access;
        Assert.Equal(DomainAccess.Read, atMagasin.AccessTo(LevelPages.Screens));
        Assert.False(atMagasin.CanWrite(LevelPages.Media));
    }

    [Fact]
    public void An_access_on_the_whole_area_covers_it_and_a_smaller_one_does_not_widen_it()
    {
        // Éditeur sur tout le Magasin, Lecteur sur sa zone Caisses : l'aire entière l'emporte (les droits se règlent par aire)
        alice.SetAccesses([Acc(editor, p => p.AreaIds.Add(magasin.Id)), Acc(reader, p => p.ZoneIds.Add(caisses.Id))]);
        Open(alice, magasin);
        Assert.True(As(alice).Access.CanWrite(LevelPages.Screens));

        // l'inverse : Lecteur sur le Magasin, Éditeur sur Caisses seulement → l'édition ne déborde pas sur le reste de l'aire
        alice.SetAccesses([Acc(reader, p => p.AreaIds.Add(magasin.Id)), Acc(editor, p => p.ZoneIds.Add(caisses.Id))]);
        Assert.False(As(alice).Access.CanWrite(LevelPages.Screens));
    }

    [Fact]
    public void Several_zone_accesses_in_one_area_apply_the_most_restricted_role()
    {
        alice.SetAccesses([Acc(editor, p => p.ZoneIds.Add(vitrines.Id)), Acc(reader, p => p.ZoneIds.Add(caisses.Id))]);
        Open(alice, magasin);
        var a = As(alice).Access;
        Assert.False(a.CanWrite(LevelPages.Screens));
        Assert.Equal(["Caisse", "Vitrine"], As(alice).Screens.Select(s => s.Name).Order());   // la vue est l'union des périmètres
    }

    [Fact]
    public void A_member_without_the_new_list_keeps_their_old_level_and_perimeter()
    {
        alice.LevelId = editor.Id;
        alice.Perimeter = new Perimeter { AreaIds = [siege.Id] };
        Assert.Equal(["Siège"], As(alice).Access.Areas.Select(a => a.Name));
        Assert.True(As(alice).Access.CanWrite(LevelPages.Screens));
    }

    [Fact]
    public void Granting_from_the_tree_adds_or_widens_an_access_and_revoking_removes_it()
    {
        Assert.Null(AccessLevels.GrantAccess(AsAdmin(), alice.Id, editor.Id, magasin.Id, null));
        Assert.Null(AccessLevels.GrantAccess(AsAdmin(), alice.Id, editor.Id, siege.Id, null));   // même rôle : un seul accès, élargi
        Assert.Single(alice.Accesses);
        Assert.Equal([magasin.Id, siege.Id], alice.Accesses[0].Perimeter.AreaIds);

        Assert.Null(AccessLevels.GrantAccess(AsAdmin(), alice.Id, reader.Id, null, accueil.Id));   // autre rôle : un deuxième accès
        Assert.Equal(2, alice.Accesses.Count);
        Assert.Equal(editor.Id, alice.LevelId);   // le premier est recopié pour les écrans qui lisent encore l'ancien couple

        Assert.Null(AccessLevels.RevokeAccess(AsAdmin(), alice.Id, reader.Id, null, accueil.Id));
        Assert.Single(alice.Accesses);   // un accès sans périmètre disparaît

        Assert.Null(AccessLevels.RevokeAccess(AsAdmin(), alice.Id, editor.Id, magasin.Id, null));
        Assert.Null(AccessLevels.RevokeAccess(AsAdmin(), alice.Id, editor.Id, siege.Id, null));
        Assert.Empty(alice.Accesses);
        Assert.Null(alice.LevelId);
    }

    [Fact]
    public void Granting_a_whole_area_replaces_its_zone_accesses_of_the_same_role()
    {
        Assert.Null(AccessLevels.GrantAccess(AsAdmin(), alice.Id, editor.Id, null, vitrines.Id));
        Assert.Null(AccessLevels.GrantAccess(AsAdmin(), alice.Id, editor.Id, magasin.Id, null));
        Assert.Empty(alice.Accesses[0].Perimeter.ZoneIds);
        Assert.Equal([magasin.Id], alice.Accesses[0].Perimeter.AreaIds);
    }

    [Fact]
    public void Only_organisation_admins_grant_and_only_members_receive()
    {
        alice.SetAccesses([Acc(editor, p => p.AllAreas = true)]);
        Assert.NotNull(AccessLevels.GrantAccess(As(alice), alice.Id, reader.Id, magasin.Id, null));
        Assert.NotNull(AccessLevels.GrantAccess(AsAdmin(), admin.Id, reader.Id, magasin.Id, null));   // un administrateur a déjà tout
        Assert.NotNull(AccessLevels.GrantAccess(AsAdmin(), alice.Id, Guid.NewGuid(), magasin.Id, null));   // rôle inconnu
    }

    [Fact]
    public void Starting_roles_cannot_be_changed_or_deleted_but_custom_roles_can()
    {
        var copy = new AccessLevel { Id = editor.Id, Name = "Éditeur", Pages = [LevelPages.Screens], ScreensAccess = DomainAccess.Write };
        Assert.NotNull(AccessLevels.Save(AsAdmin(), copy));
        Assert.NotNull(AccessLevels.Delete(AsAdmin(), reader.Id));

        var custom = new AccessLevel { Name = "Affiches", ScreensAccess = DomainAccess.Read, PlaylistsAccess = DomainAccess.None, MediaAccess = DomainAccess.Write };
        Assert.Null(AccessLevels.Save(AsAdmin(), custom));
        Assert.Null(custom.Kind);
        Assert.Null(AccessLevels.Delete(AsAdmin(), custom.Id));
    }

    [Fact]
    public void A_level_in_use_by_any_access_cannot_be_deleted()
    {
        var custom = new AccessLevel { Name = "Affiches", MediaAccess = DomainAccess.Write };
        Assert.Null(AccessLevels.Save(AsAdmin(), custom));
        alice.SetAccesses([Acc(editor, p => p.AreaIds.Add(siege.Id)), Acc(custom, p => p.AreaIds.Add(magasin.Id))]);   // utilisé par le deuxième accès
        Assert.NotNull(AccessLevels.Delete(AsAdmin(), custom.Id));
    }

    [Fact]
    public void Deleting_an_area_removes_it_from_every_access()
    {
        alice.SetAccesses([Acc(editor, p => { p.AreaIds.Add(siege.Id); p.AreaIds.Add(magasin.Id); }), Acc(reader, p => p.ZoneIds.Add(vitrines.Id))]);
        AccessLevels.Forget(AsAdmin(), [magasin.Id], magasin.Zones.Select(z => z.Id));
        Assert.Equal([siege.Id], alice.Accesses[0].Perimeter.AreaIds);
        Assert.Empty(alice.Accesses[1].Perimeter.ZoneIds);
    }

    [Fact]
    public void The_old_default_level_becomes_the_editor_and_members_become_administrators_once()
    {
        var db2 = new Db();
        var c = new Tenant { Name = "Ancienne SA" };
        db2.Clients.Add(c);
        c.Levels.Add(AccessLevels.NewDefault());   // « Utilisateur » d'avant les rôles de départ
        var custom = new AccessLevel { Name = "Éditeur", Pages = [LevelPages.Screens] };   // un niveau sur mesure qui porte déjà le nom
        custom.Normalize();
        c.Levels.Add(custom);
        var oldDefault = c.Levels[0];
        var member = new User { Email = "m@ancienne.ch", Role = Roles.ClientMember, ClientId = c.Id, LevelId = oldDefault.Id, Perimeter = new Perimeter { AllAreas = true } };
        var boss = new User { Email = "b@ancienne.ch", Role = Roles.ClientAdmin, ClientId = c.Id };
        db2.Users.AddRange([member, boss]);

        AccessLevels.MigrateRolesV2(db2);

        Assert.Equal(LevelKinds.Editor, oldDefault.Kind);
        Assert.Equal("Éditeur", oldDefault.Name);
        Assert.Equal("Éditeur (sur mesure)", custom.Name);
        Assert.Contains(c.Levels, l => l.Kind == LevelKinds.Reader);
        Assert.Equal(Roles.ClientAdmin, member.Role);
        Assert.Null(member.LevelId);
        Assert.Null(member.Perimeter);
        Assert.Empty(member.Accesses);

        // une nouvelle personne invitée ensuite reste membre : la migration ne rejoue pas
        var later = new User { Email = "n@ancienne.ch", Role = Roles.ClientMember, ClientId = c.Id };
        db2.Users.Add(later);
        AccessLevels.MigrateRolesV2(db2);
        Assert.Equal(Roles.ClientMember, later.Role);
        Assert.Equal(2, c.Levels.Count(l => l.Kind != null));   // pas de doublon des rôles de départ
    }
}
