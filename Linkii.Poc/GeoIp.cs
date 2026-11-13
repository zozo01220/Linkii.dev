using System.IO.Compression;
using System.Net;
using MaxMind.Db;

namespace Linkii.Poc;

/// <summary>
/// Localisation d'un écran (carte du monde de l'admin, réservée au super-administrateur). Seul le PAYS est conservé, jamais l'adresse IP.
/// Mode par écran : <see cref="Auto"/> (par défaut : pays déduit de l'IP du player), <see cref="Manual"/> (pays choisi à la main) ou <see cref="Off"/> (absent de la carte, rien n'est gardé).
/// </summary>
public static class Geo
{
    public const string Auto = "auto", Manual = "manual", Off = "off";
    public static bool IsMode(string? m) => m is Auto or Manual or Off;
    /// <summary>Code pays ISO 3166-1 alpha-2 connu de la carte (« CH »), ou null.</summary>
    public static string? Valid(string? cc) => cc != null && WorldGeo.Find(cc.Trim().ToUpperInvariant()) != null ? cc.Trim().ToUpperInvariant() : null;
}

public static partial class WorldGeo
{
    // paresseux : l'ordre d'initialisation entre les deux fichiers de cette classe partielle n'est pas garanti
    private static readonly Lazy<Dictionary<string, (string Code, string Name, double X, double Y)>> lazyByCode = new(() => Countries.ToDictionary(c => c.Code));
    private static Dictionary<string, (string Code, string Name, double X, double Y)> byCode => lazyByCode.Value;
    public static (string Code, string Name, double X, double Y)? Find(string code) => byCode.TryGetValue(code, out var c) ? c : null;
    public static string NameOf(string code) => byCode.TryGetValue(code, out var c) ? c.Name : code;
}

/// <summary>
/// Pays d'une adresse IP, lu dans une base locale DB-IP « Country Lite » (CC-BY 4.0, https://db-ip.com), sans appel à un service externe : les IP des clients ne quittent pas le serveur.
/// La base est téléchargée au démarrage si elle manque ou date de plus de 35 jours ; sans elle, aucun pays n'est détecté (la saisie manuelle reste possible).
/// </summary>
public sealed class GeoIpService : IDisposable
{
    private readonly string dir;
    private readonly ILogger<GeoIpService> log;
    private volatile Reader? reader;
    private string DbPath => Path.Combine(dir, "dbip-country-lite.mmdb");

    public bool Ready => reader != null;

    public GeoIpService(IConfiguration config, IWebHostEnvironment env, ILogger<GeoIpService> log)
    {
        this.log = log;
        dir = Path.Combine(config["Linkii:DataDir"] ?? env.ContentRootPath, "geo");
        Directory.CreateDirectory(dir);
        Open();
        if (!Fresh()) _ = Task.Run(Refresh);
    }

    private bool Fresh() => File.Exists(DbPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(DbPath) < TimeSpan.FromDays(35);

    private void Open()
    {
        try { if (File.Exists(DbPath)) reader = new Reader(DbPath); }
        catch (Exception ex) { log.LogWarning(ex, "Base de géolocalisation illisible : {Path}", DbPath); }
    }

    private async Task Refresh()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var now = DateTime.UtcNow;
            foreach (var m in new[] { now, now.AddMonths(-1) })   // le fichier du mois en cours n'existe qu'à partir du 1er
            {
                var url = $"https://download.db-ip.com/free/dbip-country-lite-{m:yyyy-MM}.mmdb.gz";
                using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!res.IsSuccessStatusCode) continue;
                var tmp = DbPath + ".tmp";
                await using (var gz = new GZipStream(await res.Content.ReadAsStreamAsync(), CompressionMode.Decompress))
                await using (var fs = File.Create(tmp)) await gz.CopyToAsync(fs);
                var old = reader; reader = null; old?.Dispose();
                File.Move(tmp, DbPath, overwrite: true);
                Open();
                log.LogInformation("Base de géolocalisation à jour ({Month}).", m.ToString("yyyy-MM"));
                return;
            }
            log.LogWarning("Base de géolocalisation introuvable sur db-ip.com.");
        }
        catch (Exception ex) { log.LogWarning(ex, "Téléchargement de la base de géolocalisation impossible."); Open(); }
    }

    /// <summary>Code pays ISO alpha-2 (« CH »), ou null : adresse locale ou privée, base absente, pays inconnu.</summary>
    public string? Country(IPAddress? ip)
    {
        if (ip == null || reader is not { } r) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || IsPrivate(ip)) return null;
        try
        {
            var rec = r.Find<Dictionary<string, object>>(ip);
            if (rec != null && rec.TryGetValue("country", out var c) && c is Dictionary<string, object> cd && cd.TryGetValue("iso_code", out var iso))
                return Geo.Valid(iso?.ToString());
        }
        catch { }
        return null;
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length == 4) return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC;
    }

    public void Dispose() => reader?.Dispose();
}
