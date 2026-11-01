using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Publication faite à la main : le contenu « figé » d'un écran est ce que le test y a posé au moment de la demande.</summary>
public sealed class FakePublishing : IScreenPublishing
{
    public readonly Dictionary<Guid, List<PublishedItem>> Draft = new();
    public readonly List<Guid> Notified = new();
    public ScreenSnapshot Build(Db db, Screen s) => new() { Published = Draft.GetValueOrDefault(s.Id)?.Select(i => i.Clone()).ToList() ?? new(), Stamp = "stamp" };
    public void ApplySnapshot(Db db, Screen s, ScreenSnapshot snap) { s.Published = snap.Published; s.PublishedVersion++; s.PublishedUtc = DateTime.UtcNow; }
    public Task NotifyScreen(Guid screenId) { lock (Notified) Notified.Add(screenId); return Task.CompletedTask; }
    public string? AppName(string appId) => appId;
}

public sealed class FakeApprovalNotices : IApprovalNotices
{
    public readonly List<(Guid[] To, string Title, string Body, string Severity)> Sent = new();
    public Task Tell(Guid clientId, IReadOnlyCollection<Guid> users, string title, string body, string severity, string dedupe)
    {
        lock (Sent) Sent.Add((users.ToArray(), title, body, severity));
        return Task.CompletedTask;
    }
}

/// <summary>Approbation des publications (§162) : qui est soumis, qui approuve quoi, contenu figé, décision, rappel, péremption.</summary>
public class ApprovalTests : IDisposable
{
    private readonly ApiFixture f = new();
    private readonly FakePublishing pub = new();
    private readonly FakeApprovalNotices notices = new();
    private readonly ApprovalService svc;
    private readonly Zone zoneA = new() { Name = "Bâtiment A" }, zoneB = new() { Name = "Bâtiment B" };
    private readonly User admin, ines, marc, luc;
    private readonly Screen hall, accueil, atrium1, atrium2;

    public void Dispose() => f.Dispose();

    public ApprovalTests()
    {
        svc = new ApprovalService(f.Store, pub, notices, NullLogger<ApprovalService>.Instance);
        f.Tenant.ApprovalOn = true;
        var cid = f.Tenant.Id;
        admin = new User { Email = "sophie@horizon.ch", Name = "Sophie", Role = Roles.ClientAdmin, ClientId = cid };
        ines = Member("ines@horizon.ch", new MemberRights { ZoneIds = [zoneA.Id], SubmitApproval = true });
        marc = Member("marc@horizon.ch", new MemberRights { Screens = PageMode.Read, Approver = true, ApprovalScope = Approvals.ScopeChosen, ApprovalZoneIds = [zoneA.Id] });
        luc = Member("luc@horizon.ch", new MemberRights { Screens = PageMode.Read, Approver = true, ApprovalScope = Approvals.ScopeChosen, ApprovalZoneIds = [zoneB.Id] });
        hall = Screen("Hall d'accueil", zoneA.Id); accueil = Screen("Réception", zoneA.Id);
        atrium1 = Screen("Atrium 1", zoneB.Id); atrium2 = Screen("Atrium 2", zoneB.Id);
        f.Store.Write(db =>
        {
            db.Users.AddRange([admin, ines, marc, luc]);
        });
    }

    private User Member(string email, MemberRights r) => new() { Email = email, Name = email.Split('@')[0], Role = Roles.ClientMember, ClientId = f.Tenant.Id, Rights = r };

    private Screen Screen(string name, Guid zone)
    {
        var s = new Screen { ClientId = f.Tenant.Id, Name = name, ZoneId = zone, Token = Guid.NewGuid().ToString("N") };
        f.Store.Write(db => db.Screens.Add(s));
        return s;
    }

    private static PublishedItem Item(Guid id, int sec = 8) => new() { Id = id, Type = "image", DurationSec = sec, Url = "/media/" + id.ToString("N")[..6] + ".jpg" };

    private T Q<T>(Func<Db, T> read) => f.Store.Read(read);
    private ApprovalRequest Req(Guid id) => Q(db => db.ApprovalRequests.First(r => r.Id == id));
    private Guid[] Admins => [admin.Id];

