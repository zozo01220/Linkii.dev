using System.Diagnostics;
using System.Text.Json;

namespace Linkii.Pi;

/// <summary>
/// Alimentation du Raspberry, à la demande du back-office : redémarrer, éteindre, couper ou rallumer l'affichage (la veille de l'écran).
/// Chaque action essaie plusieurs méthodes (Wayland, X11, vcgencmd, HDMI-CEC) : la première qui réussit suffit, selon la version de Raspberry Pi OS.
/// </summary>
internal static class Power
{
    public static void Reboot() => System("reboot");
    public static void Off() => System("poweroff");

    /// <summary>systemctl directement (autorisé à l'utilisateur de la session par polkit), sinon sudo sans mot de passe (règle posée par install.sh).</summary>
    private static void System(string what)
    {
        Log.Write(what == "reboot" ? "redémarrage de l'appareil demandé" : "extinction de l'appareil demandée");
        if (Exec("systemctl", [what], 8000, out _) == 0) return;
        if (Exec("sudo", ["-n", "systemctl", what], 8000, out _) == 0) return;
        Log.Write("impossible : ni systemctl ni sudo n'ont accepté (voir install.sh)");
    }

    private static volatile bool lastOn = true;
    private static readonly object gate = new();

    /// <summary>L'affichage est coupé par la veille (le player ne doit pas prendre une page muette pour une page figée).</summary>
    public static bool IsOff => !lastOn;

    /// <summary>
    /// Allume (true) ou éteint (false) l'affichage. Éteindre s'arrête à la première méthode qui réussit ; rallumer les essaie toutes (celle qui a éteint
    /// n'est pas forcément la seule à rallumer), deux fois : une télévision remet un instant à se resynchroniser. Rallumer est aussi appelé au démarrage
    /// du player, au cas où il aurait redémarré pendant la veille. Chaque essai est écrit dans le journal.
    /// </summary>
    public static void Display(bool on)
    {
        lock (gate)
        {
            var changed = on != lastOn;
            lastOn = on;
            if (changed) Log.Write(on ? "affichage : rallumage" : "affichage : extinction (veille)");
            if (!on && !changed) return;   // déjà éteint
            var ok = Try("wlr-randr", () => Wayland(on));
            if (on || !ok) ok |= Try("xset", () => X11(on));
            if (on || !ok) ok |= Try("vcgencmd", () => Vcgencmd(on));
            if (changed) Cec(on);   // une télévision : mise en veille ou réveillée par HDMI-CEC si elle le permet
            if (!ok) Log.Write("aucune méthode n'a changé l'affichage");
            if (on)
            {
                Thread.Sleep(2500);
                Wayland(true); X11(true);   // seconde passe : la télévision a eu le temps de se resynchroniser
            }
        }
    }

    // ---- Orientation de l'écran (portrait / paysage du back-office) ----

    private static string? appliedTransform;

