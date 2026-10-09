using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>Une source Drive : ce qu'on affiche dans Intégrations.</summary>
public record DriveSourceDef(string Id, string Name, string Summary, string LinkPlaceholder);

/// <summary>Un emplacement de départ du sélecteur de dossier (« Mon Drive », « Partagés avec moi »…).</summary>
/// <param name="Location">Début de l'emplacement des dossiers qu'on y trouve (« Mon Drive › … »).</param>
/// <param name="Link">Pas de liste : on y colle le lien d'un dossier (SharePoint).</param>
public record DriveRoot(string Key, string Label, string Icon, string Location, bool Link = false);

/// <summary>Un dossier ou un fichier vu dans le sélecteur.</summary>
/// <param name="Ref">Dossier : référence à garder (<see cref="DriveFolder.RemoteId"/>) ; fichier : identifiant.</param>
/// <param name="Kind">folder | image | video</param>
/// <param name="Location">Emplacement complet, connu seulement pour les résultats de recherche.</param>
public record DriveNode(string Ref, string Name, string Kind, string Info, string WebUrl, string? Location = null);

/// <summary>Contenu d'un dossier pour le sélecteur. <paramref name="Ref"/> est null pour une liste qu'on ne peut pas ajouter (« Partagés avec moi »).</summary>
public record DriveListing(string? Ref, string WebUrl, List<DriveNode> Folders, List<DriveNode> Files, int Others, bool More);

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
    /// Emplacements du sélecteur. SharePoint : lister les sites demanderait un accès de plus (Sites.Read.All) ;
    /// on colle le lien d'une bibliothèque, puis on la parcourt. Les dossiers SharePoint partagés avec le compte sont dans « Partagés avec moi ».
    /// </summary>
    public static DriveRoot[] Roots(string source) => source == "gdrive"
        ? new DriveRoot[] { new("my", "Mon Drive", "drive", "Mon Drive"), new("shared", "Partagés avec moi", "users", "Partagés avec moi"), new("drives", "Drive partagés", "team", "Drive partagés") }
        : new DriveRoot[] { new("my", "Mon OneDrive", "cloud", "OneDrive"), new("shared", "Partagés avec moi", "users", "Partagés avec moi"), new("sp", "SharePoint", "site", "SharePoint", Link: true) };

    /// <summary>
    /// Google Drive : compte Google connecté par l'organisation, avec l'accès à Drive ;
    /// OneDrive : compte Microsoft connecté par l'organisation, avec l'accès aux fichiers (partagé avec les calendriers).
    /// </summary>
    public static bool Configured(Tenant t, string source) =>
        source == "gdrive" ? GoogleAuth.Has(t, GoogleAuth.DriveScope) : MicrosoftAuth.Has(t, MicrosoftAuth.DriveScope);

    /// <summary>Dossiers proposés pour un nouveau contenu : ceux des sources actives et configurées.</summary>
    public static IEnumerable<DriveFolder> Offered(Tenant t) => t.DriveFolders
        .Where(f => Enabled(t, f.Source) && Configured(t, f.Source))
        .OrderBy(f => Order(f.Source)).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Même dossier distant ? (Google : la clé de sécurité éventuelle ne compte pas.)</summary>
    public static bool SameRemote(string source, string a, string b) => source == "gdrive" ? a.Split('/')[0] == b.Split('/')[0] : a == b;

    /// <summary>Adresse du dossier dans Google Drive ou OneDrive, pour « Ouvrir dans… ».</summary>
    public static string WebUrl(DriveFolder f)
    {
        if (f.WebUrl.Length > 0 || f.Source != "gdrive") return f.WebUrl;
        var p = f.RemoteId.Split('/');
        return p[0] == "root" ? "https://drive.google.com/drive/my-drive"
            : $"https://drive.google.com/drive/folders/{p[0]}" + (p.Length > 1 ? "?resourcekey=" + p[1] : "");
    }

    /// <summary>
    /// Fichiers d'un dossier synchronisé, prêts et dans l'ordre choisi : ce que l'écran affiche (adresses /media/…).
    /// Par nom : selon le chemin dans le dossier (sous-dossier puis fichier).
    /// </summary>
    public static List<string> Urls(IEnumerable<MediaItem> media, Guid clientId, IReadOnlyDictionary<string, string> settings)
    {
        var folder = settings.GetValueOrDefault("folder") ?? "";
        var kinds = settings.GetValueOrDefault("kinds") ?? "all";
        var files = media.Where(m => m.ClientId == clientId && m.Info.GetValueOrDefault("drive") == folder && m.Status == null && m.FileName != null
                                     && (kinds == "all" || m.Type == (kinds == "videos" ? "video" : "image")));
        files = settings.GetValueOrDefault("order") == "date"
            ? files.OrderByDescending(m => m.Info.GetValueOrDefault("driveTime"), StringComparer.Ordinal)
            : files.OrderBy(m => m.Info.GetValueOrDefault("drivePath") ?? m.Info.GetValueOrDefault("driveName") ?? m.Name, StringComparer.CurrentCultureIgnoreCase);
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
    public const int MaxFiles = 300;              // par dossier, sous-dossiers compris
    public const long MaxFileBytes = 1_000_000_000;
    private const int MaxFolders = 200;           // sous-dossiers parcourus au plus, tous niveaux confondus
    private const int MaxEntries = 5000;          // éléments lus au plus par synchronisation
    private const int BrowseMax = 600;            // éléments lus au plus pour un dossier du sélecteur

    private const string GoogleApi = "https://www.googleapis.com/drive/v3/files";
    private const string GoogleFields = "id,name,mimeType,size,modifiedTime,md5Checksum,resourceKey,webViewLink,sharingUser(emailAddress)";
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();   // un dossier n'est jamais synchronisé deux fois en même temps

    /// <summary>
    /// Un élément d'un Drive. Ref : de quoi relire un dossier ou télécharger un fichier — Google : « id » ou « id/resourceKey »
    /// (clé de sécurité de certains liens partagés avant 2021, à renvoyer avec chaque appel) ; Microsoft : « driveId/itemId ».
    /// Where : emplacement du parent (Microsoft) ; Path : chemin dans le dossier synchronisé (« 2026/affiche.jpg »).
    /// </summary>
    private record Item(string Id, string Ref, string Name, bool Folder, string Mime, long Size, string Version, string Time,
                        string? DownloadUrl, string WebUrl, string SharedBy, string Where = "", string Path = "");

    /// <summary>Fichiers retenus d'un dossier synchronisé, ceux laissés de côté au-delà de <see cref="MaxFiles"/>, sous-dossiers vus.</summary>
    private record Listed(List<Item> Files, int Skipped, int Subfolders);

    // ---------- Lien collé par l'utilisateur → dossier ----------

    /// <summary>
    /// Vérifie le lien de partage d'un dossier et rend son identifiant, son nom, son emplacement et son adresse web.
    /// Google : « dossierId » ou « dossierId/resourceKey » ; Microsoft : « driveId/itemId ».
    /// </summary>
    public async Task<(string RemoteId, string Name, string Location, string WebUrl)> Resolve(Tenant t, string source, string link)
    {
        link = link.Trim();
        if (source == "gdrive")
        {
            var id = Regex.Match(link, @"/folders/([\w-]{10,})").Groups[1].Value;
            if (id.Length == 0) id = Regex.Match(link, @"[?&]id=([\w-]{10,})").Groups[1].Value;
            if (id.Length == 0 && Regex.IsMatch(link, @"^[\w-]{10,}$")) id = link;
            if (id.Length == 0) throw new InvalidOperationException("Lien non reconnu : collez le lien d'un dossier Google Drive (…/drive/folders/…).");
            var rk = Regex.Match(link, @"[?&]resourcekey=([\w-]+)", RegexOptions.IgnoreCase).Groups[1].Value;
            using var doc = await GoogleGet(t, $"{GoogleApi}/{id}?fields=id,name,mimeType,webViewLink&supportsAllDrives=true", Keys(id, rk));
            if (doc.RootElement.GetProperty("mimeType").GetString() != FolderMime) throw new InvalidOperationException("Ce lien mène à un fichier, pas à un dossier.");
            var name = doc.RootElement.GetProperty("name").GetString() ?? "Dossier";
            return (rk.Length > 0 ? id + "/" + rk : id, name, "Google Drive › " + name, Str(doc.RootElement, "webViewLink"));
        }

        if (!AppCatalog.IsHttpUrl(link)) throw new InvalidOperationException("Collez le lien de partage du dossier (https://…).");
        var share = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(link)).TrimEnd('=').Replace('/', '_').Replace('+', '-');
        using var item = await GraphGet(t, $"{Graph}/shares/{share}/driveItem?$select=id,name,folder,parentReference,webUrl");
        var r = item.RootElement;
        if (!r.TryGetProperty("folder", out _)) throw new InvalidOperationException("Ce lien mène à un fichier, pas à un dossier.");
        var parent = r.GetProperty("parentReference");
        var folderName = r.GetProperty("name").GetString() ?? "Dossier";
        return (parent.GetProperty("driveId").GetString() + "/" + r.GetProperty("id").GetString(), folderName,
                string.Join(" › ", new[] { "OneDrive", ParentPath(parent), folderName }.Where(x => x.Length > 0)), Str(r, "webUrl"));
    }

    /// <summary>
    /// Vérifie la connexion : lit le premier dossier de la source s'il y en a un, sinon interroge OneDrive (accès aux fichiers).
    /// Google Drive n'a pas de connexion : c'est l'ajout d'un dossier qui vérifie son lien.
    /// </summary>
    public async Task<string> TestConnection(Tenant t, string source)
    {
        if (t.DriveFolders.FirstOrDefault(f => f.Source == source) is { } first)
        {
            var n = (await List(t, first)).Files.Count;
            return $"Connexion réussie · « {first.Name} » : {n} image{(n > 1 ? "s" : "")} ou vidéo{(n > 1 ? "s" : "")} lisible{(n > 1 ? "s" : "")}.";
        }
        if (source == "gdrive")
        {
            if (!GoogleAuth.Has(t, GoogleAuth.DriveScope)) return "Ajoutez un dossier : son lien est vérifié à l'ajout.";
            using var me = await GoogleGet(t, "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)", new());
            return $"Connexion réussie : Google Drive répond pour {(t.GoogleUser.Length > 0 ? t.GoogleUser : "le compte connecté")}. Ajoutez vos dossiers.";
        }
        if (!MicrosoftAuth.Has(t, MicrosoftAuth.DriveScope)) return "Ajoutez un dossier : son lien est vérifié à l'ajout.";
        using var _ = await GraphGet(t, $"{Graph}/me/drive?$select=id");
        return $"Connexion réussie : OneDrive et SharePoint répondent pour {(t.MsUser.Length > 0 ? t.MsUser : "le compte connecté")}. Ajoutez vos dossiers.";
    }

    // ---------- Sélecteur de dossier ----------

    /// <summary>
    /// Contenu d'un dossier (<paramref name="folderRef"/>) ou d'un emplacement de départ (<paramref name="folderRef"/> null) :
    /// ses sous-dossiers, ses images et vidéos lisibles, et le nombre d'autres fichiers (ignorés par les écrans).
    /// </summary>
    public async Task<DriveListing> Browse(Tenant t, string source, string root, string? folderRef)
    {
        string? self = folderRef;
        var web = "";
        List<Item> items;
        bool more;
        if (folderRef != null) (items, more) = await Children(t, source, folderRef, BrowseMax);
        else if (source == "gdrive")
        {
            (items, more) = root switch
            {
                "shared" => await GoogleItems(t, $"sharedWithMe = true and mimeType = '{FolderMime}' and trashed = false", new(), BrowseMax, ""),
                "drives" => await SharedDrives(t),
                _ => await Children(t, source, "root", BrowseMax),
            };
            if (root == "my") (self, web) = ("root", "https://drive.google.com/drive/my-drive");
        }
        else if (root == "shared") (items, more) = await GraphItems(t, $"{Graph}/me/drive/sharedWithMe?$top=200", BrowseMax);
        else if (root == "my")
        {
            using var doc = await GraphGet(t, $"{Graph}/me/drive/root?$select=id,parentReference,webUrl");
            self = doc.RootElement.GetProperty("parentReference").GetProperty("driveId").GetString() + "/" + doc.RootElement.GetProperty("id").GetString();
            web = Str(doc.RootElement, "webUrl");
            (items, more) = await Children(t, source, self, BrowseMax);
        }
        else (items, more) = (new(), false);   // SharePoint : on part d'un lien

        var byName = StringComparer.CurrentCultureIgnoreCase;
        var folders = items.Where(i => i.Folder).OrderBy(i => i.Name, byName).Select(Node).ToList();
        if (self == null) return new(null, "", folders, new(), 0, more);   // liste de dossiers seulement
        var files = items.Where(i => !i.Folder && Playable(i, source)).OrderBy(i => i.Name, byName).Select(Node).ToList();
        return new(self, web, folders, files, items.Count(i => !i.Folder) - files.Count, more);
    }

    /// <summary>Dossiers dont le nom contient <paramref name="text"/>, avec leur emplacement (OneDrive : celui du compte seulement).</summary>
    public async Task<List<DriveNode>> SearchFolders(Tenant t, string source, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return new();
        if (source == "gdrive")
        {
            var q = text.Replace("\\", "\\\\").Replace("'", "\\'");
            var (found, _) = await GoogleItems(t, $"mimeType = '{FolderMime}' and name contains '{q}' and trashed = false", new(), 50, "&corpora=allDrives");
            return found.Select(i => Node(i) with { Location = "Google Drive › " + i.Name }).ToList();
        }
        var (items, _) = await GraphItems(t, $"{Graph}/me/drive/root/search(q='{Uri.EscapeDataString(text.Replace("'", "''"))}')?$top=50", 50);
        return items.Where(i => i.Folder)
            .Select(i => Node(i) with { Location = string.Join(" › ", new[] { "OneDrive", i.Where, i.Name }.Where(x => x.Length > 0)) }).ToList();
    }

    private static DriveNode Node(Item i)
    {
        var info = i.SharedBy.Length > 0 ? "Partagé par " + i.SharedBy
            : DateTime.TryParse(i.Time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? $"Modifié le {d.ToLocalTime():dd.MM.yyyy}" : "";
        return new(i.Folder ? i.Ref : i.Id, i.Name, i.Folder ? "folder" : i.Mime.StartsWith("video/") ? "video" : "image", info, i.WebUrl);
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
    /// Copie dans la médiathèque les images et vidéos nouvelles ou modifiées du dossier (et de ses sous-dossiers si demandé),
    /// retire celles qui n'y sont plus, puis met à jour les écrans qui l'affichent. Rend un message d'erreur, ou null si tout s'est bien passé.
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

            Listed listed;
            try { listed = await List(t, folder); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
            {
                Record(clientId, folderId, ex.Message, null);
                return ex.Message;
            }
            var remote = listed.Files;

            var key = folderId.ToString();
            var local = store.Read(db => db.Media.Where(m => m.ClientId == clientId && m.Info.GetValueOrDefault("drive") == key).ToList());
            var changed = false;
            string? error = null;

            // retirés du dossier, ou modifiés (remplacés ci-dessous)
            var keep = remote.ToDictionary(f => f.Id, f => f.Version);
            var stale = local.Where(m => !keep.TryGetValue(m.Info.GetValueOrDefault("driveFile") ?? "", out var v) || v != m.Info.GetValueOrDefault("driveVer")).ToList();
            if (stale.Count > 0) { Delete(stale); changed = true; }

            // déplacés d'un sous-dossier à l'autre : même fichier, autre chemin (l'ordre « par nom » en dépend)
            var paths = remote.ToDictionary(f => f.Id, f => f.Path);
            var moved = local.Except(stale).Where(m => paths.GetValueOrDefault(m.Info.GetValueOrDefault("driveFile") ?? "") is { } p && m.Info.GetValueOrDefault("drivePath") != p)
                .Select(m => m.Id).ToHashSet();
            if (moved.Count > 0)
            {
                store.Write(db => { foreach (var m in db.Media.Where(m => moved.Contains(m.Id))) m.Info["drivePath"] = paths[m.Info["driveFile"]]; });
                changed = true;
            }

            var present = local.Except(stale).Select(m => m.Info.GetValueOrDefault("driveFile")).ToHashSet();
            foreach (var f in remote.Where(f => !present.Contains(f.Id)))
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var parts = f.Ref.Split('/');
                    using var res = f.DownloadUrl != null
                        ? await http.Download(f.DownloadUrl, ct)   // Microsoft : adresse de téléchargement déjà authentifiée
                        : await GoogleDownload(t, $"{GoogleApi}/{f.Id}?alt=media&supportsAllDrives=true", Keys(f.Id, parts.Length > 1 ? parts[1] : null), ct);
                    if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"« {f.Name} » : téléchargement refusé ({(int)res.StatusCode}).");
                    await using var s = await res.Content.ReadAsStreamAsync(ct);
                    var imported = await importer.ImportStream(s, f.Name, clientId, ct);
                    if (imported.Item is not { } item) throw new InvalidOperationException(imported.Error ?? $"« {f.Name} » : import impossible.");
                    store.Write(db =>
                    {
                        item.Info["drive"] = key; item.Info["driveFile"] = f.Id; item.Info["driveVer"] = f.Version;
                        item.Info["driveName"] = f.Name; item.Info["drivePath"] = f.Path; item.Info["driveTime"] = f.Time;
                    });
                    changed = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    error ??= ex.Message;   // un fichier en échec n'empêche pas les autres ; il sera retenté à la synchronisation suivante
                    log.LogWarning("Drive {Folder} : {Error}", folderId, ex.Message);
                }
            }

            Record(clientId, folderId, error ?? "", listed);
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

    private void Record(Guid clientId, Guid folderId, string error, Listed? listed) => store.Write(db =>
    {
        var f = db.Clients.FirstOrDefault(c => c.Id == clientId)?.DriveFolders.FirstOrDefault(x => x.Id == folderId);
        if (f == null) return;
        f.LastError = error;
        f.SyncedUtc = DateTime.UtcNow;
        if (listed == null) return;
        f.FileCount = listed.Files.Count;
        f.Skipped = listed.Skipped;
        f.SubfolderCount = listed.Subfolders;
    });

    // ---------- Contenu d'un dossier ----------

    /// <summary>
    /// Images et vidéos du dossier, et de tous ses sous-dossiers si <see cref="DriveFolder.Subfolders"/>, triées par chemin :
    /// au plus <see cref="MaxFiles"/> (les suivantes sont comptées dans Skipped) et <see cref="MaxFolders"/> sous-dossiers.
    /// </summary>
    private async Task<Listed> List(Tenant t, DriveFolder folder)
    {
        var files = new List<Item>();
        var seen = new HashSet<string>();   // Google : un fichier peut avoir plusieurs dossiers parents
        var queue = new Queue<(string Ref, string Path)>();
        queue.Enqueue((folder.RemoteId, ""));
        int subfolders = 0, entries = 0;
        while (queue.TryDequeue(out var next) && entries < MaxEntries)
        {
            var (items, _) = await Children(t, folder.Source, next.Ref, MaxEntries - entries);
            entries += items.Count;
            foreach (var i in items)
            {
                if (i.Folder)
                {
                    if (subfolders >= MaxFolders) continue;
                    subfolders++;
                    if (folder.Subfolders) queue.Enqueue((i.Ref, next.Path + i.Name + "/"));
                }
                else if (Playable(i, folder.Source) && seen.Add(i.Id)) files.Add(i with { Path = next.Path + i.Name });
            }
        }
        files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.CurrentCultureIgnoreCase));
        return new(files.Take(MaxFiles).ToList(), Math.Max(0, files.Count - MaxFiles), subfolders);
    }

    /// <summary>Images et vidéos lisibles par les écrans (formats de la médiathèque), taille plafonnée.</summary>
    private static bool Playable(Item f, string source) =>
        (f.Mime.StartsWith("image/") || f.Mime.StartsWith("video/")) && MediaImporter.IsSupported(f.Name) && f.Size <= MaxFileBytes
        && (f.DownloadUrl != null || source == "gdrive");

    /// <summary>Ce que contient un dossier (dossiers puis fichiers), au plus <paramref name="limit"/> éléments environ.</summary>
    private async Task<(List<Item> Items, bool More)> Children(Tenant t, string source, string folderRef, int limit)
    {
        var parts = folderRef.Split('/');
        return source == "gdrive"
            ? await GoogleItems(t, $"'{parts[0]}' in parents and trashed = false", Keys(parts[0], parts.Length > 1 ? parts[1] : null), limit, "&orderBy=folder,name")
            : await GraphItems(t, $"{Graph}/drives/{parts[0]}/items/{parts[^1]}/children?$top=200", limit);
    }

    private async Task<(List<Item>, bool)> GoogleItems(Tenant t, string query, Dictionary<string, string> headers, int limit, string extra)
    {
        var items = new List<Item>();
        string? page = null;
        do
        {
            using var doc = await GoogleGet(t, $"{GoogleApi}?q={Uri.EscapeDataString(query)}&pageSize=200&supportsAllDrives=true&includeItemsFromAllDrives=true{extra}" +
                $"&fields=nextPageToken,files({GoogleFields})" + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : ""), headers);
            foreach (var f in doc.RootElement.GetProperty("files").EnumerateArray())
            {
                var id = f.GetProperty("id").GetString()!;
                var rk = Str(f, "resourceKey");
                var mime = Str(f, "mimeType");
                var time = Str(f, "modifiedTime");
                items.Add(new(id, rk.Length > 0 ? id + "/" + rk : id, Str(f, "name"), mime == FolderMime, mime,
                    long.TryParse(Str(f, "size"), out var n) ? n : 0, f.TryGetProperty("md5Checksum", out var md5) ? md5.GetString() ?? time : time, time, null,
                    Str(f, "webViewLink"), f.TryGetProperty("sharingUser", out var su) ? Str(su, "emailAddress") : ""));
            }
            page = doc.RootElement.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
        } while (page != null && items.Count < limit);
        return (items, page != null);
    }

    /// <summary>Drive partagés dont le compte est membre : chacun se parcourt comme un dossier (son identifiant sert de dossier racine).</summary>
    private async Task<(List<Item>, bool)> SharedDrives(Tenant t)
    {
        using var doc = await GoogleGet(t, "https://www.googleapis.com/drive/v3/drives?pageSize=100&fields=nextPageToken,drives(id,name)", new());
        var items = doc.RootElement.GetProperty("drives").EnumerateArray().Select(d =>
        {
            var id = d.GetProperty("id").GetString()!;
            return new Item(id, id, Str(d, "name"), true, FolderMime, 0, "", "", null, $"https://drive.google.com/drive/folders/{id}", "");
        }).ToList();
        return (items, doc.RootElement.TryGetProperty("nextPageToken", out _));
    }

    /// <summary>Éléments Microsoft Graph, page après page. « Partagés avec moi » : l'élément réel est dans remoteItem.</summary>
    private async Task<(List<Item>, bool)> GraphItems(Tenant t, string url, int limit)
    {
        var items = new List<Item>();
        string? next = url;
        while (next != null && items.Count < limit)
        {
            using var doc = await GraphGet(t, next);
            foreach (var f in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var src = f.TryGetProperty("remoteItem", out var ri) ? ri : f;
                var parent = src.TryGetProperty("parentReference", out var pr) ? pr : default;
                var driveId = parent.ValueKind == JsonValueKind.Object ? Str(parent, "driveId") : "";
                if (driveId.Length == 0) continue;
                var id = src.GetProperty("id").GetString()!;
                var time = Str(src, "lastModifiedDateTime");
                var file = src.TryGetProperty("file", out var fl) ? fl : default;
                var sharedBy = ri.ValueKind == JsonValueKind.Object && src.TryGetProperty("shared", out var sh) && sh.TryGetProperty("owner", out var ow)
                               && ow.TryGetProperty("user", out var us) ? Str(us, "displayName") : "";
                items.Add(new(id, driveId + "/" + id, Str(f, "name"), src.TryGetProperty("folder", out _),
                    file.ValueKind == JsonValueKind.Object ? Str(file, "mimeType") : "",
                    src.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0,
                    f.TryGetProperty("cTag", out var tag) ? tag.GetString() ?? time : time, time,
                    f.TryGetProperty("@microsoft.graph.downloadUrl", out var dl) ? dl.GetString() : null,
                    Str(src, "webUrl"), sharedBy,
                    parent.ValueKind == JsonValueKind.Object ? ParentPath(parent) : ""));
            }
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
        }
        return (items, next != null);
    }

    /// <summary>« /drive/root:/Affichage/Hall » → « Affichage › Hall ».</summary>
    private static string ParentPath(JsonElement parentReference)
    {
        var path = Str(parentReference, "path");
        return path.Contains("root:") ? Uri.UnescapeDataString(path[(path.IndexOf("root:", StringComparison.Ordinal) + 5)..]).Trim('/').Replace("/", " › ") : "";
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

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
            throw new InvalidOperationException($"Accès refusé : vérifiez que le compte {(t.MsUser.Length > 0 ? t.MsUser : "Microsoft connecté")} a accès à ce dossier, ou reconnectez-le dans Intégrations › OneDrive et SharePoint.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("(404)"))
        {
            throw new InvalidOperationException($"Dossier introuvable : vérifiez le lien de partage et que le compte {(t.MsUser.Length > 0 ? t.MsUser : "Microsoft connecté")} y a accès.");
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
