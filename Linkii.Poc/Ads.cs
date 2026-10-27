using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Linkii.Poc;

/// <summary>
/// Campagne publicitaire (DEV-POC §45) : créée par l'administration Linkivi, qui joue le rôle de régie. Tarif et part du propriétaire propres à la campagne.
/// Deux modes : <see cref="AdModes.Between"/> (le spot s'intercale entre deux contenus) ou <see cref="AdModes.Filler"/> (« à temps perdu » : seulement
/// quand l'écran n'a rien d'autre à afficher, sans volume garanti). Les spots ne sont jamais dans les listes de lecture ni dans le planning.
/// </summary>
public class AdCampaign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Advertiser { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = AdCategories.Other;
    /// <summary>Période de diffusion, dates (aaaa-mm-jj) dans le fuseau de l'écran, incluses.</summary>
    public string Start { get; set; } = "";
    public string End { get; set; } = "";
    public string Mode { get; set; } = AdModes.Between;
    /// <summary>Prix d'un passage complet, en CHF, payé par l'annonceur.</summary>
    public decimal PricePerPlay { get; set; } = 0.03m;
    /// <summary>Part reversée au propriétaire de l'écran, en %.</summary>
    public int OwnerSharePct { get; set; } = 50;
    /// <summary>Plafond de passages complets de la campagne (0 : aucun). Atteint, les spots sont retirés des écrans.</summary>
    public int MaxPlays { get; set; }
    public int MaxPerDayPerScreen { get; set; } = 120;
    /// <summary>Mode intercalé : un spot tous les N contenus, ou toutes les N minutes si <see cref="EveryMinutes"/> &gt; 0.</summary>
    public int EveryItems { get; set; } = Ads.DefaultEveryItems;
    public int EveryMinutes { get; set; }
    public int MaxPerHour { get; set; } = Ads.DefaultMaxPerHour;
    /// <summary>Délai de réponse du propriétaire ; sans réponse, un spot qui respecte ses exclusions est diffusé.</summary>
    public int ValidationHours { get; set; } = Ads.DefaultValidationHours;
    /// <summary>Tous les comptes Pro qui acceptent la publicité, sinon seulement <see cref="Clients"/>.</summary>
    public bool AllClients { get; set; } = true;
    public List<Guid> Clients { get; set; } = new();
    /// <summary>Arrêtée par l'administration : plus diffusée, rien n'est effacé.</summary>
    public bool Stopped { get; set; }
    public List<AdSpot> Spots { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Un spot : pleine page, un visuel paysage 16:9 et/ou un visuel portrait 9:16 (image ou vidéo sans son), durée fixe.</summary>
public class AdSpot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int DurationSec { get; set; } = 15;
    public string? Landscape { get; set; }       // fichier dans le dossier ads
    public string? LandscapeType { get; set; }   // image | video
    public string? Portrait { get; set; }
    public string? PortraitType { get; set; }
    /// <summary>Le délai de réponse des propriétaires part de cette date (ou de l'activation de la publicité, si elle est plus récente).</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public bool Ready => Landscape != null || Portrait != null;
}

/// <summary>Réponse d'un propriétaire à un spot.</summary>
public class AdDecision
{
    public Guid ClientId { get; set; }
    public Guid SpotId { get; set; }
    public bool Accepted { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string By { get; set; } = "";
}

/// <summary>Statut du relevé mensuel d'un compte : validé, puis payé (le paiement se fait hors Linkivi).</summary>
public class AdStatement
{
    public Guid ClientId { get; set; }
    public string Month { get; set; } = "";   // aaaa-mm
    public string Status { get; set; } = AdStatementStates.Validated;
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

public static class AdStatementStates
{
    public const string Running = "running", ToValidate = "tovalidate", Validated = "validated", Paid = "paid";
    public static string Label(string s) => s switch { Running => "En cours", ToValidate => "À valider", Validated => "Validé", Paid => "Payé", _ => s };
    public static string Css(string s) => s switch { Paid or Validated => "ok", ToValidate => "warn", _ => "" };
}

public static class AdModes
{
    public const string Between = "between", Filler = "filler";
    public static string Label(string m) => m == Filler ? "À temps perdu" : "Intercalé";
}

public static class AdCategories
{
    public const string Other = "other";
    public static readonly (string Id, string Label)[] All =
    {
        ("food", "Restauration"), ("transport", "Transport"), ("bank", "Banque, assurance"), ("events", "Événementiel"), ("retail", "Commerce"),
        ("culture", "Culture, loisirs"), ("health", "Santé"), ("alcohol", "Alcool"), ("gambling", "Jeux d'argent"), ("politics", "Politique"), (Other, "Autre")
    };
    public static string Label(string id) => All.FirstOrDefault(c => c.Id == id).Label ?? id;
}

/// <summary>État d'un spot pour un compte : à valider (avec l'échéance), accepté, refusé, ou accepté sans réponse à l'échéance.</summary>
public record AdSpotState(AdCampaign Campaign, AdSpot Spot, string State, DateTime Deadline)
{
    public const string Pending = "pending", Accepted = "accepted", Refused = "refused", Auto = "auto", Excluded = "excluded";
    public bool Plays => State is Accepted or Auto;
}

/// <summary>Règles de la publicité : quels spots un compte doit valider, lesquels un écran diffuse, ce que reçoit le player.</summary>
public static class Ads
{
    public const int DefaultEveryItems = 4, DefaultMaxPerHour = 6, DefaultValidationHours = 72, ReminderHours = 24;
    public static readonly int[] Durations = { 10, 15, 20, 30 };
    public static readonly int[] ValidationChoices = { 24, 72, 168 };
    public const string Tag = "Publicité";
    public static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-CH");

