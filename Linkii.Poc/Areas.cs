namespace Linkii.Poc;

/// <summary>
/// Aires de gestion d'une organisation : création, zones, suppression, et déplacement d'un écran, d'une liste de lecture ou d'un média
/// d'une aire à une autre (administrateur de l'organisation). Toutes les méthodes retournent un message d'erreur, ou null si c'est fait.
/// </summary>
public static class AreaOps
{
    public static string? CheckName(string name) => name.Trim().Length == 0 ? "Donnez un nom." : name.Trim().Length > 60 ? "60 caractères au plus." : null;

    public static (Area? Area, string? Error) Create(ClientDb d, string name)
    {
        if (!d.Access.IsOrgAdmin) return (null, "Seul un administrateur de l'organisation crée des aires.");
        if (CheckName(name) is { } e) return (null, e);
        var n = name.Trim();
        if (d.Areas.Any(a => a.Name.Equals(n, StringComparison.CurrentCultureIgnoreCase))) return (null, "Une aire porte déjà ce nom.");
        var area = new Area { Name = n };
        EnsureDefaultZone(area);
        d.Areas.Add(area);
        return (area, null);
    }

    /// <summary>
    /// La zone par défaut de l'aire (créée au besoin) : tout écran est dans une zone. Si l'aire a déjà une zone nommée « Défaut », c'est elle qui le devient.
    /// </summary>
    public static Zone EnsureDefaultZone(Area a)
    {
        var z = a.Zones.FirstOrDefault(x => x.IsDefault)
            ?? a.Zones.FirstOrDefault(x => x.Name.Equals(Zone.DefaultName, StringComparison.CurrentCultureIgnoreCase));
        if (z == null) a.Zones.Insert(0, z = new Zone { Name = Zone.DefaultName });
        z.IsDefault = true;
        z.Kind = Zone.Free;
        return z;
    }

    public static string? Rename(ClientDb d, Guid areaId, string name)
    {
        if (!d.Access.IsOrgAdmin) return "Seul un administrateur de l'organisation renomme les aires.";
        if (CheckName(name) is { } e) return e;
        var a = d.Areas.FirstOrDefault(x => x.Id == areaId);
        if (a == null) return "Aire introuvable.";
        var n = name.Trim();
        if (d.Areas.Any(x => x.Id != areaId && x.Name.Equals(n, StringComparison.CurrentCultureIgnoreCase))) return "Une aire porte déjà ce nom.";
        a.Name = n;
        return null;
    }

    /// <summary>Contenu d'une aire, toutes aires confondues (l'aire ouverte n'a pas d'importance).</summary>
    public static (int Screens, int Playlists, int Media) Contents(ClientDb d, Guid areaId) =>
        (d.Root.Screens.Count(s => s.ClientId == d.ClientId && s.AreaId == areaId && s.Token != null),
         d.Root.Playlists.Count(p => p.ClientId == d.ClientId && p.AreaId == areaId),
         d.Root.Media.Count(m => m.ClientId == d.ClientId && m.AreaId == areaId && m.Status != "failed"));

    /// <summary>Une aire ne se supprime que vide (rien n'est perdu par mégarde), et jamais la dernière.</summary>
    public static string? Delete(ClientDb d, Guid areaId)
    {
        if (!d.Access.IsOrgAdmin) return "Seul un administrateur de l'organisation supprime les aires.";
        if (d.Areas.Count <= 1) return "L'organisation garde au moins une aire.";
        var (s, p, m) = Contents(d, areaId);
        if (s + p + m > 0) return $"Videz d'abord l'aire : {s} écran(s), {p} liste(s) de lecture, {m} média(s). Déplacez-les vers une autre aire ou supprimez-les.";
        d.Root.Media.RemoveAll(x => x.ClientId == d.ClientId && x.AreaId == areaId);   // vidéos en échec de conversion
        foreach (var u in d.Users) u.AreaRoles.RemoveAll(r => r.AreaId == areaId);
        d.Areas.RemoveAll(a => a.Id == areaId);
        return null;
    }

