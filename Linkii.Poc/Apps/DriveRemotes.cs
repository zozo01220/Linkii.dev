using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Linkii.Poc;

/// <summary>
/// Un élément d'un Drive. Ref : de quoi relire un dossier ou télécharger un fichier — Google : « id » ou « id/resourceKey »
/// (clé de sécurité de certains liens partagés avant 2021, à renvoyer avec chaque appel) ; Microsoft : « driveId/itemId » ;
/// Nextcloud et SFTP : chemin ; Dropbox : « id:… ». Where : emplacement du parent (Microsoft) ; Path : chemin dans le dossier synchronisé (« 2026/affiche.jpg »).
/// </summary>
public record DriveItem(string Id, string Ref, string Name, bool Folder, string Mime, long Size, string Version, string Time,
                        string? DownloadUrl, string WebUrl, string SharedBy, string Where = "", string Path = "");

/// <summary>
/// Connexion ouverte à un Drive « distant » (Nextcloud, SFTP, Dropbox) : lire un dossier, ouvrir un fichier. Une session sert à une opération
/// (parcours d'un dossier, synchronisation) puis se referme ; elle ne se partage pas entre deux tâches.
/// </summary>
public abstract class RemoteSession : IDisposable
{
    /// <summary>Dossier de départ du sélecteur.</summary>
    public virtual Task<string> Root(CancellationToken ct) => Task.FromResult("/");

    /// <summary>Contenu d'un dossier (dossiers et fichiers), au plus <paramref name="limit"/> éléments environ.</summary>
    public abstract Task<(List<DriveItem> Items, bool More)> Children(string folderRef, int limit, CancellationToken ct);

    /// <summary>Contenu d'un fichier, à lire puis fermer.</summary>
    public abstract Task<Stream> Open(DriveItem file, CancellationToken ct);

    /// <summary>Vérifie un dossier saisi (chemin) et rend sa référence, son nom, son emplacement et son adresse web.</summary>
    public abstract Task<(string Ref, string Name, string Location, string WebUrl)> Resolve(string input, CancellationToken ct);

    /// <summary>Dossiers dont le nom contient le texte ; non pris en charge par défaut (voir <see cref="DriveSources.CanSearch"/>).</summary>
    public virtual Task<List<DriveItem>> SearchFolders(string text, CancellationToken ct) =>
        throw new InvalidOperationException("La recherche n'est pas disponible pour ce Drive : parcourez les dossiers.");

    /// <summary>Message de succès d'un test de connexion.</summary>
    public abstract Task<string> Test(CancellationToken ct);

    public virtual void Dispose() { }
}

/// <summary>Mime d'un fichier d'après son extension : ni Nextcloud (parfois), ni SFTP, ni Dropbox ne le donnent de façon fiable.</summary>
public static class DriveMime
{
    public static string Of(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (VideoConverter.ImageExts.Contains(ext)) return "image/" + ext.TrimStart('.');
        return VideoConverter.IsVideo(ext) ? "video/" + ext.TrimStart('.') : "application/octet-stream";
    }

    public static DateTime? Parse(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}

/// <summary>Ouvre les sessions des Drives Nextcloud, SFTP et Dropbox à partir de la connexion de l'organisation.</summary>
public class RemoteDrives(JsonStore store, SecretBox box, SafeHttp http, DropboxAuth dropbox)
{
    public async Task<RemoteSession> Open(Tenant t, string source)
    {
        var a = DriveSources.Account(t, source);
        switch (source)
        {
            case "nextcloud" when a is { Url.Length: > 0, User.Length: > 0, Secret.Length: > 0 }:
                if (a.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !a.AllowHttp)
                    throw new InvalidOperationException("Adresse en http:// : activez « Autoriser HTTP » dans la connexion, ou utilisez https://.");
                return new NextcloudSession(http, a.Url, a.User, box.Unprotect(a.Secret));
            case "sftp" when DriveSources.Configured(t, source):
                var creds = SftpCredentials.Parse(box.Unprotect(a!.Secret));
                var clientId = t.Id;
                return new SftpSession(a.Url, a.Port > 0 ? a.Port : 22, a.User, creds, a.HostKey, fp => store.Write(d =>
                {
                    if (d.Clients.FirstOrDefault(c => c.Id == clientId)?.DriveAccounts.FirstOrDefault(x => x.Source == "sftp") is { } x) x.HostKey = fp;
                }));
            case "dropbox" when DropboxAuth.Has(t):
                return new DropboxSession(http, await dropbox.AccessToken(t));
            default:
                throw new InvalidOperationException(source switch
                {
                    "dropbox" => "Dropbox n'est pas connecté : connectez votre compte dans Intégrations › Dropbox.",
                    _ => $"{DriveSources.Find(source)?.Name ?? source} n'est pas configuré : renseignez la connexion dans Intégrations.",
                });
        }
    }
}

// ---------- Nextcloud (WebDAV) ----------

/// <summary>
/// Nextcloud (et ownCloud) par WebDAV : <c>{serveur}/remote.php/webdav/</c> désigne les fichiers de l'utilisateur authentifié, quel que soit
/// son identifiant interne. L'authentification est un mot de passe d'application (Paramètres › Sécurité), qui passe aussi la double authentification.
/// </summary>
public sealed class NextcloudSession : RemoteSession
{
    private static readonly XNamespace D = "DAV:";
    private const string Body = "<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getetag/></d:prop></d:propfind>";

