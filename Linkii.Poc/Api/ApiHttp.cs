using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Linkii.Poc;

/// <summary>Codes d'erreur stables de l'API (champ <c>code</c> de la réponse), lisibles par un programme. Le message est en français.</summary>
public static class ApiCodes
{
    public const string MissingKey = "missing_key", InvalidKey = "invalid_key", KeyRevoked = "key_revoked", KeyExpired = "key_expired", IpNotAllowed = "ip_not_allowed",
        IntegrationMissing = "integration_missing", TooManyFailures = "too_many_failures", ScopeMissing = "scope_missing", PlanRequired = "plan_required", SpaceSuspended = "space_suspended", RateLimited = "rate_limited",
        ScreenUnavailable = "screen_unavailable", IdempotencyConflict = "idempotency_conflict", RequestInProgress = "request_in_progress", NotFound = "not_found", InvalidRequest = "invalid_request", LimitReached = "limit_reached";

    public static readonly (string Code, int Status, string Title)[] All =
    {
        (MissingKey, 401, "Clé d'API absente. Envoyez l'en-tête « Authorization: Bearer lkv_… »."),
        (InvalidKey, 401, "Clé d'API inconnue ou mal formée."),
        (KeyRevoked, 401, "Cette clé a été révoquée."),
        (KeyExpired, 401, "Cette clé a expiré."),
        (IpNotAllowed, 403, "Cette clé n'est pas autorisée depuis cette adresse IP."),
        (TooManyFailures, 429, "Trop d'essais avec une clé invalide. Réessayez dans une minute."),
        (IntegrationMissing, 403, "Cette fonction vient d'une intégration qui n'est pas installée : installez-la depuis le Store."),
        (ScopeMissing, 403, "Cette clé n'a pas l'accès nécessaire."),
        (PlanRequired, 403, "L'API fait partie du plan Pro."),
        (SpaceSuspended, 403, "L'espace de cette organisation est suspendu."),
        (RateLimited, 429, "Trop de requêtes. Réessayez après le délai indiqué (en-tête Retry-After)."),
        (IdempotencyConflict, 422, "Cette Idempotency-Key a déjà servi pour une requête différente."),
        (RequestInProgress, 409, "Une requête avec la même Idempotency-Key est en cours."),
        (ScreenUnavailable, 409, "L'écran ne peut pas recevoir la commande : hors ligne, appareil non compatible, commande déjà en cours ou droit manquant (le détail l'explique)."),
        (NotFound, 404, "Ressource introuvable."),
        (InvalidRequest, 422, "Requête invalide."),
        (LimitReached, 422, "Limite de l'organisation atteinte."),
    };
}

/// <summary>Réponse d'erreur unique : <c>application/problem+json</c> avec un code stable et l'identifiant de la requête.</summary>
public static class ApiProblem
{
    public static (int Status, string Title) Info(string code)
    {
        var e = ApiCodes.All.FirstOrDefault(x => x.Code == code);
        return e.Code == null ? (500, "Erreur inattendue.") : (e.Status, e.Title);
    }

    public static string Body(HttpContext ctx, string code, string? detail, int? status = null)
    {
        var (s, title) = Info(code);
        var o = new Dictionary<string, object?>
        {
            ["type"] = $"{ctx.Request.Scheme}://{ctx.Request.Host}/docs/api#{code}", ["code"] = code, ["status"] = status ?? s, ["title"] = title,
            ["detail"] = detail ?? title, ["requestId"] = ctx.TraceIdentifier
        };
        return JsonSerializer.Serialize(o, ApiJson.Options);
    }

    public static Task Write(HttpContext ctx, string code, string? detail = null, int? status = null)
    {
        ctx.Response.StatusCode = status ?? Info(code).Status;
        ctx.Response.ContentType = "application/problem+json; charset=utf-8";
        return ctx.Response.WriteAsync(Body(ctx, code, detail, status));
    }

    public static IResult Result(HttpContext ctx, string code, string? detail = null) =>
        Results.Text(Body(ctx, code, detail), "application/problem+json; charset=utf-8", Encoding.UTF8, Info(code).Status);
}

public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
}

