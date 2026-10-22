using System.Globalization;
using System.Text.RegularExpressions;

namespace Linkii.Player;

/// <summary>Ligne de commande : une instance du player = un profil, un appairage, une fenêtre.</summary>
internal sealed class Options
{
    public string Instance = "default";
    public string? Server;
    public (int X, int Y, int W, int H)? Window;   // fenêtre de test, en pixels de l'écran virtuel
    public bool Windowed;                          // fenêtre de test sans position imposée
    public int Monitor = 1;                        // 1 = moniteur principal, puis de gauche à droite (--list-monitors)
    public bool Reset;                             // oublie l'appairage de cette instance
    public bool DevTools;
    public bool Child;                             // processus d'affichage lancé par le superviseur
    public bool NoSupervisor;                      // pas de superviseur (débogage)
    public bool ListMonitors, Help;
    public string? AutoStart;                      // on | off

    private readonly List<string> raw = new();

    public bool Kiosk => Window == null && !Windowed;

    public static Options Parse(string[] args, out string? error)
    {
        var o = new Options();
        error = null;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} demande une valeur.");
            try
            {
                switch (a.ToLowerInvariant())
                {
                    case "--instance":
                        var name = Next();
                        if (!Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,40}$")) throw new ArgumentException("Nom d'instance : lettres, chiffres, - et _ (40 caractères au plus).");
                        o.Instance = name; o.raw.Add(a); o.raw.Add(name); break;
                    case "--server": o.Server = Next(); o.raw.Add(a); o.raw.Add(o.Server); break;
                    case "--window":
                        var v = Next();
                        var p = v.Split(',');
                        if (p.Length != 4 || p.Any(x => !int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))) throw new ArgumentException("--window demande X,Y,largeur,hauteur (pixels).");
                        var n = p.Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                        if (n[2] < 200 || n[3] < 150) throw new ArgumentException("Fenêtre trop petite (200 × 150 au minimum).");
                        o.Window = (n[0], n[1], n[2], n[3]); o.raw.Add(a); o.raw.Add(v); break;
                    case "--windowed": o.Windowed = true; o.raw.Add(a); break;
                    case "--monitor":
                        var m = Next();
                        if (!int.TryParse(m, out var mi) || mi < 1) throw new ArgumentException("--monitor demande un numéro à partir de 1 (--list-monitors).");
                        o.Monitor = mi; o.raw.Add(a); o.raw.Add(m); break;
                    case "--kiosk": o.raw.Add(a); break;   // plein écran : c'est le mode par défaut
                    case "--devtools": o.DevTools = true; o.raw.Add(a); break;
                    case "--no-supervisor": o.NoSupervisor = true; o.raw.Add(a); break;
                    case "--child": o.Child = true; break;
                    case "--reset": o.Reset = true; break;
                    case "--list-monitors": o.ListMonitors = true; break;
                    case "--help": case "-h": case "/?": o.Help = true; break;
                    case "--autostart":
                        var s = Next().ToLowerInvariant();
                        if (s is not ("on" or "off")) throw new ArgumentException("--autostart on ou off.");
                        o.AutoStart = s; break;
                    default: throw new ArgumentException($"Option inconnue : {a}");
                }
            }
            catch (ArgumentException ex) { error = ex.Message; return o; }
        }
        return o;
    }

    /// <summary>Arguments du processus d'affichage et du démarrage automatique : tout sauf les ordres ponctuels (--reset, --autostart…).</summary>
    public IReadOnlyList<string> Persistent => raw;

    public const string Usage =
        "Linkii Player\n\n" +
        "  --instance NOM      instance (profil et appairage séparés) ; défaut : default\n" +
        "  --server URL        adresse du serveur Linkii (gardée pour la suite)\n" +
        "  --monitor N         moniteur en plein écran (1 = principal) ; voir --list-monitors\n" +
        "  --window X,Y,L,H    fenêtre de test, en pixels, au lieu du plein écran\n" +
        "  --windowed          fenêtre de test sans position imposée\n" +
        "  --autostart on|off  démarrage avec la session Windows (cette instance) ; « on » enregistre\n" +
        "                      puis lance, « off » retire l'entrée et s'arrête\n" +
        "  --reset             oublie l'appairage de cette instance\n" +
        "  --list-monitors     liste les moniteurs\n" +
        "  --devtools          outils de développement du navigateur\n" +
        "  --no-supervisor     sans relance automatique (débogage)\n\n" +
        "Exemples\n" +
        "  LinkiiPlayer.exe --instance salle-1 --window 0,0,960,540\n" +
        "  LinkiiPlayer.exe --instance mur-1 --monitor 2";
}
