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
builder.Services.AddSingleton<Mailer>();
builder.Services.AddScoped<Toaster>();
builder.Services.AddSingleton<PlayLog>();
builder.Services.AddSingleton<CanvaService>();
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<TenantStore>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<PreviewGrants>();   // jetons d'aperçu du simulateur
builder.Services.AddSingleton<VideoConverter>();
builder.Services.AddSingleton<MediaImporter>();
builder.Services.AddSingleton<WeatherService>();
builder.Services.AddSingleton<GraphService>();
builder.Services.AddSingleton<ICalendarConnector, M365Connector>();
builder.Services.AddSingleton<ICalendarConnector, IcsConnector>();
builder.Services.AddSingleton<GoogleConnector>();   // aussi utilisé par Intégrations (liste des agendas du compte Google)
builder.Services.AddSingleton<ICalendarConnector>(sp => sp.GetRequiredService<GoogleConnector>());
builder.Services.AddSingleton<ICalendarConnector, CalDavConnector>();
builder.Services.AddSingleton<ICalendarConnector, EwsConnector>();
builder.Services.AddSingleton<ProviderDetector>();
builder.Services.AddSingleton<CalendarService>();

// Catalogue d'apps (manifestes du dossier Apps/), secrets chiffrés, appels sortants protégés, fournisseurs de données
var dataDirEarly = builder.Configuration["Linkii:DataDir"] ?? builder.Environment.ContentRootPath;
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirEarly, "keys"))).SetApplicationName("Linkii");
builder.Services.AddSingleton<AppCatalog>();
builder.Services.AddSingleton<SecretBox>();
builder.Services.AddSingleton<SafeHttp>();
builder.Services.AddSingleton<IAppProvider, WeatherProvider>();
builder.Services.AddSingleton<IAppProvider, CalendarProvider>();
builder.Services.AddSingleton<IAppProvider, RssProvider>();
builder.Services.AddSingleton<IAppProvider, WebPageProvider>();
builder.Services.AddSingleton<IAppProvider, YouTubeProvider>();
builder.Services.AddSingleton<AppProviders>();
builder.Services.AddSingleton<GoogleAuth>();   // « Se connecter avec Google » (Calendar et Drive, lecture seule)
builder.Services.AddSingleton<MicrosoftAuth>();   // « Se connecter avec Microsoft » (agendas Microsoft 365 et OneDrive / SharePoint, lecture seule)
builder.Services.AddSingleton<ExternalLogin>();   // « Continuer avec Microsoft / Google » sur la page de connexion (identité seulement)
builder.Services.AddSingleton<ScreenControl>();   // contrôle du direct (Growth) : pause, redémarrage, aperçu
builder.Services.AddHostedService<ScreenWatchWorker>();   // commandes sans réponse, pauses terminées, alertes « écran hors ligne »
builder.Services.AddHostedService<SignupWorker>();   // inscriptions non validées supprimées, rappel et fin des essais gratuits
builder.Services.AddSingleton<DriveService>();
builder.Services.AddHostedService<DriveSyncWorker>();   // dossiers des Drives : synchronisés toutes les 15 minutes
builder.Services.AddScoped<AppService>();
builder.Services.AddScoped<MediaLibrary>();

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
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("ClientSpace", p => p.RequireClaim(Claims.Client));
    // Réglages, intégrations, widgets, aires : administrateurs de l'organisation (et équipe Linkii / revendeur qui entrent dans l'espace), pas les membres
    o.AddPolicy("ClientAdmin", p => p.RequireAssertion(c => c.User.GetGuid(Claims.Client) != null && !c.User.IsInRole(Roles.ClientMember)));
    o.AddPolicy("PlatformAdmin", p => p.RequireAssertion(c => c.User.IsInRole(Roles.PlatformAdmin) && NoClient(c.User)));
    o.AddPolicy("ResellerSpace", p => p.RequireAssertion(c => c.User.IsInRole(Roles.ResellerAdmin) && NoClient(c.User)));
});
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
app.UseForwardedHeaders();

