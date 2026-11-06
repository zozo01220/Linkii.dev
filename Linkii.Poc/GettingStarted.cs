namespace Linkii.Poc;

/// <summary>
/// Assistant « Premiers pas » (DEV-POC.md §190) : enrichir la médiathèque, puis ranger ces contenus dans une liste de diffusion.
/// La connexion d'un écran n'en fait plus partie : elle a son propre assistant (<see cref="Components.FirstScreenWizard"/>), proposé tant
/// que l'organisation n'a aucun écran. L'assistant vit dans la mise en page (MainLayout) : il reste ouvert d'une page à l'autre et
/// s'ouvre depuis le menu Aide, la page Écrans, ou tout seul à la première connexion d'un compte neuf.
/// </summary>
public sealed class GettingStarted
{
    public bool IsOpen { get; private set; }
    public event Action? Changed;

    /// <summary>Panneau à montrer sous la zone de dépôt à l'ouverture (« cloud » : retour d'un assistant d'intégration, annulé ou terminé) ; lu une fois par l'assistant.</summary>
    public string? Panel { get; set; }

    public void Open(string? panel = null) { IsOpen = true; Panel = panel; Changed?.Invoke(); }
    public void Close() { IsOpen = false; Changed?.Invoke(); }

    /// <summary>Images d'exemple (wwwroot/samples/premiers-pas, licences libres : voir CREDITS.md) : fichier et nom dans la médiathèque.</summary>
    public static readonly (string File, string Name)[] Samples =
    [
        ("lac-montagne.jpg", "Lac de montagne"),
        ("montagnes-neige.jpg", "Montagnes enneigées"),
        ("cote-turquoise.jpg", "Côte turquoise"),
        ("lac-automne.jpg", "Lac en automne"),
    ];

    /// <summary>Contenus de présentation (documents convertis en images) : ils déroulent leurs pages, leur durée ne suit pas celle des images.</summary>
    public static readonly string[] PresentationApps = ["ms-powerpoint", "google-slides", "google-docs", "pdf", "canva"];

    /// <summary>Durée réglable d'un seul coup dans l'assistant (« Chaque image reste… ») : tout sauf les vidéos et les présentations.</summary>
    public static bool UsesImageDuration(MediaItem? m) => m?.AppId is { } a && a != "video" && !PresentationApps.Contains(a);

    /// <summary>La liste de l'assistant : celle déjà créée par un passage précédent (elle existe encore), sinon une nouvelle « Ma première liste ».</summary>
    public static Playlist ListFor(ClientDb d, string name)
    {
        if (d.Tenant.FirstScreenListId is { } lid && d.Playlists.FirstOrDefault(p => p.Id == lid) is { } existing) return existing;
        var list = new Playlist { Name = name, UpdatedAt = DateTime.UtcNow };
        d.Playlists.Add(list);
        d.Tenant.FirstScreenListId = list.Id;
        return list;
    }
}
