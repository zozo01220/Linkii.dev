using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Déplacer un dossier dans un autre (médiathèque et listes) ; à la racine, le dossier est dissous et son contenu va à la racine.</summary>
public class FolderMoveTests
{
    readonly Db db = new();
    readonly Tenant client = new() { Name = "Dossiers SA", FoldersOn = true };
    readonly Area siege;
    readonly User alice, marc;
    readonly AccessLevel editeur;

    public FolderMoveTests()
    {
        db.Clients.Add(client);
        siege = new Area { ClientId = client.Id, Name = "Siège" };
        AreaOps.EnsureDefaultZone(siege); db.Areas.Add(siege);
        editeur = new AccessLevel { Name = "Éditeur", Pages = [LevelPages.Screens, LevelPages.Playlists, LevelPages.Media], AllAreas = true, CanCreateFolders = true, CanShareFolders = true };
        client.Levels.Add(editeur);
        alice = new User { Email = "alice@d.ch", Role = Roles.ClientMember, ClientId = client.Id, LevelId = editeur.Id };
        marc = new User { Email = "marc@d.ch", Role = Roles.ClientMember, ClientId = client.Id, LevelId = editeur.Id };
        db.Users.AddRange([alice, marc]);
    }

    ClientDb As(User u) => new(db, client.Id, AreaAccess.For(db, client.Id, u.Id));

    MediaFolder Folder(User u, string name, MediaFolder? parent = null)
    {
        var (err, f) = FolderOps.Create(As(u), name, parent?.Id);
        Assert.Null(err);
        return f!;
    }

    [Fact]
    public void A_folder_goes_into_another_folder_and_not_into_itself_or_a_descendant()
    {
        var a = Folder(alice, "A"); var b = Folder(alice, "B"); var sub = Folder(alice, "Sous", a);
        Assert.Null(FolderOps.MoveFolder(As(alice), b.Id, a.Id));
        Assert.Equal(a.Id, b.ParentId);
        Assert.NotNull(FolderOps.MoveFolder(As(alice), a.Id, a.Id));       // pas dans lui-même
        Assert.NotNull(FolderOps.MoveFolder(As(alice), a.Id, sub.Id));     // ni dans un descendant
        Assert.Null(a.ParentId);
    }

    [Fact]
    public void Only_the_owner_moves_a_folder_and_the_target_must_be_writable_and_free_of_the_same_name()
    {
        var a = Folder(alice, "A"); var b = Folder(marc, "B"); var clash = Folder(alice, "A2");
        Assert.NotNull(FolderOps.MoveFolder(As(marc), a.Id, b.Id));        // pas son dossier : il ne le voit même pas
        Assert.NotNull(FolderOps.MoveFolder(As(alice), a.Id, b.Id));       // le dossier de Marc est privé : pas d'écriture
        var other = Folder(alice, "X", clash); var dup = Folder(alice, "X");
        Assert.NotNull(FolderOps.MoveFolder(As(alice), dup.Id, clash.Id)); // « X » existe déjà là
    }

    [Fact]
    public void Folders_do_not_nest_beyond_the_maximum_depth()
    {
        var a = Folder(alice, "A"); var b = Folder(alice, "B", a); var c = Folder(alice, "C", b); var d4 = Folder(alice, "D", c);
        var lone = Folder(alice, "Seul"); var child = Folder(alice, "Enfant", lone);
        Assert.NotNull(FolderOps.MoveFolder(As(alice), lone.Id, d4.Id));   // 4 + 2 niveaux
        Assert.Null(lone.ParentId);
    }

    [Fact]
    public void Moving_to_the_root_dissolves_the_folder_and_sends_its_files_and_subfolders_to_the_root()
    {
        var a = Folder(alice, "A"); var sub = Folder(alice, "Sous", a);
        var file = new MediaItem { ClientId = client.Id, AreaId = siege.Id, Name = "photo", Type = "image", FileName = "p.png", FolderId = a.Id };
        db.Media.Add(file);
        Assert.Equal(new FolderContents(1, 1), FolderOps.ContentsOf(As(alice), a.Id));

        Assert.Null(FolderOps.MoveFolder(As(alice), sub.Id, a.Id));         // déjà dedans : rien
        var nested = Folder(alice, "Imbriqué", a);
        Assert.Null(FolderOps.MoveFolder(As(alice), nested.Id, null));      // un sous-dossier à la racine : dissous
        Assert.DoesNotContain(db.MediaFolders, f => f.Id == nested.Id);

        Assert.Null(FolderOps.MoveFolder(As(alice), sub.Id, null));
        Assert.Null(FolderOps.MoveFolder(As(alice), a.Id, null));           // à la racine déjà : rien
        Assert.Contains(db.MediaFolders, f => f.Id == a.Id);
        var top = Folder(alice, "Top"); var inner = Folder(alice, "Inner", top);
        var f2 = new MediaItem { ClientId = client.Id, AreaId = siege.Id, Name = "doc", Type = "image", FileName = "d.png", FolderId = inner.Id };
        db.Media.Add(f2);
        Assert.Null(FolderOps.MoveFolder(As(alice), inner.Id, null));
        Assert.Null(f2.FolderId);                                           // le fichier est à la racine
        Assert.Equal(alice.Id, f2.OwnerId);                                 // et reste visible de son propriétaire
        Assert.DoesNotContain(db.MediaFolders, f => f.Id == inner.Id);
    }

    [Fact]
    public void Playlist_folders_move_and_dissolve_the_same_way()
    {
        var (_, a) = PlaylistFolderOps.Create(As(alice), "A", null);
        var (_, b) = PlaylistFolderOps.Create(As(alice), "B", null);
        Assert.Null(PlaylistFolderOps.MoveFolder(As(alice), b!.Id, a!.Id));
        Assert.Equal(a.Id, b.ParentId);
        var pl = new Playlist { ClientId = client.Id, AreaId = siege.Id, Name = "Liste", FolderId = b.Id };
        db.Playlists.Add(pl);
        Assert.Equal(new FolderContents(1, 0), PlaylistFolderOps.ContentsOf(As(alice), b.Id));
        Assert.Null(PlaylistFolderOps.MoveFolder(As(alice), b.Id, null));
        Assert.Null(pl.FolderId);
        Assert.DoesNotContain(db.PlaylistFolders, f => f.Id == b.Id);
    }

    [Fact]
    public void Destinations_list_the_root_and_the_folders_with_the_reason_when_impossible()
    {
        var a = Folder(alice, "A"); var sub = Folder(alice, "Sous", a); var b = Folder(alice, "B");
        var targets = FolderOps.MoveTargets(As(alice), sub.Id);
        Assert.Null(targets[0].Id); Assert.Null(targets[0].Reason);                       // racine : possible (dissolution)
        Assert.Equal("déjà ici", targets.First(t => t.Id == a.Id).Reason);               // son parent
        Assert.NotNull(targets.First(t => t.Id == sub.Id).Reason);                       // lui-même
        Assert.Null(targets.First(t => t.Id == b.Id).Reason);
    }
}