    // ---------- Droits ----------

    [Fact]
    public void Approver_is_never_submitted_too()
    {
        var r = new MemberRights { SubmitApproval = true, Approver = true };
        r.Clean();
        Assert.True(r.Approver);
        Assert.False(r.SubmitApproval);   // pas d'auto-approbation : soumis ou approbateur, jamais les deux
        var copy = new MemberRights { Approver = true, ApprovalScope = Approvals.ScopeChosen, ApprovalZoneIds = [zoneA.Id, zoneA.Id] };
        copy.Clean();
        Assert.Single(copy.ApprovalZoneIds);
        Assert.Equal(Approvals.ScopeChosen, copy.Copy().ApprovalScope);
    }

    [Fact]
    public void Only_a_submitted_member_on_a_pro_plan_with_the_option_is_gated()
    {
        var ids = new[] { hall.Id };
        Assert.True(Q(db => ApprovalOps.NeedsApproval(db, f.Tenant, ines.Id, ids)));
        Assert.False(Q(db => ApprovalOps.NeedsApproval(db, f.Tenant, admin.Id, ids)));   // un administrateur publie toujours directement
        Assert.False(Q(db => ApprovalOps.NeedsApproval(db, f.Tenant, marc.Id, ids)));
        Assert.False(Q(db => ApprovalOps.NeedsApproval(db, f.Tenant, null, ids)));   // équipe Linkivi dans l'espace
        f.Tenant.ApprovalOn = false;
        Assert.False(Q(db => ApprovalOps.NeedsApproval(db, f.Tenant, ines.Id, ids)));
        f.Tenant.ApprovalOn = true; f.Tenant.Plan = Plans.Base;
        Assert.False(Q(db => ApprovalOps.NeedsApproval(db, f.Tenant, ines.Id, ids)));   // plan Pro seulement
    }

    [Fact]
    public void Approvers_follow_their_zones_then_administrators_then_nobody()
    {
        Assert.Equal([marc.Id], Q(db => ApprovalOps.Approvers(db, f.Tenant, [hall.Id, accueil.Id])).Select(u => u.Id));
        Assert.Equal([luc.Id], Q(db => ApprovalOps.Approvers(db, f.Tenant, [atrium1.Id])).Select(u => u.Id));
        // un mur à cheval sur deux zones : aucun approbateur ne couvre tout, les administrateurs reçoivent la demande
        Assert.Equal(Admins, Q(db => ApprovalOps.Approvers(db, f.Tenant, [hall.Id, atrium1.Id])).Select(u => u.Id));
        f.Tenant.ApprovalNoApprover = Approvals.Direct;
        Assert.Empty(Q(db => ApprovalOps.Approvers(db, f.Tenant, [hall.Id, atrium1.Id])));
        Assert.False(Q(db => ApprovalOps.NeedsApproval(db, f.Tenant, ines.Id, [hall.Id, atrium1.Id])));   // publication directe
    }

    [Fact]
    public void An_approver_chosen_by_screen_covers_only_that_screen()
    {
        marc.Rights!.ApprovalZoneIds.Clear(); marc.Rights.ApprovalScreenIds.Add(hall.Id);
        Assert.Equal([marc.Id], Q(db => ApprovalOps.Approvers(db, f.Tenant, [hall.Id])).Select(u => u.Id));
        Assert.Equal(Admins, Q(db => ApprovalOps.Approvers(db, f.Tenant, [accueil.Id])).Select(u => u.Id));
    }

    [Fact]
    public void An_approver_with_everything_he_sees_follows_his_page_rights()
    {
        marc.Rights!.ApprovalScope = Approvals.ScopeAll;   // lecture seule sur Écrans : il voit tout
        Assert.Equal([marc.Id], Q(db => ApprovalOps.Approvers(db, f.Tenant, [hall.Id, atrium1.Id])).Select(u => u.Id));
        marc.Rights.Screens = PageMode.Own; marc.Rights.ZoneIds = [zoneB.Id];   // il gère la zone B seulement
        Assert.Equal(Admins, Q(db => ApprovalOps.Approvers(db, f.Tenant, [hall.Id])).Select(u => u.Id));
        Assert.Equal(new[] { marc.Id, luc.Id }, Q(db => ApprovalOps.Approvers(db, f.Tenant, [atrium1.Id])).Select(u => u.Id));
    }

