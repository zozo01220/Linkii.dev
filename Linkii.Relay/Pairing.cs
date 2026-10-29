using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Linkii.Poc;

namespace Linkii.Relay;

/// <summary>Appairage du relais avec le serveur Linkii (code d'installation contre un jeton) : partagé par « pair » et par l'installateur Windows.</summary>
public static class RelayPairing
{
    /// <summary>Marqueur ajouté à la fin de l'exécutable par le téléchargement du serveur : l'adresse du serveur qui l'a servi.</summary>
    private const string Marker = "#LINKII-SERVER=";

    /// <summary>Message d'erreur si l'adresse du serveur ne convient pas (https obligatoire, sauf en local), sinon null.</summary>
    public static string? CheckServer(string? server)
    {
        if (!Uri.TryCreate((server ?? "").Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return "Adresse du serveur invalide (exemple : https://linkii.exemple.ch).";
        if (u.Scheme != Uri.UriSchemeHttps && !u.IsLoopback) return "L'adresse du serveur doit commencer par https://";
        return null;
    }

    /// <summary>Code saisi (« abcd 2345 », « ABCD-2345 ») remis en forme : 8 caractères en majuscules, ou null s'il n'a pas la bonne forme.</summary>
    public static string? CleanCode(string? code)
    {
        var c = (code ?? "").Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");
        return c.Length == 8 && c.All(char.IsLetterOrDigit) ? c : null;
    }

    public static async Task<(RelayPairResponse? Result, string? Error)> PairAsync(HttpClient http, string server, string? code, string? name)
    {
        if (CheckServer(server) is { } bad) return (null, bad);
        if (CleanCode(code) is not { } c) return (null, "Le code a 8 caractères (exemple : ABCD-2345).");
        try
        {
            var res = await http.PostAsJsonAsync(server.Trim().TrimEnd('/') + "/api/relay/pair", new RelayPairRequest(c, name, Environment.MachineName, RelayWorker.Version), RelayConfig.Json);
            if (!res.IsSuccessStatusCode) return (null, await ErrorText(res));
            var r = await res.Content.ReadFromJsonAsync<RelayPairResponse>(RelayConfig.Json);
            return r == null || r.Token.Length == 0 ? (null, "Réponse inattendue du serveur.") : (r, null);
        }
        catch (HttpRequestException e) { return (null, "Serveur injoignable : " + e.Message); }
        catch (TaskCanceledException) { return (null, "Le serveur n'a pas répondu à temps."); }
    }

    private static async Task<string> ErrorText(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        try { if (JsonDocument.Parse(body).RootElement.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } s) return s; }
        catch (JsonException) { }
        return $"Appairage refusé ({(int)res.StatusCode}).";
    }

    /// <summary>
    /// Adresse du serveur qui a servi cet exécutable : le téléchargement de Linkii ajoute « #LINKII-SERVER=https://… » à la fin du fichier
    /// (sans effet sur son fonctionnement), pour que l'installateur n'ait pas à la demander. Null si le fichier vient d'ailleurs.
    /// </summary>
    public static string? ServerOf(string exePath)
    {
        try
        {
            using var f = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var n = (int)Math.Min(f.Length, 600);
            var buf = new byte[n];
            f.Seek(-n, SeekOrigin.End);
            f.ReadExactly(buf);
            var tail = Encoding.UTF8.GetString(buf);
            var i = tail.LastIndexOf(Marker, StringComparison.Ordinal);
            if (i < 0) return null;
            var url = tail[(i + Marker.Length)..].Split('\n')[0].Trim();
            return CheckServer(url) == null ? url.TrimEnd('/') : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
