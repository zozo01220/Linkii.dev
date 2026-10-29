using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Linkii.Poc;

namespace Linkii.Relay;

/// <summary>
/// Relais Linkii (DEV-POC.md §139, lot 4).
///   Linkii.Relay pair CODE --server https://linkii.exemple.ch [--name SRV-01]   appaire le relais (code affiché dans Intégrations › Sources de données › Relais)
///   Linkii.Relay [run]                                                          attend les tâches du serveur et les exécute (service Windows ou systemd)
///   Linkii.Relay status                                                         affiche la configuration
/// Configuration : relay.json à côté du programme (serveur, jeton, nom, hôtes autorisés). Le jeton ne sert qu'à ce relais ; supprimé dans le
/// back-office, il est refusé et le relais s'arrête d'attendre.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }   // accents lisibles dans l'invite de commandes Windows
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        var isService = OperatingSystem.IsWindows() && Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
        // Windows, double-clic (ni service ni commande) : l'installateur. Sous Linux ou en service : le relais démarre.
        if (cmd == "" && OperatingSystem.IsWindows() && !isService && Environment.UserInteractive) cmd = "wizard";
        if (cmd == "") cmd = "run";
        if (cmd is "install" or "uninstall" or "wizard")
        {
            if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("L'installateur est réservé à Windows. Sous Linux : voir linkii-relay.service et le README."); return 2; }
            return await Installer.Run(args, cmd);
        }
        if (cmd is "pair") return await Pair(args);
        if (cmd is "status") { Status(); return 0; }
        if (cmd is "-h" or "--help" or "help" or "/?") { Help(); return 0; }

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(o => o.ServiceName = "LinkiiRelay");
        builder.Services.AddSystemd();
        builder.Services.AddSingleton(RelayConfig.Load());
        builder.Services.AddHostedService<RelayWorker>();
        await builder.Build().RunAsync();
        return 0;
    }

    private static void Help() => Console.WriteLine("""
        Relais Linkii : lit vos bases de données et API internes pour Linkii.

          Windows : double-cliquez sur LinkiiRelay-Setup.exe et entrez le code d'installation donné par Linkii.
                    Ligne de commande : install /code=ABCD-2345 [/server=https://votre-linkii] [/dir=C:\dossier] [/silent]
                                        uninstall [/yes]

          Linux :   ./Linkii.Relay pair CODE --server https://votre-linkii [--name NOM]
                    puis le service systemd (linkii-relay.service).

          run       démarre le relais dans cette console (le service Windows ou systemd le fait tout seul)
          status    affiche la configuration
        """);

    private static void Status()
    {
        Console.WriteLine($"Fichier     : {RelayConfig.PathOf}");
        RelayConfig c;
        try { c = RelayConfig.Load(); }
        catch (UnauthorizedAccessException) { Console.WriteLine("Accès refusé : la configuration est réservée aux administrateurs (lancez cette commande en administrateur)."); return; }
        Console.WriteLine($"Serveur     : {(c.Server.Length > 0 ? c.Server : "(non appairé)")}");
        Console.WriteLine($"Nom         : {c.Name}");
        Console.WriteLine($"Jeton       : {(c.Token.Length > 0 ? "présent" : "absent")}");
        Console.WriteLine($"Hôtes permis: {(c.AllowedHosts.Count == 0 ? "tous" : string.Join(", ", c.AllowedHosts))}");
        if (RelayPairing.ServerOf(Environment.ProcessPath ?? "") is { } s) Console.WriteLine($"Téléchargé depuis : {s}");
    }

    private static async Task<int> Pair(string[] args)
    {
        string? Opt(string name) { var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var code = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        var cfg = RelayConfig.Load();
        var server = (Opt("--server") ?? RelayPairing.ServerOf(Environment.ProcessPath ?? "") ?? cfg.Server).TrimEnd('/');
        if (code == null || server.Length == 0) { Console.Error.WriteLine("Usage : Linkii.Relay pair CODE --server https://votre-linkii [--name NOM]"); return 2; }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var (r, err) = await RelayPairing.PairAsync(http, server, code, Opt("--name"));
        if (r == null) { Console.Error.WriteLine(err); return err!.StartsWith("Appairage") || err.Contains("injoignable") || err.Contains("Code") ? 1 : 2; }
        cfg.Server = server; cfg.Token = r.Token; cfg.Name = r.Name;
        cfg.Save();
        Console.WriteLine($"Relais « {r.Name} » appairé. Configuration : {RelayConfig.PathOf}");
        Console.WriteLine("Démarrez-le maintenant (service, ou « Linkii.Relay run »).");
        return 0;
    }
}

