using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Linkii.Poc;

/// <summary>
/// Module « noyau » (§154) : la seule fonction livrée avec le socle. <c>GET /me</c>, la gestion de ses propres webhooks et l'événement <c>ping</c>
/// servent de modèle aux modules suivants (écrans, urgence, données, médias…).
/// </summary>
public class CoreApiModule : IApiModule
{
    public const string AccountRead = "account:read", WebhooksManage = "webhooks:manage";
    public string Id => "core";

    public IEnumerable<ApiScope> Scopes { get; } = new[]
    {
        new ApiScope(AccountRead, "Compte", "Lire les informations de la clé et de l'organisation"),
        new ApiScope(WebhooksManage, "Compte", "Gérer ses adresses de webhook"),
    };

    public IEnumerable<ApiTemplate> Templates { get; } = new[]
    {
        new ApiTemplate("Lecture seule", "Consulter les informations du compte", new[] { AccountRead }, 10),
        new ApiTemplate("Gestion des webhooks", "Gérer ses adresses de webhook", new[] { AccountRead, WebhooksManage }, 90),
    };

    public IEnumerable<EventType> Events { get; } = new[]
    {
        new EventType("ping", "Système", "Événement d'essai envoyé par le bouton « Envoyer un test »"),
    };

    public static readonly ApiRoute Me = new("GET", "/me", AccountRead, RateClass.Read, "Informations sur la clé et l'organisation");
    public static readonly ApiRoute WebhookList = new("GET", "/webhooks", WebhooksManage, RateClass.Read, "Lister les adresses de webhook");
    public static readonly ApiRoute WebhookCreate = new("POST", "/webhooks", WebhooksManage, RateClass.Write, "Ajouter une adresse de webhook (le secret n'est rendu qu'une fois)",
        Body: "{ \"name\": \"Supervision Teams\", \"url\": \"https://hooks.exemple.ch/linkivi\", \"events\": [\"screen.offline\", \"screen.online\"] }");
    public static readonly ApiRoute WebhookGet = new("GET", "/webhooks/{id}", WebhooksManage, RateClass.Read, "Lire une adresse de webhook");
    public static readonly ApiRoute WebhookUpdate = new("PATCH", "/webhooks/{id}", WebhooksManage, RateClass.Write, "Modifier une adresse (nom, adresse, événements, actif)",
        Body: "{ \"name\": \"Nouveau nom\", \"events\": [\"screen.*\"], \"active\": true }");
    public static readonly ApiRoute WebhookDelete = new("DELETE", "/webhooks/{id}", WebhooksManage, RateClass.Write, "Supprimer une adresse de webhook");
    public static readonly ApiRoute WebhookRotate = new("POST", "/webhooks/{id}/rotate-secret", WebhooksManage, RateClass.Write, "Renouveler le secret (l'ancien reste signé 24 h)");
    public static readonly ApiRoute WebhookTest = new("POST", "/webhooks/{id}/test", WebhooksManage, RateClass.Write, "Envoyer un événement « ping » d'essai");
    public static readonly ApiRoute WebhookDeliveries = new("GET", "/webhooks/{id}/deliveries", WebhooksManage, RateClass.Read, "Journal des livraisons d'une adresse (pagination par curseur)",
        Query: "limit:integer:1 à 100 (50 par défaut); cursor:string:valeur « next » de la page précédente; status:string:« problems » pour les échecs et attentes seulement");

    public IEnumerable<ApiRoute> Routes { get; } = new[] { Me, WebhookList, WebhookCreate, WebhookGet, WebhookUpdate, WebhookDelete, WebhookRotate, WebhookTest, WebhookDeliveries };

    public record WebhookBody(string? Name, string? Url, List<string>? Events, bool? Active);

    private static object Dto(WebhookEndpoint e) => new
    {
        id = e.Id, name = e.Name, url = e.Url, events = e.Events, active = e.Active, disabledReason = e.Active ? null : e.DisabledReason,
        apiVersion = e.ApiVersion, createdAt = e.CreatedUtc
    };

