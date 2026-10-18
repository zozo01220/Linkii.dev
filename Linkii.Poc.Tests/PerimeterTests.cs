using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Niveau de droits (ce qu'on peut faire) séparé du périmètre du membre (ce qu'on voit).</summary>
public class PerimeterTests
{
    readonly Db db = new();
    readonly Tenant client = new() { Name = "Périmètres SA", FoldersOn = true };
    readonly Area siege, magasin;
    readonly Zone vitrine;
    readonly AccessLevel editeur;
    readonly User marie, jean, sophie;
    readonly Screen borne, caisse, hall;

    public PerimeterTests()
    {
        db.Clients.Add(client);
        siege = new Area { ClientId = client.Id, Name = "Siège", CreatedUtc = DateTime.UtcNow.AddDays(-2) };
        magasin = new Area { ClientId = client.Id, Name = "Magasin", CreatedUtc = DateTime.UtcNow.AddDays(-1) };
        AreaOps.EnsureDefaultZone(siege); AreaOps.EnsureDefaultZone(magasin);
        vitrine = new Zone { Name = "Vitrine" }; magasin.Zones.Add(vitrine);
        db.Areas.AddRange([siege, magasin]);

        editeur = new AccessLevel { Name = "Éditeur", Pages = [LevelPages.Screens, LevelPages.Playlists, LevelPages.Media], AllAreas = true };
        client.Levels.Add(editeur);
        marie = Member("marie", new Perimeter { AreaIds = [siege.Id] });                        // une aire entière
        jean = Member("jean", new Perimeter { ZoneIds = [vitrine.Id] });                        // une zone
        sophie = Member("sophie", null);                                                         // pas de périmètre propre : celui du niveau (toutes les aires)
        db.Users.AddRange([marie, jean, sophie]);

        borne = Screen("Borne", siege, siege.Zones[0].Id);
        caisse = Screen("Caisse", magasin, vitrine.Id);
        hall = Screen("Hall", magasin, magasin.Zones[0].Id);
    }

    User Member(string name, Perimeter? perimeter) =>
        new() { Email = name + "@perimetre.ch", Name = name, Role = Roles.ClientMember, ClientId = client.Id, LevelId = editeur.Id, Perimeter = perimeter };

    Screen Screen(string name, Area a, Guid zone)
    {
        var s = new Screen { ClientId = client.Id, AreaId = a.Id, ZoneId = zone, Name = name, Token = "t-" + name };
        db.Screens.Add(s);
        return s;
    }

    AreaAccess Access(User u) => AreaAccess.For(db, client.Id, u.Id);

    [Fact]
    public void One_level_serves_several_teams_each_with_its_own_perimeter()
    {
        var m = Access(marie); var j = Access(jean);
        Assert.Equal(["Siège"], m.Areas.Select(a => a.Name));
        Assert.True(m.Sees!(borne)); Assert.False(m.Sees!(caisse));
        Assert.Equal(["Magasin"], j.Areas.Select(a => a.Name));
        Assert.True(j.Sees!(caisse)); Assert.False(j.Sees!(hall)); Assert.False(j.Sees!(borne));   // seule la zone Vitrine
        Assert.Equal(editeur.Id, m.Level!.Id); Assert.Equal(editeur.Id, j.Level!.Id);                // même niveau
    }

    [Fact]
    public void Changing_the_level_does_not_change_what_a_member_sees()
    {
        var lecture = new AccessLevel { Name = "Lecture", Pages = [LevelPages.Screens], ReadOnly = true, AllAreas = false };
        client.Levels.Add(lecture);
        marie.LevelId = lecture.Id;
        var a = Access(marie);
        Assert.Equal(["Siège"], a.Areas.Select(x => x.Name));   // le périmètre de Marie suit la personne
        Assert.True(a.ReadOnly);                                 // les droits, eux, changent
    }

    [Fact]
    public void A_member_without_a_perimeter_of_their_own_takes_the_one_of_their_level_and_migration_copies_it()
    {
        Assert.Equal(["Siège", "Magasin"], Access(sophie).Areas.Select(a => a.Name));
        AccessLevels.Migrate(db);
        Assert.NotNull(sophie.Perimeter);
        Assert.True(sophie.Perimeter!.AllAreas);
        editeur.AllAreas = false; editeur.AreaIds = [siege.Id];                                   // le niveau n'a plus d'effet sur ce qu'elle voit
        Assert.Equal(["Siège", "Magasin"], Access(sophie).Areas.Select(a => a.Name));
    }

    [Fact]
    public void Folder_visibility_is_read_from_the_member_perimeter()
    {
        marie.Perimeter!.MediaVisibility = LevelVisibility.AllRead;
        Assert.Equal(LevelVisibility.AllRead, Access(marie).VisibilityOf(LevelPages.Media));
        Assert.Equal(editeur.VisibilityOf(LevelPages.Media), Access(jean).VisibilityOf(LevelPages.Media));   // sans choix propre : celui du niveau
    }

    [Fact]
    public void A_deleted_zone_leaves_the_member_perimeters()
    {
        var d = new ClientDb(db, client.Id);
        AccessLevels.Forget(d, [], [vitrine.Id]);
        Assert.DoesNotContain(vitrine.Id, jean.Perimeter!.ZoneIds);
    }
}
