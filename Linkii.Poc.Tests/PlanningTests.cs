using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Créneaux du planning (outils communs aux calendriers et au planning des murs). Les calendriers des Programmations : voir CalendarsTests.</summary>
public class PlanningTests
{
    readonly Db db = new();
    readonly Tenant client = new() { Name = "Les Deux Rives", Timezone = "UTC" };
    readonly Area lac;
    readonly Zone entree, stand;
    readonly Playlist menu, pub, soir;
    readonly User admin;

    public PlanningTests()
    {
        db.Clients.Add(client);
        lac = new Area { ClientId = client.Id, Name = "Restaurant Lac" };
        AreaOps.EnsureDefaultZone(lac);
        entree = new Zone { Name = "Entrée" }; stand = new Zone { Name = "Stand service" };
        lac.Zones.Add(entree); lac.Zones.Add(stand);
        db.Areas.Add(lac);
        admin = new User { Email = "chef@deuxrives.ch", Role = Roles.ClientAdmin, ClientId = client.Id };
        db.Users.Add(admin);
        menu = Pl("Menu + prix"); pub = Pl("Menu + pub"); soir = Pl("Carte du soir");
    }

    Playlist Pl(string name) { var p = new Playlist { ClientId = client.Id, AreaId = lac.Id, Name = name }; db.Playlists.Add(p); return p; }
    Screen Sc(string name, Zone z) { var s = new Screen { ClientId = client.Id, AreaId = lac.Id, ZoneId = z.Id, Name = name, Token = Guid.NewGuid().ToString("N") }; db.Screens.Add(s); return s; }
    ClientDb As() => new(db, client.Id, AreaAccess.For(db, client.Id, admin.Id));
    static Slot Slot(Playlist p, int days, int from, int to, string? d1 = null, string? d2 = null) => new() { PlaylistId = p.Id, Days = days, From = from, To = to, DateFrom = d1, DateTo = d2 };
    const int Weekdays = 0b0011111, Weekend = 0b1100000;
    static readonly DateOnly Wed = new(2026, 10, 14);   // un mercredi

    [Fact]
    public void A_slot_matches_its_days_and_hours_only()
    {
        var s = Slot(menu, Weekdays, 11 * 60, 14 * 60);
        Assert.True(Planning.Matches(s, Wed, 11 * 60));
        Assert.True(Planning.Matches(s, Wed, 14 * 60 - 1));
        Assert.False(Planning.Matches(s, Wed, 14 * 60));          // la fin est exclue
        Assert.False(Planning.Matches(s, Wed, 10 * 60 + 59));
        Assert.False(Planning.Matches(s, new DateOnly(2026, 10, 17), 12 * 60));   // samedi
    }

    [Fact]
    public void A_dated_slot_only_matches_inside_its_period_both_ends_included()
    {
        var s = Slot(pub, 127, 0, Planning.Day, "2026-10-14", "2026-10-16");
        Assert.False(Planning.Matches(s, new DateOnly(2026, 10, 13), 600));
        Assert.True(Planning.Matches(s, new DateOnly(2026, 10, 14), 600));
        Assert.True(Planning.Matches(s, new DateOnly(2026, 10, 16), 600));
        Assert.False(Planning.Matches(s, new DateOnly(2026, 10, 17), 600));
    }

    [Fact]
    public void Slot_validation_rejects_empty_or_inverted_input()
    {
        Assert.NotNull(Planning.Check(Slot(menu, 0, 600, 700)));            // aucun jour
        Assert.NotNull(Planning.Check(Slot(menu, 1, 700, 600)));            // fin avant début
        Assert.NotNull(Planning.Check(Slot(menu, 1, 600, 600)));
        Assert.NotNull(Planning.Check(Slot(menu, 1, 600, 700, "2026-10-20", "2026-10-10")));
        Assert.Null(Planning.Check(Slot(menu, 1, 0, Planning.Day)));        // jusqu'à minuit
    }