public static class ApiAuth
{
    public const string Scheme = "ApiKey";
    public const string Client = "lkv:client", Key = "lkv:key", Owner = "lkv:owner", KeyName = "lkv:name", Scope = "lkv:scope";
    public const string ItemError = "lkv.error", ItemKey = "lkv.key";

    public static Guid ClientId(this ClaimsPrincipal u) => Guid.TryParse(u.FindFirstValue(Client), out var g) ? g : Guid.Empty;
    public static Guid KeyId(this ClaimsPrincipal u) => Guid.TryParse(u.FindFirstValue(Key), out var g) ? g : Guid.Empty;
    public static Guid? OwnerId(this ClaimsPrincipal u) => Guid.TryParse(u.FindFirstValue(Owner), out var g) ? g : null;
}

/// <summary>Échecs d'authentification par adresse : au-delà de 20 par minute, l'adresse est refusée (anti-devinette).</summary>
public class ApiFailureThrottle(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (int Count, DateTime Since)> fails = new();
    public const int Limit = 20;

    public bool Blocked(IPAddress? ip)
    {
        if (ip == null || !fails.TryGetValue(ip.ToString(), out var f)) return false;
        return time.GetUtcNow().UtcDateTime - f.Since < TimeSpan.FromMinutes(1) && f.Count >= Limit;
    }

    public void Failed(IPAddress? ip)
    {
        if (ip == null) return;
        var now = time.GetUtcNow().UtcDateTime;
        fails.AddOrUpdate(ip.ToString(), _ => (1, now), (_, f) => now - f.Since >= TimeSpan.FromMinutes(1) ? (1, now) : (f.Count + 1, f.Since));
        if (fails.Count > 10_000) foreach (var k in fails.Where(p => now - p.Value.Since > TimeSpan.FromMinutes(1)).Select(p => p.Key).ToList()) fails.TryRemove(k, out _);
    }
}

/// <summary>Schéma d'authentification des routes /api/v1 : « Authorization: Bearer lkv_… ». Le cookie n'est jamais accepté ici, ni la clé ailleurs.</summary>
public class ApiKeyAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKeyService keys, ApiFailureThrottle throttle)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        var ip = Context.Connection.RemoteIpAddress;
        if (string.IsNullOrEmpty(header)) { Context.Items[ApiAuth.ItemError] = ApiCodes.MissingKey; return Task.FromResult(AuthenticateResult.NoResult()); }
        if (throttle.Blocked(ip)) { Context.Items[ApiAuth.ItemError] = ApiCodes.TooManyFailures; return Task.FromResult(AuthenticateResult.Fail(ApiCodes.TooManyFailures)); }
        var r = keys.Authenticate(header, ip, Context.Items["reseller"] as Reseller);
        if (r.Identity is not { } id)
        {
            throttle.Failed(ip);
            Context.Items[ApiAuth.ItemError] = r.Code;
            return Task.FromResult(AuthenticateResult.Fail(r.Code ?? ApiCodes.InvalidKey));
        }
        var claims = new List<Claim>
        {
            new(ApiAuth.Client, id.ClientId.ToString()), new(ApiAuth.Key, id.KeyId.ToString()), new(ApiAuth.KeyName, id.KeyName),
        };
        if (id.OwnerId is { } o) claims.Add(new(ApiAuth.Owner, o.ToString()));
        claims.AddRange(id.Scopes.Select(s => new Claim(ApiAuth.Scope, s)));
        Context.Items[ApiAuth.ItemKey] = id.KeyId;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiAuth.Scheme));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, ApiAuth.Scheme)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var code = Context.Items[ApiAuth.ItemError] as string ?? ApiCodes.MissingKey;
        Response.Headers.WWWAuthenticate = "Bearer";
        return ApiProblem.Write(Context, code);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => ApiProblem.Write(Context, ApiCodes.ScopeMissing);
}

