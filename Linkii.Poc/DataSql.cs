using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Linkii.Poc;

/// <summary>
/// Lecture d'une base de données (§139, lot 3) : SQL Server, PostgreSQL, MySQL / MariaDB. Partagé par le serveur Linkii (accès direct) et le
/// relais Linkii (Linkii.Relay, même fichier compilé dans les deux) : rien ici ne dépend du reste du serveur.
/// Lecture seule par trois barrières : requête vérifiée (une seule instruction SELECT / WITH, aucun mot qui écrit), transaction en lecture seule
/// (PostgreSQL, MySQL) ou annulée (SQL Server), et le test signale un utilisateur qui a des droits d'écriture.
/// </summary>
public static class DataSql
{
    public const string SqlServer = "sqlserver", Postgres = "postgres", MySql = "mysql";
    public static readonly (string Id, string Label, int Port)[] Engines = { (SqlServer, "SQL Server", 1433), (Postgres, "PostgreSQL", 5432), (MySql, "MySQL / MariaDB", 3306) };

    /// <summary>Paramètres d'une connexion à une base : ce que le serveur envoie au relais, ou utilise lui-même en accès direct.</summary>
    public record Target(string Engine, string Host, int Port, string Database, string User, string Password, bool Windows, bool TrustCert);

    /// <summary>Résultat : en-têtes et valeurs en texte (dates ISO, nombres en notation invariante), au plus maxRows lignes.</summary>
    public record Result(List<string> Headers, List<List<string?>> Rows);

