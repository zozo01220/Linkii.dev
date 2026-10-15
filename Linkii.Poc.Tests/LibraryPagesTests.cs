using Linkii.Poc;
using Xunit;
namespace Linkii.Poc.Tests;

/// <summary>
/// Listes de lecture et médiathèque épurées (§51) : état de diffusion d'une liste (choisie, à publier, programmée), ce qu'une suppression touche,
/// duplication, et médias sans emploi.
/// </summary>
public class LibraryPagesTests
{
    readonly Db db = new();
    readonly Tenant client = new() { Name = "Horizon SA" };
    readonly Area siege;
    readonly Zone rez;

    public LibraryPagesTests()
    {
        db.Clients.Add(client);
        siege = new Area { ClientId = client.Id, Name = "Siège" };
        rez = AreaOps.EnsureDefaultZone(siege);
        rez.Name = "Rez";
        db.Areas.Add(siege);
    }

    Playlist List(string name)
    {
        var p = new Playlist { ClientId = client.Id, AreaId = siege.Id, Name = name, Revision = 3 };
        db.Playlists.Add(p);
        return p;
    }

    Screen Screen(string name, Playlist? p = null)
    {
        var s = new Screen { ClientId = client.Id, AreaId = siege.Id, Name = name, Token = "t-" + name, PlaylistId = p?.Id };
        db.Screens.Add(s);
        return s;
    }

    void Publish(Screen s) => s.PublishedStamp = Helpers.PublishStamp(s, db.Playlists);

    Dictionary<Guid, PlaylistUse> Uses() => PlaylistUses.Of(db.Playlists, db.Screens, db.Areas);

    // ---------- État de diffusion ----------

    [Fact]
    public void A_list_chosen_and_published_is_broadcast_without_pending_changes()
    {
        var p = List("Accueil");
        var s = Screen("Hall", p);
        Publish(s);
        var u = Uses()[p.Id];
        Assert.Equal(1, u.Screens);
        Assert.Equal(0, u.Pending);
        Assert.True(u.Used);
    }

    [Fact]
    public void A_list_modified_after_publication_is_pending_on_its_screens()
    {
        var p = List("Accueil");
        var a = Screen("Hall", p);
        var b = Screen("Accueil 1", p);
        Publish(a); Publish(b);
        p.Touch();
        Assert.Equal(2, Uses()[p.Id].Pending);
        Publish(a);
        Assert.Equal(1, Uses()[p.Id].Pending);
    }

    [Fact]
    public void A_chosen_list_never_published_is_pending()
    {
        var p = List("Accueil");
        Screen("Hall", p);
        Assert.Equal(1, Uses()[p.Id].Pending);
    }

    [Fact]
    public void Revision_numbers_are_compared_whole_not_as_prefixes()
    {
        var p = List("Accueil");
        var s = Screen("Hall", p);
        p.Revision = 12;
        Publish(s);
        p.Revision = 1;
        Assert.False(PlaylistUses.PublishedWith(s, p));
        p.Revision = 12;
        Assert.True(PlaylistUses.PublishedWith(s, p));
    }

    [Fact]
    public void A_list_in_the_second_zone_of_a_split_screen_counts_and_is_checked_on_its_own_revision()
    {
        var main = List("Principale");
        var side = List("Bandeau");
        var s = Screen("Hall", main);
        s.Layout = "50-50";
        s.ZonePlaylistIds = new() { side.Id };
        Publish(s);
        Assert.Equal(1, Uses()[side.Id].Screens);
        Assert.Equal(0, Uses()[side.Id].Pending);
        side.Touch();
        Assert.Equal(1, Uses()[side.Id].Pending);
        Assert.Equal(0, Uses()[main.Id].Pending);
    }

    [Fact]
    public void A_list_only_programmed_in_a_planning_is_used_and_says_when()
    {
        var p = List("Midi");
        rez.Slots.Add(new Slot { PlaylistId = p.Id, Days = 31, From = 11 * 60, To = 14 * 60 });
        var u = Uses()[p.Id];
        Assert.Equal(0, u.Screens);
        Assert.Equal(1, u.Slots);
        Assert.True(u.Used);
        Assert.Equal("Lun – Ven · 11:00 – 14:00", u.FirstSlot);
    }

