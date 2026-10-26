using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Linkii.Poc;

/// <summary>
/// Mise en ligne de l'adresse d'un revendeur dans Nginx Proxy Manager (API REST) : un hôte proxy par revendeur, avec le certificat
/// générique « *.domaine » déjà présent dans NPM. Sans adresse de NPM dans Réglages › Adresses, rien n'est automatisé (l'hôte générique suffit).
/// Une panne de NPM ne bloque jamais la création du revendeur : l'adresse passe en « error » et se relance depuis sa fiche.
/// </summary>
public class NpmProxy(JsonStore store, SecretBox box, IHttpClientFactory httpFactory, ResellerResolver resolver, ILogger<NpmProxy> log)
{
    public const string Online = "online", Error = "error";

    public record Conf(string Url, string User, string Password, string Scheme, string ForwardHost, int ForwardPort, string BaseDomain);

    private Conf? Read() => store.Read(d =>
    {
        var p = d.Platform;
        if (p.NpmUrl.Trim() == "" || p.NpmUser.Trim() == "" || p.NpmPassword == "" || p.NpmForwardHost.Trim() == "") return null;
        try { return new Conf(p.NpmUrl.Trim().TrimEnd('/'), p.NpmUser.Trim(), box.Unprotect(p.NpmPassword), p.NpmForwardScheme == "https" ? "https" : "http", p.NpmForwardHost.Trim(), p.NpmForwardPort, resolver.BaseDomainOf(d)); }
        catch (Exception) { return null; }   // secret illisible (clés de chiffrement changées) : à ressaisir
    });

    public bool Configured => Read() != null;

    /// <summary>Pourquoi la connexion n'est pas utilisable (champ manquant, mot de passe illisible sur ce serveur…), ou null.</summary>
    public string? Problem() => store.Read(d =>
    {
        var p = d.Platform;
        if (p.NpmUrl.Trim() == "") return "Renseignez l'adresse de NPM (ex. : http://192.168.1.120:81).";
        if (p.NpmUser.Trim() == "") return "Renseignez l'identifiant du compte NPM.";
        if (p.NpmPassword == "") return "Renseignez le mot de passe du compte NPM.";
        try { box.Unprotect(p.NpmPassword); }
        catch (Exception) { return "Le mot de passe enregistré est illisible sur ce serveur (réglages copiés depuis une autre machine) : saisissez-le à nouveau."; }
        if (p.NpmForwardHost.Trim() == "") return "Renseignez la destination des hôtes (adresse du serveur Linkivi vue depuis NPM).";
        return null;
    });

    private HttpClient Client()
    {
        var c = httpFactory.CreateClient();
        c.Timeout = TimeSpan.FromSeconds(15);
        return c;
    }