    private readonly SafeHttp _http;
    private readonly string _server, _base;
    private readonly Dictionary<string, string> _auth;

    public NextcloudSession(SafeHttp http, string server, string user, string password)
    {
        _http = http;
        _server = server.Trim().TrimEnd('/');
        _base = _server + "/remote.php/webdav";
        _auth = new() { ["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)) };
    }

    /// <summary>Adresse WebDAV d'un chemin (« /Affichage/Hall »), chaque segment encodé.</summary>
    private string UrlOf(string path, bool folder) =>
        _base + string.Concat(path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => "/" + Uri.EscapeDataString(s))) + (folder ? "/" : "");

    private string WebOf(string path) => _server + "/apps/files/?dir=" + Uri.EscapeDataString(path.Length == 0 ? "/" : path);

    private static string Normalize(string path)
    {
        path = path.Trim().Replace('\\', '/');
        return "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<(List<DriveItem> Items, DriveItem? Self)> Propfind(string path, int depth, CancellationToken ct)
    {
        path = Normalize(path);
        var headers = new Dictionary<string, string>(_auth) { ["Depth"] = depth.ToString() };
        var (status, text) = await _http.Request(new HttpMethod("PROPFIND"), UrlOf(path, true), Body, "application/xml", headers, maxBytes: 16_000_000, ct: ct);
        switch (status)
        {
            case 401: throw new InvalidOperationException("Nextcloud a refusé les identifiants : vérifiez l'identifiant et le mot de passe d'application (Nextcloud › Paramètres › Sécurité › Créer un nouveau mot de passe d'application).");
            case 403: throw new InvalidOperationException("Accès refusé par Nextcloud : ce compte n'a pas le droit de lire ce dossier.");
            case 404: throw new InvalidOperationException(path == "/" ? "Nextcloud ne répond pas à cette adresse : vérifiez l'adresse du serveur (https://cloud.exemple.ch)." : $"Dossier introuvable : « {path} ».");
            case not 207: throw new InvalidOperationException($"Nextcloud a répondu {status} : vérifiez l'adresse du serveur et ses réglages WebDAV.");
        }

        return Parse(text, path);
    }

    /// <summary>Lit la réponse d'un PROPFIND : les éléments du dossier, et le dossier lui-même (<paramref name="path"/>).</summary>
    public (List<DriveItem> Items, DriveItem? Self) Parse(string text, string path)
    {
        path = Normalize(path);
        XDocument doc;
        try { doc = XDocument.Parse(text); }
        catch (System.Xml.XmlException) { throw new InvalidOperationException("Réponse illisible : cette adresse n'est pas celle d'un serveur Nextcloud (WebDAV)."); }

        var basePath = Uri.UnescapeDataString(new Uri(_base).AbsolutePath).TrimEnd('/');
        var items = new List<DriveItem>();
        DriveItem? self = null;
        foreach (var r in doc.Descendants(D + "response"))
        {
            var href = Uri.UnescapeDataString(r.Element(D + "href")?.Value ?? "");
            if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) href = Uri.UnescapeDataString(new Uri(href).AbsolutePath);
            if (!href.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Normalize(href[basePath.Length..]);
            var prop = r.Elements(D + "propstat").FirstOrDefault(p => p.Element(D + "status")?.Value.Contains(" 200") == true)?.Element(D + "prop");
            if (prop == null) continue;
            var folder = prop.Element(D + "resourcetype")?.Element(D + "collection") != null;
            var name = rel == "/" ? "Nextcloud" : rel[(rel.LastIndexOf('/') + 1)..];
            var size = long.TryParse(prop.Element(D + "getcontentlength")?.Value, out var n) ? n : 0;
            var modified = DriveMime.Parse(prop.Element(D + "getlastmodified")?.Value);
            var time = modified is { } m ? DriveMime.Iso(m) : "";
            var etag = prop.Element(D + "getetag")?.Value.Trim('"') ?? "";
            var item = new DriveItem(rel, rel, name, folder, folder ? "" : DriveMime.Of(name), size, etag.Length > 0 ? etag : time + "/" + size, time, null, WebOf(folder ? rel : rel[..Math.Max(1, rel.LastIndexOf('/'))]), "");
            if (rel == path) self = item; else items.Add(item);
        }
        return (items, self);
    }

    public override async Task<(List<DriveItem>, bool)> Children(string folderRef, int limit, CancellationToken ct)
    {
        var (items, _) = await Propfind(folderRef, 1, ct);
        return (items.Take(limit).ToList(), items.Count > limit);
    }

    public override async Task<Stream> Open(DriveItem file, CancellationToken ct)
    {
        var res = await _http.Download(UrlOf(file.Ref, false), _auth, ct);
        if (!res.IsSuccessStatusCode) { res.Dispose(); throw new InvalidOperationException($"« {file.Name} » : téléchargement refusé ({(int)res.StatusCode})."); }
        return new ResponseStream(res, await res.Content.ReadAsStreamAsync(ct));
    }

    public override async Task<(string, string, string, string)> Resolve(string input, CancellationToken ct)
    {
        input = input.Trim();
        // adresse de l'interface (…/apps/files/?dir=/Affichage) ou chemin
        var dir = Uri.TryCreate(input, UriKind.Absolute, out var u) && System.Web.HttpUtility.ParseQueryString(u.Query)["dir"] is { Length: > 0 } d ? d : input;
        if (dir.Contains("://")) throw new InvalidOperationException("Saisissez le chemin du dossier (/Affichage/Hall) ou l'adresse de Nextcloud › Fichiers à l'intérieur du dossier.");
        var path = Normalize(dir);
        var (_, self) = await Propfind(path, 0, ct);
        if (self is not { Folder: true }) throw new InvalidOperationException("Ce chemin mène à un fichier, pas à un dossier.");
        var name = path == "/" ? "Nextcloud" : self.Name;
        return (path, name, "Nextcloud" + (path == "/" ? "" : " › " + string.Join(" › ", path.Trim('/').Split('/'))), WebOf(path));
    }

    public override async Task<string> Test(CancellationToken ct)
    {
        var (items, _) = await Propfind("/", 1, ct);
        var folders = items.Count(i => i.Folder);
        return $"Connexion réussie : Nextcloud répond ({folders} dossier{(folders > 1 ? "s" : "")} à la racine). Ajoutez vos dossiers.";
    }
}

/// <summary>Flux d'un téléchargement HTTP qui referme la réponse avec lui.</summary>
internal sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) { inner.Dispose(); response.Dispose(); }
        base.Dispose(disposing);
    }
}

