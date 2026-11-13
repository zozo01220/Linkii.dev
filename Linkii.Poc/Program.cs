using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;
using Linkii.Poc;
using Linkii.Poc.Components;
using Linkii.Poc.Hubs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents()
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 1024 * 1024); // uploads plus rapides
builder.Services.AddSignalR();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<JsonStore>();
builder.Services.AddSingleton<ResellerResolver>();
builder.Services.AddSingleton<NpmProxy>();
builder.Services.AddSingleton<Mailer>();
builder.Services.AddSingleton<LifecycleMail>();   // e-mails du parcours d'abonnement (voir Lifecycle)
builder.Services.AddScoped<Toaster>();
builder.Services.AddSingleton<PlayLog>();
builder.Services.AddSingleton<HistoryLog>();   // historique : activité, écrans, diagnostic (page Historique)
builder.Services.AddSingleton<KioskLog>();
builder.Services.AddSingleton<CanvaService>();
builder.Services.AddSingleton<PdfService>();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<TenantStore>();
builder.Services.AddScoped<AccountManager>();   // connexion et déconnexion des comptes de l'organisation
builder.Services.AddScoped<ModeService>();
builder.Services.AddScoped<AssistantPanel>();   // panneau de l'assistant : ouvert d'une page à l'autre, toute l'IA y passe (MainLayout)
builder.Services.AddScoped<GettingStarted>();   // assistant « Premiers pas » : ouvert d'une page à l'autre (MainLayout)
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<PushService>();   // notifications push (Firebase) vers l'app Android du back-office
builder.Services.AddSingleton<NotificationCenter>();   // centre de notifications : la cloche et la page Notifications
builder.Services.AddSingleton<IScreenPublishing>(sp => sp.GetRequiredService<Notifier>());
builder.Services.AddSingleton<IApprovalNotices, ApprovalNotices>();   // approbation des publications (§162) : notifications adressées et e-mails
builder.Services.AddSingleton<ApprovalService>();
builder.Services.AddScoped<ScreenPublisher>();   // toute publication d'écran faite pour une personne passe ici (demande d'approbation si elle y est soumise)
builder.Services.AddHostedService<ApprovalWorker>();   // rappel après 24 h, péremption après 7 jours, refus si la brique n'agit plus
builder.Services.AddSingleton<PublishSchedule>();   // publications programmées par écran (onglet Publication d une liste)
builder.Services.AddHostedService<PublishScheduleWorker>();
builder.Services.AddSingleton<AlertService>();  // alertes de l'organisation : e-mail et/ou notification selon les Réglages
builder.Services.AddSingleton<IApiAlerts, ApiAlerts>();   // API et webhooks (§154) : alertes « Applications et API »
builder.Services.AddSingleton<IEmergencyPush, HubEmergencyPush>();   // application « Alerte d'urgence » (§159) : message temps réel vers les écrans
builder.Services.AddSingleton<IEmergencyNotices, EmergencyNotices>();
builder.Services.AddSingleton<EmergencyService>();
builder.Services.AddScoped<EmergencyUi>();   // état du bouton, du bandeau et du tiroir d'urgence du back-office
builder.Services.AddHostedService<EmergencyWorker>();   // fin des essais, rappel après 12 h, historique d'un an
builder.Services.AddLinkiiApi();   // socle de l'API publique : clés, scopes, webhooks, journal (voir Api/)
builder.Services.AddSingleton<PreviewGrants>();   // jetons d'aperçu du simulateur
builder.Services.AddSingleton<VideoConverter>();
builder.Services.AddSingleton<WebSnapshot>();
builder.Services.AddSingleton<OfficeConverter>();   // PowerPoint, Word, Excel… envoyés depuis l'ordinateur : convertis en PDF par LibreOffice (§105)
builder.Services.AddSingleton<LocalDocuments>();
builder.Services.AddSingleton<MediaImporter>();
builder.Services.AddSingleton<WeatherService>();
builder.Services.AddSingleton<GraphService>();
builder.Services.AddSingleton<ICalendarConnector, M365Connector>();
builder.Services.AddSingleton<ICalendarConnector, IcsConnector>();
builder.Services.AddSingleton<GoogleConnector>();   // aussi utilisé par Applications (liste des agendas du compte Google)
builder.Services.AddSingleton<ICalendarConnector>(sp => sp.GetRequiredService<GoogleConnector>());
builder.Services.AddSingleton<ICalendarConnector, CalDavConnector>();
builder.Services.AddSingleton<ICalendarConnector, EwsConnector>();
// Réservation de salle : écriture dans les agendas (création des réservations faites sur la tablette, jamais la modification d'un rendez-vous)
builder.Services.AddSingleton<IWritableCalendarConnector, M365Writer>();
builder.Services.AddSingleton<IWritableCalendarConnector, GoogleWriter>();
builder.Services.AddSingleton<ProviderDetector>();
builder.Services.AddSingleton<CalendarService>();
builder.Services.AddSingleton<RoomStatusService>();   // Réservation de salle : état des salles (porte, page de l'application, cartes), cache de 30 s
builder.Services.AddSingleton<RoomSetupDrafts>();     // assistant d'installation : brouillon gardé pendant la connexion à Microsoft ou Google
builder.Services.AddSingleton<RoomBookingService>();  // réservation depuis la tablette : le serveur vérifie, écrit, prévient les écrans de la salle
builder.Services.AddHostedService<RoomWorker>();      // réponses des salles aux invitations, journal de 90 jours

// Catalogue d'apps (manifestes du dossier Apps/), secrets chiffrés, appels sortants protégés, fournisseurs de données
var dataDirEarly = builder.Configuration["Linkii:DataDir"] ?? builder.Environment.ContentRootPath;
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirEarly, "keys"))).SetApplicationName("Linkii");
builder.Services.AddSingleton<AppCatalog>();
builder.Services.AddSingleton<SecretBox>();
builder.Services.AddSingleton<AiService>();   // intelligence artificielle : une clé pour toute la plateforme, quotas par organisation (voir Ai.cs)
builder.Services.AddSingleton<AiReview>();   // relecture avant publication (piste 2) : dates passées, textes de modèle oubliés, fautes, textes trop petits
builder.Services.AddSingleton<AiMedia>();   // médiathèque intelligente (piste 3) : description, mots-clés, cadrage des images
builder.Services.AddSingleton<AiInteractive>();   // listes interactives des bornes créées ou modifiées par l assistant (ilist_create, ilist_edit)
builder.Services.AddScoped<AiAssistant>();   // assistant : réponses et actions confirmées (piste 5)
builder.Services.AddSingleton<AiCompose>();   // compositions créées par l IA : depuis une phrase, depuis le site web (piste 4)
builder.Services.AddSingleton<WebImages>();   // images d'illustration d'internet (Pexels) pour les compositions de l IA
builder.Services.AddScoped<AiStarter>();   // Premiers pas avec l assistant : site ou phrase -> pages, météo, liste (§232)
builder.Services.AddSingleton<SafeHttp>();
builder.Services.AddSingleton<IAppProvider, WeatherProvider>();
builder.Services.AddSingleton<IAppProvider, WeatherSlideProvider>();
builder.Services.AddSingleton<IAppProvider, CalendarProvider>();
builder.Services.AddSingleton<IAppProvider, RssProvider>();
builder.Services.AddSingleton<IAppProvider, FacebookProvider>();
builder.Services.AddSingleton<IAppProvider, WebPageProvider>();
builder.Services.AddSingleton<IAppProvider, YouTubeProvider>();
builder.Services.AddSingleton<IHaTransport, HaTransport>();   // Home Assistant : appels directs (https public) ou par le relais Linkii (réseau local)
builder.Services.AddSingleton<HomeAssistantClient>();   // Home Assistant : compte de l'organisation, états, commandes, historique (voir Apps/HomeAssistant.cs)
builder.Services.AddSingleton<HomeAssistantLive>();   // temps réel (WebSocket ou relevé), écrans prévenus par SignalR, santé et alertes
builder.Services.AddHostedService(sp => sp.GetRequiredService<HomeAssistantLive>());
builder.Services.AddSingleton<IAppProvider, HomeAssistantProvider>();
builder.Services.AddSingleton<AppProviders>();
builder.Services.AddSingleton<GoogleAuth>();   // « Se connecter avec Google » (Calendar et Drive, lecture seule)
builder.Services.AddSingleton<MicrosoftAuth>();   // « Se connecter avec Microsoft » (agendas Microsoft 365 et OneDrive / SharePoint, lecture seule)
builder.Services.AddSingleton<ExternalLogin>();   // « Continuer avec Microsoft / Google » sur la page de connexion (identité seulement)
builder.Services.AddSingleton<NoticeService>();   // messages affichés un moment sur un écran, demandés de l'extérieur (§224)
builder.Services.AddSingleton<DeviceCommandService>();   // commandes d'appareil demandées de l'extérieur : API, Home Assistant (§221)
builder.Services.AddSingleton<ScreenControl>();   // contrôle du direct (Pro) : pause, redémarrage, aperçu
builder.Services.AddHostedService<ScreenWatchWorker>();   // commandes sans réponse, pauses terminées, alertes « écran hors ligne »
builder.Services.AddHostedService<SubscriptionWorker>();   // comptes bloqués faute de paiement à la date anniversaire
builder.Services.AddHostedService<SignupWorker>();   // inscriptions non validées supprimées, rappel et fin des essais gratuits
builder.Services.AddSingleton<DropboxAuth>();   // « Se connecter avec Dropbox » (lecture seule)
builder.Services.AddSingleton<FacebookAuth>();   // « Se connecter avec Facebook » (Pages administrées, lecture seule)
builder.Services.AddSingleton<RemoteDrives>();   // sessions Nextcloud (WebDAV), SFTP et Dropbox
builder.Services.AddSingleton<DriveService>();
builder.Services.AddSingleton<CloudFileService>();
builder.Services.AddSingleton<PresentationMerge>();
builder.Services.AddHostedService<PresentationMergeWorker>();   // fusion des doublons de présentations au démarrage (§103)
builder.Services.AddHostedService<CloudFilesWorker>();   // Google Slides, Docs et Microsoft PowerPoint : mise à jour quand le fichier change (réglage de chaque contenu, une heure par défaut)
builder.Services.AddHostedService<DriveSyncWorker>();   // dossiers des Drives : synchronisés toutes les 15 minutes
builder.Services.AddSingleton<ScanLog>();   // scans des QR codes de comptage (Pro) : journal par client, 90 jours
builder.Services.AddSingleton<AdLog>();   // passages des spots de publicité (§45) : journal par client, 13 mois
builder.Services.AddHostedService<AdWorker>();   // publicité : rappel aux propriétaires 24 h avant l'échéance de validation d'un spot
builder.Services.AddSingleton<RelayBroker>();   // relais Linkii (§139, lot 4) : tâches confiées aux relais du réseau des clients
builder.Services.AddSingleton<DataSetService>();   // applications personnalisées (§139) : lecture des jeux de données, copie et historique
builder.Services.AddHostedService<DataSyncWorker>();   // jeux de données relus à leur fréquence (plan Pro)
builder.Services.AddScoped<AppService>();
builder.Services.AddScoped<MediaLibrary>();
builder.Services.AddScoped<PlaylistLibrary>();
builder.Services.AddScoped<InteractiveListLibrary>();
builder.Services.AddScoped<CompositionLibrary>();
builder.Services.AddScoped<ScreenFolderLibrary>();
builder.Services.AddScoped<MenuBadges>();   // pastilles du menu latéral, en direct (voir MenuBadges)

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.Cookie.Name = "linkii.auth";
        o.Cookie.HttpOnly = true;
        o.Events.OnRedirectToAccessDenied = ctx =>   // page réservée à un autre rôle : retour à l'accueil du compte (jamais de boucle)
        {
            var home = Claims.Home(ctx.HttpContext.User);
            if (ctx.Request.Path.Value == home) ctx.Response.StatusCode = 403; else ctx.Response.Redirect(home);
            return Task.CompletedTask;
        };
        o.Cookie.SameSite = SameSiteMode.Lax;   // protège les POST de /logout et /space/* (pas de jeton anti-CSRF sur ces routes)
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
    });

