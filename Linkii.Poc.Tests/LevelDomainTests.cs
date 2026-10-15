using Linkii.Poc;
using Xunit;
namespace Linkii.Poc.Tests;

/// <summary>
/// Niveau de droits par domaine (§54) : accès Écrans / Listes de lecture / Médiathèque (aucun, voir, voir et modifier), visibilité des dossiers
/// propre à chaque domaine, ses propres dossiers par domaine, migration des anciens niveaux, et contenus masqués dans les listes.
/// </summary>
public class LevelDomainTests
{
    readonly Db db = new();
    readonly Tenant client = new() { Name = "Horizon SA", FoldersOn = true };
    readonly Area siege;
    readonly User admin, marc;
    readonly AccessLevel niveau;

    public LevelDomainTests()
    {
        db.Clients.Add(client);
        siege = new Area { ClientId = client.Id, Name = "Siège" };
        AreaOps.EnsureDefaultZone(siege);
        db.Areas.Add(siege);
        niveau = new AccessLevel
        {
            Name = "Gestionnaire", AllAreas = true,
            ScreensAccess = DomainAccess.Write, PlaylistsAccess = DomainAccess.Write, MediaAccess = DomainAccess.Read,
            PlaylistsVisibility = LevelVisibility.Own, MediaVisibility = LevelVisibility.All,
            PlaylistsCreateFolders = true, PlaylistsShareFolders = true, MediaCreateFolders = true, MediaShareFolders = true
        };
        niveau.Normalize();
        client.Levels.Add(niveau);
        admin = new User { Email = "camille@horizon.ch", Role = Roles.ClientAdmin, ClientId = client.Id };
        marc = new User { Email = "marc@horizon.ch", Role = Roles.ClientMember, ClientId = client.Id, LevelId = niveau.Id };
        db.Users.AddRange([admin, marc]);
    }

    ClientDb As(User u) => new(db, client.Id, AreaAccess.For(db, client.Id, u.Id));

    MediaFolder MediaFolder(string name, User owner)
    {
        var f = new MediaFolder { ClientId = client.Id, AreaId = siege.Id, Name = name, OwnerId = owner.Id };
        db.MediaFolders.Add(f);
        return f;
    }

    PlaylistFolder ListFolder(string name, User owner)
    {
        var f = new PlaylistFolder { ClientId = client.Id, AreaId = siege.Id, Name = name, OwnerId = owner.Id };
        db.PlaylistFolders.Add(f);
        return f;
    }

    // ---------- Accès par domaine ----------

    [Fact]
    public void Normalize_derives_the_pages_and_the_global_read_only_from_the_domains()
    {
        Assert.Equal(new[] { LevelPages.Screens, LevelPages.Playlists, LevelPages.Media }, niveau.Pages);
        Assert.False(niveau.ReadOnly);

        var l = new AccessLevel { ScreensAccess = DomainAccess.Read, PlaylistsAccess = DomainAccess.None, MediaAccess = DomainAccess.Read, Pages = [LevelPages.Planning], ManageZones = true };
        l.Normalize();
        Assert.Equal(new[] { LevelPages.Screens, LevelPages.Planning, LevelPages.Media }, l.Pages);
        Assert.True(l.ReadOnly);            // aucun domaine en écriture : la lecture seule globale s'applique
        Assert.False(l.ManageZones);        // gérer les zones demande les écrans en écriture
    }

    [Fact]
    public void An_old_level_keeps_exactly_its_rights_after_migration()
    {
        var old = new AccessLevel { Name = "Ancien", AllAreas = true, Pages = [LevelPages.Screens, LevelPages.Media], ReadOnly = true, AllFolders = "read", CanCreateFolders = true };
        Assert.True(old.IsLegacy);
        Assert.Equal(DomainAccess.Read, old.AccessTo(LevelPages.Media));       // lu avant même la migration
        Assert.Equal(DomainAccess.None, old.AccessTo(LevelPages.Playlists));

        old.Normalize();
        Assert.False(old.IsLegacy);
        Assert.Equal((DomainAccess.Read, DomainAccess.None, DomainAccess.Read), (old.ScreensAccess, old.PlaylistsAccess, old.MediaAccess));
        Assert.Equal(LevelVisibility.AllRead, old.MediaVisibility);
        Assert.Equal(LevelVisibility.AllRead, old.PlaylistsVisibility);
        Assert.True(old.MediaCreateFolders && old.PlaylistsCreateFolders);
        Assert.Null(old.AllFolders);
        Assert.True(old.ReadOnly);
    }

    [Fact]
    public void Migration_chooses_folders_only_for_the_domain_that_has_level_shares()
    {
        var old = new AccessLevel { Name = "Choisis", AllAreas = true, Pages = [.. LevelPages.Offered] };
        client.Levels.Add(old);
        MediaFolder("Affiches", admin).Shares.Add(new FolderShare { Kind = FolderShareKind.Level, TargetId = old.Id });
        AccessLevels.Migrate(db);
        Assert.Equal(LevelVisibility.Chosen, old.MediaVisibility);
        Assert.Equal(LevelVisibility.Own, old.PlaylistsVisibility);
        Assert.Equal(DomainAccess.Write, old.PlaylistsAccess);
    }

