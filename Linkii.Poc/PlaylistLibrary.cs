namespace Linkii.Poc;

/// <summary>
/// Dossiers des listes de lecture d'un client (option « Dossiers », comme la médiathèque) : ce que la personne voit, ce qu'elle peut faire, partages.
/// Les listes rangées dans un dossier qu'elle ne voit pas n'apparaissent pas dans sa liste.
/// </summary>
public class PlaylistLibrary(TenantStore store)
{
    /// <summary>Dossiers de l'aire que la personne voit, avec son droit sur chacun.</summary>
    public List<PlaylistFolderRow> Folders() => store.Read(d =>
    {
        var all = PlaylistFolderOps.All(d);
        var rows = new List<PlaylistFolderRow>();
        foreach (var f in d.PlaylistFolders)
        {
            var right = FolderAccess.For(d.Access, f, all);
            if (right == FolderRight.None) continue;
            var owner = d.Users.FirstOrDefault(u => u.Id == f.OwnerId)?.Label ?? "—";
            rows.Add(new PlaylistFolderRow(f, right, owner, f.Shares.Count > 0));
        }
        return rows.OrderBy(r => r.Folder.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    /// <summary>Droit de la personne sur chaque liste de l'aire (voir <see cref="PlaylistOps.RightOf"/>).</summary>
    public Dictionary<Guid, FolderRight> PlaylistRights() => store.Read(d => d.Playlists.ToDictionary(p => p.Id, p => PlaylistOps.RightOf(d, p)));

    public (string? Error, PlaylistFolder? Folder) CreateFolder(string name, Guid? parentId) => store.Write(d => PlaylistFolderOps.Create(d, name, parentId));

    public string? RenameFolder(Guid id, string name) => store.Write(d => PlaylistFolderOps.Rename(d, id, name));

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

    /// <summary>Partages d'un dossier de listes, et les personnes et niveaux à qui l'on peut en donner.</summary>
    public PlaylistShareInfo? ShareInfo(Guid folderId) => store.Read(d =>
    {
        if (PlaylistFolderOps.Find(d, folderId) is not { } f) return null;
        var area = d.Root.Areas.FirstOrDefault(a => a.Id == f.AreaId)?.Name ?? "";
        var people = d.Users.Where(u => u.Role == Roles.ClientMember).OrderBy(u => u.Label).Select(u =>
        {
            var ua = AreaAccess.For(d.Root, d.ClientId, u.Id);
            var reason = u.Disabled ? "compte désactivé"
                : !ua.HasArea(f.AreaId) ? $"ne voit pas l'aire « {area} »"
                : ua.Level?.Pages.Contains(LevelPages.Playlists) != true ? "n'a pas la page Listes de lecture" : null;
            return new ShareCandidate(u.Id, u.Label, ua.Level?.Name ?? "Sans niveau", reason);
        }).ToList();
        var levels = d.Tenant.Levels.Select(l => new ShareCandidate(l.Id, l.Name,
            d.Users.Count(u => u.LevelId == l.Id) is var n && n > 0 ? $"{n} membre{(n > 1 ? "s" : "")}" : "aucun membre", null)).ToList();
        var shares = f.Shares.Select(s =>
        {
            var label = s.Kind == FolderShareKind.Level ? levels.FirstOrDefault(l => l.Id == s.TargetId)?.Label : people.FirstOrDefault(p => p.Id == s.TargetId)?.Label;
            return new ShareRow(s.Kind, s.TargetId, label ?? "Retiré de l'organisation", s.Write);
        }).ToList();
        return new PlaylistShareInfo(f, shares, people, levels, FolderAccess.CanShare(d.Access, f));
    });

    public string? SetShare(Guid folderId, string kind, Guid target, bool? write) => store.Write(d => PlaylistFolderOps.SetShare(d, folderId, kind, target, write));

    /// <summary>Range des listes dans un dossier, ou à la racine (null). Rend le nombre rangé et les refus.</summary>
    public (int Moved, List<string> Refused) MoveLists(IReadOnlyCollection<Guid> ids, Guid? folderId) => store.Write(d => PlaylistFolderOps.Move(d, ids, folderId));

    /// <summary>Où ranger : la racine (si la personne peut y ranger) et les dossiers où elle écrit, avec leur chemin.</summary>
    public List<(Guid? Id, string Path)> MoveTargets() => store.Read(d =>
    {
        var all = PlaylistFolderOps.All(d);
        var list = new List<(Guid? Id, string Path)>();
        if (FolderAccess.CanCreateAtRoot(d.Access)) list.Add((null, "Listes de lecture de l'aire (racine)"));
        foreach (var f in d.PlaylistFolders)
        {
            if (FolderAccess.For(d.Access, f, all) < FolderRight.Write) continue;
            var path = string.Join(" › ", FolderAccess.Chain(f, all).Reverse().Select(x => x.Name));
            list.Add((f.Id, path));
        }
        return list.OrderBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
    });
}

/// <summary>Un dossier de listes vu par la personne, avec son droit, le nom de son propriétaire et s'il est partagé.</summary>
public record PlaylistFolderRow(PlaylistFolder Folder, FolderRight Right, string OwnerName, bool Shared);

/// <summary>Partages d'un dossier de listes : ceux en place, les personnes et niveaux possibles, et si la personne peut partager.</summary>
public record PlaylistShareInfo(PlaylistFolder Folder, List<ShareRow> Shares, List<ShareCandidate> People, List<ShareCandidate> Levels, bool CanShare);
