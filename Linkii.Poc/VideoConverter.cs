using System.Diagnostics;
using System.Text;

namespace Linkii.Poc;

/// <summary>
/// Convertit n'importe quelle vidéo (MKV, AVI, MOV, WMV, FLV, MPEG, WebM, 3GP, HEVC…) en MP4 H.264 sans son, 1080p max :
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
    public async Task<string?> ConvertAsync(string input, string output, CancellationToken ct = default)
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
                "-vf", "scale='min(1920,iw)':-2",                  // 1080p maximum, hauteur paire
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-pix_fmt", "yuv420p",
                "-movflags", "+faststart", output,
            };
        }
        else
        {
            var dst = output.Replace('\\', '/');
            var sout = "#transcode{vcodec=h264,vb=3500,maxwidth=1920,maxheight=1080,venc=x264{preset=veryfast}}:std{access=file,mux=mp4,dst=\"" + dst + "\"}";
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