    [Fact]
    public void Saving_validates_the_list_and_copies_to_other_zones()
    {
        var d = As();
        var slot = Slot(menu, Weekdays, 11 * 60, 14 * 60);
        Assert.Null(PlanOps.Save(d, lac.Id, stand.Id, false, slot, new[] { entree.Id, stand.Id }));
        Assert.Single(stand.Slots);
        var copy = Assert.Single(entree.Slots);
        Assert.NotEqual(slot.Id, copy.Id);   // copie indépendante

        Assert.NotNull(PlanOps.Save(d, lac.Id, stand.Id, false, Slot(menu, 0, 600, 700)));   // aucun jour
        Assert.NotNull(PlanOps.Save(d, lac.Id, stand.Id, false, new Slot { PlaylistId = Guid.NewGuid(), Days = 1, From = 600, To = 700 }));   // liste inconnue

        slot.To = 15 * 60;   // modification : même Id, pas de doublon
        Assert.Null(PlanOps.Save(d, lac.Id, stand.Id, false, slot));
        Assert.Single(stand.Slots);
        Assert.Equal(15 * 60, stand.Slots[0].To);
    }

    [Fact]
    public void A_screen_needs_its_own_planning_before_it_gets_slots_and_can_go_back_to_its_zone()
    {
        var d = As();
        var s = Sc("Grill", stand);
        Assert.NotNull(PlanOps.Save(d, lac.Id, s.Id, true, Slot(soir, 1, 600, 700)));   // suit encore sa zone
        Assert.Null(PlanOps.CreateOwn(d, lac.Id, s.Id));
        Assert.Null(PlanOps.Save(d, lac.Id, s.Id, true, Slot(soir, 1, 600, 700)));
        Assert.Single(s.Slots);
        Assert.Null(PlanOps.RevertToZone(d, lac.Id, s.Id));
        Assert.False(s.OwnPlanning);
        Assert.Empty(s.Slots);
    }

    [Fact]
    public void Next_finds_the_following_slot_today_or_on_a_later_day()
    {
        var slots = new List<Slot> { Slot(menu, Weekdays, 11 * 60, 14 * 60), Slot(soir, Weekdays, 18 * 60, 22 * 60) };
        var (s1, d1) = Planning.Next(slots, Wed, 9 * 60)!.Value;
        Assert.Equal((menu.Id, Wed), (s1.PlaylistId, d1));                       // avant le premier créneau
        var (s2, d2) = Planning.Next(slots, Wed, 15 * 60)!.Value;
        Assert.Equal((soir.Id, Wed), (s2.PlaylistId, d2));                       // entre les deux
        var (s3, d3) = Planning.Next(slots, Wed, 23 * 60)!.Value;
        Assert.Equal((menu.Id, Wed.AddDays(1)), (s3.PlaylistId, d3));            // le lendemain
        var friday = new DateOnly(2026, 10, 16);
        Assert.Equal(new DateOnly(2026, 10, 19), Planning.Next(slots, friday, 23 * 60)!.Value.Date);   // le week-end n'a pas de créneau : lundi
        Assert.Null(Planning.Next(new List<Slot>(), Wed, 0));
    }

    [Fact]
    public void Creating_and_removing_a_zone_or_area_planning_goes_back_to_the_level_above()
    {
        var d = As();
        Assert.False(Planning.HasOwn(stand));
        Assert.Null(PlanOps.CreateFor(d, lac.Id, stand.Id));
        Assert.True(Planning.HasOwn(stand));   // vide mais créé
        Assert.Null(PlanOps.Save(d, lac.Id, stand.Id, false, Slot(menu, 1, 600, 700)));
        Assert.Null(PlanOps.RemoveFor(d, lac.Id, stand.Id));
        Assert.False(Planning.HasOwn(stand));
        Assert.Empty(stand.Slots);
        Assert.NotNull(PlanOps.CreateFor(d, lac.Id, Guid.NewGuid()));   // cible inconnue

        Assert.Null(PlanOps.CreateFor(d, lac.Id, lac.Id));
        Assert.Null(PlanOps.RemoveFor(d, lac.Id, lac.Id));
        Assert.False(Planning.HasOwn(lac));
    }

