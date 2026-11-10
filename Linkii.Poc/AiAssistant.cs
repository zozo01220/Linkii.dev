using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;

namespace Linkii.Poc;

/// <summary>Une action proposée par l'assistant, prête à être confirmée. <see cref="Label"/> dit en français ce qui va se passer.</summary>
public record AiAction(string Op, Guid ScreenId, string Label, string Title = "", string Message = "", string Look = "information", int Seconds = 30, string Command = "");

/// <summary>Réponse de l'assistant : une phrase, des actions à confirmer, un lien éventuel vers la page concernée.</summary>
public record AiAnswer(bool Ok, string Say, List<AiAction> Actions, string? Link, string? LinkLabel, string Message);

/// <summary>
/// Assistant (Pro, piste 5) : la personne écrit ce qu'elle veut (« affiche un message d'accueil sur le hall », « quels écrans sont hors ligne ? ») ; l'IA répond
/// d'après l'état des écrans et propose des actions de la même API que les clés (message affiché par-dessus la diffusion, retrait du message, redémarrage
/// du player, extinction ou réveil de l'écran). Rien ne part sans « Confirmer », action par action, avec les droits de la personne. L'assistant ne publie pas
/// et ne supprime rien. L'IA ne voit que des repères courts (S1, S2…), jamais d'identifiants : un repère inconnu est ignoré.
/// </summary>
public class AiAssistant(JsonStore root, AiService ai, NoticeService notices, DeviceCommandService commands, ILogger<AiAssistant> log)
{
    private const int MaxScreens = 60, MaxLists = 40, MaxActions = 20, MaxChars = 500;
    private static readonly string[] Commands = { "restart-player", "screen-off", "wake" };

    private const string System = """
        Tu es l'assistant de Linkivi, un logiciel d'affichage dynamique. Tu aides une personne à suivre et commander ses écrans, en français, de façon brève.
        Tu reçois l'état des écrans (repères S1, S2…) et des listes de diffusion (L1, L2…), puis sa demande.
        Réponds uniquement par un objet JSON, sans commentaire autour :
        {"say":"ta réponse en une à trois phrases","actions":[ ... ],"open":null}
        Chaque action est l'une de :
        - {"op":"show_message","screens":["S1"],"title":"60 caractères au plus","message":"300 caractères au plus","look":"information","seconds":30} : affiche un message par-dessus la diffusion (look : information ou attention ; seconds de 5 à 3600).
        - {"op":"clear_message","screens":["S1"]} : retire le message affiché.
        - {"op":"command","screens":["S1"],"command":"restart-player"} : command = restart-player (redémarrer l'application), screen-off (éteindre l'écran) ou wake (réveiller l'écran).
        « open » : un repère (S1 ou L1) ou « media », « screens », « playlists », « settings » pour proposer d'ouvrir la page concernée, sinon null.
        Règles : n'agis que sur ce qui est demandé ; si la demande est floue ou vise des écrans que tu ne reconnais pas, pose une question dans « say » et laisse « actions » vide.
        Tu ne peux pas publier une liste, la modifier, supprimer ou éteindre l'appareil : dans ce cas, explique-le et propose « open ». Un écran hors ligne ne peut pas recevoir de message : dis-le.
        N'invente aucun écran ni aucune liste. N'écris dans un message que ce que la personne demande.
        """;

    private record Ctx(Dictionary<string, Guid> Screens, Dictionary<string, (Guid Id, string Name)> Lists, Dictionary<Guid, string> Names, string Text);

    private Ctx Context(Guid clientId) => root.Read(d =>
    {
        var now = DateTime.UtcNow;
        var t = d.Clients.First(c => c.Id == clientId);
        var screens = new Dictionary<string, Guid>(); var names = new Dictionary<Guid, string>(); var lists = new Dictionary<string, (Guid, string)>();
        var sb = new System.Text.StringBuilder("Écrans :\n");
        var i = 0;
        foreach (var s in d.Screens.Where(s => s.ClientId == clientId && s.Token != null).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Take(MaxScreens))
        {
            var v = ScreenEvents.View(d, t, s, now);
            var key = "S" + ++i;
            screens[key] = s.Id; names[s.Id] = s.Name;
            sb.Append($"{key} · {s.Name} · {(v.Online ? (v.Asleep ? "en ligne, en veille" : "en ligne") : "hors ligne")} · liste : {v.Playlist?.Name ?? "aucune"} · aire : {v.Area?.Name ?? "-"}\n");
        }
        if (i == 0) sb.Append("(aucun écran)\n");
        sb.Append("\nListes de diffusion :\n");
        var j = 0;
        foreach (var p in d.Playlists.Where(p => p.ClientId == clientId).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Take(MaxLists))
        {
            var key = "L" + ++j;
            lists[key] = (p.Id, p.Name);
            sb.Append($"{key} · {p.Name} · {d.Screens.Count(s => s.PlaylistId == p.Id)} écran(s)\n");
        }
        if (j == 0) sb.Append("(aucune liste)\n");
        return new Ctx(screens, lists, names, sb.ToString());
    });

