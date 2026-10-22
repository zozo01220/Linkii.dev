using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Linkii.Player;

/// <summary>Appareil remonté au back-office (même forme que l'app Android) : { kind, os, model, app, bootMs }.</summary>
internal static class Device
{
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.1.0";

    public static string Json(string instance) => JsonSerializer.Serialize(new
    {
        kind = "windows",
        os = OsName(),
        model = Model(instance),
        app = Version,
        bootMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Environment.TickCount64
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

    /// <summary>Modèle du poste (BIOS), suivi du nom d'instance quand il y en a plusieurs sur le même PC.</summary>
    private static string Model(string instance)
    {
        string machine;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            machine = $"{k?.GetValue("SystemManufacturer")} {k?.GetValue("SystemProductName")}".Trim();
        }
        catch { machine = ""; }
        if (machine.Length == 0) machine = Environment.MachineName;
        return instance == "default" ? machine : $"{machine} · {instance}";
    }
}

/// <summary>Démarrage avec la session Windows (clé Run de l'utilisateur), une entrée par instance.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Set(string instance, bool on, IEnumerable<string> args)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        var name = "LinkiiPlayer-" + instance;
        if (!on) { key.DeleteValue(name, false); return; }
        key.SetValue(name, string.Join(' ', new[] { Environment.ProcessPath! }.Concat(args).Select(Quote)));
    }

    private static string Quote(string s) => s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? s : "\"" + s.Replace("\"", "\\\"") + "\"";
}

/// <summary>Objet exposé au player (window.LinkiiNative, voir PlayerWindow) : mêmes noms que l'app Android pour les fonctions reprises.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public sealed class Bridge
{
    private readonly string instance;
    private readonly Action restart;

    internal Bridge(string instance, Action restart) { this.instance = instance; this.restart = restart; }

    public string DeviceInfo() => Device.Json(instance);

    /// <summary>Redémarrage demandé par le back-office : le superviseur relance l'affichage aussitôt.</summary>
    public void Restart() => restart();
}
