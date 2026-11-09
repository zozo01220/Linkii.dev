using System.Globalization;

namespace Linkii.Poc;

// =====================================================================================
//  Application « Réservation de salle » (docs/reservation-salle/ANALYSE-v3.md) : données et règles pures.
//  Une salle reste un calendrier partagé (SharedCalendar.Room) ; l'application en est la porte d'entrée,
//  ajoute le type d'écran « Écran de salle » (Screen.Room) et la réservation depuis la tablette.
//  Linkivi ne modifie jamais un rendez-vous : il n'écrit que pour créer une réservation faite sur la tablette.
// =====================================================================================

/// <summary>Comment Linkivi crée un rendez-vous dans une salle.</summary>
public static class RoomModes
{
    /// <summary>Affichage seul : aucune réservation possible.</summary>
    public const string None = "none";
    /// <summary>Événement créé dans l'agenda de la salle (agenda du compte, ou agenda Google en écriture).</summary>
    public const string Direct = "direct";
    /// <summary>Événement créé dans l'agenda du compte connecté, la salle invitée comme ressource (boîte de salle Microsoft, ressource Google) : elle accepte ou refuse seule.</summary>
    public const string Invite = "invite";
    /// <summary>Réservation gardée par Linkivi (lien ICS) : visible sur les écrans Linkivi, pas dans Outlook ni Google.</summary>
    public const string Local = "local";

    public static bool CanBook(string? mode) => mode is Direct or Invite or Local;
}

/// <summary>Choix d'une source à l'installation : afficher seulement, ou afficher et réserver (ICS : réservations Linkivi).</summary>
public static class RoomPerms
{
    public const string Read = "read", Write = "write", Local = "local";
}

/// <summary>Réglages de l'application, communs à toutes les salles de l'organisation (pas de règles par salle en V1).</summary>
public class RoomAppSettings
{
    /// <summary>Durées proposées sur la tablette, en minutes.</summary>
    public List<int> Durations { get; set; } = new() { 15, 30, 45, 60, 120 };
    /// <summary>Durée maximale d'une réservation (0 : la journée).</summary>
    public int MaxMin { get; set; } = 120;
    /// <summary>Jours réservables : bits lundi = 1 … dimanche = 64 (lundi-vendredi = 31), comme les calendriers de programmation.</summary>
    public int Days { get; set; } = 31;
    /// <summary>Horaires réservables, en minutes depuis minuit (heure de l'organisation).</summary>
    public int FromMin { get; set; } = 7 * 60;
    public int ToMin { get; set; } = 19 * 60;
    /// <summary>Temps laissé libre avant le rendez-vous suivant (mode expert).</summary>
    public int BufferMin { get; set; }
    /// <summary>Titre du rendez-vous créé (mode expert).</summary>
    public string BookTitle { get; set; } = RoomRules.DefaultTitle;
    /// <summary>Réservation par QR : sur une tablette non tactile, un QR ouvre sur le téléphone le choix de la durée (et un commentaire facultatif).</summary>
    public bool Qr { get; set; }
    /// <summary>Début et fin d'une réservation faite maintenant : real (heure et minute réelles), down (début au quart d'heure précédent), up (début au quart d'heure suivant), both (début au quart précédent, fin au quart suivant).</summary>
    public string Rounding { get; set; } = RoomRounding.Real;
    /// <summary>Choix par source (m365, google…) : <see cref="RoomPerms"/>.</summary>
    public Dictionary<string, string> Sources { get; set; } = new();
    /// <summary>Sources dont le droit d'écrire a été perdu (jeton révoqué, accès retiré) : leurs salles sont en affichage seul jusqu'à une nouvelle autorisation.</summary>
    public List<string> LostRights { get; set; } = new();
    /// <summary>+1 à chaque changement qui touche les portes (règles, installation, salles) : les écrans de salle relisent leur configuration.</summary>
    public int Revision { get; set; }
    public DateTime? InstalledUtc { get; set; }

    public RoomAppSettings Copy() => new()
    {
        Durations = Durations.ToList(), MaxMin = MaxMin, Days = Days, FromMin = FromMin, ToMin = ToMin, BufferMin = BufferMin, BookTitle = BookTitle, Qr = Qr, Rounding = Rounding,
        Sources = new(Sources), LostRights = LostRights.ToList(), Revision = Revision, InstalledUtc = InstalledUtc
    };
}