var dataDir = app.Configuration["Linkii:DataDir"] ?? app.Environment.ContentRootPath;
AppPaths.MediaDir = Path.Combine(dataDir, "media");
AppPaths.BrandDir = Path.Combine(dataDir, "brand");
AppPaths.ThumbDir = Path.Combine(dataDir, "thumbs");
AppPaths.CaptureDir = Path.Combine(dataDir, "captures");
Directory.CreateDirectory(AppPaths.MediaDir);
Directory.CreateDirectory(AppPaths.BrandDir);
Directory.CreateDirectory(AppPaths.ThumbDir);

var store0 = app.Services.GetRequiredService<JsonStore>();
Seed.Run(store0, app.Configuration, store0.Path, app.Logger);
Seed.PurgeRetiredApps(store0, app.Services.GetRequiredService<AppCatalog>(), store0.Path, app.Logger);
Seed.PurgeOldCalendars(store0, store0.Path, app.Logger);
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
            if (u.Role != Roles.PlatformAdmin && db.Resellers.FirstOrDefault(r => r.Id == u.ResellerId)?.Active != true) return false;
            if (cid == null) return true;
            var c = db.Clients.FirstOrDefault(x => x.Id == cid);
            if (c == null) return false;
            return u.Role == Roles.PlatformAdmin || u.Role == Roles.ResellerAdmin && c.ResellerId == u.ResellerId
                   || Roles.IsClient(u.Role) && u.ClientId == c.Id && !c.Suspended;
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
               || p.StartsWithSegments("/internal") || p.StartsWithSegments("/media")   // /media fait son propre contrôle d'accès
               || p.StartsWithSegments("/canva/callback")   // retour de Canva : identifié par son « state », sur le domaine principal
               || p.StartsWithSegments("/google/callback")  // retour de Google (Drive) : idem
               || p.StartsWithSegments("/microsoft/callback")  // retour de Microsoft : idem
               || p.StartsWithSegments("/linkedin/callback") || p.StartsWithSegments("/github/callback")  // retours de LinkedIn et GitHub (connexion seulement)
               || p.StartsWithSegments("/manifest.webmanifest");   // le navigateur le demande sans cookie de session
    if (!open && ctx.User.Identity?.IsAuthenticated != true)
    {
        ctx.Response.Redirect("/login");
        return;
    }
    await next();
});
app.UseAntiforgery();

