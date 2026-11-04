using System.Text.Json;

namespace Linkii.Poc;

/// <summary>
/// Approbation des publications (plan Pro, §162, maquette approbation-publications.html). Les personnes désignées dans leur fiche ne publient plus directement :
/// leur publication sur un écran devient une demande, que décide un approbateur (ou un administrateur). Pendant l'attente, l'écran garde sa version ;
/// le contenu envoyé est figé (<see cref="ScreenSnapshot"/>) : approuvé, il part tel quel, même si la liste a été modifiée depuis.
/// Ni le planning, ni l'urgence, ni l'API ne sont soumis.
/// </summary>
public static class Approvals
{
    /// <summary>Type de notification (centre de notifications).</summary>
    public const string Kind = "approvals";
    public const string ScopeAll = "all", ScopeChosen = "chosen";
    /// <summary>Écran qu'aucun approbateur ne couvre : les administrateurs reçoivent la demande, ou la publication part sans approbation.</summary>
    public const string ToAdmins = "admins", Direct = "publish";

    /// <summary>La brique agit : plan Pro, option activée, espace non coupé. Sinon tout le monde publie directement.</summary>
    public static bool Active(Tenant t) => t.IsGrowth && t.ApprovalOn && !t.IsCut;

    /// <summary>L'approbation suppose un approbateur et une personne soumise : il faut au moins deux utilisateurs (administrateurs et membres actifs).</summary>
    public const int MinUsers = 2;
    public static bool EnoughUsers(IEnumerable<User> users) => users.Count(u => !u.Disabled) >= MinUsers;

    public static int Reminder(Tenant t) => Math.Clamp(t.ApprovalReminderHours, 1, 720);
    public static int ExpireDays(Tenant t) => Math.Clamp(t.ApprovalExpireDays, 1, 90);
    public static int KeepMonths(Tenant t) => Math.Clamp(t.ApprovalKeepMonths, 1, 60);
}

public static class ApprovalStatus
{
    public const string Wait = "wait", Approved = "ok", Refused = "ko", Withdrawn = "off", Expired = "expired", Replaced = "replaced";

    public static string Label(string s) => s switch
    {
        Wait => "En attente", Approved => "Approuvée", Refused => "Refusée", Withdrawn => "Retirée", Expired => "Périmée", Replaced => "Remplacée", _ => s
    };
}

/// <summary>Ce qu'une publication fige pour un écran : listes de ses zones et widgets, planning, borne. Voir <see cref="Notifier.Build"/>.</summary>
public class ScreenSnapshot
{
    public List<PublishedItem> Published { get; set; } = new();
    public string Layout { get; set; } = ScreenLayouts.Full;
    public List<List<PublishedItem>> Zones { get; set; } = new();
    public Dictionary<Guid, List<PublishedItem>> Lists { get; set; } = new();
    public Dictionary<Guid, int> ListRevs { get; set; } = new();
    public PublishedKiosk? Kiosk { get; set; }
    public string Stamp { get; set; } = "";
}

/// <summary>Un contenu de la publication demandée, comparé à ce que l'écran diffuse : inchangé, ajouté, retiré ou modifié.</summary>
public class ApprovalLine
{
    public const string Same = "same", Add = "add", Del = "del", Mod = "mod";
    public string Name { get; set; } = "";
    public int DurationSec { get; set; }
    /// <summary>Durée diffusée aujourd'hui (pour un contenu modifié).</summary>
    public int PreviousSec { get; set; }
    public string Change { get; set; } = Same;
    public Guid? MediaId { get; set; }
}

