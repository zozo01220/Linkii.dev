namespace Linkii.Poc;

/// <summary>Dossiers des compositions : ce que la personne voit et ce qu'elle peut faire (comme <see cref="PlaylistLibrary"/> pour les listes de lecture).</summary>
public class CompositionLibrary(TenantStore store, AppService apps)
{
    public List<CompositionFolderRow> Folders() => store.Read(CompositionFolderOps.Rows);

    public (string? Error, CompositionFolder? Folder) CreateFolder(string name, Guid? parentId) => store.Write(d => CompositionFolderOps.Create(d, name, parentId));

    public string? RenameFolder(Guid id, string name) => store.Write(d => CompositionFolderOps.Rename(d, id, name));

    public string? MoveFolder(Guid id, Guid? newParent) => store.Write(d => CompositionFolderOps.MoveFolder(d, id, newParent));
    public List<FolderTarget> MoveFolderTargets(Guid id) => store.Read(d => CompositionFolderOps.MoveTargets(d, id));
    public FolderContents ContentsOfFolder(Guid id) => store.Read(d => CompositionFolderOps.ContentsOf(d, id));

    /// <summary>Supprime un dossier, ses sous-dossiers et les compositions qu'ils contiennent (retirées des listes de lecture).</summary>
    public string? DeleteFolder(Guid id)
    {
        var plan = store.Write(d =>
        {
            var err = CompositionFolderOps.PlanDelete(d, id, out var folders, out var compositions);
            return (err, folders, compositions);
        });
        if (plan.err != null) return plan.err;
        foreach (var c in plan.compositions) apps.DeleteInstance(c);
        store.Write(d => CompositionFolderOps.Forget(d, plan.folders));
        return null;
    }

    /// <summary>Range des compositions dans un dossier, ou à la racine (null). Rend le nombre rangé et les refus.</summary>
    public (int Moved, List<string> Refused) MoveCompositions(IReadOnlyCollection<Guid> ids, Guid? folderId) => store.Write(d => CompositionFolderOps.Move(d, ids, folderId));

    /// <summary>Où ranger : la racine (si la personne peut y ranger) et les dossiers où elle écrit, avec leur chemin.</summary>
    public List<(Guid? Id, string Path)> MoveTargets() => store.Read(d =>
    {
        var all = CompositionFolderOps.All(d);
        var list = new List<(Guid? Id, string Path)>();
        if (CompositionFolderOps.CanCreateAtRoot(d)) list.Add((null, "Mes compositions (racine)"));
        foreach (var f in d.CompositionFolders)
        {
            if (FolderAccess.For(d.Access, f, all) < FolderRight.Write) continue;
            list.Add((f.Id, string.Join(" › ", FolderAccess.Chain(f, all).Reverse().Select(x => x.Name))));
        }
        return list.OrderBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    /// <summary>Range une composition venant d'être créée dans le dossier ouvert (silencieux si on ne peut pas y écrire).</summary>
    public void PlaceNew(Guid compositionId, Guid? folderId)
    {
        if (folderId == null) return;
        try { store.Write(d => CompositionFolderOps.PlaceNew(d, compositionId, folderId)); }
        catch (UnauthorizedAccessException) { }
    }
}