    private static string Msg(string body, int status)
    {
        try { using var j = JsonDocument.Parse(body); if (j.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)) return m.GetString() ?? $"HTTP {status}"; }
        catch (JsonException) { }
        return $"HTTP {status}";
    }

    private async Task<string> Token(HttpClient c, Conf conf)
    {
        var res = await c.PostAsync(conf.Url + "/api/tokens", new StringContent(JsonSerializer.Serialize(new { identity = conf.User, secret = conf.Password }), Encoding.UTF8, "application/json"));
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException("Connexion refusée par NPM : " + Msg(body, (int)res.StatusCode));
        using var j = JsonDocument.Parse(body);
        var t = j.RootElement.TryGetProperty("token", out var tk) ? tk.GetString() : null;
        if (string.IsNullOrEmpty(t)) throw new InvalidOperationException("NPM n'a pas renvoyé de jeton (identifiant ou mot de passe incorrect).");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t);
        return t;
    }

    private static async Task<JsonDocument> Get(HttpClient c, string url)
    {
        var res = await c.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(Msg(body, (int)res.StatusCode));
        return JsonDocument.Parse(body);
    }

    private static string NoCertificate(JsonDocument certs, string baseDomain)
    {
        var found = certs.RootElement.EnumerateArray().Where(c => c.TryGetProperty("domain_names", out _))
            .SelectMany(c => c.GetProperty("domain_names").EnumerateArray().Select(n => n.GetString() ?? "")).Where(n => n.StartsWith("*.")).Distinct().ToList();
        return $"Aucun certificat *.{baseDomain} dans NPM (domaine de base actuel : {baseDomain})." +
            (found.Count > 0 ? $" Certificats génériques trouvés : {string.Join(", ", found)}. Corrigez le « Domaine de base » dans Réglages › Adresses." : " Créez-le dans NPM (Let's Encrypt par DNS).");
    }

    private static int? WildcardCertificate(JsonDocument certs, string baseDomain)
    {
        var wanted = "*." + baseDomain;
        foreach (var c in certs.RootElement.EnumerateArray())
            if (c.TryGetProperty("domain_names", out var names) && names.EnumerateArray().Any(n => string.Equals(n.GetString(), wanted, StringComparison.OrdinalIgnoreCase)))
                return c.GetProperty("id").GetInt32();
        return null;
    }

    /// <summary>Teste la connexion (réglages de la console) : jeton, puis présence du certificat générique.</summary>
    public async Task<(bool Ok, string Message)> Test()
    {
        if (Read() is not { } conf) return (false, Problem() ?? "NPM n'est pas configuré.");
        try
        {
            using var c = Client();
            await Token(c, conf);
            using var certs = await Get(c, conf.Url + "/api/nginx/certificates");
            return WildcardCertificate(certs, conf.BaseDomain) is { } id
                ? (true, $"Connexion réussie. Certificat générique *.{conf.BaseDomain} trouvé (n° {id}).")
                : (false, "Connexion réussie, mais " + char.ToLowerInvariant(NoCertificate(certs, conf.BaseDomain)[0]) + NoCertificate(certs, conf.BaseDomain)[1..]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            return (false, ex is TaskCanceledException ? "NPM ne répond pas (délai dépassé)." : ex.Message);
        }
    }

    /// <summary>
    /// Crée (ou retrouve) l'hôte proxy d'une adresse dans NPM, avec le certificat générique. Ne lève jamais d'exception.
    /// NPM non configuré ou en panne : échec explicite, rien n'est créé.
    /// </summary>
    public async Task<(bool Ok, int? HostId, string Message)> CreateHost(string host)
    {
        if (Read() is not { } conf) return (false, null, (Problem() ?? "Nginx Proxy Manager n'est pas configuré") + " Réglages › Adresses.");
        try
        {
            using var c = Client();
            await Token(c, conf);
            using var certs = await Get(c, conf.Url + "/api/nginx/certificates");
            var certId = WildcardCertificate(certs, conf.BaseDomain) ?? throw new InvalidOperationException(NoCertificate(certs, conf.BaseDomain));
            int hostId;
            var reused = false;
            using (var hosts = await Get(c, conf.Url + "/api/nginx/proxy-hosts"))
            {
                var existing = hosts.RootElement.EnumerateArray().FirstOrDefault(h => h.TryGetProperty("domain_names", out var n) && n.EnumerateArray().Any(x => string.Equals(x.GetString(), host, StringComparison.OrdinalIgnoreCase)));
                hostId = existing.ValueKind == JsonValueKind.Object ? existing.GetProperty("id").GetInt32() : 0;
                reused = hostId != 0;
            }
            if (hostId == 0)
            {
                var payload = new
                {
                    domain_names = new[] { host }, forward_scheme = conf.Scheme, forward_host = conf.ForwardHost, forward_port = conf.ForwardPort,
                    access_list_id = 0, certificate_id = certId, ssl_forced = true, hsts_enabled = false, hsts_subdomains = false, http2_support = true,
                    block_exploits = true, caching_enabled = false, allow_websocket_upgrade = true, advanced_config = "", enabled = true,
                    meta = new { letsencrypt_agree = false, dns_challenge = false }, locations = Array.Empty<object>()
                };
                var res = await c.PostAsync(conf.Url + "/api/nginx/proxy-hosts", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
                var body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode) throw new InvalidOperationException("NPM a refusé l'hôte : " + Msg(body, (int)res.StatusCode));
                using var j = JsonDocument.Parse(body);
                hostId = j.RootElement.GetProperty("id").GetInt32();
            }
            return (true, hostId, $"Hôte NPM n° {hostId}{(reused ? " (existant réutilisé)" : "")} · certificat *.{conf.BaseDomain} (n° {certId}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            var msg = ex is TaskCanceledException ? "NPM ne répond pas (délai dépassé)." : ex is HttpRequestException ? "NPM est injoignable : " + ex.Message : ex.Message;
            log.LogWarning("Hôte proxy de {Host} : {Msg}", host, msg);
            return (false, null, msg);
        }
    }

    /// <summary>Relance la mise en ligne d'un revendeur existant dont l'adresse est « En attente » et enregistre le résultat sur sa fiche.</summary>
    public async Task<(bool Ok, string Message)> Provision(Guid resellerId)
    {
        var info = store.Read(d => d.Resellers.FirstOrDefault(r => r.Id == resellerId) is { } r ? (Host: resolver.HostFor(d, r.Slug), r.NpmHostId) : default);
        if (info.Host == null) return (false, "Revendeur introuvable.");
        var (ok, hostId, msg) = await CreateHost(info.Host);
        if (Read() != null) Save(resellerId, ok ? Online : Error, ok ? hostId : info.NpmHostId, msg);
        return (ok, ok ? $"Adresse {info.Host} mise en ligne ({msg})" : msg);
    }

    /// <summary>Retire l'hôte proxy d'un revendeur supprimé (échec sans conséquence : l'hôte pourra être supprimé à la main dans NPM).</summary>
    public async Task<string?> Remove(int hostId)
    {
        if (Read() is not { } conf) return null;
        try
        {
            using var c = Client();
            await Token(c, conf);
            var res = await c.DeleteAsync($"{conf.Url}/api/nginx/proxy-hosts/{hostId}");
            return res.IsSuccessStatusCode || (int)res.StatusCode == 404 ? null : Msg(await res.Content.ReadAsStringAsync(), (int)res.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException) { return ex.Message; }
    }

    /// <summary>Vérifie que l'adresse répond en HTTPS (certificat valide). Depuis le réseau du serveur : un échec peut venir du routage interne.</summary>
    public async Task<(bool Ok, string Message)> Check(Guid resellerId)
    {
        var host = store.Read(d => d.Resellers.FirstOrDefault(r => r.Id == resellerId) is { } r ? resolver.HostFor(d, r.Slug) : null);
        if (host == null) return (false, "Revendeur introuvable.");
        string msg; bool ok;
        try
        {
            using var c = httpFactory.CreateClient();
            c.Timeout = TimeSpan.FromSeconds(8);
            using var res = await c.GetAsync($"https://{host}/login");
            ok = (int)res.StatusCode < 500;
            msg = ok ? $"HTTPS répond ({(int)res.StatusCode})." : $"HTTPS répond avec une erreur ({(int)res.StatusCode}).";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ok = false;
            msg = ex is TaskCanceledException ? "Pas de réponse en HTTPS (délai dépassé)." : "Contrôle HTTPS impossible depuis le serveur : " + (ex.InnerException?.Message ?? ex.Message);
        }
        store.Write(d => { if (d.Resellers.FirstOrDefault(r => r.Id == resellerId) is { } r) r.AddressCheckedUtc = DateTime.UtcNow; });
        return (ok, msg);
    }

    private void Save(Guid id, string state, int? hostId, string note) => store.Write(d =>
    {
        if (d.Resellers.FirstOrDefault(r => r.Id == id) is not { } r) return;
        r.AddressState = state; r.AddressNote = note; r.NpmHostId = hostId; r.AddressCheckedUtc = DateTime.UtcNow;
    });
}