static bool NoClient(ClaimsPrincipal u) => u.GetGuid(Claims.Client) == null;

/// <summary>Retour d'un fournisseur (Google, Microsoft, Dropbox…) : redirige, et note dans l'historique le compte relié (ou la tentative refusée).</summary>
static async Task<IResult> AccountLinked(HttpContext ctx, string provider, Task<string> complete)
{
    var to = await complete;
    if (ctx.User.GetGuid(Claims.Client) is { } cid)
    {
        var failed = to.Contains("error", StringComparison.OrdinalIgnoreCase) || to.Contains("erreur", StringComparison.OrdinalIgnoreCase) || to.Contains("denied", StringComparison.OrdinalIgnoreCase);
        ctx.RequestServices.GetRequiredService<HistoryLog>().Record(cid, History.Activity, History.Integrations, failed ? $"Connexion du compte {provider} refusée ou interrompue" : $"Compte {provider} connecté",
            failed ? Notifs.Warn : Notifs.Ok, null, ctx.User.Identity?.Name, ctx.User.GetGuid(ClaimTypes.NameIdentifier), "account", null, provider, "integrations", tech: failed);
    }
    return Results.Redirect(to);
}
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("ClientSpace", p => p.RequireClaim(Claims.Client));
    // Réglages, applications, widgets, aires : administrateurs de l'organisation (et équipe Linkivi / revendeur qui entrent dans l'espace), pas les membres
    o.AddPolicy("ClientAdmin", p => p.RequireAssertion(c => c.User.GetGuid(Claims.Client) != null && !c.User.IsInRole(Roles.ClientMember)));
    // Pages d'un membre : ouvertes par son niveau de droits (relu à chaque navigation) ; les administrateurs y accèdent toujours
    foreach (var page in LevelPages.All) o.AddPolicy("Page:" + page, p => p.RequireClaim(Claims.Client).AddRequirements(new PageRequirement(page)));
    o.AddPolicy("PlatformAdmin", p => p.RequireAssertion(c => c.User.IsInRole(Roles.PlatformAdmin) && NoClient(c.User)));
    o.AddPolicy("ResellerSpace", p => p.RequireAssertion(c => c.User.IsInRole(Roles.ResellerAdmin) && NoClient(c.User)));
});
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, PageHandler>();
builder.Services.AddCascadingAuthenticationState();

// Derrière un reverse proxy (NPM) : reprend le schéma https et l'IP réelle du client (X-Forwarded-Proto / -For).
// Proxys de confiance : Linkii:TrustedProxies (IP séparées par des virgules), sinon les réseaux privés.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
    var trusted = builder.Configuration["Linkii:TrustedProxies"];
    if (!string.IsNullOrWhiteSpace(trusted))
        foreach (var ip in trusted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            o.KnownProxies.Add(System.Net.IPAddress.Parse(ip));
    else
        foreach (var net in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "::1/128" })
        {
            var p = net.Split('/');
            o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse(p[0]), int.Parse(p[1])));
        }
});

var app = builder.Build();
WebSnapshot.KillOrphans();
app.UseForwardedHeaders();

var dataDir = app.Configuration["Linkii:DataDir"] ?? app.Environment.ContentRootPath;
AppPaths.MediaDir = Path.Combine(dataDir, "media");
AppPaths.BrandDir = Path.Combine(dataDir, "brand");
AppPaths.ThumbDir = Path.Combine(dataDir, "thumbs");
AppPaths.PdfDir = Path.Combine(dataDir, "pdf");
AppPaths.CaptureDir = Path.Combine(dataDir, "captures");
AppPaths.DownloadDir = Path.Combine(dataDir, "downloads");
Directory.CreateDirectory(AppPaths.MediaDir);
Directory.CreateDirectory(AppPaths.BrandDir);
Directory.CreateDirectory(AppPaths.ThumbDir);
Directory.CreateDirectory(AppPaths.PdfDir);
DataSetService.Root = Path.Combine(dataDir, "datasets");   // copies et historiques des jeux de données (§139)
Ads.Dir = Path.Combine(dataDir, "ads");   // visuels des spots de publicité (§45)
Directory.CreateDirectory(Ads.Dir);
app.Services.GetRequiredService<AdLog>();   // plafonds des campagnes : totaux des passages disponibles dès le démarrage

var store0 = app.Services.GetRequiredService<JsonStore>();
Seed.Run(store0, app.Configuration, store0.Path, app.Logger);
Seed.PurgeRetiredApps(store0, app.Services.GetRequiredService<AppCatalog>(), store0.Path, app.Logger);
Seed.PurgeOldCalendars(store0, store0.Path, app.Logger);
// Home Assistant : identifiant de l'application présenté à l'instance (profil › jetons de rafraîchissement), puis widgets et source de données des comptes déjà connectés
HomeAssistantRules.ClientId = app.Configuration["Linkii:HomeAssistant:ClientId"] is { Length: > 0 } haId ? haId
    : (app.Configuration["Linkii:SiteUrl"] is { Length: > 0 } site ? site.TrimEnd('/') : "https://www.linkivi.com") + "/";
HomeAssistantSetup.Migrate(store0, app.Services.GetRequiredService<AppCatalog>());
RoomSetup.Migrate(store0, app.Services.GetRequiredService<AppCatalog>());   // Réservation de salle : les organisations qui ont déjà des salles reçoivent l'application (D11)
app.Logger.LogInformation("Google (Calendar, Drive) : « Se connecter avec Google » {State}.", app.Services.GetRequiredService<GoogleAuth>().IsConfigured
    ? "disponible" : "indisponible (Linkii:Google:ClientId et Linkii:Google:ClientSecret absents de la configuration)");
app.Logger.LogInformation("Microsoft (Microsoft 365, OneDrive) : « Se connecter avec Microsoft » {State}.", app.Services.GetRequiredService<MicrosoftAuth>().IsConfigured
    ? "disponible" : "indisponible (Linkii:Microsoft:ClientId et Linkii:Microsoft:ClientSecret absents de la configuration)");
app.Logger.LogInformation("Canva : « Connecter Canva » {State}.", app.Services.GetRequiredService<CanvaService>().IsConfigured
    ? "disponible" : "indisponible (Linkii:Canva:ClientId et Linkii:Canva:ClientSecret absents de la configuration)");

// une conversion vidéo interrompue par un arrêt du serveur ne reprend pas : on la marque en échec
store0.Write(d =>
{
    foreach (var m in d.Media.Where(m => m.Status == "processing")) { m.Status = "failed"; m.Error = "Conversion interrompue par un redémarrage du serveur. Réimportez le fichier."; }
});
// fichiers rangés par organisation (médias, PDF, aperçus, miniatures) et par campagne (publicité)
if (store0.Write(MediaFiles.Migrate) is > 0 and var movedFiles) app.Logger.LogInformation("{Count} fichiers rangés dans les dossiers des organisations.", movedFiles);
if (store0.Write(InteractiveLists.Migrate) is > 0 and var listsMade) app.Logger.LogInformation("{Count} menus de bornes convertis en listes interactives.", listsMade);

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // le player (HTML/JS) est toujours revalidé : une correction publiée est reprise au prochain chargement
    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path;
        if (path.StartsWithSegments("/player") || path.StartsWithSegments("/app.css")
            || path.StartsWithSegments("/sw.js") || path.StartsWithSegments("/pwa.js") || path.StartsWithSegments("/offline.html"))
            ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
});   // wwwroot : app.css, /player/*
app.UseStaticFiles(new StaticFileOptions   // logos des revendeurs : publics (affichés avant la connexion)
{
    FileProvider = new PhysicalFileProvider(AppPaths.BrandDir),
    RequestPath = "/brand"
});

// Historique (diagnostic) : une exception non gérée d'une requête, rangée dans l'espace de la personne connectée (puis relancée : la réponse d'erreur reste celle de l'application).
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex) when (ex is not OperationCanceledException && ctx.User.GetGuid(Claims.Client) is { })
    {
        var cid = ctx.User.GetGuid(Claims.Client)!.Value;
        var first = ex.StackTrace?.Split('\n').Take(4).Select(l => l.Trim());
        ctx.RequestServices.GetRequiredService<HistoryLog>().Record(cid, History.Diag, History.Server, $"Erreur serveur : {ctx.Request.Method} {ctx.Request.Path}", Notifs.Error, ex.Message, "Système",
            tech: true, facts: new() { new[] { "Type", ex.GetType().Name }, new[] { "Requête", $"{ctx.Request.Method} {ctx.Request.Path}" }, new[] { "Identifiant", ctx.TraceIdentifier } },
            trace: ex.Message + (first == null ? "" : "\n" + string.Join("\n", first)));
        throw;
    }
});

// Revendeur de la requête, déduit du nom de domaine. Un sous-domaine inconnu n'existe pas.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/internal")) { await next(); return; }
    var reseller = ctx.RequestServices.GetRequiredService<ResellerResolver>().Resolve(ctx.Request.Host.Host);
    if (reseller == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("Domaine inconnu."); return; }
    ctx.Items["reseller"] = reseller;
    await next();
});

app.UseAuthentication();
app.UseRateLimiter();   // limites par clé de l'API publique (les routes /api/v1 seules choisissent une classe de débit)
app.UseApiV1();   // identifiant de requête, usage par clé, idempotence
app.UseAuthorization();