    // ---------- Demande ----------

    [Fact]
    public async Task Submit_freezes_the_content_and_publishes_nothing()
    {
        var keep = Guid.NewGuid();
        pub.Draft[hall.Id] = [Item(keep)];
        var r = (await svc.Submit(f.Tenant.Id, ines.Id, [hall.Id], "Pour la journée du 14")).Request!;
        Assert.Equal(1, r.Number);
        Assert.Equal(ApprovalStatus.Wait, r.Status);
        Assert.Equal("Pour la journée du 14", r.Note);
        Assert.Equal(0, Q(db => db.Screens.First(s => s.Id == hall.Id).PublishedVersion));   // l'écran garde sa version
        Assert.Single(notices.Sent);
        Assert.Equal([marc.Id], notices.Sent[0].To);
        Assert.Contains("Hall d'accueil", notices.Sent[0].Title);
    }

    [Fact]
    public async Task The_summary_compares_with_what_the_screen_shows()
    {
        var (same, gone, changed, fresh) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        f.Store.Write(db => { var s = db.Screens.First(x => x.Id == hall.Id); s.Published = [Item(same), Item(gone), Item(changed, 8)]; s.PublishedVersion = 1; });
        pub.Draft[hall.Id] = [Item(same), Item(changed, 12), Item(fresh)];
        var r = (await svc.Submit(f.Tenant.Id, ines.Id, [hall.Id])).Request!;
        Assert.Equal((1, 1, 1), (r.Added, r.Removed, r.Changed));
        Assert.Equal("+1 élément, −1 élément, 1 modifié", ApprovalOps.Summary(r));
        Assert.Equal(8, r.Lines.First(l => l.Change == ApprovalLine.Mod).PreviousSec);
    }

    [Fact]
    public async Task Approving_publishes_the_frozen_content_even_if_the_draft_changed_since()
    {
        var first = Guid.NewGuid();
        pub.Draft[hall.Id] = [Item(first)];
        var r = (await svc.Submit(f.Tenant.Id, ines.Id, [hall.Id])).Request!;
        pub.Draft[hall.Id] = [Item(Guid.NewGuid()), Item(Guid.NewGuid())];   // l'auteur retouche sa liste ensuite
        Assert.Null(await svc.Approve(f.Tenant.Id, r.Id, marc.Id, "Marc", false, "Bon pour moi"));
        var s = Q(db => db.Screens.First(x => x.Id == hall.Id));
        Assert.Equal(1, s.PublishedVersion);
        Assert.Equal([first], s.Published.Select(i => i.Id));   // ce que l'approbateur a vu
        var done = Req(r.Id);
        Assert.Equal((ApprovalStatus.Approved, "Marc"), (done.Status, done.DecidedByName));
        Assert.Null(done.Screens[0].Snapshot);   // le contenu figé est lâché une fois publié
        Assert.Equal([hall.Id], pub.Notified);
        Assert.Equal([ines.Id], notices.Sent.Last().To);
        Assert.Contains("approuvée", notices.Sent.Last().Title.ToLowerInvariant());
    }

    [Fact]
    public async Task Refusing_needs_a_reason_and_leaves_the_screen_alone()
    {
        pub.Draft[hall.Id] = [Item(Guid.NewGuid())];
        var r = (await svc.Submit(f.Tenant.Id, ines.Id, [hall.Id])).Request!;
        Assert.NotNull(await svc.Refuse(f.Tenant.Id, r.Id, marc.Id, "Marc", false, "  "));
        Assert.Equal(ApprovalStatus.Wait, Req(r.Id).Status);
        Assert.Null(await svc.Refuse(f.Tenant.Id, r.Id, marc.Id, "Marc", false, "Le prix est faux."));
        var done = Req(r.Id);
        Assert.Equal(ApprovalStatus.Refused, done.Status);
        Assert.Equal("Le prix est faux.", done.Comment);
        Assert.Equal(0, Q(db => db.Screens.First(s => s.Id == hall.Id).PublishedVersion));
        Assert.Empty(pub.Notified);
        Assert.Contains("Le prix est faux.", notices.Sent.Last().Body);
        Assert.Equal(Notifs.Error, notices.Sent.Last().Severity);
    }

