using SkiaSharp;

namespace Linkii.Poc;

/// <summary>
/// Orientation des photos : décodage « à l'endroit » (Skia ignore l'étiquette EXIF, que les navigateurs appliquent) et rotation d'un quart de tour
/// enregistrée dans le fichier (l'étiquette EXIF disparaît : le fichier est réécrit dans son nouveau sens).
/// </summary>
public static class ImageOrient
{
    /// <summary>Formats réécrits sans perte de sens : les GIF (animés) et BMP gardent leur fichier.</summary>
    public static bool CanRotate(string file) => Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp";

    /// <summary>Image décodée et remise à l'endroit selon l'étiquette EXIF ; null si Skia ne sait pas la lire.</summary>
    public static SKBitmap? Decode(string path)
    {
        using var codec = SKCodec.Create(path);
        if (codec == null) return null;
        var bmp = SKBitmap.Decode(codec);
        if (bmp == null) return null;
        var up = Apply(bmp, codec.EncodedOrigin);
        if (!ReferenceEquals(up, bmp)) bmp.Dispose();
        return up;
    }

    /// <summary>Tourne le fichier d'un quart de tour et le réécrit (même nom). Renvoie les nouvelles dimensions, ou null si impossible.</summary>
    public static (int W, int H)? Rotate(string path, bool right)
    {
        if (!CanRotate(path)) return null;
        using var up = Decode(path);
        if (up == null) return null;
        using var turned = Apply(up, right ? SKEncodedOrigin.RightTop : SKEncodedOrigin.LeftBottom);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var (fmt, q) = ext switch { ".png" => (SKEncodedImageFormat.Png, 100), ".webp" => (SKEncodedImageFormat.Webp, 92), _ => (SKEncodedImageFormat.Jpeg, 93) };
        using var data = SKImage.FromBitmap(turned).Encode(fmt, q);
        if (data == null) return null;
        var tmp = path + ".rot" + ext;
        try
        {
            using (var fs = File.Create(tmp)) data.SaveTo(fs);
            File.Move(tmp, path, true);
        }
        catch { try { File.Delete(tmp); } catch { } return null; }
        return (turned.Width, turned.Height);
    }

    private static SKBitmap Apply(SKBitmap src, SKEncodedOrigin o)
    {
        if (o is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return src;
        int w = src.Width, h = src.Height;
        var swap = o >= SKEncodedOrigin.LeftTop;
        var m = o switch
        {
            SKEncodedOrigin.TopRight => Mx(-1, 0, w, 0, 1, 0),        // miroir horizontal
            SKEncodedOrigin.BottomRight => Mx(-1, 0, w, 0, -1, h),    // 180°
            SKEncodedOrigin.BottomLeft => Mx(1, 0, 0, 0, -1, h),      // miroir vertical
            SKEncodedOrigin.LeftTop => Mx(0, 1, 0, 1, 0, 0),          // transposée
            SKEncodedOrigin.RightTop => Mx(0, -1, h, 1, 0, 0),        // 90° vers la droite
            SKEncodedOrigin.RightBottom => Mx(0, -1, h, -1, 0, w),
            SKEncodedOrigin.LeftBottom => Mx(0, 1, 0, -1, 0, w),      // 90° vers la gauche
            _ => SKMatrix.Identity,
        };
        var dst = new SKBitmap(new SKImageInfo(swap ? h : w, swap ? w : h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(m);
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }

    private static SKMatrix Mx(float sx, float kx, float tx, float ky, float sy, float ty) =>
        new() { ScaleX = sx, SkewX = kx, TransX = tx, SkewY = ky, ScaleY = sy, TransY = ty, Persp0 = 0, Persp1 = 0, Persp2 = 1 };
}
