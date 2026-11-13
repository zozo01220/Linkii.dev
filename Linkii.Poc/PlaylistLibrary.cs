namespace Linkii.Poc;

/// <summary>
/// Dossiers des listes de lecture d'un client (option « Dossiers », comme la médiathèque) : ce que la personne voit, ce qu'elle peut faire.
/// Elle voit ses listes et ses dossiers, ceux qu'on lui partage, ou tout en lecture seule (voir <see cref="Ownership"/>).
/// </summary>
public class PlaylistLibrary(TenantStore store)
{
    /// <summary>Dossiers de l'aire que la personne voit, avec son droit sur chacun et leur place dans les onglets.</summary>
    public List<PlaylistFolderRow> Folders() => store.Read(d =>
    {
        var all = PlaylistFolderOps.All(d);
        var rows = new List<PlaylistFolderRow>();
        var shared = ItemShareOps.Counts(d, ShareKind.PlaylistFolder);
        foreach (var f in d.PlaylistFolders)
        {
            var right = FolderAccess.For(d.Access, f, all);
            if (right == FolderRight.None) continue;
            var owner = d.Users.FirstOrDefault(u => u.Id == f.OwnerId)?.Label ?? "—";
            rows.Add(new PlaylistFolderRow(f, right, owner, shared.ContainsKey(f.Id), FolderAccess.TabInfo(d.Access, f, all)));
        }
        return rows.OrderBy(r => r.Folder.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    /// <summary>Droit de la personne sur chaque liste de l'aire (voir <see cref="PlaylistOps.RightOf"/>).</summary>
    public Dictionary<Guid, FolderRight> PlaylistRights() => store.Read(PlaylistOps.Rights);

    /// <summary>Place de chaque liste visible dans les onglets « Mes listes / Partagées avec moi » (voir <see cref="LibraryTab"/>).</summary>
    public Dictionary<Guid, LibraryTab.Info> ListTabs() => store.Read(d =>
    {
        var folders = PlaylistFolderOps.All(d).ToDictionary(f => f.Id);
        var rights = PlaylistOps.Rights(d);
        return d.Playlists.Where(p => rights.GetValueOrDefault(p.Id) != FolderRight.None).ToDictionary(p => p.Id, p =>
        {
            var parents = Ownership.Parents(p.FolderId, folders);
            return Ownership.TabInfo(d.Access, ShareKind.Playlist, p.Id, p.OwnerId, parents);
        });
    });

    public (string? Error, PlaylistFolder? Folder) CreateFolder(string name, Guid? parentId) => store.Write(d => PlaylistFolderOps.Create(d, name, parentId));

    public string? RenameFolder(Guid id, string name) => store.Write(d => PlaylistFolderOps.Rename(d, id, name));

    public string? MoveFolder(Guid id, Guid? newParent) => store.Write(d => PlaylistFolderOps.MoveFolder(d, id, newParent));
    public List<FolderTarget> MoveFolderTargets(Guid id) => store.Read(d => PlaylistFolderOps.MoveTargets(d, id));
    public FolderContents ContentsOfFolder(Guid id) => store.Read(d => PlaylistFolderOps.ContentsOf(d, id));

    /// <summary>Supprime un dossier, ses sous-dossiers et les listes qu'ils contiennent.</summary>
    public string? DeleteFolder(Guid id)
    {
        var plan = store.Write(d =>
        {
            var err = PlaylistFolderOps.PlanDelete(d, id, out var folders, out var lists);
            return (err, folders, lists);
        });
        if (plan.err != null) return plan.err;
        store.Write(d =>
        {
            PlaylistOps.Remove(d, plan.lists);
            PlaylistFolderOps.Forget(d, plan.folders);
        });
        return null;
    }

    /// <summary>Range des listes dans un dossier, ou à la racine (null). Rend le nombre rangé et les refus.</summary>
    public (int Moved, List<string> Refused) MoveLists(IReadOnlyCollection<Guid> ids, Guid? folderId) => store.Write(d => PlaylistFolderOps.Move(d, ids, folderId));

    /// <summary>Où ranger : la racine (si la personne peut y ranger) et les dossiers où elle écrit, avec leur chemin.</summary>
    public List<(Guid? Id, string Path)> MoveTargets() => store.Read(d =>
    {
        var all = PlaylistFolderOps.All(d);
        var list = new List<(Guid? Id, string Path)>();
        if (FolderAccess.CanCreateAtRoot(d.Access, LevelPages.Playlists)) list.Add((null, "Mes listes de lecture (racine)"));
        foreach (var f in d.PlaylistFolders)
        {
            if (FolderAccess.For(d.Access, f, all) < FolderRight.Write) continue;
            var path = string.Join(" › ", FolderAccess.Chain(f, all).Reverse().Select(x => x.Name));
            list.Add((f.Id, path));
        }
        return list.OrderBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
    });
}

/// <summary>
/// Diffusion d'une liste de lecture : écrans qui l'ont choisie (une de leurs zones), écrans qui diffusent une version antérieure
/// (modifiée depuis la publication, ou jamais publiée), créneaux de planning qui la programment. « Utilisée » = choisie ou programmée,
/// partout pareil (filtre, tri, carte).
/// </summary>
public record PlaylistUse(int Screens, int Pending, int Slots, string? FirstSlot)
{
    public bool Used => Screens > 0 || Slots > 0;
    public static readonly PlaylistUse None = new(0, 0, 0, null);
}

/// <summary>Où une suppression de listes se fait sentir : écrans qui les ont choisies et créneaux de planning retirés (« zone « Rez » · Lun – Ven · 08:00 – 12:00 »).</summary>
public record PlaylistDeleteImpact(List<string> Screens, List<string> Slots);

/// <summary>
/// Format d'une liste de lecture : celui choisi dans ses réglages ; à défaut, celui de la plupart de ses contenus pleine page qui ont une taille
/// (images, vidéos, compositions) ; à défaut, celui de la plupart des écrans qui la diffusent en plein écran ; sinon paysage.
/// Sert à la carte (carrousel en portrait) et à l'ouverture de la fenêtre « Aperçu ».
/// </summary>
public static class PlaylistShape
{
    public static bool IsPortrait(Playlist p, Func<Guid, MediaItem?> media, IEnumerable<Screen> screens)
    {
        if (p.Orientation is "portrait" or "landscape") return p.Orientation == "portrait";
        int port = 0, land = 0;
        foreach (var i in p.Draft.Where(i => i.Placement == "full" && !i.Off))
            switch (media(i.MediaId) is { } m ? Portrait(m) : null) { case true: port++; break; case false: land++; break; }
        if (port + land == 0)
            foreach (var s in screens.Where(s => s.PlaylistId == p.Id))
                if (s.Orientation == "portrait") port++; else land++;
        return port > land;
    }

    /// <summary>Un contenu en hauteur (true), en largeur (false), ou sans taille connue (null : apps, pages web…).</summary>
    public static bool? Portrait(MediaItem m)
    {
        var size = m.AppId == Compositions.AppId ? Compositions.Size(m.Settings)
            : m.Width is > 0 and var w && m.Height is > 0 and var h ? (w, h) : ((int W, int H)?)null;
        return size is { } z && z.W != z.H ? z.H > z.W : null;
    }
}

public static class PlaylistUses
{
    public static Dictionary<Guid, PlaylistUse> Of(IEnumerable<Playlist> lists, IReadOnlyCollection<Screen> screens, IEnumerable<Area> areas, IEnumerable<ScreenCalendar>? calendars = null)
    {
        var zones = areas.SelectMany(a => a.Zones).ToList();
        var cals = calendars?.ToList() ?? new();
        var result = new Dictionary<Guid, PlaylistUse>();
        foreach (var p in lists)
        {
            var chosen = screens.Where(s => s.ZonePlaylists().Contains(p.Id)).ToList();
            var pending = chosen.Count(s => !PublishedWith(s, p))
                + screens.Count(s => !chosen.Contains(s) && s.PublishedListRevs.TryGetValue(p.Id, out var rev) && rev != p.Revision);   // liste du planning modifiée depuis son instantané
            // planning des murs (zones), puis calendriers des Programmations (événements et liste par défaut)
            var slots = areas.SelectMany(a => a.Slots).Concat(zones.SelectMany(z => z.Slots)).Concat(screens.Where(s => s.CalendarId == null).SelectMany(s => s.Slots))
                .Where(x => x.PlaylistId == p.Id).Select(Planning.Describe).ToList();
            foreach (var c in cals)
            {
                slots.AddRange(c.Events.Where(e => e.Kind == CalEvent.Play && e.PlaylistId == p.Id).Select(e => $"{c.Name} · {Calendars.Describe(e)}"));
                if (c.DefaultPlaylistId == p.Id) slots.Add($"{c.Name} · liste par défaut");
            }
            result[p.Id] = new PlaylistUse(chosen.Count, pending, slots.Count, slots.FirstOrDefault());
        }
        return result;
    }

    /// <summary>
    /// L'écran a publié cette liste dans sa révision actuelle. Lu dans l'empreinte de publication (<see cref="Helpers.PublishStamp"/>) :
    /// « liste:révision:widgets » puis, découpé, « |découpage|liste:révision » par zone.
    /// </summary>
    public static bool PublishedWith(Screen s, Playlist p)
    {
        if (string.IsNullOrEmpty(s.PublishedStamp)) return false;
        var id = p.Id.ToString();
        var rev = p.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return s.PublishedStamp.Split('|').Select(part => part.Split(':')).Any(x => x.Length >= 2 && x[0] == id && x[1] == rev);
    }

    /// <summary>Ce que retirerait <see cref="PlaylistOps.Remove"/> : les écrans qui les ont choisies (ils gardent leur instantané) et les créneaux de planning.</summary>
    public static PlaylistDeleteImpact Impact(IReadOnlyCollection<Guid> ids, IEnumerable<Screen> screens, IEnumerable<Area> areas, IEnumerable<ScreenCalendar>? calendars = null)
    {
        var calSlots = new List<string>();
        foreach (var c in calendars ?? Enumerable.Empty<ScreenCalendar>())
        {
            calSlots.AddRange(c.Events.Where(e => e.Kind == CalEvent.Play && e.PlaylistId is { } pid && ids.Contains(pid)).Select(e => $"calendrier « {c.Name} » · {Calendars.Describe(e)}"));
            if (ids.Contains(c.DefaultPlaylistId)) calSlots.Add($"calendrier « {c.Name} » · liste par défaut (à remplacer)");
        }
        var set = ids.ToHashSet();
        var list = screens.ToList();
        var chosen = list.Where(s => s.ZonePlaylists().Any(z => z is { } zid && set.Contains(zid))).Select(s => s.Name).ToList();
        var slots = new List<string>();
        foreach (var a in areas)
            slots.AddRange(a.Slots.Where(x => set.Contains(x.PlaylistId)).Select(x => $"aire « {a.Name} » · {Planning.Describe(x)}"));
        foreach (var z in areas.SelectMany(a => a.Zones))
            slots.AddRange(z.Slots.Where(x => set.Contains(x.PlaylistId)).Select(x => $"zone « {z.Name} » · {Planning.Describe(x)}"));
        foreach (var s in list.Where(s => s.CalendarId == null))   // les créneaux d'un calendrier sont comptés avec lui
            slots.AddRange(s.Slots.Where(x => set.Contains(x.PlaylistId)).Select(x => $"écran « {s.Name} » · {Planning.Describe(x)}"));
        slots.AddRange(calSlots);
        return new PlaylistDeleteImpact(chosen, slots);
    }
}

/// <summary>Un dossier de listes vu par la personne, avec son droit, le nom de son propriétaire et s'il est partagé.</summary>
public record PlaylistFolderRow(PlaylistFolder Folder, FolderRight Right, string OwnerName, bool Shared, LibraryTab.Info Tab);

