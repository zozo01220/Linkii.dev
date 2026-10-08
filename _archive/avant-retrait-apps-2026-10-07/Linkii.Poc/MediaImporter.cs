using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components.Forms;

namespace Linkii.Poc;

/// <summary>
/// Import des fichiers de la médiathèque (images et vidéos uniquement), depuis l'ordinateur ou depuis une adresse web.
/// Les images et les MP4 sont publiés tels quels ; toute autre vidéo est convertie en arrière-plan en MP4 H.264 (statut « processing » puis prête).
/// La progression est rapportée en octets reçus.
/// </summary>
public class MediaImporter(JsonStore store, VideoConverter converter, SafeHttp http)
{
    public const long MaxBytes = 2_000_000_000;

    public record Result(MediaItem? Item, string? Error);

    public static bool IsSupported(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return VideoConverter.ImageExts.Contains(ext) || VideoConverter.IsVideo(ext);
    }

    /// <summary>Fichier choisi ou déposé dans le navigateur.</summary>
    public async Task<Result> Import(IBrowserFile f, Guid clientId, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (f.Size > MaxBytes) return new(null, $"« {f.Name} » dépasse la taille maximale (2 Go).");
        await using var s = f.OpenReadStream(MaxBytes, ct);
        return await Import(s, f.Name, clientId, progress, ct);
    }

    /// <summary>Fichier téléchargé par le serveur depuis une adresse publique (jamais le réseau interne : <see cref="SafeHttp"/>).</summary>
    public async Task<Result> ImportUrl(string url, Guid clientId, Action<long, long?>? progress = null, CancellationToken ct = default)
    {
        HttpResponseMessage res;
        try { res = await http.Download(url, ct); }
        catch (InvalidOperationException ex) { return new(null, ex.Message); }
        catch (HttpRequestException ex) { return new(null, "Adresse injoignable : " + ex.Message); }
        using (res)
        {
            if (!res.IsSuccessStatusCode) return new(null, $"Le serveur distant a répondu {(int)res.StatusCode}.");
            var total = res.Content.Headers.ContentLength;
            if (total > MaxBytes) return new(null, "Fichier trop volumineux (2 Go maximum).");
            var name = FileNameFor(res, url);
            if (!IsSupported(name)) return new(null, "Cette adresse ne mène pas à une image ou une vidéo (formats acceptés : JPG, PNG, GIF, WEBP, SVG, MP4, MOV, AVI, MKV…).");
            await using var s = await res.Content.ReadAsStreamAsync(ct);
            return await Import(s, name, clientId, progress == null ? null : new Progress<long>(n => progress(n, total)), ct);
        }
    }

    // Nom du fichier : en-tête Content-Disposition, sinon fin de l'adresse ; extension déduite du type si elle manque.
    private static string FileNameFor(HttpResponseMessage res, string url)
    {
        var name = res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName;
        name = name?.Trim('"');
        if (string.IsNullOrWhiteSpace(name))
        {
            var path = res.RequestMessage?.RequestUri?.AbsolutePath ?? new Uri(url).AbsolutePath;
            name = Uri.UnescapeDataString(Path.GetFileName(path));
        }
        if (string.IsNullOrWhiteSpace(name)) name = "media";
        name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        if (!IsSupported(name) && ExtFor(res.Content.Headers.ContentType) is { } ext) name = Path.GetFileNameWithoutExtension(name) + ext;
        return name.Length > 120 ? name[^120..] : name;
    }

    private static string? ExtFor(MediaTypeHeaderValue? t) => t?.MediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg", "image/png" => ".png", "image/gif" => ".gif", "image/webp" => ".webp", "image/svg+xml" => ".svg",
        "video/mp4" => ".mp4", "video/webm" => ".webm", "video/quicktime" => ".mov", "video/x-msvideo" => ".avi", "video/x-matroska" => ".mkv",
        _ => null
    };

    private async Task<Result> Import(Stream input, string name, Guid clientId, IProgress<long>? progress, CancellationToken ct)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var isImage = VideoConverter.ImageExts.Contains(ext);
        var isVideo = VideoConverter.IsVideo(ext);
        if (!isImage && !isVideo) return new(null, $"Format non supporté : {name}");

        var needsConversion = isVideo && VideoConverter.NeedsConversion(ext, false);
        if (needsConversion && converter.Find() == null)
            return new(null, $"« {name} » doit être converti, mais aucun convertisseur n'est installé sur le serveur (ffmpeg ou VLC). Importez un MP4 H.264 ou installez-en un.");

        var id = Guid.NewGuid().ToString("N");
        var source = Path.Combine(AppPaths.MediaDir, id + ext);
        long written = 0;
        try
        {
            await using var fs = File.Create(source);
            var buf = new byte[256 * 1024];
            int n;
            var last = DateTime.MinValue;
            while ((n = await input.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                written += n;
                if (written > MaxBytes) throw new InvalidOperationException("Fichier trop volumineux (2 Go maximum).");
                if (DateTime.UtcNow - last > TimeSpan.FromMilliseconds(150)) { last = DateTime.UtcNow; progress?.Report(written); }   // pas plus de ~7 mises à jour par seconde
            }
            progress?.Report(written);
        }
        catch
        {
            try { File.Delete(source); } catch { }   // import annulé ou interrompu : pas de fichier orphelin
            throw;
        }

        var title = Path.GetFileNameWithoutExtension(name);
        if (!needsConversion)
        {
            var item = new MediaItem { ClientId = clientId, Name = title, Type = isVideo ? "video" : "image", FileName = id + ext, Size = written, AddedUtc = DateTime.UtcNow };
            store.Write(d => { d.Media.Add(item); });
            return new(item, null);
        }

        var pending = new MediaItem { ClientId = clientId, Name = title, Type = "video", FileName = null, Status = "processing", Size = written, AddedUtc = DateTime.UtcNow };
        store.Write(d => { d.Media.Add(pending); });
        _ = Task.Run(() => ConvertInBackground(pending, source, id));
        return new(pending, null);
    }

    private async Task ConvertInBackground(MediaItem item, string source, string id)
    {
        var output = Path.Combine(AppPaths.MediaDir, id + ".mp4");
        string? error;
        try { error = await converter.ConvertAsync(source, output + ".part"); }
        catch (Exception ex) { error = ex.Message; }

        if (error == null)
        {
            try { File.Move(output + ".part", output, true); } catch (Exception ex) { error = "Fichier converti inutilisable : " + ex.Message; }
        }
        if (error == null)
        {
            try { File.Delete(source); } catch { }   // on ne garde que le MP4
            var size = new FileInfo(output).Length;
            store.Write(d => { item.FileName = id + ".mp4"; item.Status = null; item.Error = null; item.Size = size; });
        }
        else
        {
            try { File.Delete(source); } catch { }
            store.Write(d => { item.Status = "failed"; item.Error = error; });
        }
    }
}
