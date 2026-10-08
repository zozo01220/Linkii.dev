using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>Une source Drive : ce qu'on affiche dans Intégrations.</summary>
public record DriveSourceDef(string Id, string Name, string Summary, string LinkPlaceholder);

/// <summary>
/// Drives : des dossiers Google Drive ou OneDrive / SharePoint copiés dans la médiathèque (fichiers masqués) et affichés par
/// l'app « Dossier Drive ». Les fichiers sont résolus à la publication puis tenus à jour à chaque synchronisation, sans republier.
/// </summary>
public static class DriveSources
{
    public static readonly DriveSourceDef[] All =
    {
        new("gdrive", "Google Drive", "Dossiers de votre compte Google, y compris les Drive partagés (lecture seule).",
            "https://drive.google.com/drive/folders/…"),
        new("onedrive", "OneDrive et SharePoint", "Dossiers OneDrive ou bibliothèques SharePoint de votre organisation.",
            "https://organisation.sharepoint.com/:f:/s/…"),
    };

    public static DriveSourceDef? Find(string? id) => All.FirstOrDefault(s => s.Id == id);
    public static int Order(string source) => Array.FindIndex(All, s => s.Id == source);
    public static bool Enabled(Tenant t, string source) => t.DrivesEnabled.Contains(source);

    /// <summary>
    /// Google Drive : compte Google connecté par l'organisation, avec l'accès à Drive ;
    /// OneDrive : connexion Microsoft 365 du client (partagée avec les calendriers).
    /// </summary>
    public static bool Configured(Tenant t, string source) =>
        source == "gdrive" ? GoogleAuth.Has(t, GoogleAuth.DriveScope) : GraphService.Configured(t);

    /// <summary>Dossiers proposés pour un nouveau contenu : ceux des sources actives et configurées.</summary>
    public static IEnumerable<DriveFolder> Offered(Tenant t) => t.DriveFolders
        .Where(f => Enabled(t, f.Source) && Configured(t, f.Source))
        .OrderBy(f => Order(f.Source)).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Fichiers d'un dossier synchronisé, prêts et dans l'ordre choisi : ce que l'écran affiche (adresses /media/…).</summary>
    public static List<string> Urls(IEnumerable<MediaItem> media, Guid clientId, IReadOnlyDictionary<string, string> settings)
    {
        var folder = settings.GetValueOrDefault("folder") ?? "";
        var kinds = settings.GetValueOrDefault("kinds") ?? "all";
        var files = media.Where(m => m.ClientId == clientId && m.Info.GetValueOrDefault("drive") == folder && m.Status == null && m.FileName != null
                                     && (kinds == "all" || m.Type == (kinds == "videos" ? "video" : "image")));
        files = settings.GetValueOrDefault("order") == "date"
            ? files.OrderByDescending(m => m.Info.GetValueOrDefault("driveTime"), StringComparer.Ordinal)
            : files.OrderBy(m => m.Info.GetValueOrDefault("driveName") ?? m.Name, StringComparer.CurrentCultureIgnoreCase);
        return files.Select(m => "/media/" + m.FileName).ToList();
    }

    /// <summary>« il y a 6 min », « il y a 2 h », « le 07.10 ».</summary>
    public static string Ago(DateTime? utc)
    {
        if (utc is not { } at) return "jamais synchronisé";
        var d = DateTime.UtcNow - at;
        return d.TotalMinutes < 1 ? "à l'instant" : d.TotalHours < 1 ? $"il y a {(int)d.TotalMinutes} min" : d.TotalDays < 1 ? $"il y a {(int)d.TotalHours} h" : $"le {at.ToLocalTime():dd.MM}";
    }
}

