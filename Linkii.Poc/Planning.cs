using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Linkii.Poc;

/// <summary>Plage horaire récurrente, éventuellement bornée par des dates (Slot et ce que reçoit le player).</summary>
public interface ITimeSlot
{
    int Days { get; }            // jours de la semaine, un bit chacun : lundi = 1 … dimanche = 64
    int From { get; }            // début, en minutes depuis minuit
    int To { get; }              // fin (exclue), en minutes depuis minuit : 1 440 = minuit
    string? DateFrom { get; }    // aaaa-mm-jj : premier jour de la période (vide : sans borne)
    string? DateTo { get; }      // aaaa-mm-jj : dernier jour (inclus)
}

/// <summary>
/// Créneau d'un planning de diffusion : pendant cette plage, la zone (ou l'écran) joue cette liste de lecture à la place de sa liste par défaut.
/// Sans créneau, la zone ou l'écran joue sa liste en permanence.
/// </summary>
public class Slot : ITimeSlot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlaylistId { get; set; }
    public int Days { get; set; }
    public int From { get; set; }
    public int To { get; set; }
    public string? DateFrom { get; set; }
    public string? DateTo { get; set; }

    public bool Dated => !string.IsNullOrEmpty(DateFrom) || !string.IsNullOrEmpty(DateTo);

    public Slot Copy() => (Slot)MemberwiseClone();
}

/// <summary>
/// Planning de diffusion : créneaux rattachés à une zone d'aire ou à un écran (facultatif, partout).
/// Priorité : créneaux propres à l'écran, puis ceux de sa zone, puis la liste par défaut (celle de l'écran ou du mur) ; à égalité, le créneau daté passe avant le récurrent.
/// Les heures sont celles du fuseau de l'aire (à défaut, celui de l'organisation), comme la mise en veille.
/// Chaque écran garde un instantané des listes qu'il peut jouer (<see cref="Screen.PublishedLists"/>), pour tourner hors ligne.
/// </summary>
public static class Planning
{
    public const int Day = 1440;
    public static readonly string[] DayNames = { "Lun", "Mar", "Mer", "Jeu", "Ven", "Sam", "Dim" };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Bit(int day) => 1 << day;
    public static bool HasDay(int days, int day) => (days & Bit(day)) != 0;

