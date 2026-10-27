using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>App « Document PDF » : choix des pages, conversion en images 1080p (PDFium), fonds, nettoyage.</summary>
[Collection("platform")]   // AppPaths est statique : sérialisé avec les tests qui démarrent l'application
public class PdfServiceTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "linkii-pdf-" + Guid.NewGuid().ToString("N"));
    private readonly JsonStore store;
    private readonly PdfService pdf;
    private readonly Tenant tenant = new() { Name = "Client" };

    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Linkivi";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private readonly (string Media, string Pdf, string Thumb) before = (AppPaths.MediaDir, AppPaths.PdfDir, AppPaths.ThumbDir);

    public PdfServiceTests()
    {
        Directory.CreateDirectory(AppPaths.MediaDir = Path.Combine(dir, "media"));
        Directory.CreateDirectory(AppPaths.PdfDir = Path.Combine(dir, "pdf"));
        Directory.CreateDirectory(AppPaths.ThumbDir = Path.Combine(dir, "thumbs"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Linkii:DataDir"] = dir }).Build();
        store = new JsonStore(config, new Env());
        store.Write(db => db.Clients.Add(tenant));
        pdf = new PdfService(store, NullLogger<PdfService>.Instance);
    }

    public void Dispose()
    {
        (AppPaths.MediaDir, AppPaths.PdfDir, AppPaths.ThumbDir) = before;
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>PDF minimal : un rectangle plein par page, aux dimensions données (en points).</summary>
    private string WritePdf((int W, int H)[] pages)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Obj(int n, string body) { offsets.Add(sb.Length); sb.Append($"{n} 0 obj\n{body}\nendobj\n"); }
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, $"<< /Type /Pages /Count {pages.Length} /Kids [{string.Join(" ", pages.Select((_, i) => $"{3 + i * 2} 0 R"))}] >>");
        for (var i = 0; i < pages.Length; i++)
        {
            var (w, h) = pages[i];
            Obj(3 + i * 2, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Contents {4 + i * 2} 0 R >>");
            var content = $"0.1 0.3 0.8 rg 20 20 {w - 40} {h - 40} re f";
            Obj(4 + i * 2, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(Path.Combine(AppPaths.PdfDir, id + ".pdf"), Encoding.ASCII.GetBytes(sb.ToString()));
        return id;
    }

    private static readonly (int, int)[] Three = { (595, 842), (842, 595), (595, 842) };

    [Fact]
    public void Range_accepts_pages_and_intervals_in_the_given_order()
    {
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, PdfService.ParseRange("", 5).Pages);
        Assert.Equal(new[] { 4, 5, 1, 2 }, PdfService.ParseRange("4-5, 1-2, 2", 5).Pages);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("3-2")]
    [InlineData("a")]
    public void Range_refuses_pages_outside_the_document_or_unreadable(string range)
    {
        var (pages, error) = PdfService.ParseRange(range, 5);
        Assert.Null(pages);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Each_page_becomes_a_1080p_image_in_the_media_library()
    {
        var id = WritePdf(Three);
        var res = await pdf.Convert(tenant.Id, null, new PdfService.Source(id, "Programme", 3), new PdfService.Options("", "none", "landscape"));

        Assert.Null(res.Error);
        Assert.Equal(3, res.Files.Count);
        var pres = Assert.IsType<MediaItem>(res.Presentation);   // plusieurs pages : une présentation de la médiathèque garde les images
        Assert.Equal(Pres.Type, pres.Type);
        Assert.Equal(id, pres.Info["pdfId"]);
        Assert.Equal("3", pres.Info["pages"]);
        Assert.All(res.Files, f => { Assert.Equal("image", f.Type); Assert.Equal(pres.Id.ToString(), f.Info["pres"]); Assert.False(f.Info.ContainsKey("pdf")); Assert.True(File.Exists(f.FilePath)); });
        Assert.Equal("Programme — page 1", res.Files[0].Name);
        Assert.True(res.Files[0].Height <= 1920 && res.Files[0].Width <= 1080 && res.Files[0].Height > res.Files[0].Width);   // portrait
        Assert.True(res.Files[1].Width <= 1920 && res.Files[1].Height <= 1080 && res.Files[1].Width > res.Files[1].Height);   // paysage
        Assert.Equal(3, store.Read(d => d.Media.Count(Pres.IsPage)));
    }

    [Fact]
    public async Task A_single_page_document_stays_an_ordinary_image()
    {
        var id = WritePdf(new[] { (595, 842) });
        var res = await pdf.Convert(tenant.Id, null, new PdfService.Source(id, "Affiche", 1), new PdfService.Options("", "none", "landscape"));
        Assert.Null(res.Presentation);
        Assert.Equal(id, Assert.Single(res.Files).Info["pdf"]);
        Assert.DoesNotContain(store.Read(d => d.Media.ToList()), Pres.Is);
    }

    [Fact]
    public async Task A_new_conversion_of_a_single_page_removes_the_previous_unused_image_of_the_same_pdf()
    {
        var id = WritePdf(new[] { (595, 842) });
        var first = await pdf.Convert(tenant.Id, null, new PdfService.Source(id, "Doc", 1), new PdfService.Options("", "none", "landscape"));
        var second = await pdf.Convert(tenant.Id, null, new PdfService.Source(id, "Doc", 1), new PdfService.Options("", "white", "landscape"));

        var left = store.Read(d => d.Media.Where(m => m.Info.ContainsKey("pdf")).Select(m => m.Id).ToList());
        Assert.Equal(second.Files.Select(f => f.Id), left);
        Assert.All(first.Files, f => Assert.False(File.Exists(f.FilePath)));
    }

    [Fact]
    public async Task The_same_document_converted_again_reuses_its_pages_and_a_new_background_adds_only_that_variant()
    {
        var id = WritePdf(Three);
        var first = await pdf.Convert(tenant.Id, null, new PdfService.Source(id, "Doc", 3), new PdfService.Options("", "none", "landscape"));
        var again = await pdf.Convert(tenant.Id, null, new PdfService.Source(id, "Doc", 3), new PdfService.Options("2-3", "none", "landscape"));
        Assert.Equal(first.Files.Skip(1).Select(f => f.Id), again.Files.Select(f => f.Id));   // aucune image créée : les mêmes pages
        Assert.Equal(3, store.Read(d => d.Media.Count(Pres.IsPage)));
        Assert.Single(store.Read(d => d.Media.Where(Pres.Is).ToList()));

        var white = await pdf.Convert(tenant.Id, null, new PdfService.Source(id, "Doc", 3), new PdfService.Options("1", "white", "landscape"));
        Assert.DoesNotContain(white.Files[0].Id, first.Files.Select(f => f.Id));   // autre rendu : autre image
        Assert.Equal(4, store.Read(d => d.Media.Count(Pres.IsPage)));   // les trois pages de base + la page blanche
        Assert.Single(store.Read(d => d.Media.Where(Pres.Is).ToList()));
    }

    [Fact]
    public async Task A_modified_file_is_converted_once_and_every_content_switches_to_the_new_images()
    {
        var id = WritePdf(Three);
        var res = await pdf.Convert(tenant.Id, null, new PdfService.Source(id, "Doc", 3), new PdfService.Options("1-2", "none", "landscape"), meta: new PdfService.Meta("ms-powerpoint:abc", "2026-10-01"));
        var pres = res.Presentation!;
        var a = new MediaItem { ClientId = tenant.Id, AppId = "ms-powerpoint", Name = "Accueil", Type = "app" };
        var b = new MediaItem { ClientId = tenant.Id, AppId = "ms-powerpoint", Name = "Hall", Type = "app" };
        foreach (var c in new[] { a, b })
        {
            c.Settings["pdf"] = id; c.Settings["pages"] = string.Join(",", res.Files.Select(f => f.Id)); c.Settings["range"] = "1-2"; c.Settings["gkind"] = "slides"; c.Settings["gfileMod"] = "2026-10-01";
        }
        store.Write(d => { d.Media.Add(a); d.Media.Add(b); });
        Assert.Equal(2, store.Read(d => Pres.ContentsOf(d.Media, pres).Count));

        // le fichier change : le PDF gardé est remplacé (même identifiant), puis la présentation est refaite une fois
        File.Copy(WritePdfPath(WritePdf(new[] { (595, 842), (842, 595), (595, 842), (842, 595) })), WritePdfPath(id), true);
        var refreshed = await pdf.Refresh(tenant.Id, pres.Id, new PdfService.Source(id, "Doc", 4), "2026-10-05", "Doc.pptx");
        Assert.Null(refreshed.Error);
        Assert.Equal(2, refreshed.Contents.Count);

        var after = store.Read(d => d.Media.Where(m => m.AppId != null).ToList());
        Assert.All(after, c => { Assert.Equal(2, AppField.MediaIds(c.Settings["pages"]).Count); Assert.Equal("4", c.Settings["pdfPages"]); Assert.Equal("2026-10-05", c.Settings["gfileMod"]); });
        Assert.Equal(after[0].Settings["pages"], after[1].Settings["pages"]);   // les deux contenus partagent les mêmes images
        Assert.DoesNotContain(AppField.MediaIds(after[0].Settings["pages"])[0], res.Files.Select(f => f.Id));
        Assert.Equal("4", store.Read(d => d.Media.First(m => m.Id == pres.Id).Info["pages"]));

        pdf.PurgeStale(tenant.Id, pres.Id);   // les écrans sont passés aux nouvelles images : les anciennes partent
        Assert.All(res.Files, f => Assert.False(File.Exists(f.FilePath)));
        Assert.Equal(4, store.Read(d => d.Media.Count(m => Pres.IsPage(m) && m.Info["gen"] == "2" && m.Info["var"] == "none")));   // les 4 pages de base de la nouvelle version
    }


    [Fact]
    public async Task A_presentation_and_its_pages_go_into_the_integration_media_folder_shared_by_all_of_that_source()
    {
        var a = await pdf.Convert(tenant.Id, null, new PdfService.Source(WritePdf(Three), "Menu", 3), new PdfService.Options("", "none", "landscape"));
        var b = await pdf.Convert(tenant.Id, null, new PdfService.Source(WritePdf(Three), "Carte", 3), new PdfService.Options("", "none", "landscape"));
        var folder = Assert.Single(store.Read(d => d.MediaFolders.ToList()));
        Assert.Equal("PDF_médias", folder.Name);
        Assert.Equal(folder.Id, a.Presentation!.FolderId);
        Assert.Equal(folder.Id, b.Presentation!.FolderId);
        Assert.All(a.Files.Concat(b.Files), f => Assert.Equal(folder.Id, f.FolderId));   // les pages suivent la carte
        Assert.Equal("PowerPoint_médias", Pres.FolderName("ppt"));
        Assert.Equal("GoogleSlides_médias", Pres.FolderName("gslides"));
        Assert.Equal("Canva_médias", Pres.FolderName("canva"));
    }

    [Fact]
    public void Every_integration_document_type_can_be_imported_and_is_recognised_by_a_common_key()
    {
        foreach (var name in new[] { "a.pptx", "a.ppt", "a.docx", "a.doc", "a.xlsx", "a.xls", "a.odt", "a.ods", "a.csv" })
            Assert.True(CloudFileKind.MsDocument.Matches(name), name);
        Assert.False(CloudFileKind.MsDocument.Matches("a.png"));
        Assert.Equal("ppt", CloudFileKind.MsDocument.SrcOf("Offre.pptx"));
        Assert.Equal("doc", CloudFileKind.MsDocument.SrcOf("Note.docx"));
        Assert.Equal("xls", CloudFileKind.MsDocument.SrcOf("Budget.xlsx"));
        Assert.Equal("gslides", CloudFileKind.Slides.SrcOf("x"));
        Assert.Equal("gdocs", CloudFileKind.Docs.SrcOf("x"));
        Assert.Equal("gsheets", CloudFileKind.Sheets.SrcOf("x"));
        // l'app PowerPoint et le bouton Importer reconnaissent le même fichier
        Assert.Equal(CloudFileKind.MsDocument.KeyOf("d/1"), CloudFileKind.PowerPoint.KeyOf("d/1"));
        var pres = new MediaItem { Type = Pres.Type };
        pres.Info["key"] = "onedrive:d/1"; pres.Info["src"] = "doc";
        Assert.Equal(CloudFileKind.MsDocument, CloudFileKind.ForPresentation(pres));
        Assert.Equal("d/1", CloudFileKind.RefOf(pres));
        pres.Info["key"] = "ms-powerpoint:d/2";   // ancienne clé (une par app)
        Assert.Equal(CloudFileKind.MsDocument, CloudFileKind.ForPresentation(pres));
        Assert.Equal("d/2", CloudFileKind.RefOf(pres));
        pres.Info["key"] = "gdrive:abc"; pres.Info["src"] = "gsheets";
        Assert.Equal(CloudFileKind.Sheets, CloudFileKind.ForPresentation(pres));
    }


    [Fact]
    public void Local_documents_are_recognised_by_extension_and_become_ppt_doc_xls_or_pdf()
    {
        foreach (var n in new[] { "a.pdf", "a.PPT", "a.pptx", "a.doc", "a.docx", "a.xls", "a.xlsx", "a.odt", "a.rtf", "a.csv" }) Assert.True(LocalDocuments.IsDocument(n), n);
        Assert.False(LocalDocuments.IsDocument("a.png"));
        Assert.False(LocalDocuments.IsDocument("a.mp4"));
        Assert.Equal("ppt", LocalDocuments.SrcOf("Offre.pptx"));
        Assert.Equal("doc", LocalDocuments.SrcOf("Note.DOCX"));
        Assert.Equal("xls", LocalDocuments.SrcOf("Budget.xlsx"));
        Assert.Equal("pdf", LocalDocuments.SrcOf("Menu.pdf"));
    }

    [Fact]
    public async Task A_pdf_from_the_computer_is_one_presentation_in_its_integration_folder_and_a_second_send_reuses_it()
    {
        var docs = new LocalDocuments(pdf, new OfficeConverter(NullLogger<OfficeConverter>.Instance), store);
        var bytes = File.ReadAllBytes(WritePdfPath(WritePdf(Three)));
        var first = await docs.Import(new MemoryStream(bytes), "Menu.pdf", tenant.Id, null);
        Assert.Null(first.Error);
        Assert.Equal(Pres.Type, first.Done!.Item.Type);
        Assert.False(first.Done.Reused);
        Assert.Equal("PDF_médias", store.Read(d => d.MediaFolders.Single(f => f.Id == first.Done.Item.FolderId).Name));

        var second = await docs.Import(new MemoryStream(bytes), "Menu copie.pdf", tenant.Id, null);
        Assert.True(second.Done!.Reused);
        Assert.Equal(first.Done.Item.Id, second.Done.Item.Id);
        Assert.Single(store.Read(d => d.Media.Where(Pres.Is).ToList()));
        Assert.Equal(3, store.Read(d => d.Media.Count(Pres.IsPage)));
    }

    [Fact]
    public async Task A_word_document_from_the_computer_is_converted_by_LibreOffice_and_kept_once()
    {
        var office = new OfficeConverter(NullLogger<OfficeConverter>.Instance);
        if (!office.Available) return;   // LibreOffice absent de ce poste : rien à vérifier ici
        var docs = new LocalDocuments(pdf, office, store);
        var rtf = Encoding.ASCII.GetBytes(@"{\rtf1\ansi Premiere page\page Deuxieme page\page Troisieme page}");

        var first = await docs.Import(new MemoryStream(rtf), "Note.rtf", tenant.Id, null);
        Assert.Null(first.Error);
        Assert.Equal(Pres.Type, first.Done!.Item.Type);
        Assert.Equal(3, Pres.Pages(first.Done.Item));
        Assert.Equal("doc", first.Done.Item.Info["src"]);
        Assert.Equal("Word_médias", store.Read(d => d.MediaFolders.Single(f => f.Id == first.Done.Item.FolderId).Name));

        var again = await docs.Import(new MemoryStream(rtf), "Note.rtf", tenant.Id, null);   // même fichier : pas de seconde conversion
        Assert.True(again.Done!.Reused);
        Assert.Equal(first.Done.Item.Id, again.Done.Item.Id);
        Assert.Single(store.Read(d => d.Media.Where(Pres.Is).ToList()));
    }

    [Fact]
    public async Task Without_LibreOffice_an_office_document_is_refused_with_a_clear_message()
    {
        var office = new OfficeConverter(NullLogger<OfficeConverter>.Instance);
        if (office.Available) return;
        var docs = new LocalDocuments(pdf, office, store);
        var res = await docs.Import(new MemoryStream(new byte[] { 1, 2, 3 }), "Offre.pptx", tenant.Id, null);
        Assert.Null(res.Done);
        Assert.Equal(OfficeConverter.Missing, res.Error);
    }


    [Fact]
    public void Local_import_follows_the_installed_integrations()
    {
        Func<string, bool> none = _ => false, pptOnly = a => a == "ms-powerpoint", pdfOnly = a => a == "pdf", both = _ => true;
        Assert.Empty(LocalDocuments.AllowedExtensions(none));
        Assert.Equal("", LocalDocuments.FormatsText(none));
        Assert.True(LocalDocuments.Allowed("a.pptx", pptOnly));
        Assert.False(LocalDocuments.Allowed("a.pdf", pptOnly));
        Assert.False(LocalDocuments.Allowed("a.docx", pptOnly));
        Assert.True(LocalDocuments.Allowed("a.pdf", pdfOnly));
        Assert.True(LocalDocuments.Allowed("a.docx", pdfOnly));
        Assert.True(LocalDocuments.Allowed("a.xlsx", pdfOnly));
        Assert.False(LocalDocuments.Allowed("a.pptx", pdfOnly));
        Assert.False(LocalDocuments.Allowed("a.png", both));
        Assert.Equal("PowerPoint", LocalDocuments.FormatsText(pptOnly));
        Assert.Equal("PDF, Word, Excel", LocalDocuments.FormatsText(pdfOnly));
        Assert.Equal("PowerPoint, PDF, Word, Excel", LocalDocuments.FormatsText(both));
        Assert.DoesNotContain(".pdf", LocalDocuments.AllowedExtensions(pptOnly));
        Assert.Contains(".pptx", LocalDocuments.AllowedExtensions(pptOnly));
        Assert.Contains("Document PDF", LocalDocuments.NotInstalled("a.docx"));
        Assert.Contains("Microsoft PowerPoint", LocalDocuments.NotInstalled("a.pptx"));
    }

    private static string WritePdfPath(string id) => Path.Combine(AppPaths.PdfDir, id + ".pdf");

    [Fact]
    public async Task The_same_pdf_sent_twice_is_one_presentation()
    {
        var path = WritePdfPath(WritePdf(Three));
        await using var s1 = File.OpenRead(path);
        var first = await pdf.SaveStream(tenant.Id, s1, "Menu");
        await pdf.Convert(tenant.Id, null, first.Source!, new PdfService.Options("", "none", "landscape"), meta: new PdfService.Meta("sha:" + first.Source!.Hash));

        await using var s2 = File.OpenRead(path);
        var second = await pdf.SaveStream(tenant.Id, s2, "Menu");
        var kept = pdf.ReuseUploaded(tenant.Id, null, second.Source!, out var reused);
        Assert.True(reused);
        Assert.Equal(first.Source.Id, kept.Id);
        Assert.False(PdfService.Exists(second.Source!.Id));   // le fichier envoyé en double est effacé
    }

    [Fact]
    public void Duplicates_from_before_the_presentations_are_merged_into_one()
    {
        // deux contenus PowerPoint du même fichier, chacun avec son propre PDF et ses propres images (comme avant les présentations)
        var contents = new List<MediaItem>();
        var all = new List<MediaItem>();
        for (var n = 0; n < 2; n++)
        {
            var id = WritePdf(Three);
            var imgs = new List<MediaItem>();
            for (var p = 1; p <= 3; p++)   // images d'avant : Info["pdf"], sans présentation
            {
                var file = Guid.NewGuid().ToString("N") + ".jpg";
                File.WriteAllBytes(Path.Combine(AppPaths.MediaDir, file), new byte[] { 1, 2, 3 });
                var img = new MediaItem { ClientId = tenant.Id, Name = $"Offre — diapositive {p}", Type = "image", FileName = file, Size = 3, AddedUtc = DateTime.UtcNow };
                img.Info["pdf"] = id; imgs.Add(img);
            }
            var c = new MediaItem { ClientId = tenant.Id, AppId = "ms-powerpoint", Name = "Écran " + n, Type = "app" };
            c.Settings["pdf"] = id; c.Settings["pdfTitle"] = "Offre"; c.Settings["pdfPages"] = "3"; c.Settings["pages"] = string.Join(",", imgs.Select(i => i.Id));
            c.Settings["gfile"] = "item-1"; c.Settings["gfileMod"] = "2026-10-01"; c.Settings["gkind"] = "slides";
            contents.Add(c); all.AddRange(imgs);
        }
        store.Write(d => { foreach (var m in all) d.Media.Add(m); foreach (var c in contents) d.Media.Add(c); });

        var merge = new PresentationMerge(store, NullLogger<PresentationMerge>.Instance);
        var outcome = merge.Run(tenant.Id);

        Assert.Equal(2, outcome.Adopted);
        Assert.Equal(1, outcome.Merged);
        var pres = Assert.Single(store.Read(d => d.Media.Where(Pres.Is).ToList()));
        var live = store.Read(d => d.Media.Where(m => m.AppId != null).ToList());
        Assert.Equal(live[0].Settings["pages"], live[1].Settings["pages"]);   // les deux contenus pointent vers les mêmes images
        Assert.Equal(live[0].Settings["pdf"], live[1].Settings["pdf"]);
        Assert.Equal(3, store.Read(d => d.Media.Count(m => Pres.IdOf(m) == pres.Id.ToString() && m.Info["gen"] == "1")));
        Assert.Equal(3, store.Read(d => d.Media.Count(Pres.IsPage)));   // les images en trop sont parties
    }

    [Fact]
    public async Task Selected_pages_only_are_converted()
    {
        var id = WritePdf(Three);
        var res = await pdf.Convert(tenant.Id, null, new PdfService.Source(id, "Doc", 3), new PdfService.Options("3, 1", "none", "landscape"));
        Assert.Equal(new[] { "Doc — page 3", "Doc — page 1" }, res.Files.Select(f => f.Name));
    }

    [Theory]
    [InlineData("white")]
    [InlineData("blur")]
    public async Task A_background_gives_the_page_the_proportions_of_the_screen(string background)
    {
        var id = WritePdf(Three);
        var land = await pdf.Convert(tenant.Id, null, new PdfService.Source(id, "Doc", 3), new PdfService.Options("1", background, "landscape"));
        Assert.Equal((1920, 1080), (land.Files[0].Width, land.Files[0].Height));
        using (var bmp = SKBitmap.Decode(land.Files[0].FilePath!))   // avant la conversion suivante, qui retire ces pages
        {
            var middle = bmp.GetPixel(960, 540);   // la page (bleue) au centre
            Assert.True(middle.Blue > middle.Red + 100);
            var edge = bmp.GetPixel(5, 540);       // le fond, pas la page
            Assert.True(background == "white" ? edge.Red > 240 && edge.Green > 240 : edge.Blue >= edge.Red);
        }
        var portrait = await pdf.Convert(tenant.Id, null, new PdfService.Source(id, "Doc", 3), new PdfService.Options("2", background, "portrait"));
        Assert.Equal((1080, 1920), (portrait.Files[0].Width, portrait.Files[0].Height));
    }

    [Fact]
    public async Task More_than_fifty_pages_are_cut_and_reported()
    {
        var id = WritePdf(Enumerable.Repeat((200, 200), 52).ToArray());
        var res = await pdf.Convert(tenant.Id, null, new PdfService.Source(id, "Long", 52), new PdfService.Options("", "none", "landscape"));
        Assert.True(res.Truncated);
        Assert.Equal(50, res.Files.Count);
    }

    [Fact]
    public async Task A_missing_source_is_reported_not_thrown()
    {
        var res = await pdf.Convert(tenant.Id, null, new PdfService.Source(Guid.NewGuid().ToString("N"), "Perdu", 2), new PdfService.Options("", "none", "landscape"));
        Assert.NotNull(res.Error);
        Assert.Empty(res.Files);
    }
}
