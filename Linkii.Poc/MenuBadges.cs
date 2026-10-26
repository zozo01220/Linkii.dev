namespace Linkii.Poc;

/// <summary>
/// Ce qu'une personne a déjà vu dans une page du menu (famille « screens », « playlists »…) : les éléments connus (<see cref="Ids"/>), ceux qui lui étaient
/// déjà partagés (<see cref="Shared"/>, null : ancien enregistrement), ou la version de l'application pour Réglages (<see cref="Mark"/>).
/// </summary>
public class BadgeSeen
{
    public Guid UserId { get; set; }
    public string Family { get; set; } = "";
    public List<Guid> Ids { get; set; } = new();
    public List<Guid>? Shared { get; set; }
    public string? Mark { get; set; }
}

/// <summary>Pastille d'une entrée du menu : nombre d'éléments, nombre de nouveaux (rouge), écrans hors ligne (orange).</summary>
public record Badge(int Count, int New, int Offline)
{
    public static readonly Badge None = new(0, 0, 0);
}

/// <summary>
/// Pastilles du menu latéral et des onglets (une par circuit). Elles se recalculent toutes seules, sans recharger la page : à chaque écriture dans la base
/// (<see cref="JsonStore.Changed"/>, regroupées sur une demi-seconde) et toutes les 20 secondes (un écran qui s'éteint n'écrit rien).
/// <para>Rouge : un élément visible que la personne n'a pas créé et n'a pas encore vu, ou qu'on vient de lui partager (même s'il était déjà visible de tous :
/// une liste à la racine). Il compte dans l'onglet qui le montre (« Partagés avec moi », sinon « Toute l'organisation »). Il est lu quand la page affiche cet onglet
/// (<see cref="View"/>, appelé par <c>NewMark</c>), qui le fait alors rebondir (<see cref="TakeFresh"/>).</para>
/// <para>Orange (Écrans) : un écran qui a déjà été connecté ne répond plus. Réglages : point rouge quand l'application a changé de version.
/// Membres (pas d'onglets) est lu dès que la page est ouverte ; Intégrations n'a que le nombre.</para>
/// </summary>
public class MenuBadges(JsonStore root, TenantStore store, TenantContext ctx, PlaylistLibrary lists, MediaLibrary media) : IDisposable
{
    public const string Screens = "screens", Playlists = "playlists", MediaFam = "media", Compositions = "compositions", Members = "members", Integrations = "integrations", Settings = "settings";

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500), Tick = TimeSpan.FromSeconds(20), BumpFor = TimeSpan.FromMilliseconds(900);

    /// <summary>Un élément d'une famille : propriétaire, place dans les onglets (null : pas d'onglets), partagé avec la personne, dossier ou non.</summary>
    private record Item(Guid Id, Guid? Owner, LibraryTab.Info? Info, bool Folder);

    private readonly object gate = new();
    private Dictionary<string, Badge> current = new();
    private Dictionary<(string, string), int> tabNew = new();
    private readonly Dictionary<string, DateTime> bumped = new();
    private readonly Dictionary<string, List<Guid>> fresh = new();
    private Func<string?>? family;
    private bool withCompositions, admin, started;
    private Timer? tick, later;
    private (string? Fam, string? Tab) reading;   // page et onglet que la personne regarde : ce qui y est nouveau est lu au prochain calcul

    /// <summary>Une pastille a changé : le menu (ou un onglet) doit se redessiner.</summary>
    public event Action? Changed;

    public Badge Of(string fam) => current.TryGetValue(fam, out var b) ? b : Badge.None;

    /// <summary>Nombre de nouveaux éléments montrés par cet onglet (« shared », « all »…) de la page.</summary>
    public int TabNew(string fam, string tab) => tabNew.GetValueOrDefault((fam, tab));

    /// <summary>La pastille vient de gagner un nouveau (ou un écran vient de passer hors ligne) : elle rebondit.</summary>
    public bool Bumping(string fam) => bumped.TryGetValue(fam, out var t) && DateTime.UtcNow - t < BumpFor;

    /// <summary>
    /// Démarre le suivi du menu de la personne connectée. <paramref name="currentFamily"/> donne la page ouverte ;
    /// <paramref name="compositions"/> et <paramref name="orgAdmin"/> disent quelles entrées le menu montre.
    /// </summary>
    public void Start(Func<string?> currentFamily, bool compositions, bool orgAdmin)
    {
        family = currentFamily; withCompositions = compositions; admin = orgAdmin;
        if (!started) { started = true; root.Changed += OnStoreChanged; tick = new Timer(_ => Refresh(), null, Tick, Tick); }
        Refresh();
    }

    /// <summary>
    /// La page affiche cet onglet (null : une page sans onglets) : ce qui y est nouveau est lu. Rend les éléments qui viennent d'être lus, à faire rebondir.
    /// </summary>
    public List<Guid> View(string fam, string? tab)
    {
        lock (gate) reading = (fam, tab);
        Refresh();
        lock (gate) reading = default;   // seul un affichage de la page lit : un élément qui arrive pendant qu'on la regarde reste « nouveau » jusqu'au prochain affichage
        return TakeFresh(fam);
    }

    /// <summary>Éléments lus depuis le dernier appel (ils viennent d'apparaître sur la page) ; la liste est vidée.</summary>
    public List<Guid> TakeFresh(string fam)
    {
        lock (gate) { var l = fresh.GetValueOrDefault(fam) ?? new(); fresh.Remove(fam); return l; }
    }

    /// <summary>Recalcule maintenant (changement de page, de droits…).</summary>
    public void Refresh()
    {
        if (!started) return;
        bool changed;
        lock (gate)
        {
            try
            {
                var next = Compute(out var nextTabs);
                changed = next.Count != current.Count || next.Any(kv => !current.TryGetValue(kv.Key, out var o) || o != kv.Value)
                    || nextTabs.Count != tabNew.Count || nextTabs.Any(kv => tabNew.GetValueOrDefault(kv.Key) != kv.Value);
                foreach (var kv in next)
                {
                    var old = current.GetValueOrDefault(kv.Key, Badge.None);
                    if (kv.Value.New > old.New || kv.Value.Offline > old.Offline) bumped[kv.Key] = DateTime.UtcNow;
                }
                current = next; tabNew = nextTabs;
            }
            catch (Exception) { return; /* la base change pendant le calcul (ou la session se ferme) : on réessaie au prochain tour */ }
        }
        if (changed) Changed?.Invoke();
    }

    private void OnStoreChanged() { later?.Dispose(); later = new Timer(_ => Refresh(), null, Debounce, Timeout.InfiniteTimeSpan); }

    private Dictionary<string, Badge> Compute(out Dictionary<(string, string), int> tabs)
    {
        var me = ctx.UserId is { } uid && !ctx.IsImpersonating ? uid : (Guid?)null;
        var open = family?.Invoke();
        var now = DateTime.UtcNow;
        var res = new Dictionary<string, Badge>();
        var tabCounts = new Dictionary<(string, string), int>();

        // Écrans visibles (toutes aires) : propriétaire, hors ligne (déjà connectés puis silencieux, hors veille)
        var (screens, offline) = store.Read(d =>
        {
            var all = d.AllScreens.ToList();
            bool Down(Screen s) => s.Token != null && s.LastSeenUtc != null && !ScreenControl.Online(s, now)
                && !Sleep.State(d.Tenant, d.Root.Areas.FirstOrDefault(a => a.Id == s.AreaId), s, Helpers.Wall(d.Root.Areas, s) != null, now).Asleep;
            return (all.Select(s => new Item(s.Id, s.OwnerId, new LibraryTab.Info(s.OwnerId, s.OwnerId, false), false)).ToList(), all.Count(Down));
        });
        res[Screens] = Eval(Screens, screens, true, offline);

        var rights = lists.PlaylistRights();
        var tabsOf = lists.ListTabs();
        var pl = store.Read(d => d.Playlists.Where(p => rights.GetValueOrDefault(p.Id) != FolderRight.None)
            .Select(p => new Item(p.Id, p.OwnerId, tabsOf.GetValueOrDefault(p.Id), false)).ToList());
        pl.AddRange(lists.Folders().Select(r => new Item(r.Folder.Id, r.Folder.OwnerId, r.Tab, true)));
        res[Playlists] = Eval(Playlists, pl, true);

        var fileTabs = media.FileTabs();
        var files = media.Entries().Select(m => new Item(m.Id, m.OwnerId, fileTabs.GetValueOrDefault(m.Id), false)).ToList();
        files.AddRange(media.Folders().Select(r => new Item(r.Folder.Id, r.Folder.OwnerId, r.Tab, true)));
        res[MediaFam] = Eval(MediaFam, files, true);

        if (withCompositions)
        {
            var comps = store.Read(d => d.Media.Where(m => m.AppId == Linkii.Poc.Compositions.AppId && CompositionRights.RightOf(d, m) != FolderRight.None)
                .Select(m => new Item(m.Id, m.OwnerId, CompositionRights.TabOf(d, ShareKind.Composition, m.Id, m.OwnerId, m.FolderId), false)).ToList()
                .Concat(CompositionFolderOps.Rows(d).Select(r => new Item(r.Folder.Id, r.Folder.OwnerId, r.Tab, true))).ToList());
            res[Compositions] = Eval(Compositions, comps, true);
        }

        if (admin)
            res[Members] = Eval(Members, store.Read(d => d.Users.Where(u => !u.Disabled).Select(u => new Item(u.Id, u.Id, null, false)).ToList()), false);

        // Intégrations : seulement le nombre. Les apps de base s'ajoutent d'elles-mêmes à la première visite d'une organisation : ce ne sont pas des nouveautés.
        res[Integrations] = Eval(Integrations, store.Read(d => d.AppInstalls.Select(a => new Item(a.Id, null, null, false)).ToList()), false, track: false);

        // Réglages : pas de nombre, seulement un point rouge quand l'application a changé de version depuis la dernière visite
        res[Settings] = new Badge(0, admin && me != null && VersionNew(me.Value, open == Settings) ? 1 : 0, 0);
        tabs = tabCounts;
        return res;

        // Rouge : élément visible, que la personne n'a pas créé, qu'elle n'a pas encore vu ou qu'on vient de lui partager. Il est lu quand elle regarde l'onglet qui le montre.
        Badge Eval(string fam, List<Item> items, bool tabbed, int off = 0, bool track = true)
        {
            var ids = items.Select(i => i.Id).ToHashSet();
            var sharedIds = items.Where(i => i.Info?.SharedIn == true).Select(i => i.Id).ToHashSet();
            var count = items.Count(i => !i.Folder);
            if (me == null || !track) return new Badge(count, 0, off);
            var row = root.Read(d => d.BadgeSeen.FirstOrDefault(b => b.UserId == me && b.Family == fam) is { } r ? (Ids: r.Ids.ToHashSet(), Shared: r.Shared?.ToHashSet()) : default);
            if (row.Ids == null)   // premier passage : tout ce qui existe est déjà vu
            {
                Remember(me.Value, fam, ids, sharedIds);
                return new Badge(count, 0, off);
            }
            var seenShared = row.Shared ?? sharedIds;   // ancien enregistrement : les partages actuels sont déjà vus
            var news = items.Where(i => i.Owner != me && (!row.Ids.Contains(i.Id) || i.Info?.SharedIn == true && !seenShared.Contains(i.Id))).ToList();

            // lu : la page affiche l'onglet qui montre ces éléments (Membres, Intégrations : dès que la page est ouverte)
            var lookingHere = tabbed ? reading.Fam == fam : open == fam;
            var readTab = tabbed ? reading.Tab : null;
            var read = !lookingHere ? new List<Item>() : news.Where(i => readTab == null || readTab == LibraryTab.All || TabOf(i) == readTab).ToList();
            if (read.Count > 0)
            {
                var f = fresh.TryGetValue(fam, out var fl) ? fl : fresh[fam] = new();
                f.AddRange(read.Select(i => i.Id).Where(id => !f.Contains(id)));
            }
            var still = news.Except(read).ToList();
            var stillIds = still.Select(i => i.Id).ToHashSet();

            if (tabbed)
                foreach (var g in still.GroupBy(TabOf)) tabCounts[(fam, g.Key)] = g.Count();

            // ce qui reste « nouveau » reste hors de la mémoire ; tout le reste (vu, lu, créé par soi) y entre
            var newIds = ids.Where(id => row.Ids.Contains(id) || !stillIds.Contains(id)).ToHashSet();
            var newShared = sharedIds.Where(id => seenShared.Contains(id) || !stillIds.Contains(id)).ToHashSet();
            if (!newIds.SetEquals(row.Ids) || row.Shared == null || !newShared.SetEquals(row.Shared)) Remember(me.Value, fam, newIds, newShared);
            return new Badge(count, still.Count, off);
        }

        string TabOf(Item i)
        {
            var info = i.Info ?? new LibraryTab.Info(i.Owner, i.Owner, false);
            return LibraryTab.IsMine(info, me, admin) ? LibraryTab.Mine : LibraryTab.InTab(LibraryTab.Shared, info, me, admin) ? LibraryTab.Shared : LibraryTab.All;
        }
    }

    private bool VersionNew(Guid me, bool onPage)
    {
        var mark = root.Read(d => d.BadgeSeen.FirstOrDefault(b => b.UserId == me && b.Family == Settings)?.Mark);
        if (mark == null || onPage)
        {
            if (mark != AppVersion.Short) Remember(me, Settings, new HashSet<Guid>(), null, AppVersion.Short);
            return false;
        }
        return mark != AppVersion.Short;
    }

    private void Remember(Guid me, string fam, HashSet<Guid> ids, HashSet<Guid>? shared, string? mark = null) => root.Write(d =>
    {
        var row = d.BadgeSeen.FirstOrDefault(b => b.UserId == me && b.Family == fam);
        if (row == null) d.BadgeSeen.Add(row = new BadgeSeen { UserId = me, Family = fam });
        row.Ids = ids.ToList(); row.Shared = shared?.ToList(); row.Mark = mark;
    });

    public void Dispose()
    {
        if (started) root.Changed -= OnStoreChanged;
        started = false; tick?.Dispose(); later?.Dispose();
    }
}
