using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Core.Json;
using Ubl.Engine.Core.Toilet;
using Xunit;

namespace Ubl.Engine.Core.Tests;

public class ToiletGoldenTests
{
    private static readonly ToiletRules R = ToiletRules.Default;

    /// <summary>Manawala male toilet (AR-141): 5'-0" x 6'-3" inside, door at the corner of the short wall.</summary>
    public static ToiletSpec ManawalaMale => new()
    {
        Name = "MALE TOILET", Width = 60, Depth = 75,
        Door = new DoorSpec { Wall = Side.South, FromCorner = DoorCorner.End, Offset = 4.5, Width = 30, SwingIn = true },
    };

    [Fact]
    public void Manawala_male_puts_WC_and_basin_on_one_wet_wall_as_drawn()
    {
        var p = new ToiletPlanner().Plan(ManawalaMale);
        Assert.False(p.Refused, p.RefusalReason);
        var wc = p.Item(Roles.Wc)!;
        var basin = p.Item(Roles.Basin)!;
        Assert.Equal(Side.West, wc.Wall);
        Assert.Equal(Side.West, basin.Wall);
        Assert.Equal(18.0, wc.S, 6);                       // 1'-6" from the north (back) wall
        Assert.Equal(75 - 18.0, basin.S, 6);               // 1'-6" from the south (front) wall
        Assert.Equal(39.0, Math.Abs(basin.S - wc.S), 6);   // 3'-3" apart, as on AR-141
        var mirror = p.Item(Roles.Mirror)!;
        Assert.Equal(basin.S, mirror.S, 6);                // centred over the basin
        Assert.Empty(ToiletChecker.Check(p, R, ToiletPlanner.RequiredRoles(p.Spec)));
    }

    [Fact]
    public void Manawala_female_is_the_mirror_image()
    {
        var spec = ManawalaMale with { Name = "FEMALE TOILET", Door = ManawalaMale.Door with { FromCorner = DoorCorner.Start } };
        var p = new ToiletPlanner().Plan(spec);
        Assert.False(p.Refused, p.RefusalReason);
        Assert.Equal(Side.East, p.Item(Roles.Wc)!.Wall);
        Assert.Equal(Side.East, p.Item(Roles.Basin)!.Wall);
        var male = new ToiletPlanner().Plan(ManawalaMale);
        foreach (var role in new[] { Roles.Wc, Roles.Basin, Roles.Mirror, Roles.TissueHolder })
        {
            var a = male.Item(role)!.Plan;
            var b = p.Item(role)!.Plan;
            Assert.Equal(a.X0, 60 - b.X1, 6);              // reflected about the room's centre-line
            Assert.Equal(a.Y0, b.Y0, 6);
        }
    }

    /// <summary>New-branch toilet 1: 7'-11" x 5'-0", vanity in a corner, WC on the short wall opposite the door.
    /// The wet wall (east) is our reading of the drawing; to be confirmed when the room is read from the DWG.</summary>
    [Fact]
    public void New_branch_toilet_1_puts_the_WC_opposite_the_door()
    {
        var spec = new ToiletSpec
        {
            Width = 95, Depth = 60, Basin = BasinKind.Vanity, WetWalls = new() { Side.East },
            Door = new DoorSpec { Wall = Side.West, FromCorner = DoorCorner.End, Offset = 4.5 },
        };
        var p = new ToiletPlanner().Plan(spec);
        Assert.False(p.Refused, p.RefusalReason);
        var wc = p.Item(Roles.Wc)!;
        Assert.Equal(Side.East, wc.Wall);
        Assert.Equal(18.0, Math.Min(wc.S, 60 - wc.S), 6);
        var vanity = p.Item(Roles.Vanity)!;
        Assert.Equal(18.0, Math.Min(vanity.S, RoomGeometry.Wall(spec, vanity.Wall!.Value).Length - vanity.S), 6);   // in a corner
        Assert.Equal(vanity.S, p.Item(Roles.Basin)!.S, 6);
        Assert.Empty(ToiletChecker.Check(p, R, ToiletPlanner.RequiredRoles(p.Spec)));
    }

    /// <summary>New-branch toilet 2: same size, WC on the door wall (wet wall there).</summary>
    [Fact]
    public void New_branch_toilet_2_puts_the_WC_on_the_door_wall()
    {
        var spec = new ToiletSpec
        {
            Width = 95, Depth = 60, Basin = BasinKind.Vanity, WetWalls = new() { Side.South },
            Door = new DoorSpec { Wall = Side.South, FromCorner = DoorCorner.Start, Offset = 4.5 },
        };
        var p = new ToiletPlanner().Plan(spec);
        Assert.False(p.Refused, p.RefusalReason);
        Assert.Equal(Side.South, p.Item(Roles.Wc)!.Wall);
        Assert.Empty(ToiletChecker.Check(p, R, ToiletPlanner.RequiredRoles(p.Spec)));
    }
}

