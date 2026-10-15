using Linkii.Poc;
using Xunit;
namespace Linkii.Poc.Tests;

/// <summary>Dossiers des listes de lecture : droits (propriétaire, partages, niveaux, lecture seule), création, partage, rangement, suppression.</summary>
public class PlaylistFolderTests
{
    // Horizon SA, aire Siège. Camille administre ; Alice et Marc éditent (listes et médiathèque) ; Léa est en lecture seule ; Sophie n'a pas la page Listes.
    readonly Db db = new();
    readonly Tenant client = new() { Name = "Horizon SA", FoldersOn = true };
    readonly Area siege;
    readonly User admin, alice, marc, lea, sophie;
    readonly AccessLevel editeur, lecture, sansListes;

    public PlaylistFolderTests()
    {
        db.Clients.Add(client);
        siege = new Area { ClientId = client.Id, Name = "Siège", CreatedUtc = DateTime.UtcNow.AddDays(-2) };
        AreaOps.EnsureDefaultZone(siege);
        db.Areas.Add(siege);

        editeur = new AccessLevel { Name = "Éditeur", Pages = [LevelPages.Screens, LevelPages.Playlists, LevelPages.Media], AllAreas = true, CanCreateFolders = true, CanShareFolders = true };
        lecture = new AccessLevel { Name = "Lecture", Pages = [LevelPages.Playlists], AllAreas = true, ReadOnly = true, CanCreateFolders = true, CanShareFolders = true };
        sansListes = new AccessLevel { Name = "Sans listes", Pages = [LevelPages.Screens, LevelPages.Media], AllAreas = true };
        client.Levels.AddRange([editeur, lecture, sansListes]);

        admin = new User { Email = "camille@horizon.ch", Role = Roles.ClientAdmin, ClientId = client.Id };
        alice = Member("alice@horizon.ch", editeur);
        marc = Member("marc@horizon.ch", editeur);
        lea = Member("lea@horizon.ch", lecture);
        sophie = Member("sophie@horizon.ch", sansListes);
        db.Users.AddRange([admin, alice, marc, lea, sophie]);
    }

    User Member(string email, AccessLevel level) => new() { Email = email, Role = Roles.ClientMember, ClientId = client.Id, LevelId = level.Id };

    ClientDb As(User u) => new(db, client.Id, AreaAccess.For(db, client.Id, u.Id));

    PlaylistFolder MakeFolder(User u, string name, Guid? parent = null)
    {
        var (error, folder) = PlaylistFolderOps.Create(As(u), name, parent);
        Assert.Null(error);
        return folder!;
    }

    Playlist MakeList(string name, Guid? folder = null)
    {
        var p = new Playlist { ClientId = client.Id, AreaId = siege.Id, Name = name, FolderId = folder };
        db.Playlists.Add(p);
        return p;
    }

    FolderRight FolderRightOf(User u, PlaylistFolder f) => FolderAccess.For(As(u).Access, f, PlaylistFolderOps.All(As(u)));

    // ---------- Droits ----------

    [Fact]
    public void A_list_at_the_root_stays_visible_and_editable_for_every_member_with_the_page()
    {
        var p = MakeList("Accueil");
        Assert.Equal(FolderRight.Write, PlaylistOps.RightOf(As(alice), p));
        Assert.Equal(FolderRight.Write, PlaylistOps.RightOf(As(marc), p));
    }

    [Fact]
    public void A_list_in_a_private_folder_is_invisible_to_the_others()
    {
        var f = MakeFolder(alice, "Privé");
        var p = MakeList("Secrète", f.Id);
        Assert.Equal(FolderRight.Owner, PlaylistOps.RightOf(As(alice), p));
        Assert.Equal(FolderRight.None, PlaylistOps.RightOf(As(marc), p));
        Assert.Equal(FolderRight.Owner, PlaylistOps.RightOf(As(admin), p));
    }

