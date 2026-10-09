using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Linkii.Poc;

/// <summary>
/// Ce que montre le simulateur : un écran (tel qu'il est publié), un mur entier, ou une liste de lecture dans son état actuel
/// sur un format choisi (Cols × Rows écrans de ce format : plus d'un, c'est un mur).
/// </summary>
public record PreviewGrant(Guid ClientId, Guid? ScreenId, Guid? WallZoneId, Guid? PlaylistId, string Orientation, string Resolution, int Cols, int Rows);

/// <summary>
/// Jetons d'aperçu du simulateur : créés par le back-office pour une personne qui a accès à l'écran ou à la liste,
/// en lecture seule, gardés en mémoire et valables 1 h après leur dernière utilisation.
/// Un aperçu ne compte aucune diffusion, ne marque pas l'écran en ligne et ne reçoit aucun ordre envoyé aux écrans.
/// </summary>
public class PreviewGrants
{
    private static readonly TimeSpan Life = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, (PreviewGrant Grant, DateTime Until)> grants = new();

    public string Create(PreviewGrant grant)
    {
        var now = DateTime.UtcNow;
        foreach (var (k, v) in grants) if (v.Until < now) grants.TryRemove(k, out _);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');
        grants[token] = (grant, now + Life);
        return token;
    }

    public PreviewGrant? Find(string? token)
    {
        if (string.IsNullOrEmpty(token) || !grants.TryGetValue(token, out var g)) return null;
        if (g.Until < DateTime.UtcNow) { grants.TryRemove(token, out _); return null; }
        grants[token] = (g.Grant, DateTime.UtcNow + Life);
        return g.Grant;
    }
}

/// <summary>Ce que reçoit le player : un écran appairé, ou le simulateur du back-office (mêmes données, mêmes rendus).</summary>
public static class PlayerFeed
{
    /// <summary>Contenus publiés sur l'écran, toutes zones (repli : ancien instantané de sa liste s'il n'a jamais été publié).</summary>
    public static List<PublishedItem> AllItems(Db db, Screen s) =>
        s.PublishedVersion > 0 ? s.Published.Concat(s.PublishedZones.SelectMany(z => z)).ToList()
        : db.Playlists.FirstOrDefault(p => p.Id == s.PlaylistId && p.ClientId == s.ClientId)?.Published ?? new();