public class ToiletRuleTests
{
    private static readonly ToiletRules R = ToiletRules.Default;

    [Fact]
    public void A_room_too_small_is_refused_with_the_rule_and_the_size_that_would_pass()
    {
        var p = new ToiletPlanner().Plan(new ToiletSpec { Width = 48, Depth = 48 });
        Assert.True(p.Refused);
        Assert.NotNull(p.RefusalRule);
        Assert.Contains("would pass", p.WouldPass);
        Assert.Empty(p.Items);
    }

    [Fact]
    public void A_door_that_does_not_fit_its_wall_is_refused()
    {
        var p = new ToiletPlanner().Plan(new ToiletSpec { Width = 30, Depth = 75, Door = new DoorSpec { Wall = Side.South, Offset = 4.5, Width = 30 } });
        Assert.True(p.Refused);
        Assert.Equal("door", p.RefusalRule);
    }

    [Fact]
    public void Same_input_gives_byte_identical_output()
    {
        var a = EngineJson.Serialize(new ToiletPlanner().Plan(ToiletGoldenTests.ManawalaMale));
        var b = EngineJson.Serialize(new ToiletPlanner().Plan(ToiletGoldenTests.ManawalaMale));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Pinning_the_WC_is_respected_or_refused_with_the_broken_rule()
    {
        var pinned = ToiletGoldenTests.ManawalaMale with { PinWc = new FixturePin(Side.North, DoorCorner.End) };
        var p = new ToiletPlanner().Plan(pinned);
        Assert.False(p.Refused, p.RefusalReason);
        Assert.Equal(Side.North, p.Item(Roles.Wc)!.Wall);

        var bad = ToiletGoldenTests.ManawalaMale with { PinWc = new FixturePin(Side.South, DoorCorner.End) };   // right in the doorway
        var q = new ToiletPlanner().Plan(bad);
        Assert.True(q.Refused);
        Assert.Contains("door", q.RefusalReason);
    }

    [Fact]
    public void Real_family_sizes_change_the_layout_not_the_rules()
    {
        // a wider WC no longer leaves room for the tissue holder beside the tank: it moves to the side wall
        var cat = FixtureCatalog.Defaults().With(Roles.Wc, new FixtureSize(20, 28, 31, Mount.Floor));
        var p = new ToiletPlanner(cat).Plan(ToiletGoldenTests.ManawalaMale);
        Assert.False(p.Refused, p.RefusalReason);
        var wc = p.Item(Roles.Wc)!;
        Assert.Equal(18.0, Math.Min(wc.S, 75 - wc.S), 6);
        Assert.NotEqual(wc.Wall, p.Item(Roles.TissueHolder)!.Wall);
        Assert.Empty(ToiletChecker.Check(p, R, ToiletPlanner.RequiredRoles(p.Spec)));
    }

    [Fact]
    public void Door_swing_is_a_quarter_circle_not_a_square()
    {
        var spec = new ToiletSpec { Width = 60, Depth = 75 };
        var door = RoomGeometry.Door(spec)!;          // south wall, opening 25.5..55.5, hinge at 55.5
        Assert.Equal(55.5, door.HingeS, 6);
        Assert.True(RoomGeometry.HitsSwing(spec, door, new Box(40, 5, 45, 10)));
        Assert.False(RoomGeometry.HitsSwing(spec, door, new Box(25.5, 28, 28, 30)));   // corner of the square outside the arc
        Assert.False(RoomGeometry.HitsSwing(spec, door, new Box(0, 0, 25.5, 30)));     // beside the opening
        Assert.False(RoomGeometry.HitsSwing(spec with { Door = spec.Door with { SwingIn = false } }, door with { SwingIn = false }, new Box(40, 5, 45, 10)));
    }

    [Fact]
    public void Reads_a_toilet_json()
    {
        var spec = ToiletInput.Parse("""
            { "name": "MALE TOILET", "width": "5'-0\"", "depth": "6'-3\"", "walls": 4.5, "ceiling_height": "7'-2\"",
              "door": { "wall": "south", "from_corner": "east", "offset": 4.5, "width": "2'-6\"", "swing": "in" },
              "basin": "half_pedestal", "hand_drying": "towel_dispenser", "wet_walls": ["west"],
              "pin_wc": { "wall": "west", "corner": "north" } }
            """);
        Assert.Equal(60.0, spec.Width, 6);
        Assert.Equal(75.0, spec.Depth, 6);
        Assert.Equal(86.0, spec.CeilingHeight, 6);
        Assert.Equal(DoorCorner.End, spec.Door.FromCorner);
        Assert.Equal(HandDrying.TowelDispenser, spec.HandDrying);
        Assert.Equal(new FixturePin(Side.West, DoorCorner.Start), spec.PinWc);
        Assert.Single(spec.WetWalls);
    }

    /// <summary>
    /// A coarse version of the full sweep (the CLI runs all 85,264 cases): 4'-0" to 10'-0" in 6" steps, door on every
    /// wall, both ends, in and out. Each case must be a toilet that passes every rule, or a refusal naming a rule.
    /// </summary>
    [Fact]
    public void Coarse_sweep_every_case_is_valid_or_refused_with_a_rule()
    {
        var planner = new ToiletPlanner();
        int valid = 0, refused = 0;
        for (double w = 48; w <= 120; w += 6)
            for (double d = 48; d <= 120; d += 6)
                foreach (var side in RoomGeometry.Sides)
                    foreach (var corner in new[] { DoorCorner.Start, DoorCorner.End })
                        foreach (bool swingIn in new[] { true, false })
                        {
                            var spec = new ToiletSpec { Width = w, Depth = d, Door = new DoorSpec { Wall = side, FromCorner = corner, SwingIn = swingIn } };
                            var p = planner.Plan(spec, explainRefusal: false);
                            if (p.Refused) { refused++; Assert.False(string.IsNullOrEmpty(p.RefusalRule)); continue; }
                            valid++;
                            var v = ToiletChecker.Check(p, R, ToiletPlanner.RequiredRoles(spec));
                            Assert.True(v.Count == 0, $"{w}x{d} door {side}/{corner}/{swingIn}: {string.Join("; ", v)}");
                            Assert.All(p.Items.Where(i => i.Wall is not null), i => Assert.True(Math.Abs(RoomGeometry.Wall(spec, i.Wall!.Value).DOf(i.Anchor) - i.BackOffset) < 1e-6));
                        }
        Assert.True(valid > refused, $"{valid} valid, {refused} refused");
    }
}

/// <summary>
/// The placement arithmetic the Revit add-in uses: for every front convention, every wall and random family
/// extents (including insertion points well off the object), the placed family must cover exactly the rectangle
/// the planner reserved, face the room, and sit at the planned height.
/// </summary>
public class PlacementMathTests
{
    [Fact]
    public void Measured_family_lands_exactly_on_its_planned_rectangle()
    {
        var rnd = new Random(11);
        var spec = new ToiletSpec { Width = 95, Depth = 75 };
        int n = 0;
        foreach (var front in new[] { "-Y", "+Y", "-X", "+X" })
            foreach (var side in RoomGeometry.Sides)
                for (int k = 0; k < 50; k++)
                {
                    var f = PlacementMath.Front(front);
                    double x0 = rnd.NextDouble() * 40 - 30, y0 = rnd.NextDouble() * 40 - 30, z0 = rnd.NextDouble() * 4 - 2;
                    var m = new PlacementMath.Extents(x0, x0 + 3 + rnd.NextDouble() * 30, y0, y0 + 3 + rnd.NextDouble() * 30, z0, z0 + 2 + rnd.NextDouble() * 30);
                    var (along, outward) = PlacementMath.PlanSize(m, f);
                    var wl = RoomGeometry.Wall(spec, side);
                    double s = along / 2 + rnd.NextDouble() * (wl.Length - along);
                    double bottom = rnd.NextDouble() * 48;
                    var item = ToiletPlanner.WallItem(Roles.Dryer, wl, s, 0, new FixtureSize(along, outward, m.SizeZ, Mount.Wall), bottom, "test");

                    var (theta, insert, insertZ) = PlacementMath.Solve(m, f, backed: true, item.Anchor, item.Facing, item.Z0);
                    var placed = PlacementMath.PlacedPlan(m, insert, theta);
                    Assert.True(placed.SameAs(item.Plan, 1e-9), $"{front} on {side}: placed {placed}, planned {item.Plan}");
                    var faced = PlacementMath.Rotate(f, theta);
                    Assert.True(Pt.Dist(faced, item.Facing) < 1e-9, $"{front} on {side}: faces {faced}, should face {item.Facing}");
                    Assert.Equal(item.Z0, insertZ + m.Z0, 9);
                    n++;
                }
        Assert.Equal(800, n);
    }

