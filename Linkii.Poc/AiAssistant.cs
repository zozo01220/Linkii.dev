using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic.Models.Messages;

namespace Linkii.Poc;

/// <summary>Une action proposée par l'assistant, prête à être confirmée. <see cref="Label"/> dit en français ce qui va se passer ; <see cref="A"/> garde ses arguments, déjà contrôlés.</summary>
public record AiAction(string Op, string Label, Dictionary<string, string> A);

/// <summary>Résultat d'une action confirmée : une erreur, ou une information, et de quoi la défaire quand c'est possible.</summary>
public record AiRun(string? Error, string? Info = null, Func<Task<string?>>? Undo = null);

/// <summary>Réponse de l'assistant : une phrase, des actions à confirmer, un lien éventuel vers la page concernée.</summary>
public record AiAnswer(bool Ok, string Say, List<AiAction> Actions, string? Link, string? LinkLabel, string Message);

/// <summary>Étapes d'une demande à l'assistant, dans l'ordre : lire l'organisation, attendre l'IA, recevoir sa réponse, contrôler les actions.</summary>
public enum AiStep { Reading, Thinking, Writing, Checking }

/// <summary>Où en est une demande : l'étape, ce qui a été lu, le début de la réponse et les sortes d'actions déjà annoncées par l'IA.</summary>
public record AiProgress(AiStep Step, string Read, string Say, IReadOnlyList<string> Ops);

