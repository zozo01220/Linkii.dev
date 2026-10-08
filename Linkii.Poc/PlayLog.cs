using System.Collections.Concurrent;
using System.Text.Json;

namespace Linkii.Poc;

/// <summary>Une diffusion d'un contenu sur un écran (preuve de diffusion), remontée par le player.</summary>
public record PlayEntry(DateTime StartUtc, Guid ScreenId, Guid? MediaId, string? AppId, string Name, int Seconds);

/// <summary>Historique de diffusion : un fichier JSON Lines par client (plays/{client}.jsonl, dans le dossier des données),
/// hors de data.json qui resterait sinon à réécrire à chaque contenu affiché. Conservation : <see cref="RetentionDays"/> jours.</summary>
public class PlayLog
{
    public const int RetentionDays = 90;

    private readonly string dir;
    private readonly ConcurrentDictionary<Guid, object> locks = new();
    private readonly ConcurrentDictionary<Guid, DateTime> lastPrune = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PlayLog(IConfiguration config, IWebHostEnvironment env)
    {
        dir = Path.Combine(config["Linkii:DataDir"] ?? env.ContentRootPath, "plays");
        Directory.CreateDirectory(dir);
    }

    private string FileOf(Guid clientId) => Path.Combine(dir, clientId.ToString("N") + ".jsonl");

    public void Append(Guid clientId, IEnumerable<PlayEntry> entries)
    {
        var lines = entries.Select(e => JsonSerializer.Serialize(e, Json)).ToList();
        if (lines.Count == 0) return;
        lock (locks.GetOrAdd(clientId, _ => new object()))
        {
            File.AppendAllLines(FileOf(clientId), lines);
            // une fois par jour : on retire ce qui dépasse la durée de conservation
            if (DateTime.UtcNow - lastPrune.GetOrAdd(clientId, DateTime.MinValue) > TimeSpan.FromDays(1))
            {
                lastPrune[clientId] = DateTime.UtcNow;
                var keep = ReadUnlocked(clientId, DateTime.UtcNow.AddDays(-RetentionDays)).Select(e => JsonSerializer.Serialize(e, Json)).ToList();
                File.WriteAllLines(FileOf(clientId), keep);
            }
        }
    }

    /// <summary>Diffusions commencées depuis sinceUtc, de la plus ancienne à la plus récente.</summary>
    public List<PlayEntry> Read(Guid clientId, DateTime sinceUtc)
    {
        lock (locks.GetOrAdd(clientId, _ => new object())) return ReadUnlocked(clientId, sinceUtc);
    }

    private List<PlayEntry> ReadUnlocked(Guid clientId, DateTime sinceUtc)
    {
        var path = FileOf(clientId);
        var list = new List<PlayEntry>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<PlayEntry>(line, Json);
                if (e != null && e.StartUtc >= sinceUtc) list.Add(e);
            }
            catch (JsonException) { /* ligne tronquée (arrêt pendant l'écriture) : ignorée */ }
        }
        return list.OrderBy(e => e.StartUtc).ToList();
    }
}