    /// <summary>Le compte peut recevoir de la publicité : licence Pro, réglage activé, compte ni suspendu ni bloqué.</summary>
    public static bool On(Tenant t) => t.IsGrowth && t.AdsOn && !t.IsCut;

    public static DateTime? Day(string? iso) => DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>La campagne vise ce compte et n'est pas terminée (une campagne programmée est déjà à valider).</summary>
    public static bool Targets(AdCampaign c, Tenant t, DateTime now) =>
        !c.Stopped && (c.AllClients || c.Clients.Contains(t.Id)) && (Day(c.End) is not { } end || end.AddDays(2) > now);

    /// <summary>Spots de toutes les campagnes qui visent le compte, avec leur état (les catégories refusées sont écartées d'office).</summary>
    public static List<AdSpotState> States(Db db, Tenant t, DateTime now)
    {
        var list = new List<AdSpotState>();
        if (!On(t)) return list;
        var since = t.AdsSinceUtc ?? now;
        foreach (var c in db.AdCampaigns.Where(c => Targets(c, t, now)))
            foreach (var s in c.Spots.Where(s => s.Ready))
            {
                var deadline = (s.CreatedUtc > since ? s.CreatedUtc : since).AddHours(Math.Clamp(c.ValidationHours, 1, 24 * 30));
                var d = db.AdDecisions.FirstOrDefault(x => x.ClientId == t.Id && x.SpotId == s.Id);
                var state = t.AdsExcluded.Contains(c.Category) ? AdSpotState.Excluded
                    : d != null ? (d.Accepted ? AdSpotState.Accepted : AdSpotState.Refused)
                    : deadline <= now ? AdSpotState.Auto : AdSpotState.Pending;
                list.Add(new AdSpotState(c, s, state, deadline));
            }
        return list;
    }

    /// <summary>Nombre de spots à valider (pastille du menu Réglages et de la rubrique Publicité).</summary>
    public static int PendingCount(Db db, Tenant t, DateTime now) => States(db, t, now).Count(x => x.State == AdSpotState.Pending);

