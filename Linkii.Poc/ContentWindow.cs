using System.Globalization;

namespace Linkii.Poc;

/// <summary>
/// Période de diffusion d'un contenu de liste de lecture (onglet « Programmation »). Début et fin facultatifs, au format
/// <c>aaaa-mm-ddTHH:mm</c> dans le fuseau de l'organisation ; rien de renseigné = diffusion sans limite.
/// Le player compare la même chaîne à l'heure locale de l'écran (player.js, <c>isValidNow</c>) : le format est à largeur
/// fixe, donc la comparaison de texte suffit.
/// </summary>
public static class ContentWindow
{
    public const string Format = "yyyy-MM-dd'T'HH:mm";
    private static readonly CultureInfo Fr = new("fr-FR");

    /// <summary>Vrai si le contenu est diffusé à cet instant (chaîne aaaa-mm-ddTHH:mm dans le fuseau de l'organisation).</summary>
    public static bool Plays(string? from, string? to, string nowIso) =>
        (from == null || string.CompareOrdinal(nowIso, from) >= 0) && (to == null || string.CompareOrdinal(nowIso, to) <= 0);

    /// <summary>Heure locale de l'organisation, au format de la période.</summary>
    public static string NowIso(string timezone)
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(timezone); } catch { tz = TimeZoneInfo.Utc; }
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).ToString(Format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Pastille sous le nom du contenu : « Jusqu'au… » (en cours), « Démarre le… » (avant), « Terminé le… » (après).
    /// Null sans période. Css : now | soon | done.
    /// </summary>
    public static (string Css, string Text)? Chip(string? from, string? to, string nowIso)
    {
        if (from == null && to == null) return null;
        if (to != null && string.CompareOrdinal(nowIso, to) > 0) return ("done", $"Terminé le {Day(to)}");
        if (from != null && string.CompareOrdinal(nowIso, from) < 0) return ("soon", $"Démarre le {Hour(from)}");
        return to != null ? ("now", $"Jusqu'au {Hour(to)}") : ("now", $"Depuis le {Hour(from!)}");
    }

    /// <summary>« 24 déc. à 18:00 ».</summary>
    public static string Hour(string iso) =>
        Parse(iso) is { } d ? d.ToString("d MMM", Fr) + " à " + d.ToString("H:mm", CultureInfo.InvariantCulture) : iso;

    /// <summary>« 24 déc. ».</summary>
    public static string Day(string iso) => Parse(iso) is { } d ? d.ToString("d MMM", Fr) : iso;

    /// <summary>« 24 déc. 2026 à 18:00 ».</summary>
    public static string Long(string iso) =>
        Parse(iso) is { } d ? d.ToString("d MMM yyyy", Fr) + " à " + d.ToString("H:mm", CultureInfo.InvariantCulture) : iso;

    private static DateTime? Parse(string iso) =>
        DateTime.TryParseExact(iso, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Vrai si la période est cohérente : chaque borne au format attendu, fin après début.</summary>
    public static bool Valid(string? from, string? to, out string? error)
    {
        error = null;
        if (from != null && Parse(from) == null) { error = "Le début n'est pas une date et une heure valides."; return false; }
        if (to != null && Parse(to) == null) { error = "La fin n'est pas une date et une heure valides."; return false; }
        if (from != null && to != null && string.CompareOrdinal(to, from) <= 0) { error = "La fin doit être après le début."; return false; }
        return true;
    }
}
