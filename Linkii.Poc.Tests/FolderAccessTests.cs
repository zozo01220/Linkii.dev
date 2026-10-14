using Linkii.Poc;
using Xunit;
namespace Linkii.Poc.Tests;

/// <summary>Dossiers de la médiathèque (§49) : droits (propriétaire, partages, niveaux, lecture seule), création, partage, suppression, rangement des fichiers.</summary>
public class FolderAccessTests
{
    // Horizon SA : Siège et Magasin. Camille administre ; Alice et Marc sont éditeurs ; Léa est en lecture seule ; Sophie ne voit que le Magasin, sans médiathèque.
    readonly Db db = new();
    readonly Tenant client = new() { Name = "Horizon SA", FoldersOn = true };
    readonly Area siege, magasin;
    readonly User admin, alice, marc, lea, sophie;
    readonly AccessLevel editeur, lecture, caisse;

    public FolderAccessTests()
    {
        db.Clients.Add(client);
        siege = new Area { ClientId = client.Id, Name = "Siège", CreatedUtc = DateTime.UtcNow.AddDays(-2) };
        magasin = new Area { ClientId = client.Id, Name = "Magasin", CreatedUtc = DateTime.UtcNow.AddDays(-1) };
        foreach (var a in new[] { siege, magasin }) { AreaOps.EnsureDefaultZone(a); db.Areas.Add(a); }

        editeur = new AccessLevel { Name = "Éditeur", Pages = [LevelPages.Screens, LevelPages.Playlists, LevelPages.Media], AllAreas = true, CanCreateFolders = true, CanShareFolders = true };
        lecture = new AccessLevel { Name = "Lecture", Pages = [LevelPages.Media], AllAreas = true, ReadOnly = true, CanCreateFolders = true, CanShareFolders = true };
        caisse = new AccessLevel { Name = "Caisse", Pages = [LevelPages.Screens], AreaIds = [magasin.Id] };
        client.Levels.AddRange([editeur, lecture, caisse]);

        admin = new User { Email = "camille@horizon.ch", Role = Roles.ClientAdmin, ClientId = client.Id };
        alice = Member("alice@horizon.ch", editeur);
        marc = Member("marc@horizon.ch", editeur);
        lea = Member("lea@horizon.ch", lecture);
        sophie = Member("sophie@horizon.ch", caisse);
        db.Users.AddRange([admin, alice, marc, lea, sophie]);
    }

    User Member(string email, AccessLevel level)
    {
        var u = new User { Email = email, Role = Roles.ClientMember, ClientId = client.Id, LevelId = level.Id };
        return u;
    }

    ClientDb As(User u) => new(db, client.Id, AreaAccess.For(db, client.Id, u.Id));

    FolderRight Right(User u, MediaFolder f)
    {
        var d = As(u);
        return FolderAccess.For(d.Access, f, FolderOps.All(d));
    }

    MediaFolder Make(User u, string name, Guid? parent = null)
    {
        var (error, folder) = FolderOps.Create(As(u), name, parent);
        Assert.Null(error);
        return folder!;
    }

    // ---------- Droits ----------

    [Fact]
    public void The_administrator_sees_every_folder_as_owner()
    {
        var f = Make(alice, "Privé d'Alice");
        Assert.Equal(FolderRight.Owner, Right(admin, f));
    }

    [Fact]
    public void A_private_folder_of_another_member_is_invisible_to_the_others()
    {
        var f = Make(alice, "Privé");
        Assert.Equal(FolderRight.Owner, Right(alice, f));
        Assert.Equal(FolderRight.None, Right(marc, f));
    }

    [Fact]
    public void A_share_to_a_person_gives_read_or_write_and_flows_down_to_subfolders()
    {
        var f = Make(alice, "Promotions");
        var sub = Make(alice, "Été 2026", f.Id);
        Assert.Null(FolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, marc.Id, false));
        Assert.Equal(FolderRight.Read, Right(marc, f));
        Assert.Equal(FolderRight.Read, Right(marc, sub));