    /// <summary>
    /// L'écran peut diffuser de la pub : compte qui l'accepte, aire autorisée, écran non exclu, pleine page (pas découpé en zones) et hors d'un mur.
    /// </summary>
    public static bool ScreenAllowed(Db db, Tenant t, Screen s) =>
        On(t) && s.Token != null && (t.AdsAllAreas || t.AdsAreas.Contains(s.AreaId)) && !t.AdsScreensOff.Contains(s.Id)
        && ScreenFits(db, s);

    /// <summary>Écran plein (découpage publié « plein écran ») et hors d'un mur : les seuls où un spot pleine page a sa place (décision 7).</summary>
    public static bool ScreenFits(Db db, Screen s) =>
        (s.PublishedVersion > 0 ? s.PublishedLayout : s.Layout) == ScreenLayouts.Full && Helpers.Wall(db.Areas, s) == null;

    /// <summary>Format du visuel pour l'écran : celui de son orientation, sinon l'autre (centré sur fond flouté, jamais déformé ni recadré).</summary>
    public static (string Url, string Type, string Format, bool Approx)? Visual(AdSpot spot, Screen s)
    {
        var portrait = s.Orientation == "portrait";
        var (file, type, format) = portrait
            ? (spot.Portrait ?? spot.Landscape, spot.Portrait != null ? spot.PortraitType : spot.LandscapeType, spot.Portrait != null ? "portrait" : "landscape")
            : (spot.Landscape ?? spot.Portrait, spot.Landscape != null ? spot.LandscapeType : spot.PortraitType, spot.Landscape != null ? "landscape" : "portrait");
        if (file == null) return null;
        // ratio inhabituel (4:3, carré…) : le visuel le plus proche, centré sur fond flouté
        var ratio = s.DetectedW is int w and > 0 && s.DetectedH is int h and > 0 ? (double)Math.Max(w, h) / Math.Min(w, h) : 16 / 9.0;
        var approx = format != (portrait ? "portrait" : "landscape") || Math.Abs(ratio - 16 / 9.0) > 0.12;
        return ("/ads/" + file, type ?? "image", format, approx);
    }

    /// <summary>Passages complets de chaque campagne, toutes organisations : plafond de la campagne (mis à jour par <see cref="AdLog"/>).</summary>
    public static Func<Guid, int> TotalPlays { get; set; } = _ => 0;

    /// <summary>Spots que l'écran diffuse maintenant (même liste pour le player et pour l'empreinte de la configuration).</summary>
    public static List<(AdCampaign Campaign, AdSpot Spot)> Playable(Db db, Tenant t, Screen s, DateTime now)
    {
        if (!ScreenAllowed(db, t, s)) return new();
        return States(db, t, now).Where(x => x.Plays)
            .Where(x => x.Campaign.MaxPlays <= 0 || TotalPlays(x.Campaign.Id) < x.Campaign.MaxPlays)
            .Select(x => (x.Campaign, x.Spot)).ToList();
    }

    /// <summary>
    /// Empreinte de ce que l'écran diffuse en publicité : elle entre dans la version de sa configuration, l'écran recharge quand elle change
    /// (spot accepté, échéance passée, campagne arrêtée, plafond atteint…). Vide : pas de publicité.
    /// </summary>
    public static string Stamp(Db db, Tenant t, Screen s, DateTime now)
    {
        var l = Playable(db, t, s, now);
        if (l.Count == 0) return "";
        var raw = string.Join(",", l.Select(x => $"{x.Spot.Id:N}{x.Spot.DurationSec}{x.Spot.Landscape}{x.Spot.Portrait}{x.Campaign.Mode}{x.Campaign.EveryItems}{x.Campaign.EveryMinutes}{x.Campaign.MaxPerHour}{x.Campaign.MaxPerDayPerScreen}{x.Campaign.Start}{x.Campaign.End}"));
        return "|ad" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)), 0, 6);
    }

