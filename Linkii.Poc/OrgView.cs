namespace Linkii.Poc;

/// <summary>Lieux choisis dans le sélecteur de la page Organisation : toute l'organisation, ou des aires entières et/ou des zones.</summary>
public class PlaceSel
{
    public bool All { get; set; }
    public HashSet<Guid> Areas { get; set; } = new();
    public HashSet<Guid> Zones { get; set; } = new();
    public bool Any => All || Areas.Count + Zones.Count > 0;
    public PlaceSel Copy() => new() { All = All, Areas = [.. Areas], Zones = [.. Zones] };
}

/// <summary>Filtres communs aux vues Lieux et Personnes de la page Organisation (maquette organisation-echelle.html).</summary>
public class OrgFilter
{
    public string Q { get; set; } = "";
    public HashSet<Guid> Areas { get; set; } = new();
    public HashSet<string> Roles { get; set; } = new();
    /// <summary>« Actif », « Invité », « Désactivé », ou vide : tous.</summary>
    public string Status { get; set; } = "";
    /// <summary>Lieux : aires sans membre. Personnes : membres sans aucun accès.</summary>
    public bool Flag { get; set; }
    public int Count => (Q.Length > 0 ? 1 : 0) + (Areas.Count > 0 ? 1 : 0) + (Roles.Count > 0 ? 1 : 0) + (Status.Length > 0 ? 1 : 0) + (Flag ? 1 : 0);
    public void Clear() { Q = ""; Areas.Clear(); Roles.Clear(); Status = ""; Flag = false; }
}

/// <summary>Qui a accès à quoi, filtré : le calcul de la page Organisation, sans interface (testable).</summary>
public class OrgView(IReadOnlyList<Area> areas, IReadOnlyList<Screen> screens, IReadOnlyList<User> people, IReadOnlyList<AccessLevel> levels)
{
    public const string AdminRole = LevelKinds.AdminName;
    public IReadOnlyList<Area> Areas => areas;
    public IReadOnlyList<User> People => people;

    public static bool IsAdmin(User u) => u.Role == Roles.ClientAdmin;
    public static string StatusOf(User u) => u.Disabled ? "Désactivé" : u.IsInvited ? "Invité" : "Actif";

    /// <summary>Accès d'un membre avec son rôle (ceux dont le rôle a disparu sont ignorés).</summary>
    public List<(MemberAccess Access, AccessLevel Level)> AccessesOf(User u) => IsAdmin(u) ? new()
        : u.AccessList(levels).Select(a => (Access: a, Level: levels.FirstOrDefault(l => l.Id == a.LevelId)!)).Where(x => x.Level != null).ToList();

    /// <summary>Noms des rôles d'une personne (« Administrateur » pour un administrateur de l'organisation).</summary>
    public List<string> RolesOf(User u) => IsAdmin(u) ? [AdminRole] : AccessesOf(u).Select(a => a.Level.Name).Distinct().ToList();

    public bool Reaches(User u, Area a) => IsAdmin(u) || AccessesOf(u).Any(x => AccessLevels.Reaches(x.Access.Perimeter, a, screens));

    public bool ReachesZone(User u, Area a, Zone z) => IsAdmin(u) || AccessesOf(u).Any(x =>
        x.Access.Perimeter.AllAreas || x.Access.Perimeter.AreaIds.Contains(a.Id) || x.Access.Perimeter.ZoneIds.Contains(z.Id)
        || screens.Any(s => s.ZoneId == z.Id && x.Access.Perimeter.ScreenIds.Contains(s.Id)));

    public List<User> Admins => people.Where(IsAdmin).ToList();

    /// <summary>Membres (hors administrateurs) qui ont accès à l'aire.</summary>
    public List<User> MembersOf(Area a) => people.Where(u => !IsAdmin(u) && Reaches(u, a)).ToList();

    public List<User> MembersOf(Area a, Zone z) => people.Where(u => !IsAdmin(u) && ReachesZone(u, a, z)).ToList();

    public List<User> FilterPeople(OrgFilter f, string sort = "name", bool desc = false)
    {
        var q = f.Q.Trim();
        var r = people.Where(u =>
        {
            if (q.Length > 0 && !(u.Label.Contains(q, StringComparison.CurrentCultureIgnoreCase) || u.Email.Contains(q, StringComparison.CurrentCultureIgnoreCase))) return false;
            if (f.Roles.Count > 0 && !RolesOf(u).Any(f.Roles.Contains)) return false;
            if (f.Areas.Count > 0 && !areas.Any(a => f.Areas.Contains(a.Id) && Reaches(u, a))) return false;
            if (f.Status.Length > 0 && StatusOf(u) != f.Status) return false;
            if (f.Flag && (IsAdmin(u) || AccessesOf(u).Count > 0)) return false;
            return true;
        });
        Func<User, string> key = sort switch
        {
            "role" => u => RolesOf(u).FirstOrDefault() ?? "ZZZ",
            "status" => StatusOf,
            _ => u => u.Label
        };
        var cmp = StringComparer.CurrentCultureIgnoreCase;
        return (desc ? r.OrderByDescending(key, cmp) : r.OrderBy(key, cmp)).ThenBy(u => u.Label, cmp).ToList();
    }

    public List<Area> FilterAreas(OrgFilter f)
    {
        var q = f.Q.Trim();
        return areas.Where(a =>
        {
            if (f.Areas.Count > 0 && !f.Areas.Contains(a.Id)) return false;
            if (q.Length > 0 && !(a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || a.Zones.Any(z => z.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)))) return false;
            var members = MembersOf(a);
            if (f.Roles.Count > 0 && !f.Roles.Any(r => r == AdminRole ? Admins.Count > 0 : members.Any(u => AccessesOf(u).Any(x => x.Level.Name == r && AccessLevels.Reaches(x.Access.Perimeter, a, screens))))) return false;
            if (f.Status.Length > 0 && !members.Any(u => StatusOf(u) == f.Status)) return false;
            if (f.Flag && members.Count > 0) return false;
            return true;
        }).ToList();
    }

    /// <summary>Vue ouverte au départ : les Lieux quand il y a peu d'aires (3 au plus), les Personnes sinon.</summary>
    public static string DefaultView(int areaCount) => areaCount <= 3 ? "places" : "people";
}