/// <summary>Écran de salle : la porte d'une salle, réglée dans l'assistant « Nouvel écran de salle ».</summary>
public class RoomScreen
{
    /// <summary>light | dark (panneau de la journée).</summary>
    public string Theme { get; set; } = "light";
    /// <summary>Boutons de durée sur la tablette (réservation).</summary>
    public bool Buttons { get; set; } = true;
    /// <summary>Titre des réunions sur la porte (sinon seulement « Occupée »).</summary>
    public bool Titles { get; set; }
    /// <summary>+1 à chaque modification : la tablette relit sa configuration.</summary>
    public int Revision { get; set; } = 1;
}

/// <summary>Réservation faite sur un écran de salle (journal de 90 jours ; seule trace des réservations locales d'un lien ICS).</summary>
public class RoomBooking
{
    public const string Pending = "pending", Confirmed = "confirmed", Refused = "refused", Failed = "failed", Cancelled = "cancelled";

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RoomId { get; set; }
    public Guid ScreenId { get; set; }
    public string ScreenName { get; set; } = "";
    /// <summary>Commentaire saisi par la personne (réservation par QR) : il remplace « Réservé sur place » ; null : le titre des règles.</summary>
    public string? Title { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public string Mode { get; set; } = RoomModes.Local;
    public string State { get; set; } = Pending;
    /// <summary>Identifiant de l'événement chez Microsoft ou Google (null : réservation locale, ou pas encore écrite).</summary>
    public string? ProviderEventId { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Refus de la salle déjà montré sur la porte.</summary>
    public bool Shown { get; set; }

    /// <summary>La réservation occupe la salle (en attente de la salle ou confirmée).</summary>
    public bool Holds => State is Pending or Confirmed;
}

/// <summary>Un créneau occupé d'une salle : rendez-vous de l'agenda ou réservation Linkivi pas encore lue dans l'agenda.</summary>
public record RoomInterval(DateTime StartUtc, DateTime EndUtc, string Title, bool AllDay, bool Linkivi);

/// <summary>État d'une salle à un instant : free | soon | busy, jusqu'à quand, titre (vide si masqué), créneaux suivants de la journée.</summary>
public record RoomNow(string State, DateTime? UntilUtc, string Title, List<RoomInterval> Next, bool Linkivi = false);

/// <summary>Une durée proposée sur la tablette : possible, ou grisée avec la raison.</summary>
/// <summary>Arrondi du début et de la fin d'une réservation (réglage de l'application).</summary>
public static class RoomRounding
{
    public const string Real = "real", Down = "down", Up = "up", Both = "both";
    public static readonly string[] All = { Real, Down, Up, Both };
    public static string Label(string r) => r switch { Down => "Au quart d'heure inférieur", Up => "Au quart d'heure supérieur", Both => "Quart d'heure inférieur et supérieur", _ => "Heure et minute réelles" };
}

public record RoomOption(int Minutes, bool Ok, string? Why, DateTime? Start = null, DateTime? End = null);

/// <summary>Règles pures de l'application (testées) : état d'une salle, durées possibles, fusion des réservations, mode d'écriture.</summary>
public static class RoomRules
{
    public const string AppId = "room-booking";
    public const string DefaultTitle = "Réservé sur place";
    public static readonly int[] AllDurations = { 15, 30, 45, 60, 120, 180 };
    public static readonly int[] MaxChoices = { 30, 60, 120, 180, 240, 0 };
    public const int RetentionDays = 90;
    public const string GoogleResourceSuffix = "@resource.calendar.google.com";

    public static TimeZoneInfo Zone(string? id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(id) ? "UTC" : id); }
        catch { return TimeZoneInfo.Utc; }
    }