    /// <summary>Ce que reçoit le player (null : pas de publicité sur cet écran). Jamais dans le simulateur : aucun spot n'y passe ni n'y est compté.</summary>
    public static object? ForPlayer(Db db, Tenant t, Screen s, DateTime now)
    {
        var spots = Playable(db, t, s, now)
            .Select(x => (x.Campaign, x.Spot, V: Visual(x.Spot, s)))
            .Where(x => x.V != null)
            .Select(x => new
            {
                id = x.Spot.Id, campaignId = x.Campaign.Id, mode = x.Campaign.Mode, url = x.V!.Value.Url, type = x.V.Value.Type,
                format = x.V.Value.Format, approx = x.V.Value.Approx, durationSec = Math.Clamp(x.Spot.DurationSec, 5, 60),
                every = Math.Max(1, x.Campaign.EveryItems), everyMin = Math.Max(0, x.Campaign.EveryMinutes),
                maxHour = Math.Max(1, x.Campaign.MaxPerHour), maxDay = Math.Max(1, x.Campaign.MaxPerDayPerScreen),
                from = x.Campaign.Start, to = x.Campaign.End
            }).ToList();
        return spots.Count == 0 ? null : new { tag = Tag, tz = t.Timezone, spots };
    }

    /// <summary>Montant d'un passage complet pour le propriétaire.</summary>
    public static decimal OwnerAmount(AdCampaign c) => c.PricePerPlay * Math.Clamp(c.OwnerSharePct, 0, 100) / 100m;

    public static string Chf(decimal v) => v.ToString("N2", Fr) + " CHF";

    public static string Human(long sec) => sec < 60 ? $"{sec} s" : sec < 3600 ? $"{sec / 60} min" : $"{sec / 3600} h {sec % 3600 / 60:00}";

    /// <summary>Statut du relevé d'un mois : en cours (mois actuel), à valider, puis validé ou payé par l'administration.</summary>
    public static string StatementStatus(Db db, Guid clientId, string month, DateTime nowLocal) =>
        month == nowLocal.ToString("yyyy-MM") ? AdStatementStates.Running
        : db.AdStatements.FirstOrDefault(x => x.ClientId == clientId && x.Month == month)?.Status ?? AdStatementStates.ToValidate;

    /// <summary>Fichier de visuel accepté : image (jpg, png, webp) ou vidéo (mp4, webm). Renvoie le type, ou null.</summary>
    public static string? FileType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".png" or ".webp" => "image",
        ".mp4" or ".webm" => "video",
        _ => null
    };

    public static string Dir = "ads";
}

/// <summary>
/// Un passage de spot remonté par le player. <see cref="Id"/> est choisi par le player : un lot renvoyé après une réponse perdue n'est compté qu'une fois.
/// Complet = affiché 100 % de sa durée prévue, sans pause, veille, coupure ni rechargement ; un passage interrompu est noté à part et n'est pas payé.
/// </summary>
public record AdPlay(Guid Id, DateTime StartUtc, Guid ScreenId, Guid CampaignId, Guid SpotId, int PlannedMs, int ShownMs, bool Complete, string? Cause, string Format, bool Approx, bool Offline);

/// <summary>
/// Journal des passages de spots (preuve de diffusion) : un fichier JSON Lines par client (adplays/{client}.jsonl), conservé 13 mois,
/// séparé de l'historique de diffusion (90 jours). Dédoublonné par identifiant de passage.
/// </summary>
public class AdLog
{
    public const int RetentionDays = 396;   // 13 mois

    private readonly string dir;
    private readonly ConcurrentDictionary<Guid, object> locks = new();
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> ids = new();
    private readonly ConcurrentDictionary<Guid, DateTime> lastPrune = new();
    private readonly ConcurrentDictionary<Guid, int> totals = new();   // passages complets par campagne, toutes organisations
    private bool totalsLoaded;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AdLog(IConfiguration config, IWebHostEnvironment env) : this(Path.Combine(config["Linkii:DataDir"] ?? env.ContentRootPath, "adplays")) { }

    public AdLog(string directory)
    {
        dir = directory;
        Directory.CreateDirectory(dir);
        Ads.TotalPlays = Total;
    }

