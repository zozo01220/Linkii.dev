using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Aires de gestion : contenu propre à chaque aire, rôles par aire, migration, zones, déplacements entre aires, comptage pour la console.</summary>
public class AreasTests
{
    // Horizon SA : aires Lausanne et Genève. Claire administre l'organisation ; Marco administre Lausanne ; Sofia est utilisatrice de Genève.
    readonly Db db = new();
    readonly Tenant client = new() { Name = "Horizon SA" };
    readonly Area lausanne, geneva;
    readonly User claire, marco, sofia;

    public AreasTests()
    {
        db.Clients.Add(client);
        lausanne = new Area { ClientId = client.Id, Name = "Lausanne", CreatedUtc = DateTime.UtcNow.AddDays(-2) };
        geneva = new Area { ClientId = client.Id, Name = "Genève", CreatedUtc = DateTime.UtcNow.AddDays(-1) };
        db.Areas.AddRange(new[] { lausanne, geneva });
        User U(string mail, string role, params (Area, string)[] roles)
        {
            var u = new User { Email = mail, Role = role, ClientId = client.Id, AreaRoles = roles.Select(r => new AreaRole { AreaId = r.Item1.Id, Role = r.Item2 }).ToList() };
            db.Users.Add(u);
            return u;
        }
        claire = U("claire@horizon.ch", Roles.ClientAdmin);
        marco = U("marco@horizon.ch", Roles.ClientMember, (lausanne, AreaRoles.Admin));
        sofia = U("sofia@horizon.ch", Roles.ClientMember, (geneva, AreaRoles.User));
        Screen S(string name, Area a) { var s = new Screen { ClientId = client.Id, AreaId = a.Id, Name = name, Token = Guid.NewGuid().ToString("N") }; db.Screens.Add(s); return s; }
        S("Accueil Lausanne", lausanne);
        S("Cafétéria", lausanne);
        S("Hall Genève", geneva);
    }

    ClientDb As(User u) => new(db, client.Id, AreaAccess.For(db, client.Id, u.Id));

    [Fact]
    public void An_org_admin_has_every_area_and_a_member_only_his_own()
    {
        Assert.Equal(new[] { "Lausanne", "Genève" }, As(claire).Access.Areas.Select(a => a.Name));
        Assert.True(As(claire).Access.CanManage(geneva.Id));
        Assert.Equal(new[] { "Lausanne" }, As(marco).Access.Areas.Select(a => a.Name));
        Assert.True(As(marco).Access.ManagesCurrent);
        Assert.False(As(sofia).Access.ManagesCurrent);   // utilisatrice : contenus, pas les zones ni les membres
        Assert.False(As(marco).Access.CanManage(geneva.Id));
    }

    [Fact]
    public void Only_the_open_area_is_visible_and_new_content_lands_in_it()
    {
        Assert.Equal(2, As(claire).Screens.Count);   // aire ouverte par défaut : la plus ancienne
        Assert.Equal(new[] { "Hall Genève" }, As(sofia).Screens.Select(s => s.Name));

        claire.LastAreas[client.Id] = geneva.Id;      // Claire ouvre Genève
        Assert.Equal(geneva.Id, As(claire).Area!.Id);
        Assert.Equal(new[] { "Hall Genève" }, As(claire).Screens.Select(s => s.Name));

        var pl = new Playlist { Name = "Menus" };
        As(sofia).Playlists.Add(pl);
        Assert.Equal(geneva.Id, pl.AreaId);
        Assert.Empty(As(marco).Playlists);            // Lausanne ne la voit pas

        marco.LastAreas[client.Id] = geneva.Id;       // une aire qui ne lui est pas confiée est ignorée
        Assert.Equal(lausanne.Id, As(marco).Area!.Id);
    }

    [Fact]
    public void Drive_files_are_shared_by_every_area_and_a_member_without_area_sees_nothing()
    {
        var drive = new MediaItem { ClientId = client.Id, Name = "photo", Type = "image", FileName = "d.jpg" };
        drive.Info["drive"] = Guid.NewGuid().ToString();
        db.Media.Add(drive);
        Assert.Contains(As(marco).Media, m => m.Id == drive.Id);
        Assert.Contains(As(sofia).Media, m => m.Id == drive.Id);

        var nobody = new User { Email = "nadia@horizon.ch", Role = Roles.ClientMember, ClientId = client.Id };
        db.Users.Add(nobody);
        Assert.Null(As(nobody).Area);
        Assert.Empty(As(nobody).Screens);
        Assert.Empty(As(nobody).Playlists);
    }