public class ApprovalScreen
{
    public Guid ScreenId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Contenu figé ; vidé dès que la demande est décidée (la publication l'a repris, ou il est abandonné).</summary>
    public ScreenSnapshot? Snapshot { get; set; }
}

/// <summary>Une demande d'approbation : un ou plusieurs écrans (un mur forme une seule demande), son auteur, son état et la décision.</summary>
public class ApprovalRequest : IClientOwned
{
    public Guid ClientId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Numéro lisible (n° 128), par organisation.</summary>
    public int Number { get; set; }
    public Guid AuthorId { get; set; }
    public string AuthorName { get; set; } = "";
    public List<ApprovalScreen> Screens { get; set; } = new();
    public bool Wall { get; set; }
    public List<ApprovalLine> Lines { get; set; } = new();
    public string Status { get; set; } = ApprovalStatus.Wait;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string? Note { get; set; }
    public DateTime? DecidedUtc { get; set; }
    public Guid? DecidedById { get; set; }
    public string? DecidedByName { get; set; }
    /// <summary>Commentaire de la décision (motif obligatoire d'un refus) ou raison de la péremption.</summary>
    public string? Comment { get; set; }
    public DateTime? RemindedUtc { get; set; }
    /// <summary>Quand l'auteur a pris connaissance de la décision (ferme le bandeau de refus sur la page Écrans).</summary>
    public DateTime? AuthorSeenUtc { get; set; }

    public bool Open => Status == ApprovalStatus.Wait;
    public int Added => Lines.Count(l => l.Change == ApprovalLine.Add);
    public int Removed => Lines.Count(l => l.Change == ApprovalLine.Del);
    public int Changed => Lines.Count(l => l.Change == ApprovalLine.Mod);

    /// <summary>Écran (ou mur) concerné, pour les titres.</summary>
    public string Title => Screens.Count == 0 ? "—" : Screens.Count == 1 ? Screens[0].Name : Wall ? $"Mur · {Screens.Count} écrans" : $"{Screens.Count} écrans";
}

/// <summary>Règles de l'approbation sur la base : qui est soumis, qui approuve quoi, ce qu'une personne voit. Sans effet de bord (testable).</summary>
public static class ApprovalOps
{
    private static bool IsAdmin(User u) => u.Role == Roles.ClientAdmin && !u.Disabled;

    /// <summary>La personne est soumise à approbation (utilisateur désigné dans sa fiche, brique active).</summary>
    public static bool Subject(Db db, Tenant t, Guid? userId) =>
        Approvals.Active(t) && userId is { } id && db.Users.FirstOrDefault(u => u.Id == id && u.ClientId == t.Id) is { Role: Roles.ClientMember, Rights.SubmitApproval: true, Disabled: false };

    /// <summary>Écrans qu'un approbateur peut approuver : tout ce qu'il voit, ou les zones et écrans choisis dans sa fiche.</summary>
    public static HashSet<Guid> ScreensOf(Db db, Guid clientId, User u)
    {
        if (u.Rights is not { Approver: true } r) return new();
        var scope = r.ApprovalScope == Approvals.ScopeChosen
            ? new MemberRights { Screens = PageMode.Own, ZoneIds = r.ApprovalZoneIds, ScreenIds = r.ApprovalScreenIds }
            : r;
        return MemberOps.ScreensOf(new ClientDb(db, clientId), scope).Select(s => s.Id).ToHashSet();
    }

    /// <summary>Approbateurs qui couvrent tous ces écrans ; à défaut, les administrateurs (ou personne, si l'organisation publie alors sans approbation).</summary>
    public static List<User> Approvers(Db db, Tenant t, IReadOnlyCollection<Guid> screenIds)
    {
        var members = db.Users.Where(u => u.ClientId == t.Id && u.Role == Roles.ClientMember && !u.Disabled && !u.AwaitsVerification && u.Rights is { Approver: true })
            .Where(u => { var set = ScreensOf(db, t.Id, u); return screenIds.All(set.Contains); }).ToList();
        if (members.Count > 0 || t.ApprovalNoApprover == Approvals.Direct) return members;
        return db.Users.Where(u => u.ClientId == t.Id && IsAdmin(u) && !u.AwaitsVerification).ToList();
    }