// Session : le compte doit exister, être actif, correspondre au domaine, et son client / revendeur ne pas être suspendus.
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path;
    if (ctx.User.Identity?.IsAuthenticated == true && !p.StartsWithSegments("/api") && !p.StartsWithSegments("/hubs") && !p.StartsWithSegments("/logout"))
    {
        var host = (Reseller)ctx.Items["reseller"]!;
        var uid = ctx.User.GetGuid(ClaimTypes.NameIdentifier);
        var cid = ctx.User.GetGuid(Claims.Client);
        var store = ctx.RequestServices.GetRequiredService<JsonStore>();
        bool mustChange = false, roleChanged = false, stale = false, awaitsVerification = false;
        var ok = store.Read(db =>
        {
            var u = db.Users.FirstOrDefault(x => x.Id == uid && !x.Disabled);
            if (u == null || !Tenancy.HostAllows(u, host)) return false;
            mustChange = u.MustChangePassword && !ctx.User.HasClaim(c => c.Type == Claims.Impersonating);
            awaitsVerification = u.AwaitsVerification;
            roleChanged = !ctx.User.IsInRole(u.Role);   // administrateur de l'organisation ↔ membre : le cookie est refait
            stale = u.LastActiveUtc == null || DateTime.UtcNow - u.LastActiveUtc > TimeSpan.FromHours(1);
            u.LastRequestUtc = DateTime.UtcNow;   // non persisté
            if (u.Role != Roles.PlatformAdmin && db.Resellers.FirstOrDefault(r => r.Id == u.ResellerId)?.Active != true) return false;
            if (cid == null) return true;
            var c = db.Clients.FirstOrDefault(x => x.Id == cid);
            if (c == null) return false;
            return u.Role == Roles.PlatformAdmin || u.Role == Roles.ResellerAdmin && c.ResellerId == u.ResellerId
                   || Roles.IsClient(u.Role) && u.ClientId == c.Id && !c.Blocked;
        });
        if (!ok)
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            ctx.Response.Redirect("/login");
            return;
        }
        if (stale) store.Write(db => { if (db.Users.FirstOrDefault(x => x.Id == uid) is { } u) u.LastActiveUtc = DateTime.UtcNow; });   // utilisateurs actifs (console)
        if (roleChanged && !ctx.User.HasClaim(c => c.Type == Claims.Impersonating) && !p.StartsWithSegments("/_blazor") && !p.StartsWithSegments("/_framework") && HttpMethods.IsGet(ctx.Request.Method))
        {
            var fresh = store.Read(db => Claims.Build(db, db.Users.First(x => x.Id == uid)));
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, fresh, new AuthenticationProperties { IsPersistent = true });
            ctx.Response.Redirect(p + ctx.Request.QueryString);
            return;
        }
        // Inscription par e-mail non confirmée, puis mot de passe provisoire : rien d'autre n'est accessible avant.
        if ((awaitsVerification || mustChange) && !p.StartsWithSegments("/login") && !p.StartsWithSegments("/_framework") && !p.StartsWithSegments("/_blazor"))
        {
            ctx.Response.Redirect(awaitsVerification ? "/login/verify" : "/login/password");
            return;
        }
    }
    await next();
});

// Porte d'entrée du back-office : tout est protégé sauf le login, l'API/hub des players et le transport Blazor.
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path;
    var open = p.StartsWithSegments("/login") || p.StartsWithSegments("/api") || p.StartsWithSegments("/hubs")
               || p.StartsWithSegments("/_framework") || p.StartsWithSegments("/_blazor") || p.StartsWithSegments("/logout")
               || p.StartsWithSegments("/internal") || p.StartsWithSegments("/media") || p.StartsWithSegments("/ads")   // /media et /ads font leur propre contrôle d'accès (jeton de l'écran ou session)
               || p.StartsWithSegments("/salle")   // réservation par QR : scannée par le téléphone d'un visiteur, sans compte (code signé dans l'adresse)
               || p.StartsWithSegments("/q")   // adresse courte des QR codes de comptage : scannée par le téléphone d'un visiteur, sans compte
               || p.StartsWithSegments("/download")   // installeur du player Windows : téléchargé sur le PC de l'écran, souvent sans session
               || p.StartsWithSegments("/canva/callback")   // retour de Canva : identifié par son « state », sur le domaine principal
               || p.StartsWithSegments("/google/callback")  // retour de Google (Drive) : idem
               || p.StartsWithSegments("/dropbox/callback")  // retour de Dropbox : idem
               || p.StartsWithSegments("/facebook/callback")  // retour de Facebook : idem
               || p.StartsWithSegments("/microsoft/callback")  // retour de Microsoft : idem
               || p.StartsWithSegments("/linkedin/callback") || p.StartsWithSegments("/github/callback")  // retours de LinkedIn et GitHub (connexion seulement)
               || p.StartsWithSegments("/docs")   // documentation publique de l'API (aux couleurs du revendeur)
               || p.StartsWithSegments("/manifest.webmanifest");   // le navigateur le demande sans cookie de session
    if (!open && ctx.User.Identity?.IsAuthenticated != true)
    {
        ctx.Response.Redirect("/login");
        return;
    }
    await next();
});
app.UseAntiforgery();

// Manifeste de l'application installable (écran d'accueil iPhone / iPad) : nom et couleur du revendeur du domaine.
app.MapGet("/manifest.webmanifest", (HttpContext ctx) =>
{
    // Installation sur l'écran d'accueil : réservée à Apple (pas d'application iPhone / iPad). Android et Windows ont l'application Linkivi :
    // sans manifeste, Chrome et Edge ne proposent plus d'installer le site. Safari (iPadOS en mode « site de bureau » compris) le reçoit.
    var ua = ctx.Request.Headers.UserAgent.ToString();
    var apple = ua.Contains("iPhone") || ua.Contains("iPad") || ua.Contains("iPod")
        || ua.Contains("Macintosh") && !ua.Contains("Chrome") && !ua.Contains("Edg/") && !ua.Contains("Firefox");
    if (!apple) return Results.NotFound();
    var r = ctx.Items["reseller"] as Reseller;
    var name = r?.DisplayName is { Length: > 0 } n ? n : "Linkivi";
    var color = r?.BrandColor is { Length: > 0 } c ? c : "#0B1F3A";
    ctx.Response.Headers.CacheControl = "no-cache";
    return Results.Json(new
    {
        id = "/", name, short_name = name.Length > 12 ? name[..12].TrimEnd() : name,
        description = "Pilotez vos écrans d'affichage : listes de lecture, médiathèque et applications.",
        lang = "fr", start_url = "/", scope = "/", display = "standalone", orientation = "any",
        background_color = "#F5F7FA", theme_color = color,
        icons = new object[]
        {
            new { src = "/icons/icon-192.png", sizes = "192x192", type = "image/png", purpose = "any" },
            new { src = "/icons/icon-512.png", sizes = "512x512", type = "image/png", purpose = "any" },
            new { src = "/icons/maskable-512.png", sizes = "512x512", type = "image/png", purpose = "maskable" }
        }
    }, contentType: "application/manifest+json");
});

// Canva : connexion du compte Canva d'un client. Départ depuis la page Applications (utilisateur connecté à un espace client),
// retour de Canva sur l'adresse déclarée dans le portail développeurs (un seul domaine), puis renvoi vers le domaine d'origine.
app.MapGet("/canva/connect", (HttpContext ctx, CanvaService canva) =>
{
    var cid = ctx.User.GetGuid(Claims.Client);
    if (cid == null) return Results.Redirect("/");
    if (!canva.IsConfigured) return Results.Redirect("/applications?canva=unconfigured");
    var back = $"{ctx.Request.Scheme}://{ctx.Request.Host}" + AccountPurposes.ReturnPage(ctx.Request);
    return Results.Redirect(canva.StartAuthorization(cid.Value, back));
});
app.MapGet("/canva/callback", async (HttpContext ctx, HttpRequest req, CanvaService canva) =>
    await AccountLinked(ctx, "Canva", canva.CompleteAsync(req.Query["state"], req.Query["code"], req.Query["error"])));

// Compte Google : « Se connecter avec Google » depuis Applications › Google Calendar (?for=calendar), Google Drive (?for=drive)
// ou Comptes connectés (?for=all&tab=comptes : retour sur cet onglet),
// application OAuth de la plateforme, lecture seule. Un seul compte par organisation ; chaque application ajoute son accès.
app.MapGet("/google/connect", (HttpContext ctx, GoogleAuth google) =>
{
    var cid = ctx.User.GetGuid(Claims.Client);
    if (cid == null) return Results.Redirect("/");
    var purpose = AccountPurposes.Parse(ctx.Request.Query["for"]);
    if (!google.IsConfigured) return Results.Redirect($"/applications?for={purpose}&google=unconfigured");
    var here = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    return Results.Redirect(google.StartAuthorization(cid.Value, purpose, here + AccountPurposes.ReturnPage(ctx.Request), here));
});
app.MapGet("/google/callback", async (HttpContext ctx, HttpRequest req, GoogleAuth google, ExternalLogin sso) =>
    sso.Owns(req.Query["state"])   // « Continuer avec Google » de la page de connexion : même adresse de retour
        ? Results.Redirect(await sso.CompleteAsync(req.Query["state"], req.Query["code"], req.Query["error"], req.Query["error_description"]))
        : await AccountLinked(ctx, "Google", google.CompleteAsync(req.Query["state"], req.Query["code"], req.Query["error"])));

// Compte Dropbox : « Se connecter avec Dropbox » depuis Applications › Dropbox ou Comptes connectés (?tab=comptes : retour sur cet onglet),
// application OAuth de la plateforme, lecture seule. Un seul compte par organisation.
app.MapGet("/dropbox/connect", (HttpContext ctx, DropboxAuth dropbox) =>
{
    var cid = ctx.User.GetGuid(Claims.Client);
    if (cid == null) return Results.Redirect("/");
    if (!dropbox.IsConfigured) return Results.Redirect("/applications?dropbox=unconfigured");
    var here = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    return Results.Redirect(dropbox.StartAuthorization(cid.Value, here + AccountPurposes.ReturnPage(ctx.Request), here));
});
app.MapGet("/dropbox/callback", async (HttpContext ctx, HttpRequest req, DropboxAuth dropbox) =>
    await AccountLinked(ctx, "Dropbox", dropbox.CompleteAsync(req.Query["state"], req.Query["code"], req.Query["error"])));

// Compte Facebook : « Se connecter avec Facebook » depuis Réglages › Connexions (retour sur cet onglet) ou la fenêtre d'un contenu Facebook,
// application Meta de la plateforme, lecture seule des Pages administrées. Un seul compte par organisation.
app.MapGet("/facebook/connect", (HttpContext ctx, FacebookAuth facebook) =>
{
    var cid = ctx.User.GetGuid(Claims.Client);
    if (cid == null) return Results.Redirect("/");
    if (!facebook.IsConfigured) return Results.Redirect("/applications?facebook=unconfigured");
    var here = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    return Results.Redirect(facebook.StartAuthorization(cid.Value, here + AccountPurposes.ReturnPage(ctx.Request), here));
});
app.MapGet("/facebook/callback", async (HttpContext ctx, HttpRequest req, FacebookAuth facebook) =>
    await AccountLinked(ctx, "Facebook", facebook.CompleteAsync(req.Query["state"], req.Query["code"], req.Query["error"])));

// Compte Microsoft : « Se connecter avec Microsoft » depuis Applications › Microsoft 365 (?for=calendar), OneDrive et SharePoint (?for=drive)
// ou Comptes connectés (?for=all&tab=comptes : retour sur cet onglet),
// application Entra ID de la plateforme, lecture seule. Un seul compte par organisation ; chaque application ajoute son accès.
app.MapGet("/microsoft/connect", (HttpContext ctx, MicrosoftAuth microsoft) =>
{
    var cid = ctx.User.GetGuid(Claims.Client);
    if (cid == null) return Results.Redirect("/");
    var purpose = AccountPurposes.Parse(ctx.Request.Query["for"]);
    if (!microsoft.IsConfigured) return Results.Redirect($"/applications?for={purpose}&microsoft=unconfigured");
    var here = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    return Results.Redirect(microsoft.StartAuthorization(cid.Value, purpose, here + AccountPurposes.ReturnPage(ctx.Request), here));
});
app.MapGet("/microsoft/callback", async (HttpContext ctx, HttpRequest req, MicrosoftAuth microsoft, ExternalLogin sso) =>
    sso.Owns(req.Query["state"])   // « Continuer avec Microsoft » de la page de connexion : même adresse de retour
        ? Results.Redirect(await sso.CompleteAsync(req.Query["state"], req.Query["code"], req.Query["error"], req.Query["error_description"]))
        : await AccountLinked(ctx, "Microsoft", microsoft.CompleteAsync(req.Query["state"], req.Query["code"], req.Query["error"], req.Query["error_description"])));