/// <summary>Lecture des Drives (Google Drive API v3, Microsoft Graph) et synchronisation des dossiers dans la médiathèque.</summary>
public class DriveService(JsonStore store, GoogleAuth googleAuth, GraphService graph, SafeHttp http,
                          MediaImporter importer, Notifier notifier, ILogger<DriveService> log)
{
    public const int MaxFiles = 300;              // par dossier
    public const long MaxFileBytes = 1_000_000_000;

    private const string GoogleApi = "https://www.googleapis.com/drive/v3/files";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();   // un dossier n'est jamais synchronisé deux fois en même temps

    // ResourceKey : clé de sécurité de certains liens Google partagés avant 2021 (« resourcekey= » dans le lien), à renvoyer avec chaque appel.
    private record RemoteFile(string Id, string Name, string Mime, long Size, string Version, string Time, string? DownloadUrl, string? ResourceKey = null);

    // ---------- Lien collé par l'utilisateur → dossier ----------

    /// <summary>
    /// Vérifie le lien de partage d'un dossier et rend son identifiant, son nom et son emplacement.
    /// Google : « dossierId » ou « dossierId/resourceKey » ; Microsoft : « driveId/itemId ».
    /// </summary>
    public async Task<(string RemoteId, string Name, string Location)> Resolve(Tenant t, string source, string link)
    {
        link = link.Trim();
        if (source == "gdrive")
        {
            var id = Regex.Match(link, @"/folders/([\w-]{10,})").Groups[1].Value;
            if (id.Length == 0) id = Regex.Match(link, @"[?&]id=([\w-]{10,})").Groups[1].Value;
            if (id.Length == 0 && Regex.IsMatch(link, @"^[\w-]{10,}$")) id = link;
            if (id.Length == 0) throw new InvalidOperationException("Lien non reconnu : collez le lien d'un dossier Google Drive (…/drive/folders/…).");
            var rk = Regex.Match(link, @"[?&]resourcekey=([\w-]+)", RegexOptions.IgnoreCase).Groups[1].Value;
            using var doc = await GoogleGet(t, $"{GoogleApi}/{id}?fields=id,name,mimeType&supportsAllDrives=true", Keys(id, rk));
            if (doc.RootElement.GetProperty("mimeType").GetString() != FolderMime) throw new InvalidOperationException("Ce lien mène à un fichier, pas à un dossier.");
            var name = doc.RootElement.GetProperty("name").GetString() ?? "Dossier";
            return (rk.Length > 0 ? id + "/" + rk : id, name, "Google Drive › " + name);
        }

        if (!AppCatalog.IsHttpUrl(link)) throw new InvalidOperationException("Collez le lien de partage du dossier (https://…).");
        var share = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(link)).TrimEnd('=').Replace('/', '_').Replace('+', '-');
        using var item = await GraphGet(t, $"https://graph.microsoft.com/v1.0/shares/{share}/driveItem?$select=id,name,folder,parentReference");
        var r = item.RootElement;
        if (!r.TryGetProperty("folder", out _)) throw new InvalidOperationException("Ce lien mène à un fichier, pas à un dossier.");
        var parent = r.GetProperty("parentReference");
        var path = parent.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
        path = path.Contains("root:") ? Uri.UnescapeDataString(path[(path.IndexOf("root:", StringComparison.Ordinal) + 5)..]).Trim('/') : "";
        var folderName = r.GetProperty("name").GetString() ?? "Dossier";
        return (parent.GetProperty("driveId").GetString() + "/" + r.GetProperty("id").GetString(), folderName,
                string.Join(" › ", new[] { "OneDrive", path.Replace("/", " › "), folderName }.Where(x => x.Length > 0)));
    }

    /// <summary>
    /// Vérifie la connexion : lit le premier dossier de la source s'il y en a un, sinon interroge OneDrive (accès aux fichiers).
    /// Google Drive n'a pas de connexion : c'est l'ajout d'un dossier qui vérifie son lien.
    /// </summary>
    public async Task<string> TestConnection(Tenant t, string source)
    {
        if (t.DriveFolders.FirstOrDefault(f => f.Source == source) is { } first)
        {
            var files = await List(t, first);
            return $"Connexion réussie · « {first.Name} » : {files.Count} image{(files.Count > 1 ? "s" : "")} ou vidéo{(files.Count > 1 ? "s" : "")} lisible{(files.Count > 1 ? "s" : "")}.";
        }
        if (source == "gdrive")
        {
            if (!GoogleAuth.Has(t, GoogleAuth.DriveScope)) return "Ajoutez un dossier : son lien est vérifié à l'ajout.";
            using var me = await GoogleGet(t, "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)", new());
            return $"Connexion réussie : Google Drive répond pour {(t.GoogleUser.Length > 0 ? t.GoogleUser : "le compte connecté")}. Ajoutez vos dossiers.";
        }
        using var _ = await GraphGet(t, "https://graph.microsoft.com/v1.0/sites/root/drive?$select=id");   // avec Sites.Selected, seul l'ajout d'un dossier autorisé peut le confirmer
        return "Connexion réussie : OneDrive et SharePoint sont accessibles. Ajoutez vos dossiers.";
    }

    // ---------- Synchronisation ----------

    /// <summary>Synchronise tous les dossiers des sources actives (tâche de fond, toutes les 15 minutes).</summary>
    public async Task SyncAll(CancellationToken ct)
    {
        var work = store.Read(db => db.Clients.Where(c => !c.Suspended)
            .SelectMany(c => c.DriveFolders.Where(f => DriveSources.Enabled(c, f.Source) && DriveSources.Configured(c, f.Source)).Select(f => (c.Id, f.Id)))
            .ToList());
        foreach (var (clientId, folderId) in work)
        {
            if (ct.IsCancellationRequested) return;
            await Sync(clientId, folderId, ct);
        }
    }

    /// <summary>
    /// Copie dans la médiathèque les images et vidéos nouvelles ou modifiées du dossier, retire celles qui n'y sont plus,
    /// puis met à jour les écrans qui l'affichent. Rend un message d'erreur, ou null si tout s'est bien passé.
    /// </summary>
    public async Task<string?> Sync(Guid clientId, Guid folderId, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(folderId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var (t, folder) = store.Read(db =>
            {
                var c = db.Clients.FirstOrDefault(x => x.Id == clientId);
                return (c, c?.DriveFolders.FirstOrDefault(f => f.Id == folderId));
            });
            if (t == null || folder == null) return null;   // dossier retiré entre-temps

            List<RemoteFile> remote;
            try { remote = await List(t, folder); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
            {
                Record(clientId, folderId, ex.Message, null);
                return ex.Message;
            }

            var key = folderId.ToString();
            var local = store.Read(db => db.Media.Where(m => m.ClientId == clientId && m.Info.GetValueOrDefault("drive") == key).ToList());
            var changed = false;
            string? error = null;

            // retirés du dossier, ou modifiés (remplacés ci-dessous)
            var keep = remote.ToDictionary(f => f.Id, f => f.Version);
            var stale = local.Where(m => !keep.TryGetValue(m.Info.GetValueOrDefault("driveFile") ?? "", out var v) || v != m.Info.GetValueOrDefault("driveVer")).ToList();
            if (stale.Count > 0) { Delete(stale); changed = true; }

            var present = local.Except(stale).Select(m => m.Info.GetValueOrDefault("driveFile")).ToHashSet();
            foreach (var f in remote.Where(f => !present.Contains(f.Id)))
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    using var res = f.DownloadUrl != null
                        ? await http.Download(f.DownloadUrl, ct)   // Microsoft : adresse de téléchargement déjà authentifiée
                        : await GoogleDownload(t, $"{GoogleApi}/{f.Id}?alt=media&supportsAllDrives=true", Keys(f.Id, f.ResourceKey), ct);
                    if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"« {f.Name} » : téléchargement refusé ({(int)res.StatusCode}).");
                    await using var s = await res.Content.ReadAsStreamAsync(ct);
                    var imported = await importer.ImportStream(s, f.Name, clientId, ct);
                    if (imported.Item is not { } item) throw new InvalidOperationException(imported.Error ?? $"« {f.Name} » : import impossible.");
                    store.Write(db =>
                    {
                        item.Info["drive"] = key; item.Info["driveFile"] = f.Id; item.Info["driveVer"] = f.Version;
                        item.Info["driveName"] = f.Name; item.Info["driveTime"] = f.Time;
                    });
                    changed = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    error ??= ex.Message;   // un fichier en échec n'empêche pas les autres ; il sera retenté à la synchronisation suivante
                    log.LogWarning("Drive {Folder} : {Error}", folderId, ex.Message);
                }
            }

            Record(clientId, folderId, error ?? "", remote.Count);
            if (changed) await notifier.RefreshDrive(clientId, folderId);
            return error;
        }
        finally { gate.Release(); }
    }

    /// <summary>Retire un dossier : ses fichiers quittent la médiathèque et les écrans qui l'affichaient.</summary>
    public async Task RemoveFolder(Guid clientId, Guid folderId)
    {
        var key = folderId.ToString();
        store.Write(db => { db.Clients.First(c => c.Id == clientId).DriveFolders.RemoveAll(f => f.Id == folderId); });
        Delete(store.Read(db => db.Media.Where(m => m.ClientId == clientId && m.Info.GetValueOrDefault("drive") == key).ToList()));
        await notifier.RefreshDrive(clientId, folderId);
    }

    private void Delete(List<MediaItem> items)
    {
        var ids = items.Select(m => m.Id).ToHashSet();
        store.Write(db => { db.Media.RemoveAll(m => ids.Contains(m.Id)); });
        foreach (var m in items.Where(m => m.FileName != null))
            try { File.Delete(Path.Combine(AppPaths.MediaDir, m.FileName!)); } catch (IOException) { }
    }

    private void Record(Guid clientId, Guid folderId, string error, int? count) => store.Write(db =>
    {
        var f = db.Clients.FirstOrDefault(c => c.Id == clientId)?.DriveFolders.FirstOrDefault(x => x.Id == folderId);
        if (f == null) return;
        f.LastError = error;
        f.SyncedUtc = DateTime.UtcNow;
        if (count is { } n) f.FileCount = n;
    });

    // ---------- Contenu d'un dossier ----------

    /// <summary>Images et vidéos du dossier (sous-dossiers non parcourus), au plus <see cref="MaxFiles"/>.</summary>
    private async Task<List<RemoteFile>> List(Tenant t, DriveFolder folder)
    {
        var files = new List<RemoteFile>();
        if (folder.Source == "gdrive")
        {
            var parts = folder.RemoteId.Split('/');   // « dossierId » ou « dossierId/resourceKey »
            string? page = null;
            do
            {
                var q = Uri.EscapeDataString($"'{parts[0]}' in parents and trashed = false");
                using var doc = await GoogleGet(t, $"{GoogleApi}?q={q}&pageSize=200&supportsAllDrives=true&includeItemsFromAllDrives=true" +
                    "&fields=nextPageToken,files(id,name,mimeType,size,modifiedTime,md5Checksum,resourceKey)" + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : ""),
                    Keys(parts[0], parts.Length > 1 ? parts[1] : null));
                foreach (var f in doc.RootElement.GetProperty("files").EnumerateArray())
                {
                    var time = f.TryGetProperty("modifiedTime", out var mt) ? mt.GetString() ?? "" : "";
                    files.Add(new(f.GetProperty("id").GetString()!, f.GetProperty("name").GetString() ?? "", f.GetProperty("mimeType").GetString() ?? "",
                        f.TryGetProperty("size", out var sz) && long.TryParse(sz.GetString(), out var n) ? n : 0,
                        f.TryGetProperty("md5Checksum", out var md5) ? md5.GetString() ?? time : time, time, null,
                        f.TryGetProperty("resourceKey", out var rk) ? rk.GetString() : null));
                }
                page = doc.RootElement.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
            } while (page != null && files.Count < MaxFiles * 2);
        }
        else
        {
            var parts = folder.RemoteId.Split('/');
            string? url = $"https://graph.microsoft.com/v1.0/drives/{parts[0]}/items/{parts[^1]}/children?$top=200";
            while (url != null && files.Count < MaxFiles * 2)
            {
                using var doc = await GraphGet(t, url);
                foreach (var f in doc.RootElement.GetProperty("value").EnumerateArray())
                {
                    if (!f.TryGetProperty("file", out var file)) continue;   // sous-dossier
                    var time = f.TryGetProperty("lastModifiedDateTime", out var mt) ? mt.GetString() ?? "" : "";
                    files.Add(new(f.GetProperty("id").GetString()!, f.GetProperty("name").GetString() ?? "",
                        file.TryGetProperty("mimeType", out var mime) ? mime.GetString() ?? "" : "",
                        f.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                        f.TryGetProperty("cTag", out var tag) ? tag.GetString() ?? time : time, time,
                        f.TryGetProperty("@microsoft.graph.downloadUrl", out var dl) ? dl.GetString() : null));
                }
                url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            }
        }
        // images et vidéos lisibles par les écrans (formats de la médiathèque), taille plafonnée
        return files.Where(f => f.Mime.StartsWith("image/") || f.Mime.StartsWith("video/"))
            .Where(f => MediaImporter.IsSupported(f.Name) && f.Size <= MaxFileBytes)
            .Where(f => f.DownloadUrl != null || folder.Source == "gdrive")
            .Take(MaxFiles)
            .ToList();
    }

    // ---------- Appels authentifiés ----------

    // Google Drive : avec le compte Google connecté par l'organisation (jeton OAuth, lecture seule).
    private async Task<Dictionary<string, string>> GoogleHeaders(Tenant t, Dictionary<string, string> headers)
    {
        if (!GoogleAuth.Has(t, GoogleAuth.DriveScope)) throw new InvalidOperationException("Google Drive n'est pas connecté : connectez votre compte Google dans Intégrations › Google Drive.");
        try { headers["Authorization"] = "Bearer " + await googleAuth.AccessToken(t); }
        catch (GoogleAuth.GoogleAuthException ex) { throw new InvalidOperationException(ex.Message); }
        return headers;
    }

    /// <summary>En-tête des clés de sécurité (« X-Goog-Drive-Resource-Keys ») pour les liens qui en portent une.</summary>
    private static Dictionary<string, string> Keys(string id, string? resourceKey) =>
        string.IsNullOrEmpty(resourceKey) ? new() : new() { ["X-Goog-Drive-Resource-Keys"] = id + "/" + resourceKey };

    private async Task<HttpResponseMessage> GoogleDownload(Tenant t, string url, Dictionary<string, string> headers, CancellationToken ct) =>
        await http.Download(url, await GoogleHeaders(t, headers), ct);

    private async Task<JsonDocument> GoogleGet(Tenant t, string url, Dictionary<string, string> headers)
    {
        var (status, text) = await http.Request(HttpMethod.Get, url, headers: await GoogleHeaders(t, headers), maxBytes: 4_000_000);
        if (status is 404 or 403 && !text.Contains("accessNotConfigured") && !text.Contains("insufficient") && !text.Contains("rateLimit"))
            throw new InvalidOperationException($"Dossier introuvable : vérifiez le lien et que le compte {(t.GoogleUser.Length > 0 ? t.GoogleUser : "Google connecté")} y a accès.");
        if (status == 401) throw new InvalidOperationException("Google a refusé l'accès : reconnectez votre compte dans Intégrations › Google Drive.");
        if (status == 403 && text.Contains("accessNotConfigured")) throw new InvalidOperationException("L'API Google Drive n'est pas activée pour l'application Google de la plateforme (administrateur Linkii).");
        if (status == 403 && text.Contains("insufficient")) throw new InvalidOperationException("Accès à Drive non accordé : reconnectez votre compte dans Intégrations › Google Drive.");
        if (status != 200) throw new InvalidOperationException($"Google Drive ({status}) : {GoogleConnector.Describe(text)}");
        return JsonDocument.Parse(text);
    }

    private async Task<JsonDocument> GraphGet(Tenant t, string url)
    {
        try { return await graph.Get(t, url); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("(403)") || ex.Message.Contains("(401)"))
        {
            throw new InvalidOperationException("Accès refusé : ajoutez l'autorisation d'application Files.Read.All (ou Sites.Selected) à l'application Entra ID, puis le consentement administrateur.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("(404)"))
        {
            throw new InvalidOperationException("Dossier introuvable : vérifiez le lien de partage.");
        }
    }
}

/// <summary>Synchronise les dossiers des Drives toutes les 15 minutes (première passe une minute après le démarrage).</summary>
public class DriveSyncWorker(DriveService drives, ILogger<DriveSyncWorker> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stop); } catch (TaskCanceledException) { return; }
        using var timer = new PeriodicTimer(Every);
        do
        {
            try { await drives.SyncAll(stop); }
            catch (Exception ex) when (!stop.IsCancellationRequested) { log.LogError(ex, "Synchronisation des Drives"); }
        } while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
    }
}
