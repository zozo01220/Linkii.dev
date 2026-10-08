using Linkii.Poc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Linkii.Poc.Tests;

public class WidgetRemovalTests
{
    [Fact]
    public void Purge_removes_old_widgets_from_media_and_playlists_but_keeps_apps_and_files()
    {
        var client = Guid.NewGuid();
        var widget = new MediaItem { ClientId = client, Name = "ancienne salle", Type = "app" };               // widget : app sans AppId
        var app = new MediaItem { ClientId = client, Name = "salle", Type = "app", AppId = "room" };
        var image = new MediaItem { ClientId = client, Name = "logo", Type = "image", FileName = "logo.png" };
        var pl = new Playlist { ClientId = client, Name = "P", PublishedVersion = 3 };
        pl.Draft.AddRange(new[] { new PlaylistItem { MediaId = widget.Id }, new PlaylistItem { MediaId = app.Id }, new PlaylistItem { MediaId = image.Id } });
        pl.Published.AddRange(new[]
        {
            new PublishedItem { Id = Guid.NewGuid(), Type = "app" },                       // widget publié
            new PublishedItem { Id = Guid.NewGuid(), Type = "app", AppId = "room" },
            new PublishedItem { Id = Guid.NewGuid(), Type = "image", Url = "/media/logo.png" }
        });
        var db = new Db { Media = { widget, app, image }, Playlists = { pl } };

        Seed.PurgeWidgets(db, Path.Combine(Path.GetTempPath(), "inexistant-" + Guid.NewGuid() + ".json"), NullLogger.Instance);

        Assert.Equal(new[] { app.Id, image.Id }, db.Media.Select(m => m.Id));
        Assert.Equal(new[] { app.Id, image.Id }, pl.Draft.Select(i => i.MediaId));
        Assert.True(pl.DraftChanged);
        Assert.Equal(2, pl.Published.Count);
        Assert.All(pl.Published, x => Assert.True(x.Type != "app" || x.AppId != null));
        Assert.Equal(4, pl.PublishedVersion);   // les écrans relisent la playlist
    }

    [Fact]
    public void Purge_does_nothing_when_there_is_no_widget()
    {
        var pl = new Playlist { PublishedVersion = 7 };
        pl.Published.Add(new PublishedItem { Id = Guid.NewGuid(), Type = "app", AppId = "clock" });
        var db = new Db { Media = { new MediaItem { Type = "app", AppId = "clock" } }, Playlists = { pl } };

        Seed.PurgeWidgets(db, "inexistant.json", NullLogger.Instance);

        Assert.Single(db.Media);
        Assert.Single(pl.Published);
        Assert.Equal(7, pl.PublishedVersion);
        Assert.False(pl.DraftChanged);
    }
}