// ---------- Création de compte et connexion : Microsoft / Google / LinkedIn / GitHub, confirmation de l'adresse e-mail ----------

app.MapGet("/linkedin/callback", async (HttpRequest req, ExternalLogin sso) =>
    Results.Redirect(await sso.CompleteAsync(req.Query["state"], req.Query["code"], req.Query["error"], req.Query["error_description"])));
app.MapGet("/github/callback", async (HttpRequest req, ExternalLogin sso) =>
    Results.Redirect(await sso.CompleteAsync(req.Query["state"], req.Query["code"], req.Query["error"], req.Query["error_description"])));

// Départ : ?mode=signup (créer un espace) ou login. Le retour passe par /google/callback ou /microsoft/callback, puis /login/sso/finish.
app.MapGet("/login/sso/{provider}", (HttpContext ctx, string provider, ExternalLogin sso) =>
{
    var signup = ctx.Request.Query["mode"] == ExternalLogin.SignupMode;
    var url = sso.Start(provider, signup ? ExternalLogin.SignupMode : ExternalLogin.Login, $"{ctx.Request.Scheme}://{ctx.Request.Host}");
    return Results.Redirect(url ?? (signup ? "/login?signup=1&sso=unconfigured" : "/login?sso=unconfigured"));
});

// Identité reçue : connexion d'un compte existant, ou dernière étape de la création d'un espace (nom de l'organisation, consentements).
app.MapGet("/login/sso/finish", async (HttpContext ctx, string? t, ExternalLogin sso, JsonStore store) =>
{
    var ticket = sso.Peek(t);
    if (ticket == null) return Results.Redirect("/login?sso=expired");
    var host = (Reseller)ctx.Items["reseller"]!;
    var id = ticket.Identity;
    var (userId, problem) = store.Write(db =>
    {
        var u = db.Users.FirstOrDefault(x => x.ExternalProvider == id.Provider && x.ExternalId == id.Id && Tenancy.HostAllows(x, host));
        // Google garantit l'adresse (email_verified) : un compte existant avec cette adresse est relié. Microsoft ne la garantit pas
        // pour tous les annuaires : pas de rattachement par e-mail, le compte se connecte avec son mot de passe.
        if (u == null && id.Provider == SignupSources.Google)
        {
            u = db.Users.FirstOrDefault(x => x.Email == id.Email && x.ExternalProvider == null && Tenancy.HostAllows(x, host));
            if (u != null)
            {
                u.ExternalProvider = id.Provider; u.ExternalId = id.Id;
                if (u.EmailVerifiedUtc == null) Signup.Verify(db, u, db.Clients.FirstOrDefault(c => c.Id == u.ClientId), DateTime.UtcNow);
            }
        }
        if (u == null) return ((Guid?)null, (string?)(db.Users.Any(x => x.Email == id.Email && Tenancy.HostAllows(x, host)) ? "taken" : "nolink"));
        if (u.Disabled || u.ClientId != null && db.Clients.FirstOrDefault(c => c.Id == u.ClientId) is not { Blocked: false }) return (null, "suspended");
        return (u.Id, null);
    });
    if (userId is { } uid)
    {
        sso.Consume(t);
        return Results.Redirect(await SessionCookie.SignIn(ctx, store, uid));
    }
    // Aucun compte pour cette identité : les mêmes boutons servent à se connecter et à s'inscrire. Si l'inscription libre est ouverte,
    // on passe à la dernière étape de la création d'un espace (le ticket reste valable) ; sinon, message « aucun espace ».
    if (problem == "nolink" && host.Active && host.AllowsSignupWith(id.Provider))
        return Results.Redirect("/login?signup=1&ext=" + Uri.EscapeDataString(t!));
    sso.Consume(t);
    return Results.Redirect($"/login?sso={problem}&p={id.Provider}");
});

// Lien de l'e-mail de confirmation : l'adresse est validée, l'essai démarre, l'e-mail de bienvenue part.
app.MapGet("/login/verify/confirm", async (HttpContext ctx, string? t, JsonStore store, Mailer mail, ILogger<Program> log) =>
{
    var now = DateTime.UtcNow;
    var (result, user, client, reseller) = store.Write(db =>
    {
        var (r, u, c) = Signup.Confirm(db, t, now);
        return (r, u, c, db.Resellers.FirstOrDefault(x => x.Id == c?.ResellerId));
    });
    if (result != Signup.ConfirmResult.Ok) return Results.Redirect("/login/verify/done?state=" + (result == Signup.ConfirmResult.Expired ? "expired" : "invalid"));
    if (reseller != null && user!.WelcomeSentUtc == null)
    {
        var err = await mail.SendSignupWelcome(reseller, $"{ctx.Request.Scheme}://{ctx.Request.Host}/", user!.Name, user.Email, client!.Name,
            store.Read(db => Signup.TrialDaysOf(db, client)), client.TrialEndsUtc is { } end ? Signup.Day(client, end) : null, Signup.SignInHint(user),
            deleteNote: store.Read(db => Lifecycle.WelcomeDeleteNote(db, client)));
        if (err != null) log.LogWarning("E-mail de bienvenue non envoyé à {Email} : {Error}", user.Email, err);
        else store.Write(db => { if (db.Users.FirstOrDefault(u => u.Id == user.Id) is { } u) u.WelcomeSentUtc = DateTime.UtcNow; });
    }
    await SessionCookie.SignIn(ctx, store, user!.Id);
    return Results.Redirect("/login/verify/done");
});

app.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

// ---------- Entrer dans l'espace d'un client (revendeur pour ses clients, équipe Linkivi pour tous) : journalisé ----------
app.MapPost("/space/enter", async (HttpContext ctx, JsonStore store) =>
{
    var form = await ctx.Request.ReadFormAsync();
    if (!Guid.TryParse(form["clientId"], out var clientId)) return Results.BadRequest();
    var uid = ctx.User.GetGuid(ClaimTypes.NameIdentifier);
    var principal = store.Write(db =>
    {
        var u = db.Users.FirstOrDefault(x => x.Id == uid && !x.Disabled);
        var c = db.Clients.FirstOrDefault(x => x.Id == clientId);
        if (u == null || c == null) return null;
        var allowed = u.Role == Roles.PlatformAdmin || u.Role == Roles.ResellerAdmin && u.ResellerId == c.ResellerId;
        if (!allowed) return null;
        db.AccessLog.Add(new AccessLogEntry { UserId = u.Id, UserEmail = u.Email, Role = u.Role, ClientId = c.Id, ClientName = c.Name, Action = "enter" });
        return Claims.Build(db, u, c.Id, impersonating: true);
    });
    if (principal == null) return Results.StatusCode(403);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);   // cookie de session : l'accès ne survit pas à la fermeture du navigateur
    return Results.Redirect("/");
}).RequireAuthorization().DisableAntiforgery();

app.MapPost("/space/exit", async (HttpContext ctx, JsonStore store) =>
{
    var uid = ctx.User.GetGuid(ClaimTypes.NameIdentifier);
    var cid = ctx.User.GetGuid(Claims.Client);
    if (!ctx.User.HasClaim(c => c.Type == Claims.Impersonating)) return Results.Redirect("/");
    var principal = store.Write(db =>
    {
        var u = db.Users.FirstOrDefault(x => x.Id == uid && !x.Disabled);
        if (u == null) return null;
        db.AccessLog.Add(new AccessLogEntry { UserId = u.Id, UserEmail = u.Email, Role = u.Role, ClientId = cid ?? Guid.Empty, ClientName = db.Clients.FirstOrDefault(c => c.Id == cid)?.Name ?? "", Action = "exit" });
        return Claims.Build(db, u);
    });
    if (principal == null) return Results.StatusCode(403);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { IsPersistent = true });
    return Results.Redirect(Claims.Home(principal));
}).RequireAuthorization().DisableAntiforgery();

// Caddy / Traefik (certificat à la demande) : n'émettre un certificat que pour un domaine déclaré par un revendeur actif.
// QR code de comptage : le scan est compté (licence Pro, contenu activé, pas un robot) puis le visiteur est redirigé vers la destination.
// La redirection a toujours lieu tant que le contenu existe, même si le suivi est coupé : un QR encore affiché ne doit jamais être cassé.
// Réservation par QR : page du téléphone (choix de la durée, commentaire) puis confirmation. Sans compte : le code signé de l'adresse identifie l'écran de salle.
app.MapGet("/salle/{code}", async (string code, JsonStore store, RoomStatusService status) =>
{
    var now = DateTime.UtcNow;
    var (t, s, room) = store.Read<(Tenant?, Screen?, SharedCalendar?)>(db =>
    {
        var s = RoomQr.Resolve(db, code, now);
        var t = s == null ? null : db.Clients.FirstOrDefault(c => c.Id == s.ClientId);
        return (t, s, s?.RoomCalendarId is { } rid ? t?.SharedCalendars.FirstOrDefault(c => c.Id == rid) : null);
    });
    IResult Html(string h, int st = 200) => Results.Text(h, "text/html; charset=utf-8", statusCode: st);
    if (t == null || s == null) return Html(RoomQr.Page(code, "", "", new(), "Ce QR n'est plus valable. Scannez celui qui est affiché sur l'écran de la salle."), 410);
    var name = room != null ? SharedCalendarSources.RoomName(room) : "";
    if (t.RoomApp?.Qr != true || room == null || s.Room?.Buttons != true || !RoomModes.CanBook(room.RoomWriteMode)) return Html(RoomQr.Page(code, name, "", new(), "La réservation par QR n'est pas proposée pour cette salle."));
    var snap = await status.Read(t.Id, room.Id, fresh: true);
    if (snap == null || snap.Error != null) return Html(RoomQr.Page(code, name, "", new(), "L'agenda de la salle ne répond pas : réessayez dans un instant."));
    var tz = RoomRules.Zone(t.Timezone);
    var opts = RoomRules.Options(now, snap.Events, t.RoomApp, room.RoomAllDay, tz);
    var start = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
    var list = opts.Select(o => new RoomQr.Option(o.Minutes, RoomRules.Label(o.Minutes), o.Ok, o.Why, $"{RoomRules.Hm(o.Start ?? start, tz)} – {RoomRules.Hm(o.End ?? start.AddMinutes(o.Minutes), tz)}")).ToList();
    var until = RoomRules.State(now, snap.Events, room.RoomSoon, room.RoomAllDay, tz);
    var text = opts.Count == 0 ? "" : until.UntilUtc is { } u && until.State != "busy" ? $"Libre jusqu'à {RoomRules.Hm(u, tz)}." : "Libre.";
    return Html(RoomQr.Page(code, name, text, list, opts.Count == 0 ? "La salle vient d'être prise." : null));
});
app.MapPost("/salle/{code}/book", async (string code, RoomQrBookDto body, JsonStore store, RoomBookingService rooms) =>
{
    var now = DateTime.UtcNow;
    var (id, tz) = store.Read<(Guid?, TimeZoneInfo)>(db =>
    {
        var s = RoomQr.Resolve(db, code, now);
        return (s?.Id, RoomRules.Zone(s == null ? null : db.Clients.FirstOrDefault(c => c.Id == s.ClientId)?.Timezone));
    });
    if (id == null) return Results.Json(new { error = "Ce QR n'est plus valable. Scannez celui qui est affiché sur l'écran de la salle.", code = "expired" }, statusCode: 410);
    var o = await rooms.Book(id.Value, body.Minutes, body.Comment, viaQr: true);
    return o.Status == 200 ? Results.Ok(new { id = o.Id, from = o.From, to = o.To, until = RoomRules.Hm(o.To!.Value, tz) }) : Results.Json(new { error = o.Error, code = o.Code }, statusCode: o.Status);
});

