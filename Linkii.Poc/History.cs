using System.Collections.Concurrent;
using System.Text.Json;

namespace Linkii.Poc;

/// <summary>
/// Une ligne de l'historique : ce qui s'est passé (<see cref="Title"/>), où (<see cref="TargetName"/>) et qui l'a fait (<see cref="Actor"/>).
/// Trois circuits (<see cref="Channel"/>) : l'activité des membres et du système, la vie des écrans, et le diagnostic technique.
/// </summary>
public class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>activity, screens ou diag (voir <see cref="History"/>).</summary>
    public string Channel { get; set; } = History.Activity;
    /// <summary>info, ok, warn ou error (même vocabulaire que <see cref="Notifs"/>).</summary>
    public string Level { get; set; } = Notifs.Info;
    public string Cat { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Detail { get; set; }
    /// <summary>Visible aussi dans l'onglet Diagnostic (erreur de synchro, réveil non confirmé…).</summary>
    public bool Tech { get; set; }
    /// <summary>Nom de la personne ; « Système » pour un traitement automatique, « Player » pour un écran.</summary>
    public string? Actor { get; set; }
    public Guid? ActorId { get; set; }
    public string? TargetKind { get; set; }
    public Guid? TargetId { get; set; }
    public string? TargetName { get; set; }
    /// <summary>Page ouverte par le lien de la ligne (chemin relatif).</summary>
    public string? Path { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    /// <summary>Éléments du tiroir de détail (libellé, valeur), dans l'ordre.</summary>
    public List<string[]>? Facts { get; set; }
    /// <summary>Chronologie ou trace technique, une ligne par étape.</summary>
    public string? Trace { get; set; }
}

public static class History
{
    public const string Activity = "activity", Screens = "screens", Diag = "diag";

    public const string Publish = "publish", Media = "media", Members = "members", Integrations = "integrations", Account = "account", Settings = "settings",
        Offline = "offline", Sleep = "sleep", Player = "player", Battery = "battery", Sync = "sync", Emergency = "emergency", Data = "data", Approval = "approval", Login = "login", Server = "server";

    /// <summary>Conservation : l'activité et les écrans un an, le diagnostic 30 jours.</summary>
    public const int KeepDays = 365, KeepDiagDays = 30;

    public static readonly (string Id, string Label)[] ActivityCats =
    {
        (Publish, "Publications"), (Media, "Médias"), (Members, "Membres"), (Integrations, "Intégrations"), (Account, "Compte"), (Settings, "Réglages"),
    };

    public static readonly (string Id, string Label)[] ScreenCats =
    {
        (Offline, "Hors ligne"), (Sleep, "Veille / réveil"), (Player, "Player"), (Battery, "Batterie"),
    };

    public static string Label(string cat) => cat switch
    {
        Publish => "Publications", Media => "Médias", Members => "Membres", Integrations => "Intégrations", Account => "Compte", Settings => "Réglages",
        Offline => "Hors ligne", Sleep => "Veille / réveil", Player => "Player", Battery => "Batterie", Sync => "Synchronisation", Emergency => "Urgence",
        Data => "Données", Approval => "Approbations", Login => "Connexions", Server => "Serveur", _ => cat,
    };

    /// <summary>La catégorie qui range une ligne dans le filtre « Intégrations » (synchro et données en font partie).</summary>
    public static string Filter(string cat) => cat is Sync or Data ? Integrations : cat is Approval ? Publish : cat is Emergency ? Publish : cat is Login ? Members : cat;

    public static string Mark(string level) => level switch { Notifs.Ok => "✓", Notifs.Warn => "!", Notifs.Error => "×", _ => "i" };
}

/// <summary>
/// Journal d'historique : un fichier JSON Lines par organisation (history/{client}.jsonl, dans le dossier des données), hors de data.json.
/// Ne lève jamais d'exception à l'écriture : l'historique ne doit jamais empêcher l'action qu'il décrit.
/// </summary>
public class HistoryLog
{
    private readonly string dir;
    private readonly ConcurrentDictionary<Guid, object> locks = new();
    private readonly ConcurrentDictionary<Guid, DateTime> lastPrune = new();
    private readonly ILogger<HistoryLog>? log;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Une ligne vient d'être écrite : les pages ouvertes se rafraîchissent.</summary>
    public event Action<Guid>? Added;

    public HistoryLog(IConfiguration config, IWebHostEnvironment env, ILogger<HistoryLog>? log = null)
    {
        dir = Path.Combine(config["Linkii:DataDir"] ?? env.ContentRootPath, "history");
        Directory.CreateDirectory(dir);
        this.log = log;
    }

    private string FileOf(Guid clientId) => Path.Combine(dir, clientId.ToString("N") + ".jsonl");

    public void Add(Guid clientId, HistoryEntry e)
    {
        try
        {
            var line = JsonSerializer.Serialize(e, Json);
            lock (locks.GetOrAdd(clientId, _ => new object()))
            {
                File.AppendAllText(FileOf(clientId), line + "\n");
                if (DateTime.UtcNow - lastPrune.GetOrAdd(clientId, DateTime.UtcNow.AddHours(-23)) > TimeSpan.FromDays(1)) Prune(clientId);
            }
            Added?.Invoke(clientId);
        }
        catch (Exception ex) { log?.LogWarning(ex, "Historique : ligne perdue"); }
    }

    /// <summary>Écrit une ligne. <paramref name="actor"/> : null = « Système ».</summary>
    public void Record(Guid clientId, string channel, string cat, string title, string level = Notifs.Info, string? detail = null, string? actor = "Système", Guid? actorId = null,
        string? targetKind = null, Guid? targetId = null, string? targetName = null, string? path = null, bool tech = false, string? before = null, string? after = null,
        List<string[]>? facts = null, string? trace = null) =>
        Add(clientId, new HistoryEntry
        {
            Channel = channel, Cat = cat, Title = title, Level = level, Detail = detail, Actor = actor, ActorId = actorId, TargetKind = targetKind, TargetId = targetId,
            TargetName = targetName, Path = path, Tech = tech, Before = before, After = after, Facts = facts, Trace = trace,
        });

    /// <summary>Action d'une personne connectée (activité de l'espace). L'équipe Linkivi qui entre dans l'espace est nommée comme telle.</summary>
    public void By(TenantContext ctx, string cat, string title, string level = Notifs.Info, string? detail = null, string? targetKind = null, Guid? targetId = null,
        string? targetName = null, string? path = null, string? before = null, string? after = null, List<string[]>? facts = null, string channel = History.Activity)
    {
        try
        {
            if (ctx.ClientId is not { } cid) return;
            Record(cid, channel, cat, title, level, detail, ctx.IsImpersonating ? "Équipe Linkivi" : ctx.DisplayName, ctx.IsImpersonating ? null : ctx.UserId,
                targetKind, targetId, targetName, path, false, before, after, facts);
        }
        catch (Exception ex) { log?.LogWarning(ex, "Historique : ligne perdue"); }
    }

    /// <summary>Lignes écrites depuis sinceUtc, de la plus récente à la plus ancienne.</summary>
    public List<HistoryEntry> Read(Guid clientId, DateTime sinceUtc)
    {
        lock (locks.GetOrAdd(clientId, _ => new object())) return ReadUnlocked(clientId, sinceUtc).OrderByDescending(e => e.AtUtc).ToList();
    }

    private List<HistoryEntry> ReadUnlocked(Guid clientId, DateTime sinceUtc)
    {
        var list = new List<HistoryEntry>();
        var path = FileOf(clientId);
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<HistoryEntry>(line, Json);
                if (e != null && e.AtUtc >= sinceUtc) list.Add(e);
            }
            catch (JsonException) { /* ligne tronquée (arrêt pendant l'écriture) : ignorée */ }
        }
        return list;
    }

    private void Prune(Guid clientId)
    {
        lastPrune[clientId] = DateTime.UtcNow;
        var now = DateTime.UtcNow;
        var keep = ReadUnlocked(clientId, now.AddDays(-History.KeepDays))
            .Where(e => e.Channel != History.Diag || now - e.AtUtc <= TimeSpan.FromDays(History.KeepDiagDays))
            .Select(e => JsonSerializer.Serialize(e, Json)).ToList();
        File.WriteAllLines(FileOf(clientId), keep);
    }
}

