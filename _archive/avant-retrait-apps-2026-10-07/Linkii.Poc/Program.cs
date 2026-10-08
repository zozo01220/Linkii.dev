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
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<TenantStore>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<VideoConverter>();
builder.Services.AddSingleton<MediaImporter>();
builder.Services.AddSingleton<WeatherService>();
builder.Services.AddSingleton<GraphService>();
builder.Services.AddSingleton<ICalendarConnector, M365Connector>();
builder.Services.AddSingleton<ICalendarConnector, IcsConnector>();
builder.Services.AddSingleton<ICalendarConnector, GoogleConnector>();
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
builder.Services.AddSingleton<IAppProvider, WeatherForecastProvider>();
builder.Services.AddSingleton<IAppProvider, CalendarProvider>();
builder.Services.AddSingleton<IAppProvider, RoomProvider>();
builder.Services.AddSingleton<IAppProvider, RssProvider>();
builder.Services.AddSingleton<IAppProvider, WebPageProvider>();
builder.Services.AddSingleton<IAppProvider, YouTubeProvider>();
builder.Services.AddSingleton<LinkedinService>();
builder.Services.AddSingleton<IAppProvider, LinkedinProvider>();
builder.Services.AddSingleton<IAppProvider, MenuCsvProvider>();
builder.Services.AddSingleton<IAppProvider, MenuApiProvider>();
builder.Services.AddSingleton<AppProviders>();
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
Directory.CreateDirectory(AppPaths.MediaDir);
Directory.CreateDirectory(AppPaths.BrandDir);

var store0 = app.Services.GetRequiredService<JsonStore>();
Seed.Run(store0, app.Configuration, store0.Path, app.Logger);

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
        var ok = store.Read(db =>
        {
            var u = db.Users.FirstOrDefault(x => x.Id == uid && !x.Disabled);
            if (u == null || !Tenancy.HostAllows(u, host)) return false;
            if (u.Role != Roles.PlatformAdmin && db.Resellers.FirstOrDefault(r => r.Id == u.ResellerId)?.Active != true) return false;
            if (cid == null) return true;
            var c = db.Clients.FirstOrDefault(x => x.Id == cid);
            if (c == null) return false;
            return u.Role == Roles.PlatformAdmin || u.Role == Roles.ResellerAdmin && c.ResellerId == u.ResellerId
                   || u.Role == Roles.ClientAdmin && u.ClientId == c.Id && !c.Suspended;
        });
        if (!ok)
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            ctx.Response.Redirect("/login");
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
        description = "Pilotez vos écrans d'affichage : playlists, médiathèque et apps.",
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

/// <summary>401 si le jeton est inconnu, 403 si le client ou le revendeur de l'écran est suspendu.</summary>
static IResult? Gate(Screen? s, Db db) =>
    s == null ? Results.Unauthorized() : !Tenancy.IsActive(db, s) ? Results.StatusCode(403) : null;

api.MapGet("/player/playlist", (HttpRequest req, JsonStore store) => store.Read(db =>
{
    var s = Auth(req, db);
    if (Gate(s, db) is { } denied) return denied;
    var t = db.Clients.First(c => c.Id == s!.ClientId);
    var reseller = db.Resellers.FirstOrDefault(r => r.Id == t.ResellerId);
    var p = db.Playlists.FirstOrDefault(x => x.Id == s!.PlaylistId && x.ClientId == s.ClientId);
    return Results.Ok(new
    {
        version = Helpers.Revision(p, s!, t, reseller),
        items = (s.PublishedVersion > 0 ? s.Published : p?.Published ?? new List<PublishedItem>()).Select(i => i.ForPlayer()),   // repli : écran jamais publié depuis le passage à la publication par écran
        screen = new { orientation = s!.Orientation, resolution = s.Resolution },
        settings = new { timezone = t.Timezone, reloadHour = t.ReloadHour },
        brand = BrandDto.From(reseller)
    });
}));

api.MapGet("/player/version", (HttpRequest req, JsonStore store) => store.Read(db =>
{
    var s = Auth(req, db);
    if (Gate(s, db) is { } denied) return denied;
    return Results.Ok(new { version = Notifier.Revision(db, s!) });
}));

api.MapPost("/player/ack", (HttpRequest req, AckDto body, JsonStore store) => store.Read(db =>
{
    var s = Auth(req, db);
    if (Gate(s, db) is { } denied) return denied;
    s!.AppliedRevision = body.Version;
    if (body.W > 0 && body.H > 0) { s.DetectedW = body.W; s.DetectedH = body.H; }
    return Results.Ok();
}));