    private static object DeliveryDto(Delivery d) => new
    {
        id = d.Id, eventId = d.EventId, type = d.EventType, endpointId = d.EndpointId, status = d.Status, test = d.Test, queuedAt = d.QueuedUtc,
        nextAttemptAt = d.NextUtc, attempts = d.Attempts.Count, lastHttpStatus = d.Attempts.LastOrDefault()?.Status, lastError = d.Attempts.LastOrDefault()?.Error, note = d.Note
    };

    private static IResult Invalid(HttpContext ctx, string error) =>
        ApiProblem.Result(ctx, error.StartsWith("Limite atteinte", StringComparison.Ordinal) ? ApiCodes.LimitReached : ApiCodes.InvalidRequest, error);

    public void Map(ApiRouter r)
    {
        r.Map(Me, (HttpContext ctx, JsonStore store) =>
        {
            var u = ctx.User;
            return store.Read(db =>
            {
                var t = db.Clients.First(c => c.Id == u.ClientId());
                var k = t.ApiKeys.First(x => x.Id == u.KeyId());
                return Results.Json(new
                {
                    apiVersion = ApiRegistry.Version,
                    key = new { id = k.Id, name = k.Name, prefix = k.Prefix, template = k.Template, scopes = k.Scopes, createdAt = k.CreatedUtc, expiresAt = k.ExpiresUtc },
                    organisation = new { id = t.Id, name = t.Name, plan = t.Plan },
                    limits = new
                    {
                        requestsPerMinute = new { read = ApiLimits.Permits(t, RateClass.Read), write = ApiLimits.Permits(t, RateClass.Write), urgent = ApiLimits.Permits(t, RateClass.Urgent) },
                        keys = ApiLimits.Keys(t), webhooks = ApiLimits.Webhooks(t), journalDays = ApiLimits.JournalDays(t)
                    }
                }, ApiJson.Options);
            });
        });

        r.Map(WebhookList, (HttpContext ctx, WebhookService svc) => Results.Json(new { data = svc.List(ctx.User.ClientId()).Select(e => Dto(e)) }, ApiJson.Options));

        r.Map(WebhookCreate, (HttpContext ctx, WebhookService svc, WebhookBody? body) =>
        {
            var res = svc.Create(ctx.User.ClientId(), ctx.User.OwnerId(), body?.Name, body?.Url, body?.Events);
            if (res.Endpoint == null) return Invalid(ctx, res.Error!);
            return Results.Json(new { id = res.Endpoint.Id, name = res.Endpoint.Name, url = res.Endpoint.Url, events = res.Endpoint.Events, active = true, secret = res.Secret }, ApiJson.Options, statusCode: 201);
        });

        r.Map(WebhookGet, (HttpContext ctx, WebhookService svc, Guid id) =>
            svc.Get(ctx.User.ClientId(), id) is { } e ? Results.Json(Dto(e), ApiJson.Options) : ApiProblem.Result(ctx, ApiCodes.NotFound));

        r.Map(WebhookUpdate, (HttpContext ctx, WebhookService svc, Guid id, WebhookBody? body) =>
        {
            var cid = ctx.User.ClientId();
            if (svc.Get(cid, id) is not { } e) return ApiProblem.Result(ctx, ApiCodes.NotFound);
            var err = svc.Update(cid, id, body?.Name ?? e.Name, body?.Url ?? e.Url, body?.Events ?? e.Events);
            if (err != null) return Invalid(ctx, err);
            if (body?.Active == true && !e.Active) svc.Reactivate(cid, id);
            else if (body?.Active == false && e.Active) svc.Disable(cid, id, "Désactivée par l'API", notify: false);
            return Results.Json(Dto(svc.Get(cid, id)!), ApiJson.Options);
        });

        r.Map(WebhookDelete, (HttpContext ctx, WebhookService svc, Guid id) =>
            svc.Delete(ctx.User.ClientId(), id) ? Results.NoContent() : ApiProblem.Result(ctx, ApiCodes.NotFound));

        r.Map(WebhookRotate, (HttpContext ctx, WebhookService svc, Guid id) =>
            svc.RotateSecret(ctx.User.ClientId(), id) is { } secret ? Results.Json(new { secret }, ApiJson.Options) : ApiProblem.Result(ctx, ApiCodes.NotFound));

        r.Map(WebhookTest, async (HttpContext ctx, WebhookService svc, Guid id) =>
            await svc.SendTest(ctx.User.ClientId(), id, ctx.RequestAborted) is { } d ? Results.Json(DeliveryDto(d), ApiJson.Options) : ApiProblem.Result(ctx, ApiCodes.NotFound));

        r.Map(WebhookDeliveries, (HttpContext ctx, WebhookService svc, WebhookJournal journal, Guid id, int? limit, long? cursor, string? status) =>
        {
            var cid = ctx.User.ClientId();
            if (svc.Get(cid, id) == null) return ApiProblem.Result(ctx, ApiCodes.NotFound);
            var n = Math.Clamp(limit ?? 50, 1, 100);
            var page = journal.Query(cid, id, problemsOnly: status == "problems", limit: n + 1, before: cursor is { } c ? new DateTime(c, DateTimeKind.Utc) : null);
            return Results.Json(new { data = page.Take(n).Select(DeliveryDto), next = page.Count > n ? page[n - 1].QueuedUtc.Ticks.ToString() : null }, ApiJson.Options);
        });
    }
}