    private string FileOf(Guid clientId) => Path.Combine(dir, clientId.ToString("N") + ".jsonl");

    /// <summary>Passages complets d'une campagne (plafond de passages), toutes organisations.</summary>
    public int Total(Guid campaignId)
    {
        LoadTotals();
        return totals.GetValueOrDefault(campaignId);
    }

    private void LoadTotals()
    {
        if (totalsLoaded) return;
        lock (totals)
        {
            if (totalsLoaded) return;
            foreach (var f in Directory.GetFiles(dir, "*.jsonl"))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(f), "N", out var cid))
                    foreach (var p in Read(cid, DateTime.MinValue).Where(p => p.Complete))
                        totals.AddOrUpdate(p.CampaignId, 1, (_, n) => n + 1);
            totalsLoaded = true;
        }
    }

    /// <summary>Ajoute les passages inconnus ; renvoie ceux qui ont été retenus (les doublons sont ignorés).</summary>
    public List<AdPlay> Append(Guid clientId, IEnumerable<AdPlay> plays)
    {
        LoadTotals();
        lock (locks.GetOrAdd(clientId, _ => new object()))
        {
            var known = ids.GetOrAdd(clientId, id => ReadUnlocked(id, DateTime.MinValue).Select(p => p.Id).ToHashSet());
            var fresh = plays.Where(p => known.Add(p.Id)).ToList();
            if (fresh.Count == 0) return fresh;
            File.AppendAllLines(FileOf(clientId), fresh.Select(p => JsonSerializer.Serialize(p, Json)));
            foreach (var p in fresh.Where(p => p.Complete)) totals.AddOrUpdate(p.CampaignId, 1, (_, n) => n + 1);
            if (DateTime.UtcNow - lastPrune.GetOrAdd(clientId, DateTime.MinValue) > TimeSpan.FromDays(1))
            {
                lastPrune[clientId] = DateTime.UtcNow;
                var keep = ReadUnlocked(clientId, DateTime.UtcNow.AddDays(-RetentionDays));
                File.WriteAllLines(FileOf(clientId), keep.Select(p => JsonSerializer.Serialize(p, Json)));
                ids[clientId] = keep.Select(p => p.Id).ToHashSet();
            }
            return fresh;
        }
    }

    public List<AdPlay> Read(Guid clientId, DateTime sinceUtc)
    {
        lock (locks.GetOrAdd(clientId, _ => new object())) return ReadUnlocked(clientId, sinceUtc);
    }

    /// <summary>Passages de toutes les organisations (résultats d'une campagne côté administration).</summary>
    public List<(Guid ClientId, AdPlay Play)> ReadAll(DateTime sinceUtc)
    {
        var all = new List<(Guid, AdPlay)>();
        foreach (var f in Directory.GetFiles(dir, "*.jsonl"))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(f), "N", out var cid))
                all.AddRange(Read(cid, sinceUtc).Select(p => (cid, p)));
        return all;
    }

    private List<AdPlay> ReadUnlocked(Guid clientId, DateTime sinceUtc)
    {
        var path = FileOf(clientId);
        var list = new List<AdPlay>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var p = JsonSerializer.Deserialize<AdPlay>(line, Json);
                if (p != null && p.StartUtc >= sinceUtc) list.Add(p);
            }
            catch (JsonException) { /* ligne tronquée : ignorée */ }
        }
        return list.OrderBy(p => p.StartUtc).ToList();
    }

    /// <summary>
    /// Contrôle d'un lot reçu d'un écran : spot connu de la campagne, dates plausibles, durées bornées, pas deux passages qui se chevauchent sur l'écran.
    /// Complet : affiché au moins sa durée prévue (à une demi-seconde près). Rien n'est compté pour un écran qui n'a pas droit à la publicité.
    /// </summary>
    public static List<AdPlay> Check(IEnumerable<AdPlayDto> batch, Guid screenId, IReadOnlyList<AdCampaign> campaigns, IEnumerable<AdPlay> recent, DateTime now)
    {
        var spots = campaigns.SelectMany(c => c.Spots.Select(s => (c, s))).ToDictionary(x => x.s.Id);
        var busy = recent.Where(p => p.ScreenId == screenId).Select(p => (Start: p.StartUtc, End: p.StartUtc.AddMilliseconds(p.ShownMs))).ToList();
        var res = new List<AdPlay>();
        foreach (var d in batch.Take(500).OrderBy(d => d.Start))
        {
            if (d.Id == Guid.Empty || !spots.TryGetValue(d.SpotId, out var cs) || cs.c.Id != d.CampaignId) continue;
            if (!double.IsFinite(d.Start)) continue;
            var at = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Clamp(Math.Round(d.Start), 0, 253402300799000)).UtcDateTime;
            if (at < now.AddDays(-AdLog.RetentionDays) || at > now.AddMinutes(5)) continue;
            var planned = cs.s.DurationSec * 1000;
            var shown = Math.Clamp(d.ShownMs, 0, planned + 2000);
            var end = at.AddMilliseconds(shown);
            if (busy.Any(b => at < b.End.AddMilliseconds(-1000) && end > b.Start.AddMilliseconds(1000))) continue;   // chevauche un autre passage : invraisemblable
            busy.Add((at, end));
            var complete = d.Complete && shown >= planned - 500;
            var cause = complete ? null : d.Cause is "pause" or "sleep" or "reload" or "error" or "publish" ? d.Cause : "cut";
            res.Add(new AdPlay(d.Id, at, screenId, cs.c.Id, cs.s.Id, planned, shown, complete, cause, d.Format == "portrait" ? "portrait" : "landscape", d.Approx, d.Offline));
        }
        return res;
    }
}

