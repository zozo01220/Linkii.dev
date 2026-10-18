namespace Linkii.Poc;

/// <summary>Partage entre aires de gestion, côté pages : médias, dossiers de médias, listes de lecture, dossiers de listes (voir <see cref="AreaShare"/>).</summary>
public class AreaSharing(TenantStore store)
{
    /// <summary>Peut-on partager cet élément de l'aire ouverte avec d'autres aires ?</summary>
    public bool CanShare(string kind, Guid id) => store.Read(d => AreaShareOps.CanShare(d, kind, id));

    /// <summary>Plusieurs aires accessibles : le partage a un sens.</summary>
    public bool HasOtherAreas() => store.Read(d => d.Area != null && d.Access.Areas.Count(a => a.Id != d.Area.Id) > 0);

    public List<AreaTarget> Targets(string kind, Guid id) => store.Read(d => AreaShareOps.Targets(d, kind, id));
    public List<AreaShareRow> RowsOf(string kind, Guid id) => store.Read(d => AreaShareOps.RowsOf(d, kind, id));

    /// <summary>Fixe les aires avec lesquelles l'élément est partagé (plusieurs éléments : ajoute seulement).</summary>
    public string? Set(string kind, IReadOnlyCollection<Guid> ids, IReadOnlyCollection<Guid> areas, bool onlyAdd) => store.Write(d => AreaShareOps.SetTargets(d, kind, ids, areas, onlyAdd));
    public string? Unshare(string kind, Guid id, Guid toArea) => store.Write(d => AreaShareOps.Unshare(d, kind, id, toArea));
    public string? Leave(string kind, Guid id) => store.Write(d => AreaShareOps.Leave(d, kind, id));

    /// <summary>Éléments d'autres aires que l'aire ouverte reçoit, d'un type : identifiant → nom de l'aire d'origine (pastille « lien » des cartes).</summary>
    public Dictionary<Guid, string> Origins(string kind) => store.Read(d =>
    {
        var cur = d.Area?.Id;
        return d.AreaShares.Where(s => s.ClientId == d.ClientId && s.Kind == kind && s.ToAreaId == cur)
            .GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => d.Root.Areas.FirstOrDefault(a => a.Id == g.First().FromAreaId)?.Name ?? "Autre aire");
    });

    /// <summary>Éléments partagés vus par l'aire ouverte, par type : tout ce qui vient d'une autre aire (dossiers et listes compris), avec le nom de son aire.</summary>
    public Dictionary<Guid, string> ForeignNames() => store.Read(d =>
    {
        var names = d.Root.Areas.Where(a => a.ClientId == d.ClientId).ToDictionary(a => a.Id, a => a.Name);
        var map = new Dictionary<Guid, string>();
        foreach (var f in d.MediaFolders.Where(f => d.Access.Shared.MediaFolders.Contains(f.Id))) map[f.Id] = names.GetValueOrDefault(f.AreaId, "Autre aire");
        foreach (var f in d.PlaylistFolders.Where(f => d.Access.Shared.PlaylistFolders.Contains(f.Id))) map[f.Id] = names.GetValueOrDefault(f.AreaId, "Autre aire");
        foreach (var p in d.Playlists.Where(p => d.Access.Shared.Playlists.Contains(p.Id))) map[p.Id] = names.GetValueOrDefault(p.AreaId, "Autre aire");
        return map;
    });
}