    [Fact]
    public void Saving_a_level_normalizes_it()
    {
        var l = new AccessLevel { Name = "Vitrine", AllAreas = true, ScreensAccess = DomainAccess.Write, PlaylistsAccess = DomainAccess.Read, MediaAccess = DomainAccess.None };
        Assert.Null(AccessLevels.Save(As(admin), l));
        Assert.Equal(new[] { LevelPages.Screens, LevelPages.Playlists }, l.Pages);
        Assert.Equal(LevelVisibility.Own, l.MediaVisibility);
    }

    // ---------- Droits par domaine ----------

    [Fact]
    public void Seeing_the_media_caps_even_the_own_folders_while_lists_stay_editable()
    {
        var mine = MediaFolder("Mes médias", marc);
        var lists = ListFolder("Mes listes", marc);
        var d = As(marc);
        Assert.Equal(FolderRight.Read, FolderOps.RightOf(d, mine));
        Assert.Equal(FolderRight.Owner, PlaylistFolderOps.RightOf(d, lists));
    }

    [Fact]
    public void Items_outside_folders_follow_the_access_of_their_domain()
    {
        var file = new MediaItem { ClientId = client.Id, AreaId = siege.Id, Name = "logo.png", Type = "image" };
        db.Media.Add(file);
        var list = new Playlist { ClientId = client.Id, AreaId = siege.Id, Name = "Accueil" };
        db.Playlists.Add(list);
        var d = As(marc);
        Assert.False(FolderOps.CanEditFile(d, file));
        Assert.False(FolderOps.CanUploadTo(d, null));
        Assert.Equal(FolderRight.Write, PlaylistOps.RightOf(d, list));

        niveau.MediaAccess = DomainAccess.Write;
        Assert.True(FolderOps.CanEditFile(As(marc), file));
    }

    [Fact]
    public void Visibility_is_set_per_domain()
    {
        var affiches = MediaFolder("Affiches", admin);
        var evenements = ListFolder("Événements", admin);
        var d = As(marc);
        Assert.Equal(FolderRight.Read, FolderOps.RightOf(d, affiches));                 // médiathèque : tous les dossiers, en « Voir »
        Assert.Equal(FolderRight.None, PlaylistFolderOps.RightOf(d, evenements));       // listes : hors dossier seulement

        niveau.PlaylistsVisibility = LevelVisibility.All;
        Assert.Equal(FolderRight.Write, PlaylistFolderOps.RightOf(As(marc), evenements));
        niveau.PlaylistsVisibility = LevelVisibility.AllRead;
        Assert.Equal(FolderRight.Read, PlaylistFolderOps.RightOf(As(marc), evenements));
    }

    [Fact]
    public void Own_folders_are_created_and_shared_per_domain()
    {
        var a = As(marc).Access;
        Assert.True(FolderAccess.CanCreateAtRoot(a, LevelPages.Playlists));
        Assert.False(FolderAccess.CanCreateAtRoot(a, LevelPages.Media));   // médiathèque en « Voir »

        niveau.PlaylistsCreateFolders = false;
        Assert.False(FolderAccess.CanCreateAtRoot(As(marc).Access, LevelPages.Playlists));
        Assert.True(FolderAccess.CanCreateAtRoot(As(admin).Access, LevelPages.Media));
    }

    [Fact]
    public void A_level_without_the_media_page_still_sees_media_by_visibility_but_only_reads()
    {
        niveau.MediaAccess = DomainAccess.None;
        niveau.Normalize();
        var affiches = MediaFolder("Affiches", admin);
        Assert.Equal(FolderRight.Read, FolderOps.RightOf(As(marc), affiches));   // utilisables dans ses contenus
        Assert.DoesNotContain(LevelPages.Media, niveau.Pages);
    }

    [Fact]
    public void The_audience_counts_writing_only_for_a_domain_in_writing()
    {
        var f = MediaFolder("Affiches", admin);
        f.Shares.Add(new FolderShare { Kind = FolderShareKind.User, TargetId = marc.Id, Write = true });
        var a = FolderAudiences.Compute(As(admin), FolderOps.All(As(admin)), new[] { f }, LevelPages.Media)[f.Id];
        Assert.False(Assert.Single(a.People).Write);
    }

    [Fact]
    public void A_domain_read_only_refusal_says_which_domain()
    {
        Assert.Contains("médiathèque", ReadOnlyLevelException.For(LevelPages.Media).Message);
        Assert.Contains("écrans", ReadOnlyLevelException.For(LevelPages.Screens).Message);
    }

    // ---------- Contenus masqués (option C) ----------

    [Fact]
    public void A_content_using_a_hidden_file_is_masked()
    {
        var hiddenFile = new MediaItem { Type = "image", Name = "secret.jpg" };
        var openFile = new MediaItem { Type = "image", Name = "public.jpg" };
        var slideshow = new MediaItem { Type = "app", AppId = "slideshow-images", Settings = { ["medias"] = $"{openFile.Id},{hiddenFile.Id}" } };
        var web = new MediaItem { Type = "app", AppId = "web", Settings = { ["url"] = "https://horizon.ch" } };
        var hidden = new HashSet<Guid> { hiddenFile.Id };

        Assert.True(MediaLibrary.UsesHidden(hiddenFile, hidden));
        Assert.True(MediaLibrary.UsesHidden(slideshow, hidden));
        Assert.False(MediaLibrary.UsesHidden(openFile, hidden));
        Assert.False(MediaLibrary.UsesHidden(web, hidden));
        Assert.False(MediaLibrary.UsesHidden(slideshow, new HashSet<Guid>()));
    }
}
