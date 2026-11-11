using Microsoft.AspNetCore.Components;

namespace Linkii.Poc;

/// <summary>
/// Discussion avec l'assistant, gardée pour son auteur seul (ni l'administrateur ni les autres membres ne la voient) :
/// 30 jours après le dernier échange elle s'efface, sauf si elle est épinglée ; au plus 50 par personne et par organisation.
/// Maquette : maquettes/ia-assistant-panneau.html.
/// </summary>
public class AiChat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Page où la discussion a commencé (« Listes de diffusion »), montrée dans la liste des discussions récentes.</summary>
    public string Page { get; set; } = "";
    /// <summary>Première partie de l'adresse de cette page (« playlists ») : choisit l'icône de la discussion.</summary>
    public string Area { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime Updated { get; set; } = DateTime.UtcNow;
    public bool Pinned { get; set; }
    public List<AiChatLine> Lines { get; set; } = new();
}

/// <summary>Un échange : la demande (et d'où elle vient : « Bandeau · Message »), la réponse, et les actions avec ce qu'elles sont devenues.</summary>
public class AiChatLine
{
    public string Ask { get; set; } = "";
    public string? From { get; set; }
    public string Say { get; set; } = "";
    public List<AiChatAct> Acts { get; set; } = new();
    public DateTime At { get; set; } = DateTime.UtcNow;
}

/// <summary>Action proposée par l'assistant ; <see cref="State"/> : « fait », « annulé », « échec » ou « non confirmé ».</summary>
public class AiChatAct
{
    public string Label { get; set; } = "";
    public string State { get; set; } = "";
}

/// <summary>Règles de conservation et d'accès des discussions (pures, testées sans interface).</summary>
public static class AiChats
{
    public const int KeepDays = 30, Max = 50;

    /// <summary>Les discussions de cette personne dans cette organisation, la plus récente d'abord. Personne d'autre n'y a accès.</summary>
    public static List<AiChat> Mine(Db d, Guid clientId, Guid userId) =>
        d.AiChats.Where(c => c.ClientId == clientId && c.UserId == userId).OrderByDescending(c => c.Updated).ToList();

    public static AiChat? Get(Db d, Guid clientId, Guid userId, Guid id) =>
        d.AiChats.FirstOrDefault(c => c.Id == id && c.ClientId == clientId && c.UserId == userId);

    /// <summary>Efface les discussions non épinglées de plus de 30 jours, puis garde les 50 plus récentes (les épinglées restent toujours).</summary>
    public static void Purge(Db d, Guid clientId, Guid userId, DateTime now)
    {
        d.AiChats.RemoveAll(c => c.ClientId == clientId && c.UserId == userId && !c.Pinned && c.Updated < now.AddDays(-KeepDays));
        var extra = d.AiChats.Where(c => c.ClientId == clientId && c.UserId == userId && !c.Pinned).OrderByDescending(c => c.Updated).Skip(Max).ToHashSet();
        if (extra.Count > 0) d.AiChats.RemoveAll(extra.Contains);
    }

