using System.Globalization;

namespace Linkii.Poc;

/// <summary>
/// Diffusion d'une liste (onglet « Diffusion » de la page d'une liste, maquette maquettes/liste-diffusion-allegee.html) : sur quels écrans publier la
/// modification, et quand (maintenant ou à une heure choisie, écran par écran). L'interrupteur « Publier » d'un écran ne retire jamais la liste : éteint,
/// l'écran garde sa version actuelle. Retirer la liste d'un écran passe par son menu ⋯, avec confirmation.
/// Partagé par l'en-tête (« Publier sur N écrans », visible depuis les trois onglets) et le tableau de l'onglet.
/// Rien n'est enregistré avant « Publier » : interrupteurs, heures et zones choisies restent en mémoire tant que la page est ouverte.
/// </summary>
public sealed class PlaylistDiffusion(TenantStore store, ScreenPublisher publisher, PublishSchedule sched, Toaster toast)
{
    public sealed class ZoneRow
    {
        public int Index { get; init; }
        public string? Other { get; init; }   // liste jouée par la zone quand ce n'est pas celle-ci (null : rien)
        public bool Init { get; init; }       // la liste y est dans la configuration actuelle de l'écran
    }

    public sealed class ScreenRow
    {
        public required Screen S { get; init; }
        public required Area A { get; init; }
        public required List<ZoneRow> Zones { get; init; }
        public bool Stale { get; init; }            // la liste (ou un réglage) a changé depuis la dernière publication de l'écran
        public DateTime? Scheduled { get; init; }   // publication programmée de cette liste déjà enregistrée, heure de l'organisation
        public bool On { get; set; }                // l'écran reçoit la prochaine publication
        public DateTime? When { get; set; }         // heure choisie pour cet écran (null : maintenant)
        public int Target { get; set; }             // écran qui n'a pas la liste : zone qui la recevra (0 = zone 1)
        public bool Offline => S.LastSeenUtc is null || DateTime.UtcNow - S.LastSeenUtc > TimeSpan.FromSeconds(90);
        public bool Has => Zones.Any(z => z.Init);
        public bool Multi => Zones.Count > 1;
        public bool Portrait => S.Orientation == "portrait";
        public double[] Sizes => ScreenLayouts.Find(S.Layout).Sizes;
        /// <summary>La zone porte la liste (actuellement, ou après la publication pour un écran qui la reçoit).</summary>
        public bool Shows(int zone) => Has ? Zones[zone].Init : On && zone == Target;
    }

    public Guid PlaylistId { get; private set; }
    public List<ScreenRow> Rows { get; private set; } = new();
    public bool ManyAreas { get; private set; }
    public int Outside { get; private set; }   // écrans hors du périmètre du membre qui jouent la liste
    public bool Busy { get; private set; }
    public string Timezone { get; private set; } = "Europe/Zurich";