    [Fact]
    public void Existing_data_moves_into_a_default_area_and_drive_files_stay_shared()
    {
        var legacy = new Db();
        var c = new Tenant { Name = "Ancien client" };
        legacy.Clients.Add(c);
        legacy.Screens.Add(new Screen { ClientId = c.Id, Name = "Écran", Token = "t" });
        legacy.Playlists.Add(new Playlist { ClientId = c.Id, Name = "Liste" });
        legacy.Media.Add(new MediaItem { ClientId = c.Id, Name = "image", Type = "image" });
        var drive = new MediaItem { ClientId = c.Id, Name = "drive", Type = "image" };
        drive.Info["drive"] = "x";
        legacy.Media.Add(drive);

        Seed.EnsureAreas(legacy);
        Seed.EnsureAreas(legacy);   // idempotent

        var area = Assert.Single(legacy.Areas);
        Assert.Equal(Seed.DefaultAreaName, area.Name);
        Assert.All(legacy.Screens.Concat<IAreaOwned>(legacy.Playlists), x => Assert.Equal(area.Id, x.AreaId));
        Assert.Equal(area.Id, legacy.Media.First(m => m.Name == "image").AreaId);
        Assert.Equal(Guid.Empty, drive.AreaId);
    }

    [Fact]
    public void Zones_are_managed_by_area_admins_and_deleting_one_sends_its_screens_to_the_default_zone()
    {
        Assert.NotNull(As(sofia).Access.Current);
        Assert.NotNull(AreaOps.AddZone(As(sofia), geneva.Id, "Hall").Error);   // utilisatrice : refusé
        var (zone, err) = AreaOps.AddZone(As(marco), lausanne.Id, "Accueil");
        Assert.Null(err);
        Assert.NotNull(AreaOps.AddZone(As(marco), lausanne.Id, "accueil").Error);   // doublon
        var screen = db.Screens.First(s => s.Name == "Accueil Lausanne");
        screen.ZoneId = zone!.Id;
        Assert.Null(AreaOps.DeleteZone(As(marco), lausanne.Id, zone.Id));
        Assert.Equal(lausanne.Zones.Single(z => z.IsDefault).Id, screen.ZoneId);   // retour dans la zone par défaut
        Assert.Equal(lausanne.Id, screen.AreaId);
    }

    [Fact]
    public void Only_an_org_admin_moves_things_between_areas()
    {
        var screen = db.Screens.First(s => s.Name == "Cafétéria");
        Assert.NotNull(AreaOps.MoveScreen(As(marco), screen.Id, geneva.Id));
        Assert.Equal(lausanne.Id, screen.AreaId);
    }

    [Fact]
    public void A_moved_screen_keeps_its_publication_but_loses_playlists_of_its_old_area()
    {
        var pl = new Playlist { ClientId = client.Id, AreaId = lausanne.Id, Name = "Accueil" };
        db.Playlists.Add(pl);
        var screen = db.Screens.First(s => s.Name == "Cafétéria");
        screen.PlaylistId = pl.Id;
        screen.PublishedVersion = 3;
        Assert.Null(AreaOps.MoveScreen(As(claire), screen.Id, geneva.Id));
        Assert.Equal(geneva.Id, screen.AreaId);
        Assert.Null(screen.PlaylistId);
        Assert.Equal(3, screen.PublishedVersion);
    }

