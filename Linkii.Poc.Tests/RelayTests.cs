extern alias relay;
using System.Net;
using System.Net.Http.Json;
using Linkii.Poc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RelayConfig = relay::Linkii.Relay.RelayConfig;
using RelayPairing = relay::Linkii.Relay.RelayPairing;
using RelayWorker = relay::Linkii.Relay.RelayWorker;

namespace Linkii.Poc.Tests;

/// <summary>Bases de données (§139, lot 3) et relais Linkii (lot 4) : requêtes en lecture seule, appairage, tâches réservées au relais de l'organisation.</summary>
[Collection("platform")]
public class RelayTests(Platform p)
{
    [Theory]
    [InlineData("abcd-2345", "ABCD2345")]
    [InlineData(" ABCD 2345 ", "ABCD2345")]
    [InlineData("abc", null)]
    [InlineData("ABCD-23456", null)]
    [InlineData("AB!D-2345", null)]
    [InlineData(null, null)]
    public void Code_d_installation_remis_en_forme(string? saisi, string? attendu) => Assert.Equal(attendu, RelayPairing.CleanCode(saisi));

    [Theory]
    [InlineData("https://linkii.duckdns.org", true)]
    [InlineData("http://localhost:5093", true)]
    [InlineData("http://linkii.exemple.ch", false)]
    [InlineData("linkii.exemple.ch", false)]
    [InlineData("", false)]
    public void Serveur_en_https_sauf_en_local(string server, bool accepte) => Assert.Equal(accepte, RelayPairing.CheckServer(server) == null);

