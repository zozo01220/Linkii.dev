using Microsoft.AspNetCore.Identity;

namespace Linkii.Poc;

/// <summary>Hachage PBKDF2 via le PasswordHasher d'ASP.NET Core (aucun mot de passe en clair dans data.json).</summary>
public static class Passwords
{
    private static readonly PasswordHasher<object> Hasher = new();

    public static string Hash(string password) => Hasher.HashPassword(new object(), password);

    public static bool Verify(string hash, string password) =>
        !string.IsNullOrEmpty(hash) &&
        Hasher.VerifyHashedPassword(new object(), hash, password) != PasswordVerificationResult.Failed;

    // Sans caractères ambigus (0/O, 1/l/I) : le mot de passe provisoire est recopié depuis un e-mail.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    /// <summary>Mot de passe provisoire aléatoire, par groupes de 4 (ex. « Hq7k-Wm3p-Zx9a »).</summary>
    public static string Generate()
    {
        var chars = Enumerable.Range(0, 12).Select(_ => Alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray();
        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}-{new string(chars, 8, 4)}";
    }
}