/// <summary>Contrôles communs d'une route : accès de la clé, licence Pro, espace actif. La clé et le débit sont contrôlés plus tôt.</summary>
public class ApiGuard(string scope) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var user = http.User;
        if (!user.HasClaim(ApiAuth.Scope, scope)) return ApiProblem.Result(http, ApiCodes.ScopeMissing, $"Cette clé n'a pas l'accès « {scope} ».");
        var cid = user.ClientId();
        var code = http.RequestServices.GetRequiredService<JsonStore>().Read(db =>
        {
            var t = db.Clients.FirstOrDefault(x => x.Id == cid);
            if (t == null) return ApiCodes.InvalidKey;
            if (t.IsCut || db.Resellers.FirstOrDefault(r => r.Id == t.ResellerId) is not { Active: true, Suspended: false }) return ApiCodes.SpaceSuspended;
            if (!ApiLimits.Allowed(t)) return ApiCodes.PlanRequired;
            // accès d'une intégration du Store (ex. « Alerte d'urgence ») : elle doit être installée
            return http.RequestServices.GetRequiredService<ApiRegistry>().Scope(scope)?.App is { } app && !db.AppInstalls.Any(a => a.ClientId == cid && a.AppId == app) ? ApiCodes.IntegrationMissing : null;
        });
        if (code != null) return ApiProblem.Result(http, code);
        return await next(context);
    }
}

/// <summary>
/// Mémoire des requêtes à « Idempotency-Key » (24 h) : un POST rejoué avec la même clé rend la même réponse sans rien refaire.
/// Même clé et autre corps : 422 ; même clé encore en cours : 409. Fichier <c>api/idempotency.jsonl</c> (hors data.json), reprise au redémarrage.
/// </summary>
public class IdempotencyStore
{
    public sealed record Entry(string BodyHash, int Status, string? ContentType, string? Body, DateTime CreatedUtc, bool Done);
    public enum Begin { Started, Replay, Conflict, InProgress }
    public static readonly TimeSpan Keep = TimeSpan.FromHours(24);
    public const int MaxStoredBody = 64 * 1024;

    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private readonly string file;
    private readonly TimeProvider time;
    private readonly object io = new();

    public IdempotencyStore(IConfiguration config, IWebHostEnvironment env, TimeProvider time) : this(ApiData.Dir(config, env), time) { }

    public IdempotencyStore(string directory, TimeProvider time)
    {
        this.time = time;
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "idempotency.jsonl");
        try
        {
            if (!File.Exists(file)) return;
            var since = time.GetUtcNow().UtcDateTime - Keep;
            foreach (var line in File.ReadLines(file))
            {
                var r = JsonSerializer.Deserialize<Row>(line);
                if (r != null && r.T > since) entries[r.K] = new Entry(r.H, r.S, r.C, r.B, r.T, true);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
    }

    private record Row(string K, string H, int S, string? C, string? B, DateTime T);

    public Begin TryBegin(string key, string bodyHash, out Entry? existing)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var fresh = new Entry(bodyHash, 0, null, null, now, false);
        while (true)
        {
            if (entries.TryAdd(key, fresh)) { existing = null; return Begin.Started; }
            if (!entries.TryGetValue(key, out var e)) continue;
            if (now - e.CreatedUtc > Keep) { entries.TryRemove(new KeyValuePair<string, Entry>(key, e)); continue; }
            existing = e;
            return e.BodyHash != bodyHash ? Begin.Conflict : e.Done ? Begin.Replay : Begin.InProgress;
        }
    }

    public void Complete(string key, string bodyHash, int status, string? contentType, string body)
    {
        var e = new Entry(bodyHash, status, contentType, body, time.GetUtcNow().UtcDateTime, true);
        entries[key] = e;
        lock (io)
        {
            try { File.AppendAllText(file, JsonSerializer.Serialize(new Row(key, bodyHash, status, contentType, body, e.CreatedUtc)) + "\n"); } catch (IOException) { }
        }
    }

    public void Abort(string key) => entries.TryRemove(key, out _);

    public void Prune()
    {
        var since = time.GetUtcNow().UtcDateTime - Keep;
        foreach (var p in entries.Where(p => p.Value.CreatedUtc < since).ToList()) entries.TryRemove(p.Key, out _);
        lock (io)
        {
            try
            {
                var rows = entries.Where(p => p.Value.Done).Select(p => JsonSerializer.Serialize(new Row(p.Key, p.Value.BodyHash, p.Value.Status, p.Value.ContentType, p.Value.Body, p.Value.CreatedUtc)));
                File.WriteAllLines(file, rows);
            }
            catch (IOException) { }
        }
    }
}

