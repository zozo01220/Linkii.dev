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
        public string ApplicationName { get; set; } = "Linkii";
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
        Assert.All(res.Files, f => { Assert.Equal("image", f.Type); Assert.Equal(id, f.Info["pdf"]); Assert.True(File.Exists(Path.Combine(AppPaths.MediaDir, f.FileName!))); });
        Assert.Equal("Programme — page 1", res.Files[0].Name);
        Assert.True(res.Files[0].Height <= 1920 && res.Files[0].Width <= 1080 && res.Files[0].Height > res.Files[0].Width);   // portrait
        Assert.True(res.Files[1].Width <= 1920 && res.Files[1].Height <= 1080 && res.Files[1].Width > res.Files[1].Height);   // paysage
        Assert.Equal(3, store.Read(d => d.Media.Count(m => m.Info.ContainsKey("pdf"))));
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
        using (var bmp = SKBitmap.Decode(Path.Combine(AppPaths.MediaDir, land.Files[0].FileName!)))   // avant la conversion suivante, qui retire ces pages
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
    public async Task A_new_conversion_removes_the_previous_unused_pages_of_the_same_pdf()
    {
        var id = WritePdf(Three);
        var first = await pdf.Convert(tenant.Id, null, new PdfService.Source(id, "Doc", 3), new PdfService.Options("", "none", "landscape"));
        var second = await pdf.Convert(tenant.Id, null, new PdfService.Source(id, "Doc", 3), new PdfService.Options("1", "none", "landscape"));

        var left = store.Read(d => d.Media.Where(m => m.Info.ContainsKey("pdf")).Select(m => m.Id).ToList());
        Assert.Equal(second.Files.Select(f => f.Id), left);
        Assert.All(first.Files, f => Assert.False(File.Exists(Path.Combine(AppPaths.MediaDir, f.FileName!))));
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
