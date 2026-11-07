using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Linkii.Poc.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Linkii.Poc;

/// <summary>
/// Home Assistant en temps réel, pour les organisations dont au moins un écran affiche un widget Home Assistant.
/// En direct : une connexion WebSocket par organisation (/api/websocket : états, puis chaque changement d'état) ; si l'instance refuse le WebSocket
/// (proxy), ou par le relais Linkii : les états sont relus toutes les 5 secondes. Dès qu'une entité affichée change, les écrans concernés
/// sont prévenus par SignalR (« DataChanged ») et relisent leurs données aussitôt ; leur sondage reste une sécurité (60 s).
/// Suit aussi la santé de l'instance (erreur enregistrée dans le compte, alerte « Applications » après 5 minutes de panne, bonne nouvelle au retour)
/// et relit la liste des entités une fois par jour (entités utilisées mais introuvables : signalées dans le back-office et par une alerte).
/// </summary>
public class HomeAssistantLive(JsonStore store, HomeAssistantClient ha, IHubContext<ScreenHub> hub, AlertService alerts, ILogger<HomeAssistantLive> log) : BackgroundService
{
    public static readonly TimeSpan AlertAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan EntitiesEvery = TimeSpan.FromHours(24);
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(5);

    private sealed class Entry
    {
        public string Key = "";
        public readonly CancellationTokenSource Cts = new();
        public readonly ConcurrentDictionary<string, HaState> States = new();
        public readonly ConcurrentDictionary<string, string> Signatures = new();
        public volatile bool Ready;
        public volatile bool Socket;
        public volatile Dictionary<string, List<(Guid Screen, string Item)>> Uses = new();
        public readonly ConcurrentDictionary<string, byte> Changed = new();
        public readonly SemaphoreSlim Wake = new(0, 1);
        public int Flushing;
    }

    private readonly ConcurrentDictionary<Guid, Entry> entries = new();
    private readonly ConcurrentDictionary<Guid, (bool Ok, string Error)> health = new();
    private readonly ConcurrentDictionary<Guid, DateTime> lastListRefresh = new();

    /// <summary>Clé d'un compte : une connexion temps réel est rouverte quand l'adresse, l'accès ou les identifiants changent.</summary>
    public static string AccountKey(HomeAssistantAccount a) => string.Join("|", a.Url, a.Via, a.RelayId, a.AuthMode, a.User, a.Secret);

    /// <summary>Connexion temps réel ouverte et à jour pour ce compte ?</summary>
    public bool IsLive(Guid clientId) => entries.TryGetValue(clientId, out var e) && e.Ready;

    /// <summary>« Temps réel » (WebSocket), « Relevé toutes les 5 s » ou rien (aucun écran n'affiche Home Assistant).</summary>
    public string? Mode(Guid clientId) => entries.TryGetValue(clientId, out var e) && e.Ready ? e.Socket ? "Temps réel" : "Relevé toutes les 5 s" : null;