    public static string Hm(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    public static bool TryHm(string? s, out int minutes)
    {
        minutes = 0;
        if (!TimeOnly.TryParseExact(s, new[] { "H:mm", "HH:mm", "HH:mm:ss" }, Inv, DateTimeStyles.None, out var t)) return false;
        minutes = t.Hour * 60 + t.Minute;
        return true;
    }

    public static bool IsDate(string? s) => DateOnly.TryParseExact(s, "yyyy-MM-dd", Inv, DateTimeStyles.None, out _);

    /// <summary>« Lun – Ven », « Lun, Mer, Sam », « Tous les jours ».</summary>
    public static string DaysText(int days)
    {
        days &= 127;
        if (days == 127) return "Tous les jours";
        var list = Enumerable.Range(0, 7).Where(d => HasDay(days, d)).ToList();
        if (list.Count == 0) return "Aucun jour";
        // suites de jours consécutifs : « Lun – Ven », « Sam – Dim »
        var parts = new List<string>();
        for (var i = 0; i < list.Count;)
        {
            var j = i;
            while (j + 1 < list.Count && list[j + 1] == list[j] + 1) j++;
            parts.Add(j - i >= 2 ? $"{DayNames[list[i]]} – {DayNames[list[j]]}" : string.Join(", ", list.Skip(i).Take(j - i + 1).Select(d => DayNames[d])));
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    /// <summary>« Lun – Ven · 11:00 – 14:00 », suivi de la période si le créneau est daté.</summary>
    public static string Describe(ITimeSlot s) =>
        $"{DaysText(s.Days)} · {Hm(s.From)} – {Hm(s.To)}" + (Period(s) is { } p ? " · " + p : "");

    public static string? Period(ITimeSlot s)
    {
        string F(string d) => DateOnly.ParseExact(d, "yyyy-MM-dd", Inv).ToString("d MMM", CultureInfo.GetCultureInfo("fr-CH"));
        var a = IsDate(s.DateFrom) ? s.DateFrom : null;
        var b = IsDate(s.DateTo) ? s.DateTo : null;
        return a != null && b != null ? $"du {F(a)} au {F(b)}" : a != null ? $"dès le {F(a)}" : b != null ? $"jusqu'au {F(b)}" : null;
    }

    /// <summary>Erreur de saisie d'un créneau, ou null s'il est valide.</summary>
    public static string? Check(Slot s)
    {
        if ((s.Days & 127) == 0) return "Choisissez au moins un jour.";
        if (s.From < 0 || s.To > Day || s.From >= s.To) return "L'heure de fin doit suivre l'heure de début (au plus minuit).";
        if (!string.IsNullOrEmpty(s.DateFrom) && !IsDate(s.DateFrom) || !string.IsNullOrEmpty(s.DateTo) && !IsDate(s.DateTo)) return "Dates invalides.";
        if (IsDate(s.DateFrom) && IsDate(s.DateTo) && string.CompareOrdinal(s.DateFrom, s.DateTo) > 0) return "La date de fin précède celle de début.";
        return null;
    }

    /// <summary>Ce jour-là, à cette minute : le créneau est-il actif ?</summary>
    public static bool Matches(ITimeSlot s, DateOnly date, int minute)
    {
        if (!HasDay(s.Days, ((int)date.DayOfWeek + 6) % 7) || minute < s.From || minute >= s.To) return false;
        var iso = date.ToString("yyyy-MM-dd", Inv);
        return !(IsDate(s.DateFrom) && string.CompareOrdinal(iso, s.DateFrom) < 0) && !(IsDate(s.DateTo) && string.CompareOrdinal(iso, s.DateTo) > 0);
    }

    /// <summary>Deux créneaux se chevauchent-ils (un jour, une heure et une date en commun) ?</summary>
    public static bool Overlap(ITimeSlot a, ITimeSlot b)
    {
        if ((a.Days & b.Days & 127) == 0 || a.From >= b.To || b.From >= a.To) return false;
        static string? Lo(ITimeSlot s) => IsDate(s.DateFrom) ? s.DateFrom : null;
        static string? Hi(ITimeSlot s) => IsDate(s.DateTo) ? s.DateTo : null;
        return !(Hi(a) != null && Lo(b) != null && string.CompareOrdinal(Hi(a), Lo(b)) < 0) && !(Hi(b) != null && Lo(a) != null && string.CompareOrdinal(Hi(b), Lo(a)) < 0);
    }

    /// <summary>Créneaux d'un même niveau dans l'ordre de priorité : les datés d'abord, puis les récurrents (ordre de création conservé).</summary>
    public static List<Slot> Order(IEnumerable<Slot>? slots) => (slots ?? Enumerable.Empty<Slot>()).OrderByDescending(s => s.Dated).ToList();

    public static Zone? ZoneOf(Area? area, Screen s) => area?.Zones.FirstOrDefault(z => z.Id == s.ZoneId);

    /// <summary>Créneaux qui s'appliquent à l'écran, par priorité : les siens (s'il a un planning propre), puis ceux de sa zone.</summary>
    public static List<Slot> Effective(Area? area, Screen s) =>
        (s.OwnPlanning ? Order(s.Slots) : new List<Slot>()).Concat(Order(ZoneOf(area, s)?.Slots)).ToList();

    /// <summary>Créneau actif à cet instant parmi une liste déjà ordonnée (null : la liste par défaut).</summary>
    public static T? Pick<T>(IEnumerable<T> ordered, DateOnly date, int minute) where T : class, ITimeSlot =>
        ordered.FirstOrDefault(s => Matches(s, date, minute));

    public static DateTime LocalNow(Tenant t, Area? area, DateTime nowUtc)
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(Sleep.TimeZone(t, area)); } catch { tz = TimeZoneInfo.Utc; }
        return TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
    }

    /// <summary>Créneau actif sur l'écran à cet instant (heure locale de l'aire) ; null : sa liste par défaut.</summary>
    public static Slot? Active(Tenant t, Area? area, Screen s, DateTime nowUtc)
    {
        var l = LocalNow(t, area, nowUtc);
        return Pick(Effective(area, s), DateOnly.FromDateTime(l), l.Hour * 60 + l.Minute);
    }

    /// <summary>Ce que reçoit le player : fuseau et créneaux (dans l'ordre de priorité) avec le contenu de leur liste ; null : aucun planning.
    /// Un créneau dont la liste n'a pas encore été publiée sur l'écran n'est pas envoyé.</summary>
    public static object? ForPlayer(Tenant t, Area? area, Screen s, Func<List<PublishedItem>, List<PublishedItem>> shape)
    {
        var slots = Effective(area, s).Where(x => s.PublishedLists.ContainsKey(x.PlaylistId)).ToList();
        if (slots.Count == 0) return null;
        return new
        {
            tz = Sleep.TimeZone(t, area),
            slots = slots.Select(x => new
            {
                days = x.Days, from = x.From, to = x.To, dateFrom = x.DateFrom, dateTo = x.DateTo,
                items = shape(s.PublishedLists[x.PlaylistId]).Select(i => i.ForPlayer()).ToList()
            }).ToList()
        };
    }

    /// <summary>Empreinte des créneaux d'un écran : elle fait partie de sa révision, qui change dès qu'un créneau (ou la zone de l'écran) change.</summary>
    public static string Stamp(IEnumerable<Area> areas, Screen s)
    {
        var area = areas.FirstOrDefault(a => a.Id == s.AreaId);
        var slots = Effective(area, s);
        if (slots.Count == 0 && s.PublishedLists.Count == 0) return "";   // aucun planning : la révision ne change pas
        var raw = string.Join(";", slots.Select(x => $"{x.PlaylistId:N}/{x.Days}/{x.From}/{x.To}/{x.DateFrom}/{x.DateTo}/{s.PublishedLists.ContainsKey(x.PlaylistId)}")) + "|" + area?.SleepTz + "|" + s.PlanningRevision;
        return "|pn" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..8];
    }

