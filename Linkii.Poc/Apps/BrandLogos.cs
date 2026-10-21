namespace Linkii.Poc;

/// <summary>
/// Vrais logos des fournisseurs (dessins simplifiés de wwwroot/brand-logos.svg, maquette icones-integrations.html) et pictogrammes Linkii
/// des sources sans marque. Un logo est affiché tel quel, sans pastille ni recoloration.
/// </summary>
public static class BrandLogos
{
    // Identifiant d'app, de source ou de fournisseur -> symbole du sprite.
    private static readonly Dictionary<string, string> Map = new()
    {
        ["microsoft"] = "microsoft", ["google"] = "google", ["facebook"] = "facebook", ["youtube"] = "youtube", ["canva"] = "canva",
        ["homeassistant"] = "ha", ["ha-sensors"] = "ha", ["ha-switches"] = "ha", ["ha-lights"] = "ha", ["ha-home"] = "ha",
        ["google-slides"] = "gslides", ["google-docs"] = "gdocs", ["ms-powerpoint"] = "ppt", ["rss"] = "rss",
        ["weather"] = "weather", ["weather-slide"] = "weather", ["pdf"] = "pdf",
        ["image"] = "image", ["slideshow-images"] = "images", ["video"] = "video", ["slideshow-videos"] = "videos", ["webpage"] = "web", ["composition"] = "compo",
        ["cal-m365"] = "m365cal", ["cal-google"] = "gcal", ["cal-ews"] = "exchange", ["cal-caldav"] = "caldav", ["cal-ics"] = "ics",
        ["drv-gdrive"] = "gdrive", ["drv-onedrive"] = "onedrive", ["drv-sharepoint"] = "sharepoint", ["drv-dropbox"] = "dropbox",
        ["drv-nextcloud"] = "nextcloud", ["drv-sftp"] = "sftp",
    };

    // Logos sans fond propre : sur un fond sombre, ils passent sur une pastille blanche.
    private static readonly HashSet<string> NoBackground = ["microsoft", "google", "dropbox", "onedrive", "gdrive", "sharepoint", "gcal", "gslides", "gdocs", "pdf"];

    public static string? Symbol(string? id) => id != null && Map.TryGetValue(id, out var s) ? s : null;

    public static bool Has(string? id) => Symbol(id) != null;

    public static bool NeedsWhiteOnDark(string? id) => Symbol(id) is { } s && NoBackground.Contains(s);
}