// ---------- SFTP ----------

/// <summary>Identifiants SFTP : mot de passe et/ou clé privée (PEM), enregistrés chiffrés.</summary>
public record SftpCredentials(string Password, string Key, string Passphrase)
{
    public bool Any => Password.Length > 0 || Key.Length > 0;
    public string ToJson() => JsonSerializer.Serialize(this);
    public static SftpCredentials Parse(string json)
    {
        try { return JsonSerializer.Deserialize<SftpCredentials>(json) ?? new("", "", ""); }
        catch (JsonException) { return new("", "", ""); }
    }
}

/// <summary>
/// SFTP (SSH.NET). Le serveur est reconnu à son empreinte : retenue à la première connexion, puis exigée (une empreinte différente peut être
/// celle d'un intrus). Comme les autres Drives, le serveur ne doit pas être sur le réseau interne de Linkii.
/// </summary>
public sealed class SftpSession : RemoteSession
{
    private readonly string _host, _user, _knownKey;
    private readonly int _port;
    private readonly SftpCredentials _creds;
    private readonly Action<string> _remember;
    private SftpClient? _client;

    public SftpSession(string host, int port, string user, SftpCredentials creds, string knownKey, Action<string> remember)
        => (_host, _port, _user, _creds, _knownKey, _remember) = (host.Trim(), port, user, creds, knownKey, remember);

