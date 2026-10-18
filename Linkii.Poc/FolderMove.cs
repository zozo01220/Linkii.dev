namespace Linkii.Poc;

/// <summary>Une destination possible pour un dossier déplacé : la racine (Id null) ou un dossier, avec son chemin ; <paramref name="Reason"/> dit pourquoi elle est impossible.</summary>
public record FolderTarget(Guid? Id, string Path, int Depth, string? Reason);

/// <summary>Ce que contient un dossier : de quoi annoncer ce qui part à la racine quand on le dissout.</summary>
public record FolderContents(int Items, int Subfolders);

/// <summary>
/// Déplacer un dossier dans un autre dossier (médiathèque et listes de lecture). Le propriétaire déplace ; il faut pouvoir écrire dans le dossier d'arrivée,
/// qui est dans la même aire, ne contient pas déjà un dossier de ce nom, et ne fait pas dépasser <see cref="FolderAccess.MaxDepth"/> niveaux.
/// Déplacer à la racine <b>dissout</b> le dossier : ses fichiers (ou listes) et ses sous-dossiers vont à la racine de l'aire, le dossier est supprimé.
/// </summary>
public static class FolderMove
{
    /// <summary>Le dossier et tous ses descendants.</summary>
    public static HashSet<Guid> Subtree<T>(IReadOnlyCollection<T> all, Guid id) where T : class, IFolderNode
    {
        var tree = new HashSet<Guid>();
        var queue = new Queue<Guid>(new[] { id });
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!tree.Add(cur)) continue;
            foreach (var child in all.Where(x => x.ParentId == cur)) queue.Enqueue(child.Id);
        }
        return tree;
    }

    /// <summary>Nombre de niveaux du dossier et de ses descendants (1 : sans sous-dossier).</summary>
    public static int Height<T>(IReadOnlyCollection<T> all, Guid id, int guard = 0) where T : class, IFolderNode =>
        guard > FolderAccess.MaxDepth + 2 ? 1 : 1 + all.Where(x => x.ParentId == id).Select(x => Height(all, x.Id, guard + 1)).DefaultIfEmpty(0).Max();

    /// <summary>Message d'erreur si le dossier ne peut pas aller sous cette destination (null : possible).</summary>
    public static string? CheckTarget<T>(IReadOnlyCollection<T> all, T f, T? target, FolderRight targetRight) where T : class, IFolderNode
    {
        if (target == null) return null;
        if (target.Id == f.Id || Subtree(all, f.Id).Contains(target.Id)) return "dans lui-même";
        if (target.AreaId != f.AreaId) return "autre aire";
        if (targetRight < FolderRight.Write) return "lecture seule";
        if (all.Any(x => x.Id != f.Id && x.AreaId == f.AreaId && x.ParentId == target.Id && x.Name.Equals(f.Name, StringComparison.CurrentCultureIgnoreCase))) return "nom déjà pris";
        if (FolderAccess.Chain(target, all).Count() + Height(all, f.Id) > FolderAccess.MaxDepth) return $"{FolderAccess.MaxDepth} niveaux au plus";
        return null;
    }

    public static string Explain(string reason) => reason switch
    {
        "dans lui-même" => "Un dossier ne se range pas dans lui-même ni dans l'un de ses sous-dossiers.",
        "autre aire" => "Un dossier reste dans son aire de gestion.",
        "lecture seule" => "Vous ne pouvez pas écrire dans ce dossier.",
        "nom déjà pris" => "Un dossier porte déjà ce nom à cet endroit.",
        _ => $"Les dossiers ne s'imbriquent pas au-delà de {FolderAccess.MaxDepth} niveaux."
    };

    /// <summary>Un dossier à la racine d'un dossier du même nom ? (dissolution : ses sous-dossiers arrivent à la racine)</summary>
    public static string? RootNameClash<T>(IReadOnlyCollection<T> all, T f) where T : class, IFolderNode =>
        all.Where(x => x.ParentId == f.Id).FirstOrDefault(c => all.Any(r => r.Id != c.Id && r.Id != f.Id && r.AreaId == f.AreaId && r.ParentId == null && r.Name.Equals(c.Name, StringComparison.CurrentCultureIgnoreCase)))?.Name;
}
