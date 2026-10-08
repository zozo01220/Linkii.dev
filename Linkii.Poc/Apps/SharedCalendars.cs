namespace Linkii.Poc;

/// <summary>
/// Connexion Microsoft 365 saisie une seule fois (application Entra ID), partagée par les calendriers, l'annuaire des salles,
/// OneDrive et SharePoint ; chaque fenêtre « Gérer » la modifie sur place. Le compte Google, lui, se connecte par OAuth (<see cref="GoogleAuth"/>).
/// </summary>
public static class SharedConnections
{
    public static string? CheckM365(string tenantId, string clientId, string secret, bool hasSecret) =>
        tenantId.Length == 0 || clientId.Length == 0 ? "Renseignez l'ID du locataire et l'ID de l'application."
        : secret.Length == 0 && !hasSecret ? "Collez la valeur du secret client." : null;

    /// <summary>Dans Store.Write : identifiants de l'application Entra ID (champs Ms*, lus par GraphService). POC : secret en clair dans data.json.</summary>
    public static void SaveM365(Tenant t, string tenantId, string clientId, string secret)
    {
        t.MsTenantId = tenantId;
        t.MsClientId = clientId;
        if (secret.Length > 0) t.MsClientSecret = secret;
        if (SharedCalendarSources.Account(t, "m365") is { } a) a.LastError = "";
    }
}

/// <summary>Une source de calendriers partagés : ce qu'on affiche dans Intégrations et ce qu'on demande pour ajouter un calendrier.</summary>
public record CalendarSourceDef(string Id, string Name, string Summary, string RefLabel, string RefPlaceholder, string RefHelp);

/// <summary>
/// Calendriers partagés : la connexion se règle une fois par source (Intégrations), les contenus « Calendrier partagé »
/// ne désignent qu'un calendrier. Tout est résolu côté serveur : secrets et liens ne vont jamais sur un écran.
/// </summary>
public static class SharedCalendarSources
{
    public static readonly CalendarSourceDef[] All =
    {
        new("m365", "Microsoft 365", "Salles et boîtes de votre organisation, depuis l'annuaire.",
            "Adresse de la salle ou de la boîte", "salle-a101@organisation.ch", "Choisissez une salle de l'annuaire ou saisissez l'adresse d'une boîte."),
        new("google", "Google Calendar", "Agendas de votre compte Google, ou partagés avec lui (lecture seule).",
            "ID de l'agenda", "c_…@group.calendar.google.com", "Choisissez un agenda de votre compte, ou collez l'ID d'un agenda auquel il a accès (Google Calendar › Paramètres de l'agenda › Intégrer l'agenda › ID de l'agenda)."),
        new("caldav", "CalDAV", "Nextcloud, iCloud, Zimbra, SOGo, Synology…",
            "Adresse du calendrier", "calendars/salle/agenda/", "Chemin à partir de l'adresse du serveur, ou adresse complète de la collection (Nextcloud : Paramètres du calendrier › Copier le lien privé)."),
        new("ics", "Liens ICS", "Calendriers publiés (Outlook, Google…), sans compte.",
            "Lien ICS", "https://…/calendar.ics", "Outlook : Paramètres du calendrier › Calendriers partagés › Publier › ICS. Google : Paramètres › Adresse secrète au format iCal. Chiffré, il reste sur le serveur."),
        new("ews", "Exchange sur site", "Serveur Exchange (EWS) joignable depuis internet.",
            "Adresse de la boîte", "salle-a101@organisation.ch", "Le compte de service doit pouvoir lire ce calendrier (droit de lecture ou délégation)."),
    };

    public static CalendarSourceDef? Find(string? id) => All.FirstOrDefault(s => s.Id == id);
    public static int Order(string source) => Array.FindIndex(All, s => s.Id == source);

    public static CalendarAccount? Account(Tenant t, string source) => t.CalendarAccounts.FirstOrDefault(a => a.Source == source);
    public static bool Enabled(Tenant t, string source) => Account(t, source)?.Enabled == true;

    /// <summary>La connexion de la source est renseignée (sans garantie qu'elle fonctionne : voir LastError).</summary>
    public static bool Configured(Tenant t, string source)
    {
        var a = Account(t, source);
        return source switch
        {
            "m365" => GraphService.Configured(t),
            "google" => GoogleAuth.Has(t, GoogleAuth.CalendarScope),   // compte Google connecté, avec l'accès aux agendas
            "caldav" => true,   // un calendrier peut porter son adresse complète, et un calendrier public n'a pas d'identifiants
            "ews" => a is { Url.Length: > 0, User.Length: > 0, Secret.Length: > 0 },
            _ => true           // liens ICS : pas de connexion
        };
    }

    /// <summary>Calendriers proposés pour un nouveau contenu : ceux des sources actives et configurées.</summary>
    public static IEnumerable<SharedCalendar> Offered(Tenant t) => t.SharedCalendars
        .Where(c => Enabled(t, c.Source) && Configured(t, c.Source))
        .OrderBy(c => Order(c.Source)).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>
    /// Ce qu'il faut au connecteur pour lire ce calendrier. Une source désactivée continue de servir les contenus existants
    /// (comme une intégration désactivée) ; un calendrier supprimé ne s'affiche plus.
    /// </summary>
    public static CalendarSource Resolve(Tenant t, Guid calendarId, SecretBox box)
    {
        var c = t.SharedCalendars.FirstOrDefault(x => x.Id == calendarId)
            ?? throw new InvalidOperationException("Ce calendrier a été retiré des calendriers partagés (Intégrations).");
        var a = Account(t, c.Source);
        var secret = a is { Secret.Length: > 0 } ? box.Unprotect(a.Secret) : "";
        return c.Source switch
        {
            "m365" => new("m365", c.Ref, "", "", "", ""),
            "google" => new("google", c.Ref, "", "", "", ""),   // le jeton vient du compte Google connecté (GoogleConnector)
            "ews" => new("ews", c.Ref, a?.Url ?? "", a?.User ?? "", secret, ""),
            "caldav" => new("caldav", "", CalDavUrl(a?.Url ?? "", c.Ref), a?.User ?? "", secret, ""),
            _ => new("ics", "", box.Unprotect(c.Ref), "", "", "")
        };
    }

    /// <summary>CalDAV : le calendrier est une adresse complète, ou un chemin à partir de l'adresse du serveur.</summary>
    public static string CalDavUrl(string server, string path) =>
        AppCatalog.IsHttpUrl(path) || server.Length == 0 ? path : server.TrimEnd('/') + "/" + path.TrimStart('/');

    /// <summary>Lien ICS raccourci pour l'affichage : domaine et fin du chemin, jamais le jeton complet.</summary>
    public static string ShortLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host + "/…/" + (u.Segments.LastOrDefault()?.Trim('/') is { Length: > 0 and <= 40 } last ? last : "") : "";
}
