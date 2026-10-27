using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Publicité (§45) : validation des spots par le propriétaire, écrans concernés, format du visuel, contrôle et journal des passages.</summary>
public class AdsTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc);

    private static (Db Db, Tenant T, Screen S, AdCampaign C, AdSpot Spot) World(string category = "food", bool growth = true, bool on = true)
    {
        var t = new Tenant { Name = "UNIL", Plan = growth ? Plans.Growth : Plans.Base, AdsOn = on, AdsSinceUtc = Now.AddDays(-10) };
        var area = new Area { ClientId = t.Id, Name = "Hall" };
        var s = new Screen { ClientId = t.Id, AreaId = area.Id, Name = "Hall A", Token = "tok", Orientation = "landscape" };
        var spot = new AdSpot { Name = "Menu", DurationSec = 15, Landscape = "a.jpg", LandscapeType = "image", CreatedUtc = Now.AddHours(-1) };
        var c = new AdCampaign { Advertiser = "Café des Arts", Category = category, Start = "2026-10-01", End = "2026-11-30", Spots = { spot } };
        var db = new Db { Clients = { t }, Areas = { area }, Screens = { s }, AdCampaigns = { c } };
        return (db, t, s, c, spot);
    }

    [Fact]
    public void Un_nouveau_spot_est_a_valider_puis_diffuse_sans_reponse_a_l_echeance()
    {
        var (db, t, s, _, spot) = World();
        Assert.Equal(1, Ads.PendingCount(db, t, Now));
        Assert.Empty(Ads.Playable(db, t, s, Now));   // pas encore de réponse, échéance non passée : rien à l'écran

        var later = spot.CreatedUtc.AddHours(Ads.DefaultValidationHours).AddMinutes(1);
        Assert.Equal(0, Ads.PendingCount(db, t, later));
        Assert.Equal(AdSpotState.Auto, Ads.States(db, t, later).Single().State);
        Assert.Single(Ads.Playable(db, t, s, later));   // le silence vaut acceptation
    }

    [Fact]
    public void Refuser_ou_exclure_la_categorie_retire_le_spot()
    {
        var (db, t, s, _, spot) = World();
        db.AdDecisions.Add(new AdDecision { ClientId = t.Id, SpotId = spot.Id, Accepted = false });
        Assert.Equal(AdSpotState.Refused, Ads.States(db, t, Now.AddDays(10)).Single().State);
        Assert.Empty(Ads.Playable(db, t, s, Now.AddDays(10)));

        var (db2, t2, s2, _, _) = World("alcohol");   // catégorie refusée par défaut
        Assert.Equal(0, Ads.PendingCount(db2, t2, Now));
        Assert.Empty(Ads.Playable(db2, t2, s2, Now.AddDays(10)));
    }

    [Fact]
    public void Accepte_le_spot_part_tout_de_suite()
    {
        var (db, t, s, _, spot) = World();
        db.AdDecisions.Add(new AdDecision { ClientId = t.Id, SpotId = spot.Id, Accepted = true });
        Assert.Single(Ads.Playable(db, t, s, Now));
        Assert.NotEqual("", Ads.Stamp(db, t, s, Now));
    }

    [Fact]
    public void Pas_de_pub_sans_licence_Pro_ni_reglage_ni_sur_un_ecran_decoupe()
    {
        var (db, t, s, _, spot) = World(growth: false);
        db.AdDecisions.Add(new AdDecision { ClientId = t.Id, SpotId = spot.Id, Accepted = true });
        Assert.Null(Ads.ForPlayer(db, t, s, Now));

        var (db2, t2, s2, _, spot2) = World(on: false);
        db2.AdDecisions.Add(new AdDecision { ClientId = t2.Id, SpotId = spot2.Id, Accepted = true });
        Assert.Null(Ads.ForPlayer(db2, t2, s2, Now));

        var (db3, t3, s3, _, spot3) = World();
        db3.AdDecisions.Add(new AdDecision { ClientId = t3.Id, SpotId = spot3.Id, Accepted = true });
        s3.Layout = ScreenLayouts.All[1].Id;   // découpé en zones
        Assert.Null(Ads.ForPlayer(db3, t3, s3, Now));
        s3.Layout = ScreenLayouts.Full;
        t3.AdsScreensOff.Add(s3.Id);   // écran retiré par le propriétaire
        Assert.Null(Ads.ForPlayer(db3, t3, s3, Now));
    }

    [Fact]
    public void Ecran_portrait_sans_visuel_portrait_prend_le_paysage_sur_fond_flou()
    {
        var (_, _, s, _, spot) = World();
        s.Orientation = "portrait";
        var v = Ads.Visual(spot, s)!.Value;
        Assert.Equal("landscape", v.Format);
        Assert.True(v.Approx);
        spot.Portrait = "p.mp4"; spot.PortraitType = "video";
        v = Ads.Visual(spot, s)!.Value;
        Assert.Equal(("portrait", "video", false), (v.Format, v.Type, v.Approx));
    }

    [Fact]
    public void Plafond_de_la_campagne_atteint_le_spot_quitte_les_ecrans()
    {
        var (db, t, s, c, spot) = World();
        db.AdDecisions.Add(new AdDecision { ClientId = t.Id, SpotId = spot.Id, Accepted = true });
        c.MaxPlays = 10;
        var old = Ads.TotalPlays;
        try
        {
            Ads.TotalPlays = _ => 10;
            Assert.Empty(Ads.Playable(db, t, s, Now));
        }
        finally { Ads.TotalPlays = old; }
    }

    [Fact]
    public void Controle_des_passages_complets_interrompus_et_invraisemblables()
    {
        var (_, _, s, c, spot) = World();
        long Ms(DateTime d) => new DateTimeOffset(d).ToUnixTimeMilliseconds();
        var batch = new[]
        {
            new AdPlayDto(Guid.NewGuid(), c.Id, spot.Id, Ms(Now.AddMinutes(-10)), 15000, true, null, "landscape", false, false),       // complet
            new AdPlayDto(Guid.NewGuid(), c.Id, spot.Id, Ms(Now.AddMinutes(-10).AddSeconds(5)), 15000, true, null, "landscape", false, false),   // chevauche le précédent : écarté
            new AdPlayDto(Guid.NewGuid(), c.Id, spot.Id, Ms(Now.AddMinutes(-5)), 9000, true, null, "landscape", false, false),        // annoncé complet mais trop court
            new AdPlayDto(Guid.NewGuid(), c.Id, spot.Id, Ms(Now.AddMinutes(-3)), 4000, false, "sleep", "landscape", false, true),     // interrompu par la veille
            new AdPlayDto(Guid.NewGuid(), c.Id, Guid.NewGuid(), Ms(Now.AddMinutes(-2)), 15000, true, null, "landscape", false, false), // spot inconnu
            new AdPlayDto(Guid.NewGuid(), c.Id, spot.Id, Ms(Now.AddHours(1)), 15000, true, null, "landscape", false, false),          // dans le futur
        };
        var ok = AdLog.Check(batch, s.Id, new[] { c }, Array.Empty<AdPlay>(), Now);
        Assert.Equal(3, ok.Count);
        Assert.True(ok[0].Complete);
        Assert.False(ok[1].Complete); Assert.Equal("cut", ok[1].Cause);
        Assert.False(ok[2].Complete); Assert.Equal("sleep", ok[2].Cause); Assert.True(ok[2].Offline);
    }

    [Fact]
    public void Journal_dedoublonne_et_compte_les_passages_complets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "linkii-ads-" + Guid.NewGuid().ToString("N"));
        var old = Ads.TotalPlays;
        try
        {
            var log = new AdLog(dir);
            var client = Guid.NewGuid(); var camp = Guid.NewGuid();
            var p = new AdPlay(Guid.NewGuid(), DateTime.UtcNow, Guid.NewGuid(), camp, Guid.NewGuid(), 15000, 15000, true, null, "landscape", false, false);
            Assert.Single(log.Append(client, new[] { p }));
            Assert.Empty(log.Append(client, new[] { p }));   // lot renvoyé après une réponse perdue : compté une seule fois
            Assert.Single(log.Read(client, DateTime.MinValue));
            Assert.Equal(1, log.Total(camp));
            Assert.Equal(1, new AdLog(dir).Total(camp));   // relu depuis le disque au démarrage
        }
        finally { Ads.TotalPlays = old; Directory.Delete(dir, true); }
    }

    [Fact]
    public void Releve_montants_par_campagne_et_empreinte_stable()
    {
        var (_, _, s, c, spot) = World();
        c.PricePerPlay = 0.03m; c.OwnerSharePct = 50;
        var plays = Enumerable.Range(0, 4).Select(i => new AdPlay(Guid.NewGuid(), Now.AddMinutes(i), s.Id, c.Id, spot.Id, 15000, i == 3 ? 3000 : 15000, i != 3, i == 3 ? "pause" : null, "landscape", false, false)).ToList();
        var row = AdReports.Rows(plays, new[] { c }).Single();
        Assert.Equal((3, 1, 45L), (row.Complete, row.Interrupted, row.Seconds));
        Assert.Equal(0.09m, row.Gross);
        Assert.Equal(0.045m, row.Owner);
        Assert.Equal(AdReports.Fingerprint(plays), AdReports.Fingerprint(plays.AsEnumerable().Reverse()));
    }
}