public static class ApiPipeline
{
    /// <summary>Identifiant de requête, usage par clé, idempotence : pour tout ce qui passe par /api/v1.</summary>
    public static void UseApiV1(this WebApplication app)
    {
        app.UseWhen(c => c.Request.Path.StartsWithSegments("/api/v1"), branch =>
        {
            branch.Use(async (ctx, next) =>
            {
                ctx.Response.Headers["X-Request-Id"] = ctx.TraceIdentifier;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                await next();
                if (ctx.Items[ApiAuth.ItemKey] is Guid keyId)
                    ctx.RequestServices.GetRequiredService<ApiUsage>().Record(keyId, ctx.Response.StatusCode >= 400);
                // historique (diagnostic) : erreurs serveur, accès refusés et limites atteintes d'une clé d'API
                var code = ctx.Response.StatusCode;
                if ((code >= 500 || code is 401 or 403 or 429) && Guid.TryParse(ctx.User.FindFirst(ApiAuth.Client)?.Value, out var apiClient)
                    && ctx.RequestServices.GetService<HistoryLog>() is { } hist)
                {
                    var keyName = ctx.User.FindFirst(ApiAuth.KeyName)?.Value;
                    hist.Record(apiClient, History.Diag, History.Integrations, $"API : {ctx.Request.Method} {ctx.Request.Path} → {code}", code >= 500 ? Notifs.Error : Notifs.Warn,
                        keyName == null ? null : $"Clé « {keyName} »", "API", targetKind: "apikey", targetName: keyName, path: "apiwebhooks", tech: true,
                        facts: new() { new[] { "Requête", $"{ctx.Request.Method} {ctx.Request.Path}" }, new[] { "Code", code.ToString() }, new[] { "Durée", $"{timer.ElapsedMilliseconds} ms" }, new[] { "Identifiant", ctx.TraceIdentifier } });
                }
            });
            branch.Use(Idempotency);
        });
    }

    private static async Task Idempotency(HttpContext ctx, Func<Task> next)
    {
        var idem = ctx.Request.Headers["Idempotency-Key"].ToString();
        if (!HttpMethods.IsPost(ctx.Request.Method) || idem.Length == 0) { await next(); return; }
        if (idem.Length > 100) { await ApiProblem.Write(ctx, ApiCodes.InvalidRequest, "Idempotency-Key : 100 caractères au plus."); return; }
        var peek = ctx.RequestServices.GetRequiredService<ApiKeyService>().Peek(ctx.Request.Headers.Authorization.ToString());
        if (peek is not { } who) { await next(); return; }   // clé inconnue : la chaîne normale répond 401

        ctx.Request.EnableBuffering();
        byte[] body;
        using (var ms = new MemoryStream())
        {
            await ctx.Request.Body.CopyToAsync(ms);
            body = ms.ToArray();
        }
        ctx.Request.Body.Position = 0;
        if (body.Length > 1_000_000) { await next(); return; }
        var hash = Convert.ToHexString(SHA256.HashData(body));
        var key = $"{who.Key:N}|{ctx.Request.Path}|{idem}";
        var store = ctx.RequestServices.GetRequiredService<IdempotencyStore>();
        switch (store.TryBegin(key, hash, out var existing))
        {
            case IdempotencyStore.Begin.Conflict: await ApiProblem.Write(ctx, ApiCodes.IdempotencyConflict); return;
            case IdempotencyStore.Begin.InProgress: await ApiProblem.Write(ctx, ApiCodes.RequestInProgress); return;
            case IdempotencyStore.Begin.Replay:
                ctx.Response.StatusCode = existing!.Status;
                ctx.Response.Headers["Idempotency-Replayed"] = "true";
                if (existing.ContentType != null) ctx.Response.ContentType = existing.ContentType;
                await ctx.Response.WriteAsync(existing.Body ?? "");
                return;
        }

        var original = ctx.Response.Body;
        using var capture = new MemoryStream();
        ctx.Response.Body = capture;
        try { await next(); }
        catch { store.Abort(key); ctx.Response.Body = original; throw; }
        ctx.Response.Body = original;
        var status = ctx.Response.StatusCode;
        var text = Encoding.UTF8.GetString(capture.ToArray());
        if (status >= 500 || status == 429 || capture.Length > IdempotencyStore.MaxStoredBody) store.Abort(key);
        else store.Complete(key, hash, status, ctx.Response.ContentType, text);
        capture.Position = 0;
        await capture.CopyToAsync(original);
    }
}