app.MapPost("/salle/{code}/cancel", async (string code, RoomCancelDto body, JsonStore store, RoomBookingService rooms) =>
{
    var id = store.Read(db => RoomQr.Resolve(db, code, DateTime.UtcNow)?.Id);
    if (id == null) return Results.Json(new { error = "Ce QR n'est plus valable.", code = "expired" }, statusCode: 410);
    var o = await rooms.Cancel(id.Value, body.Id);
    return o.Status == 200 ? Results.Ok() : Results.Json(new { error = o.Error, code = o.Code }, statusCode: o.Status);
});
app.MapGet("/q/{code}", (string code, HttpContext ctx, JsonStore store, ScanLog scans) =>
{
    if (!QrTracking.IsCode(code)) return Results.NotFound();
    var hit = store.Read(db =>
    {
        foreach (var p in db.Playlists)
            if (p.Draft.FirstOrDefault(x => x.QrCode == code) is { } it)
            {
                var client = db.Clients.FirstOrDefault(c => c.Id == p.ClientId);
                return new QrHit(p.ClientId, it.Id, QrTracking.Destination(it.QrUrl), it.QrOn && client is { IsGrowth: true, IsCut: false });
            }
        return null;
    });
    if (hit?.Destination == null) return Results.NotFound();
    var ua = ctx.Request.Headers.UserAgent.ToString();
    if (hit.Counts && !QrTracking.LooksLikeBot(ua, ctx.Request.Method))
        scans.Append(hit.ClientId, new ScanEntry(DateTime.UtcNow, hit.ItemId, scans.Visitor(ctx.Connection.RemoteIpAddress?.ToString(), ua, DateTime.UtcNow)));
    ctx.Response.Headers.CacheControl = "no-store";   // chaque scan doit passer par le serveur
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    return Results.Redirect(hit.Destination);   // 302
});

app.MapGet("/internal/tls-allow", (string domain, ResellerResolver resolver) =>
    resolver.IsKnownDomain(domain) ? Results.Ok() : Results.StatusCode(403));

// ---------- Téléchargement du player Windows (public : l'installeur ne contient rien du client) ----------
// Dernier Linkivi-*.msi du dossier downloads des données (déposé par build-installer.ps1 en local, à la main sur le serveur).
app.MapGet("/download/windows", () =>
{
    var msi = Directory.Exists(AppPaths.DownloadDir)
        ? new DirectoryInfo(AppPaths.DownloadDir).GetFiles("Linkivi-*.msi").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
        : null;
    return msi == null
        ? Results.Content("Le player Windows n'est pas encore disponible au téléchargement.", "text/plain; charset=utf-8", statusCode: 404)
        : Results.File(msi.FullName, "application/x-msi", msi.Name);
});

// Player Android (APK) : dernier Linkivi-*.apk du même dossier ; installation directe sur une TV, un boîtier ou une tablette.
app.MapGet("/download/android", () =>
{
    var apk = PlayerDownloads.Latest("android");
    return apk == null
        ? Results.Content("Le player Android n'est pas encore disponible au téléchargement.", "text/plain; charset=utf-8", statusCode: 404)
        : Results.File(apk.Path, "application/vnd.android.package-archive", apk.Name);
});

// Player Linux (Raspberry Pi OS 64 bits) : dernier linkivi-pi-*.tar.gz du même dossier, déposé par build-package.ps1.
app.MapGet("/download/linux", () =>
{
    var pkg = PlayerDownloads.Latest("linux");
    return pkg == null
        ? Results.Content("Le player Linux n'est pas encore disponible au téléchargement.", "text/plain; charset=utf-8", statusCode: 404)
        : Results.File(pkg.Path, "application/gzip", pkg.Name);
});

// Relais Linkii (§139, lot 4), fichiers du dossier downloads déposés par Linkii.Relay/publish.ps1 :
//  - Windows : LinkiiRelay-Setup.exe, l'installateur (c'est le relais lui-même : il demande le code, installe et démarre le service). Le serveur ajoute
//    à la fin du fichier « #LINKII-SERVER=https://… » (sans effet sur l'exécutable) : l'installateur connaît ainsi l'adresse du serveur qui l'a servi ;
//  - Linux : LinkiiRelay-linux-x64.zip (appairage et service systemd en ligne de commande).
app.MapGet("/download/relay/{os}", async (HttpContext ctx, string os) =>
{
    var pattern = os switch { "windows" => "LinkiiRelay-Setup*.exe", "linux-x64" => "LinkiiRelay-linux-x64*.zip", _ => null };
    var file = pattern != null && Directory.Exists(AppPaths.DownloadDir)
        ? new DirectoryInfo(AppPaths.DownloadDir).GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
        : null;
    if (file == null)
        return pattern == null ? Results.NotFound() : Results.Content("Le relais Linkii n'est pas encore disponible au téléchargement.", "text/plain; charset=utf-8", statusCode: 404);
    if (os != "windows") return Results.File(file.FullName, "application/zip", "LinkiiRelay-linux-x64.zip");
    var trailer = System.Text.Encoding.UTF8.GetBytes("\n#LINKII-SERVER=" + ctx.Request.Scheme + "://" + ctx.Request.Host + "\n");
    ctx.Response.ContentType = "application/octet-stream";
    ctx.Response.Headers.ContentDisposition = "attachment; filename=\"LinkiiRelay-Setup.exe\"";
    ctx.Response.ContentLength = file.Length + trailer.Length;
    await ctx.Response.SendFileAsync(file.FullName);
    await ctx.Response.Body.WriteAsync(trailer);
    return Results.Empty;
});

// ---------- Médias : l'accès est contrôlé par client ----------
// Autorisé : un écran du client propriétaire (en-tête X-Token ou ?t=) ou un utilisateur connecté sur l'espace de ce client.
// Tout le reste reçoit 404, pour ne pas révéler l'existence d'un fichier d'un autre client.
var mediaTypes = new FileExtensionContentTypeProvider();
app.MapGet("/media/{file}", async (HttpContext ctx, string file, JsonStore store, VideoConverter converter, PreviewGrants grants) =>
{
    if (file != Path.GetFileName(file)) return Results.NotFound();
    var token = ctx.Request.Headers["X-Token"].ToString();
    if (string.IsNullOrEmpty(token)) token = ctx.Request.Query["t"].ToString();
    var userClient = ctx.User.Identity?.IsAuthenticated == true ? ctx.User.GetGuid(Claims.Client) : null;
    // aperçu (?pv=jeton) : le player en mode aperçu, y compris sur un téléphone sans compte (lien des Premiers pas, 24 h)
    var previewClient = ctx.Request.Query["pv"].ToString() is { Length: > 0 } pv ? grants.Find(pv)?.ClientId : null;

    var m = store.Read(db =>
    {
        var it = db.Media.FirstOrDefault(x => x.FileName == file);
        if (it == null) return null;
        if (userClient == it.ClientId || previewClient == it.ClientId) return it;
        return !string.IsNullOrEmpty(token) && db.Screens.Any(s => s.Token == token && s.ClientId == it.ClientId && Tenancy.IsActive(db, s)) ? it : null;
    });
    var path = m?.FilePath;   // media/{organisation}/{membre}/{fichier} : l'adresse, elle, ne change pas
    if (m == null || !File.Exists(path)) return Results.NotFound();
    if (!mediaTypes.TryGetContentType(file, out var type)) type = "application/octet-stream";
    if (int.TryParse(ctx.Request.Query["w"], out var tw) && tw > 0 && Thumbnails.Get(m, Thumbnails.Snap(tw)) is { } thumbPath)
    {
        ctx.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(thumbPath, "image/webp");
    }
    if (tw > 0 && VideoConverter.IsVideo(Path.GetExtension(file).ToLowerInvariant()))   // vignette d'une vidéo : une image, pas le fichier
    {
        if (await Thumbnails.GetVideo(converter, m, Thumbnails.Snap(tw), ctx.RequestAborted) is not { } poster) return Results.NotFound();
        ctx.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(poster, "image/jpeg");
    }
    ctx.Response.Headers.CacheControl = "private, max-age=300";
    return Results.File(path, type, enableRangeProcessing: true);
});

// Capture d'une page web (vignette de l'app « Page web ») : même contrôle d'accès que les médias, servie depuis le disque seulement.
app.MapGet("/webshot/{id:guid}", (HttpContext ctx, Guid id, JsonStore store, WebSnapshot snap) =>
{
    var token = ctx.Request.Headers["X-Token"].ToString();
    if (string.IsNullOrEmpty(token)) token = ctx.Request.Query["t"].ToString();
    var userClient = ctx.User.Identity?.IsAuthenticated == true ? ctx.User.GetGuid(Claims.Client) : null;
    var url = store.Read(db =>
    {
        var m = db.Media.FirstOrDefault(x => x.Id == id && x.AppId == "webpage");
        if (m == null) return null;
        var ok = userClient == m.ClientId
            || !string.IsNullOrEmpty(token) && db.Screens.Any(s => s.Token == token && s.ClientId == m.ClientId && Tenancy.IsActive(db, s));
        return ok ? m.Settings.GetValueOrDefault("url") : null;
    });
    if (snap.Cached(url) is not { } path) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "private, max-age=3600";
    return Results.File(path, "image/jpeg");
});

// ---------- API consommée par le player ----------
var api =app.MapGroup("/api");

// Les écrans se connectent à une adresse neutre : leur code d'appairage identifie le client. Le domaine (ou ?r=slug)
// ne sert qu'à habiller l'écran d'appairage aux couleurs du revendeur.
api.MapPost("/pairing/start", (StartDto? body, HttpContext ctx, JsonStore store) => store.Write(db =>
{
    string code;
    do { code = Random.Shared.Next(0, 1_000_000).ToString("D6"); }
    while (db.Screens.Any(x => x.PairingCode == code));
    var s = new Screen { Name = "(non appairé)", PairingCode = code, DetectedW = body?.W, DetectedH = body?.H };
    db.Screens.Add(s);

    var hint = (Reseller)ctx.Items["reseller"]!;
    var slug = ctx.Request.Query["r"].ToString();
    if (hint.IsDefault && slug.Length > 0) hint = db.Resellers.FirstOrDefault(r => r.Slug == slug && r.Active) ?? hint;
    return new { screenId = s.Id, code, brand = BrandDto.From(hint) };
}));