    /// <summary>Cette publication doit être approuvée : la personne est soumise et quelqu'un peut décider.</summary>
    public static bool NeedsApproval(Db db, Tenant t, Guid? userId, IReadOnlyCollection<Guid> screenIds) =>
        Subject(db, t, userId) && Approvers(db, t, screenIds).Count > 0;

    /// <summary>Décider : un administrateur, ou un approbateur dont le périmètre couvre tous les écrans ; jamais l'auteur.</summary>
    public static bool CanDecide(Db db, Tenant t, ApprovalRequest r, Guid? userId, bool admin)
    {
        if (userId == r.AuthorId) return false;
        if (admin) return true;
        return userId is { } id && db.Users.FirstOrDefault(u => u.Id == id && u.ClientId == t.Id && !u.Disabled) is { Role: Roles.ClientMember, Rights.Approver: true } u
            && ScreensOf(db, t.Id, u) is var set && r.Screens.All(s => set.Contains(s.ScreenId) || !db.Screens.Any(x => x.Id == s.ScreenId));   // un écran supprimé depuis la demande ne bloque personne
    }

    /// <summary>Demandes que la personne voit : les siennes, et celles qu'elle peut décider (un administrateur : toutes).</summary>
    public static List<ApprovalRequest> Visible(Db db, Tenant t, Guid? userId, bool admin) =>
        db.ApprovalRequests.Where(r => r.ClientId == t.Id && (admin || r.AuthorId == userId || CanDecide(db, t, r, userId, false)))
            .OrderByDescending(r => r.CreatedUtc).ToList();

    /// <summary>Nombre de demandes en attente que la personne peut décider (pastille du menu).</summary>
    public static int PendingFor(Db db, Guid clientId, Guid? userId, bool admin) =>
        db.Clients.FirstOrDefault(c => c.Id == clientId) is { } t && Approvals.Active(t)
            ? db.ApprovalRequests.Count(r => r.ClientId == clientId && r.Open && CanDecide(db, t, r, userId, admin)) : 0;

    /// <summary>La brique concerne la personne : soumise, approbatrice ou administratrice, brique active (entrée « Approbations » du menu).</summary>
    public static bool Involved(Db db, Tenant t, Guid? userId, bool admin) =>
        Approvals.Active(t) && (admin || userId is { } id && db.Users.FirstOrDefault(u => u.Id == id) is { Rights: { } r } && (r.SubmitApproval || r.Approver));

    /// <summary>Demande ouverte qui contient cet écran (null : aucune).</summary>
    public static ApprovalRequest? OpenFor(Db db, Guid clientId, Guid screenId) =>
        db.ApprovalRequests.Where(r => r.ClientId == clientId && r.Open && r.Screens.Any(s => s.ScreenId == screenId)).OrderByDescending(r => r.CreatedUtc).FirstOrDefault();

    /// <summary>Compare ce que l'écran diffuse et ce que la demande publierait, contenu par contenu.</summary>
    public static List<ApprovalLine> Diff(Db db, Func<string, string?> appName, IReadOnlyCollection<PublishedItem> before, ScreenSnapshot after)
    {
        var now = after.Published.Concat(after.Zones.SelectMany(z => z)).Concat(after.Lists.Values.SelectMany(l => l)).ToList();
        var old = before.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<Guid>();
        var lines = new List<ApprovalLine>();
        string NameOf(PublishedItem i) =>
            db.Media.FirstOrDefault(m => m.Id == i.MediaId)?.Name is { Length: > 0 } n ? n
            : i.AppId is { } a ? (i.Placement == "free" ? "Widget · " : "") + (appName(a) ?? a) : i.Type;
        foreach (var i in now)
        {
            if (!seen.Add(i.Id)) continue;
            var change = !old.TryGetValue(i.Id, out var o) ? ApprovalLine.Add : JsonSerializer.Serialize(o) == JsonSerializer.Serialize(i) ? ApprovalLine.Same : ApprovalLine.Mod;
            lines.Add(new ApprovalLine { Name = NameOf(i), DurationSec = i.DurationSec, PreviousSec = o?.DurationSec ?? i.DurationSec, Change = change, MediaId = i.MediaId });
        }
        foreach (var o in before)
            if (seen.Add(o.Id)) lines.Add(new ApprovalLine { Name = NameOf(o), DurationSec = o.DurationSec, PreviousSec = o.DurationSec, Change = ApprovalLine.Del, MediaId = o.MediaId });
        return lines;
    }

