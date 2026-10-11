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
/// Luminosité et contraste selon l'heure, pour les écrans de l'app Android (voir Linkii.Android).
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
        Grid = new string('0', Sleep.Cells)
    };

    /// <summary>Semaine proposée par « Jour, soirée et nuit » : soirée de 17 h à 19 h, nuit de 19 h à 7 h.</summary>
    public static string SuggestedGrid()
    {
        var b = new StringBuilder();
        for (var d = 0; d < 7; d++)
            for (var h = 0; h < 24; h++) b.Append(h >= 17 && h < 19 ? '1' : h < 7 || h >= 19 ? '2' : '0');
        return b.ToString();
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
