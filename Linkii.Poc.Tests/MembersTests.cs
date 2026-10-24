using Linkii.Poc;
using Xunit;
namespace Linkii.Poc.Tests;

/// <summary>
/// Membres, droits et partage (24 octobre 2026) : chacun ses écrans (zones ou un à un), ses médias, ses listes et ses dossiers ; la lecture seule par page ;
/// le partage « Peut utiliser » / « Peut modifier » à des personnes ou à toute l'organisation ; les onglets « Mes … / Partagés avec moi ».
/// </summary>
public class MembersTests
{
    // Horizon SA : Camille administre. Inès gère les Caisses et la Vitrine ; Tom gère l'Accueil ; Anaïs voit tous les écrans en lecture seule
    // mais gère ses médias et ses listes ; Paul est en lecture seule partout.
    readonly Db db = new();
    readonly Tenant client = new() { Name = "Horizon SA", FoldersOn = true };
    readonly Area area;
    readonly Zone accueil, caisses;
    readonly Screen accueil1, caisse1, caisse2, vitrine;
    readonly User admin, ines, tom, anais, paul;

    public MembersTests()
    {
        db.Clients.Add(client);
        area = new Area { ClientId = client.Id, Name = "Principale" };
        var def = AreaOps.EnsureDefaultZone(area);
        accueil = new Zone { Name = "Accueil" }; caisses = new Zone { Name = "Caisses" };
        area.Zones.AddRange([accueil, caisses]);
        db.Areas.Add(area);
        accueil1 = Scr("Accueil 1", accueil.Id); caisse1 = Scr("Caisse 1", caisses.Id); caisse2 = Scr("Caisse 2", caisses.Id); vitrine = Scr("Vitrine", def.Id);

        admin = new User { Email = "camille@horizon.ch", Name = "Camille Roux", Role = Roles.ClientAdmin, ClientId = client.Id };
        ines = Member("ines@horizon.ch", new MemberRights { ZoneIds = [caisses.Id], ScreenIds = [vitrine.Id] });
        tom = Member("tom@horizon.ch", new MemberRights { ZoneIds = [accueil.Id] });
        anais = Member("anais@horizon.ch", new MemberRights { Screens = PageMode.Read });
        paul = Member("paul@horizon.ch", new MemberRights { Screens = PageMode.Read, Media = PageMode.Read, Playlists = PageMode.Read });
        db.Users.AddRange([admin, ines, tom, anais, paul]);
    }

    Screen Scr(string name, Guid zone)
    {
        var s = new Screen { ClientId = client.Id, AreaId = area.Id, ZoneId = zone, Name = name, Token = Guid.NewGuid().ToString("N") };
        db.Screens.Add(s);
        return s;
    }

    User Member(string email, MemberRights r) => new() { Email = email, Name = email.Split('@')[0], Role = Roles.ClientMember, ClientId = client.Id, Rights = r };

    ClientDb As(User u) => new(db, client.Id, AreaAccess.For(db, client.Id, u.Id));

    MediaItem File(User u, string name, Guid? folder = null)
    {
        var m = new MediaItem { Name = name, Type = "image", FileName = name + ".jpg", FolderId = folder };
        As(u).Media.Add(m);
        return m;
    }

    MediaFolder Folder(User u, string name, Guid? parent = null)
    {
        var (error, f) = FolderOps.Create(As(u), name, parent);
        Assert.Null(error);
        return f!;
    }

    Playlist List(User u, string name, Guid? folder = null)
    {
        var p = new Playlist { Name = name, FolderId = folder };
        As(u).Playlists.Add(p);
        return p;
    }

    FolderRight FileRight(User u, MediaItem m) => FolderOps.RightOfFile(As(u), m);
    FolderRight ListRight(User u, Playlist p) => PlaylistOps.RightOf(As(u), p);
    void Share(User by, string kind, Guid id, Guid to, bool edit = false) => Assert.Null(ItemShareOps.Set(As(by), kind, [id], new Dictionary<Guid, bool> { [to] = edit }));

    // ---------- Écrans : le seul filtre de visibilité ----------

    [Fact]
    public void A_user_sees_the_screens_of_his_zones_and_those_given_one_by_one()
    {
        Assert.Equal(["Caisse 1", "Caisse 2", "Vitrine"], As(ines).Screens.Select(s => s.Name).Order().ToList());
        Assert.Equal(["Accueil 1"], As(tom).Screens.Select(s => s.Name).ToList());
    }

