using System.Globalization;

namespace Linkii.Poc;

/// <summary>
/// Calendrier de programmation (page Programmations, mode Expert) : une liste de lecture par défaut, obligatoire, et des événements
/// « diffuser une liste » ou « éteindre l'écran ». Un calendrier s'assigne à un écran ou à une borne depuis son tiroir (<see cref="Screen.CalendarId"/>) ;
/// hors événements, l'écran joue la liste par défaut du calendrier.
/// À l'enregistrement, le calendrier est « compilé » en créneaux (<see cref="Slot"/>) copiés sur chaque écran assigné (<see cref="Screen.Slots"/>,
/// <see cref="Screen.OffSlots"/>) : la publication, l'instantané des listes et le player sont ceux du planning.
/// Priorité : une extinction passe devant tout ; parmi les diffusions, une période datée passe devant une récurrence, puis la plus récente gagne.
/// </summary>
public class ScreenCalendar : IClientOwned, IOwned
{
    public Guid ClientId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? OwnerId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Liste jouée hors événements (obligatoire à la création).</summary>
    public Guid DefaultPlaylistId { get; set; }
    public List<CalEvent> Events { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Événement d'un calendrier. Heures et dates : celles du fuseau de l'aire de l'écran, comme le planning.</summary>
public class CalEvent
{
    public const string Play = "play", Off = "off";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = Play;
    /// <summary>Liste diffusée (événement <see cref="Play"/>).</summary>
    public Guid? PlaylistId { get; set; }
    /// <summary>Diffusion « toujours » : sans heures ni dates (tous les jours choisis, en entier). Jamais pour une extinction.</summary>
    public bool Always { get; set; }
    /// <summary>aaaa-mm-jj et HH:mm. Avec une récurrence, les dates bornent la répétition et peuvent rester vides (sans limite).</summary>
    public string? StartDate { get; set; }
    public string? StartTime { get; set; }
    public string? EndDate { get; set; }
    public string? EndTime { get; set; }
    /// <summary>Récurrence : chaque semaine, ces jours-là (bits lundi = 1 … dimanche = 64). Faux : « Ne se répète pas ».</summary>
    public bool Repeat { get; set; }
    public int Days { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public CalEvent Copy() => (CalEvent)MemberwiseClone();
}

public static class Calendars
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-CH");

    private static DateOnly? D(string? s) => DateOnly.TryParseExact(s, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d) ? d : null;
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);
    private static int? M(string? s) => Planning.TryHm(s, out var m) ? m : null;

    /// <summary>Erreur de saisie, ou null si l'événement est valide.</summary>
    public static string? Check(CalEvent e)
    {
        if (e.Kind != CalEvent.Play && e.Kind != CalEvent.Off) return "Type d'événement inconnu.";
        if (e.Kind == CalEvent.Play && (e.PlaylistId == null || e.PlaylistId == Guid.Empty)) return "Choisissez une liste de lecture.";
        if (e.Repeat && (e.Days & 127) == 0) return "Choisissez au moins un jour.";
        if (e.Kind == CalEvent.Play && e.Always) return null;
        if (M(e.StartTime) is not { } from || M(e.EndTime) is not { } to) return "Indiquez l'heure de début et l'heure de fin.";
        if (e.Repeat)
        {
            if (!string.IsNullOrEmpty(e.StartDate) && D(e.StartDate) == null || !string.IsNullOrEmpty(e.EndDate) && D(e.EndDate) == null) return "Dates invalides.";
            if (D(e.StartDate) is { } a && D(e.EndDate) is { } b && b < a) return "La date de fin précède celle de début.";
            if (from == (to == 0 ? Planning.Day : to)) return "L'heure de fin doit différer de l'heure de début.";
            return null;
        }
        if (D(e.StartDate) is not { } sd || D(e.EndDate) is not { } ed) return "Indiquez la date de début et la date de fin.";
        if (ed.ToDateTime(TimeOnly.MinValue).AddMinutes(to) <= sd.ToDateTime(TimeOnly.MinValue).AddMinutes(from)) return "La fin doit suivre le début.";
        return null;
    }

    private static Slot S(CalEvent e, int days, int from, int to, string? df, string? dt) =>
        new() { Id = e.Id, PlaylistId = e.PlaylistId ?? Guid.Empty, Days = days & 127, From = from, To = to, DateFrom = df, DateTo = dt };

    /// <summary>Créneaux équivalents à l'événement (une plage qui passe minuit est coupée en deux). Vide si l'événement est invalide.</summary>
    public static List<Slot> Compile(CalEvent e)
    {
        var res = new List<Slot>();
        if (Check(e) != null) return res;
        if (e.Kind == CalEvent.Play && e.Always) { res.Add(S(e, e.Repeat ? e.Days : 127, 0, Planning.Day, null, null)); return res; }
        var from = M(e.StartTime)!.Value; var to = M(e.EndTime)!.Value;
        if (e.Repeat)
        {
            string? df = D(e.StartDate) is { } a ? Iso(a) : null, dt = D(e.EndDate) is { } b ? Iso(b) : null;
            if (to == 0) to = Planning.Day;
            if (to > from) { res.Add(S(e, e.Days, from, to, df, dt)); return res; }
            // passe minuit : la fin tombe le lendemain de chaque jour choisi
            res.Add(S(e, e.Days, from, Planning.Day, df, dt));
            var next = ((e.Days << 1) | (e.Days >> 6)) & 127;
            if (to > 0) res.Add(S(e, next, 0, to, D(e.StartDate)?.AddDays(1) is { } a1 ? Iso(a1) : null, D(e.EndDate)?.AddDays(1) is { } b1 ? Iso(b1) : null));
            return res;
        }
        var sd = D(e.StartDate)!.Value; var ed = D(e.EndDate)!.Value;
        if (sd == ed) { res.Add(S(e, 127, from, to, Iso(sd), Iso(sd))); return res; }
        res.Add(S(e, 127, from, Planning.Day, Iso(sd), Iso(sd)));
        if (ed.DayNumber - sd.DayNumber > 1) res.Add(S(e, 127, 0, Planning.Day, Iso(sd.AddDays(1)), Iso(ed.AddDays(-1))));
        if (to > 0) res.Add(S(e, 127, 0, to, Iso(ed), Iso(ed)));
        return res;
    }

    private static bool Dated(CalEvent e) => !e.Repeat && !e.Always || !string.IsNullOrEmpty(e.StartDate) || !string.IsNullOrEmpty(e.EndDate);

    /// <summary>Diffusions dans l'ordre de priorité (datées d'abord, puis la plus récente), puis la liste par défaut sur toute la semaine.</summary>
    public static List<Slot> PlaySlots(ScreenCalendar c)
    {
        var res = c.Events.Where(e => e.Kind == CalEvent.Play).OrderByDescending(Dated).ThenByDescending(e => e.CreatedUtc).SelectMany(Compile).ToList();
        if (c.DefaultPlaylistId != Guid.Empty) res.Add(new Slot { Id = c.Id, PlaylistId = c.DefaultPlaylistId, Days = 127, From = 0, To = Planning.Day });
        return res;
    }

    public static List<Slot> OffSlots(ScreenCalendar c) => c.Events.Where(e => e.Kind == CalEvent.Off).SelectMany(Compile).ToList();

    /// <summary>Recopie le calendrier sur un écran (ou l'en détache, c null). Retourne vrai si ses créneaux ont changé.</summary>
    public static bool Stamp(Screen s, ScreenCalendar? c)
    {
        var before = Sig(s);
        s.CalendarId = c?.Id;
        s.Slots = c == null ? new() : PlaySlots(c);
        s.OffSlots = c == null ? new() : OffSlots(c);
        s.OwnPlanning = c != null;
        return Sig(s) != before;
    }

    private static string Sig(Screen s) => s.CalendarId + "|" + string.Join(";", s.Slots.Concat(s.OffSlots).Select(x => $"{x.PlaylistId:N}/{x.Days}/{x.From}/{x.To}/{x.DateFrom}/{x.DateTo}"));

    /// <summary>Écrans assignés à ce calendrier (toutes aires).</summary>
    public static List<Screen> ScreensOf(ClientDb d, Guid calendarId) => d.Root.Screens.Where(s => s.ClientId == d.ClientId && s.CalendarId == calendarId).ToList();

    /// <summary>Recopie le calendrier sur ses écrans ; retourne ceux qui ont un jeton (à prévenir).</summary>
    public static List<Guid> Apply(ClientDb d, ScreenCalendar c)
    {
        var res = new List<Guid>();
        foreach (var s in ScreensOf(d, c.Id)) { Stamp(s, c); if (s.Token != null) res.Add(s.Id); }
        return res;
    }

    // ---------- textes ----------

    public static string Hm(string? t) => M(t) is { } m ? Planning.Hm(m) : "";
    private static string Day(string? d) => D(d) is { } x ? x.ToString("d MMM yyyy", Fr) : "";

    /// <summary>« Lun – Ven · 07:00 – 11:00 », « Du 9 nov. 2026 07:00 au 10 nov. 2026 11:00 », « Toujours ».</summary>
    public static string Describe(CalEvent e)
    {
        if (e.Kind == CalEvent.Play && e.Always) return e.Repeat ? Planning.DaysText(e.Days) + " · toute la journée" : "Toujours";
        if (e.Repeat)
        {
            var bounds = (D(e.StartDate), D(e.EndDate)) switch
            {
                ({ }, { }) => $" · du {Day(e.StartDate)} au {Day(e.EndDate)}",
                ({ }, null) => $" · dès le {Day(e.StartDate)}",
                (null, { }) => $" · jusqu'au {Day(e.EndDate)}",
                _ => ""
            };
            return $"{Planning.DaysText(e.Days)} · {Hm(e.StartTime)} – {Hm(e.EndTime)}" + bounds;
        }
        return e.StartDate == e.EndDate ? $"Le {Day(e.StartDate)} · {Hm(e.StartTime)} – {Hm(e.EndTime)}" : $"Du {Day(e.StartDate)} {Hm(e.StartTime)} au {Day(e.EndDate)} {Hm(e.EndTime)}";
    }

    // ---------- migration de l'ancien planning (écran › zone › aire) ----------

    /// <summary>Une fois : chaque planning effectif d'écran devient un calendrier (les écrans qui suivaient la même zone ou aire le partagent).
    /// Les murs gardent leur planning de zone. La liste par défaut du calendrier est celle de l'écran.</summary>
    public static void Migrate(Db root)
    {
        const string key = "calendars-v1";
        if (root.Done.Contains(key)) return;
        var stamp = DateTime.UtcNow.AddDays(-1);
        foreach (var t in root.Clients)
        {
            var made = new Dictionary<string, ScreenCalendar>();
            foreach (var s in root.Screens.Where(x => x.ClientId == t.Id && x.CalendarId == null))
            {
                var area = root.Areas.FirstOrDefault(a => a.Id == s.AreaId);
                var zone = Planning.ZoneOf(area, s);
                if (zone?.IsWall == true) continue;
                var own = s.OwnPlanning ? Planning.Order(s.Slots) : new List<Slot>();
                var zs = Planning.Order(zone?.Slots); var ars = Planning.Order(area?.Slots);
                var all = own.Concat(zs).Concat(ars).ToList();
                if (all.Count == 0) { s.OwnPlanning = false; s.Slots.Clear(); continue; }
                var dflt = s.PlaylistId ?? all[0].PlaylistId;
                var keyCal = (own.Count > 0 ? "s" + s.Id : zs.Count > 0 ? "z" + zone!.Id : "a" + area!.Id) + "|" + dflt;
                if (!made.TryGetValue(keyCal, out var cal))
                {
                    var name = own.Count > 0 ? $"Planning de {s.Name}" : zs.Count > 0 ? $"Planning de la zone {zone!.Name}" : $"Planning de l'aire {area!.Name}";
                    cal = new ScreenCalendar { ClientId = t.Id, Name = name, DefaultPlaylistId = dflt, CreatedUtc = stamp };
                    // l'ancien ordre de priorité devient l'ordre de création (le plus récent gagne)
                    var i = 0;
                    foreach (var x in all)
                        cal.Events.Add(new CalEvent
                        {
                            Kind = CalEvent.Play, PlaylistId = x.PlaylistId, Repeat = true, Days = x.Days,
                            StartTime = Planning.Hm(x.From), EndTime = Planning.Hm(x.To % Planning.Day),
                            StartDate = x.DateFrom, EndDate = x.DateTo, CreatedUtc = stamp.AddSeconds(-(i++))
                        });
                    made[keyCal] = cal;
                    root.Calendars.Add(cal);
                }
                Stamp(s, cal);
            }
            foreach (var a in root.Areas.Where(a => a.ClientId == t.Id))
            {
                a.Slots.Clear(); a.OwnPlanning = false;
                foreach (var z in a.Zones.Where(z => !z.IsWall)) { z.Slots.Clear(); z.OwnPlanning = false; }
            }
        }
        root.Done.Add(key);
    }
}