    [Fact]
    public async Task Only_a_covering_approver_or_an_administrator_decides_and_never_the_author()
    {
        pub.Draft[hall.Id] = [Item(Guid.NewGuid())];
        var r = (await svc.Submit(f.Tenant.Id, ines.Id, [hall.Id])).Request!;
        Assert.NotNull(await svc.Approve(f.Tenant.Id, r.Id, luc.Id, "Luc", false, null));   // sa zone est l'autre
        Assert.NotNull(await svc.Approve(f.Tenant.Id, r.Id, ines.Id, "Inès", false, null));   // l'auteur
        Assert.NotNull(await svc.Approve(f.Tenant.Id, r.Id, ines.Id, "Inès", true, null));    // même s'il passait pour administrateur
        Assert.Equal(ApprovalStatus.Wait, Req(r.Id).Status);
        Assert.Null(await svc.Approve(f.Tenant.Id, r.Id, admin.Id, "Sophie", true, null));
        Assert.Equal(ApprovalStatus.Approved, Req(r.Id).Status);
        Assert.NotNull(await svc.Approve(f.Tenant.Id, r.Id, marc.Id, "Marc", false, null));   // déjà décidée
    }

    [Fact]
    public async Task A_newer_request_replaces_the_open_one_for_the_same_screen()
    {
        pub.Draft[hall.Id] = [Item(Guid.NewGuid())];
        var first = (await svc.Submit(f.Tenant.Id, ines.Id, [hall.Id])).Request!;
        var second = (await svc.Submit(f.Tenant.Id, ines.Id, [hall.Id])).Request!;
        Assert.Equal(ApprovalStatus.Replaced, Req(first.Id).Status);
        Assert.Contains($"n° {second.Number}", Req(first.Id).Comment);
        Assert.Equal(ApprovalStatus.Wait, Req(second.Id).Status);
        Assert.Equal(2, second.Number);
    }

    [Fact]
    public async Task Resubmitting_restarts_the_validation_with_the_current_draft()
    {
        var old = Guid.NewGuid();
        pub.Draft[hall.Id] = [Item(old)];
        var first = (await svc.Submit(f.Tenant.Id, ines.Id, [hall.Id])).Request!;
        var now = Guid.NewGuid();
        pub.Draft[hall.Id] = [Item(now)];
        var second = (await svc.Resubmit(f.Tenant.Id, ines.Id, first.Id)).Request!;
        Assert.Equal(ApprovalStatus.Replaced, Req(first.Id).Status);
        Assert.Equal([now], second.Screens[0].Snapshot!.Published.Select(i => i.Id));
        Assert.NotNull((await svc.Resubmit(f.Tenant.Id, marc.Id, second.Id)).Error);   // seul son auteur la met à jour
    }

    [Fact]
    public async Task A_wall_makes_a_single_request()
    {
        pub.Draft[atrium1.Id] = pub.Draft[atrium2.Id] = [Item(Guid.NewGuid())];
        var r = (await svc.Submit(f.Tenant.Id, ines.Id, [atrium1.Id, atrium2.Id])).Request!;
        Assert.Equal(2, r.Screens.Count);
        Assert.Equal(1, Q(db => db.ApprovalRequests.Count));
        Assert.Equal([luc.Id], notices.Sent[0].To);   // Luc couvre la zone B : une seule notification pour tout le mur
    }

    [Fact]
    public async Task A_screen_deleted_meanwhile_is_dropped_and_none_left_makes_the_request_expire()
    {
        pub.Draft[hall.Id] = pub.Draft[accueil.Id] = [Item(Guid.NewGuid())];
        var r = (await svc.Submit(f.Tenant.Id, ines.Id, [hall.Id, accueil.Id])).Request!;
        f.Store.Write(db => db.Screens.RemoveAll(s => s.Id == hall.Id));
        Assert.Null(await svc.Approve(f.Tenant.Id, r.Id, marc.Id, "Marc", false, null));
        Assert.Equal([accueil.Id], pub.Notified);
        Assert.Equal(1, Q(db => db.Screens.First(s => s.Id == accueil.Id).PublishedVersion));

        var r2 = (await svc.Submit(f.Tenant.Id, ines.Id, [accueil.Id])).Request!;
        f.Store.Write(db => db.Screens.RemoveAll(s => s.Id == accueil.Id));
        Assert.Null(await svc.Approve(f.Tenant.Id, r2.Id, marc.Id, "Marc", false, null));
        Assert.Equal(ApprovalStatus.Expired, Req(r2.Id).Status);
    }