    [Fact]
    public void A_read_share_shows_the_lists_but_forbids_editing_them_and_flows_to_subfolders()
    {
        var f = MakeFolder(alice, "Promotions");
        var sub = MakeFolder(alice, "Été 2026", f.Id);
        var p = MakeList("Vitrine", sub.Id);
        Assert.Null(PlaylistFolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, marc.Id, false));
        Assert.Equal(FolderRight.Read, FolderRightOf(marc, sub));
        Assert.Equal(FolderRight.Read, PlaylistOps.RightOf(As(marc), p));
        Assert.False(PlaylistOps.CanEdit(As(marc), p));
    }

    [Fact]
    public void A_write_share_lets_the_member_edit_the_lists_of_the_folder()
    {
        var f = MakeFolder(alice, "Équipe");
        var p = MakeList("Caisse", f.Id);
        Assert.Null(PlaylistFolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, marc.Id, true));
        Assert.True(PlaylistOps.CanEdit(As(marc), p));
    }

    [Fact]
    public void A_member_without_the_playlist_page_cannot_receive_a_share()
    {
        var f = MakeFolder(alice, "Équipe");
        var error = PlaylistFolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, sophie.Id, false);
        Assert.NotNull(error);
        Assert.Contains("Listes de lecture", error);
    }

    [Fact]
    public void A_read_only_level_caps_a_write_share_to_reading()
    {
        var f = MakeFolder(admin, "Commun");
        Assert.Null(PlaylistFolderOps.SetShare(As(admin), f.Id, FolderShareKind.User, lea.Id, true));
        Assert.Equal(FolderRight.Read, FolderRightOf(lea, f));
    }

    // ---------- Rangement et déplacement ----------

    [Fact]
    public void Moving_a_list_into_a_folder_needs_write_on_it()
    {
        var f = MakeFolder(alice, "Équipe");
        var p = MakeList("Accueil");
        Assert.Null(PlaylistFolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, marc.Id, false));

        var (moved, refused) = PlaylistFolderOps.Move(As(marc), new[] { p.Id }, f.Id);
        Assert.Equal(0, moved);
        Assert.NotEmpty(refused);

        (moved, _) = PlaylistFolderOps.Move(As(alice), new[] { p.Id }, f.Id);
        Assert.Equal(1, moved);
        Assert.Equal(f.Id, p.FolderId);
    }

    [Fact]
    public void Moving_a_list_out_to_the_root_needs_the_right_to_create_at_the_root()
    {
        var f = MakeFolder(alice, "Équipe");
        var p = MakeList("Accueil", f.Id);
        var (moved, refused) = PlaylistFolderOps.Move(As(sophie), new[] { p.Id }, null);
        Assert.Equal(0, moved);
        Assert.NotEmpty(refused);
    }

    // ---------- Suppression ----------

    [Fact]
    public void Deleting_a_folder_removes_its_lists_and_subfolders()
    {
        var f = MakeFolder(alice, "Promotions");
        var sub = MakeFolder(alice, "Été", f.Id);
        var p = MakeList("Été 1", sub.Id);
        var error = PlaylistFolderOps.PlanDelete(As(alice), f.Id, out var folders, out var lists);
        Assert.Null(error);
        Assert.Contains(f.Id, folders);
        Assert.Contains(sub.Id, folders);
        Assert.Contains(p.Id, lists);
    }

    [Fact]
    public void A_folder_with_a_subfolder_of_someone_else_cannot_be_deleted()
    {
        var f = MakeFolder(alice, "Promotions");
        Assert.Null(PlaylistFolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, marc.Id, true));
        MakeFolder(marc, "Privé de Marc", f.Id);   // Marc écrit dans le dossier : son sous-dossier lui appartient
        var error = PlaylistFolderOps.PlanDelete(As(alice), f.Id, out _, out _);
        Assert.NotNull(error);
    }

    // ---------- Niveaux ----------

    [Fact]
    public void A_level_share_gives_every_member_of_the_level_the_folder()
    {
        var f = MakeFolder(admin, "Commun");
        var modes = new Dictionary<Guid, string> { [f.Id] = "read" };
        Assert.Null(FolderOps.SetLevelShares(As(admin), editeur.Id, modes));
        Assert.Equal(FolderRight.Read, FolderRightOf(marc, f));
        Assert.Equal(FolderRight.None, FolderRightOf(sophie, f));
    }

    // ---------- Visibilité par le niveau (§52) ----------

    [Fact]
    public void All_folders_gives_the_folders_an_administrator_created_without_sharing_them()
    {
        editeur.AllFolders = "read";
        var f = MakeFolder(admin, "Campagnes");
        var sub = MakeFolder(admin, "Été", f.Id);
        var p = MakeList("Promo", sub.Id);
        Assert.Equal(FolderRight.Read, FolderRightOf(marc, f));
        Assert.Equal(FolderRight.Read, PlaylistOps.RightOf(As(marc), p));

        editeur.AllFolders = "write";
        Assert.Equal(FolderRight.Write, PlaylistOps.RightOf(As(marc), p));

        editeur.AllFolders = null;
        Assert.Equal(FolderRight.None, PlaylistOps.RightOf(As(marc), p));
    }

    [Fact]
    public void All_folders_never_gives_the_private_folder_of_another_member()
    {
        editeur.AllFolders = "write";
        var f = MakeFolder(alice, "Brouillons d'Alice");
        var p = MakeList("Essai", f.Id);
        Assert.Equal(FolderRight.None, PlaylistOps.RightOf(As(marc), p));
        // dès qu'Alice le partage (avec n'importe qui), il fait partie des dossiers de l'organisation
        Assert.Null(PlaylistFolderOps.SetShare(As(alice), f.Id, FolderShareKind.User, lea.Id, false));
        Assert.Equal(FolderRight.Write, PlaylistOps.RightOf(As(marc), p));
    }

    [Fact]
    public void A_folder_whose_owner_was_removed_is_given_by_all_folders()
    {
        editeur.AllFolders = "read";
        var f = MakeFolder(alice, "Repris");
        f.OwnerId = null;
        Assert.Equal(FolderRight.Read, FolderRightOf(marc, f));
    }

    [Fact]
    public void A_chosen_folder_shows_its_lists_and_subfolders_and_an_empty_choice_removes_it()
    {
        var f = MakeFolder(admin, "Accueil");
        var sub = MakeFolder(admin, "Matin", f.Id);
        var p = MakeList("Ouverture", sub.Id);
        Assert.Equal(FolderRight.None, PlaylistOps.RightOf(As(marc), p));

        Assert.Null(FolderOps.SetLevelShares(As(admin), editeur.Id, new Dictionary<Guid, string> { [f.Id] = "write" }));
        Assert.Equal(FolderRight.Write, PlaylistOps.RightOf(As(marc), p));

        // « Aucun » (ou « Tous ») : la page Membres envoie un choix vide, qui retire le partage du niveau
        Assert.Null(FolderOps.SetLevelShares(As(admin), editeur.Id, new Dictionary<Guid, string>()));
        Assert.Equal(FolderRight.None, PlaylistOps.RightOf(As(marc), p));
    }

    [Fact]
    public void A_read_only_level_with_all_folders_in_writing_still_only_reads()
    {
        lecture.AllFolders = "write";
        var p = MakeList("Vitrine", MakeFolder(admin, "Vitrines").Id);
        Assert.Equal(FolderRight.Read, PlaylistOps.RightOf(As(lea), p));
    }

    [Fact]
    public void The_lists_offered_on_screens_are_the_visible_ones_plus_those_already_in_place()
    {
        var hidden = MakeFolder(admin, "Direction");
        var secret = MakeList("Résultats", hidden.Id);
        var used = MakeList("Annonce", hidden.Id);
        var open = MakeList("Accueil");
        db.Screens.Add(new Screen { ClientId = client.Id, AreaId = siege.Id, ZoneId = siege.Zones[0].Id, Name = "Hall", Token = "t", PlaylistId = used.Id });

        var offered = PlaylistOps.Choosable(As(marc)).Select(p => p.Id).ToList();
        Assert.Contains(open.Id, offered);
        Assert.Contains(used.Id, offered);      // déjà sur un écran qu'il voit : le choix actuel reste affiché
        Assert.DoesNotContain(secret.Id, offered);

        Assert.Contains(secret.Id, PlaylistOps.Choosable(As(admin)).Select(p => p.Id));
    }
}