    // ---------- Zones (administrateurs de l'aire) ----------

    private static (Area? Area, string? Error) Managed(ClientDb d, Guid areaId)
    {
        if (!d.Access.CanManage(areaId)) return (null, "Seuls les administrateurs de l'aire gèrent ses zones.");
        return d.Areas.FirstOrDefault(a => a.Id == areaId) is { } a ? (a, null) : (null, "Aire introuvable.");
    }

    public static (Zone? Zone, string? Error) AddZone(ClientDb d, Guid areaId, string name)
    {
        var (a, err) = Managed(d, areaId);
        if (a == null) return (null, err);
        if (CheckName(name) is { } e) return (null, e);
        var n = name.Trim();
        if (a.Zones.Any(z => z.Name.Equals(n, StringComparison.CurrentCultureIgnoreCase))) return (null, "Cette zone existe déjà.");
        var zone = new Zone { Name = n };
        a.Zones.Add(zone);
        return (zone, null);
    }

    public static string? RenameZone(ClientDb d, Guid areaId, Guid zoneId, string name)
    {
        var (a, err) = Managed(d, areaId);
        if (a == null) return err;
        if (CheckName(name) is { } e) return e;
        var n = name.Trim();
        if (a.Zones.FirstOrDefault(z => z.Id == zoneId) is not { } zone) return "Zone introuvable.";
        if (a.Zones.Any(z => z.Id != zoneId && z.Name.Equals(n, StringComparison.CurrentCultureIgnoreCase))) return "Cette zone existe déjà.";
        zone.Name = n;
        return null;
    }

    /// <summary>Ses écrans retournent dans la zone par défaut de l'aire. La zone par défaut ne se supprime pas.</summary>
    public static string? DeleteZone(ClientDb d, Guid areaId, Guid zoneId)
    {
        var (a, err) = Managed(d, areaId);
        if (a == null) return err;
        if (a.Zones.FirstOrDefault(z => z.Id == zoneId) is not { } zone) return "Zone introuvable.";
        if (zone.IsDefault) return "La zone par défaut ne se supprime pas.";
        a.Zones.Remove(zone);
        var fallback = EnsureDefaultZone(a).Id;
        foreach (var s in d.Root.Screens.Where(s => s.ClientId == d.ClientId && s.ZoneId == zoneId)) { s.ZoneId = fallback; s.WallPos = null; }
        return null;
    }

    /// <summary>
    /// Active ou coupe la lecture synchronisée d'une zone. L'origine de la boucle est fixée à l'activation : tous les écrans de la zone la partagent.
    /// (Aucune republication : la durée des contenus « jusqu'à la fin » est fixée à l'envoi vers l'écran.)
    /// </summary>
    public static string? SetZoneSync(ClientDb d, Guid areaId, Guid zoneId, bool on)
    {
        var (a, err) = Managed(d, areaId);
        if (a == null) return err;
        if (a.Zones.FirstOrDefault(z => z.Id == zoneId) is not { } zone) return "Zone introuvable.";
        if (zone.Kind != Zone.Free) return "Cette zone ne se synchronise pas ainsi.";
        if (zone.Sync == on) return null;
        zone.Sync = on;
        if (on) zone.SyncEpochUtc = DateTime.UtcNow;
        return null;
    }

    // ---------- Mur d'écrans (administrateurs de l'aire) ----------