    public static string Fingerprint(byte[] sha256) => "SHA256:" + Convert.ToBase64String(sha256).TrimEnd('=');

    private SftpClient Connect()
    {
        if (_client is { IsConnected: true } ok) return ok;
        // la connexion se fait à l'adresse contrôlée, pas au nom : un nom qui change de réponse ne mène jamais au réseau interne
        var addresses = Dns.GetHostAddresses(_host);
        var ip = addresses.FirstOrDefault(SafeHttp.IsPublic) ?? throw new InvalidOperationException("Adresse non autorisée : le serveur SFTP doit être joignable depuis internet (réseau interne refusé).");

        var methods = new List<AuthenticationMethod>();
        if (_creds.Key.Length > 0)
        {
            try
            {
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(_creds.Key.Trim() + "\n"));
                methods.Add(new PrivateKeyAuthenticationMethod(_user, _creds.Passphrase.Length > 0 ? new PrivateKeyFile(ms, _creds.Passphrase) : new PrivateKeyFile(ms)));
            }
            catch (Exception ex) when (ex is SshException or ArgumentException or InvalidOperationException)
            {
                throw new InvalidOperationException("La clé privée n'a pas pu être lue : collez-la en entier (-----BEGIN … PRIVATE KEY-----), avec sa phrase secrète si elle en a une.");
            }
        }
        if (_creds.Password.Length > 0) methods.Add(new PasswordAuthenticationMethod(_user, _creds.Password));
        if (methods.Count == 0) throw new InvalidOperationException("Aucun mot de passe ni clé privée n'est enregistré pour ce serveur SFTP.");

        var client = new SftpClient(new Renci.SshNet.ConnectionInfo(ip.ToString(), _port, _user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(15) });
        string? mismatch = null;
        client.HostKeyReceived += (_, e) =>
        {
            var seen = Fingerprint(System.Security.Cryptography.SHA256.HashData(e.HostKey));
            if (_knownKey.Length == 0) { _remember(seen); e.CanTrust = true; }
            else if (_knownKey == seen) e.CanTrust = true;
            else { e.CanTrust = false; mismatch = seen; }
        };
        try { client.Connect(); }
        catch (SshAuthenticationException) { client.Dispose(); throw new InvalidOperationException("Le serveur SFTP a refusé l'identifiant, le mot de passe ou la clé."); }
        catch (SshConnectionException ex) when (mismatch != null)
        {
            client.Dispose();
            throw new InvalidOperationException($"L'empreinte du serveur a changé (attendue {_knownKey}, reçue {mismatch}). Si le changement est voulu, « Oublier l'empreinte » dans la connexion, puis testez de nouveau.", ex);
        }
        catch (Exception ex) when (ex is SshException or SocketException or TimeoutException or IOException)
        {
            client.Dispose();
            throw new InvalidOperationException($"Connexion SFTP impossible ({_host}:{_port}) : {ex.Message}", ex);
        }
        return _client = client;
    }

    /// <summary>SSH.NET est synchrone : le travail se fait hors du fil appelant, et ses erreurs réseau deviennent des erreurs lisibles.</summary>
    private static Task<T> Work<T>(Func<T> job, CancellationToken ct) => Task.Run(() =>
    {
        try { return job(); }
        catch (Exception ex) when (ex is SshException or SocketException or IOException) { throw new InvalidOperationException($"Serveur SFTP : {ex.Message}", ex); }
    }, ct);

    public override Task<string> Root(CancellationToken ct) => Work(() => Connect().WorkingDirectory, ct);

    private static string Join(string folder, string name) => folder == "/" ? "/" + name : folder.TrimEnd('/') + "/" + name;

    public override Task<(List<DriveItem>, bool)> Children(string folderRef, int limit, CancellationToken ct) => Work(() =>
    {
        var sftp = Connect();
        var items = new List<DriveItem>();
        try
        {
            foreach (var f in sftp.ListDirectory(folderRef))
            {
                if (f.Name is "." or "..") continue;
                var (dir, length, utc) = (f.IsDirectory, f.Length, f.LastWriteTimeUtc);
                if (f.IsSymbolicLink)   // lien vers un dossier ou un fichier : on suit la cible ; un lien cassé est ignoré
                {
                    try { var target = sftp.GetAttributes(f.FullName); (dir, length, utc) = (target.IsDirectory, target.Size, target.LastWriteTimeUtc); }
                    catch (SshException) { continue; }
                }
                else if (!f.IsDirectory && !f.IsRegularFile) continue;
                var time = DriveMime.Iso(utc);
                items.Add(new(f.FullName, f.FullName, f.Name, dir, dir ? "" : DriveMime.Of(f.Name), dir ? 0 : length, time + "/" + length, time, null, "", ""));
                if (items.Count >= limit) return (items, true);
            }
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException) { throw new InvalidOperationException($"Dossier introuvable sur le serveur : « {folderRef} »."); }
        catch (Renci.SshNet.Common.SftpPermissionDeniedException) { throw new InvalidOperationException($"Accès refusé au dossier « {folderRef} »."); }
        return (items, false);
    }, ct);

    public override Task<Stream> Open(DriveItem file, CancellationToken ct) => Work<Stream>(() => Connect().OpenRead(file.Ref), ct);

    public override Task<(string, string, string, string)> Resolve(string input, CancellationToken ct) => Work(() =>
    {
        var sftp = Connect();
        var path = input.Trim().Replace('\\', '/');
        if (path.Length == 0) throw new InvalidOperationException("Saisissez le chemin du dossier sur le serveur (/home/affichage).");
        if (!path.StartsWith('/')) path = Join(sftp.WorkingDirectory, path);
        if (path.Length > 1) path = path.TrimEnd('/');
        try
        {
            var attrs = sftp.GetAttributes(path);
            if (!attrs.IsDirectory) throw new InvalidOperationException("Ce chemin mène à un fichier, pas à un dossier.");
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException) { throw new InvalidOperationException($"Dossier introuvable sur le serveur : « {path} »."); }
        var name = path == "/" ? _host : path[(path.LastIndexOf('/') + 1)..];
        return (path, name, $"{_host} › {path}", "");
    }, ct);

    public override Task<string> Test(CancellationToken ct) => Work(() =>
    {
        var sftp = Connect();
        var n = sftp.ListDirectory(sftp.WorkingDirectory).Count(f => f.Name is not ("." or ".."));
        return $"Connexion réussie : {_host} répond ({n} élément{(n > 1 ? "s" : "")} dans {sftp.WorkingDirectory}). Ajoutez vos dossiers.";
    }, ct);

    public override void Dispose()
    {
        try { _client?.Dispose(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException) { }
        _client = null;
    }
}

