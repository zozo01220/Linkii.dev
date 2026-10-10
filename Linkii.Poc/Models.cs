namespace Linkii.Poc;

/// <summary>Racine persistée (data.json) : tous les revendeurs, clients et leurs données. L'isolation par client se fait dans <see cref="ClientDb"/>.</summary>
public class Db
{
    public List<Reseller> Resellers { get; set; } = new();
    public List<Tenant> Clients { get; set; } = new();
    public List<User> Users { get; set; } = new();
    public List<Screen> Screens { get; set; } = new();
    public List<MediaItem> Media { get; set; } = new();
    public List<Playlist> Playlists { get; set; } = new();
    public List<AccessLogEntry> AccessLog { get; set; } = new();
    public List<AppInstall> AppInstalls { get; set; } = new();   // apps du catalogue ajoutées par chaque client
    public List<Area> Areas { get; set; } = new();               // aires de gestion des clients
    public PlatformSettings Platform { get; set; } = new();      // réglages communs à toutes les organisations (console › Réglages)

    /// <summary>Ancien format mono-client (avant le multi-tenant) : lu une fois pour migration, jamais réécrit.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public LegacyTenant? Tenant { get; set; }
}

/// <summary>Toute donnée qui appartient à un client : isolée par le filtre global de <see cref="ScopedList{T}"/>.</summary>
public interface IClientOwned { Guid ClientId { get; set; } }

/// <summary>Donnée rangée dans une aire de gestion (écran, liste de lecture, média) : partagée par les membres de l'aire, invisible des autres aires.</summary>
public interface IAreaOwned { Guid AreaId { get; set; } }

/// <summary>Réglages de la plateforme, communs à toutes les organisations quel que soit leur revendeur (console Linkii › Réglages).</summary>
public class PlatformSettings
{
    /// <summary>Durée de l'essai gratuit d'une organisation créée en libre-service, à partir de la validation de son adresse e-mail.</summary>
    public int TrialDays { get; set; } = 7;
    /// <summary>Validité du lien de confirmation de l'adresse e-mail.</summary>
    public int VerifyLinkHours { get; set; } = 48;
    /// <summary>Un compte jamais validé est supprimé après ce délai (libère une adresse mal saisie ou fausse).</summary>
    public int PurgeUnverifiedDays { get; set; } = 7;

    // Envoi d'e-mails (SMTP), commun à toutes les organisations. Vide : repli sur la configuration du serveur (Linkii:Smtp:*).
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool SmtpSsl { get; set; } = true;
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";   // chiffré (SecretBox), jamais réaffiché
    public string SmtpFrom { get; set; } = "";       // adresse d'expédition
}

/// <summary>Comment une organisation a été créée : inscription libre (e-mail, Google, Microsoft), par un revendeur ou par l'équipe Linkii.</summary>
public static class SignupSources
{
    public const string Email = "email";
    public const string Google = "google";
    public const string Microsoft = "microsoft";
    public const string LinkedIn = "linkedin";
    public const string GitHub = "github";
    public const string Reseller = "reseller";
    public const string Console = "console";

    public static readonly string[] All = { Email, Google, Microsoft, LinkedIn, GitHub, Reseller, Console };
    public static bool IsSelfService(string? s) => s is Email or Google or Microsoft or LinkedIn or GitHub;

    public static string Label(string? s) => s switch
    {
        Email => "E-mail", Google => "Google", Microsoft => "Microsoft", LinkedIn => "LinkedIn", GitHub => "GitHub",
        Reseller => "Revendeur", Console => "Linkii", _ => "Non renseignée"
    };
}

public static class Roles
{
    public const string PlatformAdmin = "PlatformAdmin";   // équipe Linkii : console administrateur
    public const string ResellerAdmin = "ResellerAdmin";   // revendeur : clients, marque, équipe
    public const string ClientAdmin = "ClientAdmin";       // administrateur de l'organisation (client du revendeur) : toutes les aires, réglages
    public const string ClientMember = "ClientMember";     // membre de l'organisation : un rôle par aire (User.AreaRoles)

    public static bool IsClient(string role) => role is ClientAdmin or ClientMember;
}

/// <summary>Rôle d'un membre dans une aire de gestion.</summary>
public static class AreaRoles
{
    public const string Admin = "admin";   // contenus de l'aire, zones et membres de l'aire
    public const string User = "user";     // contenus de l'aire (écrans, listes de lecture, médias) : modifier et publier

    public static string Label(string? role) => role == Admin ? "Administrateur" : role == User ? "Utilisateur" : "Aucun accès";
}