// Manifeste de l'application installable (écran d'accueil Android / iPhone) : nom et couleur du revendeur du domaine.
app.MapGet("/manifest.webmanifest", (HttpContext ctx) =>
{
    var r = ctx.Items["reseller"] as Reseller;
    var name = r?.DisplayName is { Length: > 0 } n ? n : "Linkii";
    var color = r?.BrandColor is { Length: > 0 } c ? c : "#0B1F3A";
    ctx.Response.Headers.CacheControl = "no-cache";
    return Results.Json(new
    {
        id = "/", name, short_name = name.Length > 12 ? name[..12].TrimEnd() : name,
        description = "Pilotez vos écrans d'affichage : listes de lecture, médiathèque et intégrations.",
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

// Canva : connexion du compte Canva d'un client. Départ depuis la page Intégrations (utilisateur connecté à un espace client),
// retour de Canva sur l'adresse déclarée dans le portail développeurs (un seul domaine), puis renvoi vers le domaine d'origine.
app.MapGet("/canva/connect", (HttpContext ctx, CanvaService canva) =>
{
    var cid = ctx.User.GetGuid(Claims.Client);
    if (cid == null) return Results.Redirect("/");
    if (!canva.IsConfigured) return Results.Redirect("/integrations?canva=unconfigured");
    var back = $"{ctx.Request.Scheme}://{ctx.Request.Host}" + AccountPurposes.ReturnPage(ctx.Request);
    return Results.Redirect(canva.StartAuthorization(cid.Value, back));
});
app.MapGet("/canva/callback", async (HttpRequest req, CanvaService canva) =>
    Results.Redirect(await canva.CompleteAsync(req.Query["state"], req.Query["code"], req.Query["error"])));

// Compte Google : « Se connecter avec Google » depuis Intégrations › Google Calendar (?for=calendar), Google Drive (?for=drive)
// ou Comptes connectés (?for=all&tab=comptes : retour sur cet onglet),
// application OAuth de la plateforme, lecture seule. Un seul compte par organisation ; chaque intégration ajoute son accès.
app.MapGet("/google/connect", (HttpContext ctx, GoogleAuth google) =>
{
    var cid = ctx.User.GetGuid(Claims.Client);
    if (cid == null) return Results.Redirect("/");
    var purpose = AccountPurposes.Parse(ctx.Request.Query["for"]);
    if (!google.IsConfigured) return Results.Redirect($"/integrations?for={purpose}&google=unconfigured");
    var here = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    return Results.Redirect(google.StartAuthorization(cid.Value, purpose, here + AccountPurposes.ReturnPage(ctx.Request), here));
});
app.MapGet("/google/callback", async (HttpRequest req, GoogleAuth google, ExternalLogin sso) =>
    Results.Redirect(sso.Owns(req.Query["state"])   // « Continuer avec Google » de la page de connexion : même adresse de retour
        ? await sso.CompleteAsync(req.Query["state"], req.Query["code"], req.Query["error"], req.Query["error_description"])
        : await google.CompleteAsync(req.Query["state"], req.Query["code"], req.Query["error"])));

// Compte Microsoft : « Se connecter avec Microsoft » depuis Intégrations › Microsoft 365 (?for=calendar), OneDrive et SharePoint (?for=drive)
// ou Comptes connectés (?for=all&tab=comptes : retour sur cet onglet),
// application Entra ID de la plateforme, lecture seule. Un seul compte par organisation ; chaque intégration ajoute son accès.
app.MapGet("/microsoft/connect", (HttpContext ctx, MicrosoftAuth microsoft) =>
{
    var cid = ctx.User.GetGuid(Claims.Client);
    if (cid == null) return Results.Redirect("/");
    var purpose = AccountPurposes.Parse(ctx.Request.Query["for"]);
    if (!microsoft.IsConfigured) return Results.Redirect($"/integrations?for={purpose}&microsoft=unconfigured");
    var here = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    return Results.Redirect(microsoft.StartAuthorization(cid.Value, purpose, here + AccountPurposes.ReturnPage(ctx.Request), here));
});
app.MapGet("/microsoft/callback", async (HttpRequest req, MicrosoftAuth microsoft, ExternalLogin sso) =>
    Results.Redirect(sso.Owns(req.Query["state"])   // « Continuer avec Microsoft » de la page de connexion : même adresse de retour
        ? await sso.CompleteAsync(req.Query["state"], req.Query["code"], req.Query["error"], req.Query["error_description"])
        : await microsoft.CompleteAsync(req.Query["state"], req.Query["code"], req.Query["error"], req.Query["error_description"])));

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
        if (u.Disabled || u.ClientId != null && db.Clients.FirstOrDefault(c => c.Id == u.ClientId)?.Suspended != false) return (null, "suspended");
        return (u.Id, null);
    });
    if (userId is { } uid)
    {
        sso.Consume(t);
        return Results.Redirect(await SessionCookie.SignIn(ctx, store, uid));
    }
    // Aucun compte pour cette identité : les mêmes boutons servent à se connecter et à s'inscrire. Si l'inscription libre est ouverte,
    // on passe à la dernière étape de la création d'un espace (le ticket reste valable) ; sinon, message « aucun espace ».
    if (problem == "nolink" && host is { Active: true, AllowSelfSignup: true })
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
            store.Read(db => db.Platform.TrialDays), client.TrialEndsUtc is { } end ? Signup.Day(client, end) : null, Signup.SignInHint(user));
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

// ---------- Entrer dans l'espace d'un client (revendeur pour ses clients, équipe Linkii pour tous) : journalisé ----------
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
app.MapGet("/internal/tls-allow", (string domain, ResellerResolver resolver) =>
    resolver.IsKnownDomain(domain) ? Results.Ok() : Results.StatusCode(403));

// ---------- Médias : l'accès est contrôlé par client ----------
// Autorisé : un écran du client propriétaire (en-tête X-Token ou ?t=) ou un utilisateur connecté sur l'espace de ce client.
// Tout le reste reçoit 404, pour ne pas révéler l'existence d'un fichier d'un autre client.
var mediaTypes = new FileExtensionContentTypeProvider();
app.MapGet("/media/{file}", (HttpContext ctx, string file, JsonStore store) =>
{
    if (file != Path.GetFileName(file)) return Results.NotFound();
    var token = ctx.Request.Headers["X-Token"].ToString();
    if (string.IsNullOrEmpty(token)) token = ctx.Request.Query["t"].ToString();
    var userClient = ctx.User.Identity?.IsAuthenticated == true ? ctx.User.GetGuid(Claims.Client) : null;

    var allowed = store.Read(db =>
    {
        var m = db.Media.FirstOrDefault(x => x.FileName == file);
        if (m == null) return false;
        if (userClient == m.ClientId) return true;
        return !string.IsNullOrEmpty(token) && db.Screens.Any(s => s.Token == token && s.ClientId == m.ClientId && Tenancy.IsActive(db, s));
    });
    var path = Path.Combine(AppPaths.MediaDir, file);
    if (!allowed || !File.Exists(path)) return Results.NotFound();
    if (!mediaTypes.TryGetContentType(file, out var type)) type = "application/octet-stream";
    if (int.TryParse(ctx.Request.Query["w"], out var tw) && tw > 0 && Thumbnails.Get(file, Thumbnails.Snap(tw)) is { } thumbPath)
    {
        ctx.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(thumbPath, "image/webp");
    }
    ctx.Response.Headers.CacheControl = "private, max-age=300";
    return Results.File(path, type, enableRangeProcessing: true);
});

// ---------- API consommée par le player ----------
var api = app.MapGroup("/api");

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
    return Results.Ok(PlayerFeed.ForScreen(db, s!));
}));

