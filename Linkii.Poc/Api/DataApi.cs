namespace Linkii.Poc;

/// <summary>
/// Module « Données » de l'API (§153, envoi direct ; remplace le lot « envoi direct » du §139) : le système du client pousse ses lignes dans un jeu de données
/// « Envoi direct », que les compositions utilisent comme n'importe quel autre jeu (champs, listes reliées, indicateurs). Les jeux lus depuis une API, un fichier
/// ou une base restent gérés depuis l'application : l'API les liste et les lit, sans les modifier.
/// </summary>
public class DataApiModule : IApiModule
{
    public const string Read = "data:read", Write = "data:write";
    public string Id => "data";

    public IEnumerable<ApiScope> Scopes { get; } = new[]
    {
        new ApiScope(Read, "Données", "Lire les jeux de données et leurs lignes"),
        new ApiScope(Write, "Données", "Créer des jeux « Envoi direct » et leur envoyer, modifier ou retirer des lignes"),
    };

    public IEnumerable<ApiTemplate> Templates { get; } = new[]
    {
        new ApiTemplate("Lecture seule", "Consulter les données", new[] { Read }, 10),
        new ApiTemplate("Envoi de données", "Envoyer des lignes depuis un autre système (ERP, caisse, capteurs…)", new[] { Read, Write }, 40),
    };

    public IEnumerable<EventType> Events { get; } = new[]
    {
        new EventType("data.failed", "Données", "Une source de données ne peut plus être lue (la copie précédente reste affichée)"),
        new EventType("data.recovered", "Données", "Une source de données en échec est de nouveau lue"),
    };

    public static readonly ApiRoute List = new("GET", "/datasets", Read, RateClass.Read, "Lister les jeux de données (tous types) et leurs colonnes");
    public static readonly ApiRoute Create = new("POST", "/datasets", Write, RateClass.Write, "Créer un jeu « Envoi direct » (nom, colonnes facultatives)",
        Body: "{ \"name\": \"Occupation des salles\", \"columns\": [ { \"name\": \"Salle\" }, { \"name\": \"Étage\", \"type\": \"number\" } ] }");
    public static readonly ApiRoute One = new("GET", "/datasets/{id}", Read, RateClass.Read, "Lire un jeu de données");
    public static readonly ApiRoute Delete = new("DELETE", "/datasets/{id}", Write, RateClass.Write, "Supprimer un jeu « Envoi direct » (refusé s'il est utilisé par une composition)");
    public static readonly ApiRoute RowsGet = new("GET", "/datasets/{id}/rows", Read, RateClass.Read, "Lire les lignes d'un jeu (limit, cursor)",
        Query: "limit:integer:1 à 1000 (100 par défaut); cursor:string:valeur « next » de la page précédente");
    public static readonly ApiRoute RowsReplace = new("PUT", "/datasets/{id}/rows", Write, RateClass.Write, "Remplacer toutes les lignes (JSON ou CSV)",
        Body: "[ { \"Salle\": \"A1\", \"Étage\": 1 }, { \"Salle\": \"A2\", \"Étage\": 2 } ]");
    public static readonly ApiRoute RowsAdd = new("POST", "/datasets/{id}/rows", Write, RateClass.Write, "Ajouter des lignes (JSON ou CSV) ; avec ?key=colonne, met à jour celles qui ont la même clé",
        Query: "key:string:colonne clé : les lignes qui ont la même valeur sont mises à jour",
        Body: "[ { \"Salle\": \"A3\", \"Étage\": 1 } ]");
    public static readonly ApiRoute RowsRemove = new("DELETE", "/datasets/{id}/rows", Write, RateClass.Write, "Retirer toutes les lignes, ou celles dont ?key=colonne vaut ?value=…",
        Query: "key:string:colonne clé (avec value); value:string:valeur à retirer");

    public IEnumerable<ApiRoute> Routes { get; } = new[] { List, Create, One, Delete, RowsGet, RowsReplace, RowsAdd, RowsRemove };

    public record ColumnBody(string? Name, string? Label, string? Type);
    public record CreateBody(string? Name, List<ColumnBody>? Columns);
    public record ColDto(string Key, string Label, string Type);
    public record DatasetDto(Guid Id, string Name, string Source, bool Writable, int? Rows, DateTime? UpdatedAt, List<ColDto> Columns);

    private static DatasetDto Dto(DataSetService svc, Guid clientId, DataSet ds, string kind)
    {
        var t = svc.Table(clientId, ds.Id);
        var cols = (t?.Columns ?? ds.Columns).Select(c => new ColDto(c.Key, c.Label.Length > 0 ? c.Label : c.Key, c.Type)).ToList();
        return new DatasetDto(ds.Id, ds.Name, kind, kind == DataKinds.Push, t?.Rows.Count, t?.ReadUtc, cols);
    }