    [Fact]
    public async Task The_author_can_withdraw_but_not_someone_else()
    {
        pub.Draft[hall.Id] = [Item(Guid.NewGuid())];
        var r = (await svc.Submit(f.Tenant.Id, ines.Id, [hall.Id])).Request!;
        Assert.NotNull(svc.Withdraw(f.Tenant.Id, r.Id, marc.Id, false));
        Assert.Null(svc.Withdraw(f.Tenant.Id, r.Id, ines.Id, false));
        Assert.Equal(ApprovalStatus.Withdrawn, Req(r.Id).Status);
        Assert.NotNull(svc.Withdraw(f.Tenant.Id, r.Id, ines.Id, false));   // plus en attente
    }

    // ---------- Visibilité ----------

    [Fact]
    public async Task Each_person_sees_their_own_requests_and_those_they_can_decide()
    {
        pub.Draft[hall.Id] = pub.Draft[atrium1.Id] = [Item(Guid.NewGuid())];
        var inHall = (await svc.Submit(f.Tenant.Id, ines.Id, [hall.Id])).Request!;
        var inAtrium = (await svc.Submit(f.Tenant.Id, ines.Id, [atrium1.Id])).Request!;
        var t = f.Tenant;
        Assert.Equal([inHall.Id], Q(db => ApprovalOps.Visible(db, t, marc.Id, false)).Select(r => r.Id));
        Assert.Equal([inAtrium.Id], Q(db => ApprovalOps.Visible(db, t, luc.Id, false)).Select(r => r.Id));
        Assert.Equal(2, Q(db => ApprovalOps.Visible(db, t, ines.Id, false)).Count);   // les siennes
        Assert.Equal(2, Q(db => ApprovalOps.Visible(db, t, admin.Id, true)).Count);
        Assert.Equal(1, Q(db => ApprovalOps.PendingFor(db, t.Id, marc.Id, false)));
        Assert.Equal(2, Q(db => ApprovalOps.PendingFor(db, t.Id, admin.Id, true)));
        Assert.Equal(0, Q(db => ApprovalOps.PendingFor(db, t.Id, ines.Id, false)));   // l'auteur n'a rien à décider
    }

    [Fact]
    public void The_menu_entry_concerns_submitted_people_approvers_and_administrators()
    {
        var t = f.Tenant;
        Assert.True(Q(db => ApprovalOps.Involved(db, t, ines.Id, false)));
        Assert.True(Q(db => ApprovalOps.Involved(db, t, marc.Id, false)));
        Assert.True(Q(db => ApprovalOps.Involved(db, t, admin.Id, true)));
        var other = Member("tom@horizon.ch", new MemberRights());
        f.Store.Write(db => db.Users.Add(other));
        Assert.False(Q(db => ApprovalOps.Involved(db, t, other.Id, false)));
        t.ApprovalOn = false;
        Assert.False(Q(db => ApprovalOps.Involved(db, t, ines.Id, false)));
    }

    // ---------- Rappel, péremption, plan ----------

    [Fact]
    public async Task Approvers_are_reminded_once_then_the_request_expires()
    {
        pub.Draft[hall.Id] = [Item(Guid.NewGuid())];
        var r = (await svc.Submit(f.Tenant.Id, ines.Id, [hall.Id])).Request!;
        var t0 = r.CreatedUtc;
        await svc.Tick(t0.AddHours(2));
        Assert.Single(notices.Sent);   // rien avant 24 h
        await svc.Tick(t0.AddHours(25));
        await svc.Tick(t0.AddHours(30));
        Assert.Equal(2, notices.Sent.Count);   // un seul rappel
        Assert.Equal([marc.Id], notices.Sent[1].To);
        Assert.Contains("Toujours à approuver", notices.Sent[1].Title);
        await svc.Tick(t0.AddDays(8));
        Assert.Equal(ApprovalStatus.Expired, Req(r.Id).Status);
        Assert.Equal([ines.Id], notices.Sent.Last().To);
        Assert.Null(Req(r.Id).Screens[0].Snapshot);
    }

