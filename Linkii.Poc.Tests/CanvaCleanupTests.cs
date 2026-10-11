using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

public class CanvaCleanupTests
{
    private static MediaItem Export(string name) => new() { Name = name, Type = "image", FileName = name + ".png", Info = new() { ["canva"] = "D1" } };

    [Fact]
    public void Only_exports_used_nowhere_are_unused()
    {
        var free = Export("libre");
        var inContent = Export("contenu");
        var inDraft = Export("brouillon");
        var onScreen = Export("ecran");
        var upload = new MediaItem { Name = "perso", Type = "image", FileName = "perso.png" };
        var instance = new MediaItem { Type = "app", AppId = "canva", Settings = new() { ["pages"] = inContent.Id.ToString() } };
        var playlist = new Playlist { Draft = { new PlaylistItem { MediaId = inDraft.Id } } };
        var screen = new Screen { Published = { new PublishedItem { Urls = new() { "/media/" + onScreen.FileName + "?t=1" } } } };

        var unused = MediaLibrary.UnusedCanva(new[] { free, inContent, inDraft, onScreen, upload, instance }, new[] { playlist }, new[] { screen });

        Assert.Equal(new[] { free.Id }, unused.Select(m => m.Id));
    }

    [Fact]
    public void An_export_being_converted_is_never_unused()
    {
        var pending = Export("video"); pending.Status = "processing";
        Assert.Empty(MediaLibrary.UnusedCanva(new[] { pending }, Array.Empty<Playlist>(), Array.Empty<Screen>()));
    }
}