/// <summary>relay.json : serveur, jeton, nom, et hôtes autorisés (vide : tous ; sinon le relais refuse les autres, ex. « sql-salles.local », « 10.0.2.* »).</summary>
public class RelayConfig
{
    public string Server { get; set; } = "";
    public string Token { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> AllowedHosts { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    /// <summary>Dossier de relay.json : celui du programme ; l'installateur le règle sur le dossier d'installation.</summary>
    public static string Dir { get; set; } = AppContext.BaseDirectory;
    public static string PathOf => Path.Combine(Dir, "relay.json");

    public static RelayConfig Load()
    {
        try { return File.Exists(PathOf) ? JsonSerializer.Deserialize<RelayConfig>(File.ReadAllText(PathOf), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public void Save() => File.WriteAllText(PathOf, JsonSerializer.Serialize(this, Json));

    /// <summary>Hôte permis par la liste (joker « * » accepté).</summary>
    public bool Allows(string host)
    {
        if (AllowedHosts.Count == 0) return true;
        host = host.Split('\\')[0].Trim().ToLowerInvariant();
        return AllowedHosts.Any(p =>
        {
            p = p.Trim().ToLowerInvariant();
            return p.EndsWith('*') ? host.StartsWith(p.TrimEnd('*')) : p.StartsWith("*.") ? host.EndsWith(p[1..]) : host == p;
        });
    }
}

/// <summary>Boucle du relais : attend une tâche (attente longue), l'exécute en parallèle des suivantes, rend le résultat.</summary>
public class RelayWorker : BackgroundService
{
    public static readonly string Version = typeof(RelayWorker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    private readonly RelayConfig cfg;
    private readonly ILogger<RelayWorker> log;
    private readonly HttpClient server;

    public RelayWorker(RelayConfig cfg, ILogger<RelayWorker> log) : this(cfg, log, null) { }

    /// <summary>toServer : canal vers le serveur Linkii (tests : celui du serveur de test) ; null = réseau.</summary>
    public RelayWorker(RelayConfig cfg, ILogger<RelayWorker> log, HttpMessageHandler? toServer)
    {
        this.cfg = cfg; this.log = log;
        server = toServer == null ? new HttpClient() : new HttpClient(toServer, false);
        server.Timeout = TimeSpan.FromSeconds(45);
    }
    private readonly HttpClient web = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8) }) { Timeout = TimeSpan.FromSeconds(15) };

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (cfg.Server.Length == 0 || cfg.Token.Length == 0)
        {
            log.LogError("Relais non appairé : lancez « Linkii.Relay pair CODE --server https://… » puis redémarrez-le.");
            return;
        }
        server.DefaultRequestHeaders.Add("X-Relay-Token", cfg.Token);
        server.DefaultRequestHeaders.Add("X-Relay-Machine", Environment.MachineName);
        server.DefaultRequestHeaders.Add("X-Relay-Version", Version);
        web.DefaultRequestHeaders.UserAgent.ParseAdd("LinkiiRelay/" + Version);
        log.LogInformation("Relais « {Name} » {Version} : connecté à {Server}", cfg.Name, Version, cfg.Server);
        var pause = TimeSpan.FromSeconds(2);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var res = await server.GetAsync(cfg.Server + "/api/relay/next", stop);
                if (res.StatusCode == HttpStatusCode.Unauthorized)
                {
                    log.LogError("Jeton refusé : ce relais a été supprimé ou ré-appairé dans Linkii. Appairez-le de nouveau.");
                    await Task.Delay(TimeSpan.FromMinutes(5), stop);
                    continue;
                }
                pause = TimeSpan.FromSeconds(2);
                if (res.StatusCode == HttpStatusCode.NoContent) continue;
                res.EnsureSuccessStatusCode();
                var job = await res.Content.ReadFromJsonAsync<RelayJob>(RelayConfig.Json, stop);
                if (job != null) _ = Task.Run(() => Handle(job, stop), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                log.LogWarning("Serveur injoignable ({Message}) : nouvel essai dans {Pause} s", e.Message, pause.TotalSeconds);
                try { await Task.Delay(pause, stop); } catch (OperationCanceledException) { break; }
                pause = TimeSpan.FromSeconds(Math.Min(60, pause.TotalSeconds * 2));
            }
        }
    }

    private async Task Handle(RelayJob job, CancellationToken stop)
    {
        RelayReply reply;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { reply = await Run(job, stop); }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException or ArgumentException)
        {
            reply = new RelayReply { Id = job.Id, Ok = false, Error = e is TaskCanceledException ? "Délai dépassé." : e.Message };
        }
        log.LogInformation("{Kind}/{Op} {Target} : {Result} en {Ms} ms", job.Kind, job.Op, job.Kind == "sql" ? job.Db?.Host : Host(job.Url), reply.Ok ? "ok" : reply.Error, sw.ElapsedMilliseconds);
        try { using var _ = await server.PostAsJsonAsync(cfg.Server + "/api/relay/reply", reply, RelayConfig.Json, stop); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { log.LogWarning("Résultat non remis : {Message}", e.Message); }
    }

    private static string Host(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "?";

    private async Task<RelayReply> Run(RelayJob job, CancellationToken stop)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        cts.CancelAfter(TimeSpan.FromSeconds(18));
        var ct = cts.Token;
        if (job.Kind == "sql")
        {
            var t = job.Db ?? throw new InvalidOperationException("Tâche incomplète.");
            if (!cfg.Allows(t.Host)) throw new InvalidOperationException($"Serveur « {t.Host} » non autorisé par ce relais (AllowedHosts de relay.json).");
            switch (job.Op)
            {
                case "test": return new RelayReply { Id = job.Id, Ok = true, Message = await DataSql.Test(t, ct) };
                case "discover": return new RelayReply { Id = job.Id, Ok = true, Tables = await DataSql.Discover(t, ct) };
                default:
                    var r = await DataSql.Read(t, job.Table, job.Sql, Math.Clamp(job.MaxRows, 1, 5000), ct);
                    return new RelayReply { Id = job.Id, Ok = true, Headers = r.Headers, Rows = r.Rows };
            }
        }
        if (job.Kind == "http")
        {
            if (!Uri.TryCreate(job.Url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) throw new InvalidOperationException("Adresse invalide.");
            if (!cfg.Allows(u.Host)) throw new InvalidOperationException($"Hôte « {u.Host} » non autorisé par ce relais (AllowedHosts de relay.json).");
            var login = job.Op == "login" && job.Method == "POST";   // seul POST accepté : l'appel de connexion d'une API (utilisateur, mot de passe → jeton)
            if (job.Method != "GET" && !login) throw new InvalidOperationException("Méthode non autorisée : le relais ne fait que lire.");
            using var req = new HttpRequestMessage(login ? HttpMethod.Post : HttpMethod.Get, u);
            foreach (var (k, v) in job.Headers ?? new()) req.Headers.TryAddWithoutValidation(k, v);
            if (login) req.Content = new StringContent(job.Body ?? "", System.Text.Encoding.UTF8, job.ContentType ?? "application/json");
            using var res = await web.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            await using var s = await res.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buf = new byte[16 * 1024];
            int n;
            while ((n = await s.ReadAsync(buf, ct)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > job.MaxBytes) throw new InvalidOperationException($"Réponse trop volumineuse (plus de {job.MaxBytes / 1_000_000} Mo).");
            }
            return new RelayReply { Id = job.Id, Ok = true, Status = (int)res.StatusCode, Body64 = Convert.ToBase64String(ms.ToArray()) };
        }
        throw new InvalidOperationException("Tâche inconnue.");
    }
}
