using System.Diagnostics;
using System.Text;

namespace Linkii.Poc;

/// <summary>
/// Convertit n'importe quelle vidéo (MKV, AVI, MOV, WMV, FLV, MPEG, WebM, 3GP, HEVC…) en MP4 H.264 sans son, 1080p max (les MP4 plus grands sont aussi réduits) :
/// le seul format que tous les navigateurs et toutes les clés HDMI lisent, et qui se met en cache pour la lecture hors ligne.
/// Outils gratuits utilisés côté serveur : ffmpeg (préféré) ou VLC en ligne de commande.
/// Un « plugin » VLC dans le navigateur n'existe plus (NPAPI supprimé de tous les navigateurs depuis 2015).
/// </summary>
public class VideoConverter
{
    public record Tool(string Kind, string Path, string Version);

    public static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg" };

    /// <summary>Extensions vidéo acceptées (la liste n'est limitée que par ce que ffmpeg / VLC savent lire).</summary>
    public static readonly string[] VideoExts =
    {
        ".mp4", ".m4v", ".mov", ".avi", ".mkv", ".wmv", ".asf", ".flv", ".f4v", ".webm", ".ogv", ".ogg", ".mpg", ".mpeg", ".mpe", ".m2v",
        ".ts", ".mts", ".m2ts", ".vob", ".3gp", ".3g2", ".divx", ".xvid", ".rm", ".rmvb", ".mxf", ".dv", ".qt", ".hevc", ".h264", ".h265",
    };

    public static bool IsVideo(string ext) => VideoExts.Contains(ext);

    /// <summary>Taille maximale publiée : 1920×1080 en paysage, 1080×1920 en portrait. Au-delà (2K, 4K), les écrans et boîtiers courants saccadent.</summary>
    public const int MaxLong = 1920, MaxShort = 1080;

    public static bool FitsLimit(int w, int h) => Math.Max(w, h) <= MaxLong && Math.Min(w, h) <= MaxShort;

    // Réduit au besoin pour tenir dans la boîte (jamais d'agrandissement), proportions gardées, dimensions paires
    private const string ScaleFilter =
        "scale=w='min(iw,if(gt(iw,ih),1920,1080))':h='min(ih,if(gt(iw,ih),1080,1920))':force_original_aspect_ratio=decrease:force_divisible_by=2";

    /// <summary>MP4/M4V sont publiés tels quels (on suppose du H.264) sauf si l'on force la conversion (H.265, 4K…).</summary>
    public static bool NeedsConversion(string ext, bool force) => force || ext is not ".mp4" and not ".m4v";

    private Tool? _tool;
    private bool _searched;

    public Tool? Find()
    {
        if (_searched) return _tool;
        _searched = true;
        _tool = FindFfmpeg() ?? FindVlc();
        return _tool;
    }

    public string Describe()
    {
        var t = Find();
        return t == null
            ? "Aucun convertisseur vidéo détecté (installez ffmpeg ou VLC sur le serveur) : seuls les MP4 H.264 sont acceptés."
            : $"Conversion automatique via {t.Kind} {t.Version}".Trim();
    }