    [Fact]
    public void A_zone_gives_the_screens_added_to_it_later()
    {
        Scr("Caisse 3", caisses.Id);
        Assert.Contains(As(ines).Screens, s => s.Name == "Caisse 3");
        Assert.DoesNotContain(As(tom).Screens, s => s.Name == "Caisse 3");
    }

    [Fact]
    public void Read_only_on_screens_sees_every_screen_and_cannot_write_them()
    {
        Assert.Equal(4, As(anais).Screens.Count());
        Assert.False(As(anais).Access.CanWrite(LevelPages.Screens));
        Assert.True(As(anais).Access.CanWrite(LevelPages.Media));   // la lecture seule se règle page par page
        Assert.True(As(paul).Access.ReadOnly);
    }

    [Fact]
    public void Administrators_see_every_screen_and_alone_manage_zones()
    {
        Assert.Equal(4, As(admin).Screens.Count());
        Assert.True(As(admin).Access.CanManageZones);
        Assert.False(As(ines).Access.CanManageZones);
        Assert.NotNull(AreaOps.AddZone(As(ines), area.Id, "Terrasse").Error);
    }

    [Fact]
    public void Several_users_can_manage_the_same_screen_and_the_admin_assigns_it()
    {
        Assert.Null(MemberOps.AssignScreen(As(admin), accueil1.Id, [ines.Id]));
        Assert.Contains(As(ines).Screens, s => s.Id == accueil1.Id);
        Assert.Contains(As(tom).Screens, s => s.Id == accueil1.Id);   // Tom le garde par sa zone
        Assert.Equal(2, MemberOps.ManagersOf(As(admin), accueil1).Count);
        Assert.NotNull(MemberOps.AssignScreen(As(ines), accueil1.Id, [tom.Id]));   // administrateur seulement
    }

    [Fact]
    public void A_user_who_connects_a_screen_manages_it()
    {
        var s = new Screen { ClientId = client.Id, AreaId = area.Id, ZoneId = area.Zones[0].Id, Name = "Nouvel écran", Token = "t" };
        db.Screens.Add(s);
        Assert.DoesNotContain(As(tom).Screens, x => x.Id == s.Id);
        MemberOps.AdoptScreen(As(tom), s.Id);
        Assert.Contains(As(tom).Screens, x => x.Id == s.Id);
    }

    [Fact]
    public void Users_see_every_area_their_screens_are_filtered()
    {
        var other = new Area { ClientId = client.Id, Name = "Genève" };
        AreaOps.EnsureDefaultZone(other);
        db.Areas.Add(other);
        Assert.Equal(2, As(ines).Access.Areas.Count);
    }

    // ---------- Chacun ses médias, ses listes, ses dossiers ----------

    [Fact]
    public void What_a_user_creates_is_his_and_invisible_to_the_others()
    {
        var m = File(ines, "Affiche");
        Assert.Equal(ines.Id, m.OwnerId);
        Assert.Equal(FolderRight.Owner, FileRight(ines, m));
        Assert.Equal(FolderRight.None, FileRight(tom, m));
        Assert.Equal(FolderRight.Owner, FileRight(admin, m));

        var p = List(tom, "Accueil matin");
        Assert.Equal(tom.Id, p.OwnerId);
        Assert.Equal(FolderRight.None, ListRight(ines, p));
    }

    [Fact]
    public void Read_only_on_a_page_shows_everything_of_the_organisation_without_writing()
    {
        var m = File(ines, "Affiche");
        var p = List(tom, "Accueil");
        Assert.Equal(FolderRight.Read, FileRight(paul, m));
        Assert.Equal(FolderRight.Read, ListRight(paul, p));
        Assert.Equal(FolderRight.None, FileRight(anais, m));   // Anaïs gère ses médias : elle ne voit que les siens
        Assert.False(FolderAccess.CanCreateAtRoot(As(paul).Access, LevelPages.Media));
    }

    [Fact]
    public void A_file_in_my_folder_is_mine_to_manage_even_if_a_colleague_added_it()
    {
        var f = Folder(ines, "Promotions");
        Share(ines, ShareKind.MediaFolder, f.Id, tom.Id, edit: true);
        var added = File(tom, "Ajout de Tom", f.Id);
        Assert.Equal(FolderRight.Owner, FileRight(ines, added));
        Assert.Equal(FolderRight.Owner, FileRight(tom, added));
    }

    // ---------- Partage ----------