    /// <summary>Sens de rotation d'un écran en portrait : « 90 » ou « 270 » (selon le montage du moniteur), dans ~/.config/linkivi/portrait-transform ; 270 par défaut.</summary>
    private static string PortraitTransform()
    {
        try
        {
            var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "linkivi", "portrait-transform");
            var v = File.Exists(f) ? File.ReadAllText(f).Trim() : "";
            if (v is "90" or "270") return v;
        }
        catch { }
        return "270";
    }

    /// <summary>Au démarrage : l'orientation reçue la dernière fois (settings.json) est appliquée avant que le back-office ne réponde.</summary>
    public static void OrientFromSettings()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Paths.Data, "settings.json")));
            if (doc.RootElement.TryGetProperty("width", out var w) && doc.RootElement.TryGetProperty("height", out var h) && w.TryGetInt32(out var wi) && h.TryGetInt32(out var hi) && wi > 0 && hi > 0)
                Orient(hi > wi);
        }
        catch { /* pas encore de réglage reçu : l'écran reste comme il est */ }
    }

    /// <summary>
    /// Fait pivoter la sortie vidéo (Wayland, wlr-randr) : portrait → le sens choisi, paysage → normal. Le player n'a alors plus à pivoter sa scène.
    /// Réessaie pendant une trentaine de secondes : au démarrage de la session, la sortie vidéo n'est pas toujours prête.
    /// </summary>
    public static void Orient(bool portrait)
    {
        var want = portrait ? PortraitTransform() : "normal";
        lock (gate)
        {
            if (appliedTransform == want) return;
            var env = SessionEnv();
            for (var i = 0; i < 20; i++)
            {
                if (Exec("wlr-randr", [], 5000, out var list, env) == 0)
                {
                    var outputs = new List<(string Name, bool Enabled, string Transform)>();
                    string? name = null; var enabled = false; var transform = "normal";
                    void Flush() { if (name != null) outputs.Add((name, enabled, transform)); }
                    foreach (var raw in list.Split('\n'))
                    {
                        if (raw.Length == 0) continue;
                        if (!char.IsWhiteSpace(raw[0])) { Flush(); name = raw.Split(' ')[0]; enabled = false; transform = "normal"; continue; }
                        var line = raw.Trim();
                        if (line.StartsWith("Enabled:")) enabled = line.Contains("yes");
                        else if (line.StartsWith("Transform:")) transform = line["Transform:".Length..].Trim();
                    }
                    Flush();
                    var live = outputs.Where(o => o.Enabled).ToList();
                    if (live.Count > 0)
                    {
                        var ok = true;
                        foreach (var o in live.Where(o => o.Transform != want))
                            ok &= Exec("wlr-randr", ["--output", o.Name, "--transform", want], 5000, out _, env) == 0;
                        Log.Write($"orientation de l'écran : {want} ({string.Join(", ", live.Select(o => o.Name))}) {(ok ? "appliquée" : "refusée")}");
                        if (ok) appliedTransform = want;
                        return;
                    }
                }
                Thread.Sleep(1500);
            }
            Log.Write("orientation de l'écran : aucune sortie vidéo trouvée (wlr-randr absent, ou session X11)");
        }
    }

    private static bool Try(string method, Func<bool> run)
    {
        var ok = false;
        try { ok = run(); } catch (Exception ex) { Log.Write($"affichage ({method}) : {ex.Message}"); }
        Log.Write($"affichage ({method}) : {(ok ? "réussi" : "sans effet")}");
        return ok;
    }

    /// <summary>Session Wayland (labwc, Raspberry Pi OS Bookworm) : wlr-randr, sortie par sortie.</summary>
    private static bool Wayland(bool on)
    {
        var env = SessionEnv();
        if (Exec("wlr-randr", [], 5000, out var list, env) != 0) return false;
        var outputs = list.Split('\n').Where(l => l.Length > 0 && !char.IsWhiteSpace(l[0])).Select(l => l.Split(' ')[0]).Where(n => n.Length > 0).ToList();
        var any = false;
        foreach (var o in outputs) any |= Exec("wlr-randr", ["--output", o, on ? "--on" : "--off"], 5000, out _, env) == 0;
        return any;
    }

    /// <summary>Session X11 : DPMS.</summary>
    private static bool X11(bool on)
    {
        var env = new Dictionary<string, string> { ["DISPLAY"] = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0" };
        var ok = Exec("xset", ["dpms", "force", on ? "on" : "off"], 5000, out _, env) == 0;
        if (on) Exec("xset", ["s", "reset"], 5000, out _, env);
        return ok;
    }

    /// <summary>Anciennes versions (sortie HDMI pilotée par le pilote du Pi).</summary>
    private static bool Vcgencmd(bool on) => Exec("vcgencmd", ["display_power", on ? "1" : "0"], 5000, out _) == 0;

    private static void Cec(bool on)
    {
        if (!OnPath("cec-client")) return;
        Exec("cec-client", ["-s", "-d", "1"], 10000, out _, null, on ? "on 0" : "standby 0");
    }

    /// <summary>Variables de la session graphique, si le programme a été lancé sans elles (ssh, service).</summary>
    private static Dictionary<string, string> SessionEnv()
    {
        var env = new Dictionary<string, string>();
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtime)) runtime = $"/run/user/{Environment.GetEnvironmentVariable("UID") ?? "1000"}";
        env["XDG_RUNTIME_DIR"] = runtime;
        var wl = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        if (string.IsNullOrEmpty(wl))
        {
            try { wl = Directory.GetFiles(runtime, "wayland-*").Where(f => !f.EndsWith(".lock")).Select(Path.GetFileName).OrderBy(n => n).FirstOrDefault(); } catch { }
        }
        if (!string.IsNullOrEmpty(wl)) env["WAYLAND_DISPLAY"] = wl;
        return env;
    }

    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':').Any(d => File.Exists(Path.Combine(d, name)));

    /// <summary>Lance une commande, attend au plus <paramref name="timeoutMs"/> ; code de sortie, ou -1 (introuvable, trop longue).</summary>
    private static int Exec(string file, string[] args, int timeoutMs, out string output, Dictionary<string, string>? env = null, string? stdin = null)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
            using var p = Process.Start(psi)!;
            if (stdin != null) { p.StandardInput.WriteLine(stdin); p.StandardInput.Close(); }
            var text = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return -1; }
            output = text.Result;
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