    [Fact]
    public async Task Waiting_requests_are_refused_when_the_plan_or_the_option_goes_away()
    {
        pub.Draft[hall.Id] = pub.Draft[accueil.Id] = [Item(Guid.NewGuid())];
        var a = (await svc.Submit(f.Tenant.Id, ines.Id, [hall.Id])).Request!;
        f.Tenant.Plan = Plans.Base;   // retour sous Pro
        await svc.Tick(DateTime.UtcNow);
        var done = Req(a.Id);
        Assert.Equal(ApprovalStatus.Refused, done.Status);
        Assert.Equal("Linkivi", done.DecidedByName);
        Assert.Contains("republiez directement", done.Comment);
        Assert.Equal([ines.Id], notices.Sent.Last().To);
        Assert.Equal(0, Q(db => db.Screens.First(s => s.Id == hall.Id).PublishedVersion));   // rien n'est publié à sa place

        f.Tenant.Plan = Plans.Growth;
        var b = (await svc.Submit(f.Tenant.Id, ines.Id, [accueil.Id])).Request!;
        f.Tenant.ApprovalOn = false;   // option coupée
        await svc.Tick(DateTime.UtcNow);
        Assert.Equal(ApprovalStatus.Refused, Req(b.Id).Status);
    }

    [Fact]
    public async Task Old_decided_requests_are_purged_after_the_retention_period()
    {
        pub.Draft[hall.Id] = [Item(Guid.NewGuid())];
        var r = (await svc.Submit(f.Tenant.Id, ines.Id, [hall.Id])).Request!;
        await svc.Refuse(f.Tenant.Id, r.Id, marc.Id, "Marc", false, "Non.");
        await svc.Tick(DateTime.UtcNow.AddMonths(6));
        Assert.Equal(1, Q(db => db.ApprovalRequests.Count));
        await svc.Tick(DateTime.UtcNow.AddMonths(13));
        Assert.Equal(0, Q(db => db.ApprovalRequests.Count));
    }

    // ---------- Notifications adressées ----------

    [Fact]
    public void A_targeted_notification_is_seen_only_by_its_recipients_and_untargeted_ones_only_by_administrators()
    {
        var center = new NotificationCenter(f.Store);
        var cid = f.Tenant.Id;
        center.Add(cid, Approvals.Kind, Notifs.Warn, "Demande à approuver", "Inès", "/approvals", null, "approval:1", null, [marc.Id]);
        center.Add(cid, Alerts.Billing, Notifs.Warn, "Paiement refusé", "", "/");
        Assert.Equal(["Demande à approuver"], center.For(cid, marc.Id).Select(n => n.Title));
        Assert.Empty(center.For(cid, ines.Id));   // un utilisateur ne voit jamais les alertes des administrateurs
        Assert.Equal(["Paiement refusé"], center.For(cid, admin.Id).Select(n => n.Title));
        Assert.Equal(1, center.Unread(cid, marc.Id));
        center.ReadAll(cid, marc.Id);
        Assert.Equal(0, center.Unread(cid, marc.Id));
        Assert.Equal(1, center.Unread(cid, admin.Id));   // lire n'efface rien chez les autres
        Assert.Equal("shield", Notifs.Category(Approvals.Kind).Icon);
    }

    // ---------- Contournement ----------

    [Fact]
    public void No_page_publishes_a_screen_without_going_through_the_publisher()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "Linkii.Poc", "Components"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var offenders = Directory.EnumerateFiles(Path.Combine(dir!, "Linkii.Poc", "Components"), "*.razor", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p).Contains("Notifier.PublishScreen(")).Select(Path.GetFileName).ToList();
        Assert.True(offenders.Count == 0, "À remplacer par ScreenPublisher : " + string.Join(", ", offenders));
    }
}