    public static DateTime Local(DateTime utc, TimeZoneInfo tz) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
    public static DateTime Utc(DateTime local, TimeZoneInfo tz) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
    public static DateTime DayStartUtc(DateTime nowUtc, TimeZoneInfo tz) => Utc(Local(nowUtc, tz).Date, tz);
    public static string Hm(DateTime utc, TimeZoneInfo tz) => Local(utc, tz).ToString("HH:mm", CultureInfo.InvariantCulture);
    public static string Label(int minutes) => minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60:00}";
    public static string MaxLabel(int max) => max <= 0 ? "Journée" : Label(max);
    public static string Hours(int min) => $"{min / 60:00}:{min % 60:00}";

    /// <summary>Bit d'un jour de la semaine (lundi = 1 … dimanche = 64).</summary>
    public static int DayBit(DayOfWeek d) => 1 << (((int)d + 6) % 7);

    private static readonly string[] DayShort = { "lun", "mar", "mer", "jeu", "ven", "sam", "dim" };

    /// <summary>« lun-ven 07:00-19:00 ».</summary>
    public static string DaysLabel(int days)
    {
        var on = Enumerable.Range(0, 7).Where(i => (days >> i & 1) == 1).ToList();
        if (on.Count == 0) return "aucun jour";
        if (on.Count == 7) return "tous les jours";
        var contiguous = on.Last() - on.First() + 1 == on.Count;
        return contiguous && on.Count > 2 ? $"{DayShort[on.First()]}-{DayShort[on.Last()]}" : string.Join(", ", on.Select(i => DayShort[i]));
    }

    public static string Summary(RoomAppSettings r) =>
        $"{string.Join(" · ", r.Durations.OrderBy(d => d).Select(Label))} — max {MaxLabel(r.MaxMin).ToLowerInvariant()} — {DaysLabel(r.Days)} {Hours(r.FromMin)}-{Hours(r.ToMin)}";

    /// <summary>Réglages vérifiés (bornes, durées connues, au moins une durée).</summary>
    public static RoomAppSettings Clean(RoomAppSettings r)
    {
        var c = r.Copy();
        c.Durations = c.Durations.Where(AllDurations.Contains).Distinct().OrderBy(d => d).ToList();
        if (c.Durations.Count == 0) c.Durations = new() { 15, 30, 45, 60, 120 };
        c.MaxMin = MaxChoices.Contains(c.MaxMin) ? c.MaxMin : 120;
        c.Days &= 127;
        c.FromMin = Math.Clamp(c.FromMin / 15 * 15, 0, 23 * 60 + 45);
        c.ToMin = Math.Clamp(c.ToMin / 15 * 15, 15, 24 * 60);
        if (c.ToMin <= c.FromMin) c.ToMin = Math.Min(24 * 60, c.FromMin + 60);
        c.Rounding = RoomRounding.All.Contains(c.Rounding) ? c.Rounding : RoomRounding.Real;
        c.BufferMin = c.BufferMin is 0 or 5 or 10 or 15 ? c.BufferMin : 0;
        c.BookTitle = string.IsNullOrWhiteSpace(c.BookTitle) ? DefaultTitle : c.BookTitle.Trim().Length > 60 ? c.BookTitle.Trim()[..60] : c.BookTitle.Trim();
        return c;
    }

    /// <summary>Préréglages de l'assistant : Standard, Réunions courtes, Salles à la journée.</summary>
    public static (List<int> Durations, int Max) Preset(string id) => id switch
    {
        "court" => (new() { 15, 30, 45, 60 }, 60),
        "jour" => (new() { 60, 120, 180 }, 0),
        _ => (new() { 15, 30, 45, 60, 120 }, 120)
    };

    /// <summary>
    /// Rendez-vous de l'agenda + réservations Linkivi qui occupent la salle. Une réservation déjà lue dans l'agenda (même début et même fin, à 2 min près)
    /// n'est pas comptée deux fois : le créneau de l'agenda prend son titre générique (« Réservé sur place »), montré même quand les titres sont masqués.
    /// </summary>
    public static List<RoomInterval> Merge(IEnumerable<EventDto> events, IEnumerable<RoomBooking> bookings, bool titles, string bookTitle)
    {
        var list = events.Where(e => e.EndUtc > e.StartUtc).Select(e => new RoomInterval(e.StartUtc, e.EndUtc, titles ? e.Title : "", e.AllDay, false)).ToList();
        foreach (var b in bookings.Where(b => b.Holds && b.EndUtc > b.StartUtc))
        {
            var i = list.FindIndex(e => Math.Abs((e.StartUtc - b.StartUtc).TotalMinutes) <= 2 && Math.Abs((e.EndUtc - b.EndUtc).TotalMinutes) <= 2);
            if (i >= 0) list[i] = list[i] with { Title = b.Title ?? bookTitle, Linkivi = true };
            else list.Add(new RoomInterval(b.StartUtc, b.EndUtc, b.Title ?? bookTitle, false, true));
        }
        return list.OrderBy(x => x.StartUtc).ThenBy(x => x.EndUtc).ToList();
    }

    private static List<RoomInterval> Blocking(IEnumerable<RoomInterval> evs, bool allDay) =>
        evs.Where(e => e.EndUtc > e.StartUtc && (!e.AllDay || allDay)).OrderBy(e => e.StartUtc).ToList();

    /// <summary>
    /// État de la salle à l'instant now (même règle que roomState de player/composition.js) : occupée pendant un rendez-vous (prolongée tant qu'un autre commence avant sa fin),
    /// « bientôt occupée » dans les soonMin minutes avant le suivant, sinon libre jusqu'au prochain rendez-vous du jour.
    /// </summary>
    public static RoomNow State(DateTime nowUtc, IEnumerable<RoomInterval> evs, int soonMin, bool allDay, TimeZoneInfo tz)
    {
        var list = Blocking(evs, allDay);
        var cur = list.FirstOrDefault(e => e.StartUtc <= nowUtc && nowUtc < e.EndUtc);
        var end = nowUtc;
        if (cur != null)
        {
            end = cur.EndUtc;
            bool grew;
            do { grew = false; foreach (var e in list) if (e.StartUtc <= end && e.EndUtc > end) { end = e.EndUtc; grew = true; } } while (grew);
        }
        var today = Local(nowUtc, tz).Date;
        var from = cur != null ? end : nowUtc;
        var next = list.Where(e => e.StartUtc >= from && Local(e.StartUtc, tz).Date == today).Take(6).ToList();
        if (cur != null) return new RoomNow("busy", end, cur.Title, next, cur.Linkivi);
        var nx = list.FirstOrDefault(e => e.StartUtc > nowUtc);
        if (nx != null && soonMin > 0 && nx.StartUtc - nowUtc <= TimeSpan.FromMinutes(soonMin)) return new RoomNow("soon", nx.StartUtc, nx.Title, next);
        return new RoomNow("free", nx != null && Local(nx.StartUtc, tz).Date == today ? nx.StartUtc : null, "", next);
    }

    /// <summary>
    /// Début et fin d'une réservation de <paramref name="minutes"/> faite à l'instant now selon l'arrondi choisi (voir <see cref="RoomRounding"/>). Le début ne remonte jamais
    /// avant la fin d'un rendez-vous qui vient de se terminer.
    /// </summary>
    public static (DateTime Start, DateTime End) Span(DateTime now, int minutes, string? rounding, IEnumerable<RoomInterval> blocking)
    {
        const long q = 15 * TimeSpan.TicksPerMinute;
        static DateTime Floor(DateTime t) => new(t.Ticks - t.Ticks % q, DateTimeKind.Utc);
        static DateTime Ceil(DateTime t) => t.Ticks % q == 0 ? t : Floor(t).AddMinutes(15);
        var start = rounding is RoomRounding.Down or RoomRounding.Both ? Floor(now) : rounding == RoomRounding.Up ? Ceil(now) : now;
        foreach (var e in blocking.OrderBy(e => e.EndUtc))
            if (e.EndUtc > start && e.EndUtc <= now) start = e.EndUtc;   // un rendez-vous vient de finir : on ne réserve pas par-dessus
        var end = rounding == RoomRounding.Both ? Ceil(now.AddMinutes(minutes)) : start.AddMinutes(minutes);
        if (end <= start) end = start.AddMinutes(minutes);
        return (start, end);
    }

    /// <summary>
    /// Durées proposées sur la tablette à l'instant now (début à la minute courante) : une durée D est possible si D ≤ durée max, si la réservation finit
    /// avant le rendez-vous suivant moins le tampon et dans les horaires réservables. Sinon elle est grisée avec la raison. Salle occupée : aucune durée.
    /// </summary>
    public static List<RoomOption> Options(DateTime nowUtc, IEnumerable<RoomInterval> evs, RoomAppSettings rules, bool allDay, TimeZoneInfo tz)
    {
        var now = new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var list = Blocking(evs, allDay);
        if (list.Any(e => e.StartUtc <= now && now < e.EndUtc)) return new();
        var local = Local(now, tz);
        var minute = (int)local.TimeOfDay.TotalMinutes;
        var open = (rules.Days & DayBit(local.DayOfWeek)) != 0 && minute >= rules.FromMin && minute < rules.ToMin;
        var closeUtc = Utc(local.Date.AddMinutes(rules.ToMin), tz);
        var res = new List<RoomOption>();
        foreach (var d in rules.Durations.Where(AllDurations.Contains).Distinct().OrderBy(d => d))
        {
            if (rules.MaxMin > 0 && d > rules.MaxMin) continue;   // au-delà de la durée maximale : pas proposée du tout
            var (start, end) = Span(now, d, rules.Rounding, list);
            var clash = list.FirstOrDefault(e => e.StartUtc < end.AddMinutes(rules.BufferMin) && e.EndUtc > start);
            if (!open) res.Add(new RoomOption(d, false, "Hors horaires", start, end));
            else if (clash != null) res.Add(new RoomOption(d, false, "Conflit à " + Hm(clash.StartUtc, tz), start, end));
            else if (end > closeUtc) res.Add(new RoomOption(d, false, "Hors horaires", start, end));
            else res.Add(new RoomOption(d, true, null, start, end));
        }
        return res;
    }

    /// <summary>
    /// Mode d'écriture d'une salle d'après sa source, sa référence et le choix fait pour la source (googleRole : accès du compte à un agenda Google, s'il est connu).
    /// Microsoft : agenda du compte (« cal:… ») = direct, adresse d'une boîte de salle = invitation. Google : ressource = invitation, agenda en écriture = direct.
    /// ICS : réservations locales. CalDAV et Exchange sur site : affichage seul en V1.
    /// </summary>
    public static string WriteModeOf(string source, string calRef, string? perm, string? googleRole, bool rightsLost = false)
    {
        if (source == "ics") return perm == RoomPerms.Read ? RoomModes.None : RoomModes.Local;
        if (perm != RoomPerms.Write || rightsLost) return RoomModes.None;
        return source switch
        {
            "m365" => calRef.StartsWith(GraphService.CalendarRefPrefix, StringComparison.Ordinal) ? RoomModes.Direct : RoomModes.Invite,
            "google" => calRef.EndsWith(GoogleResourceSuffix, StringComparison.OrdinalIgnoreCase) ? RoomModes.Invite
                : googleRole is null or "writer" or "owner" ? RoomModes.Direct : RoomModes.None,
            _ => RoomModes.None
        };
    }

    /// <summary>Les sources qui peuvent réserver en V1 (CalDAV et Exchange sur site : bientôt).</summary>
    public static bool SourceCanWrite(string source) => source is "m365" or "google";

    /// <summary>Texte de l'état pour le back-office : « Libre jusqu'à 16:30 », « Occupée jusqu'à 15:00 », « Bientôt occupée · 14:15 », « Libre toute la journée ».</summary>
    public static string StateText(RoomNow s, TimeZoneInfo tz) => s.State switch
    {
        "busy" => "Occupée jusqu'à " + Hm(s.UntilUtc!.Value, tz),
        "soon" => "Bientôt occupée · " + Hm(s.UntilUtc!.Value, tz),
        _ => s.UntilUtc is { } u ? "Libre jusqu'à " + Hm(u, tz) : "Libre toute la journée"
    };

    /// <summary>Phrase de la section « Maintenant » de l'onglet Réservations (aucun titre) : « Occupée de 13:30 à 15:00 »…</summary>
    public static string NowText(RoomNow s, IEnumerable<RoomInterval> evs, DateTime nowUtc, bool allDay, TimeZoneInfo tz)
    {
        if (s.State == "busy")
        {
            var cur = Blocking(evs, allDay).Where(e => e.StartUtc <= nowUtc && nowUtc < e.EndUtc).OrderBy(e => e.StartUtc).First();
            return $"Occupée de {Hm(cur.StartUtc, tz)} à {Hm(s.UntilUtc!.Value, tz)}";
        }
        if (s.State == "soon") return $"Libre de {Hm(nowUtc, tz)} à {Hm(s.UntilUtc!.Value, tz)} · bientôt occupée";
        return s.UntilUtc is { } u ? $"Libre de {Hm(nowUtc, tz)} à {Hm(u, tz)}" : "Libre toute la journée";
    }
}
