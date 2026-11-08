using System.Collections.Concurrent;

namespace Linkii.Poc;

/// <summary>Un agenda proposé à l'étape « Salles » de l'assistant d'installation (rien n'est enregistré avant « Installer »).</summary>
public class RoomPick
{
    public string Source { get; set; } = "";
    /// <summary>Référence du calendrier (adresse, ID Google, « cal:… », chemin CalDAV ; lien ICS : déjà chiffré).</summary>
    public string Ref { get; set; } = "";
    public string Hint { get; set; } = "";
    public string Name { get; set; } = "";
    public string Place { get; set; } = "";
    public int Cap { get; set; }
    public bool On { get; set; }
    /// <summary>Salle de l'annuaire Microsoft, ou ressource Google.</summary>
    public bool Detected { get; set; }
    /// <summary>Google : accès du compte à cet agenda (owner, writer, reader…).</summary>
    public string? Role { get; set; }
    /// <summary>Calendrier déjà enregistré (Applications) : son identifiant est gardé (écrans, contenus).</summary>
    public Guid? Id { get; set; }
}

/// <summary>État de l'assistant d'installation, gardé le temps d'un aller-retour chez Microsoft ou Google (la page se recharge au retour).</summary>
public class RoomSetupDraft
{
    public string StepKey { get; set; } = "welcome";
    public HashSet<string> Use { get; set; } = new();
    public Dictionary<string, string> Perms { get; set; } = new();
    public List<RoomPick> Picks { get; set; } = new();
    /// <summary>Sources dont les agendas ont déjà été lus (étape « Salles »).</summary>
    public HashSet<string> Loaded { get; set; } = new();
    public RoomAppSettings Rules { get; set; } = new();
    public string Preset { get; set; } = "std";
    public bool Titles { get; set; }
    /// <summary>Source dont on revient (connexion lancée par « Installer et connecter ») : elle est installée et cochée au retour si la connexion a réussi.</summary>
    public string? Connecting { get; set; }
}

/// <summary>Brouillons de l'assistant, par organisation et personne, 30 minutes (en mémoire : rien n'est enregistré avant la fin).</summary>
public class RoomSetupDrafts
{
    private readonly ConcurrentDictionary<(Guid, Guid), (DateTime At, RoomSetupDraft Draft)> drafts = new();

    public void Keep(Guid clientId, Guid userId, RoomSetupDraft d)
    {
        foreach (var k in drafts.Where(x => DateTime.UtcNow - x.Value.At > TimeSpan.FromMinutes(30)).Select(x => x.Key).ToList()) drafts.TryRemove(k, out _);
        drafts[(clientId, userId)] = (DateTime.UtcNow, d);
    }

    public RoomSetupDraft? Take(Guid clientId, Guid userId) =>
        drafts.TryRemove((clientId, userId), out var v) && DateTime.UtcNow - v.At < TimeSpan.FromMinutes(30) ? v.Draft : null;

    public void Drop(Guid clientId, Guid userId) => drafts.TryRemove((clientId, userId), out _);
}

/// <summary>
/// Installation et retrait de l'application « Réservation de salle ». L'installation écrit tout à la fin de l'assistant : application, entrée de menu,
/// règles, salles (calendriers marqués « Room », créés s'ils n'existaient pas) et le mode d'écriture de chaque salle. Le retrait garde les salles et
/// les règles (une réinstallation retrouve tout) ; les écrans de salle affichent « Application retirée ».
/// </summary>
public static class RoomSetup
{
    public static bool Installed(ClientDb d) => d.AppInstalls.Any(a => a.AppId == RoomRules.AppId);
    public static bool Installed(Db db, Guid clientId) => db.AppInstalls.Any(a => a.ClientId == clientId && a.AppId == RoomRules.AppId);

