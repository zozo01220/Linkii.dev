using Linkii.Poc;
using SkiaSharp;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Pivoter une image : le fichier est réécrit dans son nouveau sens, les dimensions s'échangent.</summary>
public class ImageRotateTests
{
    private static string Sample(string ext, SKEncodedImageFormat fmt)
    {
        var path = Path.Combine(Path.GetTempPath(), "rot-" + Guid.NewGuid().ToString("N") + ext);
        using var bmp = new SKBitmap(40, 20);
        using (var c = new SKCanvas(bmp)) { c.Clear(SKColors.White); using var p = new SKPaint { Color = SKColors.Red }; c.DrawRect(0, 0, 10, 10, p); }   // repère en haut à gauche
        using var data = SKImage.FromBitmap(bmp).Encode(fmt, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Theory]
    [InlineData(".jpg", true)]
    [InlineData(".png", true)]
    [InlineData(".png", false)]
    [InlineData(".webp", true)]
    public void Rotation_echange_les_dimensions_et_deplace_le_repere(string ext, bool right)
    {
        var path = Sample(ext, ext == ".jpg" ? SKEncodedImageFormat.Jpeg : ext == ".png" ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Webp);
        try
        {
            Assert.Equal((20, 40), ImageOrient.Rotate(path, right));
            Assert.Equal((20, 40), MediaProbe.Size(path));
            using var bmp = SKBitmap.Decode(path);
            Assert.Equal((20, 40), (bmp.Width, bmp.Height));
            // à droite : le coin haut-gauche passe en haut à droite ; à gauche : en bas à gauche
            var spot = right ? bmp.GetPixel(15, 5) : bmp.GetPixel(5, 35);
            Assert.True(spot.Red > 200 && spot.Green < 100, $"repère introuvable : {spot}");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Un_gif_ne_se_pivote_pas() => Assert.False(ImageOrient.CanRotate("a.gif"));
}
