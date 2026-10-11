using System.Security.Cryptography;
using System.Text;

namespace Linkii.Poc;

/// <summary>
/// Plages de mise en veille des écrans. Une plage est une grille de 7 jours × 24 heures (168 caractères « 0 » / « 1 », lundi 00 h en premier ; « 1 » = écran en veille).
/// Réglée sur l'aire (par défaut de ses écrans, avec son fuseau horaire) et surchargée écran par écran ; la zone n'intervient pas.
/// Les heures sont celles du fuseau de l'aire (à défaut, celui de l'organisation).
/// </summary>
public static class Sleep
{
    public const string Inherit = "inherit";   // l'écran suit son aire
    public const string Never = "never";       // l'écran ne se met jamais en veille
    public const string Own = "own";           // l'écran a sa propre grille

    /// <summary>Durée d'un réveil manuel (menu ⋮ « Réveiller ») : l'écran reste éveillé ce temps-là, puis reprend ses plages.</summary>
    public const int WakeMinutes = 60;

    public static bool Woken(Screen s, DateTime nowUtc) => s.WakeUntilUtc is { } w && w > nowUtc;

    public const int Cells = 7 * 24;
    public const int WeekMinutes = 7 * 24 * 60;

    public static bool Valid(string? grid) => grid is { Length: Cells } && grid.All(c => c is '0' or '1');
    public static bool Any(string? grid) => Valid(grid) && grid!.Contains('1');

    /// <summary>Grille proposée à l'activation : nuits de 19 h à 7 h en semaine, week-end entier.</summary>
    public static string Suggested()
    {
        var b = new StringBuilder();
        for (var d = 0; d < 7; d++)
            for (var h = 0; h < 24; h++) b.Append(d >= 5 || h < 7 || h >= 19 ? '1' : '0');
        return b.ToString();
    }

    public static string Mode(Screen s) => s.SleepMode is Never or Own ? s.SleepMode : Inherit;

    /// <summary>Grille qui s'applique à l'écran (null : jamais en veille). Un écran d'un mur suit toujours son aire.</summary>
    public static string? EffectiveGrid(Tenant t, Area? area, Screen s, bool inWall)
    {
        if (!t.SleepEnabled) return null;
        var grid = inWall ? area?.SleepGrid : Mode(s) switch { Never => null, Own => s.SleepGrid, _ => area?.SleepGrid };
        return Any(grid) ? grid : null;
    }

    /// <summary>Fuseau des plages : celui de l'aire, à défaut celui de l'organisation.</summary>
    public static string TimeZone(Tenant t, Area? area) => !string.IsNullOrEmpty(area?.SleepTz) ? area!.SleepTz : t.Timezone;

    /// <summary>Plages en minutes depuis le lundi 00 h (début, fin) ; une plage qui franchit la fin de semaine a une fin au-delà de 10 080.</summary>
    public static List<int[]> Ranges(string grid)
    {
        var res = new List<int[]>();
        if (grid.All(c => c == '1')) { res.Add(new[] { 0, WeekMinutes }); return res; }
        // début : première heure en veille précédée d'une heure éveillée (la grille est circulaire)
        for (var i = 0; i < Cells; i++)
        {
            if (grid[i] != '1' || grid[(i + Cells - 1) % Cells] == '1') continue;
            var n = 0;
            while (grid[(i + n) % Cells] == '1') n++;
            res.Add(new[] { i * 60, (i + n) * 60 });
        }
        return res;
    }

    /// <summary>Ce que reçoit le player : fuseau et plages (null : pas de veille).</summary>
    public static object? ForPlayer(Tenant t, Area? area, Screen s, bool inWall) =>
        t.SleepEnabled && s.ForcedSleep
            ? new { tz = TimeZone(t, area), ranges = new List<int[]> { new[] { 0, WeekMinutes } }, wakeUntil = 0L }   // mise en veille manuelle : toute la semaine, jusqu'au réveil
        : EffectiveGrid(t, area, s, inWall) is { } g
            ? new { tz = TimeZone(t, area), ranges = Ranges(g), wakeUntil = Woken(s, DateTime.UtcNow) ? Helpers.EpochMs(s.WakeUntilUtc!.Value) : 0L }   // wakeUntil : réveil manuel en cours (0 : aucun)
            : null;

    /// <summary>Empreinte des réglages de veille d'un écran : elle fait partie de sa révision, qui change dès qu'une plage change.</summary>
    public static string Stamp(IEnumerable<Area> areas, Screen s)
    {
        var area = areas.FirstOrDefault(a => a.Id == s.AreaId);
        if (string.IsNullOrEmpty(area?.SleepGrid) && string.IsNullOrEmpty(area?.SleepTz) && Mode(s) == Inherit && s.WakeUntilUtc == null && !s.ForcedSleep) return "";   // rien de réglé : la révision ne change pas
        var raw = $"{area?.SleepTz}|{area?.SleepGrid}|{Mode(s)}|{(Mode(s) == Own ? s.SleepGrid : "")}|{s.WakeUntilUtc?.Ticks}|{s.ForcedSleep}";
        return "|sl" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..8];
    }

    /// <summary>L'écran est-il en veille à cet instant ? (carte de l'écran dans le back-office)</summary>
    public static (bool Asleep, string? Wake) State(Tenant t, Area? area, Screen s, bool inWall, DateTime nowUtc)
    {
        if (t.SleepEnabled && s.ForcedSleep) return (true, null);   // mise en veille manuelle : jusqu'au réveil
        if (EffectiveGrid(t, area, s, inWall) is not { } grid || Woken(s, nowUtc)) return (false, null);   // réveil manuel : éveillé malgré la plage
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(TimeZone(t, area)); } catch { tz = TimeZoneInfo.Utc; }
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        int Cell(DateTime l) => (((int)l.DayOfWeek + 6) % 7) * 24 + l.Hour;
        if (grid[Cell(local)] != '1') return (false, null);
        // réveil : première heure éveillée à venir (null si la grille est entièrement en veille)
        var h = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0);
        for (var i = 1; i <= Cells; i++)
        {
            var next = h.AddHours(i);
            if (grid[Cell(next)] != '1')   // « 07:00 », ou « lun. 07:00 » si ce n'est pas aujourd'hui (heure locale de l'aire)
                return (true, (next.Date == local.Date ? "" : next.ToString("ddd", System.Globalization.CultureInfo.GetCultureInfo("fr-CH")) + " ") + next.ToString("HH:mm"));
        }
        return (true, null);
    }
}
