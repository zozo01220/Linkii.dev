using System.Security.Cryptography;
using System.Text;

namespace Linkii.Poc;

/// <summary>Profil de luminosité et de contraste (en %) : 100 % = image inchangée.</summary>
public class DimProfile
{
    public string Name { get; set; } = "";
    public int Brightness { get; set; } = 100;
    public int Contrast { get; set; } = 100;
}

/// <summary>Réglage d'une aire ou d'un écran : des profils et la semaine (168 chiffres, lundi 00 h en premier ; chaque chiffre est l'indice d'un profil).</summary>
public class DimPlan
{
    public List<DimProfile> Profiles { get; set; } = new();
    public string Grid { get; set; } = "";

    public DimPlan Clone() => new()
    {
        Profiles = Profiles.Select(p => new DimProfile { Name = p.Name, Brightness = p.Brightness, Contrast = p.Contrast }).ToList(),
        Grid = Grid
    };
}

/// <summary>
/// Luminosité et contraste selon l'heure, pour les écrans de l'app Android (voir Linkii.AndroidPlayer).
/// Réglé sur l'aire (par défaut de ses écrans) et surchargé écran par écran, comme la mise en veille ; les heures sont celles du fuseau de l'aire.
/// La luminosité est réelle sur smartphone et tablette (fenêtre de l'app), simulée par filtre sur Android TV ; le contraste est toujours simulé.
/// </summary>
public static class Dim
{
    public const string Inherit = "inherit";   // l'écran suit son aire
    public const string Never = "never";       // aucun réglage (par défaut)
    public const string Own = "own";           // l'écran a son propre réglage

    public const int MinBrightness = 10, MaxBrightness = 100, MinContrast = 50, MaxContrast = 150;
    public const int MaxProfiles = 6;

    public static string Mode(Screen s) => s.DimMode is Inherit or Own ? s.DimMode : Never;

    public static bool Valid(DimPlan? p) =>
        p is { Profiles.Count: > 0 and <= MaxProfiles, Grid.Length: Sleep.Cells }
        && p.Grid.All(c => c >= '0' && c < '0' + p.Profiles.Count)
        && p.Profiles.All(x => x.Brightness is >= MinBrightness and <= MaxBrightness && x.Contrast is >= MinContrast and <= MaxContrast);

    /// <summary>Le réglage change-t-il quelque chose ? (sinon : aucun profil utilisé n'altère l'image)</summary>
    public static bool Active(DimPlan? p) =>
        Valid(p) && p!.Grid.Distinct().Any(c => p.Profiles[c - '0'] is { Brightness: not 100 } or { Contrast: not 100 });

    public static bool Same(DimPlan? a, DimPlan? b) =>
        a == null || b == null ? a == b
        : a.Grid == b.Grid && a.Profiles.Count == b.Profiles.Count
          && a.Profiles.Zip(b.Profiles).All(x => x.First.Brightness == x.Second.Brightness && x.First.Contrast == x.Second.Contrast);

    /// <summary>Profils proposés à l'activation : jour, soirée, nuit ; la semaine entière est en « Jour ».</summary>
    public static DimPlan Suggested() => new()
    {
        Profiles = new()
        {
            new() { Name = "Jour", Brightness = 100, Contrast = 100 },
            new() { Name = "Soirée", Brightness = 60, Contrast = 110 },
            new() { Name = "Nuit", Brightness = 25, Contrast = 120 }
        },
        Grid = SuggestedGrid()   // dès l'activation, le soir et la nuit sont déjà peints : sinon tout resterait en « Jour » et rien ne changerait
    };

    /// <summary>Semaine proposée par « Jour, soirée et nuit » : soirée de 17 h à 19 h, nuit de 19 h à 7 h.</summary>
    public static string SuggestedGrid()
    {
        var b = new StringBuilder();
        for (var d = 0; d < 7; d++)
            for (var h = 0; h < 24; h++) b.Append(h >= 17 && h < 19 ? '1' : h < 7 || h >= 19 ? '2' : '0');
        return b.ToString();
    }

    /// <summary>Teinte d'une case ou d'une frise : d'autant plus foncée que la luminosité est basse (du blanc à la nuit de la charte).</summary>
    public static string Shade(DimProfile p)
    {
        var a = (100 - p.Brightness) / 100.0;
        return $"rgb({(int)Math.Round(255 - a * 244)},{(int)Math.Round(255 - a * 224)},{(int)Math.Round(255 - a * 197)})";
    }

