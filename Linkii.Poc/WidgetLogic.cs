using System.Text;
using System.Text.RegularExpressions;
using QRCoder;

namespace Linkii.Poc;

/// <summary>QR code d'un widget : calculé sur le serveur, l'écran n'a plus qu'à dessiner le tracé (aucune bibliothèque, aucun réseau).</summary>
public static class QrCodes
{
    /// <summary>Côté (en modules, marge comprise) et tracé SVG des modules sombres ; null si le texte est vide ou trop long.</summary>
    public static (int Size, string Path)? Build(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(text.Trim(), QRCodeGenerator.ECCLevel.M);
            var m = data.ModuleMatrix;   // marge blanche de 4 modules comprise
            var sb = new StringBuilder();
            for (var y = 0; y < m.Count; y++)
            {
                var x = 0;
                while (x < m.Count)
                {
                    if (!m[y][x]) { x++; continue; }
                    var start = x;
                    while (x < m.Count && m[y][x]) x++;
                    sb.Append($"M{start} {y}h{x - start}v1h-{x - start}z");
                }
            }
            return (m.Count, sb.ToString());
        }
        catch (Exception) { return null; }   // texte trop long pour un QR code
    }

    /// <summary>Valeur du paramètre « qr » publié vers l'écran : « côté|tracé ».</summary>
    public static string? Encode(string? text) => Build(text) is { } q ? q.Size + "|" + q.Path : null;
}

/// <summary>Compte à rebours : même règle que le rendu de l'écran (apps.js « countdown »).</summary>
public static class Countdown
{
    public record Result(string Big, string Unit, bool Hidden);

    /// <param name="detailed">false : nombre de jours ; true : jours, heures et minutes (puis heures, minutes, secondes le dernier jour).</param>
    /// <param name="hideAfter">masquer le widget le lendemain de la date (sinon le message reste)</param>
    public static Result Compute(DateTime now, string? date, bool detailed, bool hideAfter, string? doneText)
    {
        var message = new Result(string.IsNullOrWhiteSpace(doneText) ? "C'est le grand jour !" : doneText.Trim(), "", false);
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var target)) return new Result("—", "", false);
        var days = (target.Date - now.Date).Days;
        if (detailed && now < target.Date)
        {
            var left = target.Date - now;
            return left.TotalDays >= 1
                ? new Result($"{(int)left.TotalDays} j {left.Hours:00} h {left.Minutes:00} min", "", false)
                : new Result($"{left.Hours:00}:{left.Minutes:00}:{left.Seconds:00}", "", false);
        }
        if (!detailed && days > 0) return new Result(days.ToString(), days == 1 ? "jour" : "jours", false);
        return days < 0 && hideAfter ? message with { Hidden = true } : message;
    }
}

/// <summary>Citation du jour : une citation par ligne, « Texte — Auteur » ; même choix que le rendu de l'écran (apps.js « quote »).</summary>
public static class Quotes
{
    private static readonly Regex Sep = new(@"^(.*\S)\s+[—–-]\s+(\S.*)$");

    public static List<(string Text, string? Author)> Parse(string? raw) =>
        (raw ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
            .Select(l => Sep.Match(l) is { Success: true } m ? (m.Groups[1].Value, (string?)m.Groups[2].Value) : (l, null))
            .ToList();

    /// <summary>« daily » : une citation par jour (change à minuit) ; « rotate » : change toutes les <paramref name="intervalSec"/> secondes.</summary>
    public static int Index(DateTime now, string? mode, int intervalSec, int count)
    {
        if (count <= 0) return 0;
        if (mode == "rotate")
        {
            var secs = (long)(now.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;
            return (int)(secs / Math.Max(5, intervalSec) % count);
        }
        return (now.Date - new DateTime(1970, 1, 1)).Days % count;
    }
}
