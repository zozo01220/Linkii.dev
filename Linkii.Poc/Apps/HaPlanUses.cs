namespace Linkii.Poc;

/// <summary>
/// Où s'affiche un plan du bâtiment (§202), et « Ajouter à… » : contenus « Vue du bâtiment » des listes de lecture et des listes interactives (tuiles de borne),
/// widgets « Plan du bâtiment » des écrans et des bornes. Tout part de la page du plan : on n'a plus à rechoisir le plan ailleurs.
/// </summary>
public static class HaPlanUses
{
    /// <summary>
    /// Une utilisation. Kind : « list » (contenu, dans des listes de lecture), « tile » (contenu ouvert par une tuile de borne),
    /// « screen » (widget d'un écran), « kiosk » (widget d'une borne, quand elle diffuse). ViewId null : widget en « Auto ».
    /// </summary>
    public sealed record Use(string Kind, string Name, Guid? ViewId, string Where, Guid? ScreenId = null, Guid? MediaId = null, Guid? PlaylistId = null, Guid? ListId = null);

    private static bool Of(IReadOnlyDictionary<string, string> s, Guid planId) => s.GetValueOrDefault("planId") == planId.ToString();
    private static Guid? ViewOf(IReadOnlyDictionary<string, string> s) => Guid.TryParse(s.GetValueOrDefault("viewId"), out var v) ? v : null;

    /// <summary>Toutes les utilisations d'un plan dans l'organisation (écrans de toutes les aires).</summary>
    public static List<Use> Of(ClientDb d, Guid planId)
    {
        var res = new List<Use>();
        var contents = d.Media.Where(m => m.AppId == HomeAssistantRules.BuildingApp && Of(m.Settings, planId)).ToList();
        foreach (var m in contents)
        {
            var lists = d.Playlists.Where(p => p.Draft.Any(i => i.MediaId == m.Id)).ToList();
            var tiles = d.InteractiveLists.Where(l => l.Pages.Any(p => p.Els.Any(e => e.Action.Type == "media" && e.Action.MediaId == m.Id))).ToList();
            foreach (var l in tiles) res.Add(new Use("tile", m.Name, ViewOf(m.Settings), $"Liste interactive « {l.Name} »", MediaId: m.Id, ListId: l.Id));
            if (lists.Count > 0 || tiles.Count == 0)
                res.Add(new Use("list", m.Name, ViewOf(m.Settings), lists.Count == 0 ? "Dans aucune liste" : string.Join(", ", lists.Select(p => $"« {p.Name} »")), MediaId: m.Id, PlaylistId: lists.FirstOrDefault()?.Id));
        }
        foreach (var s in d.AllScreens)
            foreach (var w in s.Widgets.Where(w => w.AppId == HomeAssistantRules.PlanApp && Of(w.Settings, planId)))
                res.Add(new Use(s.Kiosk != null ? "kiosk" : "screen", s.Name, ViewOf(w.Settings), s.Kiosk != null ? "Borne, quand elle diffuse" : "Écran", ScreenId: s.Id));
        return res;
    }

    /// <summary>Nombre d'utilisations par affichage (pour refuser de supprimer un affichage utilisé).</summary>
    public static Dictionary<Guid, int> ByView(ClientDb d, Guid planId) =>
        Of(d, planId).Where(u => u.ViewId != null).GroupBy(u => u.ViewId!.Value).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Nombre d'utilisations de chaque plan de l'organisation (cartes de la page Plans).</summary>
    public static Dictionary<Guid, int> CountByPlan(ClientDb d) => d.Tenant.HaPlans.ToDictionary(p => p.Id, p => Of(d, p.Id).Count);

    /// <summary>Contenu « Vue du bâtiment » déjà relié (plan + affichage), nommé « Plan · Affichage ». Null si l'application n'est pas utilisable.</summary>
    public static MediaItem? NewContent(ClientDb d, AppCatalog catalog, HaPlan plan, HaPlanView view)
    {
        if (catalog.Find(HomeAssistantRules.BuildingApp) is not { } m || !d.AppInstalls.Any(a => a.AppId == m.Id) || !d.Access.CanUse(m)) return null;
        var settings = AppCatalog.WithDefaults(m, null);
        settings["planId"] = plan.Id.ToString(); settings["viewId"] = view.Id.ToString();
        var item = new MediaItem { Type = "app", AppId = m.Id, Name = $"{plan.Name} · {view.Name}", AppVersion = m.Version, Settings = settings };
        d.Media.Add(item);
        return item;
    }

    /// <summary>Ajoute un contenu à la fin d'une liste de lecture ; la durée est celle de la visite (diffusion), sinon la durée par défaut.</summary>
    public static string? AddToList(ClientDb d, Guid playlistId, MediaItem content, HaPlanView view)
    {
        if (d.Playlists.FirstOrDefault(p => p.Id == playlistId) is not { } pl) return "Liste introuvable.";
        pl.Draft.Add(new PlaylistItem { MediaId = content.Id, DurationSec = HaViews.TourSeconds(view) ?? d.Tenant.DefaultDurationSec, Placement = "full" });
        pl.Touch();
        return null;
    }

    /// <summary>Pose le widget « Plan du bâtiment » (plan + affichage, ou « Auto » si viewId est null) sur un écran ou une borne, en haut à gauche : à placer ensuite.</summary>
    public static string? AddWidget(ClientDb d, AppCatalog catalog, Guid screenId, Guid planId, Guid? viewId)
    {
        if (d.Screens.FirstOrDefault(s => s.Id == screenId) is not { } s) return "Écran introuvable.";
        if (catalog.Find(HomeAssistantRules.PlanApp) is not { } m || !d.AppInstalls.Any(a => a.AppId == m.Id)) return "Le widget « Plan du bâtiment » n'est pas activé (Applications › Home Assistant).";
        var settings = AppCatalog.WithDefaults(m, null);
        settings["planId"] = planId.ToString(); settings["viewId"] = viewId?.ToString() ?? "";
        s.Widgets.Add(new ScreenWidget { AppId = m.Id, Settings = settings, X = 4, Y = 4, Scale = 1 });
        s.WidgetsRevision++;   // modification à publier
        return null;
    }

    /// <summary>Ajoute à la première page de la liste interactive d'une borne une tuile qui ouvre le contenu, sous les éléments déjà posés si la place le permet.</summary>
    public static string? AddTile(ClientDb d, Guid screenId, MediaItem content, string title)
    {
        if (d.Screens.FirstOrDefault(s => s.Id == screenId) is not { Kiosk: { } k } s) return "Borne introuvable.";
        if (k.ListId is not { } lid || d.InteractiveLists.FirstOrDefault(l => l.Id == lid) is not { } list) return $"La borne « {s.Name} » n'a pas encore de liste interactive : choisissez-en une dans sa fenêtre.";
        if (list.Pages.Count == 0) list.Pages.Add(new KioskPage());
        var page = list.Pages[0];
        var bottom = page.Els.Count == 0 ? 10 : page.Els.Max(e => e.Y + e.H) + 3;
        var el = new KioskEl { Kind = "tile", X = 10, Y = bottom + 20 <= 97 * Math.Max(1, page.Len) ? bottom : 10, W = 40, H = 20, Text = title, Sub = "Plan du bâtiment", Color = "#3B82C4",
            Action = new KioskAction { Type = "media", MediaId = content.Id } };
        page.Els.Add(el);
        list.Revision++; list.UpdatedUtc = DateTime.UtcNow;
        return null;
    }
}