/// <summary>Le revendeur : l'entreprise qui revend Linkii sous sa marque. Résolu à partir du nom de domaine de chaque requête.</summary>
public class Reseller
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";             // revendeur.linkii.com
    public string? CustomDomain { get; set; }          // affichage.revendeur.ch (CNAME vers Linkii)
    public bool Active { get; set; } = true;
    public bool IsDefault { get; set; }                // le « revendeur » Linkii lui-même (domaine principal, console administrateur)
    public bool AllowSelfSignup { get; set; }          // création de compte en libre-service (offre directe Linkii)
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // Marque
    public string BrandName { get; set; } = "";
    public string? LogoFile { get; set; }              // fichier du dossier brand/
    public string BrandColor { get; set; } = "#0B1F3A";
    public string SenderEmail { get; set; } = "";
    public bool ShowPoweredBy { get; set; } = true;    // mention « propulsé par Linkii »

    public string DisplayName => string.IsNullOrWhiteSpace(BrandName) ? Name : BrandName;
    public string? LogoUrl => string.IsNullOrEmpty(LogoFile) ? null : "/brand/" + LogoFile;
    /// <summary>Change dès que la marque change : fait partie de la révision envoyée aux écrans.</summary>
    public string BrandStamp => $"{DisplayName}|{LogoFile}|{BrandColor}|{ShowPoweredBy}";
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.ClientAdmin;
    public Guid? ResellerId { get; set; }   // null pour l'équipe Linkii
    public Guid? ClientId { get; set; }     // seulement pour un compte d'organisation (administrateur ou membre)
    public bool Disabled { get; set; }
    /// <summary>Mot de passe provisoire (envoyé par e-mail) : à remplacer à la prochaine connexion.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>Membre (<see cref="Roles.ClientMember"/>) : son rôle dans chaque aire. Un administrateur de l'organisation a accès à toutes les aires.</summary>
    public List<AreaRole> AreaRoles { get; set; } = new();
    /// <summary>Dernière aire ouverte, par organisation (l'équipe Linkii et les revendeurs visitent plusieurs organisations).</summary>
    public Dictionary<Guid, Guid> LastAreas { get; set; } = new();
    public DateTime? CreatedUtc { get; set; }

    /// <summary>Adresse prouvée : lien de confirmation cliqué, connexion Microsoft ou Google, ou première connexion avec le mot de passe provisoire reçu par e-mail.</summary>
    public DateTime? EmailVerifiedUtc { get; set; }
    /// <summary>Inscription par e-mail en attente : empreinte du lien de confirmation en cours (le lien lui-même n'est jamais stocké).</summary>
    public string? VerifyTokenHash { get; set; }
    public DateTime? VerifyExpiresUtc { get; set; }
    /// <summary>Envois du lien sur la dernière heure (renvoi limité).</summary>
    public List<DateTime> VerifySends { get; set; } = new();

    /// <summary>Compte créé avec « Continuer avec Microsoft / Google » : fournisseur et identifiant stable du compte chez lui (pas de mot de passe Linkii).</summary>
    public string? ExternalProvider { get; set; }
    public string? ExternalId { get; set; }

    // Consentements donnés à l'inscription (modifiables ensuite dans Mon compte ou Réglages › Compte)
    public string? TermsVersion { get; set; }
    public DateTime? TermsAcceptedUtc { get; set; }
    public bool MarketingOptIn { get; set; }        // démarches commerciales (offres)
    public DateTime? MarketingOptInUtc { get; set; }
    public bool NewsOptIn { get; set; }             // nouveautés et astuces
    public DateTime? NewsOptInUtc { get; set; }

    public DateTime? LastLoginUtc { get; set; }
    public DateTime? WelcomeSentUtc { get; set; }   // e-mail de bienvenue envoyé (une seule fois par compte)
    public DateTime? LastActiveUtc { get; set; }   // dernière requête, mise à jour au plus une fois par heure : utilisateurs actifs sur 30 jours

    /// <summary>Inscription par e-mail dont l'adresse n'est pas encore confirmée : seul l'écran « Vérifiez votre e-mail » est accessible.</summary>
    public bool AwaitsVerification => EmailVerifiedUtc == null && VerifyTokenHash != null;
    public bool HasPassword => PasswordHash.Length > 0;

    /// <summary>Invité qui ne s'est encore jamais connecté.</summary>
    public bool IsInvited => MustChangePassword && LastLoginUtc == null;
    public bool ActiveSince(DateTime utc) => !Disabled && (LastActiveUtc ?? LastLoginUtc) >= utc;
    public string? RoleIn(Guid areaId) => AreaRoles.FirstOrDefault(r => r.AreaId == areaId)?.Role;
    public string Label => string.IsNullOrWhiteSpace(Name) ? Email : Name;
}

public class AreaRole
{
    public Guid AreaId { get; set; }
    public string Role { get; set; } = Linkii.Poc.AreaRoles.User;
}

/// <summary>Aire de gestion d'une organisation : ses écrans, listes de lecture et médias, partagés par ses membres. Les zones classent ses écrans.</summary>
public class Area : IClientOwned
{
    public Guid ClientId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public List<Zone> Zones { get; set; } = new();
}

/// <summary>
/// Zone d'une aire : classe les écrans (aucun effet sur les droits) et, si <see cref="Sync"/> est actif, aligne la lecture de ceux qui jouent la même liste.
/// Chaque aire a une zone par défaut (<see cref="IsDefault"/>) : tout écran est dans une zone.
/// </summary>
public class Zone
{
    public const string DefaultName = "Défaut";
    public const string Free = "free";   // zone libre : écrans de tout type, une liste par écran
    public const string Wall = "wall";   // mur d'écrans : une seule image répartie sur une grille d'écrans, une liste portée par la zone

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public string Kind { get; set; } = Free;
    public bool Sync { get; set; }               // lecture synchronisée (désactivée par défaut ; toujours active pour un mur)
    public DateTime SyncEpochUtc { get; set; }   // origine commune de la boucle : la position se déduit de l'heure du serveur

    // Mur d'écrans : grille et liste de lecture. La place de chaque écran est Screen.WallPos (0 = en haut à gauche, de gauche à droite puis de haut en bas).
    public int WallCols { get; set; }
    public int WallRows { get; set; }
    public Guid? PlaylistId { get; set; }

    public bool IsWall => Kind == Wall;
}

