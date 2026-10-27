using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Forms;

namespace Linkii.Poc;

/// <summary>
/// Documents envoyés depuis l'ordinateur (bouton « Importer » et dépôt de fichiers de la médiathèque) : PDF, PowerPoint, Word, Excel, OpenDocument, RTF, CSV.
/// Un document Office est converti en PDF par LibreOffice (<see cref="OfficeConverter"/>), puis, comme tout PDF, en images : une présentation de la médiathèque
/// (<see cref="Pres"/>) rangée dans « intégration_médias » (PowerPoint_médias, Word_médias, Excel_médias, PDF_médias). Le même fichier envoyé deux fois
/// est reconnu par son empreinte et n'est converti qu'une fois ; un document d'une seule page devient une image ordinaire.
/// </summary>
public class LocalDocuments(PdfService pdf, OfficeConverter office, JsonStore store)
{
    private static readonly string[] PowerPoint = { ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".pot", ".potx", ".odp" };
    private static readonly string[] Word = { ".doc", ".docx", ".docm", ".dot", ".dotx", ".odt", ".rtf" };
    private static readonly string[] Excel = { ".xls", ".xlsx", ".xlsm", ".ods", ".csv" };

    public static readonly string[] Extensions = { ".pdf" };

    /// <summary>Toutes les extensions de documents acceptées (attribut accept du champ de fichiers).</summary>
    public static IEnumerable<string> AllExtensions => Extensions.Concat(PowerPoint).Concat(Word).Concat(Excel);

    public static bool IsDocument(string fileName) => AllExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>Source de la présentation (voir <see cref="Pres"/>) d'après l'extension : ppt, doc, xls ou pdf.</summary>
    public static string SrcOf(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return PowerPoint.Contains(ext) ? "ppt" : Word.Contains(ext) ? "doc" : Excel.Contains(ext) ? "xls" : "pdf";
    }

    // ---------- Adapté aux intégrations installées ----------

    /// <summary>
    /// Intégrations qui savent afficher ce type de document : PowerPoint (ppt, pptx, odp…) ; Document PDF pour les PDF, Word et Excel (leurs pages sont des images).
    /// Un type de document n'est accepté à l'import que si l'une d'elles est installée.
    /// </summary>
    public static string[] AppsFor(string src) => src == "ppt" ? new[] { "ms-powerpoint" } : new[] { "pdf" };

    public static string AppName(string appId) => appId == "ms-powerpoint" ? "Microsoft PowerPoint" : "Document PDF";

    public static bool Allowed(string fileName, Func<string, bool> installed) => IsDocument(fileName) && AppsFor(SrcOf(fileName)).Any(installed);

    /// <summary>Extensions de documents acceptées pour les intégrations installées (champ de fichiers, glisser-déposer).</summary>
    public static List<string> AllowedExtensions(Func<string, bool> installed) => AllExtensions.Where(e => Allowed("x" + e, installed)).ToList();

    /// <summary>Formats acceptés, en clair (« PowerPoint · PDF, Word, Excel »), selon les intégrations installées.</summary>
    public static string FormatsText(Func<string, bool> installed)
    {
        var parts = new List<string>();
        if (installed("ms-powerpoint")) parts.Add("PowerPoint");
        if (installed("pdf")) parts.AddRange(new[] { "PDF", "Word", "Excel" });
        return string.Join(", ", parts);
    }

    /// <summary>Message d'un document dont aucune intégration n'est installée.</summary>
    public static string NotInstalled(string fileName) =>
        $"Activez l'intégration « {AppName(AppsFor(SrcOf(fileName))[0])} » (page Intégrations) pour importer ce type de document.";

    /// <param name="Item">Ce qui entre dans la médiathèque : la présentation, ou l'image d'un document d'une seule page.</param>
    public record Imported(MediaItem Item, PdfService.Source Source, PdfService.ConvertResult Result, string Src, bool Reused);

    public async Task<(Imported? Done, string? Error)> Import(IBrowserFile file, Guid clientId, Guid? areaId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!IsDocument(file.Name)) return (null, "Format de document non pris en charge.");
        if (file.Size > PdfService.MaxBytes) return (null, $"« {file.Name} » dépasse la taille maximale ({PdfService.MaxBytes / 1_000_000} Mo).");
        await using var input = file.OpenReadStream(PdfService.MaxBytes, ct);
        return await Import(input, file.Name, clientId, areaId, progress, ct);
    }

    public async Task<(Imported? Done, string? Error)> Import(Stream input, string fileName, Guid clientId, Guid? areaId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var src = SrcOf(fileName);
        var title = Path.GetFileNameWithoutExtension(fileName);
        try
        {
            PdfService.Source source;
            bool reused;
            string key;
            if (src == "pdf")
            {
                progress?.Report("Lecture du PDF…");
                var saved = await pdf.SaveStream(clientId, input, title, null, ct);
                if (saved.Source is not { } sent) return (null, saved.Error ?? "PDF refusé.");
                source = pdf.ReuseUploaded(clientId, areaId, sent, out reused);
                key = "sha:" + source.Hash;
            }
            else
            {
                // document Office : reconnu par l'empreinte du fichier d'origine (le PDF converti change à chaque conversion)
                progress?.Report("Lecture du document…");
                using var buffer = new MemoryStream();
                await input.CopyToAsync(buffer, ct);
                var hash = Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
                key = "file:" + hash;
                if (pdf.FindPresentation(clientId, areaId, key) is { } known && known.Info.GetValueOrDefault("pdfId") is { Length: > 0 } knownPdf)
                {
                    source = new PdfService.Source(knownPdf, known.Name, Pres.Pages(known), hash);
                    reused = true;
                }
                else
                {
                    if (!office.Available) return (null, OfficeConverter.Missing);
                    progress?.Report("Conversion du document en PDF…");
                    buffer.Position = 0;
                    var (converted, error) = await office.ToPdf(buffer, fileName, ct);
                    if (converted == null) return (null, error);
                    await using (converted)
                    {
                        var saved = await pdf.SaveStream(clientId, converted, title, null, ct);
                        if (saved.Source is not { } made) return (null, saved.Error ?? "Le document converti n'a pas pu être lu.");
                        source = made with { Hash = hash };
                    }
                    reused = false;
                }
            }

            var options = new PdfService.Options("", "none", "landscape", Pres.Unit(src), src);
            var res = await pdf.Convert(clientId, areaId, source, options, progress, ct, new PdfService.Meta(key, null, fileName));
            if (res.Error != null) { if (!reused) pdf.Release(source.Id); return (null, res.Error); }

            if (res.Presentation is { } pres) return (new(pres, source, res, src, reused), null);

            // une seule page : une image ordinaire, rangée elle aussi dans « intégration_médias » ; le PDF d'origine n'a plus d'usage
            var image = store.Write(d =>
            {
                var folder = Pres.FolderFor(d, clientId, areaId ?? Guid.Empty, src);
                var live = d.Media.First(m => m.Id == res.Files[0].Id);
                live.FolderId = folder;
                return live;
            });
            pdf.Release(source.Id);
            return (new(image, source, res, src, reused), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return (null, "Le fichier n'a pas pu être lu. Réessayez."); }
    }
}
