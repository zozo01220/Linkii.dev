using System.Diagnostics;
using System.Text.Json;

namespace Linkii.Pi;

/// <summary>
/// Linkivi pour Raspberry Pi OS (64 bits). Même principe que le player Windows : une coquille autour du player web (<c>/player</c>).
/// Elle lance Chromium en kiosque, le relance (plantage, page figée, redémarrage demandé par le back-office) et lui expose
/// <c>window.LinkiiNative</c> par le protocole de débogage : infos d'appareil, redémarrage, capture d'écran.
/// </summary>
internal static class Program
{
    private const string DefaultServer = "https://linkivi.duckdns.org";
    private const int DebugPort = 9222;

    private static async Task<int> Main(string[] args)
    {
        var server = Server(args);

        // une seule coquille par session
        using var lockFile = TryLock();
        if (lockFile == null) return 0;

        Log.Write($"superviseur {Device.Version}, serveur {server}");
        var crashes = new Queue<DateTime>();
        while (true)
        {
            var restart = await RunOnce(PlayerUrl(server));
            if (restart) continue;   // demandé par le back-office : on relance tout de suite

            var now = DateTime.UtcNow;
            crashes.Enqueue(now);
            while (crashes.Count > 0 && now - crashes.Peek() > TimeSpan.FromMinutes(1)) crashes.Dequeue();
            await Task.Delay(crashes.Count >= 5 ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Un cycle de vie de Chromium. Retourne true si le back-office a demandé le redémarrage.</summary>
    private static async Task<bool> RunOnce(string url)
    {
        var exe = FindChromium();
        if (exe == null) { Log.Write("Chromium introuvable (sudo apt install chromium)"); await Task.Delay(TimeSpan.FromSeconds(30)); return false; }

        // le player (re)démarre : l'affichage est allumé, même s'il l'a été éteint par la veille avant un redémarrage (la veille le recoupe si besoin)
        _ = Task.Run(() => Power.Display(true));

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in new[]
        {
            "--kiosk", "--noerrdialogs", "--disable-infobars", "--no-first-run", "--disable-session-crashed-bubble",
            "--autoplay-policy=no-user-gesture-required", "--check-for-update-interval=31536000",
            "--disable-features=Translate,TranslateUI", "--password-store=basic", "--ozone-platform-hint=auto",
            "--disable-pinch", "--overscroll-history-navigation=0", "--disable-translate", "--disable-sync", "--no-default-browser-check",   // poste dédié : aucun geste, aucune proposition du navigateur
            "--disable-component-update", "--disable-background-networking", "--hide-crash-restore-bubble",
            $"--remote-debugging-port={DebugPort}", "--remote-allow-origins=*",
            "--user-data-dir=" + Paths.Profile, "about:blank"
        }) psi.ArgumentList.Add(a);

        using var chrome = Process.Start(psi)!;
        var restart = false;
        try
        {
            using var cdp = await Attach(chrome);
            if (cdp == null) { Log.Write("connexion à Chromium impossible"); return false; }

            cdp.Event += (name, p) =>
            {
                if (name == "Runtime.bindingCalled" && p.TryGetProperty("payload", out var pl))
                    _ = Task.Run(() => OnNative(cdp, pl.GetString() ?? "", () => { restart = true; Kill(chrome); }));
                else if (name == "Inspector.targetCrashed") { Log.Write("page plantée"); Kill(chrome); }
            };
            await cdp.Send("Inspector.enable");
            await cdp.Send("Page.enable");
            await cdp.Send("Runtime.enable");
            await cdp.Send("Runtime.addBinding", new { name = "__lkNative" });
            await cdp.Send("Page.addScriptToEvaluateOnNewDocument", new { source = InitScript() });

            _ = Task.Run(() => Navigate(cdp, url));
            _ = Task.Run(() => Heartbeat(cdp, chrome));
            await Task.WhenAny(chrome.WaitForExitAsync(), cdp.Closed);
        }
        catch (Exception ex) { Log.Write("erreur : " + ex.Message); }
        finally { Kill(chrome); }
        Log.Write("Chromium terminé, code " + (chrome.HasExited ? chrome.ExitCode : -1));
        return restart;
    }

    private static async Task<Cdp?> Attach(Process chrome)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (var i = 0; i < 60 && !chrome.HasExited; i++)
        {
            try
            {
                using var doc = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{DebugPort}/json/list"));
                var page = doc.RootElement.EnumerateArray().FirstOrDefault(t => t.GetProperty("type").GetString() == "page");
                if (page.ValueKind == JsonValueKind.Object) return await Cdp.Connect(page.GetProperty("webSocketDebuggerUrl").GetString()!, CancellationToken.None);
            }
            catch { /* pas encore prêt */ }
            await Task.Delay(500);
        }
        return null;
    }

    /// <summary>Charge le player ; serveur injoignable au démarrage : nouvelle tentative toutes les 10 s (le cache hors ligne prend le relais une fois installé).</summary>
    private static async Task Navigate(Cdp cdp, string url)
    {
        while (true)
        {
            try
            {
                var r = await cdp.Send("Page.navigate", new { url }, 30_000);
                if (!r.TryGetProperty("errorText", out var err)) return;
                Log.Write("chargement impossible : " + err.GetString());
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Write("chargement : " + ex.Message); }
            catch { return; }
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>Page qui ne répond plus pendant une minute : on ferme Chromium, la boucle le relance.</summary>
    private static async Task Heartbeat(Cdp cdp, Process chrome)
    {
        var failed = 0;
        while (!chrome.HasExited)
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            if (Power.IsOff) { failed = 0; continue; }   // affichage coupé par la veille : une page muette n'est pas une page figée
            try { await cdp.Send("Runtime.evaluate", new { expression = "1" }, 10_000); failed = 0; }
            catch { failed++; }
            if (failed >= 4) { Log.Write("page figée : arrêt"); Kill(chrome); return; }
        }
    }

    // ---- pont window.LinkiiNative -------------------------------------------------------------

    /// <summary>Mêmes noms que l'app Android pour les fonctions reprises. Les appels vers la coquille passent par le binding __lkNative (sans retour).</summary>
    private static string InitScript() => """
        (function () {
          var device = __DEVICE__;
          function send(fn, a) { try { window.__lkNative(JSON.stringify({ fn: fn, a: a || [] })); } catch (e) {} }
          window.LinkiiNative = {
            deviceInfo: function () { return device; },
            config: function (j) { send('config', [String(j)]); },
            restart: function () { send('restart'); },
            reboot: function () { send('reboot'); return true; },
            powerOff: function () { send('poweroff'); return true; },
            displayPower: function (on) { send('display', [on ? '1' : '0']); },
            capture: function (id, url, token) { send('capture', [String(id), String(url), String(token)]); },
            unpaired: function () { return false; }
          };
        })();
        """.Replace("__DEVICE__", JsonSerializer.Serialize(Device.Json()));

    private static async Task OnNative(Cdp cdp, string payload, Action restart)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var fn = doc.RootElement.GetProperty("fn").GetString();
            var a = doc.RootElement.GetProperty("a").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            switch (fn)
            {
                case "restart": Log.Write("redémarrage demandé"); restart(); break;
                case "reboot": Power.Reboot(); break;
                case "poweroff": Power.Off(); break;
                case "display":
                    var on = a.Length > 0 && a[0] == "1";
                    Power.Display(on);
                    if (on) { try { await cdp.Send("Page.bringToFront"); } catch { } }   // la page reprend la main sur l'affichage rallumé
                    break;
                case "config": try { File.WriteAllText(Path.Combine(Paths.Data, "settings.json"), a[0]); } catch { } break;
                case "capture": await Capture(cdp, a[0], a[1], a[2]); break;
            }
        }
        catch (Exception ex) { Log.Write("pont : " + ex.Message); }
    }

