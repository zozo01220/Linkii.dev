using System.Reflection;

namespace Linkii.Poc;

/// <summary>Version de l'application, générée à la compilation à partir de git (cf. cible GitVersion du .csproj) : « 1.16.0 ».</summary>
public static class AppVersion
{
    private static readonly string? Info = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>Numéro court affiché : « 1.16.0 ».</summary>
    public static string Short { get; } = (Info ?? "1.0.0").Split('+')[0];

    /// <summary>Numéro avec l'empreinte du commit (info-bulle) : « 1.16.0+1a40609 ».</summary>
    public static string Full { get; } = Info ?? Short;
}