// Données des apps : l'URL des sources et les secrets restent côté serveur.
// Un écran ne lit que les données de son propre client, d'après l'instantané publié dans la playlist de l'écran
// (les modifications de paramètres non publiées n'ont aucun effet).
api.MapGet("/data/{id:guid}", async (HttpRequest req, Guid id, JsonStore store, AppCatalog catalog, AppProviders providers, SecretBox box) =>
{
    var (denied, tenant, published) = store.Read<(IResult?, Tenant, PublishedItem?)>(db =>
    {
        var s = Auth(req, db);
        var gate = Gate(s, db);
        if (gate != null) return (gate, null!, null);
        var list = s!.PublishedVersion > 0 ? s.Published : db.Playlists.FirstOrDefault(p => p.Id == s.PlaylistId && p.ClientId == s.ClientId)?.Published;
        var pub = list?.FirstOrDefault(x => x.Id == id && x.AppId != null);
        return ((IResult?)null, db.Clients.First(c => c.Id == s!.ClientId), pub);
    });
    if (denied != null) return denied;
    if (published == null) return Results.NotFound();

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
});

// ---------- LinkedIn : connexion d'un administrateur de page (OAuth, code d'autorisation) ----------
// « state » = identifiant de l'instance + client + date, chiffré par le serveur : le retour n'est accepté que pour la même session, le même client, dans les 15 minutes.
app.MapGet("/linkedin/connect", (HttpContext ctx, Guid instance, JsonStore store, LinkedinService li, SecretBox box) =>
{
    var cid = ctx.User.GetGuid(Claims.Client);
    if (cid == null) return Results.Redirect("/media");
    var inst = store.Read(db => db.Media.FirstOrDefault(m => m.Id == instance && m.ClientId == cid && m.AppId == "linkedin") is { } m ? (m.Settings, m.Secrets) : default);
    if (inst.Settings == null) return Results.NotFound();
    var app = li.AppFor(inst.Settings, inst.Secrets, box);
    if (app == null) return Results.Redirect("/apps/instance/" + instance + "?linkedin=error&detail=" + Uri.EscapeDataString("Saisissez d'abord l'identifiant et la clé de votre application LinkedIn, puis enregistrez."));
    var state = box.Protect(System.Text.Json.JsonSerializer.Serialize(new { i = instance, c = cid, t = DateTime.UtcNow }));
    return Results.Redirect(li.AuthUrl(state, li.RedirectFor(ctx.Request), app));
}).RequireAuthorization("ClientSpace");

app.MapGet("/linkedin/callback", async (HttpContext ctx, string? code, string? state, string? error, string? error_description, JsonStore store, LinkedinService li, SecretBox box) =>
{
    var cid = ctx.User.GetGuid(Claims.Client);
    Guid instance;
    try
    {
        using var st = System.Text.Json.JsonDocument.Parse(box.Unprotect(state ?? ""));
        instance = st.RootElement.GetProperty("i").GetGuid();
        if (st.RootElement.GetProperty("c").GetGuid() != cid || DateTime.UtcNow - st.RootElement.GetProperty("t").GetDateTime().ToUniversalTime() > TimeSpan.FromMinutes(15))
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
    }
    catch { return Results.StatusCode(StatusCodes.Status401Unauthorized); }

    string Back(string flag, string? detail = null) => "/apps/instance/" + instance + "?linkedin=" + flag + (detail == null ? "" : "&detail=" + Uri.EscapeDataString(detail));
    if (!string.IsNullOrEmpty(error)) return Results.Redirect(Back("error", error == "user_cancelled_authorize" || error == "user_cancelled_login" ? "Connexion annulée." : error_description ?? error));
    if (string.IsNullOrEmpty(code)) return Results.Redirect(Back("error", "Réponse de LinkedIn incomplète."));
    try
    {
        var inst = store.Read(db => db.Media.FirstOrDefault(m => m.Id == instance && m.ClientId == cid && m.AppId == "linkedin") is { } m ? (m.Settings, m.Secrets) : default);
        var app = inst.Settings == null ? null : li.AppFor(inst.Settings, inst.Secrets, box);
        if (app == null) return Results.Redirect(Back("error", "Identifiant ou clé de l'application LinkedIn manquants."));
        var token = await li.Exchange(code, li.RedirectFor(ctx.Request), app);
        var ok = LinkedinService.StoreAuth(store, cid!.Value, instance, box.Protect(LinkedinService.AuthJson(token)));
        return Results.Redirect(ok ? Back("ok") : Back("error", "Contenu introuvable."));
    }
    catch (Exception ex) { return Results.Redirect(Back("error", ex.Message)); }
}).RequireAuthorization("ClientSpace");

app.MapHub<ScreenHub>("/hubs/screen");

// ---------- Back-office Blazor ----------
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

record AckDto(string Version, int? W, int? H);
record StartDto(int? W, int? H);

public partial class Program;