    /// <summary>Salles (actives ou non) de l'organisation, par nom.</summary>
    public static List<SharedCalendar> Rooms(Tenant t) => t.SharedCalendars.Where(c => c.Room).OrderBy(SharedCalendarSources.RoomName, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Nombre de salles qui utilisent une source (une application d'agenda ne se retire pas tant qu'elle en sert).</summary>
    public static int RoomsUsing(Tenant t, string source) => t.SharedCalendars.Count(c => c.Room && c.Source == source);

    /// <summary>Le compte de la source a le droit de créer des rendez-vous.</summary>
    public static bool HasWriteRight(Tenant t, string source) => source switch
    {
        "m365" => MicrosoftAuth.Has(t, MicrosoftAuth.WriteScope),
        "google" => GoogleAuth.Has(t, GoogleAuth.EventsScope),
        _ => false
    };

    /// <summary>Choix enregistré pour une source (ICS : réservations Linkivi ; sans choix : afficher seulement).</summary>
    public static string PermOf(RoomAppSettings? app, string source) =>
        app?.Sources.GetValueOrDefault(source) ?? (source == "ics" ? RoomPerms.Local : RoomPerms.Read);

    /// <summary>Mode d'écriture d'une salle d'après les réglages de l'application, les droits du compte et l'accès connu au calendrier (Google).</summary>
    public static string ModeOf(Tenant t, SharedCalendar c)
    {
        var perm = PermOf(t.RoomApp, c.Source);
        var lost = t.RoomApp?.LostRights.Contains(c.Source) == true || perm == RoomPerms.Write && RoomRules.SourceCanWrite(c.Source) && !HasWriteRight(t, c.Source);
        return RoomRules.WriteModeOf(c.Source, c.Ref, perm, c.Source == "google" ? c.RoomAccess : null, lost);
    }

    /// <summary>Recalcule le mode d'écriture de toutes les salles (droit accordé ou retiré, choix d'une source changé).</summary>
    public static void Recompute(Tenant t)
    {
        foreach (var c in t.SharedCalendars.Where(c => c.Room)) c.RoomWriteMode = ModeOf(t, c);
        if (t.RoomApp != null) t.RoomApp.Revision++;
    }

    /// <summary>
    /// Fin de l'assistant : tout est écrit ici. manage : gestion des salles depuis la Configuration (les règles ne changent pas).
    /// Rend le nombre de salles et d'agendas utilisés.
    /// </summary>
    public static (int Rooms, int Sources) Install(TenantStore store, AppService svc, RoomSetupDraft draft, bool manage)
    {
        var res = store.Write(d =>
        {
            var t = d.Tenant;
            var app = t.RoomApp ??= new RoomAppSettings();
            if (!manage)
            {
                var r = RoomRules.Clean(draft.Rules);
                (app.Durations, app.MaxMin, app.Days, app.FromMin, app.ToMin) = (r.Durations, r.MaxMin, r.Days, r.FromMin, r.ToMin);
            }
            foreach (var s in draft.Use)
            {
                app.Sources[s] = s == "ics" ? RoomPerms.Local : draft.Perms.GetValueOrDefault(s) == RoomPerms.Write && RoomRules.SourceCanWrite(s) ? RoomPerms.Write : RoomPerms.Read;
                if (app.Sources[s] == RoomPerms.Write && HasWriteRight(t, s)) app.LostRights.Remove(s);
            }
            app.InstalledUtc ??= DateTime.UtcNow;
            var rooms = 0;
            foreach (var p in draft.Picks.Where(p => draft.Use.Contains(p.Source)))
            {
                var c = (p.Id is { } id ? t.SharedCalendars.FirstOrDefault(x => x.Id == id) : null)
                        ?? t.SharedCalendars.FirstOrDefault(x => x.Source == p.Source && x.Ref == p.Ref);
                if (!p.On) { if (c != null) c.Room = false; continue; }
                if (c == null)
                {
                    c = new SharedCalendar { Source = p.Source, Name = Cap(p.Name.Trim().Length > 0 ? p.Name.Trim() : p.Ref, 80), Ref = p.Ref, Hint = Cap(p.Hint, 120) };
                    t.SharedCalendars.Add(c);
                }
                var name = Cap(p.Name.Trim(), 80);
                c.Room = true;
                c.Off = false;
                c.RoomName = name.Length == 0 || name == c.Name ? "" : name;
                c.RoomCap = Math.Clamp(p.Cap, 0, 5000);
                c.RoomPlace = Cap(p.Place.Trim(), 80);
                if (!manage) c.RoomTitles = draft.Titles;
                if (p.Source == "google" && p.Role is { Length: > 0 } role) c.RoomAccess = role;
                c.RoomWriteMode = ModeOf(t, c);
                rooms++;
            }
            foreach (var s in draft.Use)
            {
                var a = t.CalendarAccounts.FirstOrDefault(x => x.Source == s);
                if (a == null) t.CalendarAccounts.Add(a = new CalendarAccount { Source = s });
                a.Enabled = true;   // l'application d'agenda est installée (« Installer et connecter »)
            }
            app.Revision++;
            return (rooms, draft.Use.Count);
        });
        svc.Install(RoomRules.AppId);
        svc.Mark(RoomRules.AppId);
        foreach (var s in draft.Use) svc.Mark("cal-" + s);
        svc.Enable("agenda");
        return res;
    }

    /// <summary>Retrait de l'application : les écrans de salle affichent « Application retirée », les salles et les règles restent.</summary>
    public static int Uninstall(TenantStore store, AppService svc)
    {
        var screens = store.Write(d =>
        {
            if (d.Tenant.RoomApp is { } a) a.Revision++;
            return d.AllScreens.Count(s => s.Room != null);
        });
        svc.Disable(RoomRules.AppId);
        svc.Unmark(RoomRules.AppId);
        return screens;
    }

    /// <summary>Migration au démarrage (D11) : une organisation qui a déjà des salles reçoit l'application, en « Afficher seulement » ; aucun écran n'est modifié.</summary>
    public static void Migrate(JsonStore store, AppCatalog catalog)
    {
        const string key = "room-booking-v1";
        if (catalog.Find(RoomRules.AppId) is not { } m) return;
        store.Write(db =>
        {
            if (db.Done.Contains(key)) return;
            foreach (var t in db.Clients.Where(t => t.SharedCalendars.Any(c => c.Room) && t.RoomApp == null))
            {
                t.RoomApp = new RoomAppSettings { InstalledUtc = DateTime.UtcNow };
                foreach (var s in t.SharedCalendars.Where(c => c.Room).Select(c => c.Source).Distinct()) t.RoomApp.Sources[s] = RoomPerms.Read;
                foreach (var c in t.SharedCalendars.Where(c => c.Room)) c.RoomWriteMode = RoomModes.None;
                if (!db.AppInstalls.Any(a => a.ClientId == t.Id && a.AppId == RoomRules.AppId)) db.AppInstalls.Add(new AppInstall { ClientId = t.Id, AppId = RoomRules.AppId, Version = m.Version });
                if (!t.IntegrationsAdded.Contains(RoomRules.AppId)) t.IntegrationsAdded.Add(RoomRules.AppId);
                if (t.MenuApps is { } menu && !menu.Contains(RoomRules.AppId)) menu.Add(RoomRules.AppId);
            }
            db.Done.Add(key);
        });
    }

    private static string Cap(string s, int max) => s.Length > max ? s[..max] : s;
}
