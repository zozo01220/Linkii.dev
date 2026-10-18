using Linkii.Poc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Partage entre aires (lien vivant, sans copie) : médias, dossiers de médias, listes de lecture et dossiers de listes ; modifiables par l'aire qui reçoit, retirables.</summary>
[Collection("platform")]
public class AreaShareTests
{
    readonly JsonStore store;
    readonly Tenant client;
    readonly Area siege, magasin;
    readonly User camille;   // administratrice : ouvre l'aire qu'on veut
    readonly MediaItem photo, usedByList;
    readonly Playlist hall;
    readonly Screen borne;

    public AreaShareTests(Platform p)
    {
        store = p.Services.GetRequiredService<JsonStore>();
        (client, siege, magasin, camille, photo, usedByList, hall, borne) = store.Write(db =>
        {
            var c = new Tenant { ResellerId = p.R1.Id, Name = "Aires SA", FoldersOn = true };
            var a1 = new Area { ClientId = c.Id, Name = "Siège", CreatedUtc = DateTime.UtcNow.AddDays(-2) };
            var a2 = new Area { ClientId = c.Id, Name = "Magasin", CreatedUtc = DateTime.UtcNow.AddDays(-1) };
            AreaOps.EnsureDefaultZone(a1); AreaOps.EnsureDefaultZone(a2);
            db.Clients.Add(c); db.Areas.AddRange([a1, a2]);
            var admin = new User { Email = "camille@aires.ch", Name = "Camille", Role = Roles.ClientAdmin, ClientId = c.Id };
            db.Users.Add(admin);
            var ph = new MediaItem { ClientId = c.Id, AreaId = a1.Id, Name = "Sortie", Type = "image", FileName = "sortie.png", AddedUtc = DateTime.UtcNow };
            var used = new MediaItem { ClientId = c.Id, AreaId = a1.Id, Name = "Affiche", Type = "image", FileName = "affiche.png", AddedUtc = DateTime.UtcNow };
            db.Media.AddRange([ph, used]);
            var pl = new Playlist { ClientId = c.Id, AreaId = a1.Id, Name = "Hall d'entrée" };
            pl.Draft.Add(new PlaylistItem { MediaId = used.Id });
            db.Playlists.Add(pl);
            var sc = new Screen { ClientId = c.Id, AreaId = a2.Id, Name = "Borne", Token = "tok-" + Guid.NewGuid().ToString("N")[..6] };
            db.Screens.Add(sc);
            return (c, a1, a2, admin, ph, used, pl, sc);
        });
    }

    // Une session ouverte sur une aire donnée.
    ClientDb Open(Db db, Area area)
    {
        var u = db.Users.First(x => x.Id == camille.Id);
        u.LastAreas[client.Id] = area.Id;
        return new ClientDb(db, client.Id, AreaAccess.For(db, client.Id, camille.Id));
    }

    [Fact]
    public void A_media_shared_with_another_area_is_visible_there_and_editable_but_not_deletable()
    {
        store.Write(db => AreaShareOps.SetTargets(Open(db, siege), ShareKind.Media, [photo.Id], [magasin.Id], false));

        Assert.True(store.Read(db =>
        {
            var d = Open(db, magasin);
            Assert.Contains(d.Media, m => m.Id == photo.Id);                 // visible dans l'aire qui reçoit
            Assert.True(FolderOps.CanEditFile(d, photo));                    // et modifiable
            Assert.Equal(1, db.Media.Count(m => m.ClientId == client.Id && m.Name == "Sortie"));        // aucune copie
            return true;
        }));
        // l'autre aire ne le supprime pas
        store.Write(db => { var d = Open(db, magasin); d.Media.RemoveAll(m => m.Id == photo.Id); });
        Assert.Contains(store.Read(db => db.Media.ToList()), m => m.Id == photo.Id);
    }

    [Fact]
    public void Sharing_a_list_makes_its_media_readable_without_putting_them_at_the_root_and_the_list_can_be_edited()
    {
        store.Write(db => AreaShareOps.SetTargets(Open(db, siege), ShareKind.Playlist, [hall.Id], [magasin.Id], false));

        Assert.True(store.Read(db =>
        {
            var d = Open(db, magasin);
            Assert.Contains(d.Playlists, x => x.Id == hall.Id);
            Assert.Contains(d.Media, m => m.Id == usedByList.Id);            // lisible pour être diffusé
            Assert.Equal(FolderRight.Write, PlaylistOps.RightOf(d, hall));   // modifiable
            Assert.DoesNotContain(usedByList.Id, d.Access.Shared.Media);     // pas à la racine de la médiathèque
            return true;
        }));
    }