/// <summary>
/// Enveloppe du bus d'événements : ce qui part vers les webhooks (urgence, synchro des données, écran qui applique une publication) entre aussi dans l'historique.
/// </summary>
public class HistoryEvents(IEventPublisher inner, HistoryLog log) : IEventPublisher
{
    public void Publish(Guid clientId, string type, object? data = null)
    {
        inner.Publish(clientId, type, data);
        try
        {
            var el = data == null ? default : JsonSerializer.SerializeToElement(data);
            string? Str(params string[] path)
            {
                var cur = el;
                foreach (var p in path) { if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out cur)) return null; }
                return cur.ValueKind == JsonValueKind.String ? cur.GetString() : cur.ValueKind == JsonValueKind.Null ? null : cur.ToString();
            }
            switch (type)
            {
                case "screen.applied":
                    log.Record(clientId, History.Screens, History.Player, $"« {Str("screen", "name")} » a appliqué la publication", Notifs.Ok, $"Version {Str("version")}", "Player",
                        targetKind: "screen", targetId: Guid.TryParse(Str("screen", "id"), out var sid) ? sid : null, targetName: Str("screen", "name"));
                    break;
                case "data.failed":
                    log.Record(clientId, History.Diag, History.Data, $"Source de données « {Str("dataset", "name")} » : lecture impossible", Notifs.Error, Str("message"), tech: true,
                        targetKind: "dataset", targetName: Str("dataset", "name"), path: "datasources");
                    break;
                case "data.recovered":
                    log.Record(clientId, History.Diag, History.Data, $"Source de données « {Str("dataset", "name")} » rétablie", Notifs.Ok, Str("rows") is { } r ? $"{r} lignes lues" : null, tech: true,
                        targetKind: "dataset", targetName: Str("dataset", "name"), path: "datasources");
                    break;
                case "emergency.started":
                    log.Record(clientId, History.Activity, History.Emergency, "Urgence déclenchée", Notifs.Warn, Str("targeted") is { } t ? $"{t} écran(s) ciblé(s)" : null, null,
                        targetKind: "emergency", targetName: Str("emergency", "name"));
                    break;
                case "emergency.ended":
                    log.Record(clientId, History.Activity, History.Emergency, "Urgence terminée", Notifs.Ok,
                        Str("durationMinutes") is { } m ? $"Durée : {m} min" : null, Str("endedBy") ?? "Système", targetKind: "emergency", targetName: Str("emergency", "name"));
                    break;
            }
        }
        catch (Exception) { /* l'historique ne bloque jamais l'événement */ }
    }
}