    // choix de la personne, gardés d'un rechargement à l'autre tant qu'ils ne sont pas publiés
    private readonly Dictionary<Guid, bool> onSet = new();
    private readonly Dictionary<Guid, DateTime> whenSet = new();
    private readonly Dictionary<Guid, int> targetSet = new();
    private int revision = -1;

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public void Load(Guid playlistId)
    {
        if (playlistId != PlaylistId) { onSet.Clear(); whenSet.Clear(); targetSet.Clear(); revision = -1; }
        PlaylistId = playlistId;
        var (rows, many, outside, tz, rev) = store.Read(d =>
        {
            var areas = d.Root.Areas.Where(a => a.ClientId == d.ClientId).ToList();
            var pls = d.Playlists.ToList();
            var sch = PublishSchedule.Of(d.Root, d.ClientId).Where(x => x.S.PlaylistId == playlistId && !x.S.Remove).ToDictionary(x => x.S.ScreenId, x => x.Local);
            var list = new List<ScreenRow>();
            foreach (var s in d.AllScreens.Where(x => x.Token != null))
            {
                var a = areas.FirstOrDefault(x => x.Id == s.AreaId);
                if (a == null) continue;
                var z = a.Zones.FirstOrDefault(x => x.Id == s.ZoneId) ?? a.Zones.FirstOrDefault(x => x.IsDefault);
                if (z == null || z.Kind == Zone.Wall) continue;   // un mur porte sa liste par sa zone : elle se règle depuis la page Écrans
                var zones = s.ZonePlaylists().Select((id, i) => new ZoneRow
                {
                    Index = i, Init = id == playlistId,
                    Other = id is { } o && o != playlistId ? pls.FirstOrDefault(p => p.Id == o)?.Name ?? "une autre liste" : null
                }).ToList();
                var has = zones.Any(x => x.Init);
                list.Add(new ScreenRow { S = s, A = a, Zones = zones, Stale = has && Helpers.HasPending(s, pls), Scheduled = sch.TryGetValue(s.Id, out var at) ? at : null });
            }
            var outside = d.Access.Sees is { } sees ? d.Root.Screens.Count(s => s.ClientId == d.ClientId && s.Token != null && !sees(s) && s.ZonePlaylists().Contains(playlistId)) : 0;
            return (list, areas.Count > 1, outside, d.Tenant.Timezone, d.Playlists.FirstOrDefault(p => p.Id == playlistId)?.Revision ?? 0);
        });
        // le fil a changé : les écrans qui le diffusent repassent dans la prochaine publication
        if (revision >= 0 && rev != revision) foreach (var r in rows.Where(r => r.Has)) onSet.Remove(r.S.Id);
        revision = rev;
        foreach (var r in rows)
        {
            r.On = onSet.TryGetValue(r.S.Id, out var on) ? on : Default(r);
            r.When = whenSet.TryGetValue(r.S.Id, out var w) ? w : null;
            r.Target = targetSet.TryGetValue(r.S.Id, out var t) && t < r.Zones.Count ? t : 0;
        }
        Rows = rows.OrderBy(r => r.A.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.S.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        ManyAreas = many; Outside = outside; Timezone = tz;
    }

    /// <summary>D'office, un écran reçoit la publication s'il diffuse la liste et qu'elle a changé depuis sa dernière publication.</summary>
    private static bool Default(ScreenRow r) => r.Has && r.Stale;

    // ---------- État ----------
    public IEnumerable<ScreenRow> ToUpdate => Rows.Where(r => r.Has && r.Stale);   // écrans dont la version est en retard
    public List<ScreenRow> Chosen => Rows.Where(r => r.On).ToList();
    public int OnAir => Rows.Count(r => r.Has);
    public bool Changed => onSet.Count > 0 || whenSet.Count > 0 || targetSet.Count > 0;

    /// <summary>Libellé du bouton orange : publier, programmer, ou les deux.</summary>
    public string ButtonLabel()
    {
        var c = Chosen;
        var n = c.Count;
        var later = c.Count(r => r.When != null);
        var screens = $"{n} écran{(n > 1 ? "s" : "")}";
        return later == 0 || n == 0 ? $"Publier sur {screens}" : later == n ? $"Programmer sur {screens}" : $"Publier et programmer · {screens}";
    }

    // ---------- Gestes ----------
    public void SetOn(ScreenRow r, bool on)
    {
        r.On = on;
        if (on == Default(r)) onSet.Remove(r.S.Id); else onSet[r.S.Id] = on;
        if (!on) { r.When = null; whenSet.Remove(r.S.Id); }
    }

    public void SetAll(bool on) { foreach (var r in on ? Rows.Where(r => r.Has) : Rows) SetOn(r, on); }

    public void SetWhen(ScreenRow r, DateTime? at)
    {
        r.When = at;
        if (at is { } t) whenSet[r.S.Id] = t; else whenSet.Remove(r.S.Id);
    }

    public void SetTarget(ScreenRow r, int zone)
    {
        r.Target = zone;
        if (zone == 0) targetSet.Remove(r.S.Id); else targetSet[r.S.Id] = zone;
    }

    public void Undo()
    {
        onSet.Clear(); whenSet.Clear(); targetSet.Clear();
        foreach (var r in Rows) { r.On = Default(r); r.When = null; r.Target = 0; }
    }

    public static string Short(DateTime d) => d.ToString("d MMMM", Fr) + " à " + d.ToString("HH:mm");

    public DateTime Now()
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(Timezone)); } catch { return DateTime.Now; }
    }

    /// <summary>Zones qui portent la liste après la publication : celles d'aujourd'hui pour un écran qui la diffuse (la liste ne bouge pas), la zone choisie sinon.</summary>
    private static List<int> ZonesAfter(ScreenRow r) => r.Has ? r.Zones.Where(z => z.Init).Select(z => z.Index).ToList() : new List<int> { r.Target };

    /// <summary>
    /// Publie les écrans activés : maintenant, ou à l'heure choisie pour chacun. <paramref name="all"/> : une même heure pour tous (menu ▾ du bouton),
    /// <see cref="DateTime.MinValue"/> : tous maintenant.
    /// </summary>
    public async Task<bool> Publish(DateTime? all = null)
    {
        var list = Chosen;
        if (list.Count == 0 || Busy) return false;
        Busy = true;
        ScreenPublisher.Result? res = null;
        var now = new List<Guid>();
        int later = 0;
        try
        {
            var client = store.ClientId; var user = store.UserId;
            var clock = Now();
            foreach (var r in list)
            {
                var at = all == DateTime.MinValue ? null : all ?? r.When;
                var zones = ZonesAfter(r);
                if (at is { } t && t > clock) { sched.Schedule(client, r.S.Id, PlaylistId, t, user, zones: zones); later++; continue; }
                if (r.Scheduled != null) sched.Cancel(client, r.S.Id);   // une publication immédiate remplace la programmation
                var id = r.S.Id;
                if (!r.Has) store.Write(d => { if (d.AllScreens.FirstOrDefault(x => x.Id == id) is { } sc) PublishSchedule.AssignZones(sc, PlaylistId, zones); });
                now.Add(id);
            }
            if (now.Count > 0) res = await publisher.Publish(now);
        }
        finally { Busy = false; }
        foreach (var r in list) { onSet.Remove(r.S.Id); whenSet.Remove(r.S.Id); targetSet.Remove(r.S.Id); }
        Load(PlaylistId);
        string Count(int n) => $"{n} écran{(n > 1 ? "s" : "")}";
        if (res?.Error != null) toast.Error(res.Error);
        else if (res?.Pending == true) toast.Ok("Demande d'approbation envoyée" + (later > 0 ? $" · {Count(later)} programmé{(later > 1 ? "s" : "")}" : ""));
        else
        {
            var parts = new List<string>();
            if (now.Count > 0) parts.Add($"Publiée sur {Count(now.Count)}");
            if (later > 0) parts.Add($"programmée sur {Count(later)}");
            var left = ToUpdate.Count();
            var msg = string.Join(" · ", parts);
            msg = char.ToUpper(msg[0]) + msg[1..];
            toast.Ok(msg + (left > 0 ? $" · {Count(left)} encore à mettre à jour" : ""));
        }
        return true;
    }

    /// <summary>Retire la liste de l'écran (toutes ses zones) et le publie aussitôt : geste volontaire, demandé par le menu ⋯ et confirmé.</summary>
    public async Task Remove(ScreenRow r)
    {
        if (Busy) return;
        Busy = true;
        ScreenPublisher.Result? res = null;
        try
        {
            var id = r.S.Id;
            if (r.Scheduled != null) sched.Cancel(store.ClientId, id);
            store.Write(d => { if (d.AllScreens.FirstOrDefault(x => x.Id == id) is { } sc) PublishSchedule.AssignZones(sc, PlaylistId, Array.Empty<int>()); });
            res = await publisher.Publish(new[] { id });
        }
        finally { Busy = false; }
        onSet.Remove(r.S.Id); whenSet.Remove(r.S.Id); targetSet.Remove(r.S.Id);
        Load(PlaylistId);
        if (res?.Error != null) toast.Error(res.Error);
        else if (res?.Pending == true) toast.Ok("Demande d'approbation envoyée");
        else toast.Ok($"Liste retirée de « {r.S.Name} »");
    }

    /// <summary>Retire la publication programmée d'un écran.</summary>
    public void CancelSchedule(ScreenRow r)
    {
        sched.Cancel(store.ClientId, r.S.Id);
        Load(PlaylistId);
        toast.Ok($"Programmation retirée · {r.S.Name}");
    }
}