    private static object? Typed(string? v, string type) => v switch
    {
        null => null,
        "" => null,
        _ when type == "number" && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        _ when type == "bool" && bool.TryParse(v, out var b) => b,
        _ => v,
    };

    /// <summary>Corps de la requête (JSON : tableau d'objets, ou objet qui en contient un ; CSV) → table brute. Rend l'erreur lisible sinon.</summary>
    private static async Task<(RawTable? Raw, string? Error)> ReadBody(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await ctx.Request.Body.ReadAsync(buf, ctx.RequestAborted)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > DataKinds.MaxBytes) return (null, $"Corps trop volumineux ({DataKinds.MaxBytes / 1_000_000} Mo au plus).");
        }
        if (ms.Length == 0) return (null, "Corps vide : envoyez du JSON (tableau d'objets) ou du CSV.");
        try { return (DataTables.Parse(ms.ToArray(), null, null, null), null); }
        catch (Exception e) when (e is InvalidOperationException or System.Text.Json.JsonException or FormatException) { return (null, e.Message); }
    }

    private static IResult Pushed(HttpContext ctx, DataSetService.PushResult r) =>
        r.Ok ? Results.Json(new { rows = r.Rows, added = r.Added, updated = r.Updated, changed = r.Changed }, ApiJson.Options)
             : ApiProblem.Result(ctx, r.Error!.StartsWith("Jeu de données introuvable", StringComparison.Ordinal) ? ApiCodes.NotFound : ApiCodes.InvalidRequest, r.Error);

    public void Map(ApiRouter r)
    {
        r.Map(List, (HttpContext ctx, JsonStore store, DataSetService svc) =>
        {
            var cid = ctx.User.ClientId();
            var items = store.Read(db => db.Clients.First(c => c.Id == cid).DataSets
                .Select(ds => (Ds: ds.Copy(), Kind: db.Clients.First(c => c.Id == cid).DataConnections.FirstOrDefault(c => c.Id == ds.ConnectionId)?.Kind ?? "")).ToList());
            return Results.Json(new { data = items.OrderBy(i => i.Ds.Name, StringComparer.OrdinalIgnoreCase).Select(i => Dto(svc, cid, i.Ds, i.Kind)) }, ApiJson.Options);
        });

        r.Map(Create, (HttpContext ctx, JsonStore store, DataSetService svc, CreateBody? body) =>
        {
            var cid = ctx.User.ClientId();
            var cols = new List<DataColumn>();
            foreach (var c in body?.Columns ?? new())
            {
                var label = (c.Label ?? c.Name ?? "").Trim();
                if (label.Length == 0) return ApiProblem.Result(ctx, ApiCodes.InvalidRequest, "Chaque colonne a besoin d'un nom.");
                var type = string.IsNullOrWhiteSpace(c.Type) ? "text" : c.Type.Trim();
                if (!DataKinds.Types.Any(t => t.Id == type)) return ApiProblem.Result(ctx, ApiCodes.InvalidRequest, $"Type de colonne inconnu : {type} ({string.Join(", ", DataKinds.Types.Select(t => t.Id))}).");
                var key = DataTables.Slug(label);
                if (cols.Any(x => x.Key == key)) return ApiProblem.Result(ctx, ApiCodes.InvalidRequest, $"Deux colonnes portent la même clé : {key}.");
                cols.Add(new DataColumn { Key = key, Label = label, Type = type });
            }
            if (cols.Count > DataKinds.MaxColumns) return ApiProblem.Result(ctx, ApiCodes.InvalidRequest, $"{DataKinds.MaxColumns} colonnes au plus.");
            var ds = new DataSet { Name = body?.Name ?? "", Columns = cols };
            var error = store.Write(db =>
            {
                var t = db.Clients.First(c => c.Id == cid);
                if (t.DataSets.Count >= DataKinds.MaxDataSets) return $"Limite atteinte : {DataKinds.MaxDataSets} jeux de données au plus.";
                var conn = t.DataConnections.FirstOrDefault(c => c.Kind == DataKinds.Push);
                if (conn == null)
                {
                    if (t.DataConnections.Count >= DataKinds.MaxConnections) return $"Limite atteinte : {DataKinds.MaxConnections} connexions au plus.";
                    conn = new DataConnection { Kind = DataKinds.Push, Name = "Envoi direct (API)" };
                    t.DataConnections.Add(conn);
                }
                ds.ConnectionId = conn.Id;
                if (DataSourceOps.CheckDataSet(ds, t.DataSets) is { } e) return e;
                t.DataSets.Add(ds);
                return null;
            });
            if (error != null) return ApiProblem.Result(ctx, error.StartsWith("Limite atteinte", StringComparison.Ordinal) ? ApiCodes.LimitReached : ApiCodes.InvalidRequest, error);
            return Results.Json(Dto(svc, cid, ds, DataKinds.Push), ApiJson.Options, statusCode: 201);
        });

        r.Map(One, (HttpContext ctx, JsonStore store, DataSetService svc, Guid id) =>
        {
            var cid = ctx.User.ClientId();
            var found = store.Read(db => db.Clients.First(c => c.Id == cid) is { } t && t.DataSets.FirstOrDefault(d => d.Id == id) is { } ds
                ? (ds.Copy(), t.DataConnections.FirstOrDefault(c => c.Id == ds.ConnectionId)?.Kind ?? "") : ((DataSet, string)?)null);
            return found is { } f ? Results.Json(Dto(svc, cid, f.Item1, f.Item2), ApiJson.Options) : ApiProblem.Result(ctx, ApiCodes.NotFound);
        });

        r.Map(Delete, (HttpContext ctx, JsonStore store, DataSetService svc, Guid id) =>
        {
            var cid = ctx.User.ClientId();
            var key = id.ToString("N");
            var res = store.Write(db =>
            {
                var t = db.Clients.First(c => c.Id == cid);
                var ds = t.DataSets.FirstOrDefault(d => d.Id == id);
                if (ds == null) return "notfound";
                if (t.DataConnections.FirstOrDefault(c => c.Id == ds.ConnectionId)?.Kind != DataKinds.Push) return "Ce jeu de données est géré depuis l'application (Contenus › Sources de données).";
                var used = db.Media.Count(m => m.ClientId == cid && m.AppId == Compositions.AppId && (m.Settings.GetValueOrDefault("doc") ?? "").Contains(key));
                if (used > 0) return $"Ce jeu est utilisé par {used} composition{(used > 1 ? "s" : "")} : retirez-le d'abord de leur éditeur.";
                t.DataSets.Remove(ds);
                return null;
            });
            if (res == "notfound") return ApiProblem.Result(ctx, ApiCodes.NotFound);
            if (res != null) return ApiProblem.Result(ctx, ApiCodes.InvalidRequest, res);
            svc.Forget(cid, id);
            return Results.NoContent();
        });

        r.Map(RowsGet, (HttpContext ctx, JsonStore store, DataSetService svc, Guid id, int? limit, string? cursor) =>
        {
            var cid = ctx.User.ClientId();
            if (!store.Read(db => db.Clients.First(c => c.Id == cid).DataSets.Any(d => d.Id == id))) return ApiProblem.Result(ctx, ApiCodes.NotFound);
            var t = svc.Table(cid, id);
            if (t == null) return Results.Json(new { data = Array.Empty<object>(), next = (string?)null }, ApiJson.Options);
            var n = Math.Clamp(limit ?? 100, 1, 1000);
            var start = int.TryParse(cursor, out var c0) && c0 > 0 ? c0 : 0;
            var rows = t.Rows.Skip(start).Take(n).Select(row => t.Columns.Select((c, i) => (c.Key, V: Typed(i < row.Count ? row[i] : null, c.Type))).ToDictionary(x => x.Key, x => x.V)).ToList();
            return Results.Json(new { data = rows, next = start + n < t.Rows.Count ? (start + n).ToString() : null, total = t.Rows.Count, updatedAt = t.ReadUtc }, ApiJson.Options);
        });

        r.Map(RowsReplace, async (HttpContext ctx, DataSetService svc, Guid id) =>
        {
            var (raw, err) = await ReadBody(ctx);
            return raw == null ? ApiProblem.Result(ctx, ApiCodes.InvalidRequest, err) : Pushed(ctx, svc.Push(ctx.User.ClientId(), id, raw, DataSetService.PushMode.Replace));
        });

        r.Map(RowsAdd, async (HttpContext ctx, DataSetService svc, Guid id, string? key) =>
        {
            var (raw, err) = await ReadBody(ctx);
            if (raw == null) return ApiProblem.Result(ctx, ApiCodes.InvalidRequest, err);
            var mode = string.IsNullOrWhiteSpace(key) ? DataSetService.PushMode.Append : DataSetService.PushMode.Upsert;
            return Pushed(ctx, svc.Push(ctx.User.ClientId(), id, raw, mode, key));
        });

        r.Map(RowsRemove, (HttpContext ctx, DataSetService svc, Guid id, string? key, string? value) =>
        {
            if (!string.IsNullOrWhiteSpace(key) && value == null) return ApiProblem.Result(ctx, ApiCodes.InvalidRequest, "Avec key, indiquez aussi value.");
            var (removed, error) = svc.RemoveRows(ctx.User.ClientId(), id, string.IsNullOrWhiteSpace(key) ? null : key, value);
            return removed is { } n ? Results.Json(new { removed = n }, ApiJson.Options)
                : ApiProblem.Result(ctx, error!.StartsWith("Jeu de données", StringComparison.Ordinal) ? ApiCodes.NotFound : ApiCodes.InvalidRequest, error);
        });
    }
}
