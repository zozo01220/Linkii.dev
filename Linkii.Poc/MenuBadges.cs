namespace Linkii.Poc;

/// <summary>
/// Ce qu'une personne a déjà vu dans une page du menu (famille « screens », « playlists »…) : les éléments connus, ou la version de l'application pour Réglages.
/// Tout élément visible qui n'y figure pas et que la personne n'a pas créé elle-même est « nouveau ».
/// </summary>
public class BadgeSeen
{
    public Guid UserId { get; set; }
    public string Family { get; set; } = "";
    public List<Guid> Ids { get; set; } = new();
    public string? Mark { get; set; }
}

/// <summary>Pastille d'une entrée du menu : nombre d'éléments, nombre de nouveaux (rouge), écrans hors ligne (orange).</summary>
public record Badge(int Count, int New, int Offline)
{
    public static readonly Badge None = new(0, 0, 0);
}

/// <summary>
/// Pastilles du menu latéral (une par circuit). Elles se recalculent toutes seules, sans recharger la page : à chaque écriture dans la base
/// (<see cref="JsonStore.Changed"/>, regroupées sur une demi-seconde) et toutes les 20 secondes (un écran qui s'éteint n'écrit rien).
/// Rouge : un élément arrivé depuis la dernière visite de la page (partage, collègue, écran connecté…), jamais ce que la personne crée.
/// Orange (Écrans) : un écran qui a déjà été connecté ne répond plus. Ouvrir la page éteint son rouge ; Réglages n'a qu'un point rouge quand l'application a changé de version.
/// </summary>
public class MenuBadges(JsonStore root, TenantStore store, TenantContext ctx, PlaylistLibrary lists, MediaLibrary media) : IDisposable
{
    public const string Screens = "screens", Playlists = "playlists", MediaFam = "media", Compositions = "compositions", Members = "members", Integrations = "integrations", Settings = "settings";

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500), Tick = TimeSpan.FromSeconds(20), BumpFor = TimeSpan.FromMilliseconds(900);

    private Dictionary<string, Badge> current = new();
    private readonly Dictionary<string, DateTime> bumped = new();
    private Func<string?>? family;
    private bool withCompositions, admin, started;
    private Timer? tick, later;
    private int busy;

    /// <summary>Une pastille a changé : le menu doit se redessiner.</summary>
    public event Action? Changed;

    public Badge Of(string fam) => current.TryGetValue(fam, out var b) ? b : Badge.None;

    /// <summary>La pastille vient de gagner un nouveau (ou un écran vient de passer hors ligne) : elle rebondit.</summary>
    public bool Bumping(string fam) => bumped.TryGetValue(fam, out var t) && DateTime.UtcNow - t < BumpFor;

    /// <summary>
    /// Démarre le suivi du menu de la personne connectée. <paramref name="currentFamily"/> donne la page ouverte (ce qu'on y voit est lu : jamais rouge) ;
    /// <paramref name="compositions"/> et <paramref name="orgAdmin"/> disent quelles entrées le menu montre.
    /// </summary>
    public void Start(Func<string?> currentFamily, bool compositions, bool orgAdmin)
    {
        family = currentFamily; withCompositions = compositions; admin = orgAdmin;
        if (!started) { started = true; root.Changed += OnStoreChanged; tick = new Timer(_ => Refresh(), null, Tick, Tick); }
        Refresh();
    }

    /// <summary>Recalcule maintenant (changement de page, de droits…).</summary>
    public void Refresh()
    {
        if (!started || Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            var next = Compute();
            var changed = next.Count != current.Count || next.Any(kv => !current.TryGetValue(kv.Key, out var o) || o != kv.Value);
            foreach (var kv in next)
            {
                var old = current.GetValueOrDefault(kv.Key, Badge.None);
                if (kv.Value.New > old.New || kv.Value.Offline > old.Offline) bumped[kv.Key] = DateTime.UtcNow;
            }
            current = next;
            if (changed) Changed?.Invoke();
        }
        catch (Exception) { /* la base change pendant le calcul (ou la session se ferme) : on réessaie au prochain tour */ }
        finally { Volatile.Write(ref busy, 0); }
    }

    private void OnStoreChanged() { later?.Dispose(); later = new Timer(_ => Refresh(), null, Debounce, Timeout.InfiniteTimeSpan); }

    private Dictionary<string, Badge> Compute()
    {
        var me = ctx.UserId is { } uid && !ctx.IsImpersonating ? uid : (Guid?)null;
        var open = family?.Invoke();
        var now = DateTime.UtcNow;
        var res = new Dictionary<string, Badge>();

        // Écrans visibles (toutes aires) : propriétaire, hors ligne (déjà connectés puis silencieux, hors veille)
        var (screens, offline) = store.Read(d =>
        {
            var all = d.AllScreens.ToList();
            bool Down(Screen s) => s.Token != null && s.LastSeenUtc != null && !ScreenControl.Online(s, now)
                && !Sleep.State(d.Tenant, d.Root.Areas.FirstOrDefault(a => a.Id == s.AreaId), s, Helpers.Wall(d.Root.Areas, s) != null, now).Asleep;
            return (all.Select(s => (s.Id, s.OwnerId)).ToList(), all.Count(Down));
        });
        res[Screens] = Make(Screens, screens, offline);

        var rights = lists.PlaylistRights();
        var pl = store.Read(d => d.Playlists.Where(p => rights.GetValueOrDefault(p.Id) != FolderRight.None).Select(p => (p.Id, p.OwnerId)).ToList());
        res[Playlists] = Make(Playlists, pl);

        res[MediaFam] = Make(MediaFam, media.Entries().Select(m => (m.Id, m.OwnerId)).ToList());

        if (withCompositions)
            res[Compositions] = Make(Compositions, store.Read(d => d.Media.Where(m => m.AppId == Linkii.Poc.Compositions.AppId && CompositionRights.RightOf(d, m) != FolderRight.None).Select(m => (m.Id, m.OwnerId)).ToList()));

        if (admin)
            res[Members] = Make(Members, store.Read(d => d.Users.Where(u => !u.Disabled).Select(u => (u.Id, (Guid?)u.Id)).ToList()));

        res[Integrations] = Make(Integrations, store.Read(d => d.AppInstalls.Select(a => (a.Id, (Guid?)null)).ToList()));

        // Réglages : pas de nombre, seulement un point rouge quand l'application a changé de version depuis la dernière visite
        res[Settings] = new Badge(0, admin && me != null && VersionNew(me.Value, open == Settings) ? 1 : 0, 0);
        return res;

        Badge Make(string fam, List<(Guid Id, Guid? Owner)> items, int off = 0)
            => new(items.Count, me == null ? 0 : NewCount(me.Value, fam, items, open == fam), off);
    }

    /// <summary>Éléments visibles que la personne n'a pas encore vus et n'a pas créés. Au premier passage, tout ce qui existe est « déjà vu ». Sur la page elle-même, tout est lu.</summary>
    private int NewCount(Guid me, string fam, List<(Guid Id, Guid? Owner)> items, bool onPage)
    {
        var ids = items.Select(i => i.Id).ToHashSet();
        var seen = root.Read(d => d.BadgeSeen.FirstOrDefault(b => b.UserId == me && b.Family == fam)?.Ids.ToHashSet());
        if (seen == null || onPage)
        {
            if (seen == null || !seen.SetEquals(ids)) Remember(me, fam, ids.ToList(), null);
            return 0;
        }
        return items.Count(i => !seen.Contains(i.Id) && i.Owner != me);
    }

    private bool VersionNew(Guid me, bool onPage)
    {
        var mark = root.Read(d => d.BadgeSeen.FirstOrDefault(b => b.UserId == me && b.Family == Settings)?.Mark);
        if (mark == null || onPage)
        {
            if (mark != AppVersion.Short) Remember(me, Settings, new(), AppVersion.Short);
            return false;
        }
        return mark != AppVersion.Short;
    }

    private void Remember(Guid me, string fam, List<Guid> ids, string? mark) => root.Write(d =>
    {
        var row = d.BadgeSeen.FirstOrDefault(b => b.UserId == me && b.Family == fam);
        if (row == null) d.BadgeSeen.Add(row = new BadgeSeen { UserId = me, Family = fam });
        row.Ids = ids; row.Mark = mark;
    });

    public void Dispose()
    {
        if (started) root.Changed -= OnStoreChanged;
        started = false; tick?.Dispose(); later?.Dispose();
    }
}
