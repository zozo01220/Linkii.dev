using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>Dimensions (en pixels) des fichiers de la médiathèque, lues dans leur en-tête sans outil externe.</summary>
public static class MediaProbe
{
    public static (int W, int H)? Size(string path)
    {
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".mp4" or ".m4v" => Mp4Probe.Size(path),
                ".png" => Png(path),
                ".gif" => Gif(path),
                ".jpg" or ".jpeg" => Jpeg(path),
                ".webp" => Webp(path),
                ".svg" => Svg(path),
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>« 1080p » pour une vidéo (côté court, « 4K » à partir de 2160), « 1920×1080 » pour une image.</summary>
    public static string? Label(MediaItem m)
    {
        if (m.Width is not > 0 || m.Height is not > 0) return null;
        if (m.Type != "video") return $"{m.Width}×{m.Height}";
        var p = Math.Min(m.Width.Value, m.Height.Value);
        return p >= 2160 ? "4K" : p + "p";
    }

    private static byte[] Head(string path, int n)
    {
        using var fs = File.OpenRead(path);
        var b = new byte[Math.Min(n, fs.Length)];
        fs.ReadExactly(b);
        return b;
    }

    private static (int, int)? Png(string path)
    {
        var b = Head(path, 24);
        return b.Length == 24 && b[1] == 'P' ? (BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20))) : null;
    }

    private static (int, int)? Gif(string path)
    {
        var b = Head(path, 10);
        return b.Length == 10 && b[0] == 'G' ? (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6)), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8))) : null;
    }

    private static (int, int)? Jpeg(string path)
    {
        using var fs = File.OpenRead(path);
        var b = new byte[9];
        var exif = 1;
        if (fs.ReadByte() != 0xFF || fs.ReadByte() != 0xD8) return null;
        while (true)
        {
            int c;
            do { c = fs.ReadByte(); if (c < 0) return null; } while (c != 0xFF);
            do { c = fs.ReadByte(); } while (c == 0xFF);
            if (c < 0) return null;
            if (c is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) continue;   // marqueurs sans contenu
            if (fs.Read(b, 0, 2) < 2) return null;
            var len = BinaryPrimitives.ReadUInt16BigEndian(b);
            if (c == 0xE1 && len > 16)   // APP1 : EXIF, dont l'orientation de la photo
            {
                var ex = new byte[len - 2];
                if (fs.Read(ex, 0, ex.Length) < ex.Length) return null;
                exif = ExifOrientation(ex);
                continue;
            }
            if (c is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)   // SOFn : précision, hauteur puis largeur
            {
                if (fs.Read(b, 0, 5) < 5) return null;
                int w = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(3)), h = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(1));
                return exif >= 5 ? (h, w) : (w, h);   // orientations 5 à 8 : la photo est affichée tournée d'un quart de tour
            }
            fs.Seek(len - 2, SeekOrigin.Current);
        }
    }

    /// <summary>Étiquette d'orientation (1 à 8) d'un bloc APP1 « Exif » ; 1 si absente.</summary>
    private static int ExifOrientation(byte[] a)
    {
        if (a.Length < 14 || a[0] != 'E' || a[1] != 'x' || a[2] != 'i' || a[3] != 'f') return 1;
        var le = a[6] == 'I';
        ushort U16(int o) => o + 2 > a.Length ? (ushort)0 : le ? BinaryPrimitives.ReadUInt16LittleEndian(a.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(o));
        uint U32(int o) => o + 4 > a.Length ? 0 : le ? BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(a.AsSpan(o));
        var ifd = 6 + (int)U32(10);
        if (ifd < 6 || ifd + 2 > a.Length) return 1;
        int n = U16(ifd);
        for (var i = 0; i < n; i++)
        {
            var e = ifd + 2 + i * 12;
            if (e + 12 > a.Length) break;
            if (U16(e) == 0x0112) { var v = U16(e + 8); return v is >= 1 and <= 8 ? v : 1; }
        }
        return 1;
    }

    private static (int, int)? Webp(string path)
    {
        var b = Head(path, 32);
        if (b.Length < 30 || b[8] != 'W') return null;
        switch ((char)b[15])
        {
            case ' ':   // VP8
                return (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(26)) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28)) & 0x3FFF);
            case 'L':   // VP8L
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(21));
                return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            case 'X':   // VP8X
                return (1 + (b[24] | b[25] << 8 | b[26] << 16), 1 + (b[27] | b[28] << 8 | b[29] << 16));
            default: return null;
        }
    }

    // SVG : attributs width/height de l'élément racine (en px), sinon viewBox
    private static (int, int)? Svg(string path)
    {
        var text = System.Text.Encoding.UTF8.GetString(Head(path, 4096));
        var tag = Regex.Match(text, @"<svg\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!tag.Success) return null;
        static double? Num(string tag, string attr)
        {
            var m = Regex.Match(tag, attr + @"\s*=\s*[""']\s*([0-9.]+)\s*(px)?\s*[""']", RegexOptions.IgnoreCase);
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        if (Num(tag.Value, "width") is { } w && Num(tag.Value, "height") is { } h && w > 0 && h > 0) return ((int)Math.Round(w), (int)Math.Round(h));
        var vb = Regex.Match(tag.Value, @"viewBox\s*=\s*[""']\s*[-0-9.]+[\s,]+[-0-9.]+[\s,]+([0-9.]+)[\s,]+([0-9.]+)", RegexOptions.IgnoreCase);
        if (vb.Success && double.TryParse(vb.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var vw)
            && double.TryParse(vb.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var vh) && vw > 0 && vh > 0)
            return ((int)Math.Round(vw), (int)Math.Round(vh));
        return null;
    }
}

/// <summary>Durée des vidéos de la médiathèque (MP4 : toutes y sont converties), lue une fois dans le fichier puis gardée en mémoire.</summary>
public static class VideoLength
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Cache = new();

    public static int? Seconds(MediaItem f)
    {
        if (Helpers.VideoSeconds(f) is { } known) return known;
        if (f.Type != "video" || f.FileName is not { } name || !name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return null;
        if (Cache.TryGetValue(name, out var s)) return s;
        if (Mp4Duration.Seconds(f.FilePath!) is not { } d) return null;
        Cache[name] = d;
        return d;
    }
}