/// <summary>Journal des entrées dans l'espace d'un client (assistance).</summary>
public class AccessLogEntry
{
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = "";
    public string Role { get; set; } = "";
    public Guid ClientId { get; set; }
    public string ClientName { get; set; } = "";
    public string Action { get; set; } = "enter";   // enter | exit
}

/// <summary>Un client du revendeur (école, commerce, association) : ses paramètres. Ses données portent son <see cref="Id"/>.</summary>
public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResellerId { get; set; }
    public bool Suspended { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Voir <see cref="SignupSources"/> ; null pour une organisation créée avant le 9 octobre 2026.</summary>
    public string? SignupSource { get; set; }
    /// <summary>Fin de l'essai gratuit (validation + durée réglée dans la console). null : pas d'essai (créée par un revendeur ou Linkii, ou abonnée).</summary>
    public DateTime? TrialEndsUtc { get; set; }
    public DateTime? TrialReminderSentUtc { get; set; }   // rappel envoyé 2 jours avant la fin
    public DateTime? TrialEndedNotifiedUtc { get; set; }  // écrans prévenus de la fin de l'essai

    public bool TrialEnded(DateTime now) => TrialEndsUtc is { } end && end <= now;
    public bool InTrial(DateTime now) => TrialEndsUtc is { } end && end > now;
    /// <summary>Jours entamés restants : « 3 j » jusqu'à la dernière minute du troisième jour.</summary>
    public int TrialDaysLeft(DateTime now) => TrialEndsUtc is { } end && end > now ? (int)Math.Ceiling((end - now).TotalDays) : 0;

    /// <summary>Apps « activées par défaut » déjà activées une fois pour ce client : s'il les désactive ensuite, elles ne se réactivent pas.</summary>
    public List<string> DefaultAppsApplied { get; set; } = new();

    public string Name { get; set; } = "";

    public string Timezone { get; set; } = "Europe/Zurich";

    public int DefaultDurationSec { get; set; } = 8;     // durée d'un contenu ajouté à une playlist
    public int ReloadHour { get; set; } = 4;             // rechargement nocturne des players

    public string DefaultScreenType { get; set; } = "tv";
    public string DefaultOrientation { get; set; } = "landscape";
    public string DefaultResolution { get; set; } = "1920x1080";

    // Connexion Canva (Connect API) : jeton de renouvellement chiffré (SecretBox), à usage unique (renouvelé à chaque emploi).
    public string CanvaRefreshToken { get; set; } = "";
    public string CanvaUser { get; set; } = "";
    public DateTime? CanvaConnectedUtc { get; set; }
    /// <summary>Accès retiré par Canva (jeton refusé) : le compte reste affiché « à reconnecter » dans Comptes connectés.</summary>
    public DateTime? CanvaLostUtc { get; set; }

    // Compte Microsoft connecté par l'organisation (« Se connecter avec Microsoft », lecture seule), partagé par Microsoft 365 (agendas) et
    // OneDrive / SharePoint. MsScopes : accès accordés (noms courts Graph). Jeton de renouvellement chiffré (SecretBox).
    // Les anciens champs MsTenantId / MsClientId / MsClientSecret (application Entra ID de chaque client) ne sont plus lus.
    public string MsRefreshToken { get; set; } = "";
    public string MsScopes { get; set; } = "";
    public string MsUser { get; set; } = "";
    public DateTime? MsConnectedUtc { get; set; }
    /// <summary>Accès retiré par Microsoft (jeton refusé) : le compte reste affiché « à reconnecter », avec ses accès d'avant.</summary>
    public DateTime? MsLostUtc { get; set; }

    // Calendriers partagés (Intégrations) : une connexion par source, puis les calendriers proposés dans les listes de lecture.
    public List<CalendarAccount> CalendarAccounts { get; set; } = new();
    public List<SharedCalendar> SharedCalendars { get; set; } = new();

    // Drives (Intégrations) : sources actives (gdrive | onedrive) et dossiers synchronisés. La connexion est celle des calendriers (Google, Microsoft 365).
    public List<string> DrivesEnabled { get; set; } = new();
    public List<DriveFolder> DriveFolders { get; set; } = new();

    // Compte Google connecté par l'organisation (« Se connecter avec Google », lecture seule), partagé par Google Calendar et Google Drive.
    // GoogleScopes : accès accordés (agendas, fichiers Drive). Jeton de renouvellement chiffré (SecretBox).
    public string GoogleRefreshToken { get; set; } = "";
    public string GoogleScopes { get; set; } = "";
    public string GoogleUser { get; set; } = "";
    public DateTime? GoogleConnectedUtc { get; set; }
    /// <summary>Accès retiré par Google (jeton refusé) : le compte reste affiché « à reconnecter », avec ses accès d'avant.</summary>
    public DateTime? GoogleLostUtc { get; set; }

    /// <summary>Ancien nom (connexion Google réservée à Drive, 8 octobre 2026) : repris dans GoogleRefreshToken au démarrage, jamais réécrit.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? GoogleDriveRefreshToken { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? GoogleDriveUser { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? GoogleDriveConnectedUtc { get; set; }
}

