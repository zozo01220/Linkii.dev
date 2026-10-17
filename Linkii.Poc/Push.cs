using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Linkii.Poc;

/// <summary>Un téléphone ou une tablette qui reçoit les notifications (app Linkii.AndroidBackOffice) : jeton Firebase du compte.</summary>
public class PushDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Token { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Catégories de notification : chaque personne choisit celles qu'elle reçoit (<see cref="User.PushMuted"/>).</summary>
public static class PushCategories
{
    public const string Screens = "screens", Sync = "sync", Billing = "billing", Members = "members";

    public static readonly (string Id, string Label, string Hint)[] All =
    {
        (Screens, "Écrans hors ligne", "Un écran cesse d'émettre, ou revient (selon l'alerte réglée dans Réglages)."),
        (Sync, "Synchronisations", "Une synchronisation de dossier échoue."),
        (Billing, "Abonnement et essai", "Fin d'essai proche ou terminée, espace suspendu faute de paiement."),
        (Members, "Équipe", "Une personne est ajoutée à l'organisation."),
    };
}

/// <summary>
/// Envoi de notifications push par Firebase Cloud Messaging (API HTTP v1, sans bibliothèque). Actif seulement si
/// <c>Linkii:Fcm:ServiceAccountFile</c> désigne le fichier JSON d'un compte de service Firebase ; sinon chaque envoi est ignoré.
/// Les messages ne portent que des données (titre, texte, page à ouvrir) : l'app construit la notification.
/// </summary>
public class PushService(JsonStore store, IConfiguration config, ILogger<PushService> log)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? accessToken;
    private DateTime accessExpires;

    private string? KeyFile => config["Linkii:Fcm:ServiceAccountFile"];
    public bool Enabled => !string.IsNullOrWhiteSpace(KeyFile) && File.Exists(KeyFile);

    /// <summary>Prévient les administrateurs d'une organisation qui n'ont pas coupé cette catégorie. Ne lève jamais d'exception.</summary>
    public async Task ToClientAdmins(Guid clientId, string category, string title, string body, string? path = null, Guid? except = null)
    {
        try
        {
            if (!Enabled) return;
            var targets = store.Read(db =>
            {
                var users = db.Users.Where(u => u.ClientId == clientId && u.Role == Roles.ClientAdmin && !u.Disabled && u.Id != except
                    && !u.PushMuted.Contains(category)).Select(u => u.Id).ToHashSet();
                return db.PushDevices.Where(d => users.Contains(d.UserId)).Select(d => (d.Id, d.Token)).ToList();
            });
            foreach (var (id, token) in targets)
                if (await Send(token, category, title, body, path) == Outcome.BadToken)
                    store.Write(db => db.PushDevices.RemoveAll(d => d.Id == id));
        }
        catch (Exception ex) { log.LogWarning(ex, "Notification push non envoyée"); }
    }

    private enum Outcome { Ok, BadToken, Failed }

    private async Task<Outcome> Send(string token, string category, string title, string body, string? path)
    {
        var (auth, project) = await Auth();
        if (auth == null) return Outcome.Failed;
        var payload = JsonSerializer.Serialize(new
        {
            message = new
            {
                token,
                data = new Dictionary<string, string> { ["title"] = title, ["body"] = body, ["category"] = category, ["path"] = path ?? "/" },
                android = new { priority = "HIGH" },
            }
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{project}/messages:send")
        { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new("Bearer", auth);
        using var res = await Http.SendAsync(req);
        if (res.IsSuccessStatusCode) return Outcome.Ok;
        var text = await res.Content.ReadAsStringAsync();
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound || text.Contains("UNREGISTERED") || text.Contains("INVALID_ARGUMENT")) return Outcome.BadToken;
        log.LogWarning("FCM a refusé l'envoi ({Status}) : {Body}", (int)res.StatusCode, text);
        return Outcome.Failed;
    }

    /// <summary>Jeton d'accès OAuth du compte de service (gardé jusqu'à son expiration) et identifiant du projet Firebase.</summary>
    private async Task<(string? Token, string Project)> Auth()
    {
        await gate.WaitAsync();
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(KeyFile!));
            var root = doc.RootElement;
            var project = root.GetProperty("project_id").GetString()!;
            if (accessToken != null && DateTime.UtcNow < accessExpires) return (accessToken, project);

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var head = B64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["iss"] = root.GetProperty("client_email").GetString()!,
                ["scope"] = "https://www.googleapis.com/auth/firebase.messaging",
                ["aud"] = "https://oauth2.googleapis.com/token",
                ["iat"] = now, ["exp"] = now + 3600,
            }));
            using var rsa = RSA.Create();
            rsa.ImportFromPem(root.GetProperty("private_key").GetString()!);
            var sig = B64(rsa.SignData(Encoding.UTF8.GetBytes($"{head}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

            using var res = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = $"{head}.{claims}.{sig}",
            }));
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) { log.LogWarning("Jeton Firebase refusé : {Body}", json); return (null, project); }
            using var t = JsonDocument.Parse(json);
            accessToken = t.RootElement.GetProperty("access_token").GetString();
            accessExpires = DateTime.UtcNow.AddSeconds(Math.Max(60, t.RootElement.GetProperty("expires_in").GetInt32() - 120));
            return (accessToken, project);
        }
        finally { gate.Release(); }
    }
}