/// <summary>Documentation générée à partir du registre : OpenAPI (JSON) et page d'aide à l'adresse et aux couleurs du revendeur.</summary>
public static class ApiDocs
{
    public static JsonObject OpenApi(ApiRegistry reg, string baseUrl)
    {
        var paths = new JsonObject();
        foreach (var r in reg.Routes)
        {
            if (paths[r.Path] is not JsonObject item) paths[r.Path] = item = new JsonObject();
            var parameters = new JsonArray();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(r.Path, @"\{(\w+)\}"))
                parameters.Add(new JsonObject { ["name"] = m.Groups[1].Value, ["in"] = "path", ["required"] = true, ["schema"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" } });
            foreach (var q in (r.Query ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var p = q.Split(':', 3, StringSplitOptions.TrimEntries);
                var schema = p[1] == "integer" ? new JsonObject { ["type"] = "integer" } : p[1] == "uuid" ? new JsonObject { ["type"] = "string", ["format"] = "uuid" } : new JsonObject { ["type"] = "string" };
                parameters.Add(new JsonObject { ["name"] = p[0], ["in"] = "query", ["required"] = false, ["description"] = p.Length > 2 ? p[2] : "", ["schema"] = schema });
            }
            var problem = new JsonObject { ["content"] = new JsonObject { ["application/problem+json"] = new JsonObject { ["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/Problem" } } } };
            JsonObject Resp(string text) => new JsonObject { ["description"] = text, ["content"] = problem["content"]!.DeepClone() };
            var op = new JsonObject
            {
                ["summary"] = r.Summary,
                ["tags"] = new JsonArray(reg.Scope(r.Scope)?.Group ?? "Autres"),
                ["parameters"] = parameters,
                ["security"] = new JsonArray(new JsonObject { ["bearerAuth"] = new JsonArray() }),
                ["x-linkivi-scope"] = r.Scope,
                ["x-linkivi-rate-class"] = r.Class,
                ["responses"] = new JsonObject
                {
                    ["200"] = new JsonObject { ["description"] = "Succès" },
                    ["401"] = Resp("Clé absente, inconnue, révoquée ou expirée"),
                    ["403"] = Resp("Accès manquant, plan insuffisant ou espace suspendu"),
                    ["404"] = Resp("Ressource introuvable (ou d'une autre organisation)"),
                    ["422"] = Resp("Requête invalide, limite atteinte ou Idempotency-Key réutilisée avec un autre corps"),
                    ["429"] = Resp("Trop de requêtes (en-tête Retry-After)"),
                }
            };
            if (r.Body != null)
                op["requestBody"] = new JsonObject
                {
                    ["required"] = true,
                    ["content"] = new JsonObject { [r.BodyType] = new JsonObject { ["schema"] = new JsonObject { ["type"] = r.BodyType == "application/json" ? "object" : "string" }, ["example"] = r.BodyType == "application/json" ? JsonNode.Parse(r.Body) : r.Body } }
                };
            if (r.Method == "POST") op["parameters"]!.AsArray().Add(new JsonObject { ["name"] = "Idempotency-Key", ["in"] = "header", ["required"] = false, ["description"] = "Rejeu sans effet pendant 24 h (100 caractères au plus)", ["schema"] = new JsonObject { ["type"] = "string" } });
            item[r.Method.ToLowerInvariant()] = op;
        }
        return new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject { ["title"] = "API Linkivi", ["version"] = ApiRegistry.Version },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = baseUrl + "/api/v1" }),
            ["components"] = new JsonObject
            {
                ["securitySchemes"] = new JsonObject { ["bearerAuth"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer", ["description"] = "Clé d'API « lkv_… » créée dans Contenus › API et webhooks" } },
                ["schemas"] = new JsonObject
                {
                    ["Problem"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["code"] = new JsonObject { ["type"] = "string", ["description"] = "Code stable, lisible par un programme" }, ["status"] = new JsonObject { ["type"] = "integer" },
                            ["title"] = new JsonObject { ["type"] = "string" }, ["detail"] = new JsonObject { ["type"] = "string" }, ["requestId"] = new JsonObject { ["type"] = "string" },
                        }
                    }
                }
            },
            ["paths"] = paths,
        };
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);

    public const string SwaggerVersion = "5.17.14";

    /// <summary>
    /// Page « Essayer » : Swagger UI (chargé depuis jsDelivr, version figée) sur le descriptif OpenAPI de ce serveur. On y colle sa clé (Authorize) puis on envoie
    /// de vraies requêtes à l'adresse du revendeur. À remplacer par des fichiers servis par Linkivi si l'on veut s'affranchir du CDN.
    /// </summary>
    public static string SwaggerHtml(Reseller? reseller)
    {
        var color = reseller?.BrandColor is { Length: 7 } c && c[0] == '#' && c.Skip(1).All(Uri.IsHexDigit) ? c : "#0B1F3A";
        var name = E(reseller?.DisplayName ?? "Linkivi");
        var cdn = $"https://cdn.jsdelivr.net/npm/swagger-ui-dist@{SwaggerVersion}";
        return $"<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Essayer l'API {name}</title>"
            + $"<link rel=\"stylesheet\" href=\"{cdn}/swagger-ui.css\"><style>body{{margin:0;font-family:'IBM Plex Sans','Segoe UI',system-ui,sans-serif}}"
            + $"header{{background:#fff;border-bottom:1px solid #DDE3EA;padding:14px 24px;display:flex;gap:12px;align-items:center}}header b{{font-size:20px;color:{color}}}header span{{color:#5A6B80}}header a{{margin-left:auto;color:#3B82C4}}"
            + $".swagger-ui .topbar{{display:none}}.swagger-ui .btn.authorize{{color:{color};border-color:{color}}}.swagger-ui .btn.authorize svg{{fill:{color}}}</style></head><body>"
            + $"<header><b>{name}</b><span>Essayer l'API · version {ApiRegistry.Version}</span><a href=\"/docs/api\">Documentation</a></header><div id=\"ui\"></div>"
            + $"<script src=\"{cdn}/swagger-ui-bundle.js\"></script><script>window.ui=SwaggerUIBundle({{url:'/api/v1/openapi.json',dom_id:'#ui',persistAuthorization:true,docExpansion:'list',tryItOutEnabled:true}});</script></body></html>";
    }

    public static string Html(ApiRegistry reg, Reseller? reseller, string baseUrl)
    {
        var color = reseller?.BrandColor is { Length: 7 } c && c[0] == '#' && c.Skip(1).All(Uri.IsHexDigit) ? c : "#0B1F3A";
        var name = E(reseller?.DisplayName ?? "Linkivi");
        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>API {name}</title><style>")
          .Append($":root{{--brand:{color};--ink:#0B1F3A;--muted:#5A6B80;--line:#DDE3EA}}")
          .Append("body{font-family:'IBM Plex Sans','Segoe UI',system-ui,sans-serif;color:var(--ink);margin:0;line-height:1.55;background:#F5F7FA}")
          .Append("header{background:#fff;border-bottom:1px solid var(--line);padding:14px 24px;display:flex;gap:12px;align-items:center}header b{font-size:20px;color:var(--brand)}header span{color:var(--muted)}")
          .Append("main{max-width:900px;margin:0 auto;padding:24px}section{background:#fff;border:1px solid var(--line);border-radius:4px;padding:6px 22px 18px;margin-bottom:16px}")
          .Append("h2{font-size:20px;margin:18px 0 6px}h3{font-size:15.5px;margin:16px 0 4px}p{color:var(--muted);margin:4px 0 10px}code{background:#EDF1F5;border-radius:3px;padding:0 5px;font-size:13px}")
          .Append("pre{background:#0B1F3A;color:#DCE6F2;border-radius:4px;padding:12px 14px;overflow:auto;font-size:12.5px}table{border-collapse:collapse;width:100%;font-size:14px}")
          .Append("td,th{text-align:left;border-bottom:1px solid #EDF1F5;padding:7px 10px;vertical-align:top}th{color:var(--muted);font-weight:500;background:#FAFBFC}")
          .Append("</style></head><body>")
          .Append($"<header><b>{name}</b><span>Documentation de l'API · version {ApiRegistry.Version}</span><a href=\"/docs/api/try\" style=\"margin-left:auto;color:#3B82C4\">Essayer en ligne</a></header><main>");

        sb.Append("<section id=\"demarrer\"><h2>Premier appel</h2><p>Un administrateur crée une clé dans <b>Contenus › API et webhooks</b> (plan Pro, mode Expert). Elle n'est affichée qu'une seule fois. ")
          .Append("Envoyez-la dans l'en-tête <code>Authorization</code> ; <code>/me</code> répond avec l'organisation et les accès de la clé.</p>")
          .Append($"<pre>curl {E(baseUrl)}/api/v1/me \\\n  -H \"Authorization: Bearer lkv_votre_clé\"</pre>")
          .Append("<p>La clé ne fonctionne que sur l'adresse de votre revendeur, et ne voit que votre organisation. Les dates sont en UTC (ISO 8601).</p></section>");

        sb.Append("<section id=\"limites\"><h2>Limites</h2><p>Par clé, par minute (réglables par organisation) :</p><table><tr><th>Classe</th><th>Par défaut</th><th>Au-delà</th></tr>");
        foreach (var cls in RateClass.All)
            sb.Append($"<tr><td>{(cls == RateClass.Read ? "Lecture" : cls == RateClass.Write ? "Écriture" : "Urgence")}</td><td>{ApiLimits.Permits(new Tenant(), cls)} requêtes</td><td>429 + <code>Retry-After</code></td></tr>");
        sb.Append($"</table><p>Par défaut : {ApiLimits.DefaultKeys} clés, {ApiLimits.DefaultWebhooks} adresses de webhook, {ApiLimits.DefaultJournalDays} jours de journal.</p>")
          .Append("<h3>Idempotence</h3><p>Ajoutez l'en-tête <code>Idempotency-Key</code> (100 caractères au plus) à un <code>POST</code> : s'il est rejoué dans les 24 h avec le même corps, la même réponse est rendue sans rien refaire (<code>Idempotency-Replayed: true</code>). ")
          .Append("Même clé avec un autre corps : 422 ; même clé encore en cours : 409.</p></section>");

        sb.Append("<section id=\"erreurs\"><h2>Erreurs</h2><p>Toujours <code>application/problem+json</code> avec un <code>code</code> stable et le <code>requestId</code> (aussi dans l'en-tête <code>X-Request-Id</code>).</p><table><tr><th>Code</th><th>HTTP</th><th>Sens</th></tr>");
        foreach (var (code, status, title) in ApiCodes.All) sb.Append($"<tr id=\"{code}\"><td><code>{code}</code></td><td>{status}</td><td>{E(title)}</td></tr>");
        sb.Append("</table></section>");

        sb.Append("<section id=\"webhooks\"><h2>Webhooks</h2><p>Linkivi envoie un <code>POST</code> JSON à chaque adresse abonnée. Réponse attendue : 2xx en moins de 10 s, redirections non suivies. ")
          .Append("Livraison <b>au moins une fois</b>, ordre non garanti : l'<code>id</code> de l'événement sert à dédupliquer. Réessais à 1 min, 5 min, 30 min et 2 h (5 tentatives), puis abandon ; ")
          .Append("10 échecs consécutifs (ou une réponse 410) désactivent l'adresse.</p>")
          .Append("<pre>{ \"id\": \"evt_9f31…\", \"type\": \"ping\", \"apiVersion\": \"" + ApiRegistry.Version + "\",\n  \"createdAt\": \"2026-11-01T14:36:02Z\", \"organisationId\": \"4f0c…\", \"data\": { … } }</pre>")
          .Append("<h3>Vérifier la signature</h3><p>En-tête <code>Linkivi-Signature: t=&lt;secondes&gt;,v1=&lt;HMAC-SHA256(secret, t + \".\" + corps)&gt;</code>. Comparez sans fuite de temps et refusez les messages de plus de 5 minutes. Pendant 24 h après un renouvellement de secret, deux <code>v1</code> sont envoyés.</p>")
          .Append("<pre>// Node.js\nconst [t, v1] = entete.split(',').map(p =&gt; p.split('=')[1]);\nif (Math.abs(Date.now()/1000 - t) &gt; 300) return false;\nconst attendu = crypto.createHmac('sha256', secret).update(t + '.' + corps).digest('hex');\nreturn crypto.timingSafeEqual(Buffer.from(attendu), Buffer.from(v1));</pre>")
          .Append("<pre># Python\nattendu = hmac.new(secret.encode(), f\"{t}.{corps}\".encode(), hashlib.sha256).hexdigest()\nreturn hmac.compare_digest(attendu, v1) and abs(time.time() - int(t)) &lt;= 300</pre>")
          .Append("<pre>// C#\nvar attendu = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(t + \".\" + corps))).ToLower();\nreturn CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(attendu), Encoding.UTF8.GetBytes(v1));</pre>")
          .Append("<h3>Événements disponibles</h3><table><tr><th>Type</th><th>Quand</th></tr>");
        foreach (var e in reg.Events) sb.Append($"<tr><td><code>{E(e.Id)}</code></td><td>{E(e.Label)}</td></tr>");
        sb.Append("</table></section>");

        sb.Append("<section id=\"reference\"><h2>Référence</h2><p>Description OpenAPI : <a href=\"/api/v1/openapi.json\">/api/v1/openapi.json</a>.</p><table><tr><th>Route</th><th>Accès</th><th>Débit</th><th>Description</th></tr>");
        foreach (var r in reg.Routes) sb.Append($"<tr><td><code>{r.Method} /api/v1{E(r.Path)}</code></td><td><code>{E(r.Scope)}</code></td><td>{r.Class}</td><td>{E(r.Summary)}</td></tr>");
        sb.Append("</table><h3>Accès (scopes)</h3><table><tr><th>Accès</th><th>Description</th></tr>");
        foreach (var s in reg.Scopes) sb.Append($"<tr><td><code>{E(s.Id)}</code></td><td>{E(s.Label)}</td></tr>");
        sb.Append("</table></section></main></body></html>");
        return sb.ToString();
    }
}
