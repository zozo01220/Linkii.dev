using System.Text.RegularExpressions;

namespace Linkii.Player;

/// <summary>
/// Arguments internes du player (aucun n'est à taper par l'utilisateur : tout se règle depuis le back-office).
/// --instance : écran ajouté sur ce PC (posé par le player lui-même dans le démarrage automatique) ; --child : processus d'affichage du superviseur ;
/// --uninstall : appelé par le désinstalleur ; --devtools : outils de développement (débogage).
/// </summary>
internal sealed class Options
{
    public string Instance = "default";
    public bool Child, DevTools, Uninstall;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--instance" when i + 1 < args.Length && Regex.IsMatch(args[i + 1], "^[A-Za-z0-9_-]{1,40}$"): o.Instance = args[++i]; break;
                case "--child": o.Child = true; break;
                case "--devtools": o.DevTools = true; break;
                case "--uninstall": o.Uninstall = true; break;
                // le reste est ignoré : une ancienne entrée de démarrage automatique ne doit pas empêcher le player de démarrer
            }
        }
        return o;
    }

    /// <summary>Arguments du processus d'affichage et du démarrage automatique.</summary>
    public IReadOnlyList<string> Persistent =>
        (Instance == "default" ? Array.Empty<string>() : new[] { "--instance", Instance }).Concat(DevTools ? new[] { "--devtools" } : Array.Empty<string>()).ToList();
}