/// <summary>Un dossier Google Drive ou OneDrive / SharePoint, copié dans la médiathèque (fichiers masqués, Info["drive"] = Id) et affiché par l'app « Dossier Drive ».</summary>
public class DriveFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Source { get; set; } = "";      // gdrive | onedrive
    public string Name { get; set; } = "";
    public string RemoteId { get; set; } = "";    // Google : ID du dossier ; Microsoft : « driveId/itemId »
    public string Location { get; set; } = "";    // affichage : emplacement du dossier
    public string WebUrl { get; set; } = "";      // « Ouvrir dans… » ; vide pour les dossiers ajoutés avant (Google : déduite de RemoteId)
    public bool Subfolders { get; set; }          // fichiers des sous-dossiers compris, tous niveaux
    public int FileCount { get; set; }
    public int Skipped { get; set; }              // fichiers laissés de côté au-delà de DriveService.MaxFiles
    public int SubfolderCount { get; set; }       // sous-dossiers vus à la dernière synchronisation (directs seulement sans Subfolders)
    public DateTime? SyncedUtc { get; set; }
    public string LastError { get; set; } = "";   // dernière synchronisation en échec ; vide si elle a réussi
}

/// <summary>Connexion d'une source de calendriers partagés (une par source et par client). Microsoft 365 garde ses identifiants dans Ms*.</summary>
public class CalendarAccount
{
    public string Source { get; set; } = "";      // m365 | google | caldav | ics | ews
    public bool Enabled { get; set; }             // calendriers proposés dans les listes de lecture
    public string Url { get; set; } = "";         // CalDAV : adresse du serveur ; Exchange : adresse du service EWS
    public string User { get; set; } = "";
    public string Secret { get; set; } = "";      // chiffré (SecretBox) : mot de passe, ou clé JSON du compte de service Google
    public string Label { get; set; } = "";       // affichage : adresse du compte de service Google
    public string LastError { get; set; } = "";   // dernier test en échec ; vide si le dernier test a réussi
    public DateTime? CheckedUtc { get; set; }
}

/// <summary>Un calendrier proposé dans l'app « Calendrier partagé ». Les contenus le désignent par son Id.</summary>
public class SharedCalendar
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Source { get; set; } = "";
    public string Name { get; set; } = "";
    public string Ref { get; set; } = "";         // adresse de la boîte, ID Google, chemin CalDAV ; lien ICS : chiffré
    public string Hint { get; set; } = "";        // affichage (le lien ICS n'est jamais réaffiché en entier)
}

/// <summary>Format du POC mono-client : le compte administrateur vivait dans le tenant.</summary>
public class LegacyTenant : Tenant
{
    public string AdminName { get; set; } = "";
    public string AdminEmail { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

public class Screen : IClientOwned, IAreaOwned
{
    public Guid ClientId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AreaId { get; set; }
    public int? WallPos { get; set; }          // place dans la grille si sa zone est un mur (0 = en haut à gauche)
    public Guid? ZoneId { get; set; }          // zone de son aire (jamais vide une fois migré : par défaut, la zone « Défaut » de l'aire)
    public string Name { get; set; } = "";
    public string? PairingCode { get; set; }   // non null tant que l'écran n'est pas appairé
    public string? Token { get; set; }         // non null une fois appairé
    public Guid? PlaylistId { get; set; }
    public string? AppliedRevision { get; set; }
    public DateTime? LastSeenUtc { get; set; }

    public string ScreenType { get; set; } = "tv";
    public string Orientation { get; set; } = "landscape";   // landscape | portrait
    public string Resolution { get; set; } = "1920x1080";    // "auto" ou "LxH" (paysage)
    public int? DetectedW { get; set; }
    public int? DetectedH { get; set; }

    // Widgets posés sur l'écran (horloge, météo) : position libre, en % de l'écran. Ne partent sur l'écran qu'à la publication.
    public List<ScreenWidget> Widgets { get; set; } = new();
    public int WidgetsRevision { get; set; }

    // Publication propre à l'écran : instantané de sa playlist + de ses widgets. Les autres écrans de la même playlist ne bougent pas.
    public List<PublishedItem> Published { get; set; } = new();   // zone 1 (ou écran entier) + widgets
    public int PublishedVersion { get; set; }          // 0 : jamais publié sur cet écran (repli sur l'ancien instantané de la playlist)
    public string? PublishedStamp { get; set; }        // ce qui a été publié (playlist, sa révision, révision des widgets) : sert à repérer les modifications en attente
    public DateTime? PublishedUtc { get; set; }

    // Découpage de l'écran (voir ScreenLayouts) : chaque zone diffuse sa propre playlist.
    // Zone 1 = PlaylistId (inchangé : écran plein) ; zones 2 et 3 = ZonePlaylistIds[0] et [1].
    public string Layout { get; set; } = ScreenLayouts.Full;
    public List<Guid?> ZonePlaylistIds { get; set; } = new();
    public string PublishedLayout { get; set; } = ScreenLayouts.Full;
    public List<List<PublishedItem>> PublishedZones { get; set; } = new();   // instantanés des zones 2 et 3

    public int ZoneCount => ScreenLayouts.Find(Layout).Sizes.Length;

    /// <summary>Playlist de la zone (0 = première zone).</summary>
    public Guid? ZonePlaylist(int zone) => zone == 0 ? PlaylistId : ZonePlaylistIds.ElementAtOrDefault(zone - 1);

    /// <summary>Playlists des zones du découpage courant, dans l'ordre.</summary>
    public List<Guid?> ZonePlaylists() => Enumerable.Range(0, ZoneCount).Select(ZonePlaylist).ToList();

    public void SetZonePlaylist(int zone, Guid? playlistId)
    {
        if (zone == 0) { PlaylistId = playlistId; return; }
        while (ZonePlaylistIds.Count < zone) ZonePlaylistIds.Add(null);
        ZonePlaylistIds[zone - 1] = playlistId;
    }
}

/// <summary>Découpages proposés pour un écran. Tailles en % de la largeur (paysage) ou de la hauteur (portrait) :
/// en portrait les zones sont empilées, en paysage côte à côte.</summary>
public static class ScreenLayouts
{
    public const string Full = "1";

    public record Def(string Id, string Label, double[] Sizes);

    public static readonly Def[] All =
    {
        new(Full, "Écran plein (1 zone)", new[] { 100.0 }),
        new("50-50", "2 zones égales (1/2 – 1/2)", new[] { 50.0, 50.0 }),
        new("66-33", "2 zones (2/3 – 1/3)", new[] { 200.0 / 3, 100.0 / 3 }),
        new("33-33-33", "3 zones égales (1/3 – 1/3 – 1/3)", new[] { 100.0 / 3, 100.0 / 3, 100.0 / 3 }),
    };

    public static Def Find(string? id) => All.FirstOrDefault(d => d.Id == id) ?? All[0];

    /// <summary>Découpages qui restent lisibles pour ce format : un smartphone n'est pas découpé, une tablette en 2 au plus ;
    /// un portrait (zones empilées, moins de place) ou un écran HD n'ont pas de découpage en 3 ; un portrait HD seulement 1/2 – 1/2.</summary>
    public static Def[] For(ScreenSpec spec)
    {
        if (spec.Type == "phone") return new[] { All[0] };
        if (spec.Type == "tablet") return All.Where(d => d.Id is Full or "50-50").ToArray();
        var hd = spec.Resolution is "1280x720" or "1366x768";   // « auto » et Full HD et plus : toutes les possibilités du format
        if (spec.Orientation == "portrait")
            return All.Where(d => d.Id is Full or "50-50" || d.Id == "66-33" && !hd).ToArray();
        return hd ? All.Where(d => d.Sizes.Length <= 2).ToArray() : All;
    }

    /// <summary>Découpage gardé après un changement de format : le même s'il est encore permis, sinon le plus proche avec moins de zones.</summary>
    public static string Fit(string id, ScreenSpec spec)
    {
        var allowed = For(spec);
        if (allowed.Any(d => d.Id == id)) return id;
        var zones = Find(id).Sizes.Length;
        return allowed.Where(d => d.Sizes.Length < zones).OrderByDescending(d => d.Sizes.Length).FirstOrDefault()?.Id ?? Full;
    }
}

public class ScreenWidget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string AppId { get; set; } = "";             // clock | weather
    public Dictionary<string, string> Settings { get; set; } = new();
    public double X { get; set; } = 4;                   // coin haut-gauche, en % de la largeur / hauteur de l'écran
    public double Y { get; set; } = 4;
    public double Scale { get; set; } = 1;               // 0,6 petit · 1 moyen · 1,5 grand
}

public class MediaItem : IClientOwned, IAreaOwned
{
    public Guid ClientId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AreaId { get; set; }          // vide : fichier d'un dossier Drive, commun à toutes les aires (les connexions sont celles de l'organisation)
    public string Name { get; set; } = "";
    public string Type { get; set; } = "image";   // image | video | app
    public string? FileName { get; set; }