    private static readonly Regex Forbidden = new(
        @"\b(insert|update|delete|merge|upsert|drop|alter|create|truncate|rename|grant|revoke|deny|exec|execute|call|into|copy|lock|unlock|set|attach|detach|pragma|vacuum|load_file|kill|shutdown|backup|restore|dbcc|openrowset|opendatasource|openquery|bulk|sp_\w+|xp_\w+|pg_\w*sleep\w*|pg_read_\w+|pg_ls_\w+|lo_\w+|dblink\w*|sleep|benchmark|waitfor)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Vérifie une requête saisie : null si elle est acceptée, sinon la raison. Commentaires et textes entre guillemets sont ignorés pour l'analyse.</summary>
    public static string? Check(string? sql)
    {
        var s = (sql ?? "").Trim();
        if (s.Length == 0) return "Écrivez une requête SELECT.";
        if (s.Length > 8000) return "Requête trop longue (8 000 caractères au plus).";
        var bare = Regex.Replace(s, @"--[^\n]*|/\*.*?\*/", " ", RegexOptions.Singleline);
        bare = Regex.Replace(bare, @"'(?:[^']|'')*'|""(?:[^""]|"""")*""|`[^`]*`|\[[^\]]*\]", "x");
        bare = bare.Trim().TrimEnd(';').Trim();
        if (bare.Contains(';')) return "Une seule instruction à la fois.";
        if (!Regex.IsMatch(bare, @"^(select|with)\b", RegexOptions.IgnoreCase)) return "Seules les requêtes SELECT (ou WITH … SELECT) sont acceptées.";
        if (Forbidden.Match(bare) is { Success: true } m) return $"Mot non autorisé dans une lecture : « {m.Value} ».";
        return null;
    }

    /// <summary>Nom de table ou de vue (« schema.table ») accepté : lettres, chiffres, _, $, espace, un point au plus.</summary>
    public static bool IsTableName(string? t) => t != null && Regex.IsMatch(t.Trim(), @"^[\p{L}\p{N}_$ -]{1,128}(\.[\p{L}\p{N}_$ -]{1,128})?$");

    /// <summary>Requête qui lit une table entière (au plus maxRows + 1 lignes, pour savoir s'il y en a plus).</summary>
    public static string TableQuery(string engine, string table, int maxRows)
    {
        if (!IsTableName(table)) throw new InvalidOperationException("Nom de table invalide.");
        var parts = table.Trim().Split('.');
        string Q(string p) => engine == SqlServer ? "[" + p.Replace("]", "]]") + "]" : engine == MySql ? "`" + p.Replace("`", "``") + "`" : "\"" + p.Replace("\"", "\"\"") + "\"";
        var name = string.Join(".", parts.Select(Q));
        return engine == SqlServer ? $"SELECT TOP ({maxRows + 1}) * FROM {name}" : $"SELECT * FROM {name} LIMIT {maxRows + 1}";
    }

    private static DbConnection Open(Target t)
    {
        var port = t.Port > 0 ? t.Port : Engines.FirstOrDefault(e => e.Id == t.Engine).Port;
        switch (t.Engine)
        {
            case SqlServer:
                var host = t.Host.Contains('\\') || t.Port <= 0 ? t.Host : t.Host + "," + port;   // instance nommée (serveur\SQLEXPRESS) : sans port
                var ms = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
                {
                    DataSource = host, InitialCatalog = t.Database, ConnectTimeout = 8, ApplicationName = "Linkii",
                    Encrypt = Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Optional, TrustServerCertificate = t.TrustCert,
                    ApplicationIntent = Microsoft.Data.SqlClient.ApplicationIntent.ReadOnly, Pooling = true
                };
                if (t.Windows) ms.IntegratedSecurity = true; else { ms.UserID = t.User; ms.Password = t.Password; }
                return new Microsoft.Data.SqlClient.SqlConnection(ms.ConnectionString);
            case Postgres:
                var pg = new Npgsql.NpgsqlConnectionStringBuilder
                {
                    Host = t.Host, Port = port, Database = t.Database, Username = t.User, Password = t.Password, Timeout = 8, CommandTimeout = 10,
                    SslMode = t.TrustCert ? Npgsql.SslMode.Prefer : Npgsql.SslMode.VerifyFull, ApplicationName = "Linkii"
                };
                return new Npgsql.NpgsqlConnection(pg.ConnectionString);
            case MySql:
                var my = new MySqlConnector.MySqlConnectionStringBuilder
                {
                    Server = t.Host, Port = (uint)port, Database = t.Database, UserID = t.User, Password = t.Password, ConnectionTimeout = 8, DefaultCommandTimeout = 10,
                    SslMode = t.TrustCert ? MySqlConnector.MySqlSslMode.Preferred : MySqlConnector.MySqlSslMode.VerifyFull, AllowLoadLocalInfile = false
                };
                return new MySqlConnector.MySqlConnection(my.ConnectionString);
            default: throw new InvalidOperationException("Moteur de base inconnu.");
        }
    }

    /// <summary>Ouvre, démarre une transaction en lecture seule (PostgreSQL, MySQL ; SQL Server : annulée à la fin), exécute f, annule toujours.</summary>
    private static async Task<T> ReadOnly<T>(Target t, Func<DbConnection, DbTransaction, Task<T>> f, CancellationToken ct)
    {
        await using var c = Open(t);
        try { await c.OpenAsync(ct); }
        catch (DbException e) { throw new InvalidOperationException("Connexion impossible : " + Clean(e.Message)); }
        async Task Exec(string q, DbTransaction? tx) { await using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = q; await cmd.ExecuteNonQueryAsync(ct); }
        if (t.Engine == MySql) await Exec("SET SESSION TRANSACTION READ ONLY", null);   // MySQL : vaut pour les transactions suivantes de la session
        await using var tx = await c.BeginTransactionAsync(ct);
        try
        {
            if (t.Engine == Postgres) await Exec("SET TRANSACTION READ ONLY", tx);
            return await f(c, tx);
        }
        catch (DbException e) { throw new InvalidOperationException(Clean(e.Message)); }
        finally { try { await tx.RollbackAsync(CancellationToken.None); } catch (Exception e) when (e is DbException or InvalidOperationException) { } }
    }

    private static string Clean(string m) => m.Length > 300 ? m[..300] + "…" : m;

    /// <summary>Exécute une lecture (requête vérifiée, ou table entière) : au plus maxRows lignes, 10 s.</summary>
    public static Task<Result> Read(Target t, string? table, string? sql, int maxRows, CancellationToken ct)
    {
        string query;
        if (!string.IsNullOrWhiteSpace(sql)) { if (Check(sql) is { } bad) throw new InvalidOperationException(bad); query = sql!.Trim().TrimEnd(';'); }
        else if (!string.IsNullOrWhiteSpace(table)) query = TableQuery(t.Engine, table!, maxRows);
        else throw new InvalidOperationException("Choisissez une table ou écrivez une requête.");
        return ReadOnly(t, async (c, tx) =>
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx; cmd.CommandText = query; cmd.CommandTimeout = 10;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var headers = Enumerable.Range(0, r.FieldCount).Select(i => r.GetName(i)).ToList();
            var rows = new List<List<string?>>();
            while (rows.Count < maxRows && await r.ReadAsync(ct))
                rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => Text(r.IsDBNull(i) ? null : r.GetValue(i))).ToList());
            return new Result(headers, rows);
        }, ct);
    }

    /// <summary>Valeur d'une cellule en texte : dates ISO, nombres invariants, oui/non en true/false ; binaire omis.</summary>
    public static string? Text(object? v) => v switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
        TimeOnly to => to.ToString("HH:mm", CultureInfo.InvariantCulture),
        byte[] => null,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString()
    };

    /// <summary>Teste la connexion : version du serveur, et un avertissement (« ⚠ … ») si l'utilisateur peut écrire.</summary>
    public static Task<string> Test(Target t, CancellationToken ct) => ReadOnly(t, async (c, tx) =>
    {
        async Task<string?> One(string q)
        {
            await using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = q; cmd.CommandTimeout = 10;
            try { return Text(await cmd.ExecuteScalarAsync(ct)); } catch (DbException) { return null; }
        }
        var version = (t.Engine == SqlServer ? await One("SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64))") : await One("SELECT version()")) ?? "";
        version = Regex.Match(version, @"^[^,(]{1,60}").Value.Trim();
        var writes = t.Engine switch
        {
            SqlServer => await One("SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin') = 1 OR IS_ROLEMEMBER('db_owner') = 1 OR IS_ROLEMEMBER('db_datawriter') = 1 OR HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT') = 1 THEN 1 ELSE 0 END") == "1",
            Postgres => await One("SELECT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) OR EXISTS (SELECT 1 FROM information_schema.role_table_grants WHERE grantee = current_user AND privilege_type IN ('INSERT','UPDATE','DELETE'))") == "true",
            _ => await One("SELECT COUNT(*) FROM information_schema.user_privileges WHERE grantee = CONCAT('''', SUBSTRING_INDEX(CURRENT_USER(), '@', 1), '''@''', SUBSTRING_INDEX(CURRENT_USER(), '@', -1), '''') AND privilege_type IN ('INSERT','UPDATE','DELETE')") is { } n && n != "0"
        };
        var tables = (await Tables(c, tx, t.Engine, ct)).Count;
        return $"Connexion réussie{(version.Length > 0 ? " · " + version : "")} · {tables} table{(tables > 1 ? "s" : "")} et vue{(tables > 1 ? "s" : "")}"
            + (writes ? ". ⚠ Cet utilisateur peut aussi écrire : Linkii ne fait que lire, mais un utilisateur en lecture seule est conseillé." : " · l'utilisateur ne peut que lire.");
    }, ct);

    /// <summary>Tables et vues visibles (« schema.table »), 500 au plus.</summary>
    public static Task<List<string>> Discover(Target t, CancellationToken ct) => ReadOnly(t, (c, tx) => Tables(c, tx, t.Engine, ct), ct);

    private static async Task<List<string>> Tables(DbConnection c, DbTransaction tx, string engine, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx; cmd.CommandTimeout = 10;
        cmd.CommandText = engine == MySql
            ? "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() ORDER BY table_name LIMIT 500"
            : "SELECT " + (engine == SqlServer ? "TOP 500 " : "") + "table_schema, table_name FROM information_schema.tables WHERE table_schema NOT IN ('information_schema','pg_catalog','sys','INFORMATION_SCHEMA') ORDER BY table_schema, table_name" + (engine == Postgres ? " LIMIT 500" : "");
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(engine == MySql ? r.GetString(0) : r.GetString(0) + "." + r.GetString(1));
        return list;
    }
}
