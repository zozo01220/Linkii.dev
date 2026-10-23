using System.Text.Json;

namespace Linkii.Poc;

/// <summary>
/// Les apps qui affichent un fichier d'un Drive converti en images : « Google Slides » (présentation), « Google Docs » (document)
/// et « Microsoft PowerPoint » (présentation OneDrive ou SharePoint).
/// </summary>
/// <param name="Source">Source Drive du compte connecté : « gdrive » ou « onedrive ».</param>
/// <param name="Mime">Google : type du fichier natif. Microsoft : vide (le type se reconnaît à l'extension, voir <see cref="Matches"/>).</param>
/// <param name="Gkind">« slides » ou « docs » : nom d'une page dans le nom des images (« diapositive » ou « page »).</param>
public record CloudFileKind(string AppId, string Source, string Mime, string Gkind)
{
    public static readonly CloudFileKind Slides = new("google-slides", "gdrive", DriveService.SlidesMime, "slides");
    public static readonly CloudFileKind Docs = new("google-docs", "gdrive", DriveService.DocsMime, "docs");
    public static readonly CloudFileKind PowerPoint = new("ms-powerpoint", "onedrive", "", "slides");

    private static readonly string[] PowerPointExtensions = { ".pptx", ".ppt", ".pptm", ".ppsx", ".pps", ".potx", ".odp" };

    public static CloudFileKind? Of(string? appId) => appId == Slides.AppId ? Slides : appId == Docs.AppId ? Docs : appId == PowerPoint.AppId ? PowerPoint : null;
    public static bool IsCloudApp(string? appId) => Of(appId) != null;

    /// <summary>Type de fichier du champ « gfile » du manifeste (presentation, document, powerpoint).</summary>
    public static CloudFileKind FromAccept(string? accept) => accept == "presentation" ? Slides : accept == "document" ? Docs : PowerPoint;

    public bool IsGoogle => Source == "gdrive";

    /// <summary>Le compte connecté (Google ou Microsoft) a l'accès aux fichiers Drive.</summary>
    public bool Connected(Tenant t) => IsGoogle ? GoogleAuth.Has(t, GoogleAuth.DriveScope) : MicrosoftAuth.Has(t, MicrosoftAuth.DriveScope);

    public string User(Tenant t) => IsGoogle ? t.GoogleUser : t.MsUser;

    /// <summary>Microsoft : ce nom de fichier est une présentation que Graph sait convertir en PDF.</summary>
    public bool Matches(string fileName) => PowerPointExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Google Slides, Google Docs et Microsoft PowerPoint : le fichier est converti en PDF (export Google, conversion Microsoft Graph), gardé sur le serveur
/// (comme un PDF choisi, voir <see cref="PdfService"/>) puis converti en images de la médiathèque, lues comme un diaporama et donc disponibles hors ligne.
/// Valeurs du formulaire : gfile (référence du fichier dans le Drive), gfileName, gfileMod (date de modification au moment de l'export),
/// pdf / pdfTitle / pdfPages / pages (le PDF gardé et ses images, comme l'app Document PDF).
/// </summary>
public class CloudFileService(DriveService drive, PdfService pdf, JsonStore store, Notifier notifier, ILogger<CloudFileService> log)
{
    public enum ImportKind { New, Reused, Updated }

    /// <param name="How">Nouvelle conversion, présentation déjà dans la médiathèque réutilisée en silence, ou fichier modifié (tous les contenus mis à jour).</param>
    /// <param name="Contents">Contenus mis à jour avec la présentation (Updated).</param>
    public record Imported(PdfService.Source Source, PdfService.ConvertResult Result, DriveService.CloudFile File, ImportKind How = ImportKind.New, int Contents = 0);