    // Instance d'une app du catalogue (Type = "app", AppId renseigné)
    public string? AppId { get; set; }
    public string? AppVersion { get; set; }
    public Dictionary<string, string> Settings { get; set; } = new();   // paramètres (brouillon : ne part sur les écrans qu'à la publication)
    public Dictionary<string, string> Secrets { get; set; } = new();    // paramètres secrets, chiffrés (jamais envoyés au player)
    public Dictionary<string, string> Info { get; set; } = new();       // informations constatées (ex. durationSec d'une vidéo) : effacées quand les paramètres changent
    public string? Status { get; set; }            // null = prêt | processing (conversion vidéo) | failed
    public string? Error { get; set; }             // message si la conversion a échoué
    public long? Size { get; set; }                // octets (images et vidéos) ; complété à la lecture pour les fichiers importés avant
    public int? Width { get; set; }                // pixels (images et vidéos) ; lus dans le fichier pour ceux importés avant
    public int? Height { get; set; }
    public DateTime? AddedUtc { get; set; }
}

public class Playlist : IClientOwned, IAreaOwned
{
    public Guid ClientId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AreaId { get; set; }
    public string Name { get; set; } = "";
    public int PublishedVersion { get; set; }                      // ancien instantané au niveau de la playlist (écrans jamais publiés depuis)
    public bool DraftChanged { get; set; }
    public int Revision { get; set; }                              // +1 à chaque modification : un écran publié sur une révision antérieure a des modifications en attente
    public DateTime? UpdatedAt { get; set; }                       // dernière modification (UTC), affichée sur la carte de la playlist
    public List<PlaylistItem> Draft { get; set; } = new();       // l'ordre de la liste = l'ordre de diffusion
    public List<PublishedItem> Published { get; set; } = new();  // ancien instantané : lu seulement par les écrans jamais publiés depuis

    /// <summary>À appeler dans un Store.Write après toute modification du contenu de la playlist.</summary>
    public void Touch() { DraftChanged = true; Revision++; UpdatedAt = DateTime.UtcNow; }
}

public class PlaylistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MediaId { get; set; }
    public int DurationSec { get; set; } = 8;
    /// <summary>Apps dont la durée dépend du contenu (YouTube) : « content » = jusqu'à la fin (défaut), « fixed » = DurationSec.</summary>
    public string DurationMode { get; set; } = "content";
    public string Placement { get; set; } = "full";   // full = diapo ; sinon app en surimpression permanente
}

