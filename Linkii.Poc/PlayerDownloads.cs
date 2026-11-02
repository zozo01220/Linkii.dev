using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>Installeurs du player déposés dans le dossier downloads des données : Linkivi-&lt;version&gt;-x64.msi (Windows), Linkivi-&lt;version&gt;-*.apk (Android), linkivi-pi-&lt;version&gt;.tar.gz (Linux, Raspberry Pi OS 64 bits).
/// La version et la taille viennent du fichier : rien à tenir à jour à la main.</summary>
public static class PlayerDownloads
{
    public record Item(string Path, string Name, string Version, double SizeMb);

    public static Item? Latest(string platform)
    {
        var pattern = platform switch { "windows" => "Linkivi-*.msi", "android" => "Linkivi-*.apk", "linux" => "linkivi-pi-*.tar.gz", _ => null };
        if (pattern == null || !Directory.Exists(AppPaths.DownloadDir)) return null;
        var file = new DirectoryInfo(AppPaths.DownloadDir).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (file == null) return null;
        var m = Regex.Match(file.Name, @"\d+\.\d+(\.\d+)?");
        return new Item(file.FullName, file.Name, m.Success ? m.Value : "", Math.Round(file.Length / 1048576.0, file.Length < 10 * 1048576 ? 1 : 0));
    }
}