api.MapGet("/pairing/status", (Guid screenId, JsonStore store) => store.Read(db =>
{
    var s = db.Screens.FirstOrDefault(x => x.Id == screenId);
    if (s?.RepairTo is { } to)   // player réinstallé associé à un écran existant : il reprend le jeton de cet écran
        return db.Screens.FirstOrDefault(x => x.Id == to && x.Token != null) is { } target ? Results.Ok(new { paired = true, token = target.Token, code = (string?)null }) : Results.NotFound();
    return s == null
        ? Results.NotFound()
        : Results.Ok(new { paired = s.Token != null, token = s.Token, code = s.PairingCode });
}));

static Screen? Auth(HttpRequest r, Db db)
{
    var token = r.Headers["X-Token"].ToString();
    var s = string.IsNullOrEmpty(token) ? null : db.Screens.FirstOrDefault(x => x.Token == token);
    if (s != null) s.LastSeenUtc = DateTime.UtcNow;
    return s;
}

/// <summary>401 si le jeton est inconnu, 403 si le client ou le revendeur de l'écran est suspendu, ou l'essai gratuit terminé (reason : suspended | trial).</summary>
static IResult? Gate(Screen? s, Db db) =>
    s == null ? Results.Unauthorized() : Tenancy.Blocked(db, s) is { } why ? Results.Json(new { reason = why }, statusCode: 403) : null;

api.MapGet("/player/playlist", (HttpRequest req, JsonStore store) => store.Read(db =>
{
    var s = Auth(req, db);
    if (Gate(s, db) is { } denied) return denied;
    return Results.Ok(PlayerFeed.ForScreen(db, s!, device: true));
}));

// build : empreinte du code du player (wwwroot/player) ; un écran ouvert se recharge quand elle change (nouvelle version de Linkivi).
var playerBuild = PlayerBuild(app.Environment.WebRootPath);
api.MapGet("/player/version", (HttpRequest req, JsonStore store) => store.Read(db =>
{
    var s = Auth(req, db);
    if (Gate(s, db) is { } denied) return denied;
    // serverNow : horloge de référence du player (lecture synchronisée) ; commands : ordres du back-office pas encore accusés (filet si SignalR est coupé)
    return Results.Ok(new { version = Notifier.Revision(db, s!), build = playerBuild, serverNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), commands = ScreenControl.Pending(s!, DateTime.UtcNow), wakeUntil = Math.Max(Sleep.Woken(s!, DateTime.UtcNow) ? Helpers.EpochMs(s!.WakeUntilUtc!.Value) : 0L, Emergencies.WakeUntilMs(db, s!, DateTime.UtcNow)), emergency = Emergencies.PayloadFor(db, s!, DateTime.UtcNow), notice = Notices.PayloadFor(s!, DateTime.UtcNow) });   // urgence : réveille l'écran et le message (null : aucun)   // wakeUntil : réveil manuel demandé (l'app Android le sonde pendant la veille)
}));

// Urgence : l'écran confirme afficher le message (rapport de réception du back-office et de l'API).
api.MapPost("/player/emergency-ack", (HttpRequest req, EmergencyAckDto body, JsonStore store, EmergencyService emergencies) =>
{
    var id = store.Read(db => { var s = Auth(req, db); return Gate(s, db) is null ? s?.Id : null; });
    if (id == null) return Results.Unauthorized();
    emergencies.Ack(id.Value, body.Id);
    return Results.Ok();
});

// Appareil : système, modèle et version de l'application, remontés au démarrage puis régulièrement.
api.MapPost("/player/device", (HttpRequest req, DeviceDto body, JsonStore store) => store.Write(db =>
{
    var s = Auth(req, db);
    if (Gate(s, db) is { } denied) return denied;
    static string? Clean(string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : (v.Trim().Length > max ? v.Trim()[..max] : v.Trim());
    s!.DeviceKind = body.Kind is "androidtv" or "android" or "windows" or "raspberrypi" ? body.Kind : "web";
    s.DeviceOs = Clean(body.Os, 40); s.DeviceModel = Clean(body.Model, 60); s.DeviceApp = Clean(body.App, 20);
    s.DevicePinSig = Clean(body.PinSig, 16);
    s.DeviceOwner = s.DeviceKind is "android" or "androidtv" && body.Owner == true;
    // PC Windows : identifiant du PC, instance du player et moniteurs (plusieurs écrans sur un même PC, choix du moniteur)
    s.DeviceHost = s.DeviceKind == "windows" ? Clean(body.Host, 40) : null;
    s.DeviceInstance = s.DeviceKind == "windows" ? Clean(body.Instance, 40) : null;
    s.DeviceMonitors = s.DeviceKind == "windows" && body.Monitors != null
        ? body.Monitors.Where(m => m.N is > 0 and < 17 && m.W > 0 && m.H > 0).Take(16).Select(m => new DeviceMonitor { N = m.N, W = m.W, H = m.H, Primary = m.Primary }).ToList()
        : new();
    // batterie : seuls un smartphone ou une tablette Android la remontent (ni la TV, ni Windows, ni le navigateur) ; sinon vide
    if (s.DeviceKind == "android" && body.Battery is >= 0 and <= 100) { s.BatteryPct = body.Battery; s.BatteryCharging = body.Charging == true; }
    else { s.BatteryPct = null; s.BatteryCharging = false; s.BatteryAlertSent = false; }
    if (body.BootMs is > 0) s.DeviceBootUtc = DateTimeOffset.FromUnixTimeMilliseconds(body.BootMs.Value).UtcDateTime;
    if (body.Touch != null) s.DeviceTouch = body.Touch;   // écran tactile : l'assistant « Nouvel écran de salle » propose les boutons de durée
    return Results.Ok();
}));

// Réservation de salle : la porte d'un écran de salle lit sa salle (créneaux, durées possibles, mode) et y réserve. Le serveur décide toujours.
api.MapGet("/player/room", async (HttpRequest req, JsonStore store, RoomStatusService status) =>
{
    var (denied, s, t, room, installed) = store.Read<(IResult?, Screen?, Tenant?, SharedCalendar?, bool)>(db =>
    {
        var s = Auth(req, db);
        if (Gate(s, db) is { } no) return (no, null, null, null, false);
        var t = db.Clients.First(c => c.Id == s!.ClientId);
        return (null, s, t, s!.RoomCalendarId is { } rid ? t.SharedCalendars.FirstOrDefault(c => c.Id == rid) : null, RoomSetup.Installed(db, t.Id));
    });
    if (denied != null) return denied;
    if (s?.Room == null || t == null) return Results.NotFound();
    return Results.Ok(await RoomDoor.Payload(status, t, s, room, installed, DateTime.UtcNow, baseUrl: $"{req.Scheme}://{req.Host}"));
});
api.MapPost("/player/room/book", async (HttpRequest req, RoomBookDto body, JsonStore store, RoomBookingService rooms) =>
{
    var (denied, id) = store.Read<(IResult?, Guid?)>(db => { var s = Auth(req, db); return (Gate(s, db), s?.Id); });
    if (denied != null) return denied;
    var o = await rooms.Book(id!.Value, body.Minutes);
    return o.Status == 200 ? Results.Ok(new { from = o.From, to = o.To, id = o.Id }) : Results.Json(new { error = o.Error, code = o.Code }, statusCode: o.Status);
});
api.MapPost("/player/room/cancel", async (HttpRequest req, RoomCancelDto body, JsonStore store, RoomBookingService rooms) =>
{
    var (denied, id) = store.Read<(IResult?, Guid?)>(db => { var s = Auth(req, db); return (Gate(s, db), s?.Id); });
    if (denied != null) return denied;
    var o = await rooms.Cancel(id!.Value, body.Id);
    return o.Status == 200 ? Results.Ok() : Results.Json(new { error = o.Error, code = o.Code }, statusCode: o.Status);
});

// Accusé d'une commande du back-office : reçue, exécutée ou en échec.
api.MapPost("/player/command-ack", (HttpRequest req, CommandAckDto body, JsonStore store) => store.Write(db =>
{
    var s = Auth(req, db);
    if (Gate(s, db) is { } denied) return denied;
    return ScreenControl.Ack(s!, body.Id, body.Status ?? "", body.Detail) ? Results.Ok() : Results.NotFound();
}));

// Aperçu du direct : l'application Android envoie l'image (JPEG) de l'écran ; la dernière seule est gardée.
api.MapPost("/player/capture", async (HttpRequest req, Guid? cmd, bool? plain, JsonStore store) =>
{
    if (req.ContentLength is > 2_000_000) return Results.StatusCode(413);
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    if (ms.Length > 2_000_000) return Results.StatusCode(413);
    var bytes = ms.ToArray();
    return store.Write(db =>
    {
        var s = Auth(req, db);
        if (Gate(s, db) is { } denied) return denied;
        return ScreenControl.SaveCapture(db, s!, bytes, cmd, DateTime.UtcNow, plain == true) ? Results.Ok() : Results.BadRequest();
    });
});

static string PlayerBuild(string webRoot)
{
    var dir = Path.Combine(webRoot, "player");
    if (!Directory.Exists(dir)) return "";
    using var sha = System.Security.Cryptography.SHA256.Create();
    foreach (var f in Directory.GetFiles(dir).Order(StringComparer.Ordinal))
    {
        var bytes = File.ReadAllBytes(f);
        sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
    }
    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
    return Convert.ToHexString(sha.Hash!)[..12];
}

// Accusé de publication : stage « downloading » / « ready » (étapes avant l'affichage), sinon la version est affichée.
api.MapPost("/player/ack", (HttpRequest req, AckDto body, JsonStore store, IEventPublisher events) => store.Write(db =>
{
    var s = Auth(req, db);
    if (Gate(s, db) is { } denied) return denied;
    if (body.Stage is "downloading" or "ready")
    {
        s!.DeliveryRevision = body.Version; s.DeliveryStage = body.Stage; s.DeliveryUtc = DateTime.UtcNow;
        return Results.Ok();
    }
    if (s!.AppliedRevision != body.Version)
    {
        s.AppliedUtc = DateTime.UtcNow;
        if (s.PublishedUtc != null) events.Publish(s.ClientId, ScreenEvents.Applied, new { screen = ScreenEvents.Brief(db, s), version = s.PublishedVersion, appliedAt = s.AppliedUtc });   // webhooks (API)
    }
    s.AppliedRevision = body.Version;
    if (body.W > 0 && body.H > 0) { s.DetectedW = body.W; s.DetectedH = body.H; }
    return Results.Ok();
}));

// Historique de diffusion : le player remonte par lots les contenus affichés (gardés en mémoire locale s'il est hors ligne).
api.MapPost("/player/plays", (HttpRequest req, List<PlayDto> body, JsonStore store, PlayLog log) =>
{
    var now = DateTime.UtcNow;
    var (screen, denied, entries) = store.Read(db =>
    {
        var s = Auth(req, db);
        if (Gate(s, db) is { } no) return ((Screen?)null, (IResult?)no, new List<PlayEntry>());   // 403 (suspendu) et non 401 : le player se désappairerait
        var names = db.Media.Where(m => m.ClientId == s!.ClientId).ToDictionary(m => m.Id, m => m.Name);
        var list = body.Take(500)
            .Select(p => (p, at: DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(p.Start, 0, 253402300799000)).UtcDateTime))
            .Where(x => x.at > now.AddDays(-PlayLog.RetentionDays) && x.at < now.AddMinutes(5) && x.p.Sec is > 0 and <= 86400)
            .Select(x => new PlayEntry(x.at, s!.Id, x.p.MediaId, x.p.AppId is { Length: <= 64 } a ? a : null,
                x.p.MediaId is { } mid && names.TryGetValue(mid, out var n) ? n : "(contenu supprimé)", x.p.Sec))
            .ToList();
        return ((Screen?)s, (IResult?)null, list);
    });
    if (denied != null) return denied;
    log.Append(screen!.ClientId, entries);
    return Results.Ok(new { saved = entries.Count });
});