/// <summary>
/// Rappel par e-mail aux administrateurs d'un compte, 24 h avant l'échéance de validation d'un spot (une seule fois par spot) :
/// sans réponse, le spot sera diffusé. Les écrans, eux, prennent un spot accepté ou échu au sondage suivant (sa révision change).
/// </summary>
public class AdWorker(JsonStore store, Mailer mail, PushService push, ResellerResolver resolver, ILogger<AdWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try { await Run(DateTime.UtcNow); }
            catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogError(ex, "Rappels de la publicité"); }
        } while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
    }

    public async Task Run(DateTime now)
    {
        var work = store.Write(db =>
        {
            var list = new List<(Tenant Client, Reseller? Reseller, List<AdSpotState> Spots, List<(string Email, string Name)> To)>();
            foreach (var t in db.Clients.Where(Ads.On))
            {
                var due = Ads.States(db, t, now).Where(x => x.State == AdSpotState.Pending && x.Deadline - now <= TimeSpan.FromHours(Ads.ReminderHours) && !t.AdsReminded.Contains(x.Spot.Id)).ToList();
                if (due.Count == 0) continue;
                t.AdsReminded.AddRange(due.Select(x => x.Spot.Id));
                if (t.AdsReminded.Count > 500) t.AdsReminded.RemoveRange(0, t.AdsReminded.Count - 500);
                var to = db.Users.Where(u => u.ClientId == t.Id && u.Role == Roles.ClientAdmin && !u.Disabled && !u.AwaitsVerification && u.Email.Contains('@')).Select(u => (u.Email, u.Name)).ToList();
                list.Add((t, db.Resellers.FirstOrDefault(r => r.Id == t.ResellerId), due, to));
            }
            return list;
        });
        foreach (var (client, reseller, spots, to) in work)
        {
            var title = spots.Count == 1 ? "Un spot de publicité attend votre réponse" : $"{spots.Count} spots de publicité attendent votre réponse";
            var body = string.Join(", ", spots.Select(x => x.Campaign.Advertiser + " · " + x.Spot.Name)) +
                $". Sans réponse avant le {spots.Min(x => x.Deadline).ToLocalTime().ToString("d MMMM à HH:mm", Ads.Fr)}, ils seront diffusés sur vos écrans.";
            try { await push.ToClientAdmins(client.Id, "ads", title, body, "/settings?tab=publicite"); } catch (Exception ex) { log.LogWarning(ex, "Notification publicité"); }
            if (reseller == null) continue;
            var app = resolver.LoginUrl(reseller)[..^"login".Length];
            foreach (var (email, name) in to)
                if (await mail.SendAlert(reseller, app, client.Name, name, email, title, body, app + "settings?tab=publicite") is { } err)
                    log.LogWarning("Rappel publicité non envoyé à {Email} : {Error}", email, err);
        }
    }
}