    /// <summary>« +2 éléments, −1 élément, 1 modifié ».</summary>
    public static string Summary(ApprovalRequest r)
    {
        var parts = new List<string>();
        if (r.Added > 0) parts.Add($"+{r.Added} élément{(r.Added > 1 ? "s" : "")}");
        if (r.Removed > 0) parts.Add($"−{r.Removed} élément{(r.Removed > 1 ? "s" : "")}");
        if (r.Changed > 0) parts.Add($"{r.Changed} modifié{(r.Changed > 1 ? "s" : "")}");
        return parts.Count == 0 ? "aucun changement de contenu" : string.Join(", ", parts);
    }
}

/// <summary>Ce que l'approbation demande à la publication (<see cref="Notifier"/>) : figer un écran, le faire diffuser, le prévenir.</summary>
public interface IScreenPublishing
{
    ScreenSnapshot Build(Db db, Screen s);
    void ApplySnapshot(Db db, Screen s, ScreenSnapshot snap);
    Task NotifyScreen(Guid screenId);
    string? AppName(string appId);
}

/// <summary>Prévient des personnes précises : centre de notifications (toujours) et e-mail. Ne lève jamais d'exception.</summary>
public interface IApprovalNotices
{
    Task Tell(Guid clientId, IReadOnlyCollection<Guid> users, string title, string body, string severity, string dedupe);
}

public record SubmitResult(ApprovalRequest? Request, string? Error, List<Guid> Approvers);

/// <summary>Dépôt, décision, retrait, rappel et péremption des demandes d'approbation.</summary>
public class ApprovalService(JsonStore store, IScreenPublishing screens, IApprovalNotices notices, ILogger<ApprovalService> log, HistoryLog? history = null)
{
    private const string Path = "/approvals";

    /// <summary>Dépose une demande pour ces écrans (une seule demande : un mur reste groupé). Les demandes ouvertes qui portent sur les mêmes écrans sont remplacées.</summary>
    public async Task<SubmitResult> Submit(Guid clientId, Guid userId, IReadOnlyCollection<Guid> screenIds, string? note = null)
    {
        var made = store.Write(db =>
        {
            var t = db.Clients.FirstOrDefault(c => c.Id == clientId);
            var u = db.Users.FirstOrDefault(x => x.Id == userId && x.ClientId == clientId);
            if (t == null || u == null) return new SubmitResult(null, "Compte introuvable.", new());
            var list = db.Screens.Where(s => s.ClientId == clientId && s.Token != null && screenIds.Contains(s.Id)).ToList();
            if (list.Count == 0) return new SubmitResult(null, "Aucun écran à publier.", new());
            var req = new ApprovalRequest
            {
                ClientId = clientId, AuthorId = u.Id, AuthorName = u.Label, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                Number = db.ApprovalRequests.Where(r => r.ClientId == clientId).Select(r => r.Number).DefaultIfEmpty(0).Max() + 1,
                Wall = list.Count > 1 && Helpers.Wall(db.Areas, list[0]) != null
            };
            foreach (var s in list) req.Screens.Add(new ApprovalScreen { ScreenId = s.Id, Name = s.Name, Snapshot = screens.Build(db, s) });
            req.Lines = ApprovalOps.Diff(db, screens.AppName, PlayerFeed.AllItems(db, list[0]), req.Screens[0].Snapshot!);
            var ids = list.Select(s => s.Id).ToHashSet();
            foreach (var old in db.ApprovalRequests.Where(r => r.ClientId == clientId && r.Open).ToList())
            {
                if (old.Screens.RemoveAll(s => ids.Contains(s.ScreenId)) == 0) continue;
                if (old.Screens.Count > 0) continue;
                old.Status = ApprovalStatus.Replaced; old.DecidedUtc = DateTime.UtcNow; old.Comment = $"Remplacée par la demande n° {req.Number}.";
            }
            db.ApprovalRequests.Add(req);
            return new SubmitResult(req, null, ApprovalOps.Approvers(db, t, ids.ToList()).Select(a => a.Id).ToList());
        });
        if (made.Request is { } r && made.Approvers.Count > 0)
            await notices.Tell(clientId, made.Approvers, $"Demande à approuver : {r.Title}", $"{r.AuthorName} · {ApprovalOps.Summary(r)}", Notifs.Warn, "approval:" + r.Id);
        return made;
    }

