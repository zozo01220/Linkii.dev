using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.AspNetCore.Components.Forms;
using SkiaSharp;

namespace Linkii.Poc;

/// <summary>
/// App « Document PDF » : le PDF est gardé sur le serveur (dossier pdf/) puis converti en images 1080p (une page = une image JPEG de la médiathèque),
/// lues comme un diaporama et donc disponibles hors ligne. Le rendu est fait par PDFium (Docnet.Core), sans outil externe.
/// Les pages à afficher et le fond autour de la page sont appliqués à la conversion : les changer reconvertit le PDF gardé.
/// </summary>
public class PdfService(JsonStore store, ILogger<PdfService> log)
{
    public const int MaxPages = 50;
    public const long MaxBytes = 100_000_000;
    private const int LongSide = 1920, ShortSide = 1080;

    /// <summary>Réglages qui changent les images produites (autres réglages : lus par le player, sans reconversion).</summary>
    public static readonly string[] RenderKeys = { "range", "background", "canvas" };

    public record Source(string Id, string Title, int Pages);
    public record SourceResult(Source? Source, string? Error);
    /// <param name="Unit">Nom d'une page dans le nom des images (« page », ou « diapositive » pour Google Slides).</param>
    public record Options(string Range, string Background, string Canvas, string Unit = "page")
    {
        public static Options From(IReadOnlyDictionary<string, string> v) =>
            new(v.GetValueOrDefault("range") ?? "", v.GetValueOrDefault("background") ?? "none", v.GetValueOrDefault("canvas") ?? "landscape",
                v.GetValueOrDefault("gkind") == "slides" ? "diapositive" : "page");
    }
    public record ConvertResult(List<MediaItem> Files, int Selected, bool Truncated, string? Error);

    // PDFium n'est pas fait pour être appelé en parallèle : une conversion à la fois.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static string PathOf(string id) => Path.Combine(AppPaths.PdfDir, id + ".pdf");

    private static bool ValidId(string id) => Guid.TryParseExact(id, "N", out _);

    /// <summary>Le PDF et ses pages dans les valeurs du formulaire (champs pdf, pdfTitle, pdfPages, pages).</summary>
    public static void Apply(IDictionary<string, string> values, Source source, ConvertResult result)
    {
        values["pdf"] = source.Id;
        values["pdfTitle"] = source.Title;
        values["pdfPages"] = source.Pages.ToString();
        values["pages"] = string.Join(",", result.Files.Select(f => f.Id));
    }

    /// <summary>Le PDF gardé d'un formulaire (null : aucun, ou plus sur le serveur).</summary>
    public static Source? SourceOf(IReadOnlyDictionary<string, string> v) =>
        v.GetValueOrDefault("pdf") is { Length: > 0 } id && Exists(id) && int.TryParse(v.GetValueOrDefault("pdfPages"), out var pages)
            ? new Source(id, v.GetValueOrDefault("pdfTitle") ?? "Document", pages) : null;

    // ---------- Fichier source ----------

    /// <summary>Garde le PDF reçu sur le serveur après avoir vérifié qu'il s'ouvre (un PDF protégé par mot de passe est refusé).</summary>
    public async Task<SourceResult> SaveSource(IBrowserFile file, CancellationToken ct = default)
    {
        if (!file.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return new(null, "Choisissez un fichier PDF (.pdf).");
        if (file.Size > MaxBytes) return new(null, $"« {file.Name} » dépasse la taille maximale ({MaxBytes / 1_000_000} Mo).");
        await using var input = file.OpenReadStream(MaxBytes, ct);
        return await SaveStream(input, Path.GetFileNameWithoutExtension(file.Name), null, ct);
    }

    /// <summary>
    /// Garde un PDF lu dans un flux (envoi, ou export Google Slides / Docs) après avoir vérifié qu'il s'ouvre.
    /// <paramref name="id"/> : remplace le contenu d'un PDF déjà gardé (mise à jour d'un document Google) ; sinon un nouvel identifiant.
    /// En cas d'échec, un PDF gardé sous cet identifiant est conservé tel qu'il était.
    /// </summary>
    public async Task<SourceResult> SaveStream(Stream input, string title, string? id = null, CancellationToken ct = default)
    {
        var reuse = id != null && ValidId(id);
        var target = reuse ? id! : Guid.NewGuid().ToString("N");
        var path = PathOf(target);
        var temp = PathOf(Guid.NewGuid().ToString("N"));   // lu et vérifié avant de remplacer quoi que ce soit
        try
        {
            await using (var fs = File.Create(temp)) await input.CopyToAsync(fs, ct);
            var pages = await Count(temp, ct);
            if (pages == 0) { File.Delete(temp); return new(null, "Ce document n'a aucune page."); }
            File.Move(temp, path, overwrite: true);
            title = title.Trim();
            return new(new Source(target, title.Length > 120 ? title[..120] : title, pages), null);
        }
        catch (OperationCanceledException) { try { File.Delete(temp); } catch (IOException) { } throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "PDF illisible : {Title}", title);
            try { File.Delete(temp); } catch (IOException) { }
            return new(null, "Ce fichier n'est pas un PDF lisible (il est peut-être protégé par un mot de passe ou abîmé).");
        }
    }