    [Fact]
    public void Can_use_reads_and_can_modify_writes_through_a_folder_and_its_subfolders()
    {
        var f = Folder(ines, "Promotions");
        var sub = Folder(ines, "Été", f.Id);
        var m = File(ines, "Affiche", sub.Id);
        Share(ines, ShareKind.MediaFolder, f.Id, tom.Id);
        Assert.Equal(FolderRight.Read, FileRight(tom, m));
        Assert.Equal(FolderRight.Read, FolderOps.RightOf(As(tom), sub));
        Assert.False(FolderOps.CanEditFile(As(tom), m));

        Share(ines, ShareKind.MediaFolder, f.Id, tom.Id, edit: true);
        Assert.Equal(FolderRight.Write, FileRight(tom, m));
        Assert.False(FolderOps.CanDeleteFile(As(tom), File(ines, "Seul")));   // un média partagé seul ne se supprime pas
        Assert.True(FolderOps.CanDeleteFile(As(tom), m));                    // « ajouter et retirer » dans un dossier qu'il modifie
        Assert.NotNull(FolderOps.Rename(As(tom), f.Id, "Autre"));             // renommer le dossier reste au propriétaire
    }

    [Fact]
    public void Sharing_with_the_whole_organisation_reaches_everyone_and_only_the_owner_shares()
    {
        var p = List(ines, "Campagne");
        Assert.Null(ItemShareOps.Set(As(ines), ShareKind.Playlist, [p.Id], new Dictionary<Guid, bool> { [ItemShareOps.Everyone] = false }));
        Assert.Equal(FolderRight.Read, ListRight(tom, p));
        Assert.Equal(FolderRight.Read, ListRight(anais, p));
        Assert.NotNull(ItemShareOps.Set(As(tom), ShareKind.Playlist, [p.Id], new Dictionary<Guid, bool> { [anais.Id] = true }));
        Assert.NotNull(ItemShareOps.Leave(As(tom), ShareKind.Playlist, p.Id));   // un partage à tous ne se quitte pas
    }

    [Fact]
    public void One_item_replaces_its_shares_several_items_only_add()
    {
        var a = File(ines, "A"); var b = File(ines, "B");
        Share(ines, ShareKind.Media, a.Id, tom.Id);
        Assert.Null(ItemShareOps.Set(As(ines), ShareKind.Media, [a.Id], new Dictionary<Guid, bool> { [anais.Id] = true }));
        Assert.Equal(FolderRight.None, FileRight(tom, a));
        Assert.Equal(FolderRight.Write, FileRight(anais, a));
        Assert.Null(ItemShareOps.Set(As(ines), ShareKind.Media, [a.Id, b.Id], new Dictionary<Guid, bool> { [tom.Id] = false }));
        Assert.Equal(FolderRight.Write, FileRight(anais, a));
        Assert.Equal(FolderRight.Read, FileRight(tom, b));
    }

    [Fact]
    public void The_recipient_can_stop_seeing_a_direct_share_and_deleting_the_item_drops_its_shares()
    {
        var m = File(ines, "Affiche");
        Share(ines, ShareKind.Media, m.Id, tom.Id);
        Assert.Null(ItemShareOps.Leave(As(tom), ShareKind.Media, m.Id));
        Assert.Equal(FolderRight.None, FileRight(tom, m));

        var p = List(ines, "Vitrine");
        Share(ines, ShareKind.Playlist, p.Id, tom.Id);
        PlaylistOps.Remove(As(ines), [p.Id]);
        Assert.Empty(db.ItemShares);
    }

    [Fact]
    public void A_read_only_page_caps_a_share_in_writing()
    {
        var p = List(ines, "Vitrine");
        Share(ines, ShareKind.Playlist, p.Id, paul.Id, edit: true);
        Assert.Equal(FolderRight.Read, ListRight(paul, p));
    }

    [Fact]
    public void Lists_offered_on_screens_are_mine_and_those_shared_with_me()
    {
        var mine = List(tom, "Accueil");
        var shared = List(ines, "Campagne");
        var hidden = List(ines, "Brouillon");
        Share(ines, ShareKind.Playlist, shared.Id, tom.Id);
        var names = PlaylistOps.Choosable(As(tom)).Select(p => p.Name).ToList();
        Assert.Contains("Accueil", names);
        Assert.Contains("Campagne", names);
        Assert.DoesNotContain("Brouillon", names);
    }

    // ---------- Onglets ----------

    [Fact]
    public void Tabs_separate_my_items_from_those_shared_with_me()
    {
        var mine = new LibraryTab.Info(tom.Id, tom.Id, false);
        var received = new LibraryTab.Info(ines.Id, ines.Id, true);
        var legacy = new LibraryTab.Info(null, null, false);
        Assert.True(LibraryTab.InTab(LibraryTab.Mine, mine, tom.Id, admin: false));
        Assert.False(LibraryTab.InTab(LibraryTab.Shared, mine, tom.Id, admin: false));
        Assert.True(LibraryTab.InTab(LibraryTab.Shared, received, tom.Id, admin: false));
        Assert.True(LibraryTab.InTab(LibraryTab.Mine, legacy, admin.Id, admin: true));   // les éléments d'avant les propriétaires sont ceux des administrateurs
        Assert.False(LibraryTab.InTab(LibraryTab.Shared, legacy, admin.Id, admin: true));
    }