/// <summary>
/// Assistant (Pro) : la personne écrit ce qu'elle veut savoir ou faire ; l'IA répond d'après l'état de son organisation (écrans, listes, compositions, médias, calendriers,
/// historique récent) et propose des actions que la personne confirme une à une. Le contexte de la conversation est gardé par la fenêtre et renvoyé à chaque demande.
/// Tout passe par <see cref="TenantStore"/> et les services de l'application : mêmes droits, mêmes filtres d'aire et de périmètre que les pages. L'IA ne voit que
/// des repères courts (S1, L1, C1, M1, K1) ; un repère inconnu est ignoré. Une liste ou un calendrier créé dans la réponse reçoit un repère « ref » (NL1, NK1) et un
/// identifiant tiré d'avance, pour que les actions suivantes l'utilisent (créer la liste, y mettre la composition, la programmer, assigner le calendrier).
/// L'assistant ne publie pas : un changement de liste d'un écran, un bandeau ou un contenu
/// de liste attend le bouton « Publier » (les calendriers, eux, s'appliquent tout de suite comme sur leur page).
/// </summary>
public class AiAssistant(TenantStore store, AiService ai, AiMedia aiMedia, AiCompose compose, AiReview review, NoticeService notices, DeviceCommandService commands,
    ScreenControl control, Notifier notifier, AppService apps, MediaLibrary library, HistoryLog history, WebImages webImages, AssistantPanel panel, AiInteractive interactive,
    Microsoft.AspNetCore.Components.NavigationManager nav, ILogger<AiAssistant> log)
{
    private const int MaxActions = 25, MaxChars = 600;
    private static readonly string[] Commands = { "restart-player", "screen-off", "wake", "reboot" };
    private static readonly int[] AlertMinutes = { 1, 5, 10, 15, 30, 60 };
    private static readonly Regex Token = new(@"\{\{[^}]*\}\}", RegexOptions.Compiled);
    private const string By = "assistant IA";

    private const string System = """
        Tu es l'assistant de Linkivi, un logiciel d'affichage dynamique. Tu aides une personne à suivre et à gérer ses écrans, ses listes de diffusion, ses compositions et ses images, en français, de façon brève.
        Tu reçois l'état de son organisation avec des repères (S1 écrans, L1 listes, C1 compositions, M1 images, K1 calendriers, T1 listes interactives), le début de la conversation, puis sa demande.
        Réponds uniquement par un objet JSON, sans commentaire autour : {"say":"réponse en une à trois phrases","actions":[ ... ],"open":null}
        Chaque action est un objet avec « op » et ses champs. Un champ « screens » est une liste de repères S. Opérations :
        ÉCRANS
        - show_message {screens,title(60),message(300),look:information|attention,seconds:5-3600} · clear_message {screens}
        - command {screens,command:restart-player|screen-off|wake|reboot}
        - screen_pause {screens,minutes:1-1440} · screen_resume {screens}
        - screen_rename {screen,name} · screen_set_list {screens,list:"L1" ou null} (change la liste par défaut de l'écran ; elle ne passe à l'écran qu'à la publication, que la personne fait elle-même)
        - screen_param {screen,name,value} (variable existante de l'écran) · band_text {screens,band:top|bottom,text(160)} · band_off {screens,band}
        LISTES (brouillon seulement ; « item » est le numéro d'un contenu dans la liste, depuis 1)
        - list_create {name,ref:"NL1"} · list_rename {list,name} · list_duplicate {list,name} · list_review {list} (relit la liste : dates passées, textes oubliés, fautes)
        - list_add {list,content:"C1" ou "M1",seconds} · list_remove {list,item} · list_move {list,item,to} · list_duration {list,item (ou "all"),seconds} · list_transition {list,item (ou "all"),transition:cut|fade|push-left|push-right|push-up|push-down|zoom} · list_toggle {list,item,on:true|false}
        IMAGES ET COMPOSITIONS
        - media_rename {media,name} · media_describe {media,description,tags:[...]} · media_analyze {media} (l'IA décrit l'image)
        - composition_create {sentence,portrait:false,list:"L1" ou null,photo:"auto",ref:"NC1"} (crée une composition depuis une phrase ; « ref » : nouveau repère pour la modifier ou l'ajouter ensuite ; « photo » : "M1" pour une image précise de la médiathèque, "auto" pour qu'elle choisisse une image qui convient, sinon une photo neutre d'internet si c'est disponible, "web" pour une photo d'internet, null pour aucune image)
        - web_image {query,portrait:false} (cherche sur internet une photo d'illustration neutre, sans personne, et l'ajoute à la médiathèque ; « query » : 2 à 4 mots en français ; seulement si « Images d'internet : disponibles »)
        - text_replace {composition,find,replace} (remplace du texte exact dans une composition)
        - text_fix {target:"C1" ou "L1",mode:correct|improve|formal|warm|short} (corrige ou reformule les textes d'une composition, ou de toutes les compositions d'une liste)
        - composition_edit {composition:"C1",change:"la modification, précise et complète",photo:null} (modifie TOUT ce qu'on veut dans une composition : textes, mise en page, tailles, couleurs, polices, fond, format portrait ou paysage, éléments ajoutés, déplacés ou supprimés, formes, horloge, date, bandeau défilant, compte à rebours, code QR, images ; « photo » : "M1" pour placer cette image de la médiathèque, "auto" pour en chercher une qui convient, "web" pour une photo neutre d'internet, null sinon ; une seule action par composition et par demande : mets-y toutes les modifications)
        - composition_rename {composition,name} · composition_duplicate {composition,name,ref:"NC1"} (copie à modifier ensuite, l'originale ne change pas)
        Une composition « (ouverte) » est celle que la personne a sous les yeux : « ici », « cette compo », « la page » parlent d'elle.
        LISTES INTERACTIVES (ce qu'un visiteur touche sur une borne tactile : des pages de tuiles, de textes et d'images ; repères T ; rien à voir avec les listes de diffusion L)
        - ilist_create {name,sentence:"tout ce que la liste doit proposer",portrait:true,ref:"NT1"} (crée la liste entière : pages, tuiles, textes, images, boutons de navigation, contenus ouverts par les tuiles ; « portrait » : le format de la borne visée, true si on ne sait pas)
        - ilist_edit {ilist:"T1",change:"la modification, précise et complète"} (modifie TOUT ce qu'on veut dans une liste interactive. Pages : en ajouter, renommer, dupliquer, réordonner, supprimer, choisir la page d'accueil, couleur ou image de fond d'une page ou de toutes, longueur d'une page de 1 à 6 écrans qui défilent. Éléments : ajouter, déplacer, redimensionner, aligner, répartir, ranger en grille, dupliquer ou supprimer des tuiles, des textes, des images et des boutons « Accueil », « Retour » ou vers une page. Tuile : titre, sous-titre, taille, couleur et police du texte, gras, alignement, place du texte, couleur ou image de fond, arrondi ; ce qu'elle fait au toucher : ouvrir un contenu (média, PDF, présentation, composition, page web), ouvrir une page, retourner à l'accueil, revenir en arrière, rien ; QR code « Emporter sur mon téléphone » ; dates entre lesquelles elle s'affiche. Format portrait ou paysage. Une seule action par liste interactive et par demande : mets-y toutes les modifications)
        - ilist_rename {ilist,name} · ilist_duplicate {ilist,name,ref:"NT1"} (copie à modifier ensuite, l'originale ne change pas)
        - kiosk_set_ilist {screens,ilist:"T1"} (donne la liste interactive à des bornes : seulement des écrans marqués « borne tactile » ; à publier ensuite par la personne)
        Dans « sentence » et « change », écris toute la demande avec les noms exacts des contenus, des images et des pages : l'IA qui construit la liste ne voit pas la conversation. Une liste interactive « (ouverte) » est celle que la personne a sous les yeux.
        PLANNING ET RÉGLAGES
        - calendar_create {name,list,ref:"NK1"} (« list » : liste jouée hors des événements) · calendar_event {calendar,kind:play|off,list,days:[1..7 lundi=1] (absent : tous les jours),from:"HH:mm",to:"HH:mm",always:false} · calendar_assign {screens,calendar:"K1"}
        - alert_delay {minutes:1|5|10|15|30|60} (délai avant l'alerte « écran hors ligne »)
        « open » : un repère (S1, L1, C1, T1) ou « media », « screens », « playlists », « interactive », « programmations », « settings », sinon null.
        ENCHAÎNER : les actions s'exécutent dans l'ordre. Une liste, un calendrier ou une composition que tu crées reçoit dans « ref » un nouveau repère (« NL1 », « NL2 »… pour une liste, « NK1 »… pour un calendrier, « NC1 »… pour une composition, « NT1 »… pour une liste interactive) que les actions suivantes utilisent comme les autres. Crée d'abord, utilise ensuite.
        Fais tout l'enchaînement qu'une demande implique dans la même réponse, sans t'arrêter à la première étape. Un « planning » ou une « programmation » est un calendrier.
        Exemple « programme la compo C2 de 9 h à 11 h sur l'écran S1 » :
          1. la liste : une liste existante qui contient C2 (ou une liste vide du même nom : list_add dedans), sinon list_create {name,ref:"NL1"} puis list_add {list:"NL1",content:"C2"} ;
          2. le calendrier : si S1 a un calendrier qui ne sert qu'à lui, calendar_event dessus ; sinon calendar_create {name,list: la liste que S1 joue aujourd'hui (sinon NL1),ref:"NK1"}, calendar_event {calendar:"NK1",kind:"play",list:"NL1",from:"09:00",to:"11:00"}, calendar_assign {screens:["S1"],calendar:"NK1"}.
        Règles : n'agis que sur ce qui est demandé ; garde en tête la conversation (« sur celui-là aussi » parle de ce dont on vient de parler). Si la demande est floue ou vise ce que tu ne reconnais pas, pose une question dans « say » et laisse « actions » vide.
        Tu ne publies jamais, tu ne supprimes aucun écran, liste, liste interactive, composition, média ni membre, tu n'éteins pas l'appareil : explique-le et propose « open ». Un écran hors ligne ne reçoit pas de message ni de commande : dis-le.
        N'invente aucun repère. N'écris dans un message ou un texte que ce que la personne demande. Pour répondre à une question (état, historique, recherche d'image), « actions » reste vide.
        """;

    // ---------- Contexte ----------

    /// <summary>Ce que l'IA a vu (repères → identifiants) ; <paramref name="Web"/> : l'organisation peut recevoir des images d'internet.</summary>
    private record Ctx(Dictionary<string, Guid> Screens, Dictionary<string, Guid> Lists, Dictionary<string, List<Guid>> Items, Dictionary<string, Guid> Comps,
        Dictionary<string, Guid> Media, Dictionary<string, Guid> Cals, Dictionary<Guid, string> Names, string Text, bool Web, Dictionary<string, Guid> ILists);

    private Ctx Context(string request)
    {
        var terms = AiMedia.Terms(request);
        var norm = AiMedia.Norm(request);
        var now = DateTime.UtcNow;
        var cid = store.ClientId;
        var past = history.Read(cid, now.AddDays(-3)).Where(e => e.Channel != History.Diag).Take(8).ToList();
        var web = webImages.On(cid);
        return store.Read(d =>
        {
            var t = d.Tenant;
            var screens = new Dictionary<string, Guid>(); var lists = new Dictionary<string, Guid>(); var items = new Dictionary<string, List<Guid>>();
            var comps = new Dictionary<string, Guid>(); var media = new Dictionary<string, Guid>(); var cals = new Dictionary<string, Guid>(); var names = new Dictionary<Guid, string>();
            // repères des listes et des calendriers d'abord : la ligne d'un écran dit lesquels il utilise
            var shownLists = d.Playlists.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Take(40).ToList();
            var shownCals = d.Calendars.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Take(20).ToList();
            var listKey = shownLists.Select((p, n) => (p.Id, K: "L" + (n + 1))).ToDictionary(x => x.Id, x => x.K);
            var calKey = shownCals.Select((c, n) => (c.Id, K: "K" + (n + 1))).ToDictionary(x => x.Id, x => x.K);
            var screenKey = new Dictionary<Guid, string>();
            // listes interactives des bornes : celle qui est ouverte (son adresse est dans la conversation) puis celles qui sont nommées
            var ilists = new Dictionary<string, Guid>();
            var ilRights = InteractiveListOps.Rights(d);
            bool IOpen(InteractiveList l) => request.Contains(l.Id.ToString(), StringComparison.OrdinalIgnoreCase);
            var shownIl = d.InteractiveLists.OrderByDescending(IOpen).ThenByDescending(l => l.Name.Length > 2 && norm.Contains(AiMedia.Norm(l.Name)))
                .ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).Take(30).ToList();
            var ilKey = shownIl.Select((l, n) => (l.Id, K: "T" + (n + 1))).ToDictionary(x => x.Id, x => x.K);
            var sb = new StringBuilder("Écrans :\n");
            var i = 0;
            foreach (var s in d.AllScreens.Where(s => s.Token != null).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Take(60))
            {
                var v = ScreenEvents.View(d.Root, t, s, now);
                var key = "S" + ++i; screens[key] = s.Id; names[s.Id] = s.Name; screenKey[s.Id] = key;
                var pars = s.Params.Count > 0 ? " · variables : " + string.Join(", ", s.Params.Take(5).Select(p => $"{p.Name}={p.Value}")) : "";
                var lk = v.Playlist != null && listKey.TryGetValue(v.Playlist.Id, out var lkk) ? $" ({lkk})" : "";
                var cal = s.CalendarId is { } scid ? " · calendrier : " + (calKey.TryGetValue(scid, out var ckk) ? ckk : "oui") : "";
                if (s.Kiosk is { } kc) cal += $" · borne tactile ({(InteractiveLists.Portrait(s.Orientation) ? "portrait" : "paysage")}), liste interactive : {(kc.ListId is { } kl ? ilKey.GetValueOrDefault(kl, "oui") : "aucune")}";
                sb.Append($"{key} · {s.Name} · {(v.Online ? (v.Asleep ? "en ligne, en veille" : "en ligne") : "hors ligne")}{(v.Paused ? ", en pause" : "")} · liste : {v.Playlist?.Name ?? "aucune"}{lk}{cal} · aire : {v.Area?.Name ?? "-"}{pars}\n");
            }
            if (i == 0) sb.Append("(aucun écran)\n");

            var mediaById = d.AllMedia.ToDictionary(m => m.Id, m => m);
            sb.Append("\nListes de diffusion (contenus numérotés) :\n");
            var j = 0;
            foreach (var p in shownLists)
            {
                var key = "L" + ++j; lists[key] = p.Id; names[p.Id] = p.Name; items[key] = p.Draft.Select(x => x.Id).ToList();
                var content = string.Join(" ; ", p.Draft.Take(12).Select((x, n) => $"{n + 1}) {(mediaById.TryGetValue(x.MediaId, out var m) ? m.Name : "?")}{(x.Off ? " (désactivé)" : "")} {x.DurationSec} s"));
                sb.Append($"{key} · {p.Name} · {d.AllScreens.Count(s => s.PlaylistId == p.Id)} écran(s) · {(p.Draft.Count == 0 ? "vide" : content + (p.Draft.Count > 12 ? $" … ({p.Draft.Count} au total)" : ""))}\n");
            }
            if (j == 0) sb.Append("(aucune liste)\n");

            sb.Append("\nCompositions :\n");
            var k = 0;
            var wanted = new HashSet<Guid>();
            foreach (var l in d.Playlists.Where(p => norm.Contains(AiMedia.Norm(p.Name)) && p.Name.Length > 2).Take(3))
                foreach (var it in l.Draft) if (mediaById.TryGetValue(it.MediaId, out var mm) && mm.AppId == Compositions.AppId) wanted.Add(mm.Id);
            // la composition ouverte (son adresse est dans la conversation : « /compositions/… ») et celles qui sont nommées passent en premier, avec leurs textes
            bool Open(MediaItem m) => request.Contains(m.Id.ToString(), StringComparison.OrdinalIgnoreCase);
            bool Named(MediaItem m) => wanted.Contains(m.Id) || m.Name.Length > 2 && norm.Contains(AiMedia.Norm(m.Name));
            foreach (var c in d.Media.Where(m => m.AppId == Compositions.AppId).OrderByDescending(Open).ThenByDescending(Named).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).Take(40).OrderBy(m => Open(m) ? 0 : 1).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var key = "C" + ++k; comps[key] = c.Id; names[c.Id] = c.Name;
                var doc = c.Settings.GetValueOrDefault("doc");
                var open = Open(c);
                var texts = open || Named(c) ? " · textes : " + string.Join(" | ", TextsOf(doc).Take(10).Select(x => "« " + Cut(x, 90) + " »")) : "";
                var size = Compositions.Size(c.Settings) is { } sz ? (sz.H > sz.W ? " · portrait" : " · paysage") : "";
                var right = CompositionRights.RightOf(d, c) < FolderRight.Write ? " · lecture seule (à dupliquer pour la modifier)" : "";
                sb.Append($"{key} · {c.Name}{(open ? " (ouverte)" : "")}{size} · {Compositions.ElementCount(c.Settings)} élément(s){right}{texts}\n");
            }
            if (k == 0) sb.Append("(aucune composition)\n");

            sb.Append("\nImages (celles qui répondent à la demande, puis les plus récentes) :\n");
            var pick = new List<MediaItem>();
            if (terms.Count > 0) pick.AddRange(d.Media.Where(m => m.Type == "image" && AiMedia.Matches(m, terms, out _)).Take(12));
            pick.AddRange(d.Media.Where(m => m.Type == "image").OrderByDescending(m => m.AddedUtc).Take(12));
            var n2 = 0;
            foreach (var m in pick.DistinctBy(m => m.Id).Take(24))
            {
                var key = "M" + ++n2; media[key] = m.Id; names[m.Id] = m.Name;
                sb.Append($"{key} · {m.Name}{(m.AiDesc is { Length: > 0 } ds ? " · " + Cut(ds, 110) : "")}{(m.AiTags.Count > 0 ? " · mots-clés : " + string.Join(", ", m.AiTags.Take(6)) : "")}\n");
            }
            if (n2 == 0) sb.Append("(aucune image)\n");
            sb.Append(web ? $"Images d'internet : disponibles (photos neutres de {WebImages.Provider}, ajoutées à la médiathèque)\n" : "Images d'internet : non disponibles\n");

            sb.Append("\nListes interactives (bornes tactiles ; pages et nombre d'éléments) :\n");
            foreach (var l in shownIl)
            {
                var key = ilKey[l.Id]; ilists[key] = l.Id; names[l.Id] = l.Name;
                var bornes = InteractiveLists.UsedBy(d.AllScreens, l.Id).Count;
                var right = ilRights.GetValueOrDefault(l.Id) < FolderRight.Write ? " · lecture seule (à dupliquer pour la modifier)" : "";
                sb.Append($"{key} · {l.Name}{(IOpen(l) ? " (ouverte)" : "")} · {(InteractiveLists.Portrait(l.Orientation) ? "portrait" : "paysage")} · {l.Pages.Count} page(s) : {string.Join(", ", l.Pages.Take(8).Select(p => $"{p.Name} ({p.Els.Count})"))} · {bornes} borne(s){right}\n");
            }
            if (shownIl.Count == 0) sb.Append("(aucune liste interactive)\n");

            sb.Append("\nCalendriers de programmation :\n");
            var q = 0;
            foreach (var c in shownCals)
            {
                var key = "K" + ++q; cals[key] = c.Id; names[c.Id] = c.Name;
                var on = Calendars.ScreensOf(d, c.Id);
                var dl = d.Playlists.FirstOrDefault(p => p.Id == c.DefaultPlaylistId);
                sb.Append($"{key} · {c.Name} · liste par défaut : {dl?.Name ?? "?"}{(dl != null && listKey.TryGetValue(dl.Id, out var dk) ? $" ({dk})" : "")} · {c.Events.Count} événement(s) · " +
                    (on.Count == 0 ? "assigné à aucun écran" : $"assigné à {on.Count} écran(s) : {string.Join(", ", on.Take(8).Select(s => screenKey.GetValueOrDefault(s.Id, s.Name)))}") + "\n");
            }
            if (q == 0) sb.Append("(aucun calendrier)\n");

            sb.Append($"\nAlerte « écran hors ligne » après {t.AlertOfflineMinutes} minute(s).\n");
            if (past.Count > 0) sb.Append("\nHistorique récent :\n" + string.Join("\n", past.Select(e => $"{e.AtUtc.ToLocalTime():dd.MM HH:mm} · {e.Title}{(e.Actor is { Length: > 0 } a ? " (" + a + ")" : "")}")) + "\n");
            return new Ctx(screens, lists, items, comps, media, cals, names, sb.ToString(), web, ilists);
        });
    }

    private static List<string> TextsOf(string? doc)
    {
        var list = new List<string>();
        try { if (JsonNode.Parse(doc ?? "") is JsonObject o && o["els"] is JsonArray els) foreach (var e in els.OfType<JsonObject>()) if (Str(e, "type") == "text" && Str(e, "text") is { Length: > 0 } t) list.Add(t.Replace("\n", " ")); }
        catch (JsonException) { }
        return list;
    }

    // ---------- Demande ----------

    /// <summary>
    /// Traite une demande. <paramref name="progress"/> (facultatif) est appelé à chaque étape, puis à chaque morceau de la réponse reçue :
    /// la fenêtre montre ainsi ce qui se passe au lieu de faire attendre sans rien dire.
    /// </summary>
    public async Task<AiAnswer> Ask(string request, string conversation, CancellationToken ct = default, Action<AiProgress>? progress = null)
    {
        request = (request ?? "").Trim();
        if (request.Length < 2) return Fail("Écrivez ce que vous voulez faire ou savoir.");
        if (request.Length > MaxChars) return Fail("Cette demande est trop longue : une ou deux phrases suffisent.");
        var none = Array.Empty<string>();
        progress?.Invoke(new AiProgress(AiStep.Reading, "", "", none));
        var ctx = Context(request + " " + conversation);
        var read = ReadSummary(ctx);
        progress?.Invoke(new AiProgress(AiStep.Thinking, read, "", none));
        var prompt = ctx.Text + (conversation.Length > 0 ? "\nConversation jusqu'ici :\n" + conversation + "\n" : "") + "\nNouvelle demande : " + request;
        var buf = new StringBuilder();
        var said = ""; var seen = 0;
        var r = await ai.CompleteStreaming(store.ClientId, "assistant", System, new UserMessageParam { Content = prompt }, 2500, chunk =>
        {
            buf.Append(chunk);
            if (progress == null) return;
            var text = buf.ToString();
            var say = PartialSay(text); var ops = OpsSoFar(text);
            if (say == said && ops.Count == seen && buf.Length != chunk.Length) return;   // rien de neuf à montrer
            said = say; seen = ops.Count;
            progress(new AiProgress(AiStep.Writing, read, say, ops));
        }, ct);
        if (!r.Ok) return Fail(r.Message);
        progress?.Invoke(new AiProgress(AiStep.Checking, read, PartialSay(r.Text), OpsSoFar(r.Text)));
        var a = r.Text.IndexOf('{'); var b = r.Text.LastIndexOf('}');
        if (a < 0 || b <= a) return Fail("L'assistant n'a pas compris cette demande. Reformulez-la.");
        try
        {
            if (JsonNode.Parse(r.Text[a..(b + 1)]) is not JsonObject o) return Fail("L'assistant n'a pas compris cette demande. Reformulez-la.");
            var say = (Str(o, "say") ?? "").Trim();
            var actions = new List<AiAction>();
            foreach (var x in (o["actions"] as JsonArray ?? new()).OfType<JsonObject>())
                foreach (var act in Build(x, ctx)) if (actions.Count < MaxActions) actions.Add(act);
            var (link, label) = Link(Str(o, "open"), ctx);
            if (say.Length == 0) say = actions.Count > 0 ? "Voici ce que je propose." : "Je n'ai rien à proposer pour cette demande.";
            return new AiAnswer(true, say.Length > 700 ? say[..700] : say, actions, link, label, "");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            log.LogInformation(e, "Réponse illisible de l'assistant.");
            return Fail("L'assistant n'a pas compris cette demande. Reformulez-la.");
        }
    }

    private static AiAnswer Fail(string msg) => new(false, "", new(), null, null, msg);

    // ---------- Suivi de la demande (réponse reçue au fil de l'eau) ----------

    private static readonly Regex SayStart = new("\"say\"\\s*:\\s*\"", RegexOptions.Compiled);
    private static readonly Regex OpField = new("\"op\"\\s*:\\s*\"([a-z_]+)\"", RegexOptions.Compiled);

    /// <summary>« 3 écrans, 4 listes, 2 compositions… » : ce que l'assistant a lu de l'organisation avant d'interroger l'IA.</summary>
    private static string ReadSummary(Ctx c)
    {
        static string N(int n, string one, string many) => n == 1 ? "1 " + one : n + " " + many;
        var parts = new List<string>();
        if (c.Screens.Count > 0) parts.Add(N(c.Screens.Count, "écran", "écrans"));
        if (c.Lists.Count > 0) parts.Add(N(c.Lists.Count, "liste", "listes"));
        if (c.Comps.Count > 0) parts.Add(N(c.Comps.Count, "composition", "compositions"));
        if (c.Media.Count > 0) parts.Add(N(c.Media.Count, "image", "images"));
        if (c.Cals.Count > 0) parts.Add(N(c.Cals.Count, "calendrier", "calendriers"));
        if (c.ILists.Count > 0) parts.Add(N(c.ILists.Count, "liste interactive", "listes interactives"));
        return string.Join(", ", parts);
    }

    /// <summary>Le début de la phrase « say » d'une réponse JSON encore incomplète (échappements décodés ; un échappement coupé en route est laissé de côté).</summary>
    public static string PartialSay(string json)
    {
        var m = SayStart.Match(json ?? "");
        if (!m.Success) return "";
        var sb = new StringBuilder();
        for (var i = m.Index + m.Length; i < json!.Length; i++)
        {
            var ch = json[i];
            if (ch == '"') break;
            if (ch != '\\') { sb.Append(ch); continue; }
            if (i + 1 >= json.Length) break;
            var e = json[++i];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append(' '); break;
                case 'r': break;
                case 'u':
                    if (i + 4 < json.Length && int.TryParse(json.AsSpan(i + 1, 4), global::System.Globalization.NumberStyles.HexNumber, null, out var code)) { sb.Append((char)code); i += 4; }
                    else i = json.Length;
                    break;
                default: sb.Append(e); break;   // \" \\ \/
            }
        }
        return sb.ToString();
    }

    /// <summary>Les sortes d'actions déjà annoncées dans une réponse incomplète, dans l'ordre (libellés généraux : les détails ne sont contrôlés qu'à la fin).</summary>
    public static List<string> OpsSoFar(string json) => OpField.Matches(json ?? "").Select(m => OpLabel(m.Groups[1].Value)).OfType<string>().ToList();

    public static string? OpLabel(string op) => op switch
    {
        "show_message" => "Afficher un message", "clear_message" => "Retirer un message", "command" => "Commande sur un écran",
        "screen_pause" => "Mettre un écran en pause", "screen_resume" => "Reprendre la diffusion", "screen_rename" => "Renommer un écran",
        "screen_set_list" => "Choisir la liste d'un écran", "screen_param" => "Régler une variable d'écran", "band_text" => "Écrire un bandeau", "band_off" => "Retirer un bandeau",
        "list_create" => "Créer une liste", "list_rename" => "Renommer une liste", "list_duplicate" => "Dupliquer une liste", "list_review" => "Relire une liste",
        "list_add" => "Ajouter à une liste", "list_remove" => "Retirer d'une liste", "list_move" => "Déplacer un contenu", "list_duration" => "Changer une durée",
        "list_transition" => "Changer une transition", "list_toggle" => "Activer ou désactiver un contenu",
        "media_rename" => "Renommer une image", "media_describe" => "Décrire une image", "media_analyze" => "Faire décrire une image",
        "composition_create" => "Créer une composition", "web_image" => "Chercher une photo sur internet", "text_replace" => "Remplacer un texte", "text_fix" => "Corriger des textes",
        "calendar_create" => "Créer un calendrier", "calendar_event" => "Ajouter un créneau au calendrier", "calendar_assign" => "Assigner un calendrier",
        "alert_delay" => "Régler l'alerte hors ligne",
        "composition_edit" => "Modifier une composition", "composition_rename" => "Renommer une composition", "composition_duplicate" => "Dupliquer une composition",
        "ilist_create" => "Créer une liste interactive", "ilist_edit" => "Modifier une liste interactive", "ilist_rename" => "Renommer une liste interactive",
        "ilist_duplicate" => "Dupliquer une liste interactive", "kiosk_set_ilist" => "Choisir la liste interactive d'une borne",
        _ => null,
    };

    /// <summary>Ce qu'on dit d'une action longue pendant qu'elle tourne (elle interroge l'IA ou internet) ; null pour une action rapide.</summary>
    public static string? SlowHint(string op) => op switch
    {
        "composition_create" or "composition_edit" or "ilist_create" or "ilist_edit" or "text_fix" or "media_analyze" or "list_review" => "l'IA travaille, cela peut prendre jusqu'à une minute",
        "web_image" => "recherche sur internet, quelques secondes",
        _ => null,
    };

    private static (string?, string?) Link(string? open, Ctx ctx)
    {
        if (string.IsNullOrEmpty(open)) return (null, null);
        if (ctx.Lists.TryGetValue(open, out var l)) return ("/playlists/" + l, "Ouvrir la liste « " + ctx.Names[l] + " »");
        if (ctx.Comps.TryGetValue(open, out var c)) return ("/compositions/" + c, "Ouvrir la composition « " + ctx.Names[c] + " »");
        if (ctx.ILists.TryGetValue(open, out var t)) return ("/interactive/" + t, "Ouvrir la liste interactive « " + ctx.Names[t] + " »");
        if (ctx.Screens.ContainsKey(open)) return ("/", "Ouvrir la page Écrans");
        return open switch
        {
            "media" => ("/media", "Ouvrir la médiathèque"), "screens" => ("/", "Ouvrir la page Écrans"), "playlists" => ("/playlists", "Ouvrir les listes de diffusion"), "interactive" => ("/interactive-lists", "Ouvrir les listes interactives"),
            "programmations" => ("/programmations", "Ouvrir les programmations"), "settings" => ("/settings", "Ouvrir les réglages"), _ => (null, null),
        };
    }

    // ---------- Construction des actions (arguments contrôlés, libellés) ----------

    private static IEnumerable<AiAction> Build(JsonObject x, Ctx c)
    {
        var op = Str(x, "op") ?? "";
        IEnumerable<Guid> Screens() => (x["screens"] as JsonArray ?? (x["screen"] is JsonNode sn ? new JsonArray(sn.DeepClone()) : new()))
            .Select(k => k is JsonValue v && v.TryGetValue<string>(out var s) && c.Screens.TryGetValue(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct();
        Guid? Ref(string field, Dictionary<string, Guid> map) => Str(x, field) is { } k && map.TryGetValue(k, out var g) ? g : null;
        Guid? List() => Ref("list", c.Lists);
        int? Item(Guid list)
        {
            var key = c.Lists.First(kv => kv.Value == list).Key;
            return x["item"] is JsonValue v && v.TryGetValue<double>(out var n) && n >= 1 && n <= c.Items[key].Count ? (int)n : null;
        }
        string Num(string f, int dflt, int min, int max) => (x[f] is JsonValue v && v.TryGetValue<double>(out var d) ? (int)Math.Clamp(Math.Round(d), min, max) : dflt).ToString();
        AiAction Act(string label, params (string K, string V)[] args) => new(op, label, args.ToDictionary(a => a.K, a => a.V));
        string N(Guid g) => c.Names.GetValueOrDefault(g, "?");
        // objet créé dans cette réponse : son identifiant est tiré d'avance, et le repère « ref » que l'IA lui donne sert aux actions suivantes
        // (ajouter à la nouvelle liste, programmer le nouveau calendrier, l'assigner). Un repère déjà pris est ignoré.
        // Filet : le numéro suivant (L5 après L1 à L4) vise aussi l'objet créé, si l'IA continue la numérotation au lieu de donner « ref ».
        Guid New(Dictionary<string, Guid> map, string name)
        {
            var g = Guid.NewGuid();
            bool Free(string k) => !c.Screens.ContainsKey(k) && !c.Lists.ContainsKey(k) && !c.Comps.ContainsKey(k) && !c.Media.ContainsKey(k) && !c.Cals.ContainsKey(k) && !c.ILists.ContainsKey(k);
            void Take(string k) { map[k] = g; if (map == c.Lists) c.Items[k] = new(); }
            if (Str(x, "ref") is { Length: > 0 and <= 12 } r && Free(r)) Take(r);
            var prefix = map == c.Lists ? "L" : map == c.Comps ? "C" : map == c.ILists ? "T" : "K";
            var n = 1;
            while (!Free(prefix + n)) n++;
            Take(prefix + n);
            c.Names[g] = name;
            return g;
        }

        switch (op)
        {
            case "show_message":
            {
                var title = Cut(Str(x, "title"), 60); var msg = Cut(Str(x, "message"), 300);
                if (title.Length == 0 && msg.Length == 0) yield break;
                var look = Str(x, "look") == "attention" ? "attention" : "information";
                var sec = int.Parse(Num("seconds", 30, 5, 3600));
                foreach (var s in Screens()) yield return Act($"Afficher « {Cut(title.Length > 0 ? title : msg, 50)} » sur {N(s)} ({Duration(sec)})", ("s", s.ToString()), ("title", title), ("message", msg), ("look", look), ("seconds", sec.ToString()));
                break;
            }
            case "clear_message":
                foreach (var s in Screens()) yield return Act($"Retirer le message affiché sur {N(s)}", ("s", s.ToString()));
                break;
            case "command":
            {
                var cmd = (Str(x, "command") ?? "").Trim().ToLowerInvariant();
                if (!Commands.Contains(cmd)) yield break;
                foreach (var s in Screens()) yield return Act(cmd switch { "restart-player" => $"Redémarrer l'application de {N(s)}", "screen-off" => $"Éteindre l'écran de {N(s)}", "reboot" => $"Redémarrer l'appareil de {N(s)}", _ => $"Réveiller l'écran de {N(s)}" }, ("s", s.ToString()), ("command", cmd));
                break;
            }
            case "screen_pause":
            {
                var min = int.Parse(Num("minutes", 30, 1, 1440));
                foreach (var s in Screens()) yield return Act($"Mettre {N(s)} en pause ({(min < 60 ? min + " min" : min % 60 == 0 ? min / 60 + " h" : min / 60 + " h " + min % 60 + " min")})", ("s", s.ToString()), ("minutes", min.ToString()));
                break;
            }
            case "screen_resume":
                foreach (var s in Screens()) yield return Act($"Reprendre la diffusion sur {N(s)}", ("s", s.ToString()));
                break;
            case "screen_rename":
            {
                var name = Cut(Str(x, "name"), 60);
                if (name.Length == 0) yield break;
                foreach (var s in Screens().Take(1)) yield return Act($"Renommer {N(s)} en « {name} »", ("s", s.ToString()), ("name", name));
                break;
            }
            case "screen_set_list":
            {
                var l = List();
                if (l == null && Str(x, "list") != null && x["list"] is JsonValue lv && lv.TryGetValue<string>(out var ls) && ls != "null") yield break;   // repère inconnu
                foreach (var s in Screens()) yield return Act(l == null ? $"Retirer la liste par défaut de {N(s)}" : $"Mettre la liste « {N(l.Value)} » sur {N(s)} (à publier ensuite)", ("s", s.ToString()), ("list", l?.ToString() ?? ""));
                break;
            }
            case "screen_param":
            {
                var name = Cut(Str(x, "name"), 60); var val = Cut(Str(x, "value"), 200);
                if (name.Length == 0) yield break;
                foreach (var s in Screens().Take(1)) yield return Act($"Régler « {name} » sur « {val} » pour {N(s)}", ("s", s.ToString()), ("name", name), ("value", val));
                break;
            }
            case "band_text":
            {
                var text = Cut(Str(x, "text"), 160); var band = Str(x, "band") == "bottom" ? "bottom" : "top";
                if (text.Length == 0) yield break;
                foreach (var s in Screens()) yield return Act($"Bandeau {(band == "top" ? "du haut" : "du bas")} de {N(s)} : « {Cut(text, 50)} » (à publier ensuite)", ("s", s.ToString()), ("band", band), ("text", text));
                break;
            }
            case "band_off":
            {
                var band = Str(x, "band") == "bottom" ? "bottom" : "top";
                foreach (var s in Screens()) yield return Act($"Retirer le bandeau {(band == "top" ? "du haut" : "du bas")} de {N(s)} (à publier ensuite)", ("s", s.ToString()), ("band", band));
                break;
            }
            case "list_create":
            {
                var name = Cut(Str(x, "name"), 80);
                if (name.Length > 0) yield return Act($"Créer la liste « {name} »", ("name", name), ("id", New(c.Lists, name).ToString()));
                break;
            }
            case "list_rename":
            case "list_duplicate":
            {
                var name = Cut(Str(x, "name"), 80);
                if (List() is { } l && name.Length > 0) yield return Act(op == "list_rename" ? $"Renommer la liste « {N(l)} » en « {name} »" : $"Dupliquer la liste « {N(l)} » sous le nom « {name} »", ("l", l.ToString()), ("name", name));
                break;
            }
            case "list_review":
                if (List() is { } rl) yield return Act($"Relire la liste « {N(rl)} » (dates passées, textes oubliés, fautes)", ("l", rl.ToString()));
                break;
            case "list_add":
            {
                if (List() is not { } l) yield break;
                var content = Ref("content", c.Comps) ?? Ref("content", c.Media);
                if (content == null) yield break;
                var sec = int.Parse(Num("seconds", 0, 0, 3600));
                yield return Act($"Ajouter « {N(content.Value)} » à la liste « {N(l)} »{(sec > 0 ? $" ({sec} s)" : "")}", ("l", l.ToString()), ("content", content.Value.ToString()), ("seconds", sec.ToString()));
                break;
            }
            case "list_remove":
            case "list_toggle":
            case "list_move":
            {
                if (List() is not { } l || Item(l) is not { } n) yield break;
                var id = c.Items[c.Lists.First(kv => kv.Value == l).Key][n - 1];
                if (op == "list_remove") yield return Act($"Retirer le contenu n° {n} de la liste « {N(l)} » (le fichier reste dans la médiathèque)", ("l", l.ToString()), ("item", id.ToString()));
                else if (op == "list_toggle")
                {
                    var on = x["on"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : true;
                    yield return Act($"{(on ? "Activer" : "Désactiver")} le contenu n° {n} de la liste « {N(l)} »", ("l", l.ToString()), ("item", id.ToString()), ("on", on.ToString()));
                }
                else
                {
                    var count = c.Items[c.Lists.First(kv => kv.Value == l).Key].Count;
                    var to = int.Parse(Num("to", n, 1, count));
                    if (to != n) yield return Act($"Déplacer le contenu n° {n} en position {to} dans « {N(l)} »", ("l", l.ToString()), ("item", id.ToString()), ("to", to.ToString()));
                }
                break;
            }
            case "list_duration":
            case "list_transition":
            {
                if (List() is not { } l) yield break;
                var key = c.Lists.First(kv => kv.Value == l).Key;
                var all = Str(x, "item") == "all" || x["item"] == null;
                var ids = all ? c.Items[key] : Item(l) is { } n ? new List<Guid> { c.Items[key][n - 1] } : new();
                if (ids.Count == 0) yield break;
                var where = all ? $"de tous les contenus de « {N(l)} »" : $"du contenu n° {Item(l)} de « {N(l)} »";
                if (op == "list_duration")
                {
                    var sec = int.Parse(Num("seconds", 8, 1, 3600));
                    yield return Act($"Durée {where} : {Duration(sec)}", ("l", l.ToString()), ("items", string.Join(",", ids)), ("seconds", sec.ToString()));
                }
                else
                {
                    var tr = Str(x, "transition") ?? "";
                    if (!Transitions.All.Any(t => t.Id == tr)) yield break;
                    yield return Act($"Transition {where} : {Transitions.Label(tr).ToLowerInvariant()}", ("l", l.ToString()), ("items", string.Join(",", ids)), ("transition", tr));
                }
                break;
            }
            case "media_rename":
                if (Ref("media", c.Media) is { } m1 && Cut(Str(x, "name"), 120) is { Length: > 0 } nn) yield return Act($"Renommer l'image « {N(m1)} » en « {nn} »", ("m", m1.ToString()), ("name", nn));
                break;
            case "media_describe":
            {
                if (Ref("media", c.Media) is not { } m) yield break;
                var desc = Cut(Str(x, "description"), 400);
                var tags = (x["tags"] as JsonArray ?? new()).Select(t => Cut(t is JsonValue v && v.TryGetValue<string>(out var s) ? s : "", 40)).Where(s => s.Length > 0).Distinct().Take(12).ToList();
                if (desc.Length == 0 && tags.Count == 0) yield break;
                yield return Act($"Corriger la description de l'image « {N(m)} »", ("m", m.ToString()), ("description", desc), ("tags", string.Join("\n", tags)));
                break;
            }
            case "media_analyze":
                if (Ref("media", c.Media) is { } m2) yield return Act($"Faire décrire l'image « {N(m2)} » par l'IA", ("m", m2.ToString()));
                break;
            case "composition_create":
            {
                var sentence = Cut(Str(x, "sentence"), AiCompose.MaxSentence);
                if (sentence.Length < 4) yield break;
                var portrait = x["portrait"] is JsonValue v && v.TryGetValue<bool>(out var pb) && pb;
                var l = List();
                // photo : absente = "auto" ; null = aucune ; "M1" = cette image ; "web" = photo d'internet (si l'organisation y a droit)
                var raw = !x.ContainsKey("photo") ? "auto" : Str(x, "photo")?.Trim() ?? "";
                var photo = Ref("photo", c.Media) is { } pm ? pm.ToString() : raw == "web" ? (c.Web ? "web" : "") : raw == "auto" || raw.StartsWith('M') ? "auto" : "";
                var extra = Guid.TryParse(photo, out var pg) ? $" avec l'image « {N(pg)} »" : photo == "web" ? " avec une photo d'internet" : photo == "auto" ? " (avec une image si l'une convient)" : "";
                yield return Act($"Créer une composition « {Cut(sentence, 60)} »{(portrait ? " (portrait)" : "")}{extra}{(l != null ? $" et l'ajouter à « {N(l.Value)} »" : "")}",
                    ("sentence", sentence), ("portrait", portrait.ToString()), ("l", l?.ToString() ?? ""), ("photo", photo), ("id", New(c.Comps, Cut(sentence, 40)).ToString()));
                break;
            }
            case "composition_edit":
            {
                var change = Cut(Str(x, "change") ?? Str(x, "instruction"), AiCompose.MaxChange);
                if (Ref("composition", c.Comps) is not { } comp || change.Length < 3) yield break;
                var raw = Str(x, "photo")?.Trim() ?? "";
                var photo = Ref("photo", c.Media) is { } pm ? pm.ToString() : raw == "web" ? (c.Web ? "web" : "") : raw == "auto" ? "auto" : "";
                var extra = Guid.TryParse(photo, out var pg) ? $" (avec l'image « {N(pg)} »)" : photo == "web" ? " (avec une photo d'internet)" : "";
                yield return Act($"Modifier « {N(comp)} » : {Cut(change, 140)}{extra}", ("c", comp.ToString()), ("change", change), ("photo", photo));
                break;
            }
            case "composition_rename":
            {
                var name = Cut(Str(x, "name"), 80);
                if (Ref("composition", c.Comps) is { } comp && name.Length > 0) yield return Act($"Renommer la composition « {N(comp)} » en « {name} »", ("c", comp.ToString()), ("name", name));
                break;
            }
            case "composition_duplicate":
            {
                if (Ref("composition", c.Comps) is not { } comp) yield break;
                var name = Cut(Str(x, "name"), 80) is { Length: > 0 } nm ? nm : Cut(N(comp) + " (copie)", 80);
                yield return Act($"Dupliquer la composition « {N(comp)} » sous le nom « {name} »", ("c", comp.ToString()), ("name", name), ("id", New(c.Comps, name).ToString()));
                break;
            }
            case "web_image":
            {
                var q = Cut(Str(x, "query"), 60);
                if (!c.Web || q.Length < 2) yield break;
                var portrait = x["portrait"] is JsonValue v && v.TryGetValue<bool>(out var pb) && pb;
                yield return Act($"Chercher sur internet une photo neutre « {q} »{(portrait ? " (en hauteur)" : "")} et l'ajouter à la médiathèque", ("query", q), ("portrait", portrait.ToString()));
                break;
            }
            case "text_replace":
            {
                var find = Str(x, "find") ?? ""; var rep = Str(x, "replace") ?? "";
                if (Ref("composition", c.Comps) is { } comp && find.Length > 0 && find.Length <= 300 && rep.Length <= 300)
                    yield return Act($"Dans « {N(comp)} » : remplacer « {Cut(find, 60)} » par « {Cut(rep, 60)} »", ("c", comp.ToString()), ("find", find), ("replace", rep));
                break;
            }
            case "text_fix":
            {
                var mode = Str(x, "mode") is "improve" or "formal" or "warm" or "short" ? Str(x, "mode")! : "correct";
                var what = Str(x, "target") is { } t && c.Comps.TryGetValue(t, out var cg) ? ("c", cg, $"de la composition « {N(cg)} »") : Str(x, "target") is { } t2 && c.Lists.TryGetValue(t2, out var lg) ? ("l", lg, $"des compositions de la liste « {N(lg)} »") : default;
                if (what.Item2 == Guid.Empty) yield break;
                var verb = mode switch { "improve" => "Améliorer", "formal" => "Rendre plus formels", "warm" => "Rendre plus chaleureux", "short" => "Raccourcir", _ => "Corriger" };
                yield return Act($"{verb} les textes {what.Item3}", ("kind", what.Item1), ("id", what.Item2.ToString()), ("mode", mode));
                break;
            }
            case "calendar_create":
            {
                var name = Cut(Str(x, "name"), 80);
                if (name.Length > 0 && List() is { } l) yield return Act($"Créer le calendrier « {name} » (liste par défaut : « {N(l)} »)", ("name", name), ("l", l.ToString()), ("id", New(c.Cals, name).ToString()));
                break;
            }
            case "calendar_event":
            {
                if (Ref("calendar", c.Cals) is not { } cal) yield break;
                var kind = Str(x, "kind") == "off" ? "off" : "play";
                var l = List();
                if (kind == "play" && l == null) yield break;
                var always = x["always"] is JsonValue av && av.TryGetValue<bool>(out var ab) && ab;
                var days = (x["days"] as JsonArray ?? new()).Select(d => d is JsonValue v && v.TryGetValue<double>(out var n) ? (int)n : 0).Where(n => n is >= 1 and <= 7).Distinct().ToList();
                if (days.Count == 0) days = Enumerable.Range(1, 7).ToList();
                var from = Str(x, "from") ?? ""; var to = Str(x, "to") ?? "";
                if (!always && (!Planning.TryHm(from, out _) || !Planning.TryHm(to, out _))) yield break;
                var mask = days.Sum(d => 1 << (d - 1));
                var when = always ? "toute la journée" : $"de {from} à {to}";
                yield return Act($"Calendrier « {N(cal)} » : {(kind == "off" ? "éteindre l'écran" : $"diffuser « {N(l!.Value)} »")} {Planning.DaysText(mask)} {when} (s'applique tout de suite)", ("k", cal.ToString()), ("kind", kind), ("l", l?.ToString() ?? ""), ("days", mask.ToString()), ("from", from), ("to", to), ("always", always.ToString()));
                break;
            }
            case "calendar_assign":
            {
                if (Ref("calendar", c.Cals) is not { } cal) yield break;
                foreach (var s in Screens()) yield return Act($"Assigner le calendrier « {N(cal)} » à {N(s)} (s'applique tout de suite)", ("s", s.ToString()), ("k", cal.ToString()));
                break;
            }
            case "ilist_create":
            {
                var name = Cut(Str(x, "name"), 80); var sentence = Cut(Str(x, "sentence") ?? Str(x, "change"), AiInteractive.MaxChange);
                if (name.Length == 0 || sentence.Length < 4) yield break;
                var portrait = x["portrait"] is not JsonValue v || !v.TryGetValue<bool>(out var pb) || pb;   // rien dit : portrait, le format habituel d'une borne
                yield return Act($"Créer la liste interactive « {name} » ({(portrait ? "portrait" : "paysage")}) : {Cut(sentence, 140)}",
                    ("name", name), ("sentence", sentence), ("portrait", portrait.ToString()), ("id", New(c.ILists, name).ToString()));
                break;
            }
            case "ilist_edit":
            {
                var change = Cut(Str(x, "change") ?? Str(x, "sentence"), AiInteractive.MaxChange);
                if (Ref("ilist", c.ILists) is { } il && change.Length >= 3) yield return Act($"Modifier la liste interactive « {N(il)} » : {Cut(change, 140)}", ("t", il.ToString()), ("change", change));
                break;
            }
            case "ilist_rename":
            {
                var name = Cut(Str(x, "name"), 80);
                if (Ref("ilist", c.ILists) is { } il && name.Length > 0) yield return Act($"Renommer la liste interactive « {N(il)} » en « {name} »", ("t", il.ToString()), ("name", name));
                break;
            }
            case "ilist_duplicate":
            {
                if (Ref("ilist", c.ILists) is not { } il) yield break;
                var name = Cut(Str(x, "name"), 80) is { Length: > 0 } nm ? nm : Cut(N(il) + " (copie)", 80);
                yield return Act($"Dupliquer la liste interactive « {N(il)} » sous le nom « {name} »", ("t", il.ToString()), ("name", name), ("id", New(c.ILists, name).ToString()));
                break;
            }
            case "kiosk_set_ilist":
            {
                if (Ref("ilist", c.ILists) is not { } il) yield break;
                foreach (var s in Screens()) yield return Act($"Mettre la liste interactive « {N(il)} » sur la borne {N(s)} (à publier ensuite)", ("s", s.ToString()), ("t", il.ToString()));
                break;
            }
            case "alert_delay":
            {
                var min = int.Parse(Num("minutes", 10, 1, 60));
                if (AlertMinutes.Contains(min)) yield return Act($"Alerter après {min} minute{(min > 1 ? "s" : "")} d'écran hors ligne", ("minutes", min.ToString()));
                break;
            }
        }
    }

    private static string Duration(int s) => s < 60 ? s + " s" : s % 60 == 0 ? s / 60 + " min" : s / 60 + " min " + s % 60 + " s";

    // ---------- Exécution (après « Confirmer ») ----------

    private bool Can(string domain) => store.Read(d => d.Access.CanWrite(domain));
    private bool SeesScreen(Guid id) => store.Read(d => d.Access.CanWrite(LevelPages.Screens) && d.AllScreens.Any(s => s.Id == id));
    private static Guid G(AiAction a, string k) => Guid.TryParse(a.A.GetValueOrDefault(k), out var g) ? g : Guid.Empty;
    private const string NoRight = "Vous n'avez pas les droits pour cette action.";

    public async Task<AiRun> Run(AiAction a, CancellationToken ct = default)
    {
        var cid = store.ClientId; var uid = store.UserId;
        try
        {
            switch (a.Op)
            {
                case "show_message":
                    return new((await notices.Show(cid, uid, G(a, "s"), a.A["title"], a.A["message"], a.A["look"], int.Parse(a.A["seconds"]), false, By)).Error);
                case "clear_message":
                    return new((await notices.Clear(cid, uid, G(a, "s"), By)).Error);
                case "command":
                {
                    var res = await commands.Run(cid, uid, G(a, "s"), a.A["command"], By);
                    return new(res.NotFound ? "Écran introuvable." : res.Ok ? null : res.Error);
                }
                case "screen_pause":
                    if (!SeesScreen(G(a, "s"))) return new(NoRight);
                    return new((await control.Send(cid, G(a, "s"), ScreenCommand.Pause, By, int.Parse(a.A["minutes"]))).Error);
                case "screen_resume":
                    if (!SeesScreen(G(a, "s"))) return new(NoRight);
                    return new((await control.Send(cid, G(a, "s"), ScreenCommand.Resume, By)).Error);
                case "screen_rename": return ScreenSet(a, (s, v) => { var old = s.Name; s.Name = v; return old; }, a.A["name"], "Écran renommé.");
                case "screen_set_list":
                {
                    var lid = G(a, "list");
                    if (lid != Guid.Empty && !store.Read(d => d.Playlists.Any(p => p.Id == lid))) return new("Liste introuvable.");
                    var r = ScreenSet(a, (s, v) => { var old = s.PlaylistId?.ToString() ?? ""; s.PlaylistId = Guid.TryParse(v, out var g) ? g : null; return old; }, a.A["list"], "Liste choisie : publiez l'écran pour l'appliquer.");
                    return r;
                }
                case "screen_param":
                {
                    var name = a.A["name"];
                    if (!store.Read(d => d.AllScreens.FirstOrDefault(s => s.Id == G(a, "s"))?.Params.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) == true)) return new("Cette variable n'existe pas sur l'écran.");
                    return ScreenSet(a, (s, v) => { var p = s.Params.First(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)); var old = p.Value; p.Value = v; return old; }, a.A["value"], "Variable modifiée.");
                }
                case "band_text": case "band_off": return BandOp(a);
                case "list_create":
                {
                    if (!Can(LevelPages.Playlists)) return new(NoRight);
                    var p = new Playlist { Name = a.A["name"], UpdatedAt = DateTime.UtcNow };
                    if (Guid.TryParse(a.A.GetValueOrDefault("id"), out var nid)) p.Id = nid;   // identifiant tiré d'avance : les actions suivantes le visent
                    var ok = store.Write(d => { if (d.Root.Playlists.Any(x => x.Id == p.Id)) return false; d.Playlists.Add(p); return true; });
                    return new(ok ? null : "Cette liste a déjà été créée.", ok ? "Liste créée." : null);
                }
                case "list_rename": return ListEdit(a, (pl, d) => { pl.Name = a.A["name"]; pl.UpdatedAt = DateTime.UtcNow; return null; }, "Liste renommée.");
                case "list_duplicate":
                {
                    if (!Can(LevelPages.Playlists)) return new(NoRight);
                    var lid = G(a, "l");
                    var ok = store.Write(d =>
                    {
                        if (d.Playlists.FirstOrDefault(p => p.Id == lid) is not { } src) return false;
                        var copy = new Playlist { Name = a.A["name"], UpdatedAt = DateTime.UtcNow, DraftChanged = true, Draft = src.Draft.Select(i => new PlaylistItem
                        {
                            MediaId = i.MediaId, DurationSec = i.DurationSec, DurationMode = i.DurationMode, Placement = i.Placement, Off = i.Off, StartsAt = i.StartsAt, EndsAt = i.EndsAt,
                            Transition = i.Transition, TransitionMs = i.TransitionMs,
                        }).ToList() };
                        d.Playlists.Add(copy);
                        return true;
                    });
                    return new(ok ? null : "Liste introuvable.", ok ? "Liste dupliquée." : null);
                }
                case "list_review":
                {
                    var r = await review.Run(cid, G(a, "l"), ct);
                    if (!r.Ok) return new(r.Message);
                    return new(null, r.Findings.Count == 0 ? "Rien à signaler." : r.Findings.Count + " remarque(s) : " + string.Join(" ; ", r.Findings.Take(6).Select(f => $"{AiKinds.Label(f.Kind)} « {Cut(f.Quote, 50)} »")));
                }
                case "list_add": return ListAdd(a);
                case "list_remove": return ListEdit(a, (pl, d) => pl.Draft.RemoveAll(i => i.Id == G(a, "item")) > 0 ? null : "Contenu introuvable.", "Contenu retiré de la liste.");
                case "list_toggle": return ListEdit(a, (pl, d) => pl.Draft.FirstOrDefault(i => i.Id == G(a, "item")) is { } it ? Do(() => it.Off = a.A["on"] != "True") : "Contenu introuvable.", "Contenu mis à jour.");
                case "list_move":
                    return ListEdit(a, (pl, d) =>
                    {
                        var from = pl.Draft.FindIndex(i => i.Id == G(a, "item"));
                        if (from < 0) return "Contenu introuvable.";
                        var it = pl.Draft[from]; pl.Draft.RemoveAt(from);
                        pl.Draft.Insert(Math.Clamp(int.Parse(a.A["to"]) - 1, 0, pl.Draft.Count), it);
                        return null;
                    }, "Contenu déplacé.");
                case "list_duration":
                    return ListEdit(a, (pl, d) => { foreach (var it in pl.Draft.Where(i => Ids(a).Contains(i.Id))) { it.DurationSec = int.Parse(a.A["seconds"]); it.DurationMode = "fixed"; } return null; }, "Durée modifiée.");
                case "list_transition":
                    return ListEdit(a, (pl, d) => { foreach (var it in pl.Draft.Where(i => Ids(a).Contains(i.Id))) it.Transition = a.A["transition"]; return null; }, "Transition modifiée.");
                case "media_rename":
                {
                    var id = G(a, "m");
                    if (!store.Read(d => d.Access.CanWrite(LevelPages.Media) && d.Media.Any(m => m.Id == id && FolderOps.CanEditFile(d, m)))) return new(NoRight);
                    var old = store.Read(d => d.Media.First(m => m.Id == id).Name);
                    library.Rename(id, a.A["name"]);
                    return new(null, "Image renommée.", () => { library.Rename(id, old); return Task.FromResult<string?>(null); });
                }
                case "media_describe":
                {
                    var id = G(a, "m");
                    if (!store.Read(d => d.Access.CanWrite(LevelPages.Media) && d.Media.Any(m => m.Id == id && FolderOps.CanEditFile(d, m)))) return new(NoRight);
                    var desc = a.A["description"]; var tags = a.A["tags"].Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
                    store.Write(d => { var m = d.Media.First(x => x.Id == id); if (desc.Length > 0) m.AiDesc = desc; if (tags.Count > 0) m.AiTags = tags; m.AiDone = m.AiDone || m.AiDesc != null; });
                    return new(null, "Description enregistrée.");
                }
                case "media_analyze":
                {
                    var id = G(a, "m");
                    if (!store.Read(d => d.Access.CanWrite(LevelPages.Media) && d.Media.Any(m => m.Id == id))) return new(NoRight);
                    var r = await aiMedia.Analyze(cid, id, ct);
                    return new(r.Ok ? null : r.Message, r.Ok ? "Image décrite : voir sa fiche dans la médiathèque." : null);
                }
                case "composition_create": return await ComposeNew(a, ct);
                case "web_image":
                {
                    if (!Can(LevelPages.Media)) return new(NoRight);
                    var before = DateTime.UtcNow.AddSeconds(-1);
                    var (item, err) = await webImages.FindAndImport(cid, a.A["query"], a.A["portrait"] == "True", store.AreaId, uid, ct);
                    if (item == null) return new(err ?? "Aucune image trouvée.");
                    return new(null, item.AddedUtc < before ? $"Cette photo est déjà dans la médiathèque : « {item.Name} ». {item.Credit}." : $"Image « {item.Name} » ajoutée à la médiathèque. {item.Credit}.");
                }
                case "text_replace": return TextReplace(a);
                case "text_fix": return await TextFix(a, ct);
                case "composition_edit": return await ComposeEdit(a, ct);
                case "composition_rename": return ComposeRename(a);
                case "composition_duplicate": return ComposeDuplicate(a);
                case "ilist_create": return await IListNew(a, ct);
                case "ilist_edit": return await IListEdit(a, ct);
                case "ilist_rename":
                {
                    if (!Can(LevelPages.Playlists)) return new(NoRight);
                    var id = G(a, "t");
                    var old = store.Read(d => d.InteractiveLists.FirstOrDefault(l => l.Id == id)?.Name);
                    var err = store.Write(d => InteractiveListOps.Rename(d, id, a.A["name"]));
                    if (err != null) return new(err);
                    panel.InteractiveSaved(id);
                    return new(null, "Liste interactive renommée.", () => { var e2 = store.Write(d => InteractiveListOps.Rename(d, id, old ?? "")); panel.InteractiveSaved(id); return Task.FromResult(e2); });
                }
                case "ilist_duplicate":
                {
                    if (!Can(LevelPages.Playlists)) return new(NoRight);
                    var src = G(a, "t");
                    var err = store.Write(d =>
                    {
                        if (d.InteractiveLists.FirstOrDefault(l => l.Id == src) is not { } l) return "Liste interactive introuvable.";
                        var pre = Guid.TryParse(a.A.GetValueOrDefault("id"), out var pg) ? pg : Guid.Empty;
                        if (pre != Guid.Empty && d.Root.InteractiveLists.Any(x => x.Id == pre)) return "Cette copie a déjà été créée.";
                        InteractiveList copy;
                        try { copy = InteractiveListOps.Copy(d, l); } catch (InvalidOperationException e) { return e.Message; }
                        copy.Name = InteractiveLists.Cap(a.A["name"], 80);
                        if (pre != Guid.Empty) copy.Id = pre;   // identifiant tiré d'avance : « NT1 » pour les actions suivantes
                        return (string?)null;
                    });
                    if (err == null) panel.InteractiveSaved(src);
                    return new(err, err == null ? "Liste interactive dupliquée : aucune borne ne l'affiche encore." : null);
                }
                case "kiosk_set_ilist":
                {
                    var sid = G(a, "s"); var tid = G(a, "t");
                    var (isKiosk, same) = store.Read(d => d.AllScreens.FirstOrDefault(s => s.Id == sid) is { Kiosk: not null } s && d.InteractiveLists.FirstOrDefault(l => l.Id == tid) is { } l
                        ? (true, InteractiveLists.Portrait(l.Orientation) == InteractiveLists.Portrait(s.Orientation)) : (false, true));
                    if (!isKiosk) return new("Cet écran n'est pas une borne tactile, ou la liste interactive est introuvable (si elle devait être créée juste avant, confirmez aussi sa création).");
                    return ScreenSet(a, (s, v) => { var old = s.Kiosk!.ListId?.ToString() ?? ""; s.Kiosk.ListId = Guid.TryParse(v, out var g) ? g : null; s.KioskRevision++; return old; }, tid.ToString(),
                        "Liste interactive choisie : publiez la borne pour l'appliquer." + (same ? "" : " Attention : la liste et la borne n'ont pas le même format (portrait / paysage), elle serait déformée."));
                }
                case "calendar_create":
                {
                    if (!Can(LevelPages.Screens)) return new(NoRight);
                    var lid = G(a, "l");
                    var cal = new ScreenCalendar { Name = a.A["name"], DefaultPlaylistId = lid };
                    if (Guid.TryParse(a.A.GetValueOrDefault("id"), out var kid0)) cal.Id = kid0;   // identifiant tiré d'avance : les actions suivantes le visent
                    var err = store.Write(d =>
                    {
                        if (!d.Playlists.Any(p => p.Id == lid)) return "Liste introuvable (si elle devait être créée juste avant, confirmez aussi sa création).";
                        if (d.Root.Calendars.Any(c => c.Id == cal.Id)) return "Ce calendrier a déjà été créé.";
                        d.Calendars.Add(cal);
                        return null;
                    });
                    return new(err, err == null ? "Calendrier créé." : null);
                }
                case "calendar_event":
                {
                    if (!Can(LevelPages.Screens)) return new(NoRight);
                    var e = new CalEvent
                    {
                        Kind = a.A["kind"] == "off" ? CalEvent.Off : CalEvent.Play, PlaylistId = Guid.TryParse(a.A["l"], out var pl) ? pl : null, Always = a.A["always"] == "True" && a.A["kind"] != "off",
                        Repeat = true, Days = int.Parse(a.A["days"]) & 127, StartTime = a.A["from"], EndTime = a.A["to"], CreatedUtc = DateTime.UtcNow,
                    };
                    if (Calendars.Check(e) is { } bad) return new(bad);
                    var kid = G(a, "k");
                    var ids = store.Write(d =>
                    {
                        if (d.Calendars.FirstOrDefault(c => c.Id == kid) is not { } cal) return (List<Guid>?)null;
                        if (e.PlaylistId != null && !d.Playlists.Any(p => p.Id == e.PlaylistId)) return null;
                        cal.Events.Add(e);
                        return Calendars.Apply(d, cal);
                    });
                    if (ids == null) return new("Calendrier ou liste introuvable.");
                    await notifier.PublishPlanning(ids);
                    return new(null, "Ajouté au calendrier" + (ids.Count > 0 ? $" : {ids.Count} écran(s) mis à jour." : "."));
                }
                case "calendar_assign":
                {
                    if (!SeesScreen(G(a, "s"))) return new(NoRight);
                    var sid = G(a, "s"); var kid = G(a, "k");
                    var changed = store.Write(d =>
                    {
                        if (d.Calendars.FirstOrDefault(c => c.Id == kid) is not { } cal || d.AllScreens.FirstOrDefault(s => s.Id == sid) is not { } s) return (Guid?)null;
                        Calendars.Stamp(s, cal);
                        return s.Token != null ? s.Id : Guid.Empty;
                    });
                    if (changed == null) return new("Écran ou calendrier introuvable.");
                    if (changed != Guid.Empty) await notifier.PublishPlanning(new[] { changed.Value });
                    return new(null, "Calendrier assigné.");
                }
                case "alert_delay":
                {
                    if (!store.Read(d => d.Access.IsOrgAdmin)) return new("Seul un administrateur peut changer les alertes.");
                    var min = int.Parse(a.A["minutes"]);
                    var old = store.Read(d => d.Tenant.AlertOfflineMinutes);
                    store.Write(d => d.Tenant.AlertOfflineMinutes = min);
                    return new(null, "Délai d'alerte modifié.", () => { store.Write(d => d.Tenant.AlertOfflineMinutes = old); return Task.FromResult<string?>(null); });
                }
                default: return new("Action inconnue.");
            }
        }
        catch (ReadOnlyLevelException) { return new("Vous êtes en lecture seule sur cette partie."); }
        catch (UnauthorizedAccessException) { return new(NoRight); }
        catch (Exception e) { log.LogWarning(e, "Action de l'assistant en échec ({Op}).", a.Op); return new("L'action n'a pas pu être faite."); }
    }

    private static string? Do(Action act) { act(); return null; }
    private static HashSet<Guid> Ids(AiAction a) => (a.A.GetValueOrDefault("items") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).ToHashSet();

    /// <summary>Change un champ d'un écran (avec le moyen de revenir en arrière).</summary>
    private AiRun ScreenSet(AiAction a, Func<Screen, string, string> change, string value, string done)
    {
        var sid = G(a, "s");
        if (!SeesScreen(sid)) return new(NoRight);
        var old = store.Write(d => d.AllScreens.FirstOrDefault(s => s.Id == sid) is { } sc ? change(sc, value) : null);
        if (old == null) return new("Écran introuvable.");
        return new(null, done, () => { store.Write(d => { if (d.AllScreens.FirstOrDefault(s => s.Id == sid) is { } sc) change(sc, old); }); return Task.FromResult<string?>(null); });
    }

    private AiRun BandOp(AiAction a)
    {
        var sid = G(a, "s");
        if (!SeesScreen(sid)) return new(NoRight);
        var err = store.Write(d =>
        {
            if (d.AllScreens.FirstOrDefault(s => s.Id == sid) is not { } sc) return "Écran introuvable.";
            if (!Bands.Allowed(sc)) return "Cet écran n'accepte pas de bandeaux.";
            var bands = sc.Bands?.Clone() ?? new ScreenBands();
            var band = a.A["band"] == "bottom" ? bands.Bottom : bands.Top;
            if (a.Op == "band_off") band.On = false;
            else
            {
                var cell = band.Cells.FirstOrDefault(c => c.Kind == "text") ?? (band.Center = BandKinds.NewCell("text"));
                cell.Settings["text"] = a.A["text"];
                band.On = true;
            }
            sc.Bands = Bands.Clean(bands);
            sc.BandsRevision++;
            return null;
        });
        return new(err, err == null ? "Bandeau modifié : publiez l'écran pour l'appliquer." : null);
    }

    private AiRun ListEdit(AiAction a, Func<Playlist, ClientDb, string?> change, string done)
    {
        if (!Can(LevelPages.Playlists)) return new(NoRight);
        var lid = G(a, "l");
        var err = store.Write(d =>
        {
            if (d.Playlists.FirstOrDefault(p => p.Id == lid) is not { } pl) return "Liste introuvable.";
            if (!PlaylistOps.CanEdit(d, pl)) return NoRight;
            var e = change(pl, d);
            if (e == null) pl.Touch();
            return e;
        });
        return new(err, err == null ? done + " Publiez la liste pour l'appliquer aux écrans." : null);
    }

    private AiRun ListAdd(AiAction a)
    {
        if (!Can(LevelPages.Playlists)) return new(NoRight);
        var cid = G(a, "content"); var lid = G(a, "l"); var sec = int.Parse(a.A["seconds"]);
        var m = store.Read(d => d.AllMedia.FirstOrDefault(x => x.Id == cid && !x.Off));
        if (m == null) return new("Contenu introuvable.");
        Guid? media; string mode;
        if (m.AppId == null) { media = apps.CreateFromMedia(m)?.Id; mode = "fixed"; }
        else { media = m.Id; mode = "content"; }
        if (media == null) return new("Ce contenu ne peut pas être ajouté à la liste.");
        var err = store.Write(d =>
        {
            if (d.Playlists.FirstOrDefault(p => p.Id == lid) is not { } pl) return "Liste introuvable.";
            if (!PlaylistOps.CanEdit(d, pl)) return NoRight;
            pl.Draft.Add(new PlaylistItem { MediaId = media.Value, DurationSec = sec > 0 ? sec : d.Tenant.DefaultDurationSec, DurationMode = mode, Placement = "full" });
            pl.Touch();
            return null;
        });
        return new(err, err == null ? "Contenu ajouté. Publiez la liste pour l'appliquer aux écrans." : null);
    }

    // ---------- Compositions : création, remplacement et correction de textes ----------

    private async Task<AiRun> ComposeNew(AiAction a, CancellationToken ct)
    {
        // photo : "" aucune, "auto" (médiathèque puis internet), "web" (internet seulement), ou l'identifiant d'une image choisie
        var mode = a.A.GetValueOrDefault("photo") ?? "";
        ComposePhotos? photos = null;
        if (mode.Length > 0)
        {
            photos = AiCompose.PhotosFor(store, webImages, a.A["sentence"], Guid.TryParse(mode, out var chosen) ? chosen : null, mode is "auto" or "web");
            if (mode == "web") photos = photos with { Library = Array.Empty<(Guid, string)>(), Wanted = true };
        }
        var r = await compose.FromSentence(store.ClientId, a.A["sentence"], a.A["portrait"] == "True", ct, photos);
        if (!r.Ok) return new(r.Message);
        var slide = r.Slides[0];
        if (Compositions.Clean(slide.Doc) is not { } c) return new("La composition n'a pas pu être préparée.");
        var (item, errors) = apps.SaveInstance(Compositions.AppId, null, slide.Name, new Dictionary<string, string> { ["doc"] = c.Doc, ["images"] = string.Join(",", c.Images) },
            Guid.TryParse(a.A.GetValueOrDefault("id"), out var pre) ? pre : null);   // identifiant tiré d'avance : « NC1 » pour les actions suivantes
        if (item == null) return new(string.Join(" ", errors));
        var info = r.Photo != null ? $"Composition créée avec une image ({r.Photo})." : "Composition créée.";
        if (Guid.TryParse(a.A["l"], out var lid))
        {
            var ok = store.Write(d =>
            {
                if (d.Playlists.FirstOrDefault(p => p.Id == lid) is not { } pl || !PlaylistOps.CanEdit(d, pl)) return false;
                pl.Draft.Add(new PlaylistItem { MediaId = item.Id, DurationSec = d.Tenant.DefaultDurationSec, DurationMode = "content", Placement = "full" });
                pl.Touch();
                return true;
            });
            info += ok ? " Ajoutée à la liste (à publier ensuite)." : " Elle n'a pas pu être ajoutée à la liste.";
        }
        return new(null, info);
    }

    private string? SaveDoc(Guid id, string name, string doc, out string? err)
    {
        err = null;
        if (Compositions.Clean(doc) is not { } c) { err = "La composition est trop grande ou illisible."; return null; }
        var (item, errors) = apps.SaveInstance(Compositions.AppId, id, name, new Dictionary<string, string> { ["doc"] = c.Doc, ["images"] = string.Join(",", c.Images) });
        if (item == null) { err = string.Join(" ", errors); return null; }
        panel.CompositionSaved(id);   // l'éditeur ouvert sur cette composition se met à jour
        return c.Doc;
    }

    /// <summary>
    /// Modifie une composition selon la demande (tout ce que l'éditeur sait faire sur des éléments simples), avec les droits de la personne :
    /// une composition partagée en « Peut utiliser » est refusée par <see cref="AppService.SaveInstance"/>. « Annuler » remet l'ancien document et l'ancien nom.
    /// </summary>
    private async Task<AiRun> ComposeEdit(AiAction a, CancellationToken ct)
    {
        var id = G(a, "c");
        if (apps.Instance(id) is not { AppId: Compositions.AppId } m) return new("Composition introuvable.");
        if (store.Read(d => CompositionRights.RightOf(d, m)) < FolderRight.Write) return new("Cette composition vous est partagée en « Peut utiliser » : dupliquez-la pour la modifier.");
        var old = m.Settings.GetValueOrDefault("doc") ?? "";
        var mode = a.A.GetValueOrDefault("photo") ?? "";
        ComposePhotos? photos = null;
        if (mode.Length > 0)
        {
            photos = AiCompose.PhotosFor(store, webImages, a.A["change"], Guid.TryParse(mode, out var chosen) ? chosen : null, mode is "auto" or "web");
            if (mode == "web") photos = photos with { Library = Array.Empty<(Guid, string)>(), Wanted = true };
        }
        var names = store.Read(d => d.AllMedia.Where(x => x.Type is "image" or "video").ToDictionary(x => x.Id, x => x.Name));
        var r = await compose.Edit(store.ClientId, old, m.Name, a.A["change"], g => names.GetValueOrDefault(g), ct, photos);
        if (!r.Ok) return new(r.Message);
        SaveDoc(id, r.Name, r.Doc, out var err);
        if (err != null) return new(err);
        var info = (r.Summary.Length > 0 ? r.Summary : "Composition modifiée.") + (r.Photo != null ? $" Image : {r.Photo}." : "") + (r.Name != m.Name ? $" Nouveau nom : « {r.Name} »." : "");
        return new(null, info, () => { SaveDoc(id, m.Name, old, out var e2); return Task.FromResult(e2); });
    }

    private AiRun ComposeRename(AiAction a)
    {
        var id = G(a, "c");
        if (apps.Instance(id) is not { AppId: Compositions.AppId } m) return new("Composition introuvable.");
        var oldName = m.Name; var doc = m.Settings.GetValueOrDefault("doc") ?? "";
        SaveDoc(id, a.A["name"], doc, out var err);
        if (err != null) return new(err);
        return new(null, "Composition renommée.", () => { SaveDoc(id, oldName, doc, out var e2); return Task.FromResult(e2); });
    }

    /// <summary>Copie d'une composition (même document), rangée comme une nouvelle ; possible aussi pour une composition en lecture seule.</summary>
    private AiRun ComposeDuplicate(AiAction a)
    {
        var src = G(a, "c");
        if (apps.Instance(src) is not { AppId: Compositions.AppId } m) return new("Composition introuvable.");
        if (Compositions.Clean(m.Settings.GetValueOrDefault("doc")) is not { } c) return new("Composition illisible.");
        var (item, errors) = apps.SaveInstance(Compositions.AppId, null, a.A["name"], new Dictionary<string, string> { ["doc"] = c.Doc, ["images"] = string.Join(",", c.Images) },
            Guid.TryParse(a.A.GetValueOrDefault("id"), out var pre) ? pre : null);
        if (item == null) return new(string.Join(" ", errors));
        return new(null, $"Copie « {item.Name} » créée.");
    }

    // ---------- Listes interactives des bornes : création et modification par l'IA ----------

    private string Origin() { try { return nav.BaseUri; } catch (InvalidOperationException) { return ""; } }

    private static string Loose(int n) => n == 0 ? "" : n == 1 ? " 1 tuile reste sans action : sa cible n'a pas été trouvée." : $" {n} tuiles restent sans action : leur cible n'a pas été trouvée.";

    /// <summary>Crée une liste interactive entière depuis une description ; elle appartient à la personne, à la racine de ses listes.</summary>
    private async Task<AiRun> IListNew(AiAction a, CancellationToken ct)
    {
        if (!Can(LevelPages.Playlists)) return new(NoRight);
        var id = Guid.TryParse(a.A.GetValueOrDefault("id"), out var pre) ? pre : Guid.NewGuid();   // identifiant tiré d'avance : « NT1 » pour les actions suivantes
        var full = $"Au plus {InteractiveLists.MaxLists} listes interactives.";
        var (cat, state) = store.Read(d => (AiInteractive.Catalog(d, null, a.A["name"] + " " + a.A["sentence"]),
            d.Root.InteractiveLists.Any(x => x.Id == id) ? "Cette liste interactive a déjà été créée." : d.InteractiveLists.Count >= InteractiveLists.MaxLists ? full : null));
        if (state != null) return new(state);
        var r = await interactive.Create(store.ClientId, a.A["name"], a.A["sentence"], a.A["portrait"] == "True", cat, ct);
        if (!r.Ok) return new(r.Message);
        var origin = Origin();
        var err = store.Write(d =>
        {
            if (d.Root.InteractiveLists.Any(x => x.Id == id)) return "Cette liste interactive a déjà été créée.";
            if (d.InteractiveLists.Count >= InteractiveLists.MaxLists) return full;
            var l = new InteractiveList { Id = id };
            if (InteractiveLists.Apply(l, r.Name, r.Doc, mid => d.Media.FirstOrDefault(m => m.Id == mid && m.Status == null), d.Tenant.IsGrowth, origin) is { } e) return e;
            d.InteractiveLists.Add(l);
            return null;
        });
        if (err != null) return new(err);
        panel.InteractiveSaved(id);   // la page des listes interactives montre la nouvelle liste
        return new(null, r.Summary + Loose(r.Loose) + " Aucune borne ne l'affiche encore.");
    }

    /// <summary>
    /// Modifie une liste interactive selon la demande (tout ce que sait faire son éditeur), avec les droits de la personne : une liste en lecture seule est refusée.
    /// Le document passe par le même contrôle que l'éditeur ; les bornes qui l'affichent ont alors des modifications à publier. « Annuler » remet l'ancienne liste.
    /// </summary>
    private async Task<AiRun> IListEdit(AiAction a, CancellationToken ct)
    {
        if (!Can(LevelPages.Playlists)) return new(NoRight);
        var id = G(a, "t");
        var (copy, cat, can) = store.Read(d => d.InteractiveLists.FirstOrDefault(l => l.Id == id) is { } l
            ? (JsonSerializer.Deserialize<InteractiveList>(JsonSerializer.Serialize(l)), AiInteractive.Catalog(d, l, a.A["change"]), InteractiveListOps.CanEdit(d, l))
            : ((InteractiveList?)null, (IListCatalog?)null, false));
        if (copy == null || cat == null) return new("Liste interactive introuvable (si elle devait être créée juste avant, confirmez aussi sa création).");
        if (!can) return new("Cette liste interactive est en lecture seule : dupliquez-la pour la modifier.");
        var r = await interactive.Edit(store.ClientId, copy, a.A["change"], cat, ct);
        if (!r.Ok) return new(r.Message);
        var origin = Origin();
        var (err, bornes) = store.Write(d =>
        {
            if (d.InteractiveLists.FirstOrDefault(l => l.Id == id) is not { } l) return ("Cette liste interactive a été supprimée.", 0);
            if (!InteractiveListOps.CanEdit(d, l)) return ("Cette liste interactive est en lecture seule : dupliquez-la pour la modifier.", 0);
            if (l.Revision != copy.Revision) return ("La liste interactive a changé pendant que l'IA travaillait : rien n'a été modifié. Redemandez.", 0);
            if (InteractiveLists.Apply(l, r.Name, r.Doc, mid => d.Media.FirstOrDefault(m => m.Id == mid && m.Status == null), d.Tenant.IsGrowth, origin) is { } e) return (e, 0);
            var mine = d.Root.Screens.Where(s => s.ClientId == d.ClientId).ToList();
            InteractiveLists.Touch(mine, l.Id);
            return ((string?)null, InteractiveLists.UsedBy(mine, l.Id).Count);
        });
        if (err != null) return new(err);
        panel.InteractiveSaved(id);
        var info = r.Summary + Loose(r.Loose) + (r.Name != copy.Name ? $" Nouveau nom : « {r.Name} »." : "") + (bornes > 0 ? $" Publiez {(bornes == 1 ? "la borne qui l'affiche" : $"les {bornes} bornes qui l'affichent")} pour l'appliquer." : "");
        return new(null, info, () =>
        {
            var e2 = store.Write(d =>
            {
                if (d.InteractiveLists.FirstOrDefault(l => l.Id == id) is not { } l || !InteractiveListOps.CanEdit(d, l)) return "Liste interactive introuvable.";
                l.Name = copy.Name; l.Orientation = copy.Orientation; l.Pages = copy.Pages; l.Revision++; l.UpdatedUtc = DateTime.UtcNow;
                InteractiveLists.Touch(d.Root.Screens.Where(s => s.ClientId == d.ClientId), l.Id);
                return (string?)null;
            });
            panel.InteractiveSaved(id);
            return Task.FromResult(e2);
        });
    }

    private AiRun TextReplace(AiAction a)
    {
        var id = G(a, "c");
        if (apps.Instance(id) is not { AppId: Compositions.AppId } m) return new("Composition introuvable.");
        var old = m.Settings.GetValueOrDefault("doc") ?? "";
        if (JsonNode.Parse(old) is not JsonObject doc || doc["els"] is not JsonArray els) return new("Composition illisible.");
        var find = a.A["find"]; var rep = a.A["replace"]; var n = 0;
        foreach (var e in els.OfType<JsonObject>().Where(e => Str(e, "type") == "text"))
            if (Str(e, "text") is { } t && t.Contains(find, StringComparison.Ordinal)) { e["text"] = t.Replace(find, rep, StringComparison.Ordinal); n++; }
        if (n == 0) return new("Ce texte n'a pas été trouvé dans la composition.");
        SaveDoc(id, m.Name, doc.ToJsonString(), out var err);
        if (err != null) return new(err);
        return new(null, n == 1 ? "1 texte modifié." : n + " textes modifiés.", () => { SaveDoc(id, m.Name, old, out var e2); return Task.FromResult(e2); });
    }

    private async Task<AiRun> TextFix(AiAction a, CancellationToken ct)
    {
        var mode = a.A["mode"];
        var ids = new List<Guid>();
        var gid = G(a, "id");
        if (a.A["kind"] == "c") ids.Add(gid);
        else ids.AddRange(store.Read(d => d.Playlists.FirstOrDefault(p => p.Id == gid)?.Draft.Select(i => i.MediaId).Distinct().Where(x => d.AllMedia.Any(m => m.Id == x && m.AppId == Compositions.AppId)).Take(10).ToList()) ?? new());
        if (ids.Count == 0) return new("Aucune composition à corriger.");
        var undo = new List<(Guid Id, string Name, string Doc)>();
        var changed = 0; var texts = 0;
        foreach (var id in ids)
        {
            if (apps.Instance(id) is not { AppId: Compositions.AppId } m) continue;
            var old = m.Settings.GetValueOrDefault("doc") ?? "";
            if (JsonNode.Parse(old) is not JsonObject doc || doc["els"] is not JsonArray els) continue;
            var targets = els.OfType<JsonObject>().Where(e => Str(e, "type") == "text" && Str(e, "text") is { Length: > 1 } t && Token.Replace(t, "").Trim().Length > 1).ToList();
            if (targets.Count == 0) continue;
            var r = await ai.Complete(store.ClientId, "write", FixSystem(mode), new UserMessageParam { Content = new JsonArray(targets.Select(e => (JsonNode)JsonValue.Create(Str(e, "text")!)!).ToArray()).ToJsonString() }, 3000, ct);
            if (!r.Ok) return new(changed > 0 ? null : r.Message, changed > 0 ? $"{changed} composition(s) corrigée(s), puis : {r.Message}" : null, changed > 0 ? UndoDocs(undo) : null);
            var s = r.Text.IndexOf('['); var e2 = r.Text.LastIndexOf(']');
            if (s < 0 || e2 <= s) continue;
            JsonArray? fixedTexts;
            try { fixedTexts = JsonNode.Parse(r.Text[s..(e2 + 1)]) as JsonArray; } catch (JsonException) { continue; }
            if (fixedTexts == null || fixedTexts.Count != targets.Count) continue;
            var did = 0;
            for (var i = 0; i < targets.Count; i++)
            {
                var before = Str(targets[i], "text")!;
                var after = fixedTexts[i] is JsonValue v && v.TryGetValue<string>(out var str) ? str.Trim() : null;
                if (after == null || after.Length == 0 || after == before || after.Length > before.Length * 3 + 20) continue;
                if (!Token.Matches(before).Select(x => x.Value).OrderBy(x => x).SequenceEqual(Token.Matches(after).Select(x => x.Value).OrderBy(x => x))) continue;   // un champ {{…}} changé : texte laissé tel quel
                targets[i]["text"] = after; did++;
            }
            if (did == 0) continue;
            SaveDoc(id, m.Name, doc.ToJsonString(), out var err);
            if (err != null) continue;
            undo.Add((id, m.Name, old)); changed++; texts += did;
        }
        if (changed == 0) return new(null, "Rien à changer : les textes sont déjà corrects.");
        return new(null, $"{texts} texte(s) modifié(s) dans {changed} composition(s).", UndoDocs(undo));
    }

    private Func<Task<string?>> UndoDocs(List<(Guid Id, string Name, string Doc)> docs) => () =>
    {
        foreach (var (id, name, doc) in docs) { SaveDoc(id, name, doc, out var err); if (err != null) return Task.FromResult<string?>(err); }
        return Task.FromResult<string?>(null);
    };

    private static string FixSystem(string mode)
    {
        var what = mode switch
        {
            "improve" => "corrigé (orthographe, grammaire, ponctuation) et resserré, sans changer le sens",
            "formal" => "corrigé et réécrit sur un ton plus formel, sans changer le sens",
            "warm" => "corrigé et réécrit sur un ton plus chaleureux, sans changer le sens",
            "short" => "raccourci en gardant l'essentiel",
            _ => "corrigé seulement des fautes d'orthographe, de grammaire et de ponctuation ; ne reformule rien d'autre",
        };
        return "Tu reçois un tableau JSON de textes affichés sur un écran. Renvoie un tableau JSON de même longueur, dans le même ordre, avec chaque texte " + what + "." + " " +
            "Garde la langue du texte, les majuscules voulues, les retours à la ligne et tout ce qui est entre doubles accolades (par exemple {{nom}}) exactement tels quels. Un texte déjà correct reste identique." + " " +
            "Réponds uniquement par le tableau JSON.";
    }

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static string Cut(string? s, int n) => (s ?? "").Trim() is var t && t.Length > n ? t[..n] : (s ?? "").Trim();
}
