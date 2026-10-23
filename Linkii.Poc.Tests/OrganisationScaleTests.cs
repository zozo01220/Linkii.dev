using Linkii.Poc;
using Xunit;
namespace Linkii.Poc.Tests;

/// <summary>Page Organisation à grande échelle : filtres Lieux / Personnes, actions en lot, renvoi des invitations (maquette organisation-echelle.html).</summary>
public class OrganisationScaleTests
{
    // Siège (Défaut, Accueil), Magasin (Défaut, Vitrines, Caisses), Dépôt (Défaut). Luc admin ; Alice Éditeur Siège ; Bob Lecteur Magasin › Caisses ;
    // Cléo sans accès, invitée ; Dan Éditeur partout, désactivé.
    readonly Db db = new();
    readonly Tenant client = new() { Name = "Horizon SA" };
    readonly Area siege, magasin, depot;
    readonly Zone accueil, vitrines, caisses;
    readonly AccessLevel editor, reader;
    readonly User luc, alice, bob, cleo, dan;

    public OrganisationScaleTests()
    {
        db.Clients.Add(client);
        AccessLevels.EnsureRoles(client);
        editor = client.Levels.Single(l => l.Kind == LevelKinds.Editor);
        reader = client.Levels.Single(l => l.Kind == LevelKinds.Reader);
        Area A(string n, int days) { var a = new Area { ClientId = client.Id, Name = n, CreatedUtc = DateTime.UtcNow.AddDays(-days) }; AreaOps.EnsureDefaultZone(a); db.Areas.Add(a); return a; }
        siege = A("Siège", 3); magasin = A("Magasin", 2); depot = A("Dépôt", 1);
        accueil = new Zone { Name = "Accueil" }; siege.Zones.Add(accueil);
        vitrines = new Zone { Name = "Vitrines" }; caisses = new Zone { Name = "Caisses" }; magasin.Zones.AddRange([vitrines, caisses]);
        User U(string mail, string role) { var u = new User { Email = mail, Name = mail.Split('@')[0], Role = role, ClientId = client.Id, LastLoginUtc = DateTime.UtcNow }; db.Users.Add(u); return u; }
        luc = U("luc@h.ch", Roles.ClientAdmin);
        alice = U("alice@h.ch", Roles.ClientMember); alice.SetAccesses([new MemberAccess { LevelId = editor.Id, Perimeter = new Perimeter { AreaIds = [siege.Id] } }]);
        bob = U("bob@h.ch", Roles.ClientMember); bob.SetAccesses([new MemberAccess { LevelId = reader.Id, Perimeter = new Perimeter { ZoneIds = [caisses.Id] } }]);
        cleo = U("cleo@h.ch", Roles.ClientMember); cleo.LastLoginUtc = null; cleo.MustChangePassword = true;
        dan = U("dan@h.ch", Roles.ClientMember); dan.Disabled = true; dan.SetAccesses([new MemberAccess { LevelId = editor.Id, Perimeter = new Perimeter { AllAreas = true } }]);
    }

    ClientDb As(User u) => new(db, client.Id, AreaAccess.For(db, client.Id, u.Id));
    OrgView View() => new(db.Areas.OrderBy(a => a.CreatedUtc).ToList(), db.Screens, db.Users, client.Levels);

    [Fact]
    public void People_filters_by_text_role_area_status_and_missing_access()
    {
        var v = View();
        Assert.Equal(["alice"], v.FilterPeople(new OrgFilter { Q = "ALI" }).Select(u => u.Name));
        Assert.Equal(["alice", "dan"], v.FilterPeople(new OrgFilter { Roles = { "Éditeur" } }).Select(u => u.Name));
        Assert.Equal(["luc"], v.FilterPeople(new OrgFilter { Roles = { OrgView.AdminRole } }).Select(u => u.Name));
        // Aire Magasin : Bob (zone Caisses), Dan (toutes les aires) et l'administrateur ; pas Alice (Siège)
        Assert.Equal(["bob", "dan", "luc"], v.FilterPeople(new OrgFilter { Areas = { magasin.Id } }).Select(u => u.Name));
        Assert.Equal(["cleo"], v.FilterPeople(new OrgFilter { Status = "Invité" }).Select(u => u.Name));
        Assert.Equal(["dan"], v.FilterPeople(new OrgFilter { Status = "Désactivé" }).Select(u => u.Name));
        Assert.Equal(["cleo"], v.FilterPeople(new OrgFilter { Flag = true }).Select(u => u.Name));   // « sans accès » : un administrateur n'en a pas besoin
        Assert.Equal(["alice", "dan"], v.FilterPeople(new OrgFilter { Roles = { "Éditeur" }, Areas = { siege.Id } }).Select(u => u.Name));
    }

