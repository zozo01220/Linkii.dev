using System.Globalization;
using System.Text;

namespace Linkii.Poc;

/// <summary>Pièce dessinée sur la grille d'un étage (§195) : rectangle en mètres entiers, coin haut-gauche (X, Y).</summary>
public sealed record PlanRoom(string Id, string Name, int X, int Y, int W, int H)
{
    public bool Contains(double x, double y) => x >= X && x <= X + W && y >= Y && y <= Y + H;
}

/// <summary>
/// Segment de mur sur la grille : départ (X, Y), sens (D = 0 horizontal vers la droite, 90 vertical vers le bas), longueur en mètres.
/// S (ouvertures seulement) : côté de la pièce, +1 vers le bas / la droite, −1 vers le haut / la gauche ; donne le sens d'ouverture de la porte et la place de la pastille.
/// </summary>
public sealed record PlanSeg(int X, int Y, int D, int Len, int S = 1)
{
    public (double X, double Y) Mid => D == 0 ? (X + Len / 2.0, Y) : (X, Y + Len / 2.0);
}

/// <summary>
/// Géométrie des étages dessinés (§195) : murs montés autour des pièces, passages entre pièces voisines, ouvertures collées au mur le plus proche,
/// pièce d'un appareil et « Placer dans leur pièce ». Un mur est fait d'unités d'un mètre : « h » (horizontale en (a, b) → (a+1, b)) ou « v » ((a, b) → (a, b+1)).
/// </summary>
public static class PlanGeometry
{
    public const int DefaultW = 12, DefaultH = 8, MinSize = 3, MaxSize = 40, MaxRooms = 40, MaxGaps = 120;

    public readonly record struct Unit(bool H, int A, int B);

    /// <summary>Unités de mur d'un segment.</summary>
    public static IEnumerable<Unit> Units(PlanSeg s)
    {
        for (var i = 0; i < s.Len; i++) yield return s.D == 0 ? new Unit(true, s.X + i, s.Y) : new Unit(false, s.X, s.Y + i);
    }

    /// <summary>Unités du pourtour d'une pièce.</summary>
    public static IEnumerable<Unit> Edges(PlanRoom r)
    {
        for (var i = 0; i < r.W; i++) { yield return new(true, r.X + i, r.Y); yield return new(true, r.X + i, r.Y + r.H); }
        for (var j = 0; j < r.H; j++) { yield return new(false, r.X, r.Y + j); yield return new(false, r.X + r.W, r.Y + j); }
    }

    /// <summary>Toutes les unités de mur d'un étage (pourtours des pièces, mur commun compté une fois).</summary>
    public static HashSet<Unit> EdgeUnits(IEnumerable<PlanRoom> rooms) => rooms.SelectMany(Edges).ToHashSet();

    public static bool Overlap(PlanRoom a, PlanRoom b) => a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;

    /// <summary>Murs à monter : pourtours des pièces, moins les passages et les ouvertures (portes, fenêtres, volets), fusionnés en segments.</summary>
    public static List<PlanSeg> Walls(IEnumerable<PlanRoom> rooms, IEnumerable<PlanSeg> gaps, IEnumerable<PlanSeg> openings)
    {
        var set = EdgeUnits(rooms);
        foreach (var u in gaps.Concat(openings).SelectMany(Units)) set.Remove(u);
        return Merge(set);
    }

    /// <summary>Unités contiguës fusionnées en segments (de gauche à droite, de haut en bas).</summary>
    public static List<PlanSeg> Merge(IEnumerable<Unit> units)
    {
        var res = new List<PlanSeg>();
        foreach (var line in units.GroupBy(u => (u.H, u.H ? u.B : u.A)).OrderBy(g => g.Key.H ? 0 : 1).ThenBy(g => g.Key.Item2))
        {
            var pos = line.Select(u => u.H ? u.A : u.B).OrderBy(v => v).ToList();
            int start = pos[0], prev = pos[0];
            for (var i = 1; i <= pos.Count; i++)
            {
                if (i < pos.Count && pos[i] == prev + 1) { prev = pos[i]; continue; }
                res.Add(line.Key.H ? new PlanSeg(start, line.Key.Item2, 0, prev - start + 1) : new PlanSeg(line.Key.Item2, start, 90, prev - start + 1));
                if (i < pos.Count) start = prev = pos[i];
            }
        }
        return res;
    }

    /// <summary>Mur commun à deux pièces voisines (unités partagées), vide si elles ne se touchent pas.</summary>
    public static List<Unit> Shared(PlanRoom a, PlanRoom b)
    {
        var ea = Edges(a).ToHashSet();
        return Edges(b).Where(ea.Contains).ToList();
    }

    /// <summary>
    /// Passages proposés pour une pièce qu'on vient de dessiner ou de déplacer : un passage d'un mètre au milieu de chaque mur commun avec une pièce voisine,
    /// sauf s'il y en a déjà un sur ce mur.
    /// </summary>
    public static List<PlanSeg> AutoGaps(PlanRoom room, IEnumerable<PlanRoom> others, IEnumerable<PlanSeg> gaps)
    {
        var existing = gaps.SelectMany(Units).ToHashSet();
        var res = new List<PlanSeg>();
        foreach (var o in others.Where(o => o.Id != room.Id))
        {
            var shared = Shared(room, o);
            if (shared.Count == 0 || shared.Any(existing.Contains)) continue;
            foreach (var seg in Merge(shared))
            {
                var mid = seg.Len / 2;   // unité du milieu (la première des deux du milieu pour une longueur paire)
                if (seg.Len % 2 == 0 && mid > 0) mid--;
                res.Add(seg.D == 0 ? new PlanSeg(seg.X + mid, seg.Y, 0, 1) : new PlanSeg(seg.X, seg.Y + mid, 90, 1));
            }
        }
        return res;
    }