/// <summary>Passage de spot tel que l'envoie le player (start : heure du serveur, en ms ; décimale possible, l'écart d'horloge du player n'est pas entier).</summary>
public record AdPlayDto(Guid Id, Guid CampaignId, Guid SpotId, double Start, int ShownMs, bool Complete, string? Cause, string? Format, bool Approx, bool Offline);

/// <summary>Une ligne de relevé ou de résultats : passages et montants d'une campagne sur un écran.</summary>
public record AdRow(Guid CampaignId, Guid ScreenId, int Complete, int Interrupted, long Seconds, decimal Price, int SharePct, decimal Gross, decimal Owner);

public static class AdReports
{
    /// <summary>Regroupe des passages par campagne et par écran, avec les montants (brut pour l'annonceur, part du propriétaire).</summary>
    public static List<AdRow> Rows(IEnumerable<AdPlay> plays, IReadOnlyList<AdCampaign> campaigns) =>
        plays.GroupBy(p => (p.CampaignId, p.ScreenId)).Select(g =>
        {
            var c = campaigns.FirstOrDefault(x => x.Id == g.Key.CampaignId);
            var done = g.Count(p => p.Complete);
            var price = c?.PricePerPlay ?? 0;
            var share = c?.OwnerSharePct ?? 0;
            return new AdRow(g.Key.CampaignId, g.Key.ScreenId, done, g.Count(p => !p.Complete), g.Where(p => p.Complete).Sum(p => (long)p.PlannedMs) / 1000,
                price, share, done * price, done * (c == null ? 0 : Ads.OwnerAmount(c)));
        }).OrderBy(r => campaigns.FirstOrDefault(c => c.Id == r.CampaignId)?.Advertiser).ThenByDescending(r => r.Complete).ToList();

    /// <summary>Début (UTC) et fin d'un mois aaaa-mm dans un fuseau.</summary>
    public static (DateTime From, DateTime To) Month(string month, TimeZoneInfo zone)
    {
        var d = DateTime.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var x) ? x : DateTime.UtcNow;
        return (TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), zone),
                TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(d.AddMonths(1), DateTimeKind.Unspecified), zone));
    }

    public static TimeZoneInfo Zone(string? id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id ?? "Europe/Zurich"); } catch (Exception) { return TimeZoneInfo.Utc; }
    }

    /// <summary>Empreinte SHA-256 des lignes d'un relevé ou d'un rapport : toute modification la change.</summary>
    public static string Fingerprint(IEnumerable<AdPlay> plays) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", plays.OrderBy(p => p.StartUtc).ThenBy(p => p.Id)
            .Select(p => $"{p.Id:N};{p.StartUtc:O};{p.ScreenId:N};{p.SpotId:N};{p.ShownMs};{p.Complete}"))))).ToLowerInvariant();

    /// <summary>Numéro lisible d'un document : préfixe, mois et 4 chiffres tirés de l'empreinte.</summary>
    public static string Number(string prefix, string month, string fingerprint) =>
        $"{prefix}-{month}-{Convert.ToInt32(fingerprint[..4], 16) % 10000:0000}";

    public static string Csv(IEnumerable<string[]> rows) =>
        string.Join("\r\n", rows.Select(r => string.Join(";", r.Select(v => v.Contains(';') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v))));
}