    [Fact]
    public void A_playlist_moves_with_the_contents_it_alone_uses()
    {
        var file = new MediaItem { ClientId = client.Id, AreaId = lausanne.Id, Name = "affiche", Type = "image", FileName = "a.png" };
        var slideshow = new MediaItem { ClientId = client.Id, AreaId = lausanne.Id, Name = "Diaporama", Type = "app", AppId = "slideshow-images" };
        slideshow.Settings["images"] = file.Id.ToString();
        db.Media.AddRange(new[] { file, slideshow });
        var pl = new Playlist { ClientId = client.Id, AreaId = lausanne.Id, Name = "Vitrine" };
        pl.Draft.Add(new PlaylistItem { MediaId = slideshow.Id });
        db.Playlists.Add(pl);
        var other = new Playlist { ClientId = client.Id, AreaId = lausanne.Id, Name = "Autre" };
        other.Draft.Add(new PlaylistItem { MediaId = slideshow.Id });
        db.Playlists.Add(other);
        var screen = db.Screens.First(s => s.Name == "Accueil Lausanne");
        screen.PlaylistId = pl.Id;

        Assert.Contains("Autre", AreaOps.MovePlaylist(As(claire), pl.Id, geneva.Id));   // contenu partagé avec une autre liste : refusé
        Assert.Equal(lausanne.Id, pl.AreaId);

        other.Draft.Clear();
        Assert.Null(AreaOps.MovePlaylist(As(claire), pl.Id, geneva.Id));
        Assert.Equal(geneva.Id, pl.AreaId);
        Assert.Equal(geneva.Id, slideshow.AreaId);
        Assert.Equal(geneva.Id, file.AreaId);         // le fichier affiché par le diaporama suit
        Assert.Null(screen.PlaylistId);               // l'écran de Lausanne n'a plus de liste choisie
    }

    [Fact]
    public void A_file_used_in_its_area_does_not_move()
    {
        var file = new MediaItem { ClientId = client.Id, AreaId = lausanne.Id, Name = "logo", Type = "image", FileName = "l.png" };
        var image = new MediaItem { ClientId = client.Id, AreaId = lausanne.Id, Name = "Image", Type = "app", AppId = "image" };
        image.Settings["image"] = file.Id.ToString();
        db.Media.AddRange(new[] { file, image });
        Assert.NotNull(AreaOps.MoveMedia(As(claire), file.Id, geneva.Id));
        image.Settings["image"] = "";
        Assert.Null(AreaOps.MoveMedia(As(claire), file.Id, geneva.Id));
        Assert.Equal(geneva.Id, file.AreaId);
    }

    [Fact]
    public void An_area_is_deleted_only_when_empty_and_never_the_last_one()
    {
        Assert.NotNull(AreaOps.Delete(As(claire), geneva.Id));   // un écran y est encore
        db.Screens.RemoveAll(s => s.AreaId == geneva.Id);
        Assert.NotNull(AreaOps.Delete(As(marco), geneva.Id));    // membre : refusé
        Assert.Null(AreaOps.Delete(As(claire), geneva.Id));
        Assert.Empty(sofia.AreaRoles);                           // ses membres perdent cet accès
        db.Screens.RemoveAll(_ => true);
        Assert.NotNull(AreaOps.Delete(As(claire), lausanne.Id)); // la dernière aire reste
    }

    [Fact]
    public void A_new_client_starts_with_one_area()
    {
        var d = new Db();
        var r = new Reseller { Name = "Linkii" };
        d.Resellers.Add(r);
        var (c, err) = Tenancy.CreateClient(d, r, "École", "Admin", "admin@ecole.ch", "motdepasse");
        Assert.Null(err);
        Assert.Equal(Seed.DefaultAreaName, Assert.Single(d.Areas, a => a.ClientId == c!.Id).Name);
    }

    [Fact]
    public void Console_counts_users_admins_invitations_activity_and_areas()
    {
        marco.LastActiveUtc = DateTime.UtcNow.AddDays(-2);
        sofia.LastLoginUtc = DateTime.UtcNow.AddDays(-40);
        db.Users.Add(new User { Email = "nadia@horizon.ch", Role = Roles.ClientMember, ClientId = client.Id, MustChangePassword = true });
        db.Users.Add(new User { Email = "linkii@linkii.local", Role = Roles.PlatformAdmin });

        var st = UserStats.Of(db, client);
        Assert.Equal(4, st.Total);
        Assert.Equal(1, st.Admins);
        Assert.Equal(3, st.Members);
        Assert.Equal(1, st.Active30);
        Assert.Equal(1, st.Invited);
        Assert.Equal(2, st.Areas);
        Assert.Equal("1 administrateur · 3 membres", st.Breakdown);
    }
}