    /// <summary>
    /// Assemble des écrans de l'aire en mur : une zone de type mur est créée, chaque écran y prend sa place dans l'ordre donné
    /// (de gauche à droite, puis de haut en bas) et joue la liste du mur, en écran plein. La lecture est synchronisée d'office.
    /// À publier ensuite sur chaque écran (l'appelant s'en charge).
    /// </summary>
    public static (Zone? Zone, string? Error) CreateWall(ClientDb d, Guid areaId, IList<Guid> screenIds, int cols, int rows, Guid? playlistId, string name)
    {
        var (a, err) = Managed(d, areaId);
        if (a == null) return (null, err);
        var (screens, e) = WallScreens(d, a, screenIds, cols, rows, null);
        if (screens == null) return (null, e);
        if (playlistId is { } pid && !d.Root.Playlists.Any(p => p.Id == pid && p.ClientId == d.ClientId && p.AreaId == areaId)) return (null, "Liste de lecture introuvable.");
        var n = (name ?? "").Trim();
        if (n.Length == 0) n = $"Mur {cols} × {rows}";
        if (CheckName(n) is { } ne) return (null, ne);

        var zone = new Zone { Name = UniqueName(a, n, Guid.Empty), Kind = Zone.Wall, Sync = true, SyncEpochUtc = DateTime.UtcNow, WallCols = cols, WallRows = rows, PlaylistId = playlistId };
        a.Zones.Add(zone);
        for (var i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            s.ZoneId = zone.Id;
            s.WallPos = i;
            JoinWall(s, playlistId);
        }
        return (zone, null);
    }

    /// <summary>Réorganise un mur : autre grille et/ou autre ordre, avec les mêmes écrans.</summary>
    public static string? ArrangeWall(ClientDb d, Guid areaId, Guid zoneId, IList<Guid> screenIds, int cols, int rows)
    {
        var (a, err) = Managed(d, areaId);
        if (a == null) return err;
        if (a.Zones.FirstOrDefault(z => z.Id == zoneId) is not { IsWall: true } zone) return "Mur introuvable.";
        var (screens, e) = WallScreens(d, a, screenIds, cols, rows, zone.Id);
        if (screens == null) return e;
        if (d.Root.Screens.Any(s => s.ClientId == d.ClientId && s.ZoneId == zone.Id && !screenIds.Contains(s.Id))) return "Tous les écrans du mur doivent garder une place.";
        zone.WallCols = cols;
        zone.WallRows = rows;
        if (System.Text.RegularExpressions.Regex.IsMatch(zone.Name, @"^Mur \d+ × \d+( \(\d+\))?$")) zone.Name = UniqueName(a, $"Mur {cols} × {rows}", zone.Id);   // nom proposé : suit la grille
        for (var i = 0; i < screens.Count; i++) screens[i].WallPos = i;
        return null;
    }

    /// <summary>Liste de lecture du mur : tous ses écrans la jouent (à publier ensuite).</summary>
    public static string? SetWallPlaylist(ClientDb d, Guid areaId, Guid zoneId, Guid? playlistId)
    {
        var (a, err) = Managed(d, areaId);
        if (a == null) return err;
        if (a.Zones.FirstOrDefault(z => z.Id == zoneId) is not { IsWall: true } zone) return "Mur introuvable.";
        if (playlistId is { } pid && !d.Root.Playlists.Any(p => p.Id == pid && p.ClientId == d.ClientId && p.AreaId == areaId)) return "Liste de lecture introuvable.";
        zone.PlaylistId = playlistId;
        foreach (var s in d.Root.Screens.Where(s => s.ClientId == d.ClientId && s.ZoneId == zone.Id)) JoinWall(s, playlistId);
        return null;
    }

    /// <summary>Dissout un mur : ses écrans retournent dans la zone par défaut et gardent la liste du mur (chacun la joue en entier).</summary>
    public static string? DissolveWall(ClientDb d, Guid areaId, Guid zoneId)
    {
        var (a, err) = Managed(d, areaId);
        if (a == null) return err;
        if (a.Zones.FirstOrDefault(z => z.Id == zoneId) is not { IsWall: true }) return "Mur introuvable.";
        return DeleteZone(d, areaId, zoneId);
    }

    /// <summary>Écran du mur : écran plein, liste du mur, sans widgets (ils seraient coupés ou répétés sur la grande image).</summary>
    private static void JoinWall(Screen s, Guid? playlistId)
    {
        s.PlaylistId = playlistId;
        s.Layout = ScreenLayouts.Full;
        s.ZonePlaylistIds.Clear();
        if (s.Widgets.Count > 0) { s.Widgets.Clear(); s.WidgetsRevision++; }
    }

