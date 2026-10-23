namespace Linkii.Poc;

/// <summary>
/// Présentation de la médiathèque (§103) : un fichier de plusieurs pages (PowerPoint, Google Slides, Google Docs, Document PDF, design Canva) converti
/// une seule fois en images, quel que soit le nombre de contenus qui l'affichent. Elle est un élément de la médiathèque (<see cref="MediaItem"/> de type
/// « presentation », rangé comme un média : aire, dossier, partage) ; ses images de pages sont des médias cachés de la grille.
/// <para>Info de la présentation : src (ppt, gslides, gdocs, pdf, canva), key (fichier d'origine, pour la reconnaître à l'import), pdfId (PDF gardé sur le
/// serveur), pages (nombre de pages du document), mod (date de modification du fichier converti), synced (dernière conversion), file (nom du fichier d'origine),
/// gen (génération : elle change quand le fichier d'origine a changé et que les images sont refaites).</para>
/// <para>Info d'une image de page : pres (identifiant de la présentation), var (rendu : « none » ou « white-landscape » / « blur-portrait »…),
/// pg (numéro de page à partir de 1), gen.</para>
/// </summary>
public static class Pres
{
    public const string Type = "presentation";

    public static bool Is(MediaItem m) => m.Type == Type && m.AppId == null;

    /// <summary>Image d'une page de présentation (cachée de la grille de la médiathèque).</summary>
    public static bool IsPage(MediaItem m) => m.AppId == null && m.Type == "image" && m.Info.ContainsKey("pres");

    public static string? IdOf(MediaItem m) => m.Info.GetValueOrDefault("pres");

    /// <summary>Rendu des images : seul le fond et le format changent l'image (les pages à afficher se choisissent dans le contenu).</summary>
    public static string Variant(PdfService.Options o) => o.Background is "white" or "blur" ? $"{o.Background}-{(o.Canvas == "portrait" ? "portrait" : "landscape")}" : "none";

    public static PdfService.Options OptionsOf(string variant, string unit, string src)
    {
        var p = variant.Split('-');
        return p.Length == 2 ? new("", p[0], p[1], unit, src) : new("", "none", "landscape", unit, src);
    }

    /// <summary>Pastille de source dans l'aperçu de la carte.</summary>
    public static string Label(string? src) => src switch { "ppt" => "PPT", "gslides" => "GSlides", "gdocs" => "GDocs", "pdf" => "PDF", "canva" => "Canva", _ => "" };

    public static string Unit(string? src) => src is "ppt" or "gslides" ? "diapositive" : "page";

    public static string UnitPlural(string? src, int n) => Unit(src) + (n > 1 ? "s" : "");

    /// <summary>Source de la présentation d'après l'app qui l'a produite.</summary>
    public static string SrcOf(string? appId) => appId switch { "ms-powerpoint" => "ppt", "google-slides" => "gslides", "google-docs" => "gdocs", "canva" => "canva", _ => "pdf" };

    /// <summary>Présentation qui garde ce PDF sur le serveur (le PDF ne se supprime alors qu'avec elle).</summary>
    public static bool Owns(Db db, string? pdfId) => !string.IsNullOrEmpty(pdfId) && db.Media.Any(m => Is(m) && m.Info.GetValueOrDefault("pdfId") == pdfId);

    public static int Pages(MediaItem pres) => int.TryParse(pres.Info.GetValueOrDefault("pages"), out var n) ? n : 0;

    public static int PageOf(MediaItem page) => int.TryParse(page.Info.GetValueOrDefault("pg"), out var n) ? n : 0;

    /// <summary>Contenus (apps) qui affichent cette présentation : ceux dont les pages sont ses images, ou qui gardent son PDF.</summary>
    public static List<MediaItem> ContentsOf(IEnumerable<MediaItem> media, MediaItem pres)
    {
        var list = media.ToList();
        var pdfId = pres.Info.GetValueOrDefault("pdfId") ?? "";
        var id = pres.Id.ToString();
        var pages = list.Where(m => IdOf(m) == id).Select(m => m.Id).ToHashSet();
        return list.Where(m => m.AppId != null && m.ClientId == pres.ClientId
            && (pdfId.Length > 0 && m.Settings.GetValueOrDefault("pdf") == pdfId
                || m.Settings.GetValueOrDefault("pages") is { Length: > 0 } ids && AppField.MediaIds(ids).Any(pages.Contains))).ToList();
    }

    /// <summary>Images d'une ancienne génération de la présentation que rien n'utilise plus (ni contenu, ni écran) : retirées, fichiers compris.</summary>
    public static void PurgeStale(JsonStore store, Guid clientId, Guid presId)
    {
        var removed = store.Write(d =>
        {
            var pres = d.Media.FirstOrDefault(m => m.Id == presId && Is(m));
            if (pres == null) return new List<MediaItem>();
            var gen = pres.Info.GetValueOrDefault("gen") ?? "1";
            var unused = MediaLibrary.UnusedExports(d.Media.Where(m => m.ClientId == clientId), d.Playlists.Where(p => p.ClientId == clientId), d.Screens.Where(s => s.ClientId == clientId), "pres")
                .Where(m => IdOf(m) == presId.ToString() && m.Info.GetValueOrDefault("gen") != gen).ToList();
            var ids = unused.Select(m => m.Id).ToHashSet();
            d.Media.RemoveAll(m => ids.Contains(m.Id));
            return unused;
        });
        PdfService.DeleteImageFiles(removed);
    }
}