    /// <summary>Découpe une journée en plages consécutives (début, fin, créneau actif ou null pour la liste par défaut), au quart d'heure près, selon les priorités.</summary>
    public static List<(int From, int To, Slot? Slot)> Segments(IEnumerable<Slot> ordered, DateOnly date)
    {
        var list = ordered.ToList();
        var res = new List<(int, int, Slot?)>();
        for (var m = 0; m < Day; m += 15)
        {
            var hit = Pick(list, date, m);
            if (res.Count > 0 && res[^1].Item3 == hit) res[^1] = (res[^1].Item1, m + 15, hit);
            else res.Add((m, m + 15, hit));
        }
        return res;
    }

    /// <summary>Écrans dont le planning dépend de la zone (tous ceux qui y sont) ou de l'écran lui-même.</summary>
    public static List<Screen> Affected(ClientDb d, Guid areaId, Guid? zoneId, Guid? screenId) =>
        d.Root.Screens.Where(s => s.ClientId == d.ClientId && s.AreaId == areaId && s.Token != null && (screenId != null ? s.Id == screenId : s.ZoneId == zoneId)).ToList();
}

/// <summary>Modifications d'un planning (membres de l'aire). Chaque méthode retourne un message d'erreur, ou null si c'est fait ; l'appelant publie ensuite les écrans touchés.</summary>
public static class PlanOps
{
    private static string? Allowed(ClientDb d, Guid areaId) =>
        d.Areas.FirstOrDefault(a => a.Id == areaId) == null ? "Aire introuvable."
        : d.Access.HasArea(areaId) ? null : "Vous n'avez pas accès à cette aire.";