    [Fact]
    public void A_planning_list_changed_since_its_snapshot_is_pending_on_that_screen()
    {
        var p = List("Midi");
        var s = Screen("Hall");
        s.PublishedListRevs[p.Id] = p.Revision;
        Assert.Equal(0, Uses()[p.Id].Pending);
        p.Touch();
        Assert.Equal(1, Uses()[p.Id].Pending);
    }

    [Fact]
    public void A_list_neither_chosen_nor_programmed_is_unused()
    {
        var p = List("Archives");
        Screen("Hall");
        Assert.False(Uses()[p.Id].Used);
    }

    // ---------- Suppression ----------

    [Fact]
    public void The_delete_impact_names_screens_and_the_planning_slots_that_will_be_removed()
    {
        var p = List("Midi");
        Screen("Hall", p);
        var s = Screen("Accueil");
        rez.Slots.Add(new Slot { PlaylistId = p.Id, Days = 127, From = 8 * 60, To = 12 * 60 });
        s.Slots.Add(new Slot { PlaylistId = p.Id, Days = 32, From = 9 * 60, To = 10 * 60 });

        var impact = PlaylistUses.Impact(new[] { p.Id }, db.Screens, db.Areas);
        Assert.Equal(new[] { "Hall" }, impact.Screens);
        Assert.Equal(2, impact.Slots.Count);
        Assert.Contains("zone « Rez » · Tous les jours · 08:00 – 12:00", impact.Slots);
        Assert.Contains("écran « Accueil » · Sam · 09:00 – 10:00", impact.Slots);

        // et la suppression retire bien ces créneaux
        PlaylistOps.Remove(new ClientDb(db, client.Id), new[] { p.Id });
        Assert.Empty(rez.Slots);
        Assert.Empty(s.Slots);
    }

    // ---------- Duplication ----------

    [Fact]
    public void A_copy_gets_a_free_name_its_own_qr_code_and_stays_in_its_folder()
    {
        var folder = new PlaylistFolder { ClientId = client.Id, AreaId = siege.Id, Name = "Événements" };
        db.PlaylistFolders.Add(folder);
        var p = List("Promo");
        p.FolderId = folder.Id;
        p.Draft.Add(new PlaylistItem { MediaId = Guid.NewGuid(), QrOn = true, QrCode = QrTracking.NewCode(), QrUrl = "https://horizon.ch" });
        var d = new ClientDb(db, client.Id);

        var first = PlaylistOps.Copy(d, p);
        var second = PlaylistOps.Copy(d, p);

        Assert.Equal("Promo (copie)", first.Name);
        Assert.Equal("Promo (copie 2)", second.Name);
        Assert.Equal(folder.Id, first.FolderId);
        Assert.NotEqual(p.Draft[0].Id, first.Draft[0].Id);
        Assert.NotEqual(p.Draft[0].QrCode, first.Draft[0].QrCode);
        Assert.Equal(p.Draft[0].QrUrl, first.Draft[0].QrUrl);
    }

    // ---------- Médias sans emploi ----------

    [Fact]
    public void Unused_files_are_those_in_no_list_no_content_and_on_no_screen()
    {
        MediaItem File(string name, string? status = null)
        {
            var m = new MediaItem { ClientId = client.Id, AreaId = siege.Id, Name = name, Type = "image", FileName = name, Status = status };
            db.Media.Add(m);
            return m;
        }
        var inList = File("liste.jpg");
        var inContent = File("contenu.jpg");
        var onScreen = File("ecran.jpg");
        var alone = File("seul.jpg");
        var converting = File("film.mp4", "processing");

        var list = List("Accueil");
        list.Draft.Add(new PlaylistItem { MediaId = inList.Id });
        db.Media.Add(new MediaItem { ClientId = client.Id, AreaId = siege.Id, Type = "app", AppId = "slideshow-images", Name = "Diaporama", Settings = { ["medias"] = inContent.Id.ToString() } });
        var s = Screen("Hall");
        s.Published.Add(new PublishedItem { Url = "/media/ecran.jpg" });

        var unused = MediaLibrary.UnusedExports(db.Media, db.Playlists, db.Screens, null).Select(m => m.Id).ToHashSet();
        Assert.Contains(alone.Id, unused);
        Assert.DoesNotContain(inList.Id, unused);
        Assert.DoesNotContain(inContent.Id, unused);
        Assert.DoesNotContain(onScreen.Id, unused);
        Assert.DoesNotContain(converting.Id, unused);
    }
}
