using Linkii.Poc;
using Xunit;

namespace Linkii.Poc.Tests;

/// <summary>Plan du bâtiment, étages dessinés (§195) : pièces, murs, passages, ouvertures collées au mur, pièce d'un appareil.</summary>
public class HaPlanRoomsTests
{
    // Salon (0,0 6×4) et cuisine (6,0 4×4) côte à côte ; chambre (0,4 6×3) sous le salon
    static readonly PlanRoom Salon = new("salon", "Salon", 0, 0, 6, 4), Cuisine = new("cuisine", "Cuisine", 6, 0, 4, 4), Chambre = new("ch", "Chambre 1", 0, 4, 6, 3);

    [Fact]
    public void Drawn_floor_is_cleaned_when_read()
    {
        var json = """
            {"floors":[{"id":"rdc","name":"RDC","kind":"rooms","w":10,"h":8,"img":"/media/calque.png","ratio":2,
                "rooms":[{"id":"salon","name":" Salon ","x":0,"y":0,"w":6,"h":4},{"id":"trop","name":"Chevauche","x":5,"y":1,"w":3,"h":2},
                         {"id":"dehors","name":"Dehors","x":8,"y":6,"w":4,"h":4},{"id":"cuisine","name":"","x":6,"y":0,"w":4,"h":4}],
                "gaps":[{"x":6,"y":1,"d":90,"len":1},{"x":6,"y":1,"d":90,"len":1},{"x":3,"y":6,"d":0,"len":1},{"x":2,"y":2,"d":45,"len":1}]}],
             "devices":[{"id":"cover.volet","floor":"rdc","x":0.1,"y":0.0,"seg":{"x":1,"y":0,"d":0,"len":2,"s":1}},
                        {"id":"light.salon","floor":"rdc","x":0.3,"y":0.3,"seg":{"x":3,"y":0,"d":0,"len":1}},
                        {"id":"binary_sensor.porte","floor":"rdc","x":0.6,"y":0.1,"seg":{"x":6,"y":1,"d":90,"len":1}}]}
            """;
        var f = HomeAssistantRules.ParsePlan(json).Floors.Single();
        Assert.True(f.Drawn);
        Assert.Equal((10, 8, 1.25), (f.W, f.H, f.Ratio));            // rapport tiré de la grille
        Assert.Equal("/media/calque.png", f.Img);                       // l'image reste, comme calque
        Assert.Equal(new[] { "salon", "cuisine" }, f.Rooms.Select(r => r.Id));   // chevauchement et sortie de grille écartés
        Assert.Equal("Salon", f.Rooms[0].Name);
        Assert.Equal("Pièce", f.Rooms[1].Name);
        Assert.Single(f.Gaps);                                          // doublon, hors mur et sens inconnu écartés

        var devs = HomeAssistantRules.ParsePlan(json).Devices;
        Assert.Equal(new PlanSeg(1, 0, 0, 2, 1), devs[0].Seg);          // volet sur le mur du haut
        Assert.Null(devs[1].Seg);                                       // une lumière n'a pas de mur
        Assert.Null(devs[2].Seg);                                       // mur déjà pris par le passage
    }

    [Fact]
    public void Image_floor_keeps_no_rooms_nor_openings()
    {
        var p = HomeAssistantRules.ParsePlan("""
            {"floors":[{"id":"a","name":"RDC","img":"/media/a.png","ratio":1.5,"rooms":[{"id":"r","name":"X","x":0,"y":0,"w":2,"h":2}]}],
             "devices":[{"id":"cover.volet","floor":"a","x":0.5,"y":0.5,"seg":{"x":0,"y":0,"d":0,"len":1}}]}
            """);
        Assert.False(p.Floors[0].Drawn);
        Assert.Empty(p.Floors[0].Rooms);
        Assert.Null(p.Devices[0].Seg);
        Assert.Equal(1.5, p.Floors[0].Ratio);
    }

    [Fact]
    public void Walls_go_round_the_rooms_minus_passages_and_openings()
    {
        var walls = PlanGeometry.Walls(new[] { Salon, Cuisine }, new[] { new PlanSeg(6, 1, 90, 1) }, new[] { new PlanSeg(1, 0, 0, 2) });
        var units = walls.SelectMany(PlanGeometry.Units).ToHashSet();
        Assert.Equal(2 * (10 + 4) + 4 - 1 - 2, units.Count);            // pourtour + mur commun compté une fois, moins passage et volet
        Assert.DoesNotContain(new PlanGeometry.Unit(false, 6, 1), units);
        Assert.Contains(new PlanGeometry.Unit(false, 6, 0), units);
        Assert.Contains(new PlanSeg(3, 0, 0, 7), walls);                // segments fusionnés
    }