    /// <summary>
    /// Repères « jour dès, soirée dès, nuit dès » quand le réglage est le même tous les jours et suit le schéma nuit › jour › soirée › nuit
    /// (3 profils : jour, soirée, nuit) ; null sinon (le réglage se lit alors jour par jour).
    /// </summary>
    public static int[]? Thresholds(DimPlan? p)
    {
        if (!Valid(p) || p!.Profiles.Count != 3) return null;
        var day = p.Grid[..24];
        for (var d = 1; d < 7; d++) if (p.Grid.Substring(d * 24, 24) != day) return null;
        int t0 = 0, t1, t2;
        while (t0 < 24 && day[t0] == '2') t0++;
        t1 = t0; while (t1 < 24 && day[t1] == '0') t1++;
        t2 = t1; while (t2 < 24 && day[t2] == '1') t2++;
        if (t1 == t0 || t2 == t1 || t2 > 23) return null;
        for (var h = t2; h < 24; h++) if (day[h] != '2') return null;
        return new[] { t0, t1, t2 };
    }

    /// <summary>Semaine entière à partir des trois repères (jour dès t[0], soirée dès t[1], nuit dès t[2]).</summary>
    public static string GridFrom(int[] t)
    {
        var day = new StringBuilder();
        for (var h = 0; h < 24; h++) day.Append(h >= t[2] || h < t[0] ? '2' : h >= t[1] ? '1' : '0');
        return string.Concat(Enumerable.Repeat(day.ToString(), 7));
    }

    /// <summary>Phrase courte d'un réglage, pour les listes (« Soirée dès 17 h · Nuit dès 19 h »).</summary>
    public static string Summary(DimPlan? p)
    {
        if (!Active(p)) return "Aucun réglage : l'image est affichée telle quelle";
        return Thresholds(p) is { } t
            ? $"{p!.Profiles[1].Name} dès {t[1]} h · {p.Profiles[2].Name} dès {t[2]} h"
            : "Horaires différents selon les jours";
    }

    /// <summary>Frise d'une journée (le lundi) : suites d'heures consécutives de même profil, en nombre d'heures.</summary>
    public static List<(int Hours, DimProfile Profile)> Strip(DimPlan p)
    {
        var res = new List<(int, DimProfile)>();
        for (var h = 0; h < 24; h++)
        {
            var pr = p.Profiles[Math.Min(p.Grid[h] - '0', p.Profiles.Count - 1)];
            if (res.Count > 0 && ReferenceEquals(res[^1].Item2, pr)) res[^1] = (res[^1].Item1 + 1, pr); else res.Add((1, pr));
        }
        return res;
    }

    /// <summary>Réglage qui s'applique à l'écran (null : aucun). Un écran d'un mur suit toujours son aire.</summary>
    public static DimPlan? Effective(Tenant t, Area? area, Screen s, bool inWall)
    {
        if (!t.DimEnabled) return null;
        var plan = inWall ? area?.Dimming : Mode(s) switch { Never => null, Own => s.Dimming, _ => area?.Dimming };
        return Active(plan) ? plan : null;
    }

    /// <summary>Ce que reçoit le player : fuseau, profils et semaine (null : aucun réglage).</summary>
    public static object? ForPlayer(Tenant t, Area? area, Screen s, bool inWall) =>
        Effective(t, area, s, inWall) is { } p
            ? new { tz = Sleep.TimeZone(t, area), profiles = p.Profiles.Select(x => new { b = x.Brightness, c = x.Contrast }), grid = p.Grid }
            : null;

    /// <summary>Empreinte du réglage d'un écran : elle fait partie de sa révision, qui change dès que le réglage change.</summary>
    public static string Stamp(IEnumerable<Area> areas, Screen s)
    {
        var area = areas.FirstOrDefault(a => a.Id == s.AreaId);
        var inWall = Helpers.Wall(areas, s) != null;
        var plan = inWall ? area?.Dimming : Mode(s) switch { Never => null, Own => s.Dimming, _ => area?.Dimming };
        if (!Active(plan)) return "";   // rien de réglé : la révision ne change pas
        var raw = $"{area?.SleepTz}|{plan!.Grid}|{string.Join(";", plan.Profiles.Select(x => $"{x.Brightness},{x.Contrast}"))}";
        return "|dm" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..8];
    }
}