// build : empreinte du code du player (wwwroot/player) ; un écran ouvert se recharge quand elle change (nouvelle version de Linkii).
var playerBuild = PlayerBuild(app.Environment.WebRootPath);
api.MapGet("/player/version", (HttpRequest req, JsonStore store) => store.Read(db =>
{
    var s = Auth(req, db);
    if (Gate(s, db) is { } denied) return denied;
    // serverNow : horloge de référence du player (lecture synchronisée) ; commands : ordres du back-office pas encore accusés (filet si SignalR est coupé)
    return Results.Ok(new { version = Notifier.Revision(db, s!), build = playerBuild, serverNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), commands = ScreenControl.Pending(s!, DateTime.UtcNow) });
}));

// Appareil : système, modèle et version de l'application, remontés au démarrage puis régulièrement.
api.MapPost("/player/device", (HttpRequest req, DeviceDto body, JsonStore store) => store.Write(db =>
{
    var s = Auth(req, db);
    if (Gate(s, db) is { } denied) return denied;
    static string? Clean(string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : (v.Trim().Length > max ? v.Trim()[..max] : v.Trim());
    s!.DeviceKind = body.Kind is "androidtv" or "android" ? body.Kind : "web";
    s.DeviceOs = Clean(body.Os, 40); s.DeviceModel = Clean(body.Model, 60); s.DeviceApp = Clean(body.App, 20);
    if (body.BootMs is > 0) s.DeviceBootUtc = DateTimeOffset.FromUnixTimeMilliseconds(body.BootMs.Value).UtcDateTime;
    return Results.Ok();
}));

// Accusé d'une commande du back-office : reçue, exécutée ou en échec.
api.MapPost("/player/command-ack", (HttpRequest req, CommandAckDto body, JsonStore store) => store.Write(db =>
{
    var s = Auth(req, db);
    if (Gate(s, db) is { } denied) return denied;
    return ScreenControl.Ack(s!, body.Id, body.Status ?? "", body.Detail) ? Results.Ok() : Results.NotFound();
}));

// Aperçu du direct : l'application Android envoie l'image (JPEG) de l'écran ; la dernière seule est gardée.
api.MapPost("/player/capture", async (HttpRequest req, Guid? cmd, JsonStore store) =>
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
        return ScreenControl.SaveCapture(db, s!, bytes, cmd, DateTime.UtcNow) ? Results.Ok() : Results.BadRequest();
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

api.MapPost("/player/ack", (HttpRequest req, AckDto body, JsonStore store) => store.Read(db =>
{
    var s = Auth(req, db);
    if (Gate(s, db) is { } denied) return denied;
    s!.AppliedRevision = body.Version;
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
        var pub = PlayerFeed.AllItems(db, s!).FirstOrDefault(x => x.Id == id && x.AppId != null);   // toutes les zones de l'écran
        return ((IResult?)null, db.Clients.First(c => c.Id == s!.ClientId), pub);
    });
    if (denied != null) return denied;
    if (published == null) return Results.NotFound();
    return await AppData(published, tenant, catalog, providers, box);
});

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
    var s = g.WallZoneId is { } zid   // mur : le premier écran placé donne la publication et la grille
        ? db.Screens.Where(x => x.ClientId == g.ClientId && x.ZoneId == zid && x.Token != null && x.WallPos != null).OrderBy(x => x.WallPos).FirstOrDefault()
        : db.Screens.FirstOrDefault(x => x.Id == g.ScreenId && x.ClientId == g.ClientId && x.Token != null);
    if (s == null) return null;
    var feed = PlayerFeed.ForScreen(db, s, whole: g.WallZoneId != null);
    // mur : format du jeton (réglages du mur essayés dans le simulateur, pas forcément encore appliqués)
    feed["screen"] = g.WallZoneId != null ? new { orientation = g.Orientation, resolution = g.Resolution } : new { orientation = s.Orientation, resolution = PlayerFeed.Resolution(s) };
    return (feed, PlayerFeed.AllItems(db, s));
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

api.MapGet("/preview/data/{id:guid}", async (HttpRequest req, Guid id, JsonStore store, PreviewGrants grants, Notifier notifier, AppCatalog catalog, AppProviders providers, SecretBox box) =>
{
    if (grants.Find(req.Headers["X-Preview"]) is not { } g) return Results.Unauthorized();
    var (tenant, published) = store.Read(db => (db.Clients.FirstOrDefault(c => c.Id == g.ClientId),
        PreviewFeed(db, g, notifier)?.Items.FirstOrDefault(x => x.Id == id && x.AppId != null)));
    if (tenant == null || published == null) return Results.NotFound();
    return await AppData(published, tenant, catalog, providers, box);
});

// Dernier aperçu du direct d'un écran, pour les personnes qui ont accès à l'écran (jamais mis en cache).
app.MapGet("/screens/{id:guid}/capture.jpg", (HttpContext ctx, Guid id, JsonStore store) =>
{
    var cid = ctx.User.GetGuid(Claims.Client);
    var uid = ctx.User.GetGuid(ClaimTypes.NameIdentifier);
    if (cid == null) return Results.Unauthorized();
    var ok = store.Read(db => new ClientDb(db, cid.Value, AreaAccess.For(db, cid.Value, uid)).Screens.Any(s => s.Id == id && s.CaptureUtc != null));
    var path = ScreenControl.CapturePath(id);
    if (!ok || !File.Exists(path)) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.File(path, "image/jpeg");
}).RequireAuthorization("ClientSpace");

app.MapHub<ScreenHub>("/hubs/screen");

// ---------- Back-office Blazor ----------
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

record AckDto(string Version, int? W, int? H);
record PlayDto(Guid? MediaId, string? AppId, long Start, int Sec);
record StartDto(int? W, int? H);
record DeviceDto(string? Kind, string? Os, string? Model, string? App, long? BootMs);
record CommandAckDto(Guid Id, string? Status, string? Detail);

public partial class Program;
