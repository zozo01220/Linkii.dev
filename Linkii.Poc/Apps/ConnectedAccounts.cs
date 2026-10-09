namespace Linkii.Poc;

/// <summary>Ce qui lance une connexion Google ou Microsoft : une intégration (agendas ou fichiers) ou Comptes connectés (les deux).</summary>
public static class AccountPurposes
{
    public const string Calendar = "calendar", Drive = "drive", All = "all";

    public static string Parse(string? value) => value is Calendar or All ? value : Drive;

    /// <summary>Page où revenir après la connexion : l'onglet Comptes connectés s'il l'a lancée (?tab=comptes), sinon Intégrations.</summary>
    public static string ReturnPage(HttpRequest req) =>
        req.Query["tab"] == ConnectedAccounts.Tab ? "/integrations?tab=" + ConnectedAccounts.Tab : "/integrations";
}

/// <summary>Un accès d'un compte connecté. Purpose : accès à demander (« calendar » ou « drive ») s'il manque, null s'il va de soi.</summary>
public record AccountPermission(string Label, bool Granted, string? Purpose);

/// <summary>
/// Compte connecté par l'organisation, affiché dans Intégrations › Comptes connectés. Lost : accès retiré chez le fournisseur, à reconnecter.
/// Error : dernier test en échec (serveurs CalDAV et Exchange).
/// </summary>
public record ConnectedAccount(string Id, string Kind, string User, string Detail, DateTime? Since, bool Lost, string Error, List<AccountPermission> Permissions);

/// <summary>
/// Comptes connectés : Microsoft, Google, Canva, serveurs CalDAV et Exchange. Un compte par fournisseur, propre à l'organisation ;
/// plusieurs intégrations peuvent l'utiliser (le compte Google sert à Google Calendar et à Google Drive).
/// Vue calculée sur les champs du client (jetons Google, Microsoft et Canva, connexions des sources de calendriers).
/// </summary>
public static class ConnectedAccounts
{
    public const string Microsoft = "microsoft", Google = "google", Canva = "canva", CalDav = "caldav", Exchange = "ews";

    /// <summary>Onglet de la page Intégrations (?tab=comptes).</summary>
    public const string Tab = "comptes";

    public record KindDef(string Id, string Name, string Summary, string[] Integrations);

    /// <summary>Types de comptes, dans l'ordre d'affichage, avec les intégrations qui les utilisent (identifiants des cartes d'Intégrations).</summary>
    public static readonly KindDef[] Kinds =
    {
        new(Microsoft, "Microsoft", "Microsoft 365, OneDrive et SharePoint", ["cal-m365", "drv-onedrive"]),
        new(Google, "Google", "Google Calendar et Google Drive", ["cal-google", "drv-gdrive"]),
        new(Canva, "Canva", "Vos designs Canva", ["canva"]),
        new(CalDav, "Serveur CalDAV", "Nextcloud, iCloud, Zimbra, SOGo, Synology…", ["cal-caldav"]),
        new(Exchange, "Exchange sur site", "Serveur Exchange (EWS)", ["cal-ews"]),
    };

    public static KindDef Kind(string id) => Kinds.First(k => k.Id == id);

    /// <summary>Compte utilisé par une intégration (carte d'Intégrations), ou null si elle n'en demande pas.</summary>
    public static string? For(string integrationId) => Kinds.FirstOrDefault(k => k.Integrations.Contains(integrationId))?.Id;

    /// <summary>Comptes connectés, ou perdus (à reconnecter), dans l'ordre des types.</summary>
    public static List<ConnectedAccount> Of(Tenant t)
    {
        var list = new List<ConnectedAccount>();
        if (t.MsRefreshToken.Length > 0 || t.MsLostUtc != null)
        {
            var scopes = t.MsScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool Has(string s) => scopes.Contains(s, StringComparer.OrdinalIgnoreCase);
            list.Add(new(Microsoft, "Microsoft", t.MsUser.Length > 0 ? t.MsUser : "Compte Microsoft", "", t.MsConnectedUtc, t.MsRefreshToken.Length == 0, "",
            [
                new("Agendas, salles et boîtes partagées (lecture)", Has(MicrosoftAuth.CalendarScope), AccountPurposes.Calendar),
                new("Fichiers OneDrive et SharePoint (lecture)", Has(MicrosoftAuth.DriveScope), AccountPurposes.Drive),
            ]));
        }
        if (t.GoogleRefreshToken.Length > 0 || t.GoogleLostUtc != null)
        {
            var scopes = t.GoogleScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            list.Add(new(Google, "Google", t.GoogleUser.Length > 0 ? t.GoogleUser : "Compte Google", "", t.GoogleConnectedUtc, t.GoogleRefreshToken.Length == 0, "",
            [
                new("Agendas (lecture)", scopes.Contains(GoogleAuth.CalendarScope), AccountPurposes.Calendar),
                new("Fichiers Drive (lecture)", scopes.Contains(GoogleAuth.DriveScope), AccountPurposes.Drive),
            ]));
        }
        if (t.CanvaRefreshToken.Length > 0 || t.CanvaLostUtc != null)
            list.Add(new(Canva, "Canva", t.CanvaUser.Length > 0 ? t.CanvaUser : "Compte Canva", "", t.CanvaConnectedUtc, t.CanvaRefreshToken.Length == 0, "",
                [new("Designs (lecture et export)", true, null)]));
        if (SharedCalendarSources.Account(t, CalDav) is { } dav && (dav.Url.Length > 0 || dav.User.Length > 0))
            list.Add(new(CalDav, "Serveur CalDAV", dav.User.Length > 0 ? dav.User : "Sans identifiant", dav.Url, null, false, dav.LastError,
                [new("Agendas (lecture)", true, null)]));
        if (SharedCalendarSources.Configured(t, Exchange) && SharedCalendarSources.Account(t, Exchange) is { } ews)
            list.Add(new(Exchange, "Exchange sur site", ews.User, ews.Url, null, false, ews.LastError,
                [new("Agendas (lecture)", true, null)]));
        return list;
    }

    /// <summary>Accès à redemander pour reconnecter un compte Google ou Microsoft : ceux qu'il avait, ou les deux s'il n'en avait aucun.</summary>
    public static string ReconnectPurpose(ConnectedAccount a)
    {
        var granted = a.Permissions.Where(p => p.Granted && p.Purpose != null).Select(p => p.Purpose!).ToList();
        return granted.Count == 1 ? granted[0] : AccountPurposes.All;
    }

    /// <summary>Adresse de connexion (ou de reconnexion) d'un compte Google, Microsoft ou Canva, avec retour sur l'onglet Comptes connectés.</summary>
    public static string ConnectUrl(string kind, string purpose = AccountPurposes.All) => kind switch
    {
        Canva => "/canva/connect?tab=" + Tab,
        _ => $"/{kind}/connect?for={purpose}&tab={Tab}"
    };
}