    /// <summary>Approuve : la publication part avec le contenu figé de la demande.</summary>
    public Task<string?> Approve(Guid clientId, Guid requestId, Guid? byUserId, string byName, bool byAdmin, string? comment) =>
        Decide(clientId, requestId, byUserId, byName, byAdmin, true, comment);

    /// <summary>Refuse : le motif est obligatoire, l'auteur le lira.</summary>
    public Task<string?> Refuse(Guid clientId, Guid requestId, Guid? byUserId, string byName, bool byAdmin, string? comment) =>
        Decide(clientId, requestId, byUserId, byName, byAdmin, false, comment);

    private async Task<string?> Decide(Guid clientId, Guid requestId, Guid? byUserId, string byName, bool byAdmin, bool approve, string? comment)
    {
        var text = comment?.Trim();
        if (!approve && string.IsNullOrEmpty(text)) return "Expliquez ce qu'il faut corriger : l'auteur le lira dans sa notification.";
        var (error, req, published) = store.Write<(string? Error, ApprovalRequest? Request, List<Guid> Done)>(db =>
        {
            var t = db.Clients.FirstOrDefault(c => c.Id == clientId);
            var r = db.ApprovalRequests.FirstOrDefault(x => x.Id == requestId && x.ClientId == clientId);
            if (t == null || r == null) return ("Demande introuvable.", (ApprovalRequest?)null, new List<Guid>());
            if (!r.Open) return ($"Cette demande est déjà {ApprovalStatus.Label(r.Status).ToLowerInvariant()}.", r, new List<Guid>());
            if (!ApprovalOps.CanDecide(db, t, r, byUserId, byAdmin)) return ("Vous ne pouvez pas décider de cette demande.", r, new List<Guid>());
            var done = new List<Guid>();
            if (approve)
            {
                // un écran supprimé ou déconnecté depuis la demande n'est plus publié ; plus aucun écran : la demande est périmée
                r.Screens.RemoveAll(s => db.Screens.FirstOrDefault(x => x.Id == s.ScreenId && x.ClientId == clientId && x.Token != null) == null);
                if (r.Screens.Count == 0)
                {
                    Close(r, ApprovalStatus.Expired, null, "Plus aucun des écrans concernés n'existe.");
                    return (null, r, done);
                }
                foreach (var s in r.Screens)
                {
                    screens.ApplySnapshot(db, db.Screens.First(x => x.Id == s.ScreenId), s.Snapshot!);
                    done.Add(s.ScreenId);
                }
            }
            Close(r, approve ? ApprovalStatus.Approved : ApprovalStatus.Refused, (byUserId, byName), text);
            return (null, r, done);
        });
        if (error != null || req == null) return error;
        history?.Record(clientId, History.Activity, History.Approval, req.Status == ApprovalStatus.Expired ? $"Demande périmée : {req.Title}" : approve ? $"Publication approuvée : {req.Title}" : $"Publication refusée : {req.Title}",
            approve ? Notifs.Ok : Notifs.Warn, string.IsNullOrWhiteSpace(text) ? null : $"« {text} »", byName, byUserId, "approval", req.Id, req.Title, "approvals",
            facts: new() { new[] { "Auteur de la demande", req.AuthorName ?? "" }, new[] { "Décidée par", byName } });
        foreach (var id in published) await screens.NotifyScreen(id);
        if (req.Status == ApprovalStatus.Expired)
            await notices.Tell(clientId, [req.AuthorId], $"Demande périmée : {req.Title}", req.Comment ?? "", Notifs.Warn, "approval-end:" + req.Id);
        else
            await notices.Tell(clientId, [req.AuthorId],
                approve ? $"Publication approuvée : {req.Title}" : $"Publication refusée : {req.Title}",
                approve ? $"Approuvée par {byName}{(text == null ? "" : " : « " + text + " »")}. L'écran diffuse la nouvelle version." : $"Refusée par {byName} : « {text} ». Votre brouillon est intact : corrigez puis renvoyez.",
                approve ? Notifs.Ok : Notifs.Error, "approval-end:" + req.Id);
        return null;
    }