// ---------- Dropbox ----------

/// <summary>Dropbox API v2 (lecture) avec le jeton d'accès du compte connecté par l'organisation.</summary>
public sealed class DropboxSession(SafeHttp http, string accessToken) : RemoteSession
{
    private const string Api = "https://api.dropboxapi.com/2";
    private Dictionary<string, string> Auth => new() { ["Authorization"] = "Bearer " + accessToken };

    public override Task<string> Root(CancellationToken ct) => Task.FromResult("/");   // la racine de Dropbox s'écrit « » pour l'API

    private async Task<JsonDocument> Call(string endpoint, object? body, CancellationToken ct)
    {
        var (status, text) = await http.Request(HttpMethod.Post, Api + endpoint, body == null ? null : JsonSerializer.Serialize(body), body == null ? null : "application/json",
            Auth, maxBytes: 8_000_000, ct: ct);
        if (status == 401) throw new InvalidOperationException("Dropbox a refusé l'accès : reconnectez votre compte dans Intégrations › Comptes connectés.");
        if (status == 409)
        {
            if (text.Contains("not_found")) throw new InvalidOperationException("Dossier introuvable dans Dropbox : il a peut-être été déplacé ou supprimé.");
            if (text.Contains("not_folder")) throw new InvalidOperationException("Ce chemin mène à un fichier, pas à un dossier.");
            if (text.Contains("malformed_path")) throw new InvalidOperationException("Chemin invalide : saisissez un chemin du type /Affichage/Hall.");
        }
        if (status == 429) throw new InvalidOperationException("Dropbox limite temporairement les requêtes : réessayez dans un instant.");
        if (status != 200) throw new InvalidOperationException($"Dropbox ({status}) : {Describe(text)}");
        return JsonDocument.Parse(text);
    }