    private static (List<Screen>? Screens, string? Error) WallScreens(ClientDb d, Area a, IList<Guid> ids, int cols, int rows, Guid? wallId)
    {
        if (ids.Count < 2) return (null, "Un mur réunit au moins 2 écrans.");
        if (ids.Distinct().Count() != ids.Count) return (null, "Un écran ne prend qu'une place.");
        if (cols < 1 || rows < 1 || cols * rows != ids.Count) return (null, $"La grille {cols} × {rows} ne correspond pas aux {ids.Count} écrans.");
        var screens = new List<Screen>();
        foreach (var id in ids)
        {
            var s = d.Root.Screens.FirstOrDefault(x => x.Id == id && x.ClientId == d.ClientId && x.AreaId == a.Id && x.Token != null);
            if (s == null) return (null, "Écran introuvable dans cette aire.");
            if (s.ZoneId != wallId && a.Zones.FirstOrDefault(z => z.Id == s.ZoneId) is { IsWall: true } other)
                return (null, $"« {s.Name} » fait déjà partie du mur « {other.Name} ».");
            screens.Add(s);
        }
        return (screens, null);
    }

    private static string UniqueName(Area a, string n, Guid self)
    {
        var unique = n;
        for (var i = 2; a.Zones.Any(z => z.Id != self && z.Name.Equals(unique, StringComparison.CurrentCultureIgnoreCase)); i++) unique = $"{n} ({i})";
        return unique;
    }

    /// <summary>Écran déjà placé dans un mur : il n'en sort que par la dissolution du mur.</summary>
    public static Zone? WallOf(ClientDb d, Screen s) =>
        d.Root.Areas.FirstOrDefault(x => x.Id == s.AreaId)?.Zones.FirstOrDefault(z => z.Id == s.ZoneId && z.IsWall);

    // ---------- Déplacements entre aires (administrateur de l'organisation) ----------

    private static string? CheckTarget(ClientDb d, Guid target) =>
        !d.Access.IsOrgAdmin ? "Seul un administrateur de l'organisation déplace un élément vers une autre aire."
        : d.Areas.Any(a => a.Id == target) ? null : "Aire introuvable.";

    /// <summary>
    /// L'écran continue d'afficher ce qui y est publié. Ses listes de lecture restent dans l'ancienne aire : il n'en a plus
    /// (modification à publier) jusqu'à ce qu'on lui en choisisse une de sa nouvelle aire.
    /// </summary>
    public static string? MoveScreen(ClientDb d, Guid screenId, Guid target)
    {
        if (CheckTarget(d, target) is { } e) return e;
        var s = d.Root.Screens.FirstOrDefault(x => x.Id == screenId && x.ClientId == d.ClientId);
        if (s == null) return "Écran introuvable.";
        if (s.AreaId == target) return null;
        if (WallOf(d, s) is { } wall) return $"« {s.Name} » fait partie du mur « {wall.Name} » : dissolvez d'abord le mur.";
        s.AreaId = target;
        s.WallPos = null;
        s.ZoneId = EnsureDefaultZone(d.Areas.First(a => a.Id == target)).Id;   // zone par défaut de l'aire d'arrivée
        bool Elsewhere(Guid? id) => id is { } pid && d.Root.Playlists.FirstOrDefault(p => p.Id == pid)?.AreaId != target;
        if (Elsewhere(s.PlaylistId)) s.PlaylistId = null;
        for (var z = 0; z < s.ZonePlaylistIds.Count; z++) if (Elsewhere(s.ZonePlaylistIds[z])) s.ZonePlaylistIds[z] = null;
        return null;
    }