    [Fact]
    public void A_shared_folder_brings_its_subfolders_into_the_tab()
    {
        var root = Guid.NewGuid(); var sub = Guid.NewGuid(); var other = Guid.NewGuid();
        var set = LibraryTab.Folders(LibraryTab.Shared, [
            (root, null, new LibraryTab.Info(ines.Id, ines.Id, true)),
            (sub, root, new LibraryTab.Info(ines.Id, ines.Id, true)),
            (other, null, new LibraryTab.Info(tom.Id, tom.Id, false))], tom.Id, false);
        Assert.Contains(root, set);
        Assert.Contains(sub, set);
        Assert.DoesNotContain(other, set);
    }

    // ---------- Membres ----------

    [Fact]
    public void Only_an_admin_creates_or_changes_a_member()
    {
        var (u, err) = MemberOps.Create(As(admin), "Luc Favre", "luc@horizon.ch", false, new MemberRights { Media = PageMode.Read }, "Provisoire1!");
        Assert.Null(err);
        Assert.Equal(PageMode.Read, u!.Rights!.Media);
        Assert.NotNull(MemberOps.Create(As(ines), "X", "x@horizon.ch", false, new(), "Provisoire1!").Error);
        Assert.NotNull(MemberOps.Create(As(admin), "Luc", "luc@horizon.ch", false, new(), "Provisoire1!").Error);   // adresse déjà prise
        Assert.Null(MemberOps.Save(As(admin), tom.Id, "Tom", true, new()));
        Assert.Equal(Roles.ClientAdmin, tom.Role);
    }

    [Fact]
    public void The_organisation_keeps_an_admin_and_one_cannot_demote_oneself()
    {
        Assert.NotNull(MemberOps.Save(As(admin), admin.Id, "Camille", false, new()));
    }

    [Fact]
    public void A_removed_member_hands_everything_to_the_admin_who_removes_him()
    {
        var f = Folder(tom, "Accueil");
        var m = File(tom, "Plan", f.Id);
        var p = List(tom, "Matin");
        Share(tom, ShareKind.Playlist, p.Id, ines.Id);
        Assert.Null(MemberOps.Remove(As(admin), tom.Id));
        Assert.DoesNotContain(db.Users, u => u.Id == tom.Id);
        Assert.Equal(admin.Id, f.OwnerId);
        Assert.Equal(admin.Id, m.OwnerId);
        Assert.Equal(admin.Id, p.OwnerId);
        Assert.Equal(FolderRight.Read, ListRight(ines, p));   // les partages restent
    }

    [Fact]
    public void Migration_turns_old_roles_into_page_modes_and_zones_and_person_folder_shares_into_shares()
    {
        var editor = AccessLevels.NewEditor();
        var reader = AccessLevels.NewReader();
        client.Levels.AddRange([editor, reader]);
        var old = new User { Email = "old@horizon.ch", Role = Roles.ClientMember, ClientId = client.Id };
        old.SetAccesses([new MemberAccess { LevelId = editor.Id, Perimeter = new Perimeter { ZoneIds = [caisses.Id] } }]);
        var reading = new User { Email = "read@horizon.ch", Role = Roles.ClientMember, ClientId = client.Id };
        reading.SetAccesses([new MemberAccess { LevelId = reader.Id, Perimeter = Perimeter.Everything() }]);
        db.Users.AddRange([old, reading]);
        var folder = new MediaFolder { ClientId = client.Id, AreaId = area.Id, Name = "Ancien", OwnerId = admin.Id };
        folder.Shares.Add(new FolderShare { Kind = FolderShareKind.User, TargetId = old.Id, Write = true });
        db.MediaFolders.Add(folder);

        MemberOps.MigrateV3(db);

        Assert.Equal(PageMode.Own, old.Rights!.Screens);
        Assert.Equal([caisses.Id], old.Rights.ZoneIds);
        Assert.Equal(PageMode.Read, reading.Rights!.Media);
        Assert.Equal(PageMode.Read, reading.Rights.Screens);
        Assert.Contains(db.ItemShares, s => s.ItemId == folder.Id && s.ToUserId == old.Id && s.Edit);
        Assert.Empty(folder.Shares);
    }
}