    /// <summary>Retire le PDF gardé sur le serveur (remplacé, ou contenu supprimé).</summary>
    public static void Delete(string? id)
    {
        if (string.IsNullOrEmpty(id) || !ValidId(id)) return;
        try { File.Delete(PathOf(id)); } catch (IOException) { }
    }

    public static bool Exists(string? id) => !string.IsNullOrEmpty(id) && ValidId(id) && File.Exists(PathOf(id));

    private static async Task<int> Count(string path, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try { return await Task.Run(() => { using var doc = DocLib.Instance.GetDocReader(path, new PageDimensions(1)); return doc.GetPageCount(); }, ct); }
        finally { Gate.Release(); }
    }

    // ---------- Pages à afficher ----------

    /// <summary>« 1-5, 8, 10-12 » : numéros de pages (à partir de 1), dans l'ordre saisi, sans doublon. Vide : toutes. Null et message si la saisie est invalide.</summary>
    public static (List<int>? Pages, string? Error) ParseRange(string? range, int count)
    {
        var all = Enumerable.Range(1, count);
        if (string.IsNullOrWhiteSpace(range)) return (all.ToList(), null);
        var list = new List<int>();
        foreach (var part in range.Split(',', ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(part, @"^(\d{1,5})(?:\s*[-–]\s*(\d{1,5}))?$");
            if (!m.Success) return (null, $"Pages à afficher : « {part} » n'est pas valide (exemple : 1-5, 8, 10-12).");
            var a = int.Parse(m.Groups[1].Value);
            var b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : a;
            if (a < 1 || b < a || b > count) return (null, $"Pages à afficher : « {part} » sort du document ({count} page{(count > 1 ? "s" : "")}).");
            for (var p = a; p <= b; p++) if (!list.Contains(p)) list.Add(p);
        }
        return (list, null);
    }

    // ---------- Conversion ----------

    /// <summary>
    /// Convertit le PDF gardé en images de la médiathèque (Info["pdf"] = identifiant du PDF). Les images d'une conversion précédente du même PDF
    /// que rien n'utilise sont retirées ; celles d'un contenu enregistré le sont à son enregistrement (<see cref="AppService.SaveInstance"/>).
    /// </summary>
    public async Task<ConvertResult> Convert(Guid clientId, Guid? areaId, Source source, Options options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!Exists(source.Id)) return new(new(), 0, false, "Le PDF n'est plus sur le serveur : choisissez-le à nouveau.");
        var (pages, error) = ParseRange(options.Range, source.Pages);
        if (pages == null) return new(new(), 0, false, error);
        var truncated = pages.Count > MaxPages;
        if (truncated) pages = pages.Take(MaxPages).ToList();

        await Gate.WaitAsync(ct);
        var files = new List<MediaItem>();
        try
        {
            await Task.Run(() =>
            {
                var path = PathOf(source.Id);
                using var info = DocLib.Instance.GetDocReader(path, new PageDimensions(1));   // dimensions des pages en points
                var sizes = pages.Select(p => { using var pr = info.GetPageReader(p - 1); return (W: pr.GetPageWidth(), H: pr.GetPageHeight()); }).ToList();
                for (var n = 0; n < pages.Count; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Conversion du PDF : page {n + 1} / {pages.Count}…");
                    var jpeg = RenderPage(path, pages[n] - 1, sizes[n].W, sizes[n].H, options, out var w, out var h);
                    var file = Guid.NewGuid().ToString("N") + ".jpg";
                    File.WriteAllBytes(Path.Combine(AppPaths.MediaDir, file), jpeg);
                    var name = pages.Count > 1 ? $"{source.Title} — {options.Unit} {pages[n]}" : source.Title;
                    var item = new MediaItem
                    {
                        ClientId = clientId, AreaId = areaId ?? Guid.Empty, Name = name.Length > 120 ? name[..120] : name, Type = "image",
                        FileName = file, Size = jpeg.Length, Width = w, Height = h, AddedUtc = DateTime.UtcNow
                    };
                    item.Info["pdf"] = source.Id;
                    files.Add(item);
                }
            }, ct);
        }
        catch (Exception ex)
        {
            foreach (var f in files) try { File.Delete(Path.Combine(AppPaths.MediaDir, f.FileName!)); } catch (IOException) { }
            if (ex is OperationCanceledException) throw;
            log.LogWarning(ex, "Conversion du PDF {Id} impossible", source.Id);
            return new(new(), 0, false, "La conversion du PDF a échoué : le document est peut-être abîmé.");
        }
        finally { Gate.Release(); }

        store.Write(d => { foreach (var f in files) d.Media.Add(f); });
        PurgeUnused(clientId, source.Id, files.Select(f => f.Id).ToHashSet());
        return new(files, pages.Count, truncated, null);
    }

