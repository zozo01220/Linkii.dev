using System.Net;
using System.Net.Sockets;

namespace Linkii.Poc;

/// <summary>
/// Appels sortants des apps (flux RSS, calendrier ICS, test d'une page) : l'adresse est saisie par un client, donc elle ne doit
/// jamais atteindre le réseau interne du serveur (SSRF). L'adresse IP est contrôlée au moment de la connexion — redirections
/// et changements de DNS compris —, la taille et la durée sont plafonnées.
/// </summary>
public class SafeHttp
{
    private readonly HttpClient _http;
    private readonly HttpClient _download;   // fichiers volumineux (import de médias par URL) : pas de délai global, l'appelant fixe le sien

    public SafeHttp()
    {
        _http = Create(TimeSpan.FromSeconds(15));
        _download = Create(Timeout.InfiniteTimeSpan);
    }

    private static HttpClient Create(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            UseProxy = false,   // un proxy ferait contourner le contrôle d'adresse
            ConnectCallback = Connect,
        };
        var http = new HttpClient(handler) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LinkiiBot/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return http;
    }

    /// <summary>Téléchargement d'un fichier (médiathèque) : mêmes protections réseau, sans délai global.</summary>
    public Task<HttpResponseMessage> Download(string url, CancellationToken ct) =>
        _download.GetAsync(ParseUrl(url), HttpCompletionOption.ResponseHeadersRead, ct);

    private static async ValueTask<Stream> Connect(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
        var allowed = addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0) throw new HttpRequestException("Adresse non autorisée (réseau interne).");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, ctx.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    /// <summary>Adresse routable sur internet : ni boucle locale, ni réseau privé, ni lien local, ni métadonnées cloud.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127
                || b[0] == 100 && b[1] is >= 64 and <= 127          // CGNAT
                || b[0] == 169 && b[1] == 254                       // lien local, métadonnées cloud
                || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 192 && b[1] == 168
                || b[0] == 192 && b[1] == 0 && b[2] == 0
                || b[0] == 198 && b[1] is 18 or 19                  // tests de performance
                || b[0] >= 224);                                    // multicast et réservé
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC);   // fe80::/10, fec0::/10, ff00::/8, fc00::/7
        }
        return false;
    }

    /// <summary>Normalise et contrôle l'adresse (http / https, sans identifiants). webcal:// est accepté.</summary>
    public static Uri ParseUrl(string url)
    {
        url = url.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
        if (!AppCatalog.IsHttpUrl(url)) throw new InvalidOperationException("Adresse invalide : http:// ou https:// attendu.");
        return new Uri(url);
    }

    /// <summary>Télécharge un texte (taille plafonnée). Lève une exception si l'adresse est interne ou la réponse en erreur.</summary>
    public async Task<string> GetString(string url, int maxBytes = 2_000_000, CancellationToken ct = default, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var res = await Send(url, ct, headers);
        res.EnsureSuccessStatusCode();
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > maxBytes) throw new InvalidOperationException("Réponse trop volumineuse.");
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Requête avec méthode et corps quelconques (POST, REPORT…), mêmes protections que <see cref="GetString"/>. Rend le code HTTP et le corps (taille plafonnée), sans lever d'exception sur un code d'erreur.</summary>
    public async Task<(int Status, string Body)> Request(HttpMethod method, string url, string? body = null, string? contentType = null,
        IReadOnlyDictionary<string, string>? headers = null, int maxBytes = 2_000_000, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(method, ParseUrl(url));
        if (headers != null) foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        if (body != null) req.Content = new StringContent(body, System.Text.Encoding.UTF8, contentType ?? "text/plain");
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > maxBytes) throw new InvalidOperationException("Réponse trop volumineuse.");
        }
        return ((int)res.StatusCode, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    /// <summary>Envoie la requête et rend la réponse dès les en-têtes (le corps n'est pas lu).</summary>
    public Task<HttpResponseMessage> Send(string url, CancellationToken ct = default, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (headers == null) return _http.GetAsync(ParseUrl(url), HttpCompletionOption.ResponseHeadersRead, ct);
        var req = new HttpRequestMessage(HttpMethod.Get, ParseUrl(url));
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);   // clé d'API ou jeton : jamais redirigés hors de l'adresse saisie
        return _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }
}
