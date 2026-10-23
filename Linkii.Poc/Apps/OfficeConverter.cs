using System.Diagnostics;

namespace Linkii.Poc;

/// <summary>
/// Convertit en PDF un document Office envoyé depuis l'ordinateur (PowerPoint, Word, Excel, OpenDocument, RTF, CSV) avec LibreOffice en ligne de commande,
/// comme <see cref="VideoConverter"/> le fait pour les vidéos avec ffmpeg : outil gratuit installé sur le serveur, sans compte cloud.
/// Détection : LINKII_LIBREOFFICE, puis l'emplacement habituel (Windows, Linux, macOS), puis le PATH (soffice, libreoffice).
/// </summary>
public class OfficeConverter(ILogger<OfficeConverter> log)
{
    private string? path;
    private bool searched;
    private static readonly SemaphoreSlim Gate = new(1, 1);   // une conversion à la fois : LibreOffice est lourd et ne partage pas bien son profil

    /// <summary>Chemin de soffice (null : LibreOffice n'est pas installé sur ce serveur).</summary>
    public string? Find()
    {
        if (searched) return path;
        searched = true;
        var paths = new List<string>();
        if (Environment.GetEnvironmentVariable("LINKII_LIBREOFFICE") is { Length: > 0 } env) paths.Add(env);
        paths.Add(@"C:\Program Files\LibreOffice\program\soffice.exe");
        paths.Add(@"C:\Program Files (x86)\LibreOffice\program\soffice.exe");
        paths.Add("/usr/bin/soffice"); paths.Add("/usr/bin/libreoffice"); paths.Add("/usr/local/bin/soffice"); paths.Add("/opt/libreoffice/program/soffice");
        paths.Add("/Applications/LibreOffice.app/Contents/MacOS/soffice");
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in new[] { "soffice.exe", "soffice", "libreoffice" }) paths.Add(Path.Combine(d.Trim('"'), n));
        path = paths.FirstOrDefault(File.Exists);
        return path;
    }

    public bool Available => Find() != null;

    public const string Missing = "La conversion des documents Office n'est pas installée sur ce serveur (LibreOffice). Enregistrez le document en PDF, ou importez-le depuis OneDrive ou Google Drive.";

    /// <summary>Convertit le fichier en PDF et rend le flux du PDF (à lire puis fermer), ou un message d'erreur.</summary>
    public async Task<(Stream? Pdf, string? Error)> ToPdf(Stream input, string fileName, CancellationToken ct)
    {
        var exe = Find();
        if (exe == null) return (null, Missing);
        var work = Path.Combine(Path.GetTempPath(), "linkii-office-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            var src = Path.Combine(work, "document" + ext);
            await using (var fs = File.Create(src)) await input.CopyToAsync(fs, ct);

            await Gate.WaitAsync(ct);
            try
            {
                var profile = new Uri(Path.Combine(work, "profile")).AbsoluteUri;   // profil jetable : pas de verrou ni de réglages d'un autre lancement
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = work
                };
                foreach (var a in new[] { "--headless", "--norestore", "--nolockcheck", "--nodefault", "--nologo", "-env:UserInstallation=" + profile, "--convert-to", "pdf", "--outdir", work, src })
                    psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi);
                if (proc == null) return (null, "LibreOffice n'a pas pu démarrer.");
                var output = proc.StandardOutput.ReadToEndAsync();
                var errors = proc.StandardError.ReadToEndAsync();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                try { await proc.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(true); } catch (InvalidOperationException) { }
                    if (ct.IsCancellationRequested) throw;
                    return (null, "La conversion du document prend trop de temps : enregistrez-le en PDF puis importez le PDF.");
                }
                var pdf = Path.Combine(work, "document.pdf");
                if (proc.ExitCode != 0 || !File.Exists(pdf))
                {
                    log.LogWarning("LibreOffice : échec de la conversion de {File} (code {Code}) {Out} {Err}", fileName, proc.ExitCode, await output, await errors);
                    return (null, "Ce document n'a pas pu être converti en PDF (format non pris en charge, ou fichier protégé ou abîmé).");
                }
                // le PDF est copié en mémoire : le dossier de travail est supprimé aussitôt
                var ms = new MemoryStream(await File.ReadAllBytesAsync(pdf, ct));
                return (ms, null);
            }
            finally { Gate.Release(); }
        }
        finally { try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