    [Fact]
    public void Free_items_are_centred_and_keep_the_room_axes()
    {
        var m = new PlacementMath.Extents(-2, 10, -7, 1, 0, 3);    // insertion point far off centre
        foreach (var front in new[] { "-Y", "+Y", "-X", "+X" })
        {
            var f = PlacementMath.Front(front);
            var (along, outward) = PlacementMath.PlanSize(m, f);
            var item = ToiletPlanner.FreeItem(Roles.CeilingLight, Mount.Ceiling, new Pt(30, 40), new FixtureSize(along, outward, 3, Mount.Ceiling), 81, "test");
            var (theta, insert, _) = PlacementMath.Solve(m, f, backed: false, item.Anchor, item.Facing, item.Z0);
            Assert.True(PlacementMath.PlacedPlan(m, insert, theta).SameAs(item.Plan, 1e-9), front);
        }
    }
}

/// <summary>
/// The client's 12 bathroom families as Revit measured them on 31 Oct (Test Families report): body sizes in their
/// own axes, mapped through the fronts in toilet.families.json. Both reference toilets must still plan and pass.
/// </summary>
public class ClientFamilyTests
{
    public static FixtureCatalog Measured31Oct()
    {
        FixtureSize S(double w, double d, double h, Mount m) => new(w, d, h, m);
        return FixtureCatalog.Defaults()
            .With(Roles.Wc, S(15.94, 29.31, 29.10, Mount.Floor))
            .With(Roles.Basin, S(21.68, 17.50, 22.63, Mount.Wall))
            .With(Roles.Faucet, S(1.64, 6.27, 6.64, Mount.OnBasin))          // front +X: spout out from the wall
            .With(Roles.Mirror, S(23.94, 0.27, 42.0, Mount.Wall))
            .With(Roles.TissueHolder, S(3.58, 5.24, 8.81, Mount.Wall))
            .With(Roles.MuslimShower, S(2.45, 28.44, 3.66, Mount.Wall))      // drawn lying down
            .With(Roles.SoapDispenser, S(4.48, 9.22, 3.92, Mount.Wall))      // drawn lying down
            .With(Roles.Dryer, S(10.77, 3.94, 12.24, Mount.Wall))
            .With(Roles.TowelDispenser, S(12.69, 15.93, 5.82, Mount.Wall))
            .With(Roles.FloorTrap, S(4.04, 4.04, 0.19, Mount.Floor))
            .With(Roles.Exhaust, S(10.0, 3.0, 9.54, Mount.Wall))            // front -X: a wall unit
            .With(Roles.CeilingLight, S(4.0, 4.0, 0.75, Mount.Ceiling));
    }

