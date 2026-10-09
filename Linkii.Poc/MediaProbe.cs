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
            if (c is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)   // SOFn : précision, hauteur puis largeur
            {
                if (fs.Read(b, 0, 5) < 5) return null;
                return (BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(3)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(1)));
            }
            fs.Seek(len - 2, SeekOrigin.Current);
        }
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
