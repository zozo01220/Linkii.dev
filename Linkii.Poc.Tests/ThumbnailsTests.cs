using SkiaSharp;
using Xunit;

namespace Linkii.Poc.Tests;

[Collection("platform")]   // AppPaths est statique : sérialisé avec les autres tests qui s'en servent
public class ThumbnailsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "linkii-thumbs-" + Guid.NewGuid().ToString("N"));

    public ThumbnailsTests()
    {
        AppPaths.MediaDir = Path.Combine(dir, "media");
        AppPaths.ThumbDir = Path.Combine(dir, "thumbs");
        Directory.CreateDirectory(AppPaths.MediaDir);
        Directory.CreateDirectory(AppPaths.ThumbDir);
    }

    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    private static void WriteImage(string path, int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 90);
        File.WriteAllBytes(path, data.ToArray());
    }

    [Fact]
    public void Big_image_gets_a_smaller_cached_thumbnail()
    {
        var a = new MediaItem { ClientId = Guid.NewGuid(), OwnerId = Guid.NewGuid(), FileName = "a.jpg" };
        a.Dir = MediaFiles.Dir(a.ClientId, a.OwnerId);
        WriteImage(a.FilePath!, 1600, 900);
        var t = Thumbnails.Get(a, 320);
        Assert.NotNull(t);
        Assert.Equal(Path.GetFullPath(Path.Combine(AppPaths.ThumbDir, a.Dir)), Path.GetFullPath(Path.GetDirectoryName(t)!));   // rangée comme le média
        using var bmp = SKBitmap.Decode(t);
        Assert.Equal(320, bmp.Width);
        Assert.Equal(180, bmp.Height);
        Assert.Equal(t, Thumbnails.Get(a, 320));
        Thumbnails.Delete(a);
        Assert.False(File.Exists(t));
    }

    [Fact]
    public void Small_image_and_unknown_formats_keep_the_original()
    {
        WriteImage(Path.Combine(AppPaths.MediaDir, "s.jpg"), 100, 100);
        Assert.Null(Thumbnails.Get(new MediaItem { FileName = "s.jpg" }, 320));
        File.WriteAllText(Path.Combine(AppPaths.MediaDir, "v.svg"), "<svg/>");
        Assert.Null(Thumbnails.Get(new MediaItem { FileName = "v.svg" }, 320));
    }

    [Fact]
    public void Files_from_before_are_moved_to_their_organisation_and_member()
    {
        AppPaths.PdfDir = Path.Combine(dir, "pdf"); AppPaths.CaptureDir = Path.Combine(dir, "captures"); Ads.Dir = Path.Combine(dir, "ads");
        var db = new Db();
        var (cid, uid) = (Guid.NewGuid(), Guid.NewGuid());
        var mine = new MediaItem { ClientId = cid, OwnerId = uid, FileName = "m.jpg" };
        var drive = new MediaItem { ClientId = cid, FileName = "d.jpg" };
        db.Media.AddRange([mine, drive]);
        File.WriteAllText(Path.Combine(AppPaths.MediaDir, "m.jpg"), "x");
        File.WriteAllText(Path.Combine(AppPaths.MediaDir, "d.jpg"), "x");
        File.WriteAllText(Path.Combine(AppPaths.ThumbDir, "m-320.webp"), "x");

        Assert.Equal(2, MediaFiles.Migrate(db));
        Assert.Equal($"{cid:N}/{uid:N}", mine.Dir);
        Assert.Equal($"{cid:N}/{MediaFiles.Shared}", drive.Dir);
        Assert.True(File.Exists(mine.FilePath) && File.Exists(drive.FilePath));
        Assert.False(File.Exists(Path.Combine(AppPaths.MediaDir, "m.jpg")));
        Assert.Empty(Directory.GetFiles(AppPaths.ThumbDir));   // miniatures à la racine : refaites à la demande
        Assert.Equal(0, MediaFiles.Migrate(db));               // déjà rangé : rien à faire
    }

    [Fact]
    public void Width_snaps_to_allowed_sizes() => Assert.Equal([160, 320, 640, 640], new[] { Thumbnails.Snap(100), Thumbnails.Snap(300), Thumbnails.Snap(640), Thumbnails.Snap(5000) });
}
