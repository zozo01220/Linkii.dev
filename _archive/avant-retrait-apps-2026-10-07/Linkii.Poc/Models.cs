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

    /// <summary>Ancien format mono-client (avant le multi-tenant) : lu une fois pour migration, jamais réécrit.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public LegacyTenant? Tenant { get; set; }
}

/// <summary>Toute donnée qui appartient à un client : isolée par le filtre global de <see cref="ScopedList{T}"/>.</summary>
public interface IClientOwned { Guid ClientId { get; set; } }

public static class Roles
{
    public const string PlatformAdmin = "PlatformAdmin";   // équipe Linkii : console administrateur
    public const string ResellerAdmin = "ResellerAdmin";   // revendeur : clients, marque, équipe
    public const string ClientAdmin = "ClientAdmin";       // client du revendeur : back-office
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
    public Guid? ClientId { get; set; }     // seulement pour un administrateur de client
    public bool Disabled { get; set; }
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
    /// <summary>Apps « activées par défaut » déjà activées une fois pour ce client : s'il les désactive ensuite, elles ne se réactivent pas.</summary>
    public List<string> DefaultAppsApplied { get; set; } = new();

    public string Name { get; set; } = "";

    public string Timezone { get; set; } = "Europe/Zurich";

    public int DefaultDurationSec { get; set; } = 8;     // durée d'un contenu ajouté à une playlist
    public int ReloadHour { get; set; } = 4;             // rechargement nocturne des players

    public string DefaultScreenType { get; set; } = "tv";
    public string DefaultOrientation { get; set; } = "landscape";
    public string DefaultResolution { get; set; } = "1920x1080";

    // Connecteur Microsoft 365 (application Entra ID, flux client credentials). POC : stocké en clair dans data.json.
    public string MsTenantId { get; set; } = "";
    public string MsClientId { get; set; } = "";
    public string MsClientSecret { get; set; } = "";
}

/// <summary>Format du POC mono-client : le compte administrateur vivait dans le tenant.</summary>
public class LegacyTenant : Tenant
{
    public string AdminName { get; set; } = "";
    public string AdminEmail { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

public class Screen : IClientOwned
{
    public Guid ClientId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
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
    public List<PublishedItem> Published { get; set; } = new();
    public int PublishedVersion { get; set; }          // 0 : jamais publié sur cet écran (repli sur l'ancien instantané de la playlist)
    public string? PublishedStamp { get; set; }        // ce qui a été publié (playlist, sa révision, révision des widgets) : sert à repérer les modifications en attente
    public DateTime? PublishedUtc { get; set; }
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

public class MediaItem : IClientOwned
{
    public Guid ClientId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
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
    public DateTime? AddedUtc { get; set; }
}

/// <summary>Une ligne de menu lue par les apps « Menu restaurant » (fichier CSV ou API JSON).</summary>
public class MenuRow
{
    public string Date { get; set; } = "";         // yyyy-MM-dd
    public string Category { get; set; } = "";     // Entrée, Plat, Dessert…
    public string Name { get; set; } = "";
    public string Price { get; set; } = "";
    public string Allergens { get; set; } = "";
}

public class Playlist : IClientOwned
{
    public Guid ClientId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
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
    public Guid? MediaId { get; set; }    // instance d'app d'origine (pour mettre à jour un jeton renouvelé sans republier)

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

    /// <summary>Ce que reçoit le player : jamais les secrets.</summary>
    public object ForPlayer() => new
    {
        id = Id, type = Type, url = Url, durationSec = DurationSec, durationMode = DurationMode, placement = Placement, dataId = DataId,
        appId = AppId, appVersion = AppVersion, settings = Settings, validFrom = ValidFrom, validTo = ValidTo, x = X, y = Y, scale = Scale, urls = Urls
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

    /// <summary>Ce qu'une publication sur cet écran figerait : playlist choisie, sa révision, révision des widgets.</summary>
    public static string PublishStamp(Screen s, Playlist? p) => $"{s.PlaylistId}:{p?.Revision}:{s.WidgetsRevision}";

    /// <summary>L'écran diffuse une version antérieure à sa configuration actuelle (playlist modifiée, autre playlist, widgets).</summary>
    public static bool HasPending(Screen s, Playlist? p) => s.PublishedStamp != PublishStamp(s, p);

    /// <summary>Identifie ce que l'écran doit afficher : playlist publiée + configuration (format, fuseau, redémarrage).</summary>
    public static string Revision(Playlist? p, Screen s, Tenant t, Reseller? r) =>
        $"{(s.PublishedVersion > 0 ? "s" + s.PublishedVersion : p == null ? "none" : p.Id.ToString("N") + ":" + p.PublishedVersion)}|{s.Orientation}|{s.Resolution}|{t.Timezone}|{t.ReloadHour}|{r?.BrandStamp}";

    public static string Status(Screen s, Playlist? p, Tenant t, Reseller? r)
    {
        if (s.LastSeenUtc is null || DateTime.UtcNow - s.LastSeenUtc > TimeSpan.FromSeconds(90)) return "Hors ligne";
        return s.AppliedRevision != Revision(p, s, t, r) ? "Mise à jour" : "En ligne";
    }
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
}