        Assert.Null(FolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, marc.Id, true));
        Assert.Equal(FolderRight.Write, Right(marc, sub));
    }

    [Fact]
    public void A_share_to_a_level_applies_to_its_members_only()
    {
        var f = Make(alice, "Événements");
        Assert.Null(FolderOps.SetShare(As(alice), f.Id, FolderShareKind.Level, editeur.Id, true));
        Assert.Equal(FolderRight.Write, Right(marc, f));
        Assert.Equal(FolderRight.None, Right(sophie, f));
    }

    [Fact]
    public void A_read_only_level_caps_every_right_to_reading()
    {
        var f = Make(alice, "Identité");
        Assert.Null(FolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, lea.Id, true));
        Assert.Equal(FolderRight.Read, Right(lea, f));
    }

    [Fact]
    public void All_folders_read_gives_the_shared_folders_but_never_a_private_one()
    {
        editeur.AllFolders = "read";
        var shared = Make(alice, "Partagé");
        var privateOne = Make(alice, "Privé");
        Assert.Null(FolderOps.SetShare(As(alice), shared.Id, FolderShareKind.User, lea.Id, false));

        Assert.Equal(FolderRight.Read, Right(marc, shared));
        Assert.Equal(FolderRight.None, Right(marc, privateOne));
    }

    [Fact]
    public void A_member_outside_the_area_of_a_folder_sees_nothing_of_it()
    {
        var f = Make(alice, "Siège seulement");
        f.Shares.Add(new FolderShare { Kind = FolderShareKind.User, TargetId = sophie.Id, Write = true });   // partage forcé, hors de son aire
        Assert.Equal(FolderRight.None, Right(sophie, f));
    }

    // ---------- Création ----------

    [Fact]
    public void Only_a_level_allowed_to_create_folders_creates_them_at_the_root()
    {
        var (error, _) = FolderOps.Create(As(lea), "Nouveau", null);
        Assert.NotNull(error);   // lecture seule
        (error, _) = FolderOps.Create(As(sophie), "Nouveau", null);
        Assert.NotNull(error);   // niveau sans la permission
        (error, _) = FolderOps.Create(As(marc), "Nouveau", null);
        Assert.Null(error);
    }

    [Fact]
    public void Creating_inside_a_folder_needs_write_on_it_and_respects_the_depth_limit()
    {
        var f = Make(alice, "Racine");
        Assert.NotNull(FolderOps.Create(As(marc), "Sans droit", f.Id).Error);
        FolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, marc.Id, false);
        Assert.NotNull(FolderOps.Create(As(marc), "Lecture seule", f.Id).Error);
        FolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, marc.Id, true);
        var niveau2 = Make(marc, "2", f.Id);
        var niveau3 = Make(marc, "3", niveau2.Id);
        var niveau4 = Make(marc, "4", niveau3.Id);
        Assert.NotNull(FolderOps.Create(As(marc), "5", niveau4.Id).Error);   // au-delà de quatre niveaux
    }

    [Fact]
    public void Two_folders_cannot_share_a_name_in_the_same_place()
    {
        Make(alice, "Soldes");
        Assert.NotNull(FolderOps.Create(As(alice), "soldes", null).Error);
    }

    [Fact]
    public void Nothing_can_be_created_while_the_option_is_off()
    {
        client.FoldersOn = false;
        Assert.NotNull(FolderOps.Create(As(alice), "Refusé", null).Error);
    }

    // ---------- Partage ----------

    [Fact]
    public void Only_the_owner_shares_and_only_with_the_share_right()
    {
        var f = Make(alice, "À moi");
        Assert.NotNull(FolderOps.SetShare(As(marc), f.Id, FolderShareKind.User, lea.Id, false));   // pas le propriétaire
        Assert.Null(FolderOps.SetShare(As(admin), f.Id, FolderShareKind.User, marc.Id, false));    // l'administrateur partage tout
    }

    [Fact]
    public void A_member_without_the_share_right_cannot_share_his_own_folder()
    {
        var creer = new AccessLevel { Name = "Créer seulement", Pages = [LevelPages.Media], AllAreas = true, CanCreateFolders = true };
        client.Levels.Add(creer);
        var pierre = Member("pierre@horizon.ch", creer);
        db.Users.Add(pierre);
        var f = Make(pierre, "Sien");
        Assert.NotNull(FolderOps.SetShare(As(pierre), f.Id, FolderShareKind.User, marc.Id, false));
    }

    [Fact]
    public void A_person_who_cannot_see_the_media_page_or_the_area_cannot_receive_a_share()
    {
        var f = Make(alice, "Siège");
        Assert.NotNull(FolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, sophie.Id, true));   // pas dans l'aire Siège
        Assert.NotNull(FolderOps.SetShare(As(alice), f.Id, FolderShareKind.Level, Guid.NewGuid(), true));   // niveau inconnu
    }

    [Fact]
    public void Removing_a_share_takes_the_access_back()
    {
        var f = Make(alice, "Retrait");
        FolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, marc.Id, true);
        Assert.Null(FolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, marc.Id, null));
        Assert.Equal(FolderRight.None, Right(marc, f));
    }

    [Fact]
    public void Deleting_a_level_removes_its_folder_shares()
    {
        var temp = new AccessLevel { Name = "Temporaire", Pages = [LevelPages.Media], AllAreas = true };
        client.Levels.Add(temp);
        var f = Make(alice, "Partagé au niveau");
        FolderOps.SetShare(As(alice), f.Id, FolderShareKind.Level, temp.Id, false);
        Assert.Null(AccessLevels.Delete(As(admin), temp.Id));
        Assert.DoesNotContain(f.Shares, s => s.TargetId == temp.Id);
    }

    // ---------- Suppression ----------

    [Fact]
    public void Deleting_a_folder_plans_its_subfolders_and_their_files()
    {
        var f = Make(alice, "Racine");
        var sub = Make(alice, "Sous", f.Id);
        var file = new MediaItem { ClientId = client.Id, AreaId = siege.Id, Name = "a.jpg", FolderId = sub.Id };
        db.Media.Add(file);

        var error = FolderOps.PlanDelete(As(alice), f.Id, out var folders, out var files);
        Assert.Null(error);
        Assert.Equal(2, folders.Count);
        Assert.Equal(new[] { file.Id }, files);
    }

    [Fact]
    public void A_subfolder_owned_by_someone_else_blocks_the_deletion()
    {
        var f = Make(alice, "Racine");
        FolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, marc.Id, true);   // Marc peut créer dedans, le sous-dossier reste à lui
        Make(marc, "Celui de Marc", f.Id);
        Assert.NotNull(FolderOps.PlanDelete(As(alice), f.Id, out _, out _));
    }

    // ---------- Rangement des fichiers ----------

    [Fact]
    public void Files_move_into_a_folder_where_the_member_writes_and_back_to_the_root()
    {
        var f = Make(alice, "Ateliers");
        var photo = new MediaItem { ClientId = client.Id, AreaId = siege.Id, Name = "photo.jpg" };
        db.Media.Add(photo);

        Assert.Equal(1, FolderOps.Move(As(alice), [photo.Id], f.Id).Moved);
        Assert.Equal(f.Id, photo.FolderId);
        Assert.Equal(1, FolderOps.Move(As(alice), [photo.Id], null).Moved);   // retour à la racine : pouvoir créer à la racine
        Assert.Null(photo.FolderId);
    }

    [Fact]
    public void A_member_cannot_move_files_into_a_folder_he_does_not_write()
    {
        var f = Make(alice, "Privé");
        var photo = new MediaItem { ClientId = client.Id, AreaId = siege.Id, Name = "photo.jpg" };
        db.Media.Add(photo);

        var (moved, refused) = FolderOps.Move(As(marc), [photo.Id], f.Id);
        Assert.Equal(0, moved);
        Assert.NotEmpty(refused);
        Assert.Null(photo.FolderId);
    }

    [Fact]
    public void A_file_from_another_area_cannot_go_into_a_folder_of_this_one()
    {
        var f = Make(alice, "Siège");
        var ailleurs = new MediaItem { ClientId = client.Id, AreaId = magasin.Id, Name = "vitrine.jpg" };
        db.Media.Add(ailleurs);

        var (moved, refused) = FolderOps.Move(As(alice), [ailleurs.Id], f.Id);
        Assert.Equal(0, moved);
        Assert.Contains(refused, r => r.Contains("autre aire"));
    }

    [Fact]
    public void A_file_is_editable_at_the_root_and_only_in_a_folder_where_one_writes()
    {
        var f = Make(alice, "Partagé en lecture");
        FolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, marc.Id, false);
        var root = new MediaItem { ClientId = client.Id, AreaId = siege.Id, Name = "racine.jpg" };
        var inside = new MediaItem { ClientId = client.Id, AreaId = siege.Id, Name = "dans.jpg", FolderId = f.Id };

        Assert.True(FolderOps.CanEditFile(As(marc), root));
        Assert.False(FolderOps.CanEditFile(As(marc), inside));
        Assert.True(FolderOps.CanEditFile(As(alice), inside));
    }
}
