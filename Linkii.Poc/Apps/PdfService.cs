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

    /// <param name="Hash">Empreinte (SHA-256) du fichier gardé : un même PDF envoyé deux fois reste une seule présentation (null : inconnue).</param>
    public record Source(string Id, string Title, int Pages, string? Hash = null);
    public record SourceResult(Source? Source, string? Error);
    /// <param name="Unit">Nom d'une page dans le nom des images (« page », ou « diapositive » pour Google Slides).</param>
    /// <param name="Src">Source de la présentation (voir <see cref="Pres"/>) : ppt, gslides, gdocs ou pdf.</param>
    public record Options(string Range, string Background, string Canvas, string Unit = "page", string Src = "pdf")
    {
        /// <param name="appId">App du contenu (précise PowerPoint ou Google Slides, qui ont le même type de fichier).</param>
        public static Options From(IReadOnlyDictionary<string, string> v, string? appId = null) =>
            new(v.GetValueOrDefault("range") ?? "", v.GetValueOrDefault("background") ?? "none", v.GetValueOrDefault("canvas") ?? "landscape",
                v.GetValueOrDefault("gkind") == "slides" ? "diapositive" : "page",
                appId != null ? Pres.SrcOf(appId) : v.GetValueOrDefault("gkind") == "slides" ? "gslides" : v.GetValueOrDefault("gkind") == "docs" ? "gdocs" : "pdf");
    }
    /// <param name="Presentation">La présentation qui garde les images (document de plus d'une page) ; null : page unique, image ordinaire.</param>
    public record ConvertResult(List<MediaItem> Files, int Selected, bool Truncated, string? Error, MediaItem? Presentation = null);
    /// <summary>Ce qui reconnaît la présentation à l'import suivant (key), la date du fichier converti (mod) et son nom (file).</summary>
    public record Meta(string Key, string? Mod = null, string? File = null);

    // PDFium n'est pas fait pour être appelé en parallèle : une conversion à la fois.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Chemin du PDF gardé : pdf/{organisation}/{id}.pdf, ou pdf/{id}.pdf pour ceux d'avant le rangement (l'identifiant est unique sur la plateforme).</summary>
    public static string PathOf(string id) => Find(id) ?? Path.Combine(AppPaths.PdfDir, id + ".pdf");

    private static string? Find(string id)
    {
        var flat = Path.Combine(AppPaths.PdfDir, id + ".pdf");
        if (File.Exists(flat)) return flat;
        try { return Directory.EnumerateDirectories(AppPaths.PdfDir).Select(d => Path.Combine(d, id + ".pdf")).FirstOrDefault(File.Exists); }
        catch (IOException) { return null; }
    }

    /// <summary>Emplacement d'un nouveau PDF de l'organisation (dossier créé s'il n'existe pas).</summary>
    private static string NewPath(Guid clientId, string id)
    {
        var dir = Path.Combine(AppPaths.PdfDir, clientId.ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, id + ".pdf");
    }

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
    public async Task<SourceResult> SaveSource(Guid clientId, IBrowserFile file, CancellationToken ct = default)
    {
        if (!file.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return new(null, "Choisissez un fichier PDF (.pdf).");
        if (file.Size > MaxBytes) return new(null, $"« {file.Name} » dépasse la taille maximale ({MaxBytes / 1_000_000} Mo).");
        await using var input = file.OpenReadStream(MaxBytes, ct);
        return await SaveStream(clientId, input, Path.GetFileNameWithoutExtension(file.Name), null, ct);
    }

    /// <summary>
    /// Garde un PDF lu dans un flux (envoi, ou export Google Slides / Docs) après avoir vérifié qu'il s'ouvre.
    /// <paramref name="id"/> : remplace le contenu d'un PDF déjà gardé (mise à jour d'un document Google) ; sinon un nouvel identifiant.
    /// En cas d'échec, un PDF gardé sous cet identifiant est conservé tel qu'il était.
    /// </summary>
    public async Task<SourceResult> SaveStream(Guid clientId, Stream input, string title, string? id = null, CancellationToken ct = default)
    {
        var reuse = id != null && ValidId(id);
        var target = reuse ? id! : Guid.NewGuid().ToString("N");
        var path = (reuse ? Find(target) : null) ?? NewPath(clientId, target);
        var temp = NewPath(clientId, Guid.NewGuid().ToString("N"));   // lu et vérifié avant de remplacer quoi que ce soit
        try
        {
            await using (var fs = File.Create(temp)) await input.CopyToAsync(fs, ct);
            var pages = await Count(temp, ct);
            if (pages == 0) { File.Delete(temp); return new(null, "Ce document n'a aucune page."); }
            string hash;
            await using (var hs = File.OpenRead(temp)) hash = System.Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(hs, ct));
            File.Move(temp, path, overwrite: true);
            title = title.Trim();
            return new(new Source(target, title.Length > 120 ? title[..120] : title, pages, hash), null);
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
    /// Convertit le PDF gardé en images de la médiathèque.
    /// Document d'une seule page : une image ordinaire (Info["pdf"] = identifiant du PDF), les images d'une conversion précédente que rien n'utilise sont retirées.
    /// Document de plusieurs pages : les images appartiennent à la <b>présentation</b> du document (<see cref="Pres"/>), créée la première fois et réutilisée ensuite :
    /// seules les pages du rendu demandé qui n'existent pas encore sont produites, et le résultat est la sélection « pages à afficher » du contenu.
    /// <paramref name="meta"/> : de quoi reconnaître le fichier d'origine à l'import suivant (utile à la création de la présentation).
    /// </summary>
    public async Task<ConvertResult> Convert(Guid clientId, Guid? areaId, Source source, Options options, IProgress<string>? progress = null, CancellationToken ct = default, Meta? meta = null)
    {
        if (!Exists(source.Id)) return new(new(), 0, false, "Le PDF n'est plus sur le serveur : choisissez-le à nouveau.");
        var (pages, error) = ParseRange(options.Range, source.Pages);
        if (pages == null) return new(new(), 0, false, error);
        var truncated = pages.Count > MaxPages;
        if (truncated) pages = pages.Take(MaxPages).ToList();

        if (source.Pages > 1) return await ConvertShared(clientId, areaId, source, options, pages, truncated, meta, progress, ct);

        var (files, err) = await Render(clientId, areaId, source, pages, options, pages.Count > 1, (item, _) => item.Info["pdf"] = source.Id, progress, ct);
        if (err != null) return new(new(), 0, false, err);
        store.Write(d => { foreach (var f in files) d.Media.Add(f); });
        PurgeUnused(clientId, source.Id, files.Select(f => f.Id).ToHashSet());
        return new(files, pages.Count, truncated, null);
    }

    /// <summary>Produit les images de ces pages (une conversion PDFium à la fois). Rien n'est ajouté à la médiathèque : à l'appelant.</summary>
    private async Task<(List<MediaItem> Items, string? Error)> Render(Guid clientId, Guid? areaId, Source source, IReadOnlyList<int> pages, Options options, bool numbered,
                                                                      Action<MediaItem, int> tag, IProgress<string>? progress, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        var files = new List<MediaItem>();
        try
        {
            await Task.Run(() =>
            {
                var path = PathOf(source.Id);
                var dir = MediaFiles.Dir(clientId, null);   // pages d'un PDF : dossier commun de l'organisation
                using var info = DocLib.Instance.GetDocReader(path, new PageDimensions(1));   // dimensions des pages en points
                var sizes = pages.Select(p => { using var pr = info.GetPageReader(p - 1); return (W: pr.GetPageWidth(), H: pr.GetPageHeight()); }).ToList();
                for (var n = 0; n < pages.Count; n++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Conversion du PDF : page {n + 1} / {pages.Count}…");
                    var jpeg = RenderPage(path, pages[n] - 1, sizes[n].W, sizes[n].H, options, out var w, out var h);
                    var file = Guid.NewGuid().ToString("N") + ".jpg";
                    File.WriteAllBytes(MediaFiles.PathOf(dir, file), jpeg);
                    var name = numbered ? $"{source.Title} — {options.Unit} {pages[n]}" : source.Title;
                    var item = new MediaItem
                    {
                        ClientId = clientId, AreaId = areaId ?? Guid.Empty, Name = name.Length > 120 ? name[..120] : name, Type = "image",
                        Dir = dir, FileName = file, Size = jpeg.Length, Width = w, Height = h, AddedUtc = DateTime.UtcNow
                    };
                    tag(item, pages[n]);
                    files.Add(item);
                }
            }, ct);
        }
        catch (Exception ex)
        {
            foreach (var f in files) try { File.Delete(f.FilePath!); } catch (IOException) { }
            if (ex is OperationCanceledException) throw;
            log.LogWarning(ex, "Conversion du PDF {Id} impossible", source.Id);
            return (new(), "La conversion du PDF a échoué : le document est peut-être abîmé.");
        }
        finally { Gate.Release(); }
        return (files, null);
    }

    private static string MediaPath(MediaItem m) => m.FilePath ?? "";

    private async Task<ConvertResult> ConvertShared(Guid clientId, Guid? areaId, Source source, Options options, List<int> pages, bool truncated, Meta? meta, IProgress<string>? progress, CancellationToken ct)
    {
        var vk = Pres.Variant(options);
        var pres = PresentationOfPdf(clientId, source.Id);
        var presId = pres?.Id ?? Guid.NewGuid();
        var gen = pres?.Info.GetValueOrDefault("gen") ?? "1";

        // le rendu demandé (pages à afficher) et, en plus, toutes les pages en rendu de base : la carte et son tiroir montrent la présentation entière
        var targets = new List<(string Vk, Options O, List<int> Pages)> { (vk, options, pages) };
        if (vk != "none") targets.Add(("none", Pres.OptionsOf("none", options.Unit, options.Src), Enumerable.Range(1, Math.Min(source.Pages, MaxPages)).ToList()));

        var made = new List<MediaItem>();
        Dictionary<int, MediaItem>? wanted = null;
        foreach (var (tvk, topt, tpages) in targets)
        {
            var have = pres == null ? new Dictionary<int, MediaItem>() : store.Read(d => d.Media
                .Where(m => m.ClientId == clientId && Pres.IdOf(m) == presId.ToString() && m.Info.GetValueOrDefault("var") == tvk && m.Info.GetValueOrDefault("gen") == gen && m.FileName != null && File.Exists(MediaPath(m)))
                .GroupBy(Pres.PageOf).ToDictionary(g => g.Key, g => g.First()));
            foreach (var f in made.Where(f => f.Info["var"] == tvk)) have[Pres.PageOf(f)] = f;
            var missing = tpages.Where(p => !have.ContainsKey(p)).ToList();
            if (missing.Count > 0)
            {
                var (items, err) = await Render(clientId, areaId, source, missing, topt, true, (item, pg) =>
                {
                    item.Info["pres"] = presId.ToString(); item.Info["var"] = tvk; item.Info["pg"] = pg.ToString(); item.Info["gen"] = gen;
                }, progress, ct);
                if (err != null) { foreach (var f in made) try { File.Delete(MediaPath(f)); } catch (IOException) { } return new(new(), 0, false, err); }
                made.AddRange(items);
                foreach (var f in items) have[Pres.PageOf(f)] = f;
            }
            if (tvk == vk) wanted = have;
        }
        var have2 = wanted!;

        var result = store.Write(d =>
        {
            foreach (var f in made) d.Media.Add(f);
            var p = d.Media.FirstOrDefault(m => m.Id == presId && Pres.Is(m));
            if (p == null)
            {
                var name = source.Title.Length > 120 ? source.Title[..120] : source.Title;
                p = new MediaItem { Id = presId, ClientId = clientId, AreaId = areaId ?? Guid.Empty, Name = name, Type = Pres.Type, AddedUtc = DateTime.UtcNow, Width = made.FirstOrDefault(m => Pres.PageOf(m) == 1)?.Width ?? made.FirstOrDefault()?.Width, Height = made.FirstOrDefault(m => Pres.PageOf(m) == 1)?.Height ?? made.FirstOrDefault()?.Height };
                p.Info["src"] = options.Src; p.Info["pdfId"] = source.Id; p.Info["gen"] = gen; p.Info["synced"] = DateTime.UtcNow.ToString("o");
                p.FolderId = Pres.FolderFor(d, clientId, p.AreaId, options.Src);   // rangée dans « intégration_médias » (comme Canva)
                d.Media.Add(p);
            }
            foreach (var f in made) f.FolderId = p.FolderId;   // ses images suivent sa carte
            p.Info["pages"] = source.Pages.ToString();
            if (meta != null)
            {
                p.Info["key"] = meta.Key;
                if (meta.Mod != null) p.Info["mod"] = meta.Mod;
                if (meta.File != null) p.Info["file"] = meta.File;
            }
            return p;
        });
        if (pres == null) PurgeUnused(clientId, source.Id, new HashSet<Guid>());   // anciennes images ordinaires de ce PDF (avant les présentations)
        return new(pages.Select(p => have2[p]).ToList(), pages.Count, truncated, null, result);
    }

    // ---------- Présentations ----------

    /// <summary>Présentation de l'aire reconnue par son fichier d'origine (null : jamais converti, ou PDF disparu du serveur).</summary>
    public MediaItem? FindPresentation(Guid clientId, Guid? areaId, string key) => store.Read(d => d.Media.FirstOrDefault(m =>
        m.ClientId == clientId && Pres.Is(m) && m.AreaId == (areaId ?? Guid.Empty) && m.Info.GetValueOrDefault("key") == key && Exists(m.Info.GetValueOrDefault("pdfId"))));

    public MediaItem? PresentationOfPdf(Guid clientId, string pdfId) => store.Read(d => d.Media.FirstOrDefault(m => m.ClientId == clientId && Pres.Is(m) && m.Info.GetValueOrDefault("pdfId") == pdfId));

    /// <summary>
    /// PDF envoyé : si la même présentation existe déjà dans l'aire (même empreinte), on la réutilise et le fichier qu'on vient d'envoyer est effacé.
    /// </summary>
    public Source ReuseUploaded(Guid clientId, Guid? areaId, Source fresh, out bool reused)
    {
        reused = false;
        if (fresh.Hash == null || fresh.Pages <= 1 || FindPresentation(clientId, areaId, "sha:" + fresh.Hash) is not { } p) return fresh;
        var id = p.Info["pdfId"];
        if (id != fresh.Id) Delete(fresh.Id);
        reused = true;
        return new Source(id, p.Name, Pres.Pages(p) is > 0 and var n ? n : fresh.Pages, fresh.Hash);
    }

    /// <summary>Le PDF n'est retiré du serveur que si aucune présentation ne le garde.</summary>
    public void Release(string? id)
    {
        if (string.IsNullOrEmpty(id) || store.Read(d => Pres.Owns(d, id))) return;
        Delete(id);
    }

    public record RefreshResult(List<Guid> Contents, string? Error);

    /// <summary>
    /// Le fichier d'origine a changé : le PDF gardé est déjà remplacé (<see cref="SaveStream"/>) ; les images de la présentation sont refaites, une seule fois,
    /// pour chaque rendu et chaque page que ses contenus utilisent, et tous ces contenus passent aux nouvelles images. Rend les contenus à envoyer aux écrans ;
    /// les anciennes images sont retirées ensuite (<see cref="PurgeStale"/>), une fois les écrans passés aux nouvelles.
    /// </summary>
    public async Task<RefreshResult> Refresh(Guid clientId, Guid presId, Source source, string? mod, string? file, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var (pres, contents) = store.Read(d =>
        {
            var p = d.Media.FirstOrDefault(m => m.Id == presId && m.ClientId == clientId && Pres.Is(m));
            return (p, p == null ? new List<MediaItem>() : Pres.ContentsOf(d.Media.Where(m => m.ClientId == clientId), p));
        });
        if (pres == null) return new(new(), "Présentation introuvable.");
        var contentIds = contents.Select(c => c.Id).ToList();
        var newGen = (int.TryParse(pres.Info.GetValueOrDefault("gen"), out var g) ? g + 1 : 2).ToString();
        var srcKind = pres.Info.GetValueOrDefault("src") ?? "pdf";
        var unit = Pres.Unit(srcKind);
        var area = pres.AreaId == Guid.Empty ? (Guid?)null : pres.AreaId;

        // ce que chaque contenu affiche : un rendu et des pages
        var wanted = new Dictionary<string, SortedSet<int>> { ["none"] = new(Enumerable.Range(1, Math.Min(source.Pages, MaxPages))) };   // toutes les pages en rendu de base : la carte et son tiroir
        var per = new List<(MediaItem C, string Vk, List<int> Pages)>();
        foreach (var c in contents)
        {
            var o = Options.From(c.Settings, c.AppId);
            var (list, _) = ParseRange(o.Range, source.Pages);
            list = (list ?? Enumerable.Range(1, source.Pages).ToList()).Take(MaxPages).ToList();
            var vk = Pres.Variant(o);
            if (!wanted.TryGetValue(vk, out var set)) wanted[vk] = set = new();
            set.UnionWith(list);
            per.Add((c, vk, list));
        }

        var made = new Dictionary<(string Vk, int Pg), MediaItem>();
        foreach (var (vk, set) in wanted)
        {
            var (items, err) = await Render(clientId, area, source, set.ToList(), Pres.OptionsOf(vk, unit, srcKind), true, (item, pg) =>
            {
                item.Info["pres"] = presId.ToString(); item.Info["var"] = vk; item.Info["pg"] = pg.ToString(); item.Info["gen"] = newGen;
            }, progress, ct);
            if (err != null)
            {
                foreach (var f in made.Values) try { File.Delete(MediaPath(f)); } catch (IOException) { }
                return new(new(), err);
            }
            foreach (var f in items) made[(vk, Pres.PageOf(f))] = f;
        }

        store.Write(d =>
        {
            var folder = d.Media.FirstOrDefault(m => m.Id == presId)?.FolderId;
            foreach (var f in made.Values) { f.FolderId = folder; d.Media.Add(f); }
            foreach (var (c, vk, list) in per)
            {
                if (d.Media.FirstOrDefault(m => m.Id == c.Id) is not { } live) continue;
                live.Settings["pages"] = string.Join(",", list.Select(p => made[(vk, p)].Id));
                live.Settings["pdfPages"] = source.Pages.ToString();
                if (mod != null && live.Settings.ContainsKey("gfileMod")) live.Settings["gfileMod"] = mod;
            }
            if (d.Media.FirstOrDefault(m => m.Id == presId) is { } p)
            {
                p.Info["gen"] = newGen; p.Info["pages"] = source.Pages.ToString(); p.Info["synced"] = DateTime.UtcNow.ToString("o");
                if (mod != null) p.Info["mod"] = mod;
                if (file != null) p.Info["file"] = file;
                if (made.GetValueOrDefault(("none", 1)) is { } first) { p.Width = first.Width; p.Height = first.Height; }
            }
        });
        return new(contentIds, null);
    }

    /// <summary>Images d'une ancienne génération de la présentation que rien n'utilise plus (ni contenu, ni écran) : retirées, fichiers compris.</summary>
    public void PurgeStale(Guid clientId, Guid presId) => Pres.PurgeStale(store, clientId, presId);

    public static void DeleteImageFiles(IEnumerable<MediaItem> items)
    {
        foreach (var f in items.Where(m => m.FileName != null))
        {
            try { File.Delete(f.FilePath!); } catch (IOException) { }
            Thumbnails.Delete(f);
        }
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
            try { File.Delete(f.FilePath!); } catch (IOException) { }
            Thumbnails.Delete(f);
        }
    }
}