    private static string Describe(string body)
    {
        try { using var d = JsonDocument.Parse(body); return d.RootElement.TryGetProperty("error_summary", out var s) ? s.GetString() ?? body : body; }
        catch (JsonException) { return body.Length > 200 ? body[..200] : body; }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static DriveItem ToItem(JsonElement e)
    {
        var folder = Str(e, ".tag") == "folder";
        var name = Str(e, "name");
        var id = Str(e, "id");
        var time = Str(e, "server_modified");
        var hash = Str(e, "content_hash");
        var display = Str(e, "path_display");
        return new(id, id, name, folder, folder ? "" : DriveMime.Of(name), e.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0,
            hash.Length > 0 ? hash : Str(e, "rev"), time, null, "https://www.dropbox.com/home" + (folder ? display : display[..Math.Max(0, display.LastIndexOf('/'))]), "",
            display.Length > 0 ? string.Join(" › ", display.Trim('/').Split('/').SkipLast(1)) : "");
    }

    public override async Task<(List<DriveItem>, bool)> Children(string folderRef, int limit, CancellationToken ct)
    {
        var items = new List<DriveItem>();
        using var first = await Call("/files/list_folder", new { path = folderRef == "/" ? "" : folderRef, recursive = false, limit = 500, include_non_downloadable_files = false }, ct);
        var doc = first;
        var more = false;
        while (true)
        {
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
                if (Str(e, ".tag") is "folder" or "file") items.Add(ToItem(e));
            more = doc.RootElement.TryGetProperty("has_more", out var hm) && hm.GetBoolean();
            if (!more || items.Count >= limit) break;
            var cursor = doc.RootElement.GetProperty("cursor").GetString();
            var next = await Call("/files/list_folder/continue", new { cursor }, ct);
            if (!ReferenceEquals(doc, first)) doc.Dispose();
            doc = next;
        }
        if (!ReferenceEquals(doc, first)) doc.Dispose();
        return (items, more);
    }

    public override async Task<Stream> Open(DriveItem file, CancellationToken ct)
    {
        using var link = await Call("/files/get_temporary_link", new { path = file.Ref }, ct);   // adresse valable quatre heures, sans jeton
        var res = await http.Download(link.RootElement.GetProperty("link").GetString()!, ct);
        if (!res.IsSuccessStatusCode) { res.Dispose(); throw new InvalidOperationException($"« {file.Name} » : téléchargement refusé ({(int)res.StatusCode})."); }
        return new ResponseStream(res, await res.Content.ReadAsStreamAsync(ct));
    }

    public override async Task<(string, string, string, string)> Resolve(string input, CancellationToken ct)
    {
        var path = input.Trim().Replace('\\', '/');
        if (path.Contains("dropbox.com", StringComparison.OrdinalIgnoreCase) && path.Contains("/home", StringComparison.OrdinalIgnoreCase))
            path = Uri.UnescapeDataString(path[(path.IndexOf("/home", StringComparison.OrdinalIgnoreCase) + 5)..].Split('?')[0]);   // adresse de dropbox.com/home/Affichage
        if (path.Length == 0 || path == "/") throw new InvalidOperationException("Saisissez le chemin du dossier (/Affichage/Hall) : la racine se choisit en parcourant.");
        if (!path.StartsWith('/')) path = "/" + path;
        using var doc = await Call("/files/get_metadata", new { path = path.TrimEnd('/') }, ct);
        var e = doc.RootElement;
        if (Str(e, ".tag") != "folder") throw new InvalidOperationException("Ce chemin mène à un fichier, pas à un dossier.");
        var display = Str(e, "path_display");
        return (Str(e, "id"), Str(e, "name"), "Dropbox › " + string.Join(" › ", display.Trim('/').Split('/')), "https://www.dropbox.com/home" + display);
    }

    public override async Task<List<DriveItem>> SearchFolders(string text, CancellationToken ct)
    {
        using var doc = await Call("/files/search_v2", new { query = text, options = new { filename_only = true, max_results = 50, file_categories = new[] { "folder" } } }, ct);
        var found = new List<DriveItem>();
        foreach (var m in doc.RootElement.GetProperty("matches").EnumerateArray())
            if (m.TryGetProperty("metadata", out var md) && md.TryGetProperty("metadata", out var e) && Str(e, ".tag") == "folder") found.Add(ToItem(e));
        return found;
    }

    public override async Task<string> Test(CancellationToken ct)
    {
        using var me = await Call("/users/get_current_account", null, ct);
        var email = Str(me.RootElement, "email");
        return $"Connexion réussie : Dropbox répond pour {(email.Length > 0 ? email : "le compte connecté")}. Ajoutez vos dossiers.";
    }
}