    /// <summary>Retire sa demande tant qu'elle est en attente (l'auteur, ou un administrateur).</summary>
    public string? Withdraw(Guid clientId, Guid requestId, Guid? byUserId, bool byAdmin) => store.Write(db =>
    {
        var r = db.ApprovalRequests.FirstOrDefault(x => x.Id == requestId && x.ClientId == clientId);
        if (r == null) return "Demande introuvable.";
        if (!r.Open) return "Cette demande n'est plus en attente.";
        if (!byAdmin && r.AuthorId != byUserId) return "Seul l'auteur (ou un administrateur) retire une demande.";
        Close(r, ApprovalStatus.Withdrawn, null, null);
        history?.Record(clientId, History.Activity, History.Approval, $"Demande retirée : {r.Title}", Notifs.Info, null, r.AuthorName, r.AuthorId, "approval", r.Id, r.Title, "approvals");
        return (string?)null;
    });

    /// <summary>L'auteur a vu la décision : le bandeau de la page Écrans ne revient plus.</summary>
    public void MarkSeen(Guid clientId, Guid requestId, Guid? byUserId) => store.Write(db =>
    {
        var r = db.ApprovalRequests.FirstOrDefault(x => x.Id == requestId && x.ClientId == clientId && !x.Open && x.AuthorId == byUserId);
        if (r != null && r.AuthorSeenUtc == null) r.AuthorSeenUtc = DateTime.UtcNow;
        return 0;
    });

    /// <summary>Met à jour une demande en attente avec l'état actuel du brouillon : la validation repart à zéro (nouvelle demande, l'ancienne est remplacée).</summary>
    public Task<SubmitResult> Resubmit(Guid clientId, Guid userId, Guid requestId, string? note = null)
    {
        var ids = store.Read(db => db.ApprovalRequests.FirstOrDefault(r => r.Id == requestId && r.ClientId == clientId && r.Open && r.AuthorId == userId)?.Screens.Select(s => s.ScreenId).ToList());
        return ids == null ? Task.FromResult(new SubmitResult(null, "Cette demande n'est plus en attente.", new())) : Submit(clientId, userId, ids, note);
    }

    private static void Close(ApprovalRequest r, string status, (Guid? Id, string Name)? by, string? comment)
    {
        r.Status = status; r.DecidedUtc = DateTime.UtcNow; r.Comment = comment;
        if (by is { } b) { r.DecidedById = b.Id; r.DecidedByName = b.Name; }
        foreach (var s in r.Screens) s.Snapshot = null;
    }