    [Fact]
    public void Manawala_plans_with_the_client_families()
    {
        var p = new ToiletPlanner(Measured31Oct()).Plan(ToiletGoldenTests.ManawalaMale);
        Assert.False(p.Refused, p.RefusalReason);
        Assert.Equal(Side.West, p.Item(Roles.Wc)!.Wall);
        Assert.Equal(Side.West, p.Item(Roles.Basin)!.Wall);
        var mirror = p.Item(Roles.Mirror)!;               // the client's mirror is 3'-6" tall: it comes down to 3'-4"
        Assert.Equal(40.0, mirror.Z0, 6);
        Assert.Equal(82.0, mirror.Z1, 6);
        Assert.Empty(ToiletChecker.Check(p, ToiletRules.Default, ToiletPlanner.RequiredRoles(p.Spec)));
    }

    [Fact]
    public void New_branch_toilet_1_plans_with_the_client_families()
    {
        var spec = new ToiletSpec
        {
            Width = 95, Depth = 60, Basin = BasinKind.Vanity, WetWalls = new() { Side.East },
            Door = new DoorSpec { Wall = Side.West, FromCorner = DoorCorner.End, Offset = 4.5 },
        };
        var p = new ToiletPlanner(Measured31Oct()).Plan(spec);
        Assert.False(p.Refused, p.RefusalReason);
        Assert.Equal(Side.East, p.Item(Roles.Wc)!.Wall);
        Assert.Empty(ToiletChecker.Check(p, ToiletRules.Default, ToiletPlanner.RequiredRoles(p.Spec)));
    }

    [Fact]
    public void An_unusable_accessory_family_is_left_out_not_the_whole_toilet()
    {
        var spec = ToiletGoldenTests.ManawalaMale with { Omit = new() { Roles.MuslimShower, Roles.Mirror } };
        var p = new ToiletPlanner(Measured31Oct()).Plan(spec);
        Assert.False(p.Refused, p.RefusalReason);
        Assert.Null(p.Item(Roles.MuslimShower));
        Assert.Null(p.Item(Roles.Mirror));
        Assert.NotNull(p.Item(Roles.Dryer));
        Assert.Contains(p.Log, l => l.Contains("LEFT OUT"));
        Assert.Empty(ToiletChecker.Check(p, ToiletRules.Default, ToiletPlanner.RequiredRoles(p.Spec)));

        var noWc = ToiletGoldenTests.ManawalaMale with { Omit = new() { Roles.Wc } };   // the WC can never be left out
        Assert.Contains(Roles.Wc, ToiletPlanner.RequiredRoles(noWc));
    }
}