    [Fact]
    public void People_sort_by_name_role_and_status()
    {
        var v = View();
        Assert.Equal(["alice", "bob", "cleo", "dan", "luc"], v.FilterPeople(new OrgFilter()).Select(u => u.Name));
        Assert.Equal(["luc", "dan", "cleo", "bob", "alice"], v.FilterPeople(new OrgFilter(), "name", true).Select(u => u.Name));
        Assert.Equal("luc", v.FilterPeople(new OrgFilter(), "role").First().Name);   // Administrateur avant Éditeur
        Assert.Equal("cleo", v.FilterPeople(new OrgFilter(), "role").Last().Name);   // sans accès en dernier
    }

    [Fact]
    public void Places_filter_by_text_area_role_and_missing_member()
    {
        var v = View();
        Assert.Equal(["Magasin"], v.FilterAreas(new OrgFilter { Q = "vitr" }).Select(a => a.Name));   // le texte trouve la zone
        Assert.Equal(["Magasin", "Dépôt"], v.FilterAreas(new OrgFilter { Areas = { magasin.Id, depot.Id } }).Select(a => a.Name));
        Assert.Equal(["Siège", "Magasin", "Dépôt"], v.FilterAreas(new OrgFilter { Roles = { "Éditeur" } }).Select(a => a.Name));   // Dan : toutes les aires
        Assert.Equal(["Magasin"], v.FilterAreas(new OrgFilter { Roles = { "Lecteur" } }).Select(a => a.Name));
        Assert.Empty(v.FilterAreas(new OrgFilter { Flag = true }));   // Dan couvre tout : aucune aire sans membre
        dan.SetAccesses([]);
        Assert.Equal(["Dépôt"], View().FilterAreas(new OrgFilter { Flag = true }).Select(a => a.Name));
    }

    [Fact]
    public void Members_of_an_area_exclude_administrators_and_zone_counts_follow_perimeters()
    {
        var v = View();
        Assert.Equal(["bob", "dan"], v.MembersOf(magasin).Select(u => u.Name).Order());
        Assert.Equal(["bob", "dan"], v.MembersOf(magasin, caisses).Select(u => u.Name).Order());
        Assert.Equal(["dan"], v.MembersOf(magasin, vitrines).Select(u => u.Name));
        Assert.Equal(["luc"], v.Admins.Select(u => u.Name));
    }

    [Fact]
    public void The_default_view_is_places_for_few_areas_and_people_otherwise()
    {
        Assert.Equal("places", OrgView.DefaultView(1));
        Assert.Equal("places", OrgView.DefaultView(3));
        Assert.Equal("people", OrgView.DefaultView(4));
    }

    [Fact]
    public void Bulk_grant_gives_a_role_to_several_members_and_skips_covered_zones()
    {
        var places = new PlaceSel { Areas = { magasin.Id }, Zones = { accueil.Id } };
        Assert.Null(AccessLevels.GrantAccess(As(luc), [alice.Id, bob.Id, luc.Id], editor.Id, places));
        var a = alice.Accesses.Single(x => x.LevelId == editor.Id).Perimeter;   // Alice avait déjà Éditeur sur le Siège : l'accès s'élargit
        Assert.Equal([siege.Id, magasin.Id], a.AreaIds);
        Assert.Empty(a.ZoneIds);                                                 // la zone Accueil est dans le Siège, déjà couvert
        Assert.Equal(2, bob.Accesses.Count);                                     // Bob garde Lecteur et reçoit Éditeur
        Assert.Empty(luc.Accesses);                                              // un administrateur n'a rien à recevoir
        Assert.NotNull(AccessLevels.GrantAccess(As(luc), [alice.Id], editor.Id, new PlaceSel()));                // aucun lieu
        Assert.NotNull(AccessLevels.GrantAccess(As(luc), [luc.Id], editor.Id, new PlaceSel { All = true }));     // aucun membre
        Assert.NotNull(AccessLevels.GrantAccess(As(alice), [bob.Id], editor.Id, new PlaceSel { All = true }));   // pas administrateur
    }

