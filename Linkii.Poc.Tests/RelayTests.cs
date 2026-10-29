extern alias relay;
using System.Net;
using System.Net.Http.Json;
using Linkii.Poc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RelayConfig = relay::Linkii.Relay.RelayConfig;
using RelayWorker = relay::Linkii.Relay.RelayWorker;

namespace Linkii.Poc.Tests;

/// <summary>Bases de données (§139, lot 3) et relais Linkii (lot 4) : requêtes en lecture seule, appairage, tâches réservées au relais de l'organisation.</summary>
[Collection("platform")]
public class RelayTests(Platform p)
{
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
}
