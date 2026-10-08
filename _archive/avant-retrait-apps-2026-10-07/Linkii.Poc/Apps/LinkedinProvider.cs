using System.Globalization;

namespace Linkii.Poc;

/// <summary>
/// App LinkedIn, mode « Page entreprise » : lit les publications de la page par l'API officielle avec le jeton de l'administrateur
/// (secret chiffré de l'instance), et ne renvoie au player que du texte, des dates et des images. Le jeton ne quitte jamais le serveur.
/// </summary>
public class LinkedinProvider(LinkedinService li) : IAppProvider
{
    public string Id => "linkedin";

    public async Task<object?> Fetch(AppRuntime rt)
    {
        if (rt.Get("source", "picked") != "page") return new { mode = "picked" };
        var (token, org) = Credentials(rt);

        var count = rt.GetInt("count", 3, 1, 6);
        var maxAge = rt.GetInt("maxAgeDays", 30, 0, 3650);
        var approval = rt.Get("approval", "true") != "false";
        var approved = rt.Get("approved").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

        var all = await li.Posts(token.Access, org, 30);
        var posts = all.Where(p => p.PublishedUtc is not { } d || maxAge == 0 || DateTime.UtcNow - d <= TimeSpan.FromDays(maxAge))
            .Where(p => !approval || approved.Contains(p.Id))
            .Take(count).ToList();

        var showImages = rt.Get("showImages", "true") != "false";
        var items = new List<object>();
        foreach (var p in posts)
        {
            var (title, body) = Split(p);
            var image = showImages && p.ImageUrn != null ? await li.ImageUrl(token.Access, p.ImageUrn) : null;
            items.Add(new { id = p.Id, title, body, date = p.PublishedUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), image, qr = p.Url });
        }
        return new { mode = "page", pageName = rt.Get("orgName"), fetchedAt = DateTime.UtcNow, expiresAt = token.ExpiresUtc, posts = items };
    }

    public async Task<string> Test(AppRuntime rt)
    {
        if (rt.Get("source", "picked") != "page") return "Mode « Publications choisies » : rien à tester, aucune connexion n'est utilisée.";
        var (token, org) = Credentials(rt);
        var posts = await li.Posts(token.Access, org, 30);
        var days = (int)Math.Floor((token.ExpiresUtc - DateTime.UtcNow).TotalDays);
        var tail = days <= 10 ? $" ⚠ Le jeton expire dans {days} jour(s) : reconnectez le compte avant cette date." : "";
        if (days <= 10) return "⚠ " + Summary(rt, posts) + tail;
        return Summary(rt, posts);
    }

    private static string Summary(AppRuntime rt, List<LinkedinPost> posts) =>
        $"Connexion valide : {posts.Count} publication(s) lisible(s) sur « {rt.Get("orgName", "la page")} »"
        + (posts.Count > 0 ? $" · la plus récente : {Split(posts[0]).Title}" : "") + ".";

    private (LinkedinToken Token, string Org) Credentials(AppRuntime rt)
    {
        var token = LinkedinService.ReadAuth(rt.Get(LinkedinService.AuthKey))
            ?? throw new InvalidOperationException("Aucun compte LinkedIn connecté : ouvrez les paramètres du contenu et connectez-vous.");
        if (token.Expired) throw new InvalidOperationException("Le jeton LinkedIn a expiré : reconnectez le compte dans les paramètres du contenu.");
        var org = rt.Get("orgUrn");
        if (org.Length == 0) throw new InvalidOperationException("Choisissez la page LinkedIn à afficher.");
        return (token, org);
    }

    /// <summary>Titre = première ligne du texte (ou titre de l'article partagé), 160 caractères au plus ; le reste devient le corps.</summary>
    public static (string Title, string Body) Split(LinkedinPost p)
    {
        var lines = p.Text.Split('\n').Select(l => l.Trim()).ToList();
        var first = lines.FirstOrDefault(l => l.Length > 0) ?? p.ArticleTitle ?? "Publication LinkedIn";
        var body = string.Join("\n", lines.SkipWhile(l => l.Length == 0).Skip(1)).Trim();
        if (first.Length > 160) { body = (first[160..].Trim() + (body.Length > 0 ? "\n" : "") + body).Trim(); first = first[..160].TrimEnd() + "…"; }
        if (body.Length > 600) body = body[..600].TrimEnd() + "…";
        return (first, body);
    }
}