    /// <summary>
    /// Passage périodique : refuse les demandes d'une brique qui n'agit plus (plan Pro terminé, option coupée), périme celles restées sans réponse, rappelle les approbateurs,
    /// efface l'historique trop ancien.
    /// </summary>
    public async Task Tick(DateTime now)
    {
        var tell = new List<(Guid Client, List<Guid> To, string Title, string Body, string Severity, string Dedupe)>();
        store.Write(db =>
        {
            foreach (var r in db.ApprovalRequests.Where(r => r.Open).ToList())
            {
                var t = db.Clients.FirstOrDefault(c => c.Id == r.ClientId);
                if (t == null) { db.ApprovalRequests.Remove(r); continue; }
                if (!Approvals.Active(t))
                {
                    Close(r, ApprovalStatus.Refused, (null, "Linkivi"), "L'approbation n'est plus active (plan Pro terminé ou option désactivée) : republiez directement.");
                    tell.Add((r.ClientId, [r.AuthorId], $"Demande refusée : {r.Title}", r.Comment!, Notifs.Warn, "approval-end:" + r.Id));
                }
                else if (now - r.CreatedUtc >= TimeSpan.FromDays(Approvals.ExpireDays(t)))
                {
                    Close(r, ApprovalStatus.Expired, null, $"Sans réponse après {Approvals.ExpireDays(t)} jours : renvoyez la demande si elle est toujours d'actualité.");
                    tell.Add((r.ClientId, [r.AuthorId], $"Demande périmée : {r.Title}", r.Comment!, Notifs.Warn, "approval-end:" + r.Id));
                }
                else if (r.RemindedUtc == null && now - r.CreatedUtc >= TimeSpan.FromHours(Approvals.Reminder(t)))
                {
                    r.RemindedUtc = now;
                    var to = ApprovalOps.Approvers(db, t, r.Screens.Select(s => s.ScreenId).ToList()).Select(u => u.Id).ToList();
                    if (to.Count > 0) tell.Add((r.ClientId, to, $"Toujours à approuver : {r.Title}", $"{r.AuthorName} attend votre décision depuis {Math.Max(1, (int)(now - r.CreatedUtc).TotalHours)} h.", Notifs.Warn, "approval-remind:" + r.Id));
                }
            }
            db.ApprovalRequests.RemoveAll(r => !r.Open && db.Clients.FirstOrDefault(c => c.Id == r.ClientId) is { } t && r.DecidedUtc is { } d && d.AddMonths(Approvals.KeepMonths(t)) < now);
        });
        foreach (var x in tell)
            try { await notices.Tell(x.Client, x.To, x.Title, x.Body, x.Severity, x.Dedupe); }
            catch (Exception ex) { log.LogWarning(ex, "Notification d'approbation non envoyée"); }
    }

