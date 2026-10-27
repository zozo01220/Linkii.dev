using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Apps Google Slides, Google Docs et Microsoft PowerPoint : manifestes, validation, PDF gardé depuis un flux, mises à jour automatiques (DEV-POC.md §39).</summary>
[Collection("platform")]   // AppPaths est statique
public class CloudFilesTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "linkii-gfiles-" + Guid.NewGuid().ToString("N"));
    private readonly (string Media, string Pdf, string Thumb) before = (AppPaths.MediaDir, AppPaths.PdfDir, AppPaths.ThumbDir);

    public CloudFilesTests()
    {
        Directory.CreateDirectory(AppPaths.MediaDir = Path.Combine(dir, "media"));
        Directory.CreateDirectory(AppPaths.PdfDir = Path.Combine(dir, "pdf"));
        Directory.CreateDirectory(AppPaths.ThumbDir = Path.Combine(dir, "thumbs"));
    }

    public void Dispose()
    {
        (AppPaths.MediaDir, AppPaths.PdfDir, AppPaths.ThumbDir) = before;
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Linkivi";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static AppCatalog Catalog() =>
        new(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Linkii.Poc", "Apps"), NullLogger.Instance);

    private static byte[] OnePagePdf()
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Obj(int n, string body) { offsets.Add(sb.Length); sb.Append($"{n} 0 obj\n{body}\nendobj\n"); }
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 842 595] /Contents 4 0 R >>");
        var content = "0.1 0.3 0.8 rg 20 20 400 300 re f";
        Obj(4, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        var xref = sb.Length;
        sb.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Theory]
    [InlineData("google-slides", "presentation", "10")]
    [InlineData("google-docs", "document", "15")]
    [InlineData("ms-powerpoint", "powerpoint", "10")]
    public void Each_google_app_is_in_the_catalog_with_its_own_file_type(string id, string accept, string seconds)
    {
        var app = Catalog().Find(id);
        Assert.NotNull(app);
        var source = Assert.Single(app!.InZone("source"));
        Assert.Equal("gfile", source.Type);
        Assert.Equal(accept, source.Accept);
        Assert.Equal(seconds, app.Settings.First(f => f.Key == "interval").Default);
        Assert.Equal("60", app.Settings.First(f => f.Key == "refresh").Default);   // vérification toutes les heures
        Assert.Equal("true", app.Settings.First(f => f.Key == "auto").Default);
        Assert.DoesNotContain(app.Settings, f => f.Type == "url");   // pas de lien public
    }

    [Fact]
    public void A_google_file_must_be_exported_and_converted_before_saving()
    {
        var app = Catalog().Find("google-slides")!;
        var values = AppCatalog.WithDefaults(app, null);
        values["gfile"] = "abc";
        Assert.Contains(AppCatalog.Validate(app, values, new HashSet<string>()), e => e.Contains("Présentation Google"));

        var id = Guid.NewGuid().ToString("N");
        File.WriteAllBytes(Path.Combine(AppPaths.PdfDir, id + ".pdf"), OnePagePdf());
        values["pdf"] = id;
        values["pages"] = Guid.NewGuid().ToString();
        Assert.DoesNotContain(AppCatalog.Validate(app, values, new HashSet<string>()), e => e.Contains("Présentation Google"));
    }

    [Fact]
    public async Task An_exported_pdf_is_kept_and_a_failed_update_leaves_the_previous_one()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Linkii:DataDir"] = dir }).Build();
        var pdf = new PdfService(new JsonStore(config, new Env()), NullLogger<PdfService>.Instance);

        using var ok = new MemoryStream(OnePagePdf());
        var saved = await pdf.SaveStream(Guid.NewGuid(), ok, "Journée portes ouvertes");
        Assert.NotNull(saved.Source);
        Assert.Equal(1, saved.Source!.Pages);
        Assert.True(PdfService.Exists(saved.Source.Id));

        using var broken = new MemoryStream(Encoding.ASCII.GetBytes("pas un pdf"));
        var failed = await pdf.SaveStream(Guid.NewGuid(), broken, "Journée portes ouvertes", saved.Source.Id);   // mise à jour d'un fichier déjà gardé
        Assert.Null(failed.Source);
        Assert.NotNull(failed.Error);
        Assert.True(PdfService.Exists(saved.Source.Id));   // l'ancien PDF est conservé
        Assert.Single(Directory.GetFiles(AppPaths.PdfDir, "*", SearchOption.AllDirectories));   // et aucun fichier temporaire ne reste

        using var again = new MemoryStream(OnePagePdf());
        var replaced = await pdf.SaveStream(Guid.NewGuid(), again, "Journée portes ouvertes", saved.Source.Id);
        Assert.Equal(saved.Source.Id, replaced.Source!.Id);   // même identifiant : les images sont remplacées, pas dupliquées
    }

    [Fact]
    public void Slides_name_their_images_by_diapositive_and_docs_by_page()
    {
        Assert.Equal("diapositive", PdfService.Options.From(new Dictionary<string, string> { ["gkind"] = "slides" }).Unit);
        Assert.Equal("page", PdfService.Options.From(new Dictionary<string, string> { ["gkind"] = "docs" }).Unit);
        Assert.Equal("page", PdfService.Options.From(new Dictionary<string, string>()).Unit);
    }

    [Theory]
    [InlineData("15", 15)]
    [InlineData("60", 60)]
    [InlineData("1440", 1440)]
    [InlineData("", 60)]
    [InlineData("7", 60)]
    public void The_check_frequency_defaults_to_one_hour(string setting, int expected) =>
        Assert.Equal(expected, CloudFileService.EveryMinutes(new Dictionary<string, string> { ["refresh"] = setting }));

    [Fact]
    public void All_three_apps_count_as_cloud_apps_and_diaporamas_of_images()
    {
        Assert.True(CloudFileKind.IsCloudApp("google-slides"));
        Assert.True(CloudFileKind.IsCloudApp("google-docs"));
        Assert.True(CloudFileKind.IsCloudApp("ms-powerpoint"));
        Assert.False(CloudFileKind.IsCloudApp("pdf"));
        var item = new PublishedItem { AppId = "google-docs", Urls = new() { "/media/a.jpg", "/media/b.jpg" }, Settings = new() { ["interval"] = "15" } };
        Assert.Equal(30, Helpers.ImagesSeconds(item));
    }

    [Theory]
    [InlineData("Journée.pptx", true)]
    [InlineData("ancien.PPT", true)]
    [InlineData("diaporama.ppsx", true)]
    [InlineData("rapport.docx", false)]
    [InlineData("tableau.xlsx", false)]
    [InlineData("sans-extension", false)]
    public void PowerPoint_files_are_recognised_by_their_extension(string name, bool expected) =>
        Assert.Equal(expected, CloudFileKind.PowerPoint.Matches(name));

    [Fact]
    public void Each_kind_uses_its_own_drive_and_account()
    {
        Assert.Equal("gdrive", CloudFileKind.Slides.Source);
        Assert.Equal("gdrive", CloudFileKind.Docs.Source);
        Assert.Equal("onedrive", CloudFileKind.PowerPoint.Source);
        Assert.Same(CloudFileKind.PowerPoint, CloudFileKind.FromAccept("powerpoint"));
        Assert.Same(CloudFileKind.Slides, CloudFileKind.FromAccept("presentation"));
        Assert.Same(CloudFileKind.Docs, CloudFileKind.FromAccept("document"));

        var none = new Tenant();
        Assert.False(CloudFileKind.Slides.Connected(none));
        Assert.False(CloudFileKind.PowerPoint.Connected(none));
        var microsoft = new Tenant { MsRefreshToken = "x", MsScopes = MicrosoftAuth.DriveScope, MsUser = "a@b.c" };
        Assert.True(CloudFileKind.PowerPoint.Connected(microsoft));
        Assert.False(CloudFileKind.Slides.Connected(microsoft));   // le compte Microsoft ne sert pas à Google Slides
        Assert.Equal("a@b.c", CloudFileKind.PowerPoint.User(microsoft));
    }

    [Fact]
    public void Powerpoint_images_are_named_by_diapositive_and_the_accounts_list_the_three_apps()
    {
        Assert.Equal("diapositive", PdfService.Options.From(new Dictionary<string, string> { ["gkind"] = CloudFileKind.PowerPoint.Gkind }).Unit);
        Assert.Contains("ms-powerpoint", ConnectedAccounts.Kind(ConnectedAccounts.Microsoft).Integrations);
        Assert.Contains("google-slides", ConnectedAccounts.Kind(ConnectedAccounts.Google).Integrations);
        Assert.Contains("google-docs", ConnectedAccounts.Kind(ConnectedAccounts.Google).Integrations);
        Assert.Equal("ms-powerpoint", ConnectedAccounts.Kind(ConnectedAccounts.Microsoft).Integrations.Last());   // les index 0 et 1 restent agendas et fichiers
    }
}