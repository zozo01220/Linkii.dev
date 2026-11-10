using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic.Models.Messages;

namespace Linkii.Poc;

/// <summary>
/// Médiathèque intelligente (Pro, maquette maquettes/ia-pistes.html, onglet 3) : à l'import d'une image, l'IA écrit ce qu'elle montre, propose des mots-clés,
/// lit le texte qu'elle porte et choisit le centre d'un cadrage portrait. La recherche de la médiathèque s'en sert ; tout reste modifiable.
/// Une demande par image, jamais refaite tant que la personne ne le demande pas. Quota atteint : l'image reste simplement sans description.
/// </summary>
public class AiMedia(JsonStore root, AiService ai, ILogger<AiMedia> log)
{
    private static readonly HashSet<string> Decodable = new() { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly ConcurrentDictionary<Guid, byte> _running = new();

    private const string System = """
        Tu décris des images pour la médiathèque d'un logiciel d'affichage dynamique (écrans d'accueil, vitrines, salles d'attente).
        Réponds uniquement par un objet JSON, sans commentaire autour :
        {"description":"une à deux phrases en français, simples, qui disent ce que montre l'image","tags":["5 à 8 mots-clés courts en français, en minuscules, utiles pour retrouver l'image (sujet, lieu, ambiance, couleurs dominantes, saison)"],"text":["chaque ligne de texte lisible sur l'image, telle qu'écrite ; liste vide s'il n'y en a pas"],"template":null}
        « template » : si l'image montre un texte de modèle oublié (« Insérez votre texte ici », « Lorem ipsum », « 20XX », « Titre de la présentation »), recopie-le ; sinon null.
        N'invente rien : décris seulement ce qui est visible.
        """;

    // ---------- Analyse ----------

    /// <summary>Lance l'analyse d'une image qui vient d'être importée, sans attendre (l'import ne doit pas ralentir). Rien si l'IA n'est pas disponible.</summary>
    public void Queue(Guid clientId, Guid mediaId)
    {
        if (!ai.State(clientId, "media").Available) return;
        _ = Task.Run(async () =>
        {
            try { await Analyze(clientId, mediaId, CancellationToken.None); }
            catch (Exception e) { log.LogWarning(e, "Analyse de l'image impossible."); }
        });
    }

    /// <summary>Analyse une image. Rend Ok, ou le message à montrer (quota, IA coupée…).</summary>
    public async Task<AiResult> Analyze(Guid clientId, Guid mediaId, CancellationToken ct)
    {
        var path = root.Read(d => d.Media.FirstOrDefault(m => m.Id == mediaId && m.ClientId == clientId && m.Type == "image" && !m.Off)?.FilePath);
        if (path == null || !Decodable.Contains(Path.GetExtension(path).ToLowerInvariant()) || !File.Exists(path)) return new AiResult(false, "", "Cette image ne peut pas être analysée.");
        if (!_running.TryAdd(mediaId, 0)) return new AiResult(true, "", "");   // déjà en cours
        await _slots.WaitAsync(ct);
        try
        {
            var state = ai.State(clientId, "media");
            if (!state.Available) return new AiResult(false, "", AiService.Say(state));
            var b64 = AiReview.Jpeg(path);
            if (b64 == null) return new AiResult(false, "", "Cette image ne peut pas être analysée.");
            var blocks = new List<ContentBlockParam>
            {
                new ImageBlockParam { Source = new Base64ImageSource { Data = b64, MediaType = MediaType.ImageJpeg } },
                new TextBlockParam { Text = "Décris cette image." },
            };
            var r = await ai.Complete(clientId, "media", System, new UserMessageParam { Content = blocks }, 600, ct);
            if (!r.Ok) return r;
            if (Parse(r.Text) is not { } res) return new AiResult(false, "", "L'IA n'a pas pu décrire cette image.");
            root.Write(d =>
            {
                if (d.Media.FirstOrDefault(m => m.Id == mediaId) is not { } m) return;
                m.AiDesc = res.Desc; m.AiTags = res.Tags; m.AiText = res.Text; m.AiFocus = res.Focus; m.AiTemplate = res.Template; m.AiDone = true;
            });
            return new AiResult(true, "", "");
        }
        finally { _slots.Release(); _running.TryRemove(mediaId, out _); }
    }

    /// <summary>Images prêtes, pas encore analysées, de l'organisation (celles que « Analyser mes images » traiterait).</summary>
    public int Pending(Guid clientId) => root.Read(d => d.Media.Count(m => m.ClientId == clientId && Candidate(m)));

    private static bool Candidate(MediaItem m) =>
        m.Type == "image" && !m.Off && !m.AiDone && m.Status == null && m.FileName != null && Decodable.Contains(Path.GetExtension(m.FileName).ToLowerInvariant());

    /// <summary>Analyse jusqu'à <paramref name="max"/> images en attente, les plus récentes d'abord ; s'arrête dès que l'IA se met en pause. Rend le nombre traité et le message de fin éventuel.</summary>
    public async Task<(int Done, string Message)> AnalyzeMissing(Guid clientId, int max, CancellationToken ct = default)
    {
        var ids = root.Read(d => d.Media.Where(m => m.ClientId == clientId && Candidate(m)).OrderByDescending(m => m.AddedUtc).Take(max).Select(m => m.Id).ToList());
        var done = 0;
        foreach (var id in ids)
        {
            var r = await Analyze(clientId, id, ct);
            if (!r.Ok) return (done, r.Message);
            done++;
        }
        return (done, "");
    }

    private record Found(string Desc, List<string> Tags, List<string> Text, int? Focus, string? Template);

    private static Found? Parse(string answer)
    {
        var a = answer.IndexOf('{'); var b = answer.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try
        {
            if (JsonNode.Parse(answer[a..(b + 1)]) is not JsonObject o) return null;
            var desc = (o["description"]?.GetValue<string>() ?? "").Trim();
            if (desc.Length == 0) return null;
            List<string> Strs(string key, int max, int len) => (o[key] as JsonArray)?.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "").Where(s => s.Length > 0).Select(s => s.Length > len ? s[..len] : s).Distinct(StringComparer.CurrentCultureIgnoreCase).Take(max).ToList() ?? new();
            int? focus = null;
            if (o["focus"] is JsonValue f && f.TryGetValue<double>(out var fd)) focus = (int)Math.Clamp(Math.Round(fd), 0, 100);
            var tpl = o["template"] is JsonValue t && t.TryGetValue<string>(out var ts) && ts.Trim().Length > 0 ? ts.Trim() : null;
            return new Found(desc.Length > 400 ? desc[..400] : desc, Strs("tags", 10, 40), Strs("text", 12, 160), focus, tpl is { Length: > 160 } ? tpl[..160] : tpl);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    // ---------- Recherche ----------

    private static readonly HashSet<string> Stop = new() { "avec", "des", "du", "de", "la", "le", "les", "un", "une", "et", "en", "sur", "photo", "photos", "image", "images", "qui", "au", "aux", "d", "l", "a", "y", "il" };
    private static readonly Dictionary<string, string> Syn = new()
    {
        ["gens"] = "personne", ["monde"] = "personne", ["personnes"] = "personne", ["humain"] = "personne", ["soleil"] = "coucher de soleil", ["couchant"] = "coucher de soleil",
        ["ocean"] = "mer", ["plages"] = "plage", ["textes"] = "texte", ["ecrit"] = "texte", ["etoiles"] = "ciel etoile", ["etoile"] = "ciel etoile", ["montagnes"] = "montagne",
    };

    public static string Norm(string s)
    {
        var d = (s ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Mots cherchés : sans mots vides, avec quelques synonymes courants (« gens » : personne). Vide : la recherche ne dit rien de plus que le nom.</summary>
    public static List<string> Terms(string q) =>
        Regex.Split(Norm(q), "[^a-z0-9]+").Where(t => t.Length > 0 && !Stop.Contains(t)).Select(t => Syn.GetValueOrDefault(t, t)).Distinct().ToList();

    /// <summary>
    /// L'image répond à la recherche : tous les mots sont dans son nom, sa description, ses mots-clés ou son texte. <paramref name="hit"/> dit ce qui a répondu
    /// quand ce n'est pas le nom (pastille sur la vignette) : un mot-clé, « texte : … » ou « description ».
    /// </summary>
    public static bool Matches(MediaItem m, List<string> terms, out string? hit)
    {
        hit = null;
        var name = Norm(m.Name);
        if (terms.All(name.Contains)) return true;
        if (!m.AiDone) return false;
        var hay = Norm(string.Join(' ', new[] { m.Name, m.AiDesc ?? "" }.Concat(m.AiTags).Concat(m.AiText)));
        if (!terms.All(hay.Contains)) return false;
        hit = m.AiTags.FirstOrDefault(x => terms.Any(Norm(x).Contains))
            ?? (m.AiText.FirstOrDefault(x => terms.Any(Norm(x).Contains)) is { } tx ? "texte : « " + tx + " »" : "description");
        return true;
    }
}