public class PublishedItem
{
    public Guid Id { get; set; }          // = l'élément de playlist : identifie l'instantané publié (données des apps)
    public string Type { get; set; } = "";
    public string? Url { get; set; }
    public int DurationSec { get; set; }   // durée fixe ; pour une app « content » : durée maximale
    public string DurationMode { get; set; } = "fixed";   // fixed | content (l'app décide : fin de la vidéo…)
    public string Placement { get; set; } = "full";
    public string? DataId { get; set; }   // le player demande les données au serveur par cet identifiant
    public Guid? MediaId { get; set; }    // contenu d'origine (instance d'app ou fichier) : jeton renouvelé sans republier, historique de diffusion

    // Apps du catalogue : instantané des paramètres pris à la publication
    public string? AppId { get; set; }
    public string? AppVersion { get; set; }
    public Dictionary<string, string> Settings { get; set; } = new();   // envoyés au player
    public Dictionary<string, string> Secrets { get; set; } = new();    // chiffrés, restent sur le serveur
    public string? ValidFrom { get; set; }   // aaaa-mm-jj (apps avec période de validité)
    public string? ValidTo { get; set; }
    public double? X { get; set; }        // widget d'écran (Placement = « free ») : position en % et échelle
    public double? Y { get; set; }
    public double? Scale { get; set; }
    public List<string>? Urls { get; set; }   // apps de la médiathèque (image, vidéo, diaporamas) : fichiers résolus à la publication, dans l'ordre

    public PublishedItem Clone() => (PublishedItem)MemberwiseClone();

    /// <summary>Ce que reçoit le player : jamais les secrets.</summary>
    public object ForPlayer() => new
    {
        id = Id, type = Type, url = Url, durationSec = DurationSec, durationMode = DurationMode, placement = Placement, dataId = DataId,
        appId = AppId, appVersion = AppVersion, mediaId = MediaId, settings = Settings, validFrom = ValidFrom, validTo = ValidTo, x = X, y = Y, scale = Scale, urls = Urls
    };
}

/// <summary>Une app du catalogue ajoutée par un client (le client choisit ses apps).</summary>
public class AppInstall : IClientOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientId { get; set; }
    public string AppId { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime InstalledUtc { get; set; } = DateTime.UtcNow;
}

public record Option(string Id, string Label);

public static class ScreenOptions
{
    public static readonly (string Id, string Label, string Icon, string Orientation)[] Types =
    {
        ("tv", "Téléviseur", "📺", "landscape"),
        ("monitor", "Moniteur", "🖥️", "landscape"),
        ("totem", "Totem / borne", "🪧", "portrait"),
        ("tablet", "Tablette Android", "📱", "landscape"),
        ("phone", "Smartphone", "📲", "portrait"),
    };

    public static readonly Option[] Resolutions =
    {
        new("auto", "Auto (résolution de l'appareil)"),
        new("1280x720", "HD · 1280×720"),
        new("1366x768", "WXGA · 1366×768"),
        new("1920x1080", "Full HD · 1920×1080"),
        new("2560x1440", "QHD · 2560×1440"),
        new("3840x2160", "4K UHD · 3840×2160"),
    };

    public static readonly Option[] Placements =
    {
        new("full", "Pleine page"),
        new("top-left", "Angle haut gauche"),
        new("top-right", "Angle haut droit"),
        new("bottom-left", "Angle bas gauche"),
        new("bottom-right", "Angle bas droit"),
        new("top", "Bandeau en haut"),
        new("bottom", "Bandeau en bas"),
    };

    /// <summary>Photos et vidéos : pleine page seulement. Les positions d'une app sont celles de son manifeste.</summary>
    public static IEnumerable<Option> PlacementsFor(MediaItem? m) => Placements.Take(1);


    public static string TypeLabel(string id) => Types.FirstOrDefault(t => t.Id == id).Label ?? id;

    public static string ResolutionLabel(string res, string orientation)
    {
        if (res == "auto") return "Auto";
        var p = res.Split('x');
        return orientation == "portrait" ? $"{p[1]}×{p[0]}" : $"{p[0]}×{p[1]}";
    }
}

/// <summary>État de formulaire partagé (type / format / résolution).</summary>
public class ScreenSpec
{
    public string Type { get; set; } = "tv";
    public string Orientation { get; set; } = "landscape";
    public string Resolution { get; set; } = "1920x1080";

    public static ScreenSpec From(Tenant t) =>
        new() { Type = t.DefaultScreenType, Orientation = t.DefaultOrientation, Resolution = t.DefaultResolution };
}

public static class Helpers
{
    /// <summary>3:32 ou 1:02:07.</summary>
    public static string FormatDuration(int seconds) =>
        seconds >= 3600 ? $"{seconds / 3600}:{seconds % 3600 / 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";

    /// <summary>Texte de la pastille d'une vignette YouTube : durée de la vidéo, « Playlist », ou rien si la durée n'est pas encore connue.</summary>
    public static string? YoutubeBadge(MediaItem? m)
    {
        if (m?.AppId != "youtube") return null;
        if (YouTube.ExtractPlaylistId(m.Settings.GetValueOrDefault("url")) != null) return "Playlist";
        return VideoSeconds(m) is { } s ? FormatDuration(s) : null;
    }

    /// <summary>Durée constatée d'une vidéo YouTube (instance d'app), ou null si elle n'est pas encore connue.</summary>
    public static int? VideoSeconds(MediaItem? m) =>
        m?.Info.TryGetValue("durationSec", out var d) == true && int.TryParse(d, out var n) && n > 0 ? n : null;