    /// <summary>Unité de mur la plus proche d'un point (mètres), à moins d'un mètre et demi ; null sinon.</summary>
    public static Unit? NearestEdge(IEnumerable<PlanRoom> rooms, double x, double y)
    {
        Unit? best = null; var bd = 1.5;
        foreach (var u in EdgeUnits(rooms))
        {
            var (mx, my) = u.H ? (u.A + .5, (double)u.B) : (u.A, u.B + .5);
            var dist = Math.Sqrt((mx - x) * (mx - x) + (my - y) * (my - y));
            if (dist < bd) { bd = dist; best = u; }
        }
        return best;
    }

    /// <summary>
    /// Ouverture collée au mur libre le plus proche d'un point (mètres) : longueur voulue si elle tient, sinon un mètre. Les murs pris par un passage
    /// ou une autre ouverture sont écartés. Le côté (S) est celui du point, ramené vers une pièce s'il tombe dehors.
    /// </summary>
    public static PlanSeg? Snap(IReadOnlyList<PlanRoom> rooms, IEnumerable<PlanSeg> taken, double x, double y, int len)
    {
        var free = EdgeUnits(rooms);
        foreach (var u in taken.SelectMany(Units)) free.Remove(u);
        foreach (var l in len > 1 ? new[] { len, 1 } : new[] { 1 })
        {
            PlanSeg? best = null; var bd = double.MaxValue;
            foreach (var u in free)
            {
                var s = u.H ? new PlanSeg(u.A, u.B, 0, l) : new PlanSeg(u.A, u.B, 90, l);
                if (!Units(s).All(free.Contains)) continue;
                var (mx, my) = s.Mid;
                var dist = (mx - x) * (mx - x) + (my - y) * (my - y);
                if (dist < bd) { bd = dist; best = s; }
            }
            if (best == null) continue;
            var (bx, by) = best.Mid;
            var side = best.D == 0 ? (y >= by ? 1 : -1) : (x >= bx ? 1 : -1);
            if (!InRoom(rooms, best, side) && InRoom(rooms, best, -side)) side = -side;
            return best with { S = side };
        }
        return null;
    }

    private static bool InRoom(IEnumerable<PlanRoom> rooms, PlanSeg s, int side)
    {
        var (px, py) = Pin(s, side, .3);
        return rooms.Any(r => r.Contains(px, py) && !(px == r.X || px == r.X + r.W || py == r.Y || py == r.Y + r.H));
    }

    /// <summary>Place de la pastille d'une ouverture : milieu du mur, décalé vers la pièce.</summary>
    public static (double X, double Y) Pin(PlanSeg s, int side, double off = .45)
    {
        var (mx, my) = s.Mid;
        return s.D == 0 ? (mx, my + side * off) : (mx + side * off, my);
    }

    /// <summary>Pièce qui contient un point (mètres), la plus petite si plusieurs bords se touchent.</summary>
    public static PlanRoom? RoomAt(IEnumerable<PlanRoom> rooms, double x, double y) =>
        rooms.Where(r => r.Contains(x, y)).OrderBy(r => r.W * r.H).FirstOrDefault();

    /// <summary>Ouverture qu'un appareil dessine sur un mur : door, window ou cover (fenêtre avec volet) ; null s'il n'en est pas une.</summary>
    public static string? OpeningKind(string domain, string? deviceClass) => domain switch
    {
        HomeAssistantRules.Cover => deviceClass is "door" or "garage" or "gate" ? "door" : "cover",
        HomeAssistantRules.BinarySensor => deviceClass switch { "window" => "window", "door" or "garage_door" or "opening" => "door", _ => null },
        _ => null,
    };

    /// <summary>Largeur proposée d'une ouverture : deux mètres pour un volet ou une porte de garage, un mètre sinon.</summary>
    public static int OpeningLen(string domain, string? deviceClass) =>
        (domain == HomeAssistantRules.Cover && deviceClass is not ("door" or "gate")) || deviceClass is "garage_door" or "garage" ? 2 : 1;

    /// <summary>
    /// Pièce d'un appareil d'après son nom ou son identifiant (« light.chambre_1 », « Lampe salon ») : la pièce dont le nom y figure en mots entiers,
    /// la plus longue si plusieurs conviennent. Les accents et la casse ne comptent pas.
    /// </summary>
    public static int MatchRoom(string entityId, string entityName, IReadOnlyList<string> roomNames)
    {
        var hay = " " + Norm(entityName) + " " + Norm(entityId.Contains('.') ? entityId[(entityId.IndexOf('.') + 1)..] : entityId) + " ";
        int best = -1, len = 0;
        for (var i = 0; i < roomNames.Count; i++)
        {
            var n = Norm(roomNames[i]);
            if (n.Length > 1 && n.Length > len && hay.Contains(" " + n + " ")) { best = i; len = n.Length; }
        }
        return best;
    }

    public static string Norm(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