    /// <summary>États pour les widgets : ceux de la connexion temps réel, sinon lus par l'API (et la santé de l'instance est notée).</summary>
    public async Task<(IReadOnlyDictionary<string, HaState> States, bool Live)> States(Guid clientId, HomeAssistantAccount a)
    {
        if (entries.TryGetValue(clientId, out var e) && e.Ready && e.Key == AccountKey(a)) return (e.States, true);
        try
        {
            var s = await ha.States(ha.Conn(clientId, a));
            ReportOk(clientId);
            return (s, false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            var msg = Readable(ex);
            ReportFail(clientId, msg);
            throw new InvalidOperationException(msg);
        }
    }

    /// <summary>Une commande vient d'être envoyée : par le relais, les états sont relus tout de suite (le WebSocket reçoit le changement seul).</summary>
    public void Nudge(Guid clientId)
    {
        if (entries.TryGetValue(clientId, out var e) && !e.Socket) { try { e.Wake.Release(); } catch (SemaphoreFullException) { } }
    }

    public static string Readable(Exception ex) => ex switch
    {
        TaskCanceledException => "Home Assistant n'a pas répondu à temps.",
        HttpRequestException h when h.Message.Contains("réseau interne") => "Adresse du réseau local : choisissez l'accès « Par le relais Linkii ».",
        HttpRequestException => "Home Assistant injoignable : " + ex.Message,
        _ => ex.Message,
    };

    // ---------- santé de l'instance ----------

    public void ReportOk(Guid clientId)
    {
        if (health.TryGetValue(clientId, out var h) && h.Ok) return;
        health[clientId] = (true, "");
        var wasAlerted = store.Write(db =>
        {
            if (db.Clients.FirstOrDefault(t => t.Id == clientId)?.HomeAssistant is not { } a) return false;
            var alerted = a.AlertSent;
            a.LastError = ""; a.FailingSinceUtc = null; a.LastOkUtc = DateTime.UtcNow; a.AlertSent = false;
            return alerted;
        });
        if (wasAlerted)
            _ = alerts.Raise(clientId, Alerts.Integrations, "Home Assistant de nouveau joignable", "Les widgets Home Assistant des écrans affichent de nouveau les valeurs en direct.", "/applications", severity: Notifs.Ok, good: true);
    }

    public void ReportFail(Guid clientId, string error)
    {
        if (health.TryGetValue(clientId, out var h) && !h.Ok && h.Error == error) return;
        health[clientId] = (false, error);
        store.Write(db =>
        {
            if (db.Clients.FirstOrDefault(t => t.Id == clientId)?.HomeAssistant is not { } a) return;
            a.LastError = error;
            a.FailingSinceUtc ??= DateTime.UtcNow;
        });
    }

    /// <summary>
    /// Relit la liste des entités du compte (« Actualiser », et une fois par jour) et note celles qu'un widget ou un jeu de données utilise encore
    /// mais que l'instance n'a plus (supprimées ou renommées). Rend le compte enregistré.
    /// </summary>
    public async Task<HomeAssistantAccount> RefreshEntities(Guid clientId, CancellationToken ct = default)
    {
        var a = store.Read(db => db.Clients.FirstOrDefault(t => t.Id == clientId)?.HomeAssistant?.Copy()) ?? throw new InvalidOperationException("Compte Home Assistant non connecté.");
        HomeAssistantAccount fresh;
        try { fresh = await ha.Refresh(clientId, a, ct); }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            ReportFail(clientId, Readable(ex));
            throw new InvalidOperationException(Readable(ex));
        }
        lastListRefresh[clientId] = DateTime.UtcNow;
        var known = fresh.Entities.Select(e => e.Id).ToHashSet();
        var (saved, added) = store.Write(db =>
        {
            var t = db.Clients.FirstOrDefault(x => x.Id == clientId);
            if (t?.HomeAssistant is not { } cur) return ((HomeAssistantAccount?)null, new List<string>());
            var missing = Used(db, t).Where(id => !known.Contains(id)).OrderBy(x => x).ToList();
            var newOnes = missing.Except(cur.Missing).ToList();
            var off = cur.Entities.Where(e => e.Off).Select(e => e.Id).ToHashSet();   // l'interrupteur des entités survit à l'actualisation
            foreach (var e in fresh.Entities) e.Off = off.Contains(e.Id);
            cur.Entities = fresh.Entities; cur.EntitiesUtc = fresh.EntitiesUtc; cur.Missing = missing;
            cur.LastError = ""; cur.FailingSinceUtc = null; cur.LastOkUtc = DateTime.UtcNow;
            return (cur.Copy(), newOnes);
        });
        health[clientId] = (true, "");
        if (added.Count > 0)
            _ = alerts.Raise(clientId, Alerts.Integrations, added.Count == 1 ? "Une entité Home Assistant est introuvable" : $"{added.Count} entités Home Assistant sont introuvables",
                $"{string.Join(", ", added.Take(5))}{(added.Count > 5 ? "…" : "")} : supprimée(s) ou renommée(s) dans Home Assistant. Les widgets qui les affichent les ignorent : choisissez-en d'autres.",
                "/applications", severity: Notifs.Warn, dedupe: "ha-missing:" + string.Join(',', added));
        return saved ?? fresh;
    }