    [Fact]
    public void Neighbours_get_a_passage_in_the_middle_of_their_common_wall()
    {
        Assert.Equal(new[] { new PlanSeg(6, 1, 90, 1) }, PlanGeometry.AutoGaps(Cuisine, new[] { Salon, Cuisine }, Array.Empty<PlanSeg>()));
        Assert.Equal(new[] { new PlanSeg(2, 4, 0, 1) }, PlanGeometry.AutoGaps(Chambre, new[] { Salon, Chambre }, Array.Empty<PlanSeg>()));
        // déjà un passage sur ce mur : rien de plus
        Assert.Empty(PlanGeometry.AutoGaps(Cuisine, new[] { Salon, Cuisine }, new[] { new PlanSeg(6, 3, 90, 1) }));
        // pièces qui ne se touchent pas
        Assert.Empty(PlanGeometry.AutoGaps(Cuisine, new[] { Chambre, Cuisine }, Array.Empty<PlanSeg>()));
    }

    [Fact]
    public void Openings_snap_to_the_nearest_free_wall_on_the_room_side()
    {
        var rooms = new[] { Salon, Cuisine };
        // posé juste sous le mur du haut du salon : collé au mur du haut, côté pièce (vers le bas)
        var s = PlanGeometry.Snap(rooms, Array.Empty<PlanSeg>(), 2.5, 0.4, 2)!;
        Assert.Equal((0, 0, 2, 1), (s.Y, s.D, s.Len, s.S));
        Assert.InRange(s.X, 1, 2);
        // lâché dehors, au-dessus : la pastille revient du côté de la pièce
        Assert.Equal(1, PlanGeometry.Snap(rooms, Array.Empty<PlanSeg>(), 2.5, -0.6, 1)!.S);
        // mur de gauche
        var l = PlanGeometry.Snap(rooms, Array.Empty<PlanSeg>(), 0.2, 2.2, 1)!;
        Assert.Equal((0, 90, 1), (l.X, l.D, l.S));
        // la place est prise : l'ouverture va à côté
        var taken = new[] { new PlanSeg(2, 0, 0, 1) };
        Assert.DoesNotContain(new PlanGeometry.Unit(true, 2, 0), PlanGeometry.Units(PlanGeometry.Snap(rooms, taken, 2.5, 0.2, 1)!));
        // sans pièce : rien
        Assert.Null(PlanGeometry.Snap(Array.Empty<PlanRoom>(), Array.Empty<PlanSeg>(), 1, 1, 1));
    }

    [Theory]
    [InlineData("cover", "shutter", "cover", 2)]
    [InlineData("cover", "garage", "door", 2)]
    [InlineData("binary_sensor", "window", "window", 1)]
    [InlineData("binary_sensor", "door", "door", 1)]
    [InlineData("binary_sensor", "garage_door", "door", 2)]
    [InlineData("binary_sensor", "motion", null, 1)]
    [InlineData("light", "", null, 1)]
    public void Opening_kind_and_width(string domain, string dc, string? kind, int len)
    {
        Assert.Equal(kind, PlanGeometry.OpeningKind(domain, dc));
        if (kind != null) Assert.Equal(len, PlanGeometry.OpeningLen(domain, dc));
    }

    [Theory]
    [InlineData("light.chambre_1", "Plafonnier", 2)]
    [InlineData("light.plafonnier", "Lampe Chambre 1", 2)]
    [InlineData("switch.prise_cuisine", "Prise", 1)]
    [InlineData("sensor.temperature_salle_de_bain", "Température", 3)]
    [InlineData("light.salle", "Salle", -1)]          // « salle » seul ne désigne pas « Salle de bain »
    [InlineData("light.couloir", "Couloir", -1)]
    public void Devices_find_their_room_by_name(string id, string name, int expected) =>
        Assert.Equal(expected, PlanGeometry.MatchRoom(id, name, new[] { "Salon", "Cuisine", "Chambre 1", "Salle de bain" }));

    [Fact]
    public void Room_of_a_device_and_saved_plan_round_trip()
    {
        var json = """
            {"floors":[{"id":"rdc","name":"RDC","kind":"rooms","w":10,"h":8,
                "rooms":[{"id":"salon","name":"Salon","x":0,"y":0,"w":6,"h":4},{"id":"cuisine","name":"Cuisine","x":6,"y":0,"w":4,"h":4}],
                "gaps":[{"x":6,"y":1,"d":90,"len":1}]}],
             "devices":[{"id":"light.cuisine","floor":"rdc","x":0.8,"y":0.25},{"id":"cover.volet","floor":"rdc","x":0.15,"y":0.05,"seg":{"x":1,"y":0,"d":0,"len":2,"s":1}}]}
            """;
        var (clean, ents) = HomeAssistantRules.NormalizePlan(json);
        Assert.Equal("light.cuisine,cover.volet", ents);
        var p = HomeAssistantRules.ParsePlan(clean);
        var f = p.Floors[0];
        Assert.Equal(2, f.Rooms.Count);
        Assert.Single(f.Gaps);
        Assert.Equal("cuisine", HomeAssistantRules.RoomOf(f, p.Devices[0])?.Id);
        Assert.Equal(new PlanSeg(1, 0, 0, 2, 1), p.Devices[1].Seg);
        Assert.DoesNotContain(HomeAssistantRules.WallsOf(f, p.Devices).SelectMany(PlanGeometry.Units), u => u == new PlanGeometry.Unit(true, 1, 0));
    }
}