    /// <summary>Capture de la page (dans le sens de la scène : ?plain=true), envoyée au serveur avec le jeton de l'écran.</summary>
    private static async Task Capture(Cdp cdp, string id, string url, string token)
    {
        try
        {
            byte[] jpeg = [];
            foreach (var quality in new[] { 75, 50, 30 })
            {
                var shot = await cdp.Send("Page.captureScreenshot", new { format = "jpeg", quality }, 20_000);
                jpeg = Convert.FromBase64String(shot.GetProperty("data").GetString()!);
                if (jpeg.Length < 1_900_000) break;   // le serveur refuse au-delà de 2 Mo
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var req = new HttpRequestMessage(HttpMethod.Post, url + (url.Contains('?') ? "&" : "?") + "plain=true") { Content = new ByteArrayContent(jpeg) };
            req.Content.Headers.ContentType = new("image/jpeg");
            req.Headers.Add("X-Token", token);
            var r = await http.SendAsync(req);
            if (!r.IsSuccessStatusCode) await CaptureFailed(cdp, id, "Capture refusée par le serveur");
        }
        catch (Exception ex) { Log.Write("capture : " + ex.Message); await CaptureFailed(cdp, id, "Capture impossible"); }
    }

    private static Task CaptureFailed(Cdp cdp, string id, string why) =>
        cdp.Send("Runtime.evaluate", new { expression = $"window.lkCaptureFailed && lkCaptureFailed({JsonSerializer.Serialize(id)}, {JsonSerializer.Serialize(why)})" });

    // ---- utilitaires --------------------------------------------------------------------------

    private static void Kill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch { } }

    private static string? FindChromium()
    {
        foreach (var name in new[] { "chromium-browser", "chromium" })
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':'))
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    /// <summary>Adresse du serveur : --server, sinon LINKII_SERVER, sinon celle intégrée.</summary>
    private static string Server(string[] args)
    {
        var i = Array.IndexOf(args, "--server");
        var raw = i >= 0 && i + 1 < args.Length ? args[i + 1] : Environment.GetEnvironmentVariable("LINKII_SERVER");
        if (string.IsNullOrWhiteSpace(raw)) return DefaultServer;
        raw = raw.Trim();
        if (!raw.Contains("://")) raw = "https://" + raw;
        return Uri.TryCreate(raw, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? raw.TrimEnd('/') : DefaultServer;
    }

    private static string PlayerUrl(string server) => server.Contains("/player") ? server : server + "/player/";

    private static FileStream? TryLock()
    {
        try { return new FileStream(Path.Combine(Paths.Data, "player.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }
}
