using SkiaSharp;

namespace Linkii.Poc;

/// <summary>
/// Miniatures des images de la médiathèque : une copie réduite (WebP) créée à la première demande et gardée sur le disque,
/// pour ne pas télécharger l'original dans les listes. Les formats que Skia ne décode pas (SVG…) gardent leur original.
/// </summary>
public static class Thumbnails
{
    public static readonly int[] Widths = [160, 320, 640];
    private static readonly HashSet<string> Decodable = [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"];
    private static readonly object Gate = new();

    /// <summary>Largeur autorisée la plus proche (par excès) : évite qu'un client génère des tailles à volonté.</summary>
    public static int Snap(int w) => Widths.FirstOrDefault(x => x >= w, Widths[^1]);

    /// <summary>Chemin de la miniature (créée si besoin), ou null si le fichier ne s'y prête pas (l'original est alors servi).</summary>
    public static string? Get(string file, int width)
    {
        if (!Decodable.Contains(Path.GetExtension(file).ToLowerInvariant())) return null;
        var source = Path.Combine(AppPaths.MediaDir, file);
        var thumb = Path.Combine(AppPaths.ThumbDir, $"{Path.GetFileNameWithoutExtension(file)}-{width}.webp");
        try
        {
            if (File.Exists(thumb) && File.GetLastWriteTimeUtc(thumb) >= File.GetLastWriteTimeUtc(source)) return thumb;
            lock (Gate)
            {
                if (File.Exists(thumb)) return thumb;
                using var bmp = SKBitmap.Decode(source);
                if (bmp == null) return null;
                if (bmp.Width <= width) return null;   // déjà plus petite que la miniature : l'original suffit
                var h = Math.Max(1, (int)Math.Round(bmp.Height * (double)width / bmp.Width));
                using var small = bmp.Resize(new SKImageInfo(width, h), SKFilterQuality.Medium);
                if (small == null) return null;
                using var data = SKImage.FromBitmap(small).Encode(SKEncodedImageFormat.Webp, 75);
                var tmp = thumb + ".tmp";
                using (var fs = File.Create(tmp)) data.SaveTo(fs);
                File.Move(tmp, thumb, true);
                return thumb;
            }
        }
        catch { return null; }
    }

    /// <summary>Supprime les miniatures d'un fichier (lors de sa suppression).</summary>
    public static void Delete(string file)
    {
        try
        {
            foreach (var t in Directory.EnumerateFiles(AppPaths.ThumbDir, Path.GetFileNameWithoutExtension(file) + "-*.webp")) File.Delete(t);
        }
        catch { }
    }
}
