using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Linkii.Poc;

/// <summary>Un relais Linkii de l'organisation (§139, lot 4). Le jeton n'est gardé que sous forme d'empreinte ; le code d'appairage vaut une heure.</summary>
public class DataRelay
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? PairingCode { get; set; }
    public DateTime? CodeUtc { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime? PairedUtc { get; set; }
    /// <summary>Machine et version annoncées à l'appairage (les dernières vues sont gardées en mémoire par <see cref="RelayBroker"/>).</summary>
    public string Machine { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DataRelay Copy() => (DataRelay)MemberwiseClone();
}

/// <summary>
/// File des tâches des relais : le serveur dépose une tâche (lire une base, appeler une API interne), le relais la prend en appelant /api/relay/next
/// (attente longue), l'exécute chez le client et rend le résultat par /api/relay/reply. Rien n'est persisté : un relais hors ligne fait échouer la lecture,
/// et la dernière copie du jeu reste affichée. Présence (dernier appel, machine, version) gardée en mémoire seulement.
/// </summary>
public class RelayBroker(JsonStore store)
{
    public const int MaxPerOrg = 3;
    public static readonly TimeSpan CodeLife = TimeSpan.FromHours(1);
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(25);
    public static readonly TimeSpan OnlineFor = TimeSpan.FromSeconds(70);
    public static readonly TimeSpan ReplyIn = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<Guid, Channel<RelayJob>> queues = new();
    private readonly ConcurrentDictionary<Guid, (Guid Relay, TaskCompletionSource<RelayReply> Done)> pending = new();
    private readonly ConcurrentDictionary<Guid, (DateTime At, string Machine, string Version)> seen = new();

    private Channel<RelayJob> Queue(Guid relay) => queues.GetOrAdd(relay, _ => Channel.CreateBounded<RelayJob>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest }));

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Code d'appairage : 8 caractères sans ambiguïté (pas de 0/O, 1/I).</summary>
    public static string NewCode()
    {
        const string a = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var b = RandomNumberGenerator.GetBytes(8);
        return string.Concat(b.Select(x => a[x % a.Length]));
    }

    public bool Online(Guid relay) => seen.TryGetValue(relay, out var s) && DateTime.UtcNow - s.At < OnlineFor;
    public (DateTime At, string Machine, string Version)? Seen(Guid relay) => seen.TryGetValue(relay, out var s) ? s : null;

    /// <summary>Relais du jeton (organisation, relais), ou null.</summary>
    public (Guid Client, Guid Relay)? Auth(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200) return null;
        var h = Hash(token);
        return store.Read(db => db.Clients.SelectMany(t => t.DataRelays.Where(r => r.TokenHash.Length > 0 && r.TokenHash == h).Select(r => ((Guid, Guid)?)(t.Id, r.Id))).FirstOrDefault());
    }

    /// <summary>Appairage : le code affiché dans le back-office (une heure) contre un jeton, rendu une seule fois. Null : code inconnu ou expiré.</summary>
    public RelayPairResponse? Pair(RelayPairRequest req)
    {
        var code = (req.Code ?? "").Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
        if (code.Length != 8) return null;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return store.Write(db =>
        {
            var now = DateTime.UtcNow;
            foreach (var t in db.Clients)
                if (t.DataRelays.FirstOrDefault(r => r.PairingCode == code && r.CodeUtc is { } c && now - c < CodeLife) is { } r)
                {
                    r.PairingCode = null; r.CodeUtc = null;
                    r.TokenHash = Hash(token); r.PairedUtc = now;
                    r.Machine = Short(req.Machine); r.Version = Short(req.Version);
                    if (r.Name.Length == 0) r.Name = Short(req.Name) is { Length: > 0 } n ? n : r.Machine;
                    seen[r.Id] = (now, r.Machine, r.Version);
                    return new RelayPairResponse(token, r.Name);
                }
            return null;
        });
    }

    private static string Short(string? s) { s = (s ?? "").Trim(); return s.Length > 60 ? s[..60] : s; }

    /// <summary>Le relais attend sa prochaine tâche (25 s au plus) ; null s'il n'y en a pas.</summary>
    public async Task<RelayJob?> Next(Guid relay, string? machine, string? version, CancellationToken ct)
    {
        seen[relay] = (DateTime.UtcNow, Short(machine), Short(version));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Wait);
        try { return await Queue(relay).Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) { return null; }
        finally { seen[relay] = (DateTime.UtcNow, Short(machine), Short(version)); }
    }

    /// <summary>Résultat d'une tâche : accepté seulement du relais à qui elle était destinée.</summary>
    public bool Complete(Guid relay, RelayReply reply)
    {
        if (!pending.TryGetValue(reply.Id, out var p) || p.Relay != relay) return false;
        pending.TryRemove(reply.Id, out _);
        p.Done.TrySetResult(reply);
        return true;
    }

    /// <summary>Confie une tâche au relais et attend sa réponse (20 s). Lève une exception lisible si le relais est hors ligne, muet, ou si la tâche a échoué.</summary>
    public async Task<RelayReply> Run(Guid clientId, Guid relay, RelayJob job, CancellationToken ct)
    {
        var name = store.Read(db => db.Clients.FirstOrDefault(t => t.Id == clientId)?.DataRelays.FirstOrDefault(r => r.Id == relay)?.Name);   // un relais de l'organisation seulement
        if (name == null) throw new InvalidOperationException("Relais introuvable : choisissez un autre relais pour cette connexion.");
        if (!Online(relay)) throw new InvalidOperationException($"Le relais « {name} » est hors ligne : vérifiez que son service tourne sur la machine du réseau.");
        var tcs = new TaskCompletionSource<RelayReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[job.Id] = (relay, tcs);
        try
        {
            await Queue(relay).Writer.WriteAsync(job, ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ReplyIn);
            await using (cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                try
                {
                    var r = await tcs.Task;
                    if (!r.Ok) throw new InvalidOperationException((r.Error ?? "Échec") + $" (relais « {name} »)");
                    return r;
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException($"Le relais « {name} » n'a pas répondu à temps."); }
            }
        }
        finally { pending.TryRemove(job.Id, out _); }
    }
}