    /// <summary>Entités utilisées par l'organisation : widgets (posés ou publiés), contenus Home Assistant, jeux de données Home Assistant.</summary>
    public static HashSet<string> Used(Db db, Tenant t)
    {
        var used = new HashSet<string>();
        void Add(string? appId, Dictionary<string, string> settings) { foreach (var id in HomeAssistantRules.EntitiesOf(t, appId, settings)) used.Add(id); }
        foreach (var m in db.Media.Where(m => m.ClientId == t.Id && HomeAssistantRules.IsHaApp(m.AppId))) Add(m.AppId, m.Settings);
        foreach (var m in db.Media.Where(m => m.ClientId == t.Id && m.AppId == Compositions.AppId)) foreach (var id in Compositions.HaEntities(m.Settings.GetValueOrDefault("doc"))) used.Add(id);
        foreach (var s in db.Screens.Where(s => s.ClientId == t.Id))
        {
            foreach (var w in s.Widgets.Where(w => HomeAssistantRules.IsHaApp(w.AppId))) Add(w.AppId, w.Settings);
            foreach (var i in PlayerFeed.DataItems(db, s).Where(i => HomeAssistantRules.IsHaApp(i.AppId))) Add(i.AppId, i.Settings);
        }
        var haConns = t.DataConnections.Where(c => c.Kind == DataKinds.Ha).Select(c => c.Id).ToHashSet();
        foreach (var ds in t.DataSets.Where(d => haConns.Contains(d.ConnectionId))) foreach (var id in HomeAssistantRules.ParseDataPath(ds.Path).Ids) used.Add(id);
        return used;
    }

    /// <summary>Entité → écrans et éléments publiés qui l'affichent (à prévenir quand elle change).</summary>
    public static Dictionary<string, List<(Guid Screen, string Item)>> Uses(Db db, Guid clientId)
    {
        var map = new Dictionary<string, List<(Guid, string)>>();
        var tenant = db.Clients.FirstOrDefault(c => c.Id == clientId);
        foreach (var s in db.Screens.Where(s => s.ClientId == clientId && s.Token != null))
            foreach (var i in PlayerFeed.DataItems(db, s))
            {
                // widget : ses entités, relues par /api/data/{élément} ; composition : celles de sa source Home Assistant, relues par /api/data/{élément}/{source}
                var ids = HomeAssistantRules.IsHaApp(i.AppId) && tenant != null ? HomeAssistantRules.EntitiesOf(tenant, i.AppId, i.Settings)
                    : i.AppId == Compositions.AppId ? HomeAssistantRules.ParseEntities(i.Settings.GetValueOrDefault("ha")) : new List<string>();
                if (ids.Count == 0) continue;
                var item = i.AppId == Compositions.AppId ? i.Id.ToString() : i.DataId ?? i.Id.ToString();
                foreach (var id in ids)
                {
                    if (!map.TryGetValue(id, out var l)) map[id] = l = new();
                    if (!l.Contains((s.Id, item))) l.Add((s.Id, item));
                }
            }
        return map;
    }