/// <summary>
/// Vie des écrans pour l'historique : hors ligne et retour, mise en veille et réveil prévus, et surtout le réveil constaté ou non (l'écran a-t-il repris contact ?).
/// Passe chaque minute (voir <see cref="ScreenWatchWorker"/>), même si l'organisation n'a pas activé d'alertes : l'historique ne dépend pas des Réglages d'alertes.
/// </summary>
public static class ScreenHistory
{
    /// <summary>Un écran qui sort de veille a ce temps pour reprendre contact (même délai de grâce que l'alerte « hors ligne »).</summary>
    public static readonly TimeSpan WakeGrace = TimeSpan.FromMinutes(5);
    /// <summary>Après le démarrage du serveur, les écrans n'ont pas encore eu le temps de se reconnecter : aucune panne n'est notée.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);

    /// <summary>À appeler dans un Write (marque les écrans) ; les lignes sont écrites ensuite, hors du verrou.</summary>
    public static List<(Guid Client, HistoryEntry Entry)> Plan(Db db, DateTime now, DateTime startedUtc)
    {
        var res = new List<(Guid, HistoryEntry)>();
        var settling = now - startedUtc < Settle;
        foreach (var c in db.Clients.Where(c => !c.IsCut))
        {
            foreach (var s in db.Screens.Where(s => s.ClientId == c.Id && s.Token != null))
            {
                var area = db.Areas.FirstOrDefault(a => a.Id == s.AreaId);
                var inWall = Helpers.Wall(db.Areas, s) != null;
                var online = ScreenControl.Online(s, now);
                var asleep = Sleep.State(c, area, s, inWall, now).Asleep;
                var tz = TimeZoneOf(Sleep.TimeZone(c, area));
                string Hm(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, tz).ToString("HH:mm");
                void Add(string channel, string cat, string title, string level, string? detail = null, bool tech = false, List<string[]>? facts = null, string? trace = null) =>
                    res.Add((c.Id, new HistoryEntry
                    {
                        AtUtc = now, Channel = channel, Cat = cat, Title = title, Level = level, Detail = detail, Tech = tech, Actor = "Système", Facts = facts, Trace = trace,
                        TargetKind = "screen", TargetId = s.Id, TargetName = s.Name,
                    }));

                if (s.HistOnline == null && s.HistAsleep == null)
                {   // premier passage : on note l'état sans rien écrire
                    if (s.LastSeenUtc != null) { s.HistOnline = online; s.HistAsleep = asleep; }
                    continue;
                }

                // veille : plage de l'aire ou de l'écran, mise en veille ou réveil manuels
                if (s.HistAsleep != asleep)
                {
                    var why = s.ForcedSleep ? "mise en veille manuelle" : Sleep.Woken(s, now) ? "réveil manuel" : Sleep.Mode(s) == Sleep.Own ? "plage de l'écran" : $"plage de l'aire « {area?.Name} »";
                    if (asleep)
                    {
                        Add(History.Screens, History.Sleep, $"« {s.Name} » mis en veille", Notifs.Info, $"Prévu à {Hm(now)} ({why})");
                        s.HistWakeUtc = null;
                    }
                    else
                    {
                        Add(History.Screens, History.Sleep, $"« {s.Name} » : réveil prévu", Notifs.Info, $"Prévu à {Hm(now)} ({why}) ; le player a {(int)WakeGrace.TotalMinutes} min pour reprendre contact");
                        s.HistWakeUtc = now; s.HistWakeNote = why;
                    }
                    s.HistAsleep = asleep;
                }

                // réveil attendu : l'écran a-t-il repris contact ?
                if (s.HistWakeUtc is { } wake)
                {
                    if (online)
                    {
                        var late = s.LastSeenUtc is { } seen && seen > wake ? (int)(seen - wake).TotalSeconds : 0;
                        Add(History.Screens, History.Sleep, $"« {s.Name} » : réveil confirmé", Notifs.Ok,
                            late > 0 ? $"Le player a repris contact {late} s après l'heure prévue" : "L'écran était déjà actif à l'heure prévue");
                        s.HistWakeUtc = null;
                    }
                    else if (now - wake > WakeGrace)
                    {
                        var last = s.LastSeenUtc is { } l ? Hm(l) : "jamais";
                        Add(History.Screens, History.Sleep, $"« {s.Name} » : réveil non confirmé", Notifs.Error,
                            $"Attendu à {Hm(wake)} ({s.HistWakeNote}) ; aucun contact du player depuis {last}", tech: true,
                            facts: new()
                            {
                                new[] { "Attendu", $"Éveillé à {Hm(wake)}" },
                                new[] { "Constaté", $"Aucun contact après {(int)WakeGrace.TotalMinutes} min" },
                                new[] { "Plage utilisée", s.HistWakeNote ?? "" },
                                new[] { "Dernier contact", last },
                                new[] { "Appareil", s.ScreenType ?? "—" },
                            },
                            trace: $"{Hm(wake)}  réveil prévu ({s.HistWakeNote})\n{Hm(now)}  aucun contact depuis {last}\nÀ vérifier : mise en veille matérielle (droit administrateur de l'appareil sur Android), veille de l'écran lui-même, réseau.");
                        s.HistWakeUtc = null;
                    }
                }

                // hors ligne et retour (une panne pendant la veille est normale : rien n'est noté)
                if (!settling && s.HistOnline != online)
                {
                    if (!online)
                    {
                        var sleeping = ScreenAlerts.IsSleeping(db, c, s, now);
                        s.HistOfflineUtc = sleeping ? null : s.LastSeenUtc ?? now;
                        if (!sleeping)
                            Add(History.Screens, History.Offline, $"« {s.Name} » hors ligne", Notifs.Warn, s.LastSeenUtc is { } ls ? $"Dernier contact à {Hm(ls)}" : null, tech: false);
                    }
                    else if (s.HistOfflineUtc is { } since)
                    {
                        var min = Math.Max(1, (int)(now - since).TotalMinutes);
                        Add(History.Screens, History.Offline, $"« {s.Name} » de retour en ligne", Notifs.Ok, $"Hors ligne pendant {(min >= 60 ? $"{min / 60} h {min % 60:00}" : $"{min} min")}");
                        s.HistOfflineUtc = null;
                    }
                    s.HistOnline = online;
                }
            }
        }
        return res;
    }

    private static TimeZoneInfo TimeZoneOf(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}