    [Fact]
    public void Removing_a_share_detaches_the_list_from_the_screens_of_the_other_area_which_keep_their_published_content()
    {
        store.Write(db => AreaShareOps.SetTargets(Open(db, siege), ShareKind.Playlist, [hall.Id], [magasin.Id], false));
        store.Write(db => { var s = db.Screens.First(x => x.Id == borne.Id); s.PlaylistId = hall.Id; s.Published = [new PublishedItem { Id = Guid.NewGuid(), Type = "image", MediaId = usedByList.Id }]; s.PublishedVersion = 1; });

        Assert.Null(store.Write(db => AreaShareOps.Unshare(Open(db, siege), ShareKind.Playlist, hall.Id, magasin.Id)));

        Assert.True(store.Read(db =>
        {
            var s = db.Screens.First(x => x.Id == borne.Id);
            Assert.Null(s.PlaylistId);                                       // plus choisie
            Assert.Single(s.Published);                                      // le contenu publié reste
            Assert.DoesNotContain(Open(db, magasin).Playlists, x => x.Id == hall.Id);
            return true;
        }));
    }

    [Fact]
    public void A_shared_folder_gives_its_content_and_subfolders_and_is_not_renamed_by_the_other_area()
    {
        var (folder, sub, inside) = store.Write(db =>
        {
            var f = new MediaFolder { ClientId = client.Id, AreaId = siege.Id, Name = "Campagnes" };
            var s = new MediaFolder { ClientId = client.Id, AreaId = siege.Id, Name = "2026", ParentId = f.Id };
            db.MediaFolders.AddRange([f, s]);
            var m = new MediaItem { ClientId = client.Id, AreaId = siege.Id, Name = "Dans le dossier", Type = "image", FileName = "d.png", FolderId = s.Id };
            db.Media.Add(m);
            return (f, s, m);
        });
        store.Write(db => AreaShareOps.SetTargets(Open(db, siege), ShareKind.MediaFolder, [folder.Id], [magasin.Id], false));

        store.Write(db =>
        {
            var d = Open(db, magasin);
            Assert.Contains(d.MediaFolders, f => f.Id == sub.Id);            // sous-dossier compris
            Assert.Contains(d.Media, m => m.Id == inside.Id);
            Assert.Equal(FolderRight.Write, FolderOps.RightOf(d, folder));
            Assert.NotNull(FolderOps.Rename(d, folder.Id, "Autre nom"));     // renommer reste à l'aire d'origine
        });
    }

    [Fact]
    public void Only_the_owning_area_shares_and_only_with_other_areas()
    {
        store.Write(db => AreaShareOps.SetTargets(Open(db, siege), ShareKind.Media, [photo.Id], [magasin.Id], false));
        Assert.False(store.Read(db => AreaShareOps.CanShare(Open(db, magasin), ShareKind.Media, photo.Id)));   // un média reçu ne se repartage pas
        var err = store.Write(db => AreaShareOps.SetTargets(Open(db, siege), ShareKind.Media, [photo.Id], [siege.Id], false));
        Assert.NotNull(err);                                                                                    // pas vers sa propre aire
    }

    [Fact]
    public void The_receiving_area_can_stop_seeing_a_shared_item_and_deleting_the_item_removes_its_shares()
    {
        store.Write(db => AreaShareOps.SetTargets(Open(db, siege), ShareKind.Media, [photo.Id], [magasin.Id], false));
        Assert.Null(store.Write(db => AreaShareOps.Leave(Open(db, magasin), ShareKind.Media, photo.Id)));
        Assert.DoesNotContain(store.Read(db => Open(db, magasin).Media.ToList()), m => m.Id == photo.Id);
        Assert.NotNull(store.Write(db => AreaShareOps.Leave(Open(db, magasin), ShareKind.Media, photo.Id)));   // plus rien à quitter

        store.Write(db => AreaShareOps.SetTargets(Open(db, siege), ShareKind.Media, [photo.Id], [magasin.Id], false));
        store.Write(db => AreaShareOps.Forget(Open(db, siege), ShareKind.Media, [photo.Id]));
        Assert.False(store.Read(db => db.AreaShares.Any(s => s.ItemId == photo.Id)));
    }
}
