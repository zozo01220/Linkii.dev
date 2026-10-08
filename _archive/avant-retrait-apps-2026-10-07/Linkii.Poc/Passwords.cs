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
}