    /// <summary>Titre fait tout seul : la première demande, coupée proprement.</summary>
    public static string TitleOf(string ask)
    {
        var t = string.Join(' ', (ask ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (t.Length <= 60) return t.Length == 0 ? "Discussion" : t;
        var cut = t[..60];
        var sp = cut.LastIndexOf(' ');
        return (sp > 30 ? cut[..sp] : cut).TrimEnd(',', ';', ':', '.') + "…";
    }

    /// <summary>Rubrique de la liste des discussions récentes.</summary>
    public static string Group(AiChat c, DateTime nowLocal, DateTime updatedLocal)
    {
        if (c.Pinned) return "Épinglées";
        var days = (nowLocal.Date - updatedLocal.Date).Days;
        return days <= 0 ? "Aujourd'hui" : days == 1 ? "Hier" : days < 7 ? "Cette semaine" : "Plus ancien";
    }

    public static readonly string[] Groups = ["Épinglées", "Aujourd'hui", "Hier", "Cette semaine", "Plus ancien"];

    /// <summary>
    /// Ce que l'IA reçoit d'une discussion : les plus anciens échanges en résumé (la demande et le nombre d'actions faites),
    /// les 6 derniers en entier. Moins cher que tout renvoyer, assez pour enchaîner (« et sur l'autre écran aussi »).
    /// </summary>
    public static string Conversation(IReadOnlyList<AiChatLine> lines)
    {
        var sb = new System.Text.StringBuilder();
        var cut = Math.Max(0, lines.Count - 6);
        if (cut > 0)
        {
            sb.Append("Plus tôt dans la discussion (résumé) :\n");
            foreach (var l in lines.Take(cut))
            {
                var done = l.Acts.Count(a => a.State == "fait");
                sb.Append("  - ").Append(l.Ask.Length > 0 ? l.Ask : l.Say).Append(done > 0 ? $" ({done} action{(done > 1 ? "s" : "")} faite{(done > 1 ? "s" : "")})" : "").Append('\n');
            }
        }
        foreach (var l in lines.Skip(cut))
        {
            if (l.Ask.Length > 0) sb.Append("Personne").Append(l.From != null ? " (depuis « " + l.From + " »)" : "").Append(" : ").Append(l.Ask).Append('\n');
            if (l.Say.Length > 0) sb.Append("Assistant : ").Append(l.Say).Append('\n');
            foreach (var a in l.Acts) sb.Append("  - ").Append(a.Label).Append(" [").Append(a.State).Append("]\n");
        }
        return sb.ToString();
    }
}

/// <summary>Un échange affiché dans le panneau : une demande à l'assistant, une carte posée par une page (« Améliorer », relecture…), ou un ancien échange.</summary>
public sealed class PanelTurn
{
    public string Ask = "";
    public string? From;
    public DateTime At = DateTime.UtcNow;

    // réponse de l'assistant et exécution de ses actions
    public AiAnswer? Answer;
    public readonly HashSet<int> Picked = new(), Undone = new();
    public readonly Dictionary<int, AiRun> Done = new();
    public int? Running;
    public int RunTotal, RunDone;

    // carte d'une page : son contenu reste celui de la page (ses boutons agissent sur la page), jusqu'à ce qu'elle la retire
    public string? Key;
    public RenderFragment? Card;
    public Func<string>? Summary;

    // échange terminé : carte retirée, ou discussion reprise depuis l'historique
    public string? Said;
    public List<AiChatAct>? Past;
    public bool Old;

    public bool Live => Card != null;

    public AiChatLine ToLine()
    {
        var line = new AiChatLine { Ask = Ask, From = From, At = At };
        if (Live) line.Say = Summary?.Invoke() ?? "";
        else if (Answer is { } a)
        {
            line.Say = a.Say;
            for (var i = 0; i < a.Actions.Count; i++)
                line.Acts.Add(new AiChatAct
                {
                    Label = a.Actions[i].Label,
                    State = Done.TryGetValue(i, out var r) ? (r.Error != null ? "échec" : Undone.Contains(i) ? "annulé" : "fait") : "non confirmé",
                });
        }
        else { line.Say = Said ?? ""; line.Acts = Past ?? new(); }
        return line;
    }

    public static PanelTurn FromLine(AiChatLine l) => new() { Ask = l.Ask, From = l.From, At = l.At, Said = l.Say, Past = l.Acts, Old = true };
}

/// <summary>
/// Panneau de l'assistant (un par onglet ouvert, vit dans MainLayout) : ouvert ou fermé d'une page à l'autre, la discussion en cours,
/// et les cartes que les pages y posent. Toute l'IA passe par lui : « Améliorer » d'un champ, relecture d'une liste, « Créer avec l'IA »…
/// La page garde la main sur sa carte (ses boutons appellent la page) et la retire en se fermant ; il en reste un résumé dans la discussion.
/// </summary>
public sealed class AssistantPanel(JsonStore root, TenantContext ctx)
{
    public bool IsOpen { get; private set; }
    /// <summary>Une demande ou des actions sont en cours (pastille sur le bouton quand le panneau est fermé).</summary>
    public bool Busy { get; set; }
    public event Action? Changed;

    public List<PanelTurn> Turns { get; } = new();
    public Guid? ChatId { get; private set; }
    public string Title { get; private set; } = "";

    /// <summary>Page ouverte (nom et adresse), tenue par MainLayout : l'assistant sait où l'on est.</summary>
    public string Page { get; set; } = "";
    public string Path { get; set; } = "";

    public void Open() { if (IsOpen) return; IsOpen = true; Changed?.Invoke(); }
    public void Close() { if (!IsOpen) return; IsOpen = false; Changed?.Invoke(); }
    public void Toggle() { IsOpen = !IsOpen; Changed?.Invoke(); }
    /// <summary>État retenu par le navigateur, lu au chargement (fermé au tout premier lancement).</summary>
    public void Restore(bool open) { if (IsOpen == open) return; IsOpen = open; Changed?.Invoke(); }
    public void Refresh() => Changed?.Invoke();

    /// <summary>Pose (ou met à jour) la carte d'une page et ouvre le panneau.</summary>
    public void Show(string key, string ask, string? from, RenderFragment card, Func<string> summary)
    {
        var t = Turns.FirstOrDefault(x => x.Key == key && x.Live);
        if (t == null) Turns.Add(t = new PanelTurn { Key = key, Ask = ask, From = from });
        else { t.Ask = ask; t.From = from; }
        t.Card = card; t.Summary = summary;
        IsOpen = true;
        Save();
        Changed?.Invoke();
    }

    public bool Has(string key) => Turns.Any(x => x.Key == key && x.Live);

    /// <summary>La page retire sa carte (elle se ferme, ou l'échange est fini) : il en reste son résumé.</summary>
    public void Release(string key)
    {
        var t = Turns.FirstOrDefault(x => x.Key == key && x.Live);
        if (t == null) return;
        try { t.Said = t.Summary?.Invoke() ?? ""; } catch (Exception) { t.Said = ""; }
        t.Card = null; t.Summary = null;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Nouvelle discussion : la précédente reste dans l'historique, les cartes des pages sont retirées.</summary>
    public void New()
    {
        foreach (var t in Turns.Where(x => x.Live).ToList()) { t.Said = t.Summary?.Invoke() ?? ""; t.Card = null; t.Summary = null; }
        Save();
        Turns.Clear(); ChatId = null; Title = "";
        Changed?.Invoke();
    }

    /// <summary>Ce que l'IA reçoit : la page ouverte (si la personne ne l'a pas retirée) puis la discussion.</summary>
    public string Conversation(bool withPage)
    {
        var lines = Turns.Select(t => t.ToLine()).ToList();
        var page = withPage && Page.Length > 0 ? $"La personne regarde la page « {Page} » (adresse /{Path}).\n" : "";
        return page + AiChats.Conversation(lines);
    }

    // ---------- discussions gardées ----------

    private (Guid Client, Guid User)? Who => ctx.ClientId is { } c && ctx.UserId is { } u ? (c, u) : null;

    /// <summary>Enregistre la discussion en cours (la crée au premier échange).</summary>
    public void Save()
    {
        if (Who is not { } w || Turns.Count == 0) return;
        var lines = Turns.Select(t => t.ToLine()).ToList();
        var now = DateTime.UtcNow;
        var first = Turns[0];
        root.Write(d =>
        {
            var chat = ChatId is { } id ? AiChats.Get(d, w.Client, w.User, id) : null;
            if (chat == null)
            {
                chat = new AiChat { ClientId = w.Client, UserId = w.User, Title = AiChats.TitleOf(first.Ask), Page = PageLabel(Page), Area = Path.Split('/')[0], Created = now };
                d.AiChats.Add(chat);
            }
            chat.Lines = lines; chat.Updated = now;
            ChatId = chat.Id; Title = chat.Title;
            AiChats.Purge(d, w.Client, w.User, now);
        });
    }

    private static string PageLabel(string page) { var i = page.IndexOf(" · ", StringComparison.Ordinal); return i > 0 ? page[..i] : page; }

    public List<AiChat> History()
    {
        if (Who is not { } w) return new();
        return root.Read(d => AiChats.Mine(d, w.Client, w.User).Where(c => c.Pinned || c.Updated >= DateTime.UtcNow.AddDays(-AiChats.KeepDays)).ToList());
    }

    /// <summary>Reprend une discussion : ses échanges reviennent dans le panneau, on peut la continuer.</summary>
    public bool Load(Guid id)
    {
        if (Who is not { } w) return false;
        var chat = root.Read(d => AiChats.Get(d, w.Client, w.User, id));
        if (chat == null) return false;
        foreach (var t in Turns.Where(x => x.Live).ToList()) { t.Said = t.Summary?.Invoke() ?? ""; t.Card = null; t.Summary = null; }
        if (Turns.Count > 0) Save();
        Turns.Clear();
        Turns.AddRange(chat.Lines.Select(PanelTurn.FromLine));
        ChatId = chat.Id; Title = chat.Title;
        Changed?.Invoke();
        return true;
    }

    public void Pin(Guid id, bool on) => Edit(id, c => c.Pinned = on);

    public void Rename(Guid id, string title)
    {
        var t = string.Join(' ', (title ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (t.Length == 0) return;
        if (t.Length > 80) t = t[..80];
        Edit(id, c => c.Title = t);
        if (ChatId == id) Title = t;
    }

    public void Delete(Guid id)
    {
        if (Who is not { } w) return;
        root.Write(d => d.AiChats.RemoveAll(c => c.Id == id && c.ClientId == w.Client && c.UserId == w.User));
        if (ChatId == id) { Turns.RemoveAll(t => !t.Live); ChatId = null; Title = ""; }
        Changed?.Invoke();
    }

    public void DeleteAll()
    {
        if (Who is not { } w) return;
        root.Write(d => d.AiChats.RemoveAll(c => c.ClientId == w.Client && c.UserId == w.User));
        Turns.RemoveAll(t => !t.Live); ChatId = null; Title = "";
        Changed?.Invoke();
    }

    private void Edit(Guid id, Action<AiChat> f)
    {
        if (Who is not { } w) return;
        root.Write(d => { if (AiChats.Get(d, w.Client, w.User, id) is { } c) f(c); });
        Changed?.Invoke();
    }
}
