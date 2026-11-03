using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Core.Toilet;
using Xunit;

namespace Ubl.Engine.Core.Tests;

/// <summary>The drawing set (AR-141 to AR-148) against the Manawala sheets, with the client's families at their measured sizes.</summary>
public class ToiletDrawingTests
{
    private static ToiletPlan Manawala() => new ToiletPlanner(ClientFamilyTests.Measured31Oct()).Plan(ToiletGoldenTests.ManawalaMale);

    private static ToiletPlan NewBranch1() => new ToiletPlanner(ClientFamilyTests.Measured31Oct()).Plan(new ToiletSpec
    {
        Name = "TOILET 1", Width = 95, Depth = 60, Basin = BasinKind.Vanity, WetWalls = new() { Side.East },
        Door = new DoorSpec { Wall = Side.West, FromCorner = DoorCorner.End, Offset = 4.5 },
    });

    [Fact]
    public void Elevation_letters_follow_the_Manawala_set()
    {
        // AR-147 (C) is the wet wall with the WC and basin, AR-146 (B) the door wall, AR-145 (A) the WC's side wall
        var l = ToiletDrawingPlanner.Letters(Manawala());
        Assert.Equal("C", l[Side.West]);
        Assert.Equal("D", l[Side.East]);
        Assert.Equal("B", l[Side.South]);
        Assert.Equal("A", l[Side.North]);
    }

    [Fact]
    public void Sheets_are_numbered_as_in_the_Manawala_set()
    {
        var set = ToiletDrawingPlanner.Plan(Manawala());
        Assert.Equal(8, set.Sheets.Count);
        Assert.Equal("AR-141", set.Sheets[0].Number);
        Assert.Equal("AR-148", set.Sheets[7].Number);
        Assert.Equal("MALE TOILET LAYOUT PLAN (GROUND FLOOR)", set.Sheets[0].Name);
        Assert.Equal("WALL ELEVATION-C", set.Sheets[6].Name);
        var second = ToiletDrawingPlanner.Plan(Manawala(), variant: 1);
        Assert.Equal("AR-141-01", second.Sheets[0].Number);
        Assert.Equal("AR-148-08", second.Sheets[7].Number);
    }

    [Fact]
    public void Centre_line_chain_reads_1_6_3_3_1_6_as_on_AR_141()
    {
        var v = ToiletDrawingPlanner.Plan(Manawala()).View("layout");
        var chain = v.Dims.Single(d => d.Name.StartsWith("centre-lines on the west"));
        var a = chain.Anchors.Select(x => x.At).ToList();
        var spans = a.Zip(a.Skip(1), (p, q) => q - p).ToList();
        Assert.Equal(3, spans.Count);
        Assert.Equal(18, spans[0], 6);
        Assert.Equal(39, spans[1], 6);
        Assert.Equal(18, spans[2], 6);
        Assert.Equal(DAnchorKind.WallInner, chain.Anchors[0].Kind);
        var top = v.Dims.Single(d => d.Name.StartsWith("wall thicknesses"));
        var t = top.Anchors.Select(x => x.At).ToList();
        Assert.Equal(4.5, t[1] - t[0], 6);
        Assert.Equal(60, t[2] - t[1], 6);
        Assert.Equal(4.5, t[3] - t[2], 6);
    }

    [Fact]
    public void Floor_tiles_run_along_the_door_wall_from_the_door_corner()
    {
        var p = Manawala();
        var lines = ToiletDrawingPlanner.FloorTileLines(p.Spec, p.Door!, DrawingRules.Default);
        var along = lines.Where(l => Math.Abs(l.A.Y - l.B.Y) < 1e-9).Select(l => l.A.Y).OrderBy(y => y).ToList();
        var across = lines.Where(l => Math.Abs(l.A.X - l.B.X) < 1e-9).Select(l => l.A.X).OrderByDescending(x => x).ToList();
        Assert.Equal("12,24,36,48,60,72", string.Join(",", along));
        Assert.Equal("36,12", string.Join(",", across));       // door at the east end: full 24" tiles from x = 60
    }

    [Fact]
    public void Wall_tiles_are_12_inch_courses_to_the_ceiling()
    {
        var v = ToiletDrawingPlanner.Plan(Manawala()).View("elev-C");
        var courses = v.Dims.Single(d => d.Name == "tile courses").Anchors.Select(a => a.At).ToArray();
        Assert.Equal("0,12,24,36,48,60,72,84", string.Join(",", courses));
        Assert.NotEmpty(v.Labels.Where(l => l.Role == Roles.Mirror));
        Assert.NotEmpty(v.Labels.Where(l => l.Role == Roles.Wc));
    }

