using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace Linkii.Poc;

/// <summary>
/// Capture d'une page web (vignette de l'app « Page web ») : un navigateur sans fenêtre (Chrome, Edge ou Chromium, détecté comme ffmpeg)
/// ouvre l'adresse en 1920×1080 et la capture est gardée sur le disque, une par adresse. Elle est refaite après 12 h.
/// Sans navigateur sur le serveur, rien n'est capturé et l'icône de l'app reste affichée.
/// L'adresse est saisie par un client : elle ne doit pas viser le réseau interne du serveur (même contrôle que SafeHttp).
/// </summary>
public class WebSnapshot
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
    private static readonly SemaphoreSlim Slots = new(2);   // deux navigateurs au plus à la fois
    private static readonly HashSet<string> Running = new();   // adresses en cours de capture
    private static readonly Dictionary<string, DateTime> Failed = new();   // dernière tentative ratée, pour ne pas relancer un navigateur à chaque affichage
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);
    private string? _browser;
    private bool _searched;

    /// <summary>Au démarrage : tue les navigateurs de capture restés après un arrêt brutal du serveur (reconnus à leur dossier linkii-snap-).</summary>
    public static void KillOrphans()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var psi = Process.Start(new ProcessStartInfo("powershell", "-NoProfile -Command \"Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*linkii-snap-*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }\"")
                { CreateNoWindow = true, UseShellExecute = false });
            }
            else
            {
                using var p = Process.Start(new ProcessStartInfo("pkill", "-f linkii-snap-") { CreateNoWindow = true, UseShellExecute = false });
            }
        }
        catch { }
    }

    public string? Browser
    {
        get
        {
            if (_searched) return _browser;
            _searched = true;
            var paths = new List<string>();
            var env = Environment.GetEnvironmentVariable("LINKII_CHROME");
            if (!string.IsNullOrWhiteSpace(env)) paths.Add(env);
            paths.Add(@"C:\Program Files\Google\Chrome\Application\chrome.exe");
            paths.Add(@"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe");
            paths.Add(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe");
            paths.Add(@"C:\Program Files\Microsoft\Edge\Application\msedge.exe");
            foreach (var d in new[] { "/usr/bin", "/usr/local/bin", "/snap/bin" })
                foreach (var n in new[] { "chromium", "chromium-browser", "google-chrome", "google-chrome-stable", "microsoft-edge" })
                    paths.Add(Path.Combine(d, n));
            _browser = paths.FirstOrDefault(File.Exists);
            return _browser;
        }
    }

    public bool Available => Browser != null;

    private static string Key(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim()))).ToLowerInvariant()[..24];
    /// <summary>thumbs/web/ : une capture par adresse, commune à toutes les organisations (même page publique, même image).</summary>
    private static string Dir => Path.Combine(AppPaths.ThumbDir, "web");
    private static string PathOf(string url) => Path.Combine(Dir, "web-" + Key(url) + ".jpg");

    /// <summary>Chemin de la capture déjà faite pour cette adresse, ou null (même périmée : mieux vaut une vieille image que l'icône).</summary>
    public string? Cached(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !AppCatalog.IsHttpUrl(url)) return null;
        var p = PathOf(url);
        return File.Exists(p) ? p : null;
    }

    /// <summary>Lance la capture en arrière-plan si elle manque ou est périmée (sans attendre).</summary>
    public void Ensure(string? url)
    {
        if (!Available || string.IsNullOrWhiteSpace(url) || !AppCatalog.IsHttpUrl(url)) return;
        var p = PathOf(url);
        if (File.Exists(p) && DateTime.UtcNow - File.GetLastWriteTimeUtc(p) < MaxAge) return;
        lock (Running)
        {
            if (Failed.TryGetValue(url, out var at) && DateTime.UtcNow - at < RetryAfter) return;
            if (!Running.Add(url)) return;
        }
        _ = Task.Run(async () =>
        {
            var ok = false;
            try { ok = await Capture(url); }
            catch { }
            finally
            {
                lock (Running)
                {
                    Running.Remove(url);
                    if (ok) Failed.Remove(url); else Failed[url] = DateTime.UtcNow;
                }
            }
        });
    }

    private async Task<bool> Capture(string url)
    {
        var uri = SafeHttp.ParseUrl(url);
        var addresses = await Dns.GetHostAddressesAsync(uri.Host);
        if (addresses.Length == 0 || addresses.Any(a => !SafeHttp.IsPublic(a))) return false;

        await Slots.WaitAsync();
        var work = Path.Combine(Path.GetTempPath(), "linkii-snap-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work);
            var png = Path.Combine(work, "shot.png");
            var psi = new ProcessStartInfo(Browser!)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[]
            {
                "--headless=new", "--disable-gpu", "--no-sandbox", "--hide-scrollbars", "--mute-audio", "--no-first-run",
                "--disable-extensions", "--disable-dev-shm-usage", "--user-data-dir=" + work,
                "--window-size=1920,1080", "--virtual-time-budget=8000", "--screenshot=" + png, uri.AbsoluteUri,
            }) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            try
            {
                _ = proc.StandardOutput.ReadToEndAsync();
                _ = proc.StandardError.ReadToEndAsync();
                // Chrome headless ne se ferme pas toujours après la capture : on attend le fichier (ou la sortie), puis on tue l'arbre.
                var deadline = DateTime.UtcNow.AddSeconds(40);
                while (!proc.HasExited && DateTime.UtcNow < deadline && !File.Exists(png))
                    await Task.Delay(250);
                if (!proc.HasExited && File.Exists(png)) await Task.Delay(500);   // laisse finir l'écriture du PNG
            }
            finally
            {
                // Toujours tuer le navigateur et ses enfants, quelle que soit l'issue (délai, erreur, succès).
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
            }
            if (!File.Exists(png)) return false;

            using var bmp = SKBitmap.Decode(png);
            if (bmp == null) return false;
            var w = Math.Min(960, bmp.Width);
            using var small = bmp.Resize(new SKImageInfo(w, Math.Max(1, (int)Math.Round(bmp.Height * (double)w / bmp.Width))), SKFilterQuality.Medium);
            if (small == null) return false;
            using var data = SKImage.FromBitmap(small).Encode(SKEncodedImageFormat.Jpeg, 80);
            Directory.CreateDirectory(Dir);
            var dest = PathOf(url);
            var tmp = dest + ".tmp";
            await using (var fs = File.Create(tmp)) data.SaveTo(fs);
            File.Move(tmp, dest, true);
            return true;
        }
        catch { return false; }
        finally
        {
            Slots.Release();
            try { Directory.Delete(work, true); } catch { }
        }
    }
}
