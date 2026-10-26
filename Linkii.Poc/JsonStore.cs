using System.Text.Json;

namespace Linkii.Poc;

/// <summary>Persistance JSON plate (data.json). Tout est sous verrou : suffisant pour un POC.</summary>
public class JsonStore
{
    public string Path { get; }
    private readonly object _lock = new();
    private readonly Db _db;
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public JsonStore(IConfiguration config, IWebHostEnvironment env)
    {
        var dir = config["Linkii:DataDir"] ?? env.ContentRootPath;
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, "data.json");
        _db = File.Exists(Path) ? JsonSerializer.Deserialize<Db>(File.ReadAllText(Path)) ?? new Db() : new Db();
    }

    /// <summary>Lecture (ou mutation non persistée, ex. LastSeen). Accès à toute la base : réservé au code de la plateforme (API player, console, hub).</summary>
    public T Read<T>(Func<Db, T> f) { lock (_lock) return f(_db); }

    /// <summary>Mutation persistée.</summary>
    public T Write<T>(Func<Db, T> f) { T r; lock (_lock) { r = f(_db); Save(); } Changed?.Invoke(); return r; }

    public void Write(Action<Db> a) { lock (_lock) { a(_db); Save(); } Changed?.Invoke(); }

    /// <summary>Levé après chaque écriture (hors verrou) : les pastilles du menu se recalculent sans recharger la page (voir <see cref="MenuBadges"/>). Les abonnés doivent rester très brefs.</summary>
    public event Action? Changed;

    private void Save() => File.WriteAllText(Path, JsonSerializer.Serialize(_db, Opts));
}