    [Fact]
    public void Bulk_set_role_replaces_the_role_of_every_access_and_merges_them()
    {
        bob.SetAccesses([new MemberAccess { LevelId = reader.Id, Perimeter = new Perimeter { ZoneIds = [caisses.Id] } }, new MemberAccess { LevelId = editor.Id, Perimeter = new Perimeter { AreaIds = [depot.Id] } }]);
        var (n, err) = AccessLevels.SetRole(As(luc), [bob.Id, alice.Id, luc.Id, cleo.Id], reader.Id);
        Assert.Null(err);
        Assert.Equal(2, n);                                    // Bob et Alice ; l'administrateur et Cléo (sans accès) sont ignorés
        var merged = Assert.Single(bob.Accesses);
        Assert.Equal(reader.Id, merged.LevelId);
        Assert.Equal([caisses.Id], merged.Perimeter.ZoneIds);  // les lieux ne changent pas
        Assert.Equal([depot.Id], merged.Perimeter.AreaIds);
        Assert.Equal(reader.Id, Assert.Single(alice.Accesses).LevelId);
        Assert.NotNull(AccessLevels.SetRole(As(luc), [bob.Id], Guid.NewGuid()).Error);
    }

    [Fact]
    public void Bulk_revoke_removes_places_from_all_roles()
    {
        Assert.Null(AccessLevels.RevokeAccess(As(luc), [dan.Id], new PlaceSel { Areas = { magasin.Id } }));
        var p = dan.Accesses.Single().Perimeter;
        Assert.False(p.AllAreas);
        Assert.Equal([siege.Id, depot.Id], p.AreaIds);   // les autres aires restent

        Assert.Null(AccessLevels.RevokeAccess(As(luc), [alice.Id, bob.Id], new PlaceSel { Zones = { caisses.Id } }));
        Assert.Empty(bob.Accesses);                      // son seul accès était cette zone
        Assert.Single(alice.Accesses);                   // Alice n'avait rien dans cette zone

        Assert.Null(AccessLevels.RevokeAccess(As(luc), [alice.Id], new PlaceSel { Zones = { accueil.Id } }));   // une zone d'une aire entière : ses autres zones restent
        var ap = alice.Accesses.Single().Perimeter;
        Assert.Empty(ap.AreaIds);
        Assert.Equal(siege.Zones.Where(z => z.Id != accueil.Id).Select(z => z.Id), ap.ZoneIds);

        Assert.Null(AccessLevels.RevokeAccess(As(luc), [dan.Id], new PlaceSel { All = true }));
        Assert.Empty(dan.Accesses);
        Assert.NotNull(AccessLevels.RevokeAccess(As(luc), [dan.Id], new PlaceSel()));   // aucun lieu choisi
    }

    [Fact]
    public void Bulk_disable_spares_yourself_and_the_last_administrator()
    {
        var (n, err) = AccessLevels.SetDisabled(As(luc), [alice.Id, bob.Id, luc.Id], true);
        Assert.Null(err);
        Assert.Equal(2, n);   // pas soi-même
        Assert.True(alice.Disabled && bob.Disabled && !luc.Disabled);

        var boss2 = new User { Email = "boss2@h.ch", Name = "boss2", Role = Roles.ClientAdmin, ClientId = client.Id };
        db.Users.Add(boss2);
        var res = AccessLevels.SetDisabled(As(boss2), [luc.Id], true);   // boss2 reste actif
        Assert.Equal(1, res.Changed);
        Assert.Equal(1, AccessLevels.SetDisabled(As(boss2), [luc.Id], false).Changed);   // réactivation
        var both = AccessLevels.SetDisabled(As(luc), [luc.Id, boss2.Id], true);           // luc ne se désactive pas ; boss2 resterait seul à rester : désactiver boss2 laisserait luc
        Assert.Equal(1, both.Changed);
        Assert.NotNull(AccessLevels.SetDisabled(As(alice), [bob.Id], true).Error);        // pas administrateur
    }

    [Fact]
    public void Pending_invitations_are_the_invited_accounts_not_disabled()
    {
        Assert.Equal([cleo.Id], InviteOps.Pending(db.Users));
        cleo.Disabled = true;
        Assert.Empty(InviteOps.Pending(db.Users));
        cleo.Disabled = false; cleo.LastLoginUtc = DateTime.UtcNow;   // s'est connectée : plus en attente
        Assert.Empty(InviteOps.Pending(db.Users));
    }
}