    private static List<Slot>? ListOf(ClientDb d, Guid areaId, Guid targetId, bool screen, out string? error)
    {
        error = null;
        if (screen)
        {
            if (d.Root.Screens.FirstOrDefault(s => s.Id == targetId && s.ClientId == d.ClientId && s.AreaId == areaId && (d.Access.Sees?.Invoke(s) ?? true)) is not { } sc) { error = "Écran introuvable."; return null; }
            if (!sc.OwnPlanning) { error = "Cet écran suit le planning de sa zone."; return null; }
            return sc.Slots;
        }
        var z = d.Areas.FirstOrDefault(a => a.Id == areaId)?.Zones.FirstOrDefault(x => x.Id == targetId);
        if (z == null || !d.Access.SeesZone(z.Id)) { error = "Zone introuvable."; return null; }
        return z.Slots;
    }

    /// <summary>Ajoute ou modifie (même Id) un créneau sur la zone ou l'écran. Pour une zone, alsoZones recopie le créneau sur d'autres zones de l'aire.</summary>
    public static string? Save(ClientDb d, Guid areaId, Guid targetId, bool screen, Slot slot, IEnumerable<Guid>? alsoZones = null)
    {
        if (Allowed(d, areaId) is { } e) return e;
        if (Planning.Check(slot) is { } bad) return bad;
        if (!d.Root.Playlists.Any(p => p.Id == slot.PlaylistId && p.ClientId == d.ClientId && p.AreaId == areaId)) return "Choisissez une liste de lecture.";
        var list = ListOf(d, areaId, targetId, screen, out var err);
        if (list == null) return err;
        slot.DateFrom = Planning.IsDate(slot.DateFrom) ? slot.DateFrom : null;
        slot.DateTo = Planning.IsDate(slot.DateTo) ? slot.DateTo : null;
        slot.Days &= 127;
        var at = list.FindIndex(x => x.Id == slot.Id);
        if (at >= 0) list[at] = slot; else list.Add(slot);
        if (!screen && alsoZones != null)
        {
            var area = d.Areas.First(a => a.Id == areaId);
            foreach (var zid in alsoZones.Distinct().Where(z => z != targetId))
                if (area.Zones.FirstOrDefault(z => z.Id == zid) is { } z) { var c = slot.Copy(); c.Id = Guid.NewGuid(); z.Slots.Add(c); }
        }
        return null;
    }

    public static string? Delete(ClientDb d, Guid areaId, Guid targetId, bool screen, Guid slotId)
    {
        if (Allowed(d, areaId) is { } e) return e;
        var list = ListOf(d, areaId, targetId, screen, out var err);
        if (list == null) return err;
        return list.RemoveAll(x => x.Id == slotId) > 0 ? null : "Créneau introuvable.";
    }

    /// <summary>Donne un planning propre à l'écran (vide : il joue sa liste par défaut tant qu'il n'a pas de créneau propre, mais ceux de sa zone restent actifs).</summary>
    public static string? CreateOwn(ClientDb d, Guid areaId, Guid screenId)
    {
        if (Allowed(d, areaId) is { } e) return e;
        if (d.Root.Screens.FirstOrDefault(s => s.Id == screenId && s.ClientId == d.ClientId && s.AreaId == areaId) is not { } sc) return "Écran introuvable.";
        if (Planning.ZoneOf(d.Areas.FirstOrDefault(a => a.Id == areaId), sc) is { IsWall: true }) return "Un écran d'un mur suit le planning du mur.";
        sc.OwnPlanning = true;
        return null;
    }

    /// <summary>L'écran reprend le planning de sa zone : ses créneaux propres sont supprimés.</summary>
    public static string? RevertToZone(ClientDb d, Guid areaId, Guid screenId)
    {
        if (Allowed(d, areaId) is { } e) return e;
        if (d.Root.Screens.FirstOrDefault(s => s.Id == screenId && s.ClientId == d.ClientId && s.AreaId == areaId) is not { } sc) return "Écran introuvable.";
        sc.OwnPlanning = false;
        sc.Slots.Clear();
        return null;
    }
}