    /// <summary>Le téléchargement Windows est l'exécutable suivi de l'adresse du serveur, que l'installateur relit ; le téléchargement Linux reste le zip.</summary>
    [Fact]
    public async Task Telechargement_windows_ajoute_l_adresse_du_serveur()
    {
        var dl = Path.Combine(p.Dir, "downloads");
        Directory.CreateDirectory(dl);
        var exe = new byte[] { (byte)'M', (byte)'Z', 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(Path.Combine(dl, "LinkiiRelay-Setup.exe"), exe);
        var http = p.CreateClient();

        var res = await http.GetAsync("/download/relay/windows");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("LinkiiRelay-Setup.exe", res.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        var body = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(exe, body.Take(exe.Length).ToArray());   // le fichier n'est pas modifié, seulement prolongé
        Assert.Equal("\n#LINKII-SERVER=http://localhost\n", System.Text.Encoding.UTF8.GetString(body.Skip(exe.Length).ToArray()));
        Assert.Equal(body.Length, res.Content.Headers.ContentLength);

        var copy = Path.Combine(p.Dir, "recu.exe");
        await File.WriteAllBytesAsync(copy, body);
        Assert.Equal("http://localhost", RelayPairing.ServerOf(copy));
        Assert.Null(RelayPairing.ServerOf(Path.Combine(dl, "LinkiiRelay-Setup.exe")));   // fichier d'origine : pas d'adresse

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/download/relay/win-x64")).StatusCode);   // ancienne archive Windows retirée
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/download/relay/macos")).StatusCode);
    }

    /// <summary>L'appairage de l'installateur : code mal formé refusé sans appel réseau, bon code accepté une seule fois, message du serveur lisible.</summary>
    [Fact]
    public async Task Installateur_appaire_avec_le_code_et_montre_le_message_du_serveur()
    {
        var http = p.CreateClient();
        var (none, bad) = await RelayPairing.PairAsync(http, "http://localhost", "abc", null);
        Assert.Null(none);
        Assert.Contains("8 caractères", bad);

        p.Services.GetRequiredService<JsonStore>().Write(db => db.Clients.First(c => c.Id == p.A.Id).DataRelays.Add(new DataRelay { Name = "SRV-INSTALL", PairingCode = "QRST4567", CodeUtc = DateTime.UtcNow }));
        var (ok, err) = await RelayPairing.PairAsync(http, "http://localhost", "qrst-4567", null);
        Assert.Null(err);
        Assert.Equal("SRV-INSTALL", ok!.Name);
        Assert.True(ok.Token.Length > 20);

        var (again, why) = await RelayPairing.PairAsync(http, "http://localhost", "QRST4567", null);   // code déjà utilisé
        Assert.Null(again);
        Assert.Contains("Code inconnu ou expiré", why);
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.salles", null)]
    [InlineData("  with x as (select 1 as a) select a from x;  ", null)]
    [InlineData("SELECT REPLACE(nom, 'a', 'b') AS n FROM t -- delete en commentaire", null)]
    [InlineData("SELECT 'drop table x' AS texte", null)]
    [InlineData("DELETE FROM t", "Seules les requêtes SELECT")]
    [InlineData("SELECT 1; DROP TABLE t", "Une seule instruction")]
    [InlineData("SELECT * INTO copie FROM t", "into")]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d", "delete")]
    [InlineData("SELECT pg_sleep(30)", "pg_sleep")]
    [InlineData("SELECT * FROM t FOR UPDATE", "update")]
    public void Requete_en_lecture_seule(string sql, string? refus)
    {
        var r = DataSql.Check(sql);
        if (refus == null) Assert.Null(r); else Assert.True(r != null && r.Contains(refus, StringComparison.OrdinalIgnoreCase), r ?? "accepté");
    }

    [Fact]
    public void Table_lue_avec_le_nom_protege_selon_le_moteur()
    {
        Assert.Equal("SELECT TOP (11) * FROM [dbo].[v_salles]", DataSql.TableQuery(DataSql.SqlServer, "dbo.v_salles", 10));
        Assert.Equal("SELECT * FROM \"public\".\"salles\" LIMIT 11", DataSql.TableQuery(DataSql.Postgres, "public.salles", 10));
        Assert.Equal("SELECT * FROM `salles` LIMIT 11", DataSql.TableQuery(DataSql.MySql, "salles", 10));
        Assert.Throws<InvalidOperationException>(() => DataSql.TableQuery(DataSql.SqlServer, "x]; drop table y --", 10));
    }

    [Fact]
    public async Task Appairage_par_code_une_seule_fois()
    {
        var relayId = Guid.NewGuid();
        p.Services.GetRequiredService<JsonStore>().Write(db => db.Clients.First(c => c.Id == p.A.Id).DataRelays.Add(new DataRelay { Id = relayId, Name = "Test", PairingCode = "ABCD2345", CodeUtc = DateTime.UtcNow }));
        var http = p.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/relay/pair", new RelayPairRequest("ZZZZ9999", null, "PC", "1.0.0"))).StatusCode);
        var ok = await http.PostAsJsonAsync("/api/relay/pair", new RelayPairRequest("abcd-2345", null, "PC", "1.0.0"));
        Assert.True(ok.IsSuccessStatusCode);
        var token = (await ok.Content.ReadFromJsonAsync<RelayPairResponse>())!.Token;
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/relay/pair", new RelayPairRequest("ABCD2345", null, "PC", "1.0.0"))).StatusCode);   // code consommé
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/relay/next")).StatusCode);
        Assert.Equal((p.A.Id, relayId), p.Services.GetRequiredService<RelayBroker>().Auth(token));
        // la tâche d'une autre organisation n'atteint pas ce relais
        var err = await Assert.ThrowsAsync<InvalidOperationException>(() => p.Services.GetRequiredService<RelayBroker>().Run(p.B.Id, relayId, new RelayJob(), CancellationToken.None));
        Assert.Contains("introuvable", err.Message);
    }

    /// <summary>
    /// Bout en bout avec le vrai relais (RelayWorker) branché sur le serveur de test et la base SQL Express locale (authentification Windows).
    /// Ignoré sans SQL Express : base LinkiiTest, table dbo.salles (3 lignes, voir DEV-POC §139).
    /// </summary>
    [Fact]
    public async Task Relais_lit_une_base_sql_server_interne()
    {
        var target = new DataSql.Target(DataSql.SqlServer, @"localhost\SQLEXPRESS", 0, "LinkiiTest", "", "", true, true);
        try { await DataSql.Test(target, CancellationToken.None); }
        catch (InvalidOperationException) { return; }   // pas de SQL Express ici : rien à vérifier

        var store = p.Services.GetRequiredService<JsonStore>();
        var data = p.Services.GetRequiredService<DataSetService>();
        var relayId = Guid.NewGuid();
        store.Write(db => db.Clients.First(c => c.Id == p.A.Id).DataRelays.Add(new DataRelay { Id = relayId, Name = "SRV-TEST", PairingCode = "WXYZ6789", CodeUtc = DateTime.UtcNow }));
        var pair = await (await p.CreateClient().PostAsJsonAsync("/api/relay/pair", new RelayPairRequest("WXYZ6789", null, "PC", "1.0.0"))).Content.ReadFromJsonAsync<RelayPairResponse>();

        var worker = new RelayWorker(new RelayConfig { Server = "http://localhost", Token = pair!.Token, Name = "SRV-TEST" }, NullLogger<RelayWorker>.Instance, p.Server.CreateHandler());
        using var stop = new CancellationTokenSource();
        await worker.StartAsync(stop.Token);
        try
        {
            var broker = p.Services.GetRequiredService<RelayBroker>();
            for (var i = 0; i < 50 && !broker.Online(relayId); i++) await Task.Delay(100);
            Assert.True(broker.Online(relayId));

            var conn = new DataConnection { Name = "Base des salles", Kind = DataKinds.Db, Engine = DataSql.SqlServer, Host = @"localhost\SQLEXPRESS", Database = "LinkiiTest", DbWindows = true, Via = "relay", RelayId = relayId };
            Assert.Null(DataSourceOps.CheckConnection(conn));
            Assert.StartsWith("Connexion réussie", await data.Test(p.A.Id, conn));
            Assert.Contains("dbo.salles", await data.Discover(p.A.Id, conn));

            var table = new DataSet { Name = "Salles", ConnectionId = conn.Id, Path = "dbo.salles" };
            var query = new DataSet { Name = "Salles libres", ConnectionId = conn.Id, Sql = "SELECT salle, places FROM dbo.salles WHERE statut = N'Libre'" };
            store.Write(db => { var t = db.Clients.First(c => c.Id == p.A.Id); t.DataConnections.Add(conn); t.DataSets.Add(table); t.DataSets.Add(query); });

            var r = await data.Refresh(p.A.Id, table.Id);
            Assert.Equal("ok", r!.Level);
            var t1 = data.Table(p.A.Id, table.Id)!;
            Assert.Equal(3, t1.Rows.Count);
            Assert.Equal(new[] { "text", "number", "text", "number", "date", "bool" }, t1.Columns.Select(c => c.Type));
            Assert.Equal(new string?[] { "Léman", "2", "Libre", "12", "2026-10-29T14:00:00", "true" }, t1.Rows[0]);

            Assert.Equal("ok", (await data.Refresh(p.A.Id, query.Id))!.Level);
            Assert.Single(data.Table(p.A.Id, query.Id)!.Rows);

            // une requête qui écrit est refusée avant même d'atteindre le relais
            var bad = new DataSet { Name = "Mauvaise", ConnectionId = conn.Id, Sql = "DELETE FROM dbo.salles" };
            Assert.NotNull(DataSourceOps.CheckDataSet(bad, Array.Empty<DataSet>()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => data.ReadRaw(p.A.Id, conn, bad, CancellationToken.None));
        }
        finally
        {
            stop.Cancel();
            try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        }
    }

    // ---------- API : utilisateur et mot de passe, puis jeton Bearer ----------

    [Theory]
    [InlineData("{\"token\":\"abc.def-123\"}", null, "abc.def-123", 1200)]
    [InlineData("{\"access_token\":\"t1\",\"expires_in\":3600}", null, "t1", 3600)]
    [InlineData("{\"data\":{\"accessToken\":\"t2\",\"expiresIn\":60}}", null, "t2", 60)]
    [InlineData("{\"result\":{\"jwt\":\"t3\"}}", null, "t3", 1200)]
    [InlineData("{\"auth\":{\"value\":\"t4\"}}", "auth.value", "t4", 1200)]
    [InlineData("\"t5\"", null, "t5", 1200)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789", null, "abcdefghijklmnopqrstuvwxyz0123456789", 1200)]
    public void Jeton_lu_dans_la_reponse_de_connexion(string reponse, string? chemin, string attendu, int secondes)
    {
        var (token, s) = DataSetService.ReadToken(reponse, chemin);
        Assert.Equal(attendu, token);
        Assert.Equal(secondes, s);
    }

    [Theory]
    [InlineData("{\"message\":\"ok\"}", null)]
    [InlineData("{\"token\":\"a b\"}", null)]
    [InlineData("{\"token\":12}", null)]
    [InlineData("<html>Bienvenue</html>", null)]
    [InlineData("{\"a\":1}", "data.token")]
    public void Jeton_introuvable_ou_invalide_est_refuse(string reponse, string? chemin) =>
        Assert.Throws<InvalidOperationException>(() => DataSetService.ReadToken(reponse, chemin));

    [Fact]
    public void Jeton_jwt_donne_sa_duree_restante()
    {
        string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var jwt = $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"exp\":{exp}}}")}.sig";
        var (token, s) = DataSetService.ReadToken($"{{\"token\":\"{jwt}\"}}", null);
        Assert.Equal(jwt, token);
        Assert.InRange(s, 1700, 1800);
    }

    [Fact]
    public void Connexion_par_identifiants_demande_chemin_et_utilisateur()
    {
        var c = new DataConnection { Name = "API interne", BaseUrl = "https://api.exemple.ch/v1", Auth = "login", User = "linkii" };
        Assert.Contains("chemin de connexion", DataSourceOps.CheckConnection(c));
        c.LoginPath = "/auth/login";
        Assert.Null(DataSourceOps.CheckConnection(c));
        Assert.Equal(("username", "password", "json"), (c.LoginUserField, c.LoginPassField, c.LoginFormat));
        c.User = " ";
        Assert.Contains("utilisateur", DataSourceOps.CheckConnection(c));
        c.User = "linkii"; c.LoginPath = "https://autre.hote/login";
        Assert.NotNull(DataSourceOps.CheckConnection(c));   // le chemin ne peut pas changer d'hôte
        c.LoginPath = "/auth/login"; c.LoginUserField = "nom\"; drop";
        Assert.Contains("champ", DataSourceOps.CheckConnection(c));
    }

    /// <summary>
    /// Bout en bout par le vrai relais, avec une fausse API interne (Kestrel local) : connexion en JSON puis en formulaire, jeton gardé et réutilisé,
    /// nouvelle connexion automatique quand l'API refuse le jeton (401), mauvais mot de passe expliqué.
    /// </summary>
    [Fact]
    public async Task Api_interne_avec_utilisateur_et_mot_de_passe_puis_jeton()
    {
        var logins = 0;
        var valid = "";
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        api.MapPost("/auth/login", async (Microsoft.AspNetCore.Http.HttpRequest r) =>
        {
            string? u, pw;
            if (r.HasFormContentType) { var f = await r.ReadFormAsync(); u = f["username"]; pw = f["password"]; }
            else { var d = await r.ReadFromJsonAsync<Dictionary<string, string>>(); u = d?.GetValueOrDefault("username"); pw = d?.GetValueOrDefault("password"); }
            if (u != "linkii" || pw != "s3cret") return Microsoft.AspNetCore.Http.Results.Unauthorized();
            valid = "tok-" + Interlocked.Increment(ref logins);
            return Microsoft.AspNetCore.Http.Results.Json(new { data = new { access_token = valid, expires_in = 3600 } });
        });
        api.MapGet("/salles", (Microsoft.AspNetCore.Http.HttpRequest r) =>
            valid.Length > 0 && r.Headers.Authorization == "Bearer " + valid
                ? Microsoft.AspNetCore.Http.Results.Json(new[] { new { salle = "Léman", places = 12 }, new { salle = "Jura", places = 8 } })
                : Microsoft.AspNetCore.Http.Results.Unauthorized());
        api.MapGet("/personnes", (Microsoft.AspNetCore.Http.HttpRequest r) =>   // jeton attendu dans un autre en-tête, sans préfixe
            valid.Length > 0 && r.Headers["X-Dashboard-Token"] == valid
                ? Microsoft.AspNetCore.Http.Results.Json(new[] { new { nom = "Dupont" } })
                : Microsoft.AspNetCore.Http.Results.Unauthorized());
        await api.StartAsync();
        var baseUrl = api.Urls.First();

        var store = p.Services.GetRequiredService<JsonStore>();
        var data = p.Services.GetRequiredService<DataSetService>();
        var box = p.Services.GetRequiredService<SecretBox>();
        var relayId = Guid.NewGuid();
        store.Write(db => db.Clients.First(c => c.Id == p.A.Id).DataRelays.Add(new DataRelay { Id = relayId, Name = "SRV-API", PairingCode = "API26789", CodeUtc = DateTime.UtcNow }));
        var pair = await (await p.CreateClient().PostAsJsonAsync("/api/relay/pair", new RelayPairRequest("API26789", null, "PC", "1.2.0"))).Content.ReadFromJsonAsync<RelayPairResponse>();
        var worker = new RelayWorker(new RelayConfig { Server = "http://localhost", Token = pair!.Token, Name = "SRV-API" }, NullLogger<RelayWorker>.Instance, p.Server.CreateHandler());
        using var stop = new CancellationTokenSource();
        await worker.StartAsync(stop.Token);
        try
        {
            var broker = p.Services.GetRequiredService<RelayBroker>();
            for (var i = 0; i < 50 && !broker.Online(relayId); i++) await Task.Delay(100);
            Assert.True(broker.Online(relayId));

            DataConnection Conn(string password, string format = "json") => new()
            {
                Name = "API interne " + format, Kind = DataKinds.Api, BaseUrl = baseUrl, Auth = "login", User = "linkii", Secret = box.Protect(password),
                LoginPath = "/auth/login", LoginFormat = format, TokenPath = "data.access_token", Via = "relay", RelayId = relayId
            };

            // mauvais mot de passe : message clair, aucun jeton
            var bad = Conn("faux");
            Assert.Null(DataSourceOps.CheckConnection(bad));
            var err = await Assert.ThrowsAsync<InvalidOperationException>(() => data.Test(p.A.Id, bad));
            Assert.Contains("refusée", err.Message);

            // connexion en formulaire, puis en JSON
            Assert.StartsWith("Connexion réussie", await data.Test(p.A.Id, Conn("s3cret", "form")));
            var conn = Conn("s3cret");
            Assert.StartsWith("Connexion réussie", await data.Test(p.A.Id, conn));
            Assert.Equal(2, logins);

            var set = new DataSet { Name = "Salles", ConnectionId = conn.Id, Path = "/salles" };
            store.Write(db => { var t = db.Clients.First(c => c.Id == p.A.Id); t.DataConnections.Add(conn); t.DataSets.Add(set); });

            // le jeton obtenu au test est réutilisé : pas de nouvelle connexion
            Assert.Equal("ok", (await data.Refresh(p.A.Id, set.Id))!.Level);
            Assert.Equal(2, data.Table(p.A.Id, set.Id)!.Rows.Count);
            Assert.Equal(2, logins);

            // l'API révoque le jeton : 401, nouvelle connexion, un seul nouvel essai
            valid = "revoque";
            Assert.Equal("ok", (await data.Refresh(p.A.Id, set.Id))!.Level);
            Assert.Equal(3, logins);

            // jeton attendu dans X-Dashboard-Token, sans « Bearer »
            var other = Conn("s3cret");
            other.TokenHeader = "X-Dashboard-Token"; other.TokenPrefix = "";
            Assert.Null(DataSourceOps.CheckConnection(other));
            var people = new DataSet { Name = "Personnes", ConnectionId = other.Id, Path = "/personnes" };
            store.Write(db => { var t = db.Clients.First(c => c.Id == p.A.Id); t.DataConnections.Add(other); t.DataSets.Add(people); });
            Assert.Equal("ok", (await data.Refresh(p.A.Id, people.Id))!.Level);
            Assert.Equal("Dupont", data.Table(p.A.Id, people.Id)!.Rows[0][0]);

            // « Bearer » sans espace est complété ; un en-tête invalide est refusé
            var fix = Conn("s3cret"); fix.TokenPrefix = "Bearer";
            Assert.Null(DataSourceOps.CheckConnection(fix));
            Assert.Equal("Bearer ", fix.TokenPrefix);
            fix.TokenHeader = "Content-Type";
            Assert.Contains("en-tête", DataSourceOps.CheckConnection(fix));
        }
        finally
        {
            stop.Cancel();
            try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
            await api.StopAsync();
        }
    }

    [Fact]
    public async Task Le_relais_refuse_tout_post_hors_connexion()
    {
        var relayId = Guid.NewGuid();
        var store = p.Services.GetRequiredService<JsonStore>();
        store.Write(db => db.Clients.First(c => c.Id == p.A.Id).DataRelays.Add(new DataRelay { Id = relayId, Name = "SRV-POST", PairingCode = "POST6789", CodeUtc = DateTime.UtcNow }));
        var pair = await (await p.CreateClient().PostAsJsonAsync("/api/relay/pair", new RelayPairRequest("POST6789", null, "PC", "1.2.0"))).Content.ReadFromJsonAsync<RelayPairResponse>();
        var worker = new RelayWorker(new RelayConfig { Server = "http://localhost", Token = pair!.Token, Name = "SRV-POST" }, NullLogger<RelayWorker>.Instance, p.Server.CreateHandler());
        using var stop = new CancellationTokenSource();
        await worker.StartAsync(stop.Token);
        try
        {
            var broker = p.Services.GetRequiredService<RelayBroker>();
            for (var i = 0; i < 50 && !broker.Online(relayId); i++) await Task.Delay(100);
            var err = await Assert.ThrowsAsync<InvalidOperationException>(() => broker.Run(p.A.Id, relayId,
                new RelayJob { Kind = "http", Op = "read", Method = "POST", Url = "http://127.0.0.1:1/x", Body = "{}" }, CancellationToken.None));
            Assert.Contains("ne fait que lire", err.Message);
        }
        finally
        {
            stop.Cancel();
            try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        }
    }
}