    // ---------- Détection ----------
    private static IEnumerable<string> Candidates(params string[] names)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var d in dirs)
            foreach (var n in names)
                yield return Path.Combine(d.Trim('"'), n);
    }

    private static Tool? FindFfmpeg()
    {
        var env = Environment.GetEnvironmentVariable("LINKII_FFMPEG");
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(env)) paths.Add(env);
        paths.AddRange(Candidates("ffmpeg.exe", "ffmpeg"));
        foreach (var p in paths.Where(File.Exists))
        {
            var v = Run(p, new[] { "-version" }, 8, out _);
            if (v != null && v.Contains("ffmpeg version", StringComparison.OrdinalIgnoreCase))
                return new Tool("ffmpeg", p, v.Split('\n')[0].Replace("ffmpeg version", "").Trim().Split(' ')[0]);
        }
        return null;
    }

    private static Tool? FindVlc()
    {
        var paths = new List<string>();
        var env = Environment.GetEnvironmentVariable("LINKII_VLC");
        if (!string.IsNullOrWhiteSpace(env)) paths.Add(env);
        paths.Add(@"C:\Program Files\VideoLAN\VLC\vlc.exe");
        paths.Add(@"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe");
        paths.Add("/usr/bin/vlc"); paths.Add("/usr/bin/cvlc"); paths.Add("/usr/local/bin/vlc");
        paths.Add("/Applications/VLC.app/Contents/MacOS/VLC");
        paths.AddRange(Candidates("vlc.exe", "vlc", "cvlc"));
        foreach (var p in paths.Where(File.Exists))
        {
            // Sous Windows, vlc.exe n'écrit rien dans la console pour --version (il dépose un vlc-help.txt) : on lit la version du fichier
            var info = FileVersionInfo.GetVersionInfo(p);
            if ((info.ProductName ?? "").Contains("VLC", StringComparison.OrdinalIgnoreCase))
                return new Tool("VLC", p, (info.ProductVersion ?? "").Replace(',', '.').Replace(" ", ""));

            var v = Run(p, new[] { "--version", "-I", "dummy" }, 15, out _);
            // « VLC media player 3.0.20 Vetinari (revision …) »
            var line = v?.Split('\n').FirstOrDefault(l => l.Contains("VLC", StringComparison.OrdinalIgnoreCase) && l.Contains("version", StringComparison.OrdinalIgnoreCase))
                       ?? v?.Split('\n').FirstOrDefault(l => l.Contains("VLC media player", StringComparison.OrdinalIgnoreCase));
            if (line != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"\d+\.\d+(\.\d+)*");
                return new Tool("VLC", p, m.Success ? m.Value : "");
            }
        }
        return null;
    }

    // ---------- Conversion ----------
    /// <summary>Convertit <paramref name="input"/> vers <paramref name="output"/> (MP4). Renvoie null si OK, sinon le message d'erreur.</summary>
    /// <param name="size">Taille connue de la source (MP4) : permet à VLC, qui n'a pas de « tenir dans une boîte », de calculer le bon facteur de réduction.</param>
    public async Task<string?> ConvertAsync(string input, string output, (int W, int H)? size = null, CancellationToken ct = default)
    {
        var tool = Find();
        if (tool == null) return "Aucun convertisseur vidéo (ffmpeg ou VLC) n'est installé sur le serveur.";

        string[] args;
        if (tool.Kind == "ffmpeg")
        {
            args = new[]
            {
                "-y", "-hide_banner", "-loglevel", "error", "-i", input,
                "-an",                                             // l'écran est muet : on économise la place
                "-vf", ScaleFilter,                                // 1080p maximum (1920×1080 en paysage, 1080×1920 en portrait)
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p",
                "-movflags", "+faststart", "-f", "mp4", output,
            };
        }
        else
        {
            var dst = output.Replace('\\', '/');
            string fit;
            if (size is { } s)
            {
                var factor = Math.Min(1.0, Math.Min((double)MaxLong / Math.Max(s.W, s.H), (double)MaxShort / Math.Min(s.W, s.H)));
                fit = factor < 1 ? "scale=" + factor.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "," : "";
            }
            else fit = "maxwidth=1920,maxheight=1080,";   // taille inconnue : on suppose un format paysage
            var sout = "#transcode{vcodec=h264,vb=3500," + fit + "venc=x264{preset=veryfast}}:std{access=file,mux=mp4,dst=\"" + dst + "\"}";
            args = new[] { "-I", "dummy", "--dummy-quiet", "--no-sout-audio", "--no-spu", input, "--sout", sout, "vlc://quit" };
        }

        var tmpErr = new StringBuilder();
        var psi = new ProcessStartInfo(tool.Path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = new Process { StartInfo = psi };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null && tmpErr.Length < 4000) tmpErr.AppendLine(e.Data); };
        proc.OutputDataReceived += (_, _) => { };
        try
        {
            proc.Start();
            proc.BeginErrorReadLine(); proc.BeginOutputReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            TryDelete(output);
            return "Conversion interrompue (délai de 30 minutes dépassé).";
        }
        catch (Exception ex)
        {
            return "Impossible de lancer le convertisseur : " + ex.Message;
        }

        var ok = File.Exists(output) && new FileInfo(output).Length > 1024;
        if (!ok)
        {
            TryDelete(output);
            var tail = string.Join(" ", tmpErr.ToString().Split('\n').Where(l => l.Trim().Length > 0 && !l.Contains("stale plugins cache")).TakeLast(2)).Trim();
            return "Conversion impossible (format non reconnu ou fichier corrompu)." + (tail.Length > 0 ? " " + (tail.Length > 220 ? tail[..220] : tail) : "");
        }
        return null;
    }

    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    private static string? Run(string exe, string[] args, int timeoutSec, out int exitCode)
    {
        exitCode = -1;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var sb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutSec * 1000)) { try { p.Kill(true); } catch { } return sb.ToString(); }
            p.WaitForExit();
            exitCode = p.ExitCode;
            return sb.ToString();
        }
        catch { return null; }
    }
}

/// <summary>Lit la taille affichée d'un MP4 (boîte « tkhd » de la piste vidéo, rotation comprise) sans outil externe.</summary>
public static class Mp4Probe
{
    public static (int W, int H)? Size(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Walk(fs, 0, fs.Length);
        }
        catch { return null; }
    }

    private static (int, int)? Walk(FileStream fs, long start, long end)
    {
        var pos = start;
        var h = new byte[16];
        while (pos + 8 <= end)
        {
            fs.Position = pos;
            var got = fs.Read(h, 0, 16);
            if (got < 8) return null;
            long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(h);
            var type = Encoding.Latin1.GetString(h, 4, 4);
            long hl = 8;
            if (size == 1 && got >= 16) { size = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(8)); hl = 16; }
            else if (size == 0) size = end - pos;
            if (size < hl) return null;

            if (type is "moov" or "trak")
            {
                if (Walk(fs, pos + hl, pos + size) is { } r) return r;
            }
            else if (type == "tkhd")
            {
                var b = new byte[100];
                fs.Position = pos + hl;
                fs.Read(b, 0, b.Length);
                var wide = b[0] == 1;
                int sizeOff = wide ? 88 : 76, matrixOff = wide ? 52 : 40;
                var w = (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(sizeOff)) >> 16);
                var hh = (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(sizeOff + 4)) >> 16);
                if (w > 0 && hh > 0)
                {
                    var a = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(matrixOff));
                    var bb = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(matrixOff + 4));
                    return a == 0 && bb != 0 ? (hh, w) : (w, hh);   // vidéo de téléphone tournée de 90° : largeur et hauteur échangées
                }
            }
            pos += size;
        }
        return null;
    }
}