    [Fact]
    public void Tile_lines_stop_at_fixtures_in_the_elevation()
    {
        var p = Manawala();
        var v = ToiletDrawingPlanner.Plan(p).View("elev-C");
        var wc = p.Item(Roles.Wc)!;
        var f = v.Elevation!;
        double u0 = f.U(wc.S + wc.Width / 2), u1 = f.U(wc.S - wc.Width / 2);
        foreach (var l in v.Lines.Where(l => l.Style == DLineStyle.Tile))
        {
            var mid = new Pt((l.A.X + l.B.X) / 2, (l.A.Y + l.B.Y) / 2);
            Assert.False(mid.X > u0 + 1 && mid.X < u1 - 1 && mid.Y > 1 && mid.Y < wc.Z1 - 1, $"tile line through the WC at {mid}");
        }
    }

    [Fact]
    public void Every_sheet_fits_A3_and_notes_never_overlap()
    {
        foreach (var plan in new[] { Manawala(), NewBranch1() })
        {
            var set = ToiletDrawingPlanner.Plan(plan);
            Assert.Empty(set.Notes);
            foreach (var v in set.Views)
            {
                Assert.True(v.FitsSheet, $"{v.Name} does not fit");
                var boxes = v.Labels.Select(l => (l, b: TextOnly(l, v, set.Rules.LabelMm))).ToList();
                for (int i = 0; i < boxes.Count; i++)
                    for (int j = i + 1; j < boxes.Count; j++)
                        Assert.False(boxes[i].b.Intersects(boxes[j].b), $"{v.Name}: '{boxes[i].l.Text}' overlaps '{boxes[j].l.Text}'");
            }
        }
    }

    [Fact]
    public void Leaders_in_a_column_do_not_cross()
    {
        foreach (var plan in new[] { Manawala(), NewBranch1() })
            foreach (var v in ToiletDrawingPlanner.Plan(plan).Views)
                foreach (var side in new[] { true, false })
                {
                    var col = v.Labels.Where(l => l.Left == side).ToList();
                    for (int i = 0; i < col.Count; i++)
                        for (int j = i + 1; j < col.Count; j++)
                            Assert.False(ToiletDrawingPlanner.SegmentsCross(col[i].Elbow, col[i].Target, col[j].Elbow, col[j].Target),
                                         $"{v.Name}: leaders of '{col[i].Text}' and '{col[j].Text}' cross");
                }
    }

    [Fact]
    public void Break_splits_a_line_round_boxes()
    {
        var segs = ToiletDrawingPlanner.Break(new Pt(0, 10), new Pt(100, 10), new[] { new Box(20, 0, 30, 20), new Box(25, 5, 40, 15), new Box(60, 11, 70, 20) });
        Assert.Equal(2, segs.Count);
        Assert.Equal(20, segs[0].B.X, 6);
        Assert.Equal(40, segs[1].A.X, 6);
        Assert.Equal(100, segs[1].B.X, 6);
    }

    [Fact]
    public void Mirror_note_carries_the_measured_size()
    {
        var v = ToiletDrawingPlanner.Plan(Manawala()).View("layout");
        Assert.Contains(v.Labels, l => l.Text.StartsWith("2'-0\"x3'-6\" MIRROR"));
    }

    private static Box TextOnly(DLabel l, DrawingView v, double mm)
    {
        var lines = l.Text.Split('\n');
        double w = v.Mm(lines.Max(x => x.Length) * mm * ToiletDrawingPlanner.CharWidth), h = v.Mm(lines.Length * mm * 1.6);
        return l.Left ? new Box(l.TextAt.X - w, l.TextAt.Y - h / 2, l.TextAt.X, l.TextAt.Y + h / 2)
                      : new Box(l.TextAt.X, l.TextAt.Y - h / 2, l.TextAt.X + w, l.TextAt.Y + h / 2);
    }
}

public class ToiletDrawingConfigTests
{
    [Fact]
    public void The_shipped_drawings_section_reads_into_the_rules()
    {
        // same text as the "drawings" section of toilet.families.json; the add-in reads it with these options
        const string json = """
        { "floor_name": "FIRST FLOOR", "sheet_prefix": "AR-", "sheet_start": 151, "door_mark": "D-04", "tile_long": 24, "tile_short": 12,
          "wall_tile_top": null, "note": "ignored" }
        """;
        var r = System.Text.Json.JsonSerializer.Deserialize<DrawingRules>(json, Ubl.Engine.Core.Json.EngineJson.Options)!;
        Assert.Equal("FIRST FLOOR", r.FloorName);
        Assert.Equal(151, r.SheetStart);
        Assert.Equal("D-04", r.DoorMark);
        Assert.Null(r.WallTileTop);
        Assert.Equal("HALF PEDESTAL\nWASH BASIN", r.PlanLabels[Roles.Basin]);   // defaults kept for what is not set
        Assert.Equal("AR-151", ToiletDrawingPlanner.SheetNumber(r, 1, 0));
    }
}

/// <summary>Reading a bathroom that is already in the project: its rectangle, turned or not, and its doors.</summary>
public class ExistingRoomTests
{
    private static List<(Pt, Pt)> Loop(params Pt[] p) => p.Select((a, i) => (a, p[(i + 1) % p.Length])).ToList();