// Borne interactive : visites, touchers, tuiles ouvertes, QR scannés, remontés par lots (gardés en local hors ligne). Aucune donnée personnelle.
api.MapPost("/player/kiosk-events", (HttpRequest req, List<KioskEventDto> body, JsonStore store, KioskLog log) =>
{
    var now = DateTime.UtcNow;
    var (screen, denied, entries) = store.Read(db =>
    {
        var s = Auth(req, db);
        if (Gate(s, db) is { } no) return ((Screen?)null, (IResult?)no, new List<KioskEvent>());
        var types = new[] { "visit", "tile", "touch", "qr" };
        var list = body.Take(500)
            .Select(e => (e, at: DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(e.At, 0, 253402300799000)).UtcDateTime))
            .Where(x => x.at > now.AddDays(-KioskLog.RetentionDays) && x.at < now.AddMinutes(5) && types.Contains(x.e.Type))
            .Select(x => new KioskEvent(x.at, s!.Id, x.e.Type, x.e.Tile,
                x.e.Tile is { } tid ? db.InteractiveLists.Where(l => l.Id == s.Kiosk?.ListId).SelectMany(l => l.Pages).SelectMany(p => p.Els).FirstOrDefault(el => el.Id == tid)?.Text is { Length: > 0 } tt ? tt : (x.e.Title is { Length: <= 80 } n ? n : null) : null,
                Math.Clamp(x.e.Sec, 0, 86400), x.e.How is "auto" or "back" ? x.e.How : null))
            .ToList();
        return ((Screen?)s, (IResult?)null, list);
    });
    if (denied != null) return denied;
    log.Append(screen!.ClientId, entries);
    return Results.Ok(new { saved = entries.Count });
});

// Publicité (§45) : passages de spots remontés par lots. Dédoublonnés (identifiant de passage), contrôlés (spot connu, dates, durées, chevauchements),
// et comptés seulement pour un écran qui a droit à la publicité (licence Pro, réglage activé, aire et écran autorisés).
api.MapPost("/player/ad-plays", (HttpRequest req, List<AdPlayDto> body, JsonStore store, AdLog log) =>
{
    var now = DateTime.UtcNow;
    var (screen, denied, campaigns, allowed) = store.Read(db =>
    {
        var s = Auth(req, db);
        if (Gate(s, db) is { } no) return ((Screen?)null, (IResult?)no, new List<AdCampaign>(), false);
        var t = db.Clients.First(c => c.Id == s!.ClientId);
        return ((Screen?)s, (IResult?)null, db.AdCampaigns.ToList(), Ads.ScreenAllowed(db, t, s!));
    });
    if (denied != null) return denied;
    if (!allowed) return Results.Ok(new { saved = 0 });   // accusé quand même : le player vide sa file
    var since = now.AddDays(-1);
    var plays = AdLog.Check(body, screen!.Id, campaigns, log.Read(screen.ClientId, since), now);
    return Results.Ok(new { saved = log.Append(screen.ClientId, plays).Count });
});

// Visuels des spots : servis aux écrans (jeton actif) et aux personnes connectées (administration, propriétaires qui valident les spots).
app.MapGet("/ads/{file}", (HttpContext ctx, string file, JsonStore store) =>
{
    if (file != Path.GetFileName(file) || Ads.FileType(file) == null) return Results.NotFound();
    var token = ctx.Request.Headers["X-Token"].ToString();
    if (string.IsNullOrEmpty(token)) token = ctx.Request.Query["t"].ToString();
    var (ok, path) = store.Read(db => (ctx.User.Identity?.IsAuthenticated == true
        || !string.IsNullOrEmpty(token) && db.Screens.Any(s => s.Token == token && Tenancy.IsActive(db, s)), Ads.PathOf(db, file)));
    if (!ok || !File.Exists(path)) return Results.NotFound();
    if (!mediaTypes.TryGetContentType(file, out var type)) type = "application/octet-stream";
    ctx.Response.Headers.CacheControl = "private, max-age=86400";
    return Results.File(path, type, enableRangeProcessing: true);
});

// Données des apps : l'URL des sources et les secrets restent côté serveur.
// Un écran ne lit que les données de son propre client, d'après l'instantané publié dans la liste de lecture de l'écran
// (les modifications de paramètres non publiées n'ont aucun effet).
api.MapGet("/data/{id:guid}", async (HttpRequest req, Guid id, JsonStore store, AppCatalog catalog, AppProviders providers, SecretBox box) =>
{
    var (denied, tenant, published) = store.Read<(IResult?, Tenant, PublishedItem?)>(db =>
    {
        var s = Auth(req, db);
        var gate = Gate(s, db);
        if (gate != null) return (gate, null!, null);
        var pub = PlayerFeed.DataItems(db, s!).FirstOrDefault(x => x.Id == id && x.AppId != null);   // toutes les zones de l'écran
        return ((IResult?)null, db.Clients.First(c => c.Id == s!.ClientId), pub);
    });
    if (denied != null) return denied;
    if (published == null) return Results.NotFound();
    return await AppData(published, tenant, catalog, providers, box);
});

// Source d'une composition publiée (bloc météo, champs d'un calendrier…, §85) : mêmes règles, réglages figés dans la composition à la publication.
api.MapGet("/data/{id:guid}/{src:guid}", async (HttpRequest req, Guid id, Guid src, JsonStore store, AppCatalog catalog, AppProviders providers, SecretBox box, DataSetService data) =>
{
    var (denied, tenant, published, own) = store.Read<(IResult?, Tenant, PublishedItem?, IResult?)>(db =>
    {
        var s = Auth(req, db);
        var gate = Gate(s, db);
        if (gate != null) return (gate, null!, null, null);
        var pub = PlayerFeed.DataItems(db, s!).FirstOrDefault(x => x.Id == id && x.AppId == Compositions.AppId);
        var t = db.Clients.First(c => c.Id == s!.ClientId);
        return ((IResult?)null, t, pub, pub == null ? null : CompositionData(db, t, pub, src, s, data));
    });
    if (denied != null) return denied;
    if (own != null) return own;
    if (published == null || Compositions.SourceItem(published, src) is not { } source) return Results.NotFound();
    return await AppData(source, tenant, catalog, providers, box);
});

// Sources d'une composition servies par Linkii lui-même (§139) : « Cet écran » (nom, zone, paramètres) et jeux de données (dernière copie,
// réduite aux colonnes utilisées). Null : la source est un contenu d'application ordinaire (météo, RSS…).
static IResult? CompositionData(Db db, Tenant t, PublishedItem pub, Guid src, Screen? screen, DataSetService data)
{
    if (Compositions.UsesScreen(pub, src)) return Results.Ok(Compositions.ScreenData(db, screen));
    if (Compositions.DataRef(pub, src) is not { } keys) return null;
    if (t.DataSets.FirstOrDefault(d => d.Id == src) is not { } ds) return Results.NotFound();
    if (data.Table(t.Id, src) is not { } table) return Results.Json(new { error = "Données pas encore lues." }, statusCode: 503);   // l'écran garde ce qu'il avait
    var stale = DataSetService.IsStale(ds, table, DateTime.UtcNow);
    return stale ? Results.Ok(new { kind = "table", error = "Données indisponibles" }) : Results.Ok(DataTables.ForScreen(table, keys, false));
}

static async Task<IResult> AppData(PublishedItem published, Tenant tenant, AppCatalog catalog, AppProviders providers, SecretBox box)
{
    var app = catalog.Find(published.AppId);
    var provider = app is { Data: true } ? providers.Find(app.Provider) : null;
    if (provider == null) return Results.NotFound();
    try
    {
        var values = new Dictionary<string, string>(published.Settings);
        foreach (var (k, v) in published.Secrets) values[k] = box.Unprotect(v);
        var data = await provider.Fetch(new AppRuntime(tenant, values));
        return data == null ? Results.NotFound() : Results.Ok(data);
    }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
}

// ---------- Relais Linkii (§139, lot 4) : le relais appelle le serveur, jamais l'inverse (voir RelayProtocol.cs) ----------
api.MapPost("/relay/pair", (RelayPairRequest body, RelayBroker relays) =>
    relays.Pair(body) is { } r ? Results.Ok(r) : Results.Json(new { error = "Code inconnu ou expiré : créez-en un nouveau dans Réglages › Connexions › Relais." }, statusCode: 404));

api.MapGet("/relay/next", async (HttpRequest req, RelayBroker relays, CancellationToken ct) =>
{
    if (relays.Auth(req.Headers["X-Relay-Token"]) is not { } who) return Results.Unauthorized();
    var job = await relays.Next(who.Relay, req.Headers["X-Relay-Machine"], req.Headers["X-Relay-Version"], ct);
    return job == null ? Results.NoContent() : Results.Ok(job);
});

api.MapPost("/relay/reply", (HttpRequest req, RelayReply body, RelayBroker relays) =>
{
    if (relays.Auth(req.Headers["X-Relay-Token"]) is not { } who) return Results.Unauthorized();
    return relays.Complete(who.Relay, body) ? Results.Ok() : Results.NotFound();   // tâche inconnue, expirée, ou d'un autre relais
});   // corps : 30 Mo par défaut (Kestrel), assez pour un fichier de 10 Mo en base64

// Commande d'un widget Home Assistant depuis l'écran tactile (interrupteur, lumière, thermostat, volet, scène, serrure). Seule une entité choisie dans ce widget
// peut être commandée, seulement si son domaine appartient au widget et accepte cette commande, et jamais si « Commande à l'écran » est coupée.
// « switch » : ancienne adresse ({ entityId, on }), gardée pour les écrans pas encore rechargés.
async Task<IResult> HaCommand(HttpRequest req, Guid id, HaCommandDto body, JsonStore store, HomeAssistantClient ha, HomeAssistantLive live)
{
    var (denied, clientId, account, published, allowed) = store.Read<(IResult?, Guid, HomeAssistantAccount?, PublishedItem?, List<string>)>(db =>
    {
        var s = Auth(req, db);
        var gate = Gate(s, db);
        if (gate != null) return (gate, Guid.Empty, null, null, new());
        var pub = PlayerFeed.DataItems(db, s!).FirstOrDefault(x => x.Id == id && HomeAssistantRules.IsHaApp(x.AppId));
        var tenant = db.Clients.First(c => c.Id == s!.ClientId);
        var ids = pub == null ? new() : HomeAssistantRules.EntitiesOf(tenant, pub.AppId, pub.Settings);   // plan : les appareils du plan d'aujourd'hui, même modifié depuis la publication
        // vue du bâtiment (§200) : seulement une vue interactive, et seulement les familles qu'elle coche (la vue d'aujourd'hui, même modifiée depuis la publication)
        // (§202 : aussi le widget « Plan du bâtiment » dont l'affichage est choisi)
        if (pub != null && HaViews.Chosen(pub.AppId, pub.Settings)) { var v = HaViews.Of(tenant, pub.Settings); ids = ids.Where(e => HaViews.Allows(v, e)).ToList(); }
        return ((IResult?)null, s!.ClientId, tenant.HomeAssistant?.Copy(), pub, ids);
    });
    if (denied != null) return denied;
    if (published == null) return Results.NotFound();
    var entityId = body.EntityId ?? "";
    var action = HomeAssistantRules.ActionOf(body);
    if ((published.Settings.GetValueOrDefault("control") == "false" && !HaViews.Chosen(published.AppId, published.Settings))   // affichage choisi : c'est lui qui décide
        || !allowed.Contains(entityId) || !HomeAssistantRules.Commandable(published.AppId!, entityId, action))
        return Results.BadRequest(new { error = "Entité non configurée ou non actionnable." });
    if (account == null) return Results.Json(new { error = "Compte Home Assistant non connecté." }, statusCode: 409);
    try
    {
        await ha.Command(ha.Conn(clientId, account), entityId, action, body.Value, body.Color);
        live.Nudge(clientId);
        return Results.Ok();
    }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
    {
        return Results.Json(new { error = HomeAssistantLive.Readable(ex) }, statusCode: 502);
    }
}
api.MapPost("/player/app/{id:guid}/ha", HaCommand);
api.MapPost("/player/app/{id:guid}/switch", HaCommand);