    /// <summary>
    /// Convertit le fichier puis le passe en images. Une présentation (plusieurs pages) déjà convertie depuis ce même fichier est réutilisée sans rien refaire
    /// tant que le fichier n'a pas changé ; s'il a changé (ou si <paramref name="force"/>), il est converti une seule fois et tous les contenus qui l'affichent
    /// passent aux nouvelles images. <paramref name="reuseId"/> : identifiant du PDF déjà gardé pour ce même fichier (page unique : remplacé, les images
    /// précédentes que rien n'utilise sont retirées).
    /// </summary>
    public async Task<(Imported? Done, string? Error)> Import(Tenant t, Guid clientId, Guid? areaId, CloudFileKind kind, string fileRef, string? reuseId,
                                                                PdfService.Options options, IProgress<string>? progress = null, CancellationToken ct = default, bool force = false)
    {
        try
        {
            progress?.Report("Lecture du fichier dans " + (kind.IsGoogle ? "Google Drive…" : "OneDrive…"));
            var file = await drive.CloudFileInfo(t, kind, fileRef);
            var key = kind.AppId + ":" + fileRef;
            var meta = new PdfService.Meta(key, file.Modified, file.Name);
            var existing = pdf.FindPresentation(clientId, areaId, key) ?? (string.IsNullOrEmpty(reuseId) ? null : pdf.PresentationOfPdf(clientId, reuseId));
            var existingPdf = existing?.Info.GetValueOrDefault("pdfId");

            if (existing != null && !force && existing.Info.GetValueOrDefault("mod") == file.Modified && PdfService.Exists(existingPdf))
            {
                var kept = new PdfService.Source(existingPdf!, existing.Name, Pres.Pages(existing));
                var again = await pdf.Convert(clientId, areaId, kept, options, progress, ct, meta);
                return again.Error != null ? (null, again.Error) : (new(kept, again, file, ImportKind.Reused), null);
            }

            progress?.Report(kind.IsGoogle ? "Export par Google…" : "Conversion par Microsoft…");
            await using var stream = await drive.ExportPdf(t, kind, fileRef, ct);
            var saved = await pdf.SaveStream(stream, file.Name, existingPdf ?? reuseId, ct);
            if (saved.Source is not { } source) return (null, saved.Error ?? "Export refusé.");

            var contents = 0;
            if (existing != null && source.Pages > 1)
            {
                var done = await pdf.Refresh(clientId, existing.Id, source, file.Modified, file.Name, progress, ct);
                if (done.Error != null) return (null, done.Error);
                foreach (var id in done.Contents) await notifier.RefreshPages(clientId, id);   // les écrans reçoivent les nouvelles images sans republier
                pdf.PurgeStale(clientId, existing.Id);
                contents = done.Contents.Count;
            }
            var res = await pdf.Convert(clientId, areaId, source, options, progress, ct, meta);
            if (res.Error != null) { if (existing == null && reuseId != source.Id) pdf.Release(source.Id); return (null, res.Error); }
            return (new(source, res, file, existing != null && source.Pages > 1 ? ImportKind.Updated : ImportKind.New, contents), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Retire un PDF gardé que plus rien n'utilise (sauf s'il appartient à une présentation).</summary>
    public void Release(string? pdfId) => pdf.Release(pdfId);

    /// <summary>Le fichier et ses pages dans les valeurs du formulaire.</summary>
    public static void Apply(IDictionary<string, string> values, Imported done)
    {
        PdfService.Apply(values, done.Source, done.Result);
        values["gfile"] = done.File.Ref;
        values["gfileName"] = done.File.Name;
        values["gfileMod"] = done.File.Modified;
    }

    /// <summary>Le fichier a changé dans le Drive depuis le dernier export (ou ne se lit plus : message).</summary>
    public async Task<(bool Changed, string? Error)> HasChanged(Tenant t, CloudFileKind kind, IReadOnlyDictionary<string, string> values)
    {
        var fileRef = values.GetValueOrDefault("gfile") ?? "";
        if (fileRef.Length == 0) return (false, null);
        try
        {
            var info = await drive.CloudFileInfo(t, kind, fileRef);
            return (info.Modified != (values.GetValueOrDefault("gfileMod") ?? ""), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { return (false, ex.Message); }
    }

    // ---------- Mise à jour automatique ----------

    /// <summary>Minutes entre deux vérifications selon le réglage « refresh » (une heure par défaut).</summary>
    public static int EveryMinutes(IReadOnlyDictionary<string, string> settings) =>
        int.TryParse(settings.GetValueOrDefault("refresh"), out var m) && m is 15 or 60 or 1440 ? m : 60;

    /// <summary>
    /// Vérifie les contenus dont la mise à jour automatique est due : si le fichier a changé dans le Drive, il est converti à nouveau,
    /// ses images remplacées, et les écrans qui l'affichent reçoivent les nouvelles images sans republier.
    /// </summary>
    public async Task CheckAll(CancellationToken ct)
    {
        var due = store.Read(db => db.Clients.Where(c => !c.IsCut)
            .SelectMany(c => db.Media.Where(m => m.ClientId == c.Id && CloudFileKind.Of(m.AppId)?.Connected(c) == true
                    && m.Settings.GetValueOrDefault("gfile") is { Length: > 0 } && m.Settings.GetValueOrDefault("auto") != "false" && IsDue(m))
                .Select(m => (c.Id, m.Id)))
            .ToList());
        foreach (var (clientId, mediaId) in due)
        {
            if (ct.IsCancellationRequested) return;
            try { await Update(clientId, mediaId, force: false, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning(ex, "Mise à jour du contenu {Media}", mediaId); }
        }
    }

    private static bool IsDue(MediaItem m) =>
        !DateTime.TryParse(m.Info.GetValueOrDefault("gcheck"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var last)
        || DateTime.UtcNow - last >= TimeSpan.FromMinutes(EveryMinutes(m.Settings));

    /// <summary>Vérifie un contenu et le reconvertit si le fichier a changé (<paramref name="force"/> : dans tous les cas). Rend un message d'erreur, ou null.</summary>
    public async Task<string?> Update(Guid clientId, Guid mediaId, bool force, CancellationToken ct = default)
    {
        var (t, item) = store.Read(db => (db.Clients.FirstOrDefault(c => c.Id == clientId), db.Media.FirstOrDefault(m => m.Id == mediaId && m.ClientId == clientId)));
        if (t == null || item == null || CloudFileKind.Of(item.AppId) is not { } kind) return null;
        var settings = new Dictionary<string, string>(item.Settings);
        store.Write(db => { if (db.Media.FirstOrDefault(m => m.Id == mediaId) is { } m) m.Info["gcheck"] = DateTime.UtcNow.ToString("o"); });

        var (changed, error) = await HasChanged(t, kind, settings);
        if (error != null) return error;
        if (!changed && !force) return null;

        var (done, err) = await Import(t, clientId, item.AreaId == Guid.Empty ? null : item.AreaId, kind, settings["gfile"], settings.GetValueOrDefault("pdf"), PdfService.Options.From(settings, item.AppId), null, ct, force: true);
        if (done == null) return err;
        if (done.How == ImportKind.Updated)   // présentation : refaite une fois pour tous ses contenus (celui-ci compris), écrans déjà prévenus
        {
            store.Write(db => { if (db.Media.FirstOrDefault(m => m.Id == mediaId) is { } m) m.Info["gcheck"] = DateTime.UtcNow.ToString("o"); });
            return null;
        }

        var keep = done.Result.Files.Select(f => f.Id).ToHashSet();
        var fresh = new Dictionary<string, string>(settings);
        Apply(fresh, done);
        store.Write(db =>
        {
            var m = db.Media.FirstOrDefault(x => x.Id == mediaId);
            if (m == null) return;
            foreach (var (k, v) in fresh) m.Settings[k] = v;
            m.Info["gcheck"] = DateTime.UtcNow.ToString("o");
        });
        await notifier.RefreshPages(clientId, mediaId);
        pdf.PurgeUnused(clientId, done.Source.Id, keep);   // les anciennes images, une fois les écrans passés aux nouvelles
        return null;
    }
}

/// <summary>Vérifie toutes les cinq minutes quels contenus Google Slides, Google Docs et Microsoft PowerPoint doivent être mis à jour (chacun a sa propre fréquence).</summary>
public class CloudFilesWorker(CloudFileService files, ILogger<CloudFilesWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stop); } catch (TaskCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try { await files.CheckAll(stop); }
            catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogError(ex, "Mise à jour des fichiers Google et Microsoft"); }
        } while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
    }
}
