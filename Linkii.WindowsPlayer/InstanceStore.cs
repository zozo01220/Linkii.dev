using System.Text.Json;
using Microsoft.Win32;

namespace Linkii.Player;

/// <summary>
/// Données d'une instance : <c>%LOCALAPPDATA%\Linkii\Player\{instance}\</c> (profil du navigateur = jeton d'appairage, playlist et cache hors ligne ;
/// réglages ; journal). Deux instances ne partagent rien : chacune est un écran à part dans le back-office.
/// </summary>
internal sealed class InstanceStore
{
    private sealed record Settings(string? Server);

    public InstanceStore(string instance)
    {
        Name = instance;
        Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Linkii", "Player", instance);
        Directory.CreateDirectory(Root);
    }

    public string Name { get; }
    public string Root { get; }
    public string WebViewDir => Path.Combine(Root, "webview");
    private string SettingsPath => Path.Combine(Root, "settings.json");

    /// <summary>Oublie l'appairage : le profil du navigateur contient le jeton de l'écran.</summary>
    public void Reset()
    {
        try { if (Directory.Exists(WebViewDir)) Directory.Delete(WebViewDir, true); }
        catch (Exception ex) { Log.Write(this, "reset impossible : " + ex.Message); }
    }

    public string? SavedServer
    {
        get
        {
            try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath))?.Server : null; }
            catch { return null; }
        }
        set => File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(value)));
    }

    /// <summary>Adresse posée par l'installeur (SERVERURL), pour toutes les instances de la session ou du poste.</summary>
    public static string? InstalledServer()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = hive.OpenSubKey(@"Software\Linkii\Player");
                if (k?.GetValue("ServerUrl") is string s && s.Length > 0) return s;
            }
            catch { /* clé absente ou illisible */ }
        }
        return null;
    }

    /// <summary>« https://… » sans barre finale ; null si l'adresse n'est pas utilisable.</summary>
    public static string? NormalizeServer(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (!url.Contains("://")) url = "https://" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? url.TrimEnd('/') : null;
    }

    /// <summary>Adresse de la page du player, comme l'app Android.</summary>
    public static string PlayerUrl(string server) => server.Contains("/player") ? server : server + "/player/";
}

internal static class Log
{
    public static void Write(InstanceStore store, string message)
    {
        try
        {
            var path = Path.Combine(store.Root, "player.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".1", true);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { /* le journal ne doit jamais empêcher la diffusion */ }
    }
}