    /// <summary>Une page en JPEG : seule (cadrage libre du player) ou posée sur un fond blanc ou flou aux proportions de l'écran.</summary>
    private static byte[] RenderPage(string path, int index, int ptW, int ptH, Options o, out int outW, out int outH)
    {
        var withFond = o.Background is "white" or "blur";
        var portraitCanvas = o.Canvas == "portrait";
        // boîte dans laquelle la page est rendue : l'écran choisi si un fond est demandé, sinon le sens de la page elle-même
        var boxPortrait = withFond ? portraitCanvas : ptH > ptW;
        var boxW = boxPortrait ? ShortSide : LongSide;
        var boxH = boxPortrait ? LongSide : ShortSide;
        var scale = Math.Clamp(Math.Min((double)boxW / ptW, (double)boxH / ptH), 0.25, 4);

        byte[] raw; int w, h;
        using (var doc = DocLib.Instance.GetDocReader(path, new PageDimensions(scale)))
        using (var page = doc.GetPageReader(index))
        {
            w = page.GetPageWidth(); h = page.GetPageHeight();
            raw = page.GetImage(RenderFlags.RenderAnnotations);
        }

        using var pdf = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Marshal.Copy(raw, 0, pdf.GetPixels(), Math.Min(raw.Length, w * h * 4));

        // la page seule, sur du blanc (le rendu PDFium est transparent là où le document n'a pas de fond)
        var fits = !withFond || Math.Abs((double)w / h - (double)boxW / boxH) < 0.02;
        var cw = fits ? w : boxW;
        var ch = fits ? h : boxH;
        using var surface = SKSurface.Create(new SKImageInfo(cw, ch, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        if (!fits && o.Background == "blur")
        {
            // fond : la page agrandie pour remplir l'écran, floutée et assombrie ; la page nette se pose dessus
            var k = Math.Max((double)cw / w, (double)ch / h);
            var dst = new SKRect((float)((cw - w * k) / 2), (float)((ch - h * k) / 2), (float)((cw + w * k) / 2), (float)((ch + h * k) / 2));
            using var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(40, 40), FilterQuality = SKFilterQuality.Low };
            canvas.DrawBitmap(pdf, dst, blur);
            canvas.DrawRect(0, 0, cw, ch, new SKPaint { Color = new SKColor(0, 0, 0, 90) });
        }
        var x = (cw - w) / 2f; var y = (ch - h) / 2f;
        if (!fits) canvas.DrawRect(x, y, w, h, new SKPaint { Color = SKColors.White });   // la page garde son blanc
        canvas.DrawBitmap(pdf, x, y);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        outW = cw; outH = ch;
        return data.ToArray();
    }

    /// <summary>Images d'une conversion précédente du même PDF que rien n'utilise : retirées de la médiathèque, fichiers compris.</summary>
    public void PurgeUnused(Guid clientId, string sourceId, HashSet<Guid> keep)
    {
        var removed = store.Write(d =>
        {
            var unused = MediaLibrary.UnusedExports(d.Media.Where(m => m.ClientId == clientId), d.Playlists.Where(p => p.ClientId == clientId), d.Screens.Where(s => s.ClientId == clientId), "pdf")
                .Where(m => m.Info["pdf"] == sourceId && !keep.Contains(m.Id)).ToList();
            var ids = unused.Select(m => m.Id).ToHashSet();
            d.Media.RemoveAll(m => ids.Contains(m.Id));
            return unused;
        });
        foreach (var f in removed.Where(m => m.FileName != null))
        {
            try { File.Delete(Path.Combine(AppPaths.MediaDir, f.FileName!)); } catch (IOException) { }
            Thumbnails.Delete(f.FileName!);
        }
    }
}
