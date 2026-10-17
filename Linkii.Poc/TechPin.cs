using System.Security.Cryptography;
using System.Text;

namespace Linkii.Poc;

/// <summary>
/// Code du menu technicien de l'app Android, défini par organisation (Réglages › Contenus et écrans).
/// Tant qu'aucun code n'est défini (<see cref="Tenant.TechPin"/> vide), les appareils gardent le leur (9999 par défaut).
/// </summary>
public static class TechPin
{
    /// <summary>Ce que reçoit le player ({ pin, local }) ; null : rien n'est imposé (l'appareil garde son code). pin vide : menu sans code.</summary>
    public static object? ForPlayer(Tenant t)
    {
        if (!t.TechPinOn) return new { pin = "", local = false };
        return t.TechPin.Length == 0 ? null : new { pin = t.TechPin, local = t.TechPinLocal };
    }

    /// <summary>Empreinte courte du code, que l'app renvoie pour dire qu'elle l'a bien reçu (le code lui-même ne remonte pas).</summary>
    public static string Sig(string? pin) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("linkii-pin|" + pin)))[..8].ToLowerInvariant();

    /// <summary>Empreinte attendue d'un écran de l'organisation ; null : aucun code imposé.</summary>
    public static string? Expected(Tenant t) => !t.TechPinOn ? Sig("") : t.TechPin.Length == 0 ? null : Sig(t.TechPin);

    public static bool Valid(string? pin) => pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit);

    /// <summary>Code à 6 chiffres tiré au hasard.</summary>
    public static string Generate() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>Marque servant à recharger la configuration des écrans quand le code change.</summary>
    public static string Stamp(Tenant t) => Sig(t.TechPinOn + "|" + t.TechPin + "|" + t.TechPinLocal);
}