    /// <summary>
    /// Une liste de lecture part avec les contenus qu'elle utilise (contenus d'apps et fichiers qu'ils affichent), à condition qu'aucune autre
    /// liste de son aire ne les utilise. Les écrans de l'ancienne aire qui la diffusaient gardent leur publication, sans liste choisie.
    /// </summary>
    public static string? MovePlaylist(ClientDb d, Guid playlistId, Guid target)
    {
        if (CheckTarget(d, target) is { } e) return e;
        var pl = d.Root.Playlists.FirstOrDefault(x => x.Id == playlistId && x.ClientId == d.ClientId);
        if (pl == null) return "Liste de lecture introuvable.";
        if (pl.AreaId == target) return null;
        var source = pl.AreaId;
        var media = d.Root.Media.Where(m => m.ClientId == d.ClientId && m.AreaId == source).ToList();
        var items = pl.Draft.Select(i => i.MediaId).ToHashSet();
        var files = media.Where(m => items.Contains(m.Id) && m.AppId != null).SelectMany(Referenced).ToHashSet();
        var moving = media.Where(m => items.Contains(m.Id) || files.Contains(m.Id)).ToList();
        var ids = moving.Select(m => m.Id).ToHashSet();

        // contenus partagés avec une autre liste de lecture (ou un autre contenu d'app) de l'aire : on ne les arrache pas
        var others = d.Root.Playlists.Where(p => p.ClientId == d.ClientId && p.AreaId == source && p.Id != pl.Id && p.Draft.Any(i => ids.Contains(i.MediaId))).Select(p => "« " + p.Name + " »");
        var apps = media.Where(m => m.AppId != null && !ids.Contains(m.Id) && Referenced(m).Any(ids.Contains)).Select(m => "« " + m.Name + " »");
        var blockers = others.Concat(apps).Distinct().ToList();
        if (blockers.Count > 0) return $"Des contenus de cette liste sont aussi utilisés par {string.Join(", ", blockers)}. Dupliquez la liste et retirez-les, ou déplacez aussi ces éléments.";

        pl.AreaId = target;
        foreach (var m in moving.Where(m => !m.Info.ContainsKey("drive"))) m.AreaId = target;   // les fichiers Drive restent communs
        foreach (var s in d.Root.Screens.Where(s => s.ClientId == d.ClientId && s.AreaId == source))
        {
            if (s.PlaylistId == pl.Id) s.PlaylistId = null;
            for (var z = 0; z < s.ZonePlaylistIds.Count; z++) if (s.ZonePlaylistIds[z] == pl.Id) s.ZonePlaylistIds[z] = null;
        }
        return null;
    }

    /// <summary>Une image ou une vidéo ne part que si aucun contenu ni aucune liste de son aire ne l'utilise.</summary>
    public static string? MoveMedia(ClientDb d, Guid mediaId, Guid target)
    {
        if (CheckTarget(d, target) is { } e) return e;
        var m = d.Root.Media.FirstOrDefault(x => x.Id == mediaId && x.ClientId == d.ClientId);
        if (m == null || m.AppId != null) return "Fichier introuvable.";
        if (m.Info.ContainsKey("drive")) return "Les fichiers d'un dossier Drive sont communs à toutes les aires.";
        if (m.AreaId == target) return null;
        var users = d.Root.Media.Where(x => x.ClientId == d.ClientId && x.AreaId == m.AreaId && x.AppId != null && Referenced(x).Contains(m.Id)).Select(x => "« " + x.Name + " »")
            .Concat(d.Root.Playlists.Where(p => p.ClientId == d.ClientId && p.AreaId == m.AreaId && p.Draft.Any(i => i.MediaId == m.Id)).Select(p => "la liste « " + p.Name + " »"))
            .Distinct().ToList();
        if (users.Count > 0) return $"Ce fichier est utilisé par {string.Join(", ", users)}. Retirez-le d'abord, ou déplacez la liste de lecture entière.";
        m.AreaId = target;
        return null;
    }

    /// <summary>Fichiers affichés par un contenu d'app (champs « media » / « medias » : identifiants séparés par des virgules).</summary>
    private static IEnumerable<Guid> Referenced(MediaItem instance) => instance.Settings.Values.SelectMany(AppField.MediaIds);
}