    [Fact]
    public void A_square_room_split_into_pieces_reads_as_its_rectangle()
    {
        // 5'-0" x 6'-3" at (100, 200), the south side split in two by a wall join, boundary running clockwise
        var segs = Loop(new Pt(100, 200), new Pt(100, 275), new Pt(160, 275), new Pt(160, 200), new Pt(130, 200));
        var r = ExistingRoom.Rectangle(segs);
        Assert.Equal(60, r.Width, 6);
        Assert.Equal(75, r.Depth, 6);
        Assert.Equal(100, r.Origin.X, 6);
        Assert.Equal(200, r.Origin.Y, 6);
        Assert.True(r.Square);
        Assert.Equal(Side.West, r.SegmentSides[0]);
        Assert.Equal(Side.North, r.SegmentSides[1]);
        Assert.Equal(Side.East, r.SegmentSides[2]);
        Assert.Equal(Side.South, r.SegmentSides[3]);
        Assert.Equal(Side.South, r.SegmentSides[4]);
    }

    [Fact]
    public void A_turned_room_keeps_its_size_and_the_edge_nearest_the_x_axis()
    {
        double a = 30 * Math.PI / 180;
        var ex = new Pt(Math.Cos(a), Math.Sin(a)); var ey = new Pt(-ex.Y, ex.X);
        var o = new Pt(50, 50);
        var r = ExistingRoom.Rectangle(Loop(o, o + ex * 95, o + ex * 95 + ey * 60, o + ey * 60));
        Assert.Equal(95, r.Width, 6);
        Assert.Equal(60, r.Depth, 6);
        Assert.Equal(30, r.AngleDeg, 6);
        Assert.False(r.Square);
        var back = r.ToModel(r.ToRoom(new Pt(80, 90)));
        Assert.Equal(80, back.X, 6);
        Assert.Equal(90, back.Y, 6);
    }

    [Fact]
    public void An_L_shaped_room_is_refused_with_the_reason()
    {
        var segs = Loop(new Pt(0, 0), new Pt(60, 0), new Pt(60, 40), new Pt(30, 40), new Pt(30, 75), new Pt(0, 75));
        var ex = Record(() => ExistingRoom.Rectangle(segs));
        Assert.NotNull(ex);
        Assert.Contains("not a rectangle", ex!.Message);
    }

    [Fact]
    public void The_Manawala_door_read_from_the_model_matches_the_golden_spec()
    {
        var spec = new ToiletSpec { Width = 60, Depth = 75 };
        // door location on the south wall's centre line, 2'-6" wide, hinge at the east jamb, swinging in
        var at = ExistingRoom.WallOf(spec, new Pt(40.5, -2.25));
        Assert.NotNull(at);
        Assert.Equal(Side.South, at!.Value.Wall);
        var d = ExistingRoom.Door(spec, at.Value.Wall, at.Value.S, 30, 84, hingeS: 55.5, swingIn: true);
        var golden = ToiletGoldenTests.ManawalaMale.Door;
        Assert.Equal(golden.Wall, d.Wall);
        Assert.Equal(golden.FromCorner, d.FromCorner);
        Assert.Equal(golden.Offset, d.Offset, 6);
        Assert.Equal(golden.Width, d.Width, 6);
        Assert.True(d.HingeAtCorner);
        var plan = new ToiletPlanner(ClientFamilyTests.Measured31Oct()).Plan(ToiletGoldenTests.ManawalaMale with { Door = d });
        Assert.False(plan.Refused, plan.RefusalReason);
        Assert.Equal(Side.West, plan.Item(Roles.Wc)!.Wall);
    }

    [Fact]
    public void A_second_door_is_kept_clear()
    {
        // 8'-0" x 6'-0" with the main door on the south wall and a second door on the west wall
        var spec = new ToiletSpec
        {
            Name = "TOILET", Width = 96, Depth = 72,
            Door = new DoorSpec { Wall = Side.South, FromCorner = DoorCorner.End, Offset = 4.5 },
            OtherDoors = new() { new DoorSpec { Wall = Side.West, FromCorner = DoorCorner.Start, Offset = 6 } },
        };
        var plan = new ToiletPlanner(ClientFamilyTests.Measured31Oct()).Plan(spec);
        Assert.False(plan.Refused, plan.RefusalReason);
        var second = RoomGeometry.OtherDoors(spec).Single();
        foreach (var i in plan.Items.Where(i => i.Mount != Mount.Ceiling && !ToiletChecker.IsFlush(i)))
            Assert.False(RoomGeometry.HitsSwing(spec, second, i.Plan), $"{i.Role} is in the second door's swing");
        Assert.Empty(ToiletChecker.Check(plan, ToiletRules.Default, ToiletPlanner.RequiredRoles(spec)));
        var set = ToiletDrawingPlanner.Plan(plan);
        Assert.Equal(2, set.View("flooring").Fills.Count(f => f.Kind == FillKind.Threshold));
    }

    private static Exception? Record(Action a)
    {
        try { a(); return null; }
        catch (Exception e) { return e; }
    }
}