    private static readonly System.Globalization.CultureInfo Fr = new("fr-CH");

    /// <summary>Taille lisible : 512 o, 38 Ko, 5,37 Mo, 1,2 Go.</summary>
    public static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} o"
        : bytes < 1024 * 1024 ? (bytes / 1024d).ToString("0", Fr) + " Ko"
        : bytes < 1024L * 1024 * 1024 ? (bytes / 1024d / 1024).ToString("0.##", Fr) + " Mo"
        : (bytes / 1024d / 1024 / 1024).ToString("0.##", Fr) + " Go";

    /// <summary>Ce qu'une publication sur cet écran figerait : playlist de chaque zone et sa révision, découpage, révision des widgets.
    /// (Écran plein : même empreinte qu'avant le découpage en zones, pour ne pas signaler de fausse modification.)</summary>
    public static string PublishStamp(Screen s, IEnumerable<Playlist> playlists)
    {
        string Rev(Guid? id) => playlists.FirstOrDefault(p => p.Id == id)?.Revision.ToString() ?? "";
        var stamp = $"{s.PlaylistId}:{Rev(s.PlaylistId)}:{s.WidgetsRevision}";
        if (s.Layout != ScreenLayouts.Full)
            stamp += "|" + s.Layout + string.Concat(s.ZonePlaylists().Skip(1).Select(id => $"|{id}:{Rev(id)}"));
        return stamp;
    }

    /// <summary>L'écran diffuse une version antérieure à sa configuration actuelle (playlist modifiée, autre playlist, découpage, widgets).</summary>
    public static bool HasPending(Screen s, IEnumerable<Playlist> playlists) => s.PublishedStamp != PublishStamp(s, playlists);

    /// <summary>Identifie ce que l'écran doit afficher : playlist publiée + configuration (format, fuseau, redémarrage).</summary>
    public static string Revision(Playlist? p, Screen s, Tenant t, Reseller? r, string sync = "") =>
        $"{(s.PublishedVersion > 0 ? "s" + s.PublishedVersion : p == null ? "none" : p.Id.ToString("N") + ":" + p.PublishedVersion)}|{s.Orientation}|{s.Resolution}|{t.Timezone}|{t.ReloadHour}|{r?.BrandStamp}{sync}";

    public static string Status(Screen s, Playlist? p, Tenant t, Reseller? r, string sync = "")
    {
        if (s.LastSeenUtc is null || DateTime.UtcNow - s.LastSeenUtc > TimeSpan.FromSeconds(90)) return "Hors ligne";
        return s.AppliedRevision != Revision(p, s, t, r, sync) ? "Mise à jour" : "En ligne";
    }

    /// <summary>Zone de l'écran, si elle existe encore dans son aire.</summary>
    public static Zone? ZoneOf(IEnumerable<Area> areas, Screen s) =>
        areas.FirstOrDefault(a => a.Id == s.AreaId)?.Zones.FirstOrDefault(z => z.Id == s.ZoneId);

    /// <summary>Origine de la boucle commune si la zone de l'écran est synchronisée.</summary>
    public static DateTime? SyncEpoch(IEnumerable<Area> areas, Screen s) => ZoneOf(areas, s) is { Sync: true } z ? z.SyncEpochUtc : null;

    /// <summary>Fait partie de la révision de l'écran : activer ou couper la synchro de sa zone le fait recharger sa configuration.</summary>
    public static string SyncStamp(IEnumerable<Area> areas, Screen s) =>
        (SyncEpoch(areas, s) is { } e ? "|y" + e.Ticks : "") + (Wall(areas, s) is { } w ? $"|w{w.Cols}x{w.Rows}@{w.Col},{w.Row}" : "");   // un mur réorganisé recharge ses écrans

    public record WallPlace(int Cols, int Rows, int Col, int Row);

    /// <summary>Place de l'écran dans son mur (null s'il n'est pas dans un mur).</summary>
    public static WallPlace? Wall(IEnumerable<Area> areas, Screen s) =>
        ZoneOf(areas, s) is { IsWall: true, WallCols: > 0, WallRows: > 0 } z && s.WallPos is { } p && p >= 0 && p < z.WallCols * z.WallRows
            ? new WallPlace(z.WallCols, z.WallRows, p % z.WallCols, p / z.WallCols) : null;

    /// <summary>« 2 · haut droite » : place lisible d'un écran dans une grille.</summary>
    public static string WallLabel(int pos, int cols, int rows)
    {
        int c = pos % cols, r = pos / cols;
        var v = rows == 1 ? "" : r == 0 ? "haut" : r == rows - 1 ? "bas" : rows == 3 ? "milieu" : $"ligne {r + 1}";
        var h = cols == 1 ? "" : c == 0 ? "gauche" : c == cols - 1 ? "droite" : cols == 3 ? "centre" : $"colonne {c + 1}";
        var where = string.Join(" ", new[] { v, h }.Where(x => x.Length > 0));
        return $"{pos + 1}" + (where.Length > 0 ? " · " + where : "");
    }

    /// <summary>Grilles possibles pour n écrans (colonnes × lignes = n), la plus proche d'une image 16/9 d'abord.</summary>
    public static List<(int Cols, int Rows)> WallShapes(int n) =>
        Enumerable.Range(1, Math.Max(1, n)).Where(r => n % r == 0).Select(r => (Cols: n / r, Rows: r))
            .OrderBy(s => Math.Abs(Math.Log(s.Cols * 16.0 / (s.Rows * 9.0) / (16.0 / 9.0)))).ThenByDescending(s => s.Cols).Take(4).ToList();

    public static long EpochMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>
    /// Dans une zone synchronisée, la boucle doit durer pareil sur tous les écrans : une app qui décide de sa durée reçoit une durée fixe :
    /// un diaporama d'images, le nombre d'images × la durée de chaque image ; une vidéo YouTube, sa durée constatée ; sinon 60 s.
    /// (Pendant ce créneau, l'écran déroule le contenu à l'heure commune : apps.js « syncedList ».)
    /// </summary>
    public static List<PublishedItem> FixedForSync(IEnumerable<PublishedItem> items, IEnumerable<MediaItem> media) =>
        items.Select(i =>
        {
            if (i.DurationMode != "content") return i;
            var c = i.Clone();
            c.DurationMode = "fixed";
            c.DurationSec = ImagesSeconds(i) ?? VideoSeconds(media.FirstOrDefault(m => m.Id == i.MediaId)) ?? 60;
            return c;
        }).ToList();

    private static readonly System.Text.RegularExpressions.Regex VideoFile = new(@"\.(mp4|m4v|webm|mov|ogv)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Durée d'un diaporama fait seulement d'images (diaporama, Canva en pages, dossier Drive sans vidéo) ; null sinon.</summary>
    public static int? ImagesSeconds(PublishedItem i)
    {
        if (i.Urls is not { Count: > 0 } urls || urls.Any(u => VideoFile.IsMatch(u))) return null;
        var images = i.AppId is "slideshow-images" or "drive" || (i.AppId == "canva" && i.Settings.GetValueOrDefault("kind") != "video");
        if (!images) return null;
        var each = int.TryParse(i.Settings.GetValueOrDefault("interval"), out var s) ? Math.Clamp(s, 2, 600) : 8;
        return urls.Count * each;
    }

    /// <summary>Signature de la boucle d'un écran (contenus plein écran et durées) : deux écrans ne sont alignés que si elle est identique.</summary>
    public static string LoopKey(Screen s) =>
        s.PlaylistId + "|" + string.Join(",", s.Published.Where(i => i.Placement == "full").Select(i => i.Id.ToString("N")[..8] + ":" + i.DurationSec + i.DurationMode));

    /// <summary>Écrans de la zone alignés avec d'autres : même liste de lecture, même boucle publiée. Un écran seul sur sa liste ou sur une autre boucle reste indépendant.</summary>
    public static HashSet<Guid> SyncedScreens(IEnumerable<Screen> inZone) =>
        inZone.Where(s => s.Token != null && s.PublishedVersion > 0 && s.PlaylistId != null)
            .GroupBy(LoopKey).Where(g => g.Count() > 1).SelectMany(g => g.Select(s => s.Id)).ToHashSet();
}