    /// <summary>Chemin ouvert par une notification (la demande dans la page Approbations).</summary>
    public static string PathOf(Guid requestId) => $"{Path}?r={requestId:N}";
}

/// <summary>Passe toutes les 10 minutes : rappels, péremption, refus des demandes d'une brique devenue inactive.</summary>
public class ApprovalWorker(ApprovalService approvals, ILogger<ApprovalWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        do
        {
            try { await approvals.Tick(DateTime.UtcNow); }
            catch (Exception ex) { log.LogWarning(ex, "Passage de l'approbation en échec"); }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }
}

/// <summary>Notifications d'approbation : centre (adressées à des personnes précises, toujours) et e-mail.</summary>
public class ApprovalNotices(JsonStore store, NotificationCenter center, Mailer mail, ResellerResolver resolver, ILogger<ApprovalNotices> log) : IApprovalNotices
{
    public async Task Tell(Guid clientId, IReadOnlyCollection<Guid> users, string title, string body, string severity, string dedupe)
    {
        try
        {
            var requestId = dedupe[(dedupe.IndexOf(':') + 1)..];
            var path = Guid.TryParse(requestId, out var rid) ? ApprovalService.PathOf(rid) : "/approvals";
            center.Add(clientId, Approvals.Kind, severity, title, body, path, null, dedupe, null, users);
            var (client, reseller, list) = store.Read(db =>
            {
                var c = db.Clients.FirstOrDefault(x => x.Id == clientId);
                return (c, db.Resellers.FirstOrDefault(r => r.Id == c?.ResellerId),
                    db.Users.Where(u => users.Contains(u.Id) && !u.Disabled && !u.AwaitsVerification && u.Email.Contains('@')).Select(u => (u.Email, u.Name)).ToList());
            });
            if (client == null || reseller == null) return;
            var app = resolver.LoginUrl(reseller)[..^"login".Length];
            foreach (var (email, name) in list)
            {
                var err = await mail.SendAlert(reseller, app, client.Name, name, email, title, body, app + path.TrimStart('/'));
                if (err != null) log.LogWarning("Notification « {Title} » non envoyée à {Email} : {Error}", title, email, err);
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Notification « {Title} » non envoyée", title); }
    }
}

/// <summary>
/// Passerelle de l'interface vers la publication d'un écran : une personne soumise à approbation dépose une demande au lieu de publier. Toute publication faite
/// pour une personne passe ici, jamais par <see cref="Notifier.PublishScreen"/> (un test le vérifie dans les pages).
/// </summary>
public class ScreenPublisher(Notifier notifier, ApprovalService approvals, TenantContext ctx, JsonStore store, HistoryLog history)
{
    /// <param name="Published">Écrans publiés tout de suite.</param>
    /// <param name="Request">Demande déposée (la publication attend la décision) ; null : publié.</param>
    public record Result(int Published, ApprovalRequest? Request, string? Error)
    {
        public bool Pending => Request != null;
    }

    public async Task<Result> Publish(IReadOnlyCollection<Guid> screenIds, string? note = null)
    {
        var client = ctx.RequireClientId();
        var user = ctx.IsImpersonating ? null : ctx.UserId;   // l'équipe Linkivi qui entre dans l'espace n'est jamais soumise
        if (store.Read(db => db.Clients.FirstOrDefault(c => c.Id == client) is { } t && ApprovalOps.NeedsApproval(db, t, user, screenIds)))
        {
            var r = await approvals.Submit(client, user!.Value, screenIds, note);
            if (r.Request != null) Log(client, screenIds, "Publication en attente d'approbation", Notifs.Warn, note);
            return new Result(0, r.Request, r.Error);
        }
        foreach (var id in screenIds) await notifier.PublishScreen(id);
        Log(client, screenIds, "Publication", Notifs.Ok, note);
        return new Result(screenIds.Count, null, null);
    }

    /// <summary>Historique : qui a publié, sur quels écrans (un seul nom pour un écran, le nombre sinon), avec leur liste de lecture.</summary>
    private void Log(Guid client, IReadOnlyCollection<Guid> ids, string what, string level, string? note)
    {
        var screens = store.Read(db => db.Screens.Where(s => ids.Contains(s.Id)).Select(s => (s.Id, s.Name, List: db.Playlists.FirstOrDefault(p => p.Id == s.PlaylistId)?.Name)).ToList());
        if (screens.Count == 0) return;
        var title = screens.Count == 1 ? $"{what} sur « {screens[0].Name} »" : $"{what} sur {screens.Count} écrans";
        var facts = new List<string[]> { new[] { "Écrans", string.Join(", ", screens.Select(s => s.Name)) } };
        foreach (var l in screens.Select(s => s.List).Where(n => n != null).Distinct().Take(3)) facts.Add(new[] { "Liste de lecture", l! });
        if (!string.IsNullOrWhiteSpace(note)) facts.Add(new[] { "Message", note! });
        history.By(ctx, History.Publish, title, level, screens.Count > 1 ? string.Join(", ", screens.Select(s => s.Name).Take(4)) : null, "screen", screens.Count == 1 ? screens[0].Id : null,
            screens[0].Name, "/", facts: facts);
    }

    /// <summary>Première diffusion d'un écran qu'on vient de connecter, ou mur créé par un administrateur : rien à approuver.</summary>
    public Task Initial(Guid screenId) => notifier.PublishScreen(screenId);
}