    /// <summary>Configuration et contenus publiés d'un écran. whole : le simulateur montre tout le mur dont l'écran fait partie.</summary>
    public static Dictionary<string, object?> ForScreen(Db db, Screen s, bool whole = false)
    {
        var t = db.Clients.First(c => c.Id == s.ClientId);
        var reseller = db.Resellers.FirstOrDefault(r => r.Id == t.ResellerId);
        var p = db.Playlists.FirstOrDefault(x => x.Id == s.PlaylistId && x.ClientId == s.ClientId);
        // zone synchronisée : origine commune de la boucle ; les durées « jusqu'à la fin » sont fixées pour que tous les écrans bouclent pareil
        var epoch = Helpers.SyncEpoch(db.Areas, s);
        var clientMedia = epoch == null ? null : db.Media.Where(m => m.ClientId == s.ClientId).ToList();
        // mur d'écrans : pas de widgets d'écran (position libre), même publiés avant l'entrée dans le mur
        var wall = Helpers.Wall(db.Areas, s);
        List<PublishedItem> Sync(List<PublishedItem> l)
        {
            if (wall != null) l = l.Where(i => i.Placement != "free").ToList();
            return clientMedia == null ? l : Helpers.FixedForSync(l, clientMedia);
        }
        var lay = ScreenLayouts.Find(s.PublishedVersion > 0 ? s.PublishedLayout : null);
        return new()
        {
            ["version"] = Helpers.Revision(p, s, t, reseller, Helpers.SyncStamp(db.Areas, s)) + (whole ? "|whole" : ""),
            ["serverNow"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["sync"] = epoch is { } e ? new { epoch = Helpers.EpochMs(e) } : null,
            // mur d'écrans : l'écran n'affiche que sa portion (colonne, ligne) de l'image de tout le mur
            ["wall"] = wall is { } w ? new { cols = w.Cols, rows = w.Rows, col = w.Col, row = w.Row, whole } : null,
            ["items"] = Sync(s.PublishedVersion > 0 ? s.Published : p?.Published ?? new List<PublishedItem>()).Select(i => i.ForPlayer()),   // repli : écran jamais publié depuis le passage à la publication par écran
            // découpage publié : tailles des zones (en %), côte à côte en paysage, empilées en portrait ; zones 2 et 3 avec leur propre liste
            ["layout"] = new { id = lay.Id, dir = s.Orientation == "portrait" ? "col" : "row", sizes = lay.Sizes },
            ["zones"] = (s.PublishedVersion > 0 ? s.PublishedZones : new List<List<PublishedItem>>()).Select(z => Sync(z).Select(i => i.ForPlayer())),
            ["screen"] = new { orientation = s.Orientation, resolution = s.Resolution },
            ["settings"] = new { timezone = t.Timezone, reloadHour = t.ReloadHour },
            ["brand"] = BrandDto.From(reseller)
        };
    }

    /// <summary>Simulateur d'une liste de lecture : son contenu actuel (même non publié), sur un écran ou un mur du format choisi.</summary>
    public static Dictionary<string, object?> ForPlaylist(Db db, Playlist p, List<PublishedItem> items, PreviewGrant g)
    {
        var t = db.Clients.First(c => c.Id == p.ClientId);
        var reseller = db.Resellers.FirstOrDefault(r => r.Id == t.ResellerId);
        var wall = g.Cols * g.Rows > 1;
        if (wall) items = items.Where(i => i.Placement != "free").ToList();
        return new()
        {
            ["version"] = $"d{p.Id:N}:{p.Revision}|{g.Orientation}|{g.Resolution}|{g.Cols}x{g.Rows}|{t.Timezone}|{reseller?.BrandStamp}",
            ["serverNow"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["sync"] = null,
            ["wall"] = wall ? new { cols = g.Cols, rows = g.Rows, col = 0, row = 0, whole = true } : null,
            ["items"] = items.Select(i => i.ForPlayer()),
            ["layout"] = new { id = ScreenLayouts.Full, dir = g.Orientation == "portrait" ? "col" : "row", sizes = ScreenLayouts.Find(ScreenLayouts.Full).Sizes },
            ["zones"] = Array.Empty<object>(),
            ["screen"] = new { orientation = g.Orientation, resolution = g.Resolution },
            ["settings"] = new { timezone = t.Timezone, reloadHour = t.ReloadHour },
            ["brand"] = BrandDto.From(reseller)
        };
    }

    /// <summary>Nom et couleur de chaque contenu, pour la frise du simulateur (l'écran, lui, n'en a pas besoin).</summary>
    public static Dictionary<string, object> Meta(Db db, IEnumerable<PublishedItem> items, AppCatalog catalog) =>
        items.GroupBy(i => i.Id).Select(g => g.First()).ToDictionary(i => i.Id.ToString(), i => (object)new
        {
            name = db.Media.FirstOrDefault(m => m.Id == i.MediaId)?.Name ?? catalog.Find(i.AppId)?.Name ?? "Contenu",
            color = Components.AppIcon.LookOf(i.AppId ?? i.Type).Color
        });

    /// <summary>Résolution d'un écran pour le simulateur : « Auto » prend la taille constatée de l'appareil, sinon Full HD.
    /// resolution : réglage essayé à la place de celui de l'écran (réglages du mur pas encore appliqués).</summary>
    public static string Resolution(Screen s, string? resolution = null) =>
        (resolution ?? s.Resolution) is var r && r != "auto" ? r
        : s.DetectedW is int w and > 0 && s.DetectedH is int h and > 0 ? $"{Math.Max(w, h)}x{Math.Min(w, h)}" : "1920x1080";
}
