using System.Reflection;
using System.Text.Json;

namespace Linkii.Pi;

/// <summary>Appareil remonté au back-office (même forme que les autres players) : { kind, os, model, app, bootMs }.</summary>
internal static class Device
{
    public static string Version => Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.1.0";

    public static string Json() => JsonSerializer.Serialize(new
    {
        kind = "raspberrypi",
        os = OsName(),
        model = Model(),
        app = Version,
        bootMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - UptimeMs()
    });

    private static string OsName()
    {
        try
        {
            var line = File.ReadAllLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME="));
            if (line != null) return line["PRETTY_NAME=".Length..].Trim('"');
        }
        catch { }
        return "Raspberry Pi OS";
    }

    /// <summary>Modèle de la carte (« Raspberry Pi 5 Model B Rev 1.0 »).</summary>
    private static string Model()
    {
        try { return File.ReadAllText("/proc/device-tree/model").Trim('\0', ' ', '\n'); }
        catch { return Environment.MachineName; }
    }

    private static long UptimeMs()
    {
        try { return (long)(double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture) * 1000); }
        catch { return Environment.TickCount64; }
    }
}

internal static class Paths
{
    /// <summary>~/.local/share/linkivi : profil de Chromium (= jeton d'appairage, playlist, cache hors ligne), paramètres et journal.</summary>
    public static string Data
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var root = string.IsNullOrEmpty(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share") : xdg;
            var dir = Path.Combine(root, "linkivi");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Profile => Path.Combine(Data, "chromium");
}

internal static class Log
{
    public static void Write(string message)
    {
        try
        {
            var path = Path.Combine(Paths.Data, "player.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".1", true);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { /* le journal ne doit jamais empêcher la diffusion */ }
    }
}
