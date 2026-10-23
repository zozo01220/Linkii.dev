namespace Linkii.Poc;

/// <summary>Moniteur d'un PC Windows, remonté par le player (numéro 1 = principal, puis de gauche à droite).</summary>
public class DeviceMonitor
{
    public int N { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public bool Primary { get; set; }
}

/// <summary>
/// Paramètres de l'appareil (section « Appareil » des paramètres de l'écran) : démarrage automatique, fenêtre, moniteur.
/// L'appareil les reçoit à chaque connexion (bloc « device » du flux du player), sans publication, et les garde pour démarrer hors ligne.
/// Plusieurs écrans sur un même PC : « Ajouter un écran sur ce PC » crée un écran déjà appairé ; le PC l'ouvre (liste « open »).
/// </summary>
public static class DeviceConfig
{
    /// <summary>Le PC peut afficher d'autres écrans : player Windows qui a remonté son identifiant de PC.</summary>
    public static bool IsPc(Screen s) => s.DeviceKind == "windows" && !string.IsNullOrEmpty(s.DeviceHost);

    /// <summary>Autres écrans du même PC (même organisation).</summary>
    public static IEnumerable<Screen> SamePc(IEnumerable<Screen> all, Screen s) =>
        IsPc(s) ? all.Where(x => x.Id != s.Id && x.ClientId == s.ClientId && x.Token != null && x.DeviceHost == s.DeviceHost) : [];

    /// <summary>Écrans ajoutés sur ce PC que le PC n'a pas encore ouverts : il les ouvre en recevant sa configuration.</summary>
    public static IEnumerable<Screen> ToOpen(IEnumerable<Screen> all, Screen s) => SamePc(all, s).Where(x => x.LastSeenUtc == null);

    /// <summary>Nom de l'instance du player qui affichera un écran ajouté sur un PC.</summary>
    public static string InstanceFor(Screen s) => s.DeviceInstance ?? "e-" + s.Id.ToString("N")[..8];

    /// <summary>Taille de la fenêtre de test : la résolution de l'écran, dans son orientation (« auto » : 1280 × 720). Le PC la réduit si le moniteur est plus petit.</summary>
    public static (int W, int H) WindowSize(Screen s)
    {
        var (a, b) = (1280, 720);
        var p = s.Resolution.Split('x');
        if (p.Length == 2 && int.TryParse(p[0], out var x) && int.TryParse(p[1], out var y) && x > 0 && y > 0) (a, b) = (Math.Max(x, y), Math.Min(x, y));
        return s.Orientation == "portrait" ? (b, a) : (a, b);
    }

    /// <summary>Bloc « device » du flux du player.</summary>
    public static object ForPlayer(IEnumerable<Screen> all, Screen s)
    {
        var (w, h) = WindowSize(s);
        return new
        {
            name = s.Name,   // titre de la fenêtre de test
            autoStart = s.AutoStart,
            windowed = s.Windowed,
            monitor = s.Monitor,
            width = w, height = h,
            // écrans ajoutés sur ce PC : le PC les ouvre aussitôt, déjà appairés, à leur place
            open = ToOpen(all, s).Select(x =>
            {
                var (ow, oh) = WindowSize(x);
                return new { instance = InstanceFor(x), token = x.Token, name = x.Name, autoStart = x.AutoStart, windowed = x.Windowed, monitor = x.Monitor, width = ow, height = oh };
            }).ToList()
        };
    }

    /// <summary>Fait partie de la révision de l'écran : un paramètre changé, ou un écran à ouvrir sur le PC, recharge sa configuration.</summary>
    public static string Stamp(IEnumerable<Screen> all, Screen s) =>
        $"|d{(s.AutoStart ? 1 : 0)}{(s.Windowed ? 1 : 0)}{s.Monitor}" + string.Concat(ToOpen(all, s).Select(x => "+" + x.Id.ToString("N")[..8]));

    /// <summary>Moniteur pris par un autre écran du même PC (en plein écran), sinon null.</summary>
    public static Screen? MonitorTakenBy(IEnumerable<Screen> all, Screen s, int monitor) =>
        SamePc(all, s).FirstOrDefault(x => x.Monitor == monitor && !x.Windowed);

    /// <summary>
    /// « Ajouter un écran sur ce PC » : nouvel écran déjà appairé, sur le PC de <paramref name="from"/>. Il hérite de la zone, de la liste de lecture,
    /// du découpage et du format de l'écran d'origine (sauf la résolution, choisie à l'ajout) (hors mur : un écran de mur n'en transmet pas sa place).
    /// </summary>
    public static Screen AddOnSamePc(ClientDb d, Screen from, string name, int monitor, bool windowed, string? resolution = null)
    {
        var inWall = AreaOps.WallOf(d, from) != null;
        var area = d.Root.Areas.FirstOrDefault(a => a.Id == from.AreaId);
        var s = new Screen
        {
            AreaId = from.AreaId,
            ZoneId = inWall && area != null ? AreaOps.EnsureDefaultZone(area).Id : from.ZoneId,
            Name = name,
            Token = Guid.NewGuid().ToString("N"),
            PlaylistId = from.PlaylistId,
            Layout = inWall ? ScreenLayouts.Full : from.Layout,
            ScreenType = from.ScreenType, Orientation = from.Orientation, Resolution = ScreenOptions.Resolutions.Any(r => r.Id == resolution) ? resolution! : from.Resolution,
            AutoStart = from.AutoStart, Windowed = windowed, Monitor = monitor,
            // l'appareil est connu d'avance : c'est le même PC
            DeviceKind = from.DeviceKind, DeviceOs = from.DeviceOs, DeviceModel = from.DeviceModel, DeviceApp = from.DeviceApp,
            DeviceHost = from.DeviceHost, DeviceMonitors = from.DeviceMonitors.Select(m => new DeviceMonitor { N = m.N, W = m.W, H = m.H, Primary = m.Primary }).ToList()
        };
        s.DeviceInstance = InstanceFor(s);
        s.Layout = ScreenLayouts.Fit(s.Layout, new ScreenSpec { Type = s.ScreenType, Orientation = s.Orientation, Resolution = s.Resolution });   // découpage encore permis par la résolution choisie
        if (!inWall) for (var z = 1; z < 3; z++) s.SetZonePlaylist(z, from.ZonePlaylist(z));
        d.Screens.Add(s);
        return s;
    }
}
