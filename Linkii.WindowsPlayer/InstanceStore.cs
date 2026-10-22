using System.Text.Json;
using Microsoft.Win32;

namespace Linkii.Player;

/// <summary>Paramètres de l'appareil reçus du back-office (gardés pour démarrer hors ligne) ; valeurs par défaut avant la première connexion.</summary>
internal sealed class InstanceSettings
{
    public string? Name { get; set; }               // nom de l'écran (titre de la fenêtre de test)
    public bool AutoStart { get; set; } = true;
    public bool Windowed { get; set; }
    public int Monitor { get; set; } = 1;
    public int Width { get; set; } = 1920;          // fenêtre de test : la résolution de l'écran
    public int Height { get; set; } = 1080;
    /// <summary>Écran ajouté depuis le back-office : jeton posé dans le player avant son premier chargement, effacé dès la première configuration reçue.</summary>
    public string? Token { get; set; }

    public bool SameDisplay(InstanceSettings o) => Windowed == o.Windowed && Monitor == o.Monitor && Width == o.Width && Height == o.Height;
}

/// <summary>
/// Données d'une instance : <c>%LOCALAPPDATA%\Linkii\Player\{instance}\</c> (profil du navigateur = jeton d'appairage, playlist et cache hors ligne ;
/// paramètres reçus du back-office ; journal). Deux instances ne partagent rien : chacune est un écran à part dans le back-office.
/// </summary>
internal sealed class InstanceStore
{
    /// <summary>Adresse du serveur intégrée au player (l'installeur peut en poser une autre : SERVERURL).</summary>
    public const string DefaultServer = "http://localhost:5080";

    public static string Base => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Linkii", "Player");

    public InstanceStore(string instance)
    {
        Name = instance;
        Root = Path.Combine(Base, instance);
        Directory.CreateDirectory(Root);
    }

    public string Name { get; }
    public string Root { get; }
    public string WebViewDir => Path.Combine(Root, "webview");
    private string SettingsPath => Path.Combine(Root, "settings.json");

    public static bool Exists(string instance) => Directory.Exists(Path.Combine(Base, instance));

    public InstanceSettings Load()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<InstanceSettings>(File.ReadAllText(SettingsPath)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save(InstanceSettings s)
    {
        try { File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log.Write(this, "paramètres non enregistrés : " + ex.Message); }
    }

    /// <summary>Écran supprimé dans le back-office : tout ce que l'instance a gardé disparaît (après la fermeture du navigateur).</summary>
    public void Delete()
    {
        for (var i = 0; i < 10; i++)
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); return; }
            catch { Thread.Sleep(500); }   // le navigateur libère ses fichiers quelques instants après la fermeture
        }
    }

    /// <summary>Identifiant du PC (le même pour toutes ses instances) : le back-office reconnaît les écrans d'un même PC.</summary>
    public static string HostId()
    {
        var path = Path.Combine(Base, "host.id");
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: 32 } id) return id;
            Directory.CreateDirectory(Base);
            var fresh = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, fresh);
            return fresh;
        }
        catch { return Environment.MachineName; }
    }

    /// <summary>Adresse du serveur : celle posée par l'installeur, sinon celle intégrée au player.</summary>
    public static string Server()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = hive.OpenSubKey(@"Software\Linkii\Player");
                if (NormalizeServer(k?.GetValue("ServerUrl") as string) is { } s) return s;
            }
            catch { /* clé absente ou illisible */ }
        }
        return DefaultServer;
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
            if (!Directory.Exists(store.Root)) return;   // instance supprimée
            var path = Path.Combine(store.Root, "player.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_000_000) File.Move(path, path + ".1", true);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { /* le journal ne doit jamais empêcher la diffusion */ }
    }
}
