using SkiaSharp;
using Xunit;

namespace Linkii.Poc.Tests;

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
        WriteImage(Path.Combine(AppPaths.MediaDir, "a.jpg"), 1600, 900);
        var t = Thumbnails.Get("a.jpg", 320);
        Assert.NotNull(t);
        using var bmp = SKBitmap.Decode(t);
        Assert.Equal(320, bmp.Width);
        Assert.Equal(180, bmp.Height);
        Assert.Equal(t, Thumbnails.Get("a.jpg", 320));
        Thumbnails.Delete("a.jpg");
        Assert.False(File.Exists(t));
    }

    [Fact]
    public void Small_image_and_unknown_formats_keep_the_original()
    {
        WriteImage(Path.Combine(AppPaths.MediaDir, "s.jpg"), 100, 100);
        Assert.Null(Thumbnails.Get("s.jpg", 320));
        File.WriteAllText(Path.Combine(AppPaths.MediaDir, "v.svg"), "<svg/>");
        Assert.Null(Thumbnails.Get("v.svg", 320));
    }

    [Fact]
    public void Width_snaps_to_allowed_sizes() => Assert.Equal([160, 320, 640, 640], new[] { Thumbnails.Snap(100), Thumbnails.Snap(300), Thumbnails.Snap(640), Thumbnails.Snap(5000) });
}
