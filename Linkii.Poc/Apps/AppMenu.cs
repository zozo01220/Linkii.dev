namespace Linkii.Poc;

/// <summary>
/// Groupe « Applications » du menu de gauche (maquettes/menu-applications-refonte.html) : les applications choisies dans « Mes applications »,
/// dans l'ordre choisi. Les identifiants sont ceux de la page Applications : une app du catalogue, « cal-… » (calendrier partagé),
/// « drv-… » (Drive) ou « ha-home » (Home Assistant). Sans choix enregistré : les applications prises dans le Store, et Home Assistant s'il est connecté
/// (les applications de base installées d'office restent hors du menu).
/// </summary>
public static class AppMenu
{
    public const string Home = "ha-home";
    /// <summary>« Vue du bâtiment » (application du catalogue) : sa page montre les plans du bâtiment ; placée d'office juste après Home Assistant (mode expert).</summary>
    public const string Plans = HomeAssistantRules.BuildingApp;

    /// <summary>Identifiants du menu, dans l'ordre (y compris ceux qui ne sont plus installés : voir <see cref="Shown"/>).</summary>
    public static List<string> Ids(Tenant t)
    {
        var ids = t.MenuApps?.ToList() ?? t.IntegrationsAdded.ToList();
        if (t.MenuApps == null && t.HomeAssistant is { Url.Length: > 0 } && !ids.Contains(Home)) ids.Add(Home);
        if (!t.PlansMenuOff && !ids.Contains(Plans) && ids.IndexOf(Home) is var h && h >= 0) ids.Insert(h + 1, Plans);
        return ids;
    }

    public static bool Has(Tenant t, string id) => Ids(t).Contains(id);

    /// <summary>Ajoute l'application à la fin du groupe, ou l'en retire.</summary>
    public static void Set(Tenant t, string id, bool on)
    {
        var ids = Ids(t).ToList();
        ids.Remove(id);
        if (on) ids.Add(id);
        if (id == Plans) t.PlansMenuOff = !on;
        t.MenuApps = ids;
    }

    /// <summary>Monte (-1) ou descend (+1) l'application dans le groupe.</summary>
    public static void Move(Tenant t, string id, int delta)
    {
        var ids = Ids(t).ToList();
        var i = ids.IndexOf(id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= ids.Count) return;
        (ids[i], ids[j]) = (ids[j], ids[i]);
        t.MenuApps = ids;
    }

    /// <summary>L'application est encore dans « Mes applications » (installée, ou source active ou prise dans le Store).</summary>
    public static bool Installed(ClientDb d, string id) =>
        id == Home ? d.Tenant.HomeAssistant is { Url.Length: > 0 }
        : id == Plans ? d.Tenant.HomeAssistant is { Url.Length: > 0 } && d.AppInstalls.Any(a => a.AppId == id)
        : id.StartsWith("cal-") ? SharedCalendarSources.Enabled(d.Tenant, id[4..]) || d.Tenant.IntegrationsAdded.Contains(id)
        : id.StartsWith("drv-") ? DriveSources.Enabled(d.Tenant, id[4..]) || d.Tenant.IntegrationsAdded.Contains(id)
        : d.AppInstalls.Any(a => a.AppId == id);

    /// <summary>Entrées affichées dans le menu : celles encore installées, dans l'ordre.</summary>
    public static List<string> Shown(ClientDb d) => Ids(d.Tenant).Where(id => Installed(d, id)).ToList();

    /// <summary>Page ouverte par l'entrée du menu : la page de l'application (/apps/{id}), où se trouve sa configuration.</summary>
    public static string Href(string id) => id switch
    {
        _ => "apps/" + id
    };

    /// <summary>Nom affiché dans le menu.</summary>
    public static string Label(string id, AppCatalog catalog) =>
        id == Home ? "Home Assistant"
        : id.StartsWith("cal-") ? SharedCalendarSources.Find(id[4..])?.Name ?? id
        : id.StartsWith("drv-") ? DriveSources.Find(id[4..])?.Name ?? id
        : catalog.Find(id)?.Name ?? id;

    /// <summary>Icône (identifiant passé à AppIcon).</summary>
    public static string Icon(string id) => id == Home ? "homeassistant" : id;

    /// <summary>Entrée réservée au mode expert.</summary>
    public static bool ExpertOnly(string id) => id == Plans;
}
