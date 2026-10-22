using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Linkii.Player;

/// <summary>Appareil remonté au back-office (même forme que l'app Android) : { kind, os, model, app, bootMs } + PC, instance et moniteurs.</summary>
internal static class Device
{
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.2.0";

    public static string Json(string instance) => JsonSerializer.Serialize(new
    {
        kind = "windows",
        os = OsName(),
        model = Model(),
        app = Version,
        bootMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Environment.TickCount64,
        host = InstanceStore.HostId(),   // plusieurs écrans d'un même PC : même identifiant
        instance,
        monitors = Monitors.All().Select(m => new { n = m.Number, w = m.Width, h = m.Height, primary = m.Primary })
    });

    private static string OsName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = int.TryParse(k?.GetValue("CurrentBuildNumber") as string, out var b) ? b : 0;
            var name = build >= 22000 ? "Windows 11" : "Windows 10";
            return k?.GetValue("DisplayVersion") is string dv && dv.Length > 0 ? $"{name} {dv}" : name;
        }
        catch { return "Windows"; }
    }

    /// <summary>Modèle du poste (BIOS).</summary>
    private static string Model()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var machine = $"{k?.GetValue("SystemManufacturer")} {k?.GetValue("SystemProductName")}".Trim();
            if (machine.Length > 0) return machine;
        }
        catch { }
        return Environment.MachineName;
    }
}

/// <summary>Démarrage avec la session Windows (clé Run de l'utilisateur), une entrée par instance, réglé depuis le back-office.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Prefix = "LinkiiPlayer-";

    public static void Set(string instance, bool on, IEnumerable<string> args)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (!on) { key.DeleteValue(Prefix + instance, false); return; }
            key.SetValue(Prefix + instance, string.Join(' ', new[] { Environment.ProcessPath! }.Concat(args).Select(Quote)));
        }
        catch { /* registre inaccessible : le player tourne quand même */ }
    }

    /// <summary>Désinstallation : plus aucune instance ne démarre avec la session.</summary>
    public static void RemoveAll()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            foreach (var n in key.GetValueNames().Where(n => n.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))) key.DeleteValue(n, false);
        }
        catch { }
    }

    private static string Quote(string s) => s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? s : "\"" + s.Replace("\"", "\\\"") + "\"";
}

/// <summary>Écrans ajoutés sur ce PC depuis le back-office (« Ajouter un écran sur ce PC ») : une instance de plus, déjà appairée.</summary>
internal static class Launcher
{
    public static bool Running(string instance) => Mutex.TryOpenExisting(@"Local\LinkiiPlayer." + instance, out var m) && Dispose(m);

    private static bool Dispose(Mutex m) { m.Dispose(); return true; }

    /// <summary>Prépare l'instance (jeton et paramètres de l'écran) puis la lance ; rien si elle tourne déjà.</summary>
    public static void Open(string instance, InstanceSettings settings)
    {
        if (instance == "default" || Running(instance)) return;
        var store = new InstanceStore(instance);
        store.Save(settings);
        Log.Write(store, "écran ajouté depuis le back-office : ouverture");
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        psi.ArgumentList.Add("--instance"); psi.ArgumentList.Add(instance);
        Process.Start(psi);
    }
}

/// <summary>Objet exposé au player (window.LinkiiNative, voir PlayerWindow) : mêmes noms que l'app Android pour les fonctions reprises.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public sealed class Bridge
{
    private readonly string instance;
    private readonly Action restart;
    private readonly Action<string> config;
    private readonly Action removed;

    internal Bridge(string instance, Action restart, Action<string> config, Action removed)
    {
        this.instance = instance; this.restart = restart; this.config = config; this.removed = removed;
    }

    public string DeviceInfo() => Device.Json(instance);

    /// <summary>Redémarrage demandé par le back-office : le superviseur relance l'affichage aussitôt.</summary>
    public void Restart() => restart();

    /// <summary>Paramètres de l'appareil reçus du back-office (JSON du bloc « device »).</summary>
    public void Config(string json) => config(json);

    /// <summary>Écran supprimé : un écran ajouté sur ce PC ferme sa fenêtre (true) ; l'écran principal du PC revient au code d'appairage (false).</summary>
    public bool Unpaired()
    {
        if (instance == "default") return false;
        removed();
        return true;
    }
}
