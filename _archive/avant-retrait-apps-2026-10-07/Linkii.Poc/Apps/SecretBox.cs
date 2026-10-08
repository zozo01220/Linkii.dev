using Microsoft.AspNetCore.DataProtection;

namespace Linkii.Poc;

/// <summary>Chiffrement des paramètres secrets des apps (jamais en clair dans data.json, jamais envoyés au player).</summary>
public class SecretBox(IDataProtectionProvider provider)
{
    private const string Prefix = "dp:";
    private readonly IDataProtector _p = provider.CreateProtector("Linkii.AppSecrets.v1");

    public string Protect(string plain) => Prefix + _p.Protect(plain);

    public string Unprotect(string stored) =>
        stored.StartsWith(Prefix) ? _p.Unprotect(stored[Prefix.Length..]) : throw new InvalidOperationException("Secret non chiffré.");
}
