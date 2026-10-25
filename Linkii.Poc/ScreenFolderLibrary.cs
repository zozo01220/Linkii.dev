namespace Linkii.Poc;

/// <summary>Dossiers de rangement des écrans : ce que la personne voit et ce qu'elle peut faire (voir <see cref="ScreenFolderOps"/>).</summary>
public class ScreenFolderLibrary(TenantStore store)
{
    public List<ScreenFolderRow> Folders() => store.Read(ScreenFolderOps.Rows);

    public (string? Error, ScreenFolder? Folder) CreateFolder(string name, Guid? parentId) => store.Write(d => ScreenFolderOps.Create(d, name, parentId));
    public string? RenameFolder(Guid id, string name) => store.Write(d => ScreenFolderOps.Rename(d, id, name));
    public string? DeleteFolder(Guid id) => store.Write(d => ScreenFolderOps.Delete(d, id));
    public string? MoveFolder(Guid id, Guid? newParent) => store.Write(d => ScreenFolderOps.MoveFolder(d, id, newParent));
    public List<FolderTarget> MoveFolderTargets(Guid id) => store.Read(d => ScreenFolderOps.MoveTargets(d, id));
    public FolderContents ContentsOfFolder(Guid id) => store.Read(d => ScreenFolderOps.ContentsOf(d, id));

    public (int Moved, List<string> Refused) MoveScreens(IReadOnlyCollection<Guid> ids, Guid? folderId) => store.Write(d => ScreenFolderOps.Move(d, ids, folderId));

    /// <summary>Où ranger : la racine et les dossiers de l'aire, avec leur chemin.</summary>
    public List<(Guid? Id, string Path)> MoveTargets() => store.Read(d =>
    {
        var all = d.ScreenFolders.ToList();
        var list = new List<(Guid? Id, string Path)>();
        foreach (var f in all) list.Add((f.Id, string.Join(" › ", FolderAccess.Chain(f, all).Reverse().Select(x => x.Name))));
        list = list.OrderBy(x => x.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
        list.Insert(0, (null, "Racine des écrans"));
        return list;
    });
}
