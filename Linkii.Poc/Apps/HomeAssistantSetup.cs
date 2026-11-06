namespace Linkii.Poc;

/// <summary>
/// Ce que l'installation de Home Assistant rend visible tout de suite : les sept widgets (activés, donc proposés dans l'édition des écrans et des bornes)
/// et la source de données « Home Assistant » avec un jeu « états » (lu toutes les minutes), pour les champs, listes, graphiques et indicateurs des compositions.
/// Appliqué à la connexion du compte, et au démarrage pour les comptes déjà connectés.
/// </summary>
public static class HomeAssistantSetup
{
    public const string ConnectionName = "Home Assistant";
    /// <summary>Version de la mise en place : un compte déjà mis en place n'est pas repris au démarrage (une source supprimée exprès ne revient pas).</summary>
    public const int Version = 3;
    public const string StatesSetName = "Home Assistant — états";

    /// <summary>Widgets activés et source de données créée pour une organisation (base entière : migration au démarrage).</summary>
    public static bool Apply(Db db, Tenant t, AppCatalog catalog)
    {
        var changed = false;
        foreach (var id in HomeAssistantRules.AllApps)
        {
            if (catalog.Find(id) is not { } m || db.AppInstalls.Any(a => a.ClientId == t.Id && a.AppId == id)) continue;
            db.AppInstalls.Add(new AppInstall { ClientId = t.Id, AppId = id, Version = m.Version });
            changed = true;
        }
        return EnsureDataConnection(t) | changed;
    }

    /// <summary>Source de données « Home Assistant » (une seule) et son jeu « états » de toutes les entités, si la place le permet.</summary>
    public static bool EnsureDataConnection(Tenant t)
    {
        if (t.DataConnections.Any(c => c.Kind == DataKinds.Ha) || t.DataConnections.Count >= DataKinds.MaxConnections) return false;
        var c = new DataConnection { Name = ConnectionName, Kind = DataKinds.Ha };
        t.DataConnections.Add(c);
        if (t.DataSets.Count < DataKinds.MaxDataSets && !t.DataSets.Any(d => d.Name.Equals(StatesSetName, StringComparison.CurrentCultureIgnoreCase)))
            t.DataSets.Add(new DataSet { ConnectionId = c.Id, Name = StatesSetName, Path = HomeAssistantRules.DataPathOf("states", Array.Empty<string>()), RefreshMin = 1 });
        return true;
    }

    /// <summary>Connexion depuis le back-office : mêmes effets pour l'organisation de la page.</summary>
    public static void Install(AppService svc, TenantStore store)
    {
        foreach (var id in HomeAssistantRules.AllApps) svc.Enable(id);
        store.Write(d =>
        {
            EnsureDataConnection(d.Tenant);
            if (d.Tenant.HomeAssistant is { } a) a.SetupVersion = Version;
        });
    }

    /// <summary>
    /// Ajout annulé : widgets désactivés s'ils ne sont posés sur aucun écran, source de données retirée avec ses jeux s'ils ne servent à aucune composition.
    /// </summary>
    public static void Uninstall(AppService svc, TenantStore store)
    {
        var placed = store.Read(d => d.Screens.SelectMany(s => s.Widgets).Select(w => w.AppId).Where(HomeAssistantRules.IsHaApp).ToHashSet());
        foreach (var id in HomeAssistantRules.AllApps.Where(id => !placed.Contains(id))) svc.Disable(id);
        store.Write(d =>
        {
            foreach (var c in d.Tenant.DataConnections.Where(c => c.Kind == DataKinds.Ha).ToList())
            {
                var sets = d.Tenant.DataSets.Where(x => x.ConnectionId == c.Id).ToList();
                if (sets.Any(x => DataSourceOps.UsedBy(d, x.Id).Count > 0)) continue;
                d.Tenant.DataSets.RemoveAll(x => x.ConnectionId == c.Id);
                d.Tenant.DataConnections.Remove(c);
            }
        });
    }

    /// <summary>Migration au démarrage : organisations dont Home Assistant est déjà connecté.</summary>
    public static void Migrate(JsonStore store, AppCatalog catalog) =>
        store.Write(db =>
        {
            foreach (var t in db.Clients.Where(t => t.HomeAssistant is { Url.Length: > 0 } a && a.SetupVersion < Version))
            {
                Apply(db, t, catalog);
                t.HomeAssistant!.SetupVersion = Version;
            }
        });
}