public static class ApiRegistration
{
    /// <summary>Services du socle et module « noyau ». Les modules des fonctions futures s'ajoutent par <c>AddSingleton&lt;IApiModule, …&gt;</c>.</summary>
    public static IServiceCollection AddLinkiiApi(this IServiceCollection s)
    {
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton<IApiModule, CoreApiModule>();
        s.AddSingleton<IApiModule, ScreensApiModule>();   // supervision : GET /screens, screen.offline, screen.online
        s.AddSingleton<IApiModule, EmergencyApiModule>();   // urgence : POST/GET/DELETE /emergency (intégration « Alerte d'urgence »)
        s.AddSingleton<IApiModule, DataApiModule>();   // envoi direct de données : /datasets, data.failed, data.recovered
        s.AddSingleton<ApiRegistry>();
        s.AddSingleton<ApiKeyService>();
        s.AddSingleton<ApiUsage>();
        s.AddSingleton<ApiFailureThrottle>();
        s.AddSingleton<IdempotencyStore>();
        s.AddSingleton<EventBus>();
        s.AddSingleton<IEventPublisher>(sp => sp.GetService<HistoryLog>() is { } h ? new HistoryEvents(sp.GetRequiredService<EventBus>(), h) : sp.GetRequiredService<EventBus>());   // l'historique écoute aussi le bus
        s.AddSingleton<WebhookJournal>();
        s.AddSingleton<WebhookSender>();
        s.AddSingleton<WebhookService>();
        s.AddSingleton<WebhookDispatcher>();
        s.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
        s.AddHostedService<ApiMaintenanceWorker>();
        s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>(ApiAuth.Scheme, _ => { });
        s.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            foreach (var cls in RateClass.All)
                o.AddPolicy("api-" + cls, ctx =>
                {
                    var keys = ctx.RequestServices.GetRequiredService<ApiKeyService>();
                    var store = ctx.RequestServices.GetRequiredService<JsonStore>();
                    if (keys.Peek(ctx.Request.Headers.Authorization.ToString()) is { } who)
                    {
                        var permits = store.Read(db => db.Clients.FirstOrDefault(c => c.Id == who.Client) is { } t ? ApiLimits.Permits(t, cls) : 1);
                        return RateLimitPartition.GetFixedWindowLimiter($"k:{who.Key:N}:{cls}", _ => new FixedWindowRateLimiterOptions
                            { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });
                    }
                    // clé absente ou inconnue : limite basse par adresse (la réponse 401 vient ensuite)
                    return RateLimitPartition.GetFixedWindowLimiter("ip:" + ctx.Connection.RemoteIpAddress, _ => new FixedWindowRateLimiterOptions
                        { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });
                });
            o.OnRejected = async (c, ct) =>
            {
                if (c.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after)) c.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(after.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                else c.HttpContext.Response.Headers.RetryAfter = "60";
                await ApiProblem.Write(c.HttpContext, ApiCodes.RateLimited);
            };
        });
        return s;
    }

    public static void MapLinkiiApi(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<ApiRegistry>();
        var group = app.MapGroup("/api/v1");
        var router = new ApiRouter(group, registry);
        foreach (var m in registry.Modules) m.Map(router);
        // Route inconnue sous /api/v1 : même format d'erreur que le reste
        group.MapFallback(ctx => ApiProblem.Write(ctx, ApiCodes.NotFound, "Cette route n'existe pas."));
        app.MapGet("/api/v1/openapi.json", (HttpContext ctx) => Results.Text(ApiDocs.OpenApi(registry, $"{ctx.Request.Scheme}://{ctx.Request.Host}").ToJsonString(ApiJson.Options), "application/json")).AllowAnonymous();
        app.MapGet("/docs/api", (HttpContext ctx) => Results.Content(ApiDocs.Html(registry, ctx.Items["reseller"] as Reseller, $"{ctx.Request.Scheme}://{ctx.Request.Host}"), "text/html; charset=utf-8")).AllowAnonymous();
        app.MapGet("/docs/api/try", (HttpContext ctx) => Results.Content(ApiDocs.SwaggerHtml(ctx.Items["reseller"] as Reseller), "text/html; charset=utf-8")).AllowAnonymous();
    }
}