    public async Task<AiAnswer> Ask(Guid clientId, string request, CancellationToken ct = default)
    {
        request = (request ?? "").Trim();
        if (request.Length < 2) return new AiAnswer(false, "", new(), null, null, "Écrivez ce que vous voulez faire ou savoir.");
        if (request.Length > MaxChars) return new AiAnswer(false, "", new(), null, null, "Cette demande est trop longue : une ou deux phrases suffisent.");
        var ctx = Context(clientId);
        var r = await ai.Complete(clientId, "assistant", System, new UserMessageParam { Content = ctx.Text + "\nDemande : " + request }, 1200, ct);
        if (!r.Ok) return new AiAnswer(false, "", new(), null, null, r.Message);
        var a = r.Text.IndexOf('{'); var b = r.Text.LastIndexOf('}');
        if (a < 0 || b <= a) return new AiAnswer(false, "", new(), null, null, "L'assistant n'a pas compris cette demande. Reformulez-la.");
        try
        {
            if (JsonNode.Parse(r.Text[a..(b + 1)]) is not JsonObject o) return new AiAnswer(false, "", new(), null, null, "L'assistant n'a pas compris cette demande. Reformulez-la.");
            var say = (Str(o, "say") ?? "").Trim();
            var actions = new List<AiAction>();
            foreach (var x in (o["actions"] as JsonArray ?? new()).OfType<JsonObject>())
            {
                foreach (var key in (x["screens"] as JsonArray ?? new()).Select(k => k?.GetValue<string>() ?? "").Distinct())
                {
                    if (actions.Count >= MaxActions) break;
                    if (!ctx.Screens.TryGetValue(key, out var sid)) continue;
                    if (Build(x, sid, ctx.Names[sid]) is { } act) actions.Add(act);
                }
            }
            var (link, label) = Link(Str(o, "open"), ctx);
            if (say.Length == 0) say = actions.Count > 0 ? "Voici ce que je propose." : "Je n'ai rien à proposer pour cette demande.";
            return new AiAnswer(true, say.Length > 600 ? say[..600] : say, actions, link, label, "");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            log.LogInformation(e, "Réponse illisible de l'assistant.");
            return new AiAnswer(false, "", new(), null, null, "L'assistant n'a pas compris cette demande. Reformulez-la.");
        }
    }

    private static AiAction? Build(JsonObject x, Guid screen, string name)
    {
        switch (Str(x, "op"))
        {
            case "show_message":
                var title = Cut(Str(x, "title"), 60); var msg = Cut(Str(x, "message"), 300);
                if (title.Length == 0 && msg.Length == 0) return null;
                var look = Str(x, "look") == "attention" ? "attention" : "information";
                var sec = x["seconds"] is JsonValue v && v.TryGetValue<double>(out var dd) ? (int)Math.Clamp(Math.Round(dd), 5, 3600) : 30;
                var shown = title.Length > 0 ? title : msg;
                return new AiAction("show_message", screen, $"Afficher « {Cut(shown, 50)} » sur {name} ({Duration(sec)})", title, msg, look, sec);
            case "clear_message":
                return new AiAction("clear_message", screen, $"Retirer le message affiché sur {name}");
            case "command":
                var c = (Str(x, "command") ?? "").Trim().ToLowerInvariant();
                if (!Commands.Contains(c)) return null;
                return new AiAction("command", screen, c switch { "restart-player" => $"Redémarrer l'application de {name}", "screen-off" => $"Éteindre l'écran de {name}", _ => $"Réveiller l'écran de {name}" }, Command: c);
            default: return null;
        }
    }

    private static string Duration(int s) => s < 60 ? s + " s" : s % 60 == 0 ? s / 60 + " min" : s / 60 + " min " + s % 60 + " s";

    private static (string?, string?) Link(string? open, Ctx ctx)
    {
        if (string.IsNullOrEmpty(open)) return (null, null);
        if (ctx.Lists.TryGetValue(open, out var l)) return ("/playlists/" + l.Id, "Ouvrir la liste « " + l.Name + " »");
        if (ctx.Screens.ContainsKey(open)) return ("/", "Ouvrir la page Écrans");
        return open switch
        {
            "media" => ("/media", "Ouvrir la médiathèque"),
            "screens" => ("/", "Ouvrir la page Écrans"),
            "playlists" => ("/playlists", "Ouvrir les listes de diffusion"),
            "settings" => ("/settings", "Ouvrir les réglages"),
            _ => (null, null),
        };
    }

    /// <summary>Exécute une action confirmée, avec les droits de la personne. Rend null si tout va bien, sinon le message à montrer.</summary>
    public async Task<string?> Run(Guid clientId, Guid? userId, AiAction a)
    {
        const string by = "assistant IA";
        try
        {
            switch (a.Op)
            {
                case "show_message":
                    return (await notices.Show(clientId, userId, a.ScreenId, a.Title, a.Message, a.Look, a.Seconds, false, by)).Error;
                case "clear_message":
                    return (await notices.Clear(clientId, userId, a.ScreenId, by)).Error;
                case "command":
                    if (!Commands.Contains(a.Command)) return "Commande inconnue.";
                    var res = await commands.Run(clientId, userId, a.ScreenId, a.Command, by);
                    return res.NotFound ? "Écran introuvable." : res.Ok ? null : res.Error;
                default: return "Action inconnue.";
            }
        }
        catch (UnauthorizedAccessException) { return "Vous n'avez pas les droits pour cette action."; }
        catch (Exception e) { log.LogWarning(e, "Action de l'assistant en échec."); return "L'action n'a pas pu être faite."; }
    }

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static string Cut(string? s, int n) => (s ?? "").Trim() is var t && t.Length > n ? t[..n] : (s ?? "").Trim();
}