public static class YouTube
{
    private static readonly System.Text.RegularExpressions.Regex VideoId = new("^[A-Za-z0-9_-]{11}$");
    private static readonly System.Text.RegularExpressions.Regex ListId = new("^[A-Za-z0-9_-]{10,64}$");

    private static Uri? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var s = url.Trim();
        if (!Uri.TryCreate(s.Contains("://") ? s : "https://" + s, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        var h = u.Host.ToLowerInvariant();
        var ok = h is "youtu.be" or "youtube.com" or "youtube-nocookie.com" || h.EndsWith(".youtube.com") || h.EndsWith(".youtube-nocookie.com");
        return ok ? u : null;
    }

    /// <summary>Extrait l'identifiant (11 caractères) d'un lien YouTube : watch?v=, youtu.be/, /embed/, /shorts/, /live/, ou l'identifiant seul.</summary>
    public static string? ExtractId(string? url)
    {
        if (url != null && VideoId.IsMatch(url.Trim())) return url.Trim();
        var u = Parse(url);
        if (u == null) return null;
        string? id = null;
        if (u.Host == "youtu.be") id = u.AbsolutePath.Trim('/').Split('/')[0];
        else
        {
            var q = System.Web.HttpUtility.ParseQueryString(u.Query)["v"];
            if (!string.IsNullOrEmpty(q)) id = q;
            else
            {
                var seg = u.AbsolutePath.Trim('/').Split('/');
                if (seg.Length >= 2 && seg[0] is "embed" or "shorts" or "live" or "v") id = seg[1];
            }
        }
        return id != null && VideoId.IsMatch(id) ? id : null;
    }

    /// <summary>Identifiant d'une playlist YouTube (paramètre list=), seulement pour un lien sans vidéo précise.</summary>
    public static string? ExtractPlaylistId(string? url)
    {
        var u = Parse(url);
        if (u == null || ExtractId(url) != null) return null;
        var id = System.Web.HttpUtility.ParseQueryString(u.Query)["list"];
        return id != null && ListId.IsMatch(id) ? id : null;
    }

    /// <summary>Forme normalisée d'un lien (vidéo ou playlist), ou null si ce n'est pas un lien YouTube reconnu.</summary>
    public static string? Canonical(string? url)
    {
        if (ExtractId(url) is { } v) return "https://www.youtube.com/watch?v=" + v;
        if (ExtractPlaylistId(url) is { } l) return "https://www.youtube.com/playlist?list=" + l;
        return null;
    }
}

public static class AppPaths
{
    public static string MediaDir = "media";
    public static string BrandDir = "brand";
    public static string ThumbDir = "thumbs";
}