// ---------- Simulateur du back-office : le player en mode aperçu ----------
// Jeton d'aperçu (en-tête X-Preview) créé par le back-office pour un écran, un mur ou une liste de lecture auxquels la personne a accès.
// Mêmes données que l'écran, sans effet : l'écran n'est pas marqué « vu », aucune diffusion n'est comptée, aucun ordre n'est reçu.
static (Dictionary<string, object?> Feed, List<PublishedItem> Items)? PreviewFeed(Db db, PreviewGrant g, Notifier notifier)
{
    if (!db.Clients.Any(c => c.Id == g.ClientId)) return null;
    if (g.PlaylistId is { } pid)
    {
        var p = db.Playlists.FirstOrDefault(x => x.Id == pid && x.ClientId == g.ClientId);
        if (p == null) return null;
        var items = notifier.Items(db, p);   // état actuel de la liste, même non publié
        return (PlayerFeed.ForPlaylist(db, p, items, g), items);
    }
    if (g.InteractiveListId is { } lid)   // aperçu tactile : la liste interactive dans son état actuel, sans borne
    {
        var l = db.InteractiveLists.FirstOrDefault(x => x.Id == lid && x.ClientId == g.ClientId);
        if (l == null) return null;
        var k = notifier.PreviewKiosk(db, l);
        return (PlayerFeed.ForInteractive(db, l, k, g), k.Pages.SelectMany(p => p.Els).Select(e => e.Action.Item).OfType<PublishedItem>().ToList());
    }
    var s = g.WallZoneId is { } zid   // mur : le premier écran placé donne la publication et la grille
        ? db.Screens.Where(x => x.ClientId == g.ClientId && x.ZoneId == zid && x.Token != null && x.WallPos != null).OrderBy(x => x.WallPos).FirstOrDefault()
        : db.Screens.FirstOrDefault(x => x.Id == g.ScreenId && x.ClientId == g.ClientId && x.Token != null);
    if (s == null) return null;
    var feed = PlayerFeed.ForScreen(db, s, whole: g.WallZoneId != null);
    // mur : format du jeton (réglages du mur essayés dans le simulateur, pas forcément encore appliqués)
    feed["screen"] = g.WallZoneId != null ? new { orientation = g.Orientation, resolution = g.Resolution } : new { orientation = s.Orientation, resolution = PlayerFeed.Resolution(s) };
    return (feed, PlayerFeed.DataItems(db, s));
}

api.MapGet("/preview/playlist", (HttpRequest req, JsonStore store, PreviewGrants grants, Notifier notifier, AppCatalog catalog) =>
{
    if (grants.Find(req.Headers["X-Preview"]) is not { } g) return Results.Unauthorized();
    return store.Read(db =>
    {
        if (PreviewFeed(db, g, notifier) is not { } f) return Results.NotFound();
        f.Feed["meta"] = PlayerFeed.Meta(db, f.Items, catalog);   // noms et couleurs : frise du simulateur
        return Results.Ok(f.Feed);
    });
});

// Simulateur d'un écran de salle : la porte avec les vraies données de la salle ; la réservation y est simulée (rien n'est écrit).
api.MapGet("/preview/room", async (HttpRequest req, JsonStore store, PreviewGrants grants, RoomStatusService status) =>
{
    if (grants.Find(req.Headers["X-Preview"]) is not { } g) return Results.Unauthorized();
    var (s, t, room, installed) = store.Read<(Screen?, Tenant?, SharedCalendar?, bool)>(db =>
    {
        var s = db.Screens.FirstOrDefault(x => x.Id == g.ScreenId && x.ClientId == g.ClientId && x.Token != null);
        var t = db.Clients.FirstOrDefault(c => c.Id == g.ClientId);
        return (s, t, s?.RoomCalendarId is { } rid ? t?.SharedCalendars.FirstOrDefault(c => c.Id == rid) : null, t != null && RoomSetup.Installed(db, t.Id));
    });
    if (s?.Room == null || t == null) return Results.NotFound();
    return Results.Ok(await RoomDoor.Payload(status, t, s, room, installed, DateTime.UtcNow, baseUrl: $"{req.Scheme}://{req.Host}"));
});

api.MapGet("/preview/data/{id:guid}", async (HttpRequest req, Guid id, JsonStore store, PreviewGrants grants, Notifier notifier, AppCatalog catalog, AppProviders providers, SecretBox box) =>
{
    if (grants.Find(req.Headers["X-Preview"]) is not { } g) return Results.Unauthorized();
    var (tenant, published) = store.Read(db => (db.Clients.FirstOrDefault(c => c.Id == g.ClientId),
        PreviewFeed(db, g, notifier)?.Items.FirstOrDefault(x => x.Id == id && x.AppId != null)));
    if (tenant == null || published == null) return Results.NotFound();
    return await AppData(published, tenant, catalog, providers, box);
});

api.MapGet("/preview/data/{id:guid}/{src:guid}", async (HttpRequest req, Guid id, Guid src, JsonStore store, PreviewGrants grants, Notifier notifier, AppCatalog catalog, AppProviders providers, SecretBox box, DataSetService data) =>
{
    if (grants.Find(req.Headers["X-Preview"]) is not { } g) return Results.Unauthorized();
    var (tenant, published, own) = store.Read(db =>
    {
        var t = db.Clients.FirstOrDefault(c => c.Id == g.ClientId);
        var pub = PreviewFeed(db, g, notifier)?.Items.FirstOrDefault(x => x.Id == id && x.AppId == Compositions.AppId);
        // écran simulé (ou le premier écran du mur) ; une liste de lecture simulée n'a pas d'écran : « Cet écran » reste vide
        var screen = g.ScreenId is { } sid ? db.Screens.FirstOrDefault(x => x.Id == sid && x.ClientId == g.ClientId)
            : g.WallZoneId is { } zid ? db.Screens.Where(x => x.ClientId == g.ClientId && x.ZoneId == zid && x.WallPos != null).OrderBy(x => x.WallPos).FirstOrDefault() : null;
        return (t, pub, t == null || pub == null ? null : CompositionData(db, t, pub, src, screen, data));
    });
    if (own != null) return own;
    if (tenant == null || published == null || Compositions.SourceItem(published, src) is not { } source) return Results.NotFound();
    return await AppData(source, tenant, catalog, providers, box);
});

// Dernier aperçu du direct d'un écran, pour les personnes qui ont accès à l'écran (jamais mis en cache).
app.MapGet("/screens/{id:guid}/capture.jpg", (HttpContext ctx, Guid id, JsonStore store) =>
{
    var cid = ctx.User.GetGuid(Claims.Client);
    var uid = ctx.User.GetGuid(ClaimTypes.NameIdentifier);
    if (cid == null) return Results.Unauthorized();
    var screen = store.Read(db => new ClientDb(db, cid.Value, AreaAccess.For(db, cid.Value, uid)).Screens.FirstOrDefault(s => s.Id == id && s.CaptureUtc != null));
    var path = screen == null ? null : ScreenControl.CapturePath(screen);
    if (path == null || !File.Exists(path)) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.File(path, "image/jpeg");
}).RequireAuthorization("ClientSpace");

// Notifications push : l'app Android du back-office confie son jeton Firebase à la page (pont LinkiiAdmin), qui l'enregistre avec la session de la personne.
app.MapPost("/api/push/register", (HttpContext ctx, PushRegisterDto body, JsonStore store) =>
{
    var uid = ctx.User.GetGuid(ClaimTypes.NameIdentifier);
    var token = body.Token?.Trim() ?? "";
    if (uid == null || token.Length < 20 || token.Length > 4096) return Results.BadRequest();
    store.Write(db =>
    {
        db.PushDevices.RemoveAll(d => d.Token == token);   // le même appareil change de compte : le jeton suit la dernière personne connectée
        db.PushDevices.Add(new PushDevice { UserId = uid.Value, Token = token, Name = (body.Name ?? "").Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, 80)] : "Appareil Android" });
    });
    return Results.NoContent();
}).RequireAuthorization().DisableAntiforgery();

app.MapPost("/api/push/unregister", (HttpContext ctx, PushRegisterDto body, JsonStore store) =>
{
    var uid = ctx.User.GetGuid(ClaimTypes.NameIdentifier);
    store.Write(db => db.PushDevices.RemoveAll(d => d.UserId == uid && d.Token == body.Token));
    return Results.NoContent();
}).RequireAuthorization().DisableAntiforgery();

// Licence d'une installation sur site : le serveur du client vérifie sa clé chaque jour (voir Enterprise). Réponse : valide, échéance, tolérance.
app.MapPost("/api/license/check", (HttpContext ctx, LicenseCheckDto body, JsonStore store) =>
{
    var from = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    var r = store.Write(db => Enterprise.Verify(db, body.Key, from, DateTime.UtcNow));
    return Results.Json(new { valid = r.Valid, reason = r.Reason, expires = r.Expires, screens = r.Screens, modules = r.Modules, graceDays = r.GraceDays });
}).DisableAntiforgery();

app.MapLinkiiApi();   // /api/v1, /api/v1/openapi.json, /docs/api
app.MapHub<ScreenHub>("/hubs/screen");

// ---------- Back-office Blazor ----------
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

record AckDto(string Version, int? W, int? H, string? Stage = null);
record EmergencyAckDto(Guid Id);
record QrHit(Guid ClientId, Guid ItemId, string? Destination, bool Counts);
record PlayDto(Guid? MediaId, string? AppId, long Start, int Sec);
record KioskEventDto(long At, string Type, Guid? Tile, string? Title, int Sec, string? How);
record StartDto(int? W, int? H);
record DeviceDto(string? Kind, string? Os, string? Model, string? App, long? BootMs, string? PinSig = null, string? Host = null, string? Instance = null, List<MonitorDto>? Monitors = null, int? Battery = null, bool? Charging = null, bool? Owner = null, bool? Touch = null);
record RoomBookDto(int Minutes);
record MonitorDto(int N, int W, int H, bool Primary);
record CommandAckDto(Guid Id, string? Status, string? Detail);

public partial class Program;
record PushRegisterDto(string? Token, string? Name);
record LicenseCheckDto(string? Key);