    // ---------- boucle principale ----------

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try { await Reconcile(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogWarning(e, "Home Assistant : suivi des connexions"); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stop); } catch (OperationCanceledException) { break; }
        }
        foreach (var e in entries.Values) e.Cts.Cancel();
    }

    private async Task Reconcile(CancellationToken stop)
    {
        var now = DateTime.UtcNow;
        var clients = store.Read(db => db.Clients.Where(t => t.HomeAssistant is { Url.Length: > 0 } && !t.Suspended)
            .Select(t => (t.Id, Account: t.HomeAssistant!.Copy(), Uses: Uses(db, t.Id))).ToList());
        var ids = clients.Select(c => c.Id).ToHashSet();
        foreach (var (id, e) in entries.ToList())
            if (!ids.Contains(id)) Stop(id, e);

        foreach (var (id, account, uses) in clients)
        {
            // connexion temps réel : seulement si un écran affiche une entité de cette instance
            if (uses.Count == 0) { if (entries.TryGetValue(id, out var idle)) Stop(id, idle); }
            else
            {
                var key = AccountKey(account);
                if (entries.TryGetValue(id, out var cur) && cur.Key != key) { Stop(id, cur); cur = null; }
                if (cur == null)
                {
                    cur = new Entry { Key = key, Uses = uses };
                    entries[id] = cur;
                    var entry = cur;
                    _ = Task.Run(() => Run(id, account, entry, entry.Cts.Token));
                }
                else cur.Uses = uses;
            }

            // panne de plus de 5 minutes : une alerte (une fois par panne)
            if (account.FailingSinceUtc is { } since && now - since > AlertAfter && !account.AlertSent)
            {
                store.Write(db => { if (db.Clients.FirstOrDefault(t => t.Id == id)?.HomeAssistant is { } a) a.AlertSent = true; });
                _ = alerts.Raise(id, Alerts.Integrations, "Home Assistant injoignable",
                    $"{account.LastError} Les écrans gardent la dernière valeur connue. Vérifiez l'instance{(account.Via == "relay" ? " et le relais Linkii" : "")}, puis Applications › Home Assistant.",
                    "/applications", severity: Notifs.Error, dedupe: "ha-down");
            }

            // liste des entités relue une fois par jour
            if ((account.EntitiesUtc is not { } last || now - last > EntitiesEvery) && (!lastListRefresh.TryGetValue(id, out var tried) || now - tried > TimeSpan.FromHours(1)))
            {
                lastListRefresh[id] = now;
                try { await RefreshEntities(id, stop); }
                catch (InvalidOperationException) { }   // panne notée par RefreshEntities
            }
        }
    }

    private void Stop(Guid id, Entry e)
    {
        e.Cts.Cancel();
        entries.TryRemove(new KeyValuePair<Guid, Entry>(id, e));
    }

    /// <summary>Une organisation : WebSocket en direct ; relevé toutes les 5 s par le relais, ou si l'instance refuse le WebSocket (10 minutes, puis nouvel essai).</summary>
    private async Task Run(Guid clientId, HomeAssistantAccount a, Entry e, CancellationToken ct)
    {
        var pause = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            if (a.Via != "relay")
            {
                try
                {
                    await Socket(clientId, a, e, ct);
                    pause = TimeSpan.FromSeconds(5);
                    continue;   // fermée par l'instance (redémarrage) : on se reconnecte
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    e.Ready = false; e.Socket = false;
                    log.LogInformation("Home Assistant ({Client}) : WebSocket indisponible ({Message}), relevé par l'API", clientId, ex.Message);
                }
            }
            var until = a.Via == "relay" ? DateTime.MaxValue : DateTime.UtcNow.AddMinutes(10);
            var ok = await Poll(clientId, a, e, until, ct);
            if (ct.IsCancellationRequested) return;
            if (!ok)
            {
                try { await Task.Delay(pause, ct); } catch (OperationCanceledException) { return; }
                pause = TimeSpan.FromSeconds(Math.Min(120, pause.TotalSeconds * 2));
            }
        }
    }

    /// <summary>Relevé des états jusqu'à until ; rend false à la première erreur (l'instance est notée en panne).</summary>
    private async Task<bool> Poll(Guid clientId, HomeAssistantAccount a, Entry e, DateTime until, CancellationToken ct)
    {
        var conn = ha.Conn(clientId, a);
        while (!ct.IsCancellationRequested && DateTime.UtcNow < until)
        {
            try
            {
                var map = await ha.States(conn, fresh: true, ct);
                Apply(e, map.Values, replace: true);
                e.Ready = true; e.Socket = false;
                ReportOk(clientId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
            catch (Exception ex)
            {
                e.Ready = false;
                ReportFail(clientId, Readable(ex));
                return false;
            }
            try { await e.Wake.WaitAsync(PollEvery, ct); } catch (OperationCanceledException) { return false; }
        }
        return true;
    }

    /// <summary>WebSocket de Home Assistant : authentification, états, puis abonnement aux changements d'état. Rend quand l'instance ferme la connexion.</summary>
    private async Task Socket(Guid clientId, HomeAssistantAccount a, Entry e, CancellationToken ct)
    {
        var conn = ha.Conn(clientId, a);
        var token = await ha.AccessToken(conn, ct);
        var uri = new Uri((conn.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws") + conn.Url[conn.Url.IndexOf(':')..] + "/api/websocket");
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        using var invoker = new HttpMessageInvoker(new SocketsHttpHandler { ConnectCallback = SafeHttp.Connect, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(8) });
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(uri, invoker, connect.Token);
        }
        using (var first = await Receive(ws, ct)) if (Type(first) != "auth_required") throw new InvalidOperationException("Réponse WebSocket inattendue.");
        await SendJson(ws, new { type = "auth", access_token = token }, ct);
        using (var auth = await Receive(ws, ct))
            if (Type(auth) != "auth_ok") throw new InvalidOperationException("Jeton refusé par Home Assistant (WebSocket).");
        await SendJson(ws, new { id = 1, type = "get_states" }, ct);
        await SendJson(ws, new { id = 2, type = "subscribe_events", event_type = "state_changed" }, ct);
        while (!ct.IsCancellationRequested)
        {
            using var msg = await Receive(ws, ct);
            if (msg == null) return;   // fermée
            var root = msg.RootElement;
            var type = Type(msg);
            if (type == "result" && root.TryGetProperty("id", out var rid) && rid.GetInt32() == 1 && root.TryGetProperty("result", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                Apply(e, list.EnumerateArray().Select(HaState.Parse).OfType<HaState>(), replace: true);
                e.Ready = true; e.Socket = true;
                ReportOk(clientId);
            }
            else if (type == "event" && root.TryGetProperty("event", out var ev) && ev.TryGetProperty("data", out var data))
            {
                var id = data.TryGetProperty("entity_id", out var ide) ? ide.GetString() : null;
                if (id == null || !HomeAssistantRules.IsSupported(id)) continue;
                if (data.TryGetProperty("new_state", out var ns) && HaState.Parse(ns) is { } s) Apply(e, new[] { s }, replace: false);
                else if (e.States.TryRemove(id, out _)) { e.Signatures.TryRemove(id, out _); Mark(e, id); }
            }
        }
    }

    /// <summary>Nouveaux états : ceux qui changent pour une entité affichée préviennent les écrans.</summary>
    private void Apply(Entry e, IEnumerable<HaState> states, bool replace)
    {
        var seen = replace ? new HashSet<string>() : null;
        foreach (var s in states)
        {
            if (!HomeAssistantRules.IsSupported(s.Id)) continue;
            seen?.Add(s.Id);
            var sig = s.State + "|" + string.Join(";", s.Attrs.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value.GetRawText())) + "|" + s.Name;
            e.States[s.Id] = s;
            if (e.Signatures.TryGetValue(s.Id, out var old) && old == sig) continue;
            e.Signatures[s.Id] = sig;
            Mark(e, s.Id);
        }
        if (seen != null)
            foreach (var gone in e.States.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                e.States.TryRemove(gone, out _); e.Signatures.TryRemove(gone, out _);
                Mark(e, gone);
            }
    }

    private void Mark(Entry e, string id)
    {
        if (!e.Uses.ContainsKey(id)) return;
        e.Changed[id] = 0;
        if (Interlocked.Exchange(ref e.Flushing, 1) == 0) _ = Flush(e);
    }

    /// <summary>Regroupe les changements de 300 ms et prévient chaque écran une fois, avec les éléments à relire.</summary>
    private async Task Flush(Entry e)
    {
        try
        {
            await Task.Delay(300);
            Interlocked.Exchange(ref e.Flushing, 0);
            var ids = e.Changed.Keys.ToList();
            foreach (var id in ids) e.Changed.TryRemove(id, out _);
            var uses = e.Uses;
            foreach (var g in ids.SelectMany(id => uses.GetValueOrDefault(id) ?? new()).GroupBy(x => x.Screen))
                await hub.Clients.Group(ScreenHub.Group(g.Key)).SendAsync("DataChanged", g.Select(x => x.Item).Distinct().ToArray());
        }
        catch (Exception ex) { log.LogDebug(ex, "Home Assistant : envoi aux écrans"); }
    }

    private static string? Type(JsonDocument? d) => d != null && d.RootElement.ValueKind == JsonValueKind.Object && d.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

    private static Task SendJson(ClientWebSocket ws, object o, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)), WebSocketMessageType.Text, true, ct);

    /// <summary>Un message complet (32 Mo au plus : la liste des états d'une grosse instance) ; null si la connexion est fermée.</summary>
    private static async Task<JsonDocument?> Receive(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (ms.Length > 32_000_000) throw new InvalidOperationException("Message WebSocket trop volumineux.");
            if (r.EndOfMessage) break;
        }
        return JsonDocument.Parse(ms.ToArray());
    }
}