    [Fact]
    public void A_wall_screen_cannot_get_its_own_planning()
    {
        var wall = new Zone { Name = "Mur hall", Kind = Zone.Wall, WallCols = 2, WallRows = 1 };
        lac.Zones.Add(wall);
        var s = Sc("Mur 1", wall);
        Assert.NotNull(PlanOps.CreateOwn(As(), lac.Id, s.Id));
        Assert.False(s.OwnPlanning);
    }

    [Fact]
    public void Editing_a_scheduled_list_after_its_snapshot_makes_the_screen_pending()
    {
        var s = Sc("Grill", stand);
        s.PublishedStamp = Helpers.PublishStamp(s, db.Playlists);
        Assert.False(Helpers.HasPending(s, db.Playlists));
        s.PublishedListRevs[menu.Id] = menu.Revision;
        Assert.False(Helpers.HasPending(s, db.Playlists));
        menu.Revision++;
        Assert.True(Helpers.HasPending(s, db.Playlists));
    }

    [Fact]
    public void Moving_a_screen_to_another_area_keeps_its_own_planning()
    {
        var other = new Area { ClientId = client.Id, Name = "Restaurant Gare" };
        AreaOps.EnsureDefaultZone(other);
        db.Areas.Add(other);
        var s = Sc("Grill", stand);
        s.OwnPlanning = true; s.Slots.Add(Slot(soir, 1, 600, 700));

        Assert.Null(AreaOps.MoveScreen(As(), s.Id, other.Id));   // les listes n'ont plus d'aire (24 octobre 2026) : ses créneaux restent valables
        Assert.True(s.OwnPlanning);
        Assert.Equal(soir.Id, Assert.Single(s.Slots).PlaylistId);
    }

    [Fact]
    public void Segments_follow_priorities_across_the_day()
    {
        var base1 = Slot(menu, 127, 11 * 60, 14 * 60);
        var over = Slot(pub, 127, 12 * 60, 13 * 60, "2026-10-14", "2026-10-14");
        var seg = Planning.Segments(Planning.Order(new[] { base1, over }), Wed);
        Assert.Equal(new[] { (0, 660, (Slot?)null), (660, 720, base1), (720, 780, over), (780, 840, base1), (840, 1440, null) }, seg.Select(x => (x.From, x.To, x.Slot)));
    }

    [Fact]
    public void Another_areas_playlist_cannot_be_scheduled()
    {
        var other = new Area { ClientId = client.Id, Name = "Restaurant Gare" };
        db.Areas.Add(other);
        var alien = new Playlist { ClientId = client.Id, AreaId = other.Id, Name = "Gare" };
        db.Playlists.Add(alien);
        Assert.NotNull(PlanOps.Save(As(), lac.Id, stand.Id, false, Slot(alien, 1, 600, 700)));
    }

    [Fact]
    public void Affected_screens_are_those_of_the_zone_or_the_screen_itself()
    {
        var a = Sc("A", stand); var b = Sc("B", stand); var c = Sc("C", entree);
        var d = As();
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x), Planning.Affected(d, lac.Id, stand.Id, null).Select(s => s.Id).OrderBy(x => x));
        Assert.Equal(c.Id, Assert.Single(Planning.Affected(d, lac.Id, null, c.Id)).Id);
    }

    [Fact]
    public void Descriptions_are_readable()
    {
        Assert.Equal("Lun – Ven", Planning.DaysText(Weekdays));
        Assert.Equal("Sam, Dim", Planning.DaysText(Weekend));
        Assert.Equal("Tous les jours", Planning.DaysText(127));
        Assert.Equal("Lun, Mer, Sam", Planning.DaysText(0b0100101));
        Assert.Equal("Lun – Ven · 11:00 – 14:00", Planning.Describe(Slot(menu, Weekdays, 660, 840)));
    }
}
