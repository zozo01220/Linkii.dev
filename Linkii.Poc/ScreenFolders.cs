namespace Linkii.Poc;

/// <summary>
/// Dossier de rangement des écrans (25 octobre 2026) : purement organisationnel, propre à une aire, à côté des zones (qui, elles, portent la diffusion,
/// le planning et la synchro). Pas de partage : la visibilité d'un écran reste celle de ses zones et de ses attributions. Un membre voit les dossiers
/// qui contiennent un écran qu'il voit, et les siens ; un administrateur voit tout. Un écran y est rangé par son <see cref="Screen.FolderId"/>.
/// </summary>
public class ScreenFolder : IClientOwned, IFolderNode, IOwned
{
    public Guid ClientId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AreaId { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public Guid? OwnerId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Pas de partage pour les écrans : liste toujours vide (nécessaire pour réutiliser les règles de <see cref="FolderAccess"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public List<FolderShare> Shares { get; } = new();
}

/// <summary>Un dossier d'écrans vu par la personne : peut-elle le gérer (renommer, déplacer, supprimer), et son propriétaire.</summary>
public record ScreenFolderRow(ScreenFolder Folder, bool CanManage, string OwnerName);

/// <summary>Création, renommage, déplacement et suppression des dossiers d'écrans, rangement des écrans. Chaque méthode rend un message d'erreur, ou null si c'est fait.</summary>
public static class ScreenFolderOps
{
    /// <summary>Clé du type de dossier dans <c>FolderReparentDialog</c>.</summary>
    public const string Kind = "screenfolder";

    public static ScreenFolder? Find(ClientDb d, Guid id) => d.ScreenFolders.FirstOrDefault(f => f.Id == id);

    public static bool CanManage(ClientDb d, ScreenFolder f) => d.Access.IsOrgAdmin || f.OwnerId != null && f.OwnerId == d.Me;

    /// <summary>Créer un dossier, ranger des écrans : modifier les écrans, dans une aire ouverte.</summary>
    public static bool CanWrite(ClientDb d) => d.Area != null && d.Access.CanWrite(LevelPages.Screens);

    /// <summary>Dossiers de l'aire ouverte que la personne voit : tous pour un administrateur ; sinon les siens et ceux qui contiennent un écran qu'elle voit (sous-dossiers compris).</summary>
    public static List<ScreenFolderRow> Rows(ClientDb d)
    {
        var all = d.ScreenFolders.ToList();
        var byId = all.ToDictionary(f => f.Id);
        var seen = new HashSet<Guid>();   // dossiers qui contiennent un écran visible, et leurs parents
        foreach (var s in d.Screens.Where(s => s.FolderId != null))
            for (var cur = s.FolderId; cur is { } id && byId.TryGetValue(id, out var f) && seen.Add(id); cur = f.ParentId) { }
        var names = d.Users.ToDictionary(u => u.Id, u => u.Label);
        return all.Where(f => d.Access.IsOrgAdmin || seen.Contains(f.Id) || f.OwnerId != null && f.OwnerId == d.Me)
            .Select(f => new ScreenFolderRow(f, CanManage(d, f), f.OwnerId is { } o && names.TryGetValue(o, out var n) ? n : "—"))
            .OrderBy(r => r.Folder.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static (string? Error, ScreenFolder? Folder) Create(ClientDb d, string name, Guid? parentId)
    {
        name = name.Trim();
        if (FolderOps.CheckName(name) is { } e) return (e, null);
        if (!CanWrite(d)) return ("Vous ne pouvez pas créer de dossier ici (lecture seule).", null);
        var all = d.ScreenFolders.ToList();
        var depth = 1;
        if (parentId is { } pid)
        {
            if (Find(d, pid) is not { } parent) return ("Dossier introuvable.", null);
            depth = FolderAccess.Chain(parent, all).Count() + 1;
        }
        if (depth > FolderAccess.MaxDepth) return ($"Les dossiers ne s'imbriquent pas au-delà de {FolderAccess.MaxDepth} niveaux.", null);
        if (all.Any(f => f.ParentId == parentId && f.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))) return ("Un dossier porte déjà ce nom ici.", null);
        var folder = new ScreenFolder { Name = name, ParentId = parentId };
        d.ScreenFolders.Add(folder);
        return (null, folder);
    }

    public static string? Rename(ClientDb d, Guid id, string name)
    {
        if (Find(d, id) is not { } f) return "Dossier introuvable.";
        if (!CanManage(d, f)) return "Seul le propriétaire du dossier (ou un administrateur) peut le renommer.";
        name = name.Trim();
        if (FolderOps.CheckName(name) is { } e) return e;
        if (d.ScreenFolders.Any(x => x.Id != id && x.ParentId == f.ParentId && x.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))) return "Un dossier porte déjà ce nom ici.";
        f.Name = name;
        return null;
    }

    /// <summary>Supprime un dossier sans toucher aux écrans : ses écrans et ses sous-dossiers remontent d'un niveau.</summary>
    public static string? Delete(ClientDb d, Guid id)
    {
        if (Find(d, id) is not { } f) return "Dossier introuvable.";
        if (!CanManage(d, f)) return "Seul le propriétaire du dossier (ou un administrateur) peut le supprimer.";
        if (d.ScreenFolders.Any(x => x.ParentId == f.ParentId && x.Id != id && d.ScreenFolders.Any(c => c.ParentId == id && c.Name.Equals(x.Name, StringComparison.CurrentCultureIgnoreCase))))
            return "Un sous-dossier porte le même nom qu'un dossier du niveau au-dessus : renommez-le d'abord.";
        foreach (var s in d.Root.Screens.Where(s => s.ClientId == d.ClientId && s.FolderId == id)) s.FolderId = f.ParentId;
        foreach (var c in d.ScreenFolders.Where(x => x.ParentId == id)) c.ParentId = f.ParentId;
        d.Root.ScreenFolders.RemoveAll(x => x.Id == id && x.ClientId == d.ClientId);
        return null;
    }

    public static FolderContents ContentsOf(ClientDb d, Guid id) =>
        new(d.Screens.Count(s => s.FolderId == id), d.ScreenFolders.Count(f => f.ParentId == id));

    public static List<FolderTarget> MoveTargets(ClientDb d, Guid id)
    {
        var all = d.ScreenFolders.ToList();
        if (Find(d, id) is not { } f) return new();
        var list = new List<FolderTarget> { new(null, "Racine des écrans", 0, f.ParentId == null ? "déjà à la racine" : null) };
        foreach (var g in all.Select(x => (Folder: x, Path: string.Join(" › ", FolderAccess.Chain(x, all).Reverse().Select(c => c.Name)))).OrderBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase))
        {
            var reason = g.Folder.Id == f.ParentId ? "déjà ici" : Why(all, f, g.Folder);
            list.Add(new(g.Folder.Id, g.Path, g.Path.Count(c => c == '›'), reason));
        }
        return list;
    }

    // Pourquoi un dossier ne peut pas en recevoir un autre (null : possible)
    private static string? Why(List<ScreenFolder> all, ScreenFolder f, ScreenFolder target) =>
        FolderMove.CheckTarget(all, f, target, FolderRight.Write) is { } r ? FolderMove.Explain(r) : null;

    /// <summary>Range un dossier dans un autre, ou à la racine (null) ; le dissoudre se fait par « Supprimer ».</summary>
    public static string? MoveFolder(ClientDb d, Guid id, Guid? newParent)
    {
        if (Find(d, id) is not { } f) return "Dossier introuvable.";
        if (!CanManage(d, f)) return "Seul le propriétaire du dossier (ou un administrateur) peut le déplacer.";
        if (f.ParentId == newParent) return null;
        var all = d.ScreenFolders.ToList();
        if (newParent is { } tid)
        {
            if (Find(d, tid) is not { } target) return "Dossier introuvable.";
            if (Why(all, f, target) is { } reason) return reason;
        }
        else if (all.Any(x => x.ParentId == null && x.Id != id && x.Name.Equals(f.Name, StringComparison.CurrentCultureIgnoreCase))) return "Un dossier porte déjà ce nom à la racine : renommez-le d'abord.";
        f.ParentId = newParent;
        return null;
    }

    /// <summary>Range des écrans dans un dossier, ou à la racine (null). Les écrans d'un mur n'y vont pas : le mur reste à la racine.</summary>
    public static (int Moved, List<string> Refused) Move(ClientDb d, IEnumerable<Guid> screenIds, Guid? folderId)
    {
        if (!CanWrite(d)) return (0, new() { "Vous êtes en lecture seule sur les écrans : vous ne pouvez pas les ranger." });
        if (folderId is { } tid && Find(d, tid) == null) return (0, new() { "Dossier introuvable." });
        var moved = 0; var refused = new List<string>();
        var wanted = screenIds.ToHashSet();
        foreach (var s in d.Screens.Where(x => wanted.Contains(x.Id)).ToList())
        {
            if (s.FolderId == folderId) continue;
            if (AreaOps.WallOf(d, s) is { } wall) { refused.Add($"« {s.Name} » fait partie du mur « {wall.Name} » : le mur reste à la racine."); continue; }
            s.FolderId = folderId;
            moved++;
        }
        return (moved, refused);
    }
}
