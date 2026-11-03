using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Core.Sheets;
using Ubl.Engine.Core.Units;

namespace Ubl.Engine.Core.Toilet;

// The toilet's drawing set (Manawala AR-141 to AR-148): layout, flooring, ceiling and sanitary plans and the four
// sectional wall elevations, each on its own A3 sheet. Everything that is a drawing decision (tile setting-out,
// dimension chains, label text and where it goes, which wall is elevation A, scales, sheet numbers) is made here,
// without Revit, so it is unit-tested and previewed. The Revit layer only turns each item into a detail line,
// filled region, text note or dimension.
//
// Coordinates: plan views use room coordinates (inches, inside south-west corner at 0,0). Elevations use (u, v):
// u along the wall as seen from inside the room, from its left end, v up from the finished floor.

public enum DrawingKind { Layout, Flooring, Ceiling, Sanitary, Elevation }
public enum DLineStyle { Thin, Medium, Tile, Hidden, Invisible }
public enum FillKind { Threshold, TileSample, Marker, Slab }

public sealed record DLine(Pt A, Pt B, DLineStyle Style);
public sealed record DCircle(Pt Center, double Radius, DLineStyle Style);
public sealed record DFill(IReadOnlyList<Pt> Loop, FillKind Kind);

/// <summary>Text without a leader. Align is left, center or right; At is the middle of that edge.</summary>
public sealed record DText(string Text, Pt At, double HeightMm, string Align = "center", bool Underline = false, bool Ellipse = false);

/// <summary>A note with a leader. The text sits beside TextAt (to its left when Left), the leader runs TextAt, Elbow, Target.</summary>
public sealed record DLabel(string Text, Pt Target, Pt TextAt, Pt Elbow, bool Left, string Role);

public enum DAnchorKind { Coord, WallInner, WallOuter }

/// <summary>
/// One witness of a dimension: a wall face (so the dimension stays tied to the wall) or a plain position. For a
/// position, From is where the measured feature is across the dimension (the witness line runs from there).
/// </summary>
public sealed record DAnchor(double At, DAnchorKind Kind = DAnchorKind.Coord, Side? Wall = null, double? From = null);

/// <summary>Horizontal: measures along x (or u) with the dimension line at y = LineAt. Vertical: along y (or v), line at x = LineAt.</summary>
public sealed record DDim(string Name, bool Horizontal, double LineAt, IReadOnlyList<DAnchor> Anchors);

/// <summary>How an elevation sits in the room: the wall looked at, its inside length, the cut walls at each end, where the section cuts.</summary>
public sealed record ElevationFrame(Side Wall, double Length, double LeftWall, double RightWall, double CutDepth)
{
    /// <summary>u of a point at distance S along the wall (walls run counter-clockwise, so u runs the other way).</summary>
    public double U(double s) => Length - s;
}

public sealed class DrawingView
{
    public required string Key { get; init; }
    public required DrawingKind Kind { get; init; }
    /// <summary>The Revit view name.</summary>
    public required string Name { get; init; }
    public required ArchScale Scale { get; init; }
    /// <summary>Everything drawn, annotation included: the view's crop and the viewport's size on the sheet.</summary>
    public Box Extent { get; set; }
    public ElevationFrame? Elevation { get; init; }
    public string? Letter { get; init; }
    public bool ShowFixtures { get; init; } = true;
    public List<DLine> Lines { get; } = new();
    public List<DCircle> Circles { get; } = new();
    public List<DFill> Fills { get; } = new();
    public List<DText> Texts { get; } = new();
    public List<DLabel> Labels { get; } = new();
    public List<DDim> Dims { get; } = new();
    /// <summary>Ceiling-plan symbols for families that do not show in a ceiling plan themselves (Revit draws them only when needed).</summary>
    public List<DLine> SymbolLines { get; } = new();
    public List<DCircle> SymbolCircles { get; } = new();
    public bool FitsSheet { get; set; } = true;

    /// <summary>Paper millimetres to model inches at this view's scale.</summary>
    public double Mm(double mm) => mm * Scale.Denominator / 25.4;
}

public sealed record DrawingSheet(int Index, string Number, string Name, string ViewKey);

public sealed class ToiletDrawingSet
{
    public required ToiletPlan Plan { get; init; }
    public required DrawingRules Rules { get; init; }
    public required SheetFormat Sheet { get; init; }
    public List<DrawingView> Views { get; } = new();
    public List<DrawingSheet> Sheets { get; } = new();
    public Dictionary<Side, string> Letters { get; } = new();
    public List<string> Notes { get; } = new();

    public DrawingView View(string key) => Views.First(v => v.Key == key);
}

/// <summary>
/// Drawing rules. Source: W = Manawala sheets AR-141 to AR-148, M = UBL manual (E-39, E-40), E = engine default.
/// </summary>
public sealed record DrawingRules
{
    public double TileLong { get; init; } = 24;                    // W AR-142, M E-39: porcelain 24" x 12"
    public double TileShort { get; init; } = 12;
    /// <summary>Top of the wall tiles; null means up to the ceiling (W AR-145: tiles in 1' courses to the ceiling line).</summary>
    public double? WallTileTop { get; init; }
    /// <summary>Slab above the toilet in the elevations (inches thick).</summary>
    public double SlabThickness { get; init; } = 6;                // E
    public string FloorName { get; init; } = "GROUND FLOOR";       // W sheet titles
    public string SheetPrefix { get; init; } = "AR-";              // W
    public int SheetStart { get; init; } = 141;                    // W
    public string DoorMark { get; init; } = "D-03";                // W
    public string FloorTileNote { get; init; } = "PORCELAIN TILE\nLIGHT GRAY SIZE 12\"x24\"";      // W AR-141, AR-142
    public string WallTileNote { get; init; } = "PORCELAIN\nTILE\nSIZE 12\"x24\"";                  // W AR-145
    public string CeilingNote { get; init; } = "GYPSUM SUSPENDED\nCEILING";                         // W AR-141, AR-143
    public string ElevationCeilingNote { get; init; } = "MOISTURE RESISTANT GYPSUM";                // W AR-145, M E-40
    public string ThresholdNote { get; init; } = "BASALT\nTHRESHOLD";                                // W AR-141, AR-142
    /// <summary>Label text per role in plans; {size} is replaced by the fixture's measured size.</summary>
    public Dictionary<string, string> PlanLabels { get; init; } = new()
    {
        [Roles.Basin] = "HALF PEDESTAL\nWASH BASIN",                                                  // W AR-141
        [Roles.Faucet] = "BASIN TAP\nAS PER APPROVED",                                                // W
        [Roles.Mirror] = "{size} MIRROR\nPASTED ON WALL",                                             // W
        [Roles.TissueHolder] = "TISSUE HOLDER",                                                       // W
        [Roles.MuslimShower] = "MUSLIM SHOWER",                                                       // W AR-147
        [Roles.SoapDispenser] = "SOAP\nDISPENSER",                                                    // W AR-147
        [Roles.Dryer] = "HAND DRYER",                                                                 // E
        [Roles.TowelDispenser] = "TOWEL\nDISPENSER",                                                  // W AR-146
        [Roles.FloorTrap] = "FLOOR DRAIN",                                                            // E
        [Roles.Exhaust] = "EXHAUST",                                                                  // W AR-143
        [Roles.CeilingLight] = "CEILING LIGHT",                                                       // E
        [Roles.Vanity] = "VANITY COUNTER\n{size}",                                                    // D
    };
    /// <summary>Label text per role in elevations.</summary>
    public Dictionary<string, string> ElevationLabels { get; init; } = new()
    {
        [Roles.Wc] = "FLOOR MOUNTED WC",                                                              // W AR-147
        [Roles.Basin] = "HALF PEDESTAL\nWALL MOUNTED\nBASIN",                                         // W AR-146
        [Roles.Faucet] = "BASIN TAP\nAS PER APPROVED",                                                // W
        [Roles.Mirror] = "5mm MIRROR PASTED ON\n1/8\" PLYWOOD WRAPPED IN\nMIN. 100 MICRON\nPOLYTHENE SHEET",   // W AR-147, M E-40
        [Roles.TissueHolder] = "TISSUE HOLDER",
        [Roles.MuslimShower] = "MUSLIM SHOWER",
        [Roles.SoapDispenser] = "SOAP\nDISPENSER",
        [Roles.Dryer] = "HAND DRYER",
        [Roles.TowelDispenser] = "TOWEL\nDISPENSER",
        [Roles.Exhaust] = "EXHAUST",
        [Roles.Vanity] = "VANITY COUNTER",
    };
    public string VanityBasinLabel { get; init; } = "WASH BASIN IN\nVANITY COUNTER";                 // E
    public string ElevationDoorLabel { get; init; } = "TOILET DOOR";                                  // W AR-146

    // paper sizes in millimetres (from the Manawala A3 sheets)
    public double LabelMm { get; init; } = 2.2;
    public double RoomTagMm { get; init; } = 4.5;
    public double DoorTagMm { get; init; } = 3.0;
    public double TitleMm { get; init; } = 4.0;
    public double DimTierMm { get; init; } = 7;

    public static DrawingRules Default { get; } = new();
}

public static class ToiletDrawingPlanner
{
    /// <summary>Scales tried for each view, largest first (Manawala: about 3/4" for the layout, 1" for the sanitary plan).</summary>
    public static IReadOnlyList<ArchScale> Scales { get; } = new[]
    {
        new ArchScale(12, "1\" = 1'-0\""), new ArchScale(16, "3/4\" = 1'-0\""), new ArchScale(24, "1/2\" = 1'-0\""),
        new ArchScale(32, "3/8\" = 1'-0\""), new ArchScale(48, "1/4\" = 1'-0\""),
    };

    public static string ViewName(string toilet, string key) => key switch
    {
        "layout" => $"{toilet} - LAYOUT",
        "flooring" => $"{toilet} - FLOORING",
        "ceiling" => $"{toilet} - CEILING",
        "sanitary" => $"{toilet} - SANITARY",
        _ => $"{toilet} - ELEVATION {key[^1]}",
    };

    /// <summary>Sheet numbers as in the Manawala set: AR-141..AR-148 for the first toilet, AR-141-01..AR-148-08 for the second, then -11..-18.</summary>
    public static string SheetNumber(DrawingRules r, int index, int variant) =>
        variant == 0 ? $"{r.SheetPrefix}{r.SheetStart + index - 1}" : $"{r.SheetPrefix}{r.SheetStart + index - 1}-{(variant - 1) * 10 + index:00}";

    public static ToiletDrawingSet Plan(ToiletPlan plan, DrawingRules? rules = null, SheetFormat? sheet = null, int variant = 0)
    {
        if (plan.Refused || plan.Door is null) throw new ArgumentException("A refused toilet has no drawings.");
        rules ??= DrawingRules.Default;
        sheet ??= SheetFormat.A3Portrait;
        var set = new ToiletDrawingSet { Plan = plan, Rules = rules, Sheet = sheet };
        foreach (var (side, letter) in Letters(plan)) set.Letters[side] = letter;

        var name = plan.Spec.Name;
        DrawingView Fit(Func<ArchScale, DrawingView> make)
        {
            DrawingView? last = null;
            foreach (var s in Scales)
            {
                last = make(s);
                if (last.Extent.Width / last.Mm(1) <= sheet.DrawWidth - 8 && last.Extent.Height / last.Mm(1) <= sheet.DrawHeight - 8) return last;
            }
            last!.FitsSheet = false;
            set.Notes.Add($"{last.Name}: does not fit the sheet even at {last.Scale.Name}");
            return last;
        }

        set.Views.Add(Fit(s => PlanView(set, "layout", DrawingKind.Layout, s)));
        set.Views.Add(Fit(s => PlanView(set, "flooring", DrawingKind.Flooring, s)));
        set.Views.Add(Fit(s => PlanView(set, "ceiling", DrawingKind.Ceiling, s)));
        set.Views.Add(Fit(s => PlanView(set, "sanitary", DrawingKind.Sanitary, s)));
        foreach (var letter in new[] { "A", "B", "C", "D" })
        {
            var side = set.Letters.First(kv => kv.Value == letter).Key;
            set.Views.Add(Fit(s => ElevationView(set, side, letter, s)));
        }

        string floor = rules.FloorName;
        var names = new[]
        {
            $"{name} LAYOUT PLAN ({floor})", $"FLOORING LAYOUT PLAN ({floor})", $"CEILING LAYOUT PLAN ({floor})", $"SANITARY LAYOUT PLAN ({floor})",
            "WALL ELEVATION-A", "WALL ELEVATION-B", "WALL ELEVATION-C", "WALL ELEVATION-D",
        };
        for (int i = 0; i < 8; i++)
            set.Sheets.Add(new DrawingSheet(i + 1, SheetNumber(rules, i + 1, variant), names[i], set.Views[i].Key));
        return set;
    }

    /// <summary>
    /// Which wall is elevation A, B, C, D (W, from the Manawala set): C is the WC wall, D faces it, B is the side
    /// wall with the door (else the one with the basin), A is the other side wall.
    /// </summary>
    public static Dictionary<Side, string> Letters(ToiletPlan plan)
    {
        var wc = plan.Item(Roles.Wc);
        var basin = plan.Item(Roles.Basin);
        Side c = wc?.Wall ?? basin?.Wall ?? RoomGeometry.Opposite(plan.Door!.Wall);
        Side d = RoomGeometry.Opposite(c);
        var sides = new[] { RoomGeometry.Next(c), RoomGeometry.Prev(c) };
        Side b = sides.Contains(plan.Door!.Wall) ? plan.Door.Wall
               : basin?.Wall is Side bw && sides.Contains(bw) ? bw
               : RoomGeometry.Prev(c);
        Side a = sides.First(s => s != b);
        return new Dictionary<Side, string> { [a] = "A", [b] = "B", [c] = "C", [d] = "D" };
    }

    // ================================================================== plans

    private static DrawingView PlanView(ToiletDrawingSet set, string key, DrawingKind kind, ArchScale scale)
    {
        var plan = set.Plan; var spec = plan.Spec; var r = set.Rules; var door = plan.Door!;
        var v = new DrawingView { Key = key, Kind = kind, Name = ViewName(spec.Name, key), Scale = scale, ShowFixtures = kind is DrawingKind.Layout or DrawingKind.Sanitary };
        double W = spec.Width, D = spec.Depth;
        double tS = spec.WallSouth, tE = spec.WallEast, tN = spec.WallNorth, tW = spec.WallWest;
        var outer = new Box(-tW, -tS, W + tE, D + tN);
        // how far each side's annotation already reaches out from the wall's outer face (paper mm)
        var reach = new Dictionary<Side, double> { [Side.South] = 6, [Side.East] = 6, [Side.North] = 6, [Side.West] = 6 };
        double OuterAt(Side s) => s switch { Side.South => -tS, Side.East => W + tE, Side.North => D + tN, _ => -tW };
        double Out(Side s, double mm) => OuterAt(s) + (s is Side.South or Side.West ? -1 : 1) * v.Mm(mm);   // a line beyond the wall
        var extent = outer;
        void Grow(Box b) => extent = extent.Union(b);

        // ---- tiles and threshold
        if (kind is DrawingKind.Layout or DrawingKind.Flooring)
        {
            var holes = kind == DrawingKind.Layout
                ? plan.Items.Where(i => i.Role is Roles.Wc or Roles.Basin or Roles.Vanity or Roles.FloorTrap).Select(i => i.Plan.Inflate(0.5)).ToList()
                : new List<Box>();
            foreach (var (a, b) in FloorTileLines(spec, door, r))
                foreach (var seg in Break(a, b, holes)) v.Lines.Add(new DLine(seg.A, seg.B, DLineStyle.Tile));
        }
        var dwl = RoomGeometry.Wall(spec, door.Wall);
        double tDoor = spec.WallThickness(door.Wall);
        var threshold = new[] { dwl.At(door.S0, 0), dwl.At(door.S1, 0), dwl.At(door.S1, -tDoor), dwl.At(door.S0, -tDoor) };
        if (kind is DrawingKind.Layout or DrawingKind.Flooring)
        {
            v.Fills.Add(new DFill(threshold, FillKind.Threshold));
            foreach (var od in RoomGeometry.OtherDoors(spec))
            {
                var owl = RoomGeometry.Wall(spec, od.Wall); double ot = spec.WallThickness(od.Wall);
                v.Fills.Add(new DFill(new[] { owl.At(od.S0, 0), owl.At(od.S1, 0), owl.At(od.S1, -ot), owl.At(od.S0, -ot) }, FillKind.Threshold));
            }
        }

        // ---- dimensions (layout and sanitary): fixture centre-lines along their walls, wall thicknesses, inside size
        if (kind is DrawingKind.Layout or DrawingKind.Sanitary)
        {
            foreach (var side in RoomGeometry.Sides)
            {
                var onWall = plan.Items.Where(i => i.Wall == side && i.Role is Roles.Wc or Roles.Basin).OrderBy(i => i.S).ToList();
                if (onWall.Count == 0) continue;
                var wl = RoomGeometry.Wall(spec, side);
                bool horizontal = side is Side.South or Side.North;
                double Axis(double s) { var p = wl.At(s); return horizontal ? p.X : p.Y; }
                double Across(double s) { var p = wl.At(s); return horizontal ? p.Y : p.X; }
                var anchors = new List<DAnchor> { new(Axis(0), DAnchorKind.WallInner, RoomGeometry.Prev(side)) };
                anchors.AddRange(onWall.Select(i => new DAnchor(Axis(i.S), From: Across(i.S))));
                anchors.Add(new DAnchor(Axis(wl.Length), DAnchorKind.WallInner, RoomGeometry.Next(side)));
                v.Dims.Add(new DDim($"centre-lines on the {side.ToString().ToLowerInvariant()} wall", horizontal, Out(side, reach[side]), Sorted(anchors)));
                reach[side] += r.DimTierMm;
            }
            v.Dims.Add(new DDim("wall thicknesses and inside width", true, Out(Side.North, reach[Side.North]), new List<DAnchor>
            {
                new(-tW, DAnchorKind.WallOuter, Side.West), new(0, DAnchorKind.WallInner, Side.West),
                new(W, DAnchorKind.WallInner, Side.East), new(W + tE, DAnchorKind.WallOuter, Side.East),
            }));
            reach[Side.North] += r.DimTierMm;
            var depthSide = reach[Side.West] <= reach[Side.East] ? Side.West : Side.East;
            v.Dims.Add(new DDim("inside depth", false, Out(depthSide, reach[depthSide]), new List<DAnchor>
            {
                new(0, DAnchorKind.WallInner, Side.South), new(D, DAnchorKind.WallInner, Side.North),
            }));
            reach[depthSide] += r.DimTierMm;
        }

        // ---- flooring: one full tile hatched and dimensioned (W AR-142)
        if (kind == DrawingKind.Flooring)
        {
            var (s0, sign) = TileStart(dwl, door);
            double s1 = s0 + sign * r.TileLong;
            if (s1 >= -1e-6 && s1 <= dwl.Length + 1e-6)
            {
                v.Fills.Add(new DFill(new[] { dwl.At(s0, 0), dwl.At(s1, 0), dwl.At(s1, r.TileShort), dwl.At(s0, r.TileShort) }, FillKind.TileSample));
                bool horizontal = door.Wall is Side.South or Side.North;
                Pt pa = dwl.At(s0, r.TileShort + v.Mm(4)), pb = dwl.At(s1, 0), pc = dwl.At(s1, r.TileShort);
                if (horizontal)
                {
                    v.Dims.Add(new DDim("tile length", true, pa.Y, Sorted(new List<DAnchor> { new(dwl.At(s0).X, From: pc.Y), new(pb.X, From: pc.Y) })));
                    double x = pb.X + Math.Sign(sign * dwl.Dir.X) * v.Mm(4);
                    v.Dims.Add(new DDim("tile width", false, x, Sorted(new List<DAnchor> { new(pb.Y, From: pb.X), new(pc.Y, From: pb.X) })));
                }
                else
                {
                    v.Dims.Add(new DDim("tile length", false, pa.X, Sorted(new List<DAnchor> { new(dwl.At(s0).Y, From: pc.X), new(pb.Y, From: pc.X) })));
                    double y = pb.Y + Math.Sign(sign * dwl.Dir.Y) * v.Mm(4);
                    v.Dims.Add(new DDim("tile width", true, y, Sorted(new List<DAnchor> { new(pb.X, From: pb.Y), new(pc.X, From: pb.Y) })));
                }
            }
        }

        // ---- ceiling plan symbols (used only when a family does not show in a ceiling plan)
        if (kind == DrawingKind.Ceiling)
        {
            foreach (var i in plan.Items)
            {
                if (i.Role == Roles.CeilingLight)
                    v.SymbolCircles.Add(new DCircle(i.Plan.Center, Math.Max(i.Plan.Width, i.Plan.Height) / 2, DLineStyle.Thin));
                else if (i.Role == Roles.Exhaust)
                {
                    var c = i.Plan.Corners();
                    var style = i.Mount == Mount.Ceiling ? DLineStyle.Thin : DLineStyle.Hidden;
                    for (int k = 0; k < 4; k++) v.SymbolLines.Add(new DLine(c[k], c[(k + 1) % 4], style));
                    if (i.Mount == Mount.Ceiling) { v.SymbolLines.Add(new DLine(c[0], c[2], style)); v.SymbolLines.Add(new DLine(c[1], c[3], style)); }
                }
            }
        }

        // ---- door tag outside the door (W AR-141): two lines, aligned away from the wall when the door is on a side wall
        {
            var mid = dwl.At((door.S0 + door.S1) / 2, -tDoor);
            var dir = dwl.Normal * -1;
            string size = $"D={Compact(door.Width)}x{Compact(door.Height)}";
            bool side = door.Wall is Side.East or Side.West;
            string align = !side ? "center" : door.Wall == Side.West ? "right" : "left";
            double at = reach[door.Wall] + 3;
            if (!side)
            {
                v.Texts.Add(new DText(size, mid + dir * v.Mm(at), r.DoorTagMm));
                v.Texts.Add(new DText(r.DoorMark, mid + dir * v.Mm(at + 7), r.DoorTagMm, Ellipse: true));
                reach[door.Wall] = at + 11;
            }
            else
            {
                // door on a side wall: the tag goes below (or above) the room at the door's end, out of the label columns
                bool low = mid.Y <= D / 2;
                var tb = low ? Side.South : Side.North;
                double y0 = low ? -tS - v.Mm(reach[tb] + 3) : D + tN + v.Mm(reach[tb] + 3);
                double yStep = (low ? -1 : 1) * v.Mm(7);
                double x0 = door.Wall == Side.West ? Math.Max(W * 0.12, 0) : Math.Min(W * 0.88, W);
                v.Texts.Add(new DText(size, new Pt(x0, y0), r.DoorTagMm));
                v.Texts.Add(new DText(r.DoorMark, new Pt(x0, y0 + yStep), r.DoorTagMm, Ellipse: true));
                reach[tb] += 3 + 11;
            }
            double rt = reach[Side.South] + 6;
            var tagAt = new Pt(W * 0.7, -tS - v.Mm(rt));
            v.Texts.Add(new DText($"{RoomName(spec.Name)}\n{FtIn.Format(W).Replace(" ", "")}X{FtIn.Format(D).Replace(" ", "")}", tagAt, r.RoomTagMm));
            reach[Side.South] = rt + 2 * r.RoomTagMm * 1.6;
        }

        // ---- elevation markers (layout): a triangle pointing at the wall and its letter, outside the room
        if (kind == DrawingKind.Layout)
        {
            bool wide = W > D * 1.15;
            var mSide = reach[Side.West] <= reach[Side.East] ? Side.West : Side.East;
            double mw = reach[mSide] + 6, ms = reach[Side.South] + 6;
            double mx = mSide == Side.West ? -tW - v.Mm(mw) : W + tE + v.Mm(mw);
            foreach (var (side, letter) in set.Letters)
            {
                Pt at, dir;
                switch (side)
                {
                    // a wide room has all four markers in a row below it, so the side columns stay free for notes
                    case Side.North: at = wide ? new Pt(W * 0.4, -tS - v.Mm(ms)) : new Pt(mx, D * 0.68); dir = new Pt(0, 1); break;
                    case Side.South: at = wide ? new Pt(W * 0.6, -tS - v.Mm(ms)) : new Pt(mx, D * 0.32); dir = new Pt(0, -1); break;
                    case Side.West: at = new Pt(W * (wide ? 0.2 : 0.3), -tS - v.Mm(ms)); dir = new Pt(-1, 0); break;
                    default: at = new Pt(W * (wide ? 0.8 : 0.7), -tS - v.Mm(ms)); dir = new Pt(1, 0); break;
                }
                var perp = dir.Normal;
                var tri = new[] { at + dir * v.Mm(2.2), at - dir * v.Mm(1.3) + perp * v.Mm(2), at - dir * v.Mm(1.3) - perp * v.Mm(2) };
                v.Fills.Add(new DFill(tri, FillKind.Marker));
                var textAt = side is Side.North or Side.South ? (wide ? at - perp * v.Mm(4.5) : at - dir * v.Mm(4.5)) : at - perp * v.Mm(4.5);
                v.Texts.Add(new DText($"\"{letter}\"", textAt, r.DoorTagMm));
            }
            if (!wide) reach[mSide] = mw + 6;
            reach[Side.South] = Math.Max(reach[Side.South], ms + 6);
        }

        // ---- labels with leaders, in columns left and right of everything else
        var notes = new List<(string Text, Pt Target, string Role)>();
        var floorBusy = plan.Items.Where(i => i.Role is Roles.Wc or Roles.Basin or Roles.Vanity or Roles.FloorTrap).Select(i => i.Plan.Inflate(2)).ToList();
        if (door.SwingIn) floorBusy.Add(Swing(spec, door));
        var fixtureX = plan.Items.Where(i => i.Mount != Mount.Ceiling).Select(i => i.Plan.Center.X).ToList();
        bool fixturesLeft = fixtureX.Count(x => x < W / 2) >= fixtureX.Count(x => x >= W / 2);
        Pt Free(double fx, double fy) => FreePoint(new Box(4, 4, W - 4, D - 4), floorBusy, new Pt(W * (fixturesLeft ? 1 - fx : fx), D * fy));
        switch (kind)
        {
            case DrawingKind.Layout:
                notes.Add((r.CeilingNote, Free(0.3, 0.8), "ceiling"));
                notes.Add((r.FloorTileNote, Free(0.3, 0.5), "floor tile"));
                notes.Add((r.ThresholdNote, Box.Of(threshold).Center, "threshold"));
                AddFixtureNotes(notes, plan, r, ceiling: false);
                break;
            case DrawingKind.Sanitary:
                AddFixtureNotes(notes, plan, r, ceiling: false);
                break;
            case DrawingKind.Flooring:
                notes.Add((r.FloorTileNote, Free(0.4, 0.55), "floor tile"));
                notes.Add((r.ThresholdNote, Box.Of(threshold).Center, "threshold"));
                break;
            case DrawingKind.Ceiling:
                notes.Add(($"{r.CeilingNote}\n@ {Compact(spec.CeilingHeight)} A.F.F.", new Pt(W * 0.25, D * 0.75), "ceiling"));
                AddFixtureNotes(notes, plan, r, ceiling: true);
                break;
        }
        if (kind is DrawingKind.Layout or DrawingKind.Sanitary && plan.Item(Roles.Wc) is { } wc)
            v.Texts.Add(new DText("W/C", WcLabelPoint(wc), 3.5));
        double xl = -tW - v.Mm(reach[Side.West] + 6), xr = W + tE + v.Mm(reach[Side.East] + 6);
        double yTop = D + tN + v.Mm(reach[Side.North] + 30), yBot = -tS - v.Mm(40);
        v.Labels.AddRange(LayoutLabels(notes, xl, xr, W / 2, yBot, yTop, v, r.LabelMm));

        // ---- extent
        foreach (var d in v.Dims) Grow(DimBox(d, v));
        foreach (var t in v.Texts) Grow(TextBox(t, v));
        foreach (var l in v.Labels) Grow(LabelBox(l, v, r.LabelMm));
        foreach (var f in v.Fills) Grow(Box.Of(f.Loop));
        v.Extent = extent.Inflate(v.Mm(3));
        return v;
    }

    private static void AddFixtureNotes(List<(string, Pt, string)> notes, ToiletPlan plan, DrawingRules r, bool ceiling)
    {
        foreach (var i in plan.Items)
        {
            bool isCeiling = i.Role is Roles.CeilingLight or Roles.Exhaust;
            if (isCeiling != ceiling || i.Role == Roles.Wc) continue;
            string? text = i.Role == Roles.Basin && plan.Spec.Basin == BasinKind.Vanity ? r.VanityBasinLabel
                         : r.PlanLabels.TryGetValue(i.Role, out var t) ? t : null;
            if (text is null) continue;
            notes.Add((text.Replace("{size}", $"{Compact(i.Width)}x{Compact(i.Role == Roles.Vanity ? i.Depth : i.Height)}"), i.Plan.Center, i.Role));
        }
    }

    /// <summary>Front half of the WC, where "W/C" is written in the Manawala plans.</summary>
    private static Pt WcLabelPoint(PlacedItem wc) => wc.Anchor + wc.Facing * (wc.Depth * 0.62);

    /// <summary>Lines of a 24" x 12" tile grid: long side along the door wall, full tiles from the door corner (W AR-142, E).</summary>
    public static List<(Pt A, Pt B)> FloorTileLines(ToiletSpec spec, DoorGeometry door, DrawingRules r)
    {
        var wl = RoomGeometry.Wall(spec, door.Wall);
        double across = door.Wall is Side.South or Side.North ? spec.Depth : spec.Width;
        var lines = new List<(Pt, Pt)>();
        for (double d = r.TileShort; d < across - 0.25; d += r.TileShort) lines.Add((wl.At(0, d), wl.At(wl.Length, d)));
        var (s0, sign) = TileStart(wl, door);
        for (int k = 1; ; k++)
        {
            double s = s0 + sign * k * r.TileLong;
            if (s <= 0.25 || s >= wl.Length - 0.25) break;
            lines.Add((wl.At(s, 0), wl.At(s, across)));
        }
        return lines;
    }

    /// <summary>The door wall's corner nearer the door, and the direction tiles run from it.</summary>
    private static (double S0, int Sign) TileStart(WallLine wl, DoorGeometry door) =>
        (door.S0 + door.S1) / 2 <= wl.Length / 2 ? (0, 1) : (wl.Length, -1);

    private static Box Swing(ToiletSpec spec, DoorGeometry door)
    {
        var wl = RoomGeometry.Wall(spec, door.Wall);
        return Box.FromCorners(wl.At(door.S0, 0), wl.At(door.S1, door.Width));
    }

    // ================================================================== elevations

    private static DrawingView ElevationView(ToiletDrawingSet set, Side side, string letter, ArchScale scale)
    {
        var plan = set.Plan; var spec = plan.Spec; var r = set.Rules; var door = plan.Door!;
        var wl = RoomGeometry.Wall(spec, side);
        double across = side is Side.South or Side.North ? spec.Depth : spec.Width;
        var f = new ElevationFrame(side, wl.Length, spec.WallThickness(RoomGeometry.Next(side)), spec.WallThickness(RoomGeometry.Prev(side)), across / 2);
        var v = new DrawingView { Key = "elev-" + letter, Kind = DrawingKind.Elevation, Name = ViewName(spec.Name, "elev-" + letter), Scale = scale, Elevation = f, Letter = letter };
        double L = f.Length, CH = spec.CeilingHeight, slab = spec.SlabHeight;
        double tileTop = Math.Min(r.WallTileTop ?? CH, CH);

        // ---- what is seen: everything between the wall and the cut, except items on the ceiling and flush with the floor
        var seen = new List<(PlacedItem Item, Box Rect)>();
        foreach (var i in plan.Items)
        {
            if (i.Mount == Mount.Ceiling || ToiletChecker.IsFlush(i)) continue;
            var cs = i.Plan.Corners();
            if (cs.Min(c => wl.DOf(c)) >= f.CutDepth) continue;
            double s0 = cs.Min(c => wl.SOf(c)), s1 = cs.Max(c => wl.SOf(c));
            double u0 = Math.Max(0, f.U(s1)), u1 = Math.Min(L, f.U(s0));
            if (u1 - u0 < 0.1) continue;
            double z0 = i.Role == Roles.Vanity ? i.Z1 - 1.5 : i.Z0;
            seen.Add((i, new Box(u0, z0, u1, i.Z1)));
        }
        Box? doorRect = door.Wall == side ? new Box(f.U(door.S1) - 2, 0, f.U(door.S0) + 2, door.Height + 3) : null;
        var holes = seen.Select(x => x.Rect.Inflate(0.5)).ToList();
        if (doorRect is { } dr) holes.Add(dr);
        var otherDoorRects = RoomGeometry.OtherDoors(spec).Where(od => od.Wall == side)
            .Select(od => new Box(f.U(od.S1) - 2, 0, f.U(od.S0) + 2, od.Height + 3)).ToList();
        holes.AddRange(otherDoorRects);

        // ---- wall tiles: 12" courses from the floor, 24" joints from the corner nearer the door (W AR-145, E)
        for (double h = r.TileShort; h < tileTop - 0.25; h += r.TileShort)
            foreach (var seg in Break(new Pt(0, h), new Pt(L, h), holes)) v.Lines.Add(new DLine(seg.A, seg.B, DLineStyle.Tile));
        bool fromRight = door.Wall == RoomGeometry.Prev(side);
        for (int k = 1; ; k++)
        {
            double u = fromRight ? L - k * r.TileLong : k * r.TileLong;
            if (u <= 0.25 || u >= L - 0.25) break;
            foreach (var seg in Break(new Pt(u, 0), new Pt(u, tileTop), holes)) v.Lines.Add(new DLine(seg.A, seg.B, DLineStyle.Tile));
        }

        // ---- slab band and floor line, as in the Manawala elevations
        double ux0 = -f.LeftWall - v.Mm(5), ux1 = L + f.RightWall + v.Mm(5);
        v.Fills.Add(new DFill(new[] { new Pt(ux0, slab), new Pt(ux1, slab), new Pt(ux1, slab + r.SlabThickness), new Pt(ux0, slab + r.SlabThickness) }, FillKind.Slab));
        v.Lines.Add(new DLine(new Pt(ux0, 0), new Pt(ux1, 0), DLineStyle.Medium));

        // ---- dimensions: tile courses and their total on the left; ceiling and slab on the right; the door height
        var courses = new List<DAnchor> { new(0, From: 0) };
        for (double h = r.TileShort; h < tileTop - 0.25; h += r.TileShort) courses.Add(new DAnchor(h, From: 0));
        courses.Add(new DAnchor(tileTop, From: 0));
        double left1 = -f.LeftWall - v.Mm(r.DimTierMm), left2 = -f.LeftWall - v.Mm(2 * r.DimTierMm);
        double right1 = L + f.RightWall + v.Mm(r.DimTierMm);
        v.Dims.Add(new DDim("tile courses", false, left1, courses));
        v.Dims.Add(new DDim("tile height", false, left2, new List<DAnchor> { new(0, From: 0), new(tileTop, From: 0) }));
        v.Dims.Add(new DDim("ceiling and slab heights", false, right1, new List<DAnchor> { new(0, From: L), new(CH, From: L), new(slab, From: L) }));
        if (doorRect is { } d2) v.Dims.Add(new DDim("door height", false, d2.X0 + 2 + v.Mm(4), new List<DAnchor> { new(0, From: d2.X0 + 2), new(door.Height, From: d2.X0 + 2) }));

        // ---- texts
        v.Texts.Add(new DText(r.ElevationCeilingNote, new Pt(L / 2, CH + Math.Max(v.Mm(5), (slab - CH) / 2)), r.LabelMm));
        v.Texts.Add(new DText("F.F.L", new Pt(L + f.RightWall + v.Mm(r.DimTierMm + 3), v.Mm(2)), r.LabelMm, "left"));
        v.Texts.Add(new DText($"ELEVATION-{letter}", new Pt(L / 2, -v.Mm(10)), r.TitleMm, Underline: true));

        // ---- labels
        var notes = new List<(string Text, Pt Target, string Role)>();
        foreach (var (i, rect) in seen)
        {
            string? text = i.Role == Roles.Basin && spec.Basin == BasinKind.Vanity ? r.VanityBasinLabel
                         : r.ElevationLabels.TryGetValue(i.Role, out var t) ? t : null;
            var target = i.Role == Roles.Basin ? new Pt(rect.Center.X, rect.Y1 - Math.Min(3, rect.Height / 3)) : rect.Center;
            if (text is not null) notes.Add((text, target, i.Role));
        }
        if (doorRect is { } d3) notes.Add((r.ElevationDoorLabel, new Pt(d3.Center.X, door.Height * 0.8), "door"));
        foreach (var od in otherDoorRects) notes.Add((r.ElevationDoorLabel, new Pt(od.Center.X, od.Height * 0.75), "door"));
        var tileSpot = FreePoint(new Box(3, 3, L - 3, tileTop - 3), holes.Select(h => h.Inflate(3)).ToList(), new Pt(L * 0.25, tileTop * 0.55));
        notes.Add((r.WallTileNote, tileSpot, "wall tile"));
        double xl = left2 - v.Mm(8), xr = right1 + v.Mm(14);
        v.Labels.AddRange(LayoutLabels(notes, xl, xr, L / 2, 0, slab, v, r.LabelMm));

        // ---- extent: the cut walls, slab, dimensions, labels and the title
        var extent = new Box(ux0, -v.Mm(4), ux1, slab + r.SlabThickness);
        foreach (var d in v.Dims) extent = extent.Union(DimBox(d, v));
        foreach (var t in v.Texts) extent = extent.Union(TextBox(t, v));
        foreach (var l in v.Labels) extent = extent.Union(LabelBox(l, v, r.LabelMm));
        v.Extent = extent.Inflate(v.Mm(3));
        return v;
    }

    // ================================================================== helpers

    /// <summary>
    /// Stacks notes in a column on each side (by which half their target is in), keeping each near its target's
    /// height but never overlapping, inside [yLo, yHi]. Leaders run from the text to an elbow, then to the target.
    /// </summary>
    public static List<DLabel> LayoutLabels(List<(string Text, Pt Target, string Role)> notes, double xLeft, double xRight, double xMid,
                                            double yLo, double yHi, DrawingView v, double textMm)
    {
        var result = new List<DLabel>();
        foreach (bool left in new[] { true, false })
        {
            var col = notes.Where(n => (n.Target.X < xMid) == left).OrderByDescending(n => n.Target.Y).ToList();
            if (col.Count == 0) continue;
            var h = col.Select(n => v.Mm(n.Text.Split('\n').Length * textMm * 1.6 + 3)).ToList();
            var y = col.Select(n => n.Target.Y).ToArray();
            for (int i = 0; i < y.Length; i++)
            {
                double top = i == 0 ? yHi - h[0] / 2 : y[i - 1] - h[i - 1] / 2 - h[i] / 2;
                y[i] = Math.Min(y[i], top);
            }
            double over = (yLo + h[^1] / 2) - y[^1];
            if (over > 0)
            {
                y[^1] += over;
                for (int i = y.Length - 2; i >= 0; i--) y[i] = Math.Max(y[i], y[i + 1] + h[i + 1] / 2 + h[i] / 2);
            }
            // order within the column so that no two leaders cross: swap neighbours while any pair does
            var order = Enumerable.Range(0, col.Count).ToArray();
            double ex = (left ? xLeft : xRight) + (left ? 1 : -1) * v.Mm(3);
            for (int pass = 0; pass < 3 * col.Count; pass++)
            {
                bool swapped = false;
                for (int i = 0; i + 1 < col.Count; i++)
                {
                    var a0 = new Pt(ex, y[i]); var a1 = col[order[i]].Target;
                    var b0 = new Pt(ex, y[i + 1]); var b1 = col[order[i + 1]].Target;
                    if (!SegmentsCross(a0, a1, b0, b1)) continue;
                    (order[i], order[i + 1]) = (order[i + 1], order[i]);
                    swapped = true;
                }
                if (!swapped) break;
            }
            col = order.Select(i => col[i]).ToList();
            for (int i = 0; i < col.Count; i++)
            {
                double x = left ? xLeft : xRight;
                var at = new Pt(x, y[i]);
                var elbow = new Pt(x + (left ? 1 : -1) * v.Mm(3), y[i]);
                result.Add(new DLabel(col[i].Text, col[i].Target, at, elbow, left, col[i].Role));
            }
        }
        return result;
    }

    /// <summary>Average width of a capital letter in Arial, as a share of the text height.</summary>
    public const double CharWidth = 0.6;

    public static bool SegmentsCross(Pt a, Pt b, Pt c, Pt d)
    {
        static double O(Pt p, Pt q, Pt r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        return O(a, b, c) * O(a, b, d) < 0 && O(c, d, a) * O(c, d, b) < 0;
    }

    /// <summary>Splits an axis-aligned line where it crosses any of the boxes.</summary>
    public static List<(Pt A, Pt B)> Break(Pt a, Pt b, IReadOnlyList<Box> holes)
    {
        bool horizontal = Math.Abs(a.Y - b.Y) < 1e-9;
        double lo = horizontal ? Math.Min(a.X, b.X) : Math.Min(a.Y, b.Y), hi = horizontal ? Math.Max(a.X, b.X) : Math.Max(a.Y, b.Y);
        double c = horizontal ? a.Y : a.X;
        var cuts = new List<(double, double)>();
        foreach (var h in holes)
        {
            bool crosses = horizontal ? c > h.Y0 && c < h.Y1 : c > h.X0 && c < h.X1;
            if (!crosses) continue;
            cuts.Add(horizontal ? (h.X0, h.X1) : (h.Y0, h.Y1));
        }
        var segs = new List<(Pt, Pt)>();
        double t = lo;
        foreach (var (c0, c1) in cuts.OrderBy(x => x.Item1))
        {
            if (c1 <= t) continue;
            if (c0 > t + 0.25) segs.Add(Seg(t, Math.Min(c0, hi)));
            t = Math.Max(t, c1);
            if (t >= hi) break;
        }
        if (hi > t + 0.25) segs.Add(Seg(t, hi));
        return segs;
        (Pt, Pt) Seg(double s0, double s1) => horizontal ? (new Pt(s0, c), new Pt(s1, c)) : (new Pt(c, s0), new Pt(c, s1));
    }

    /// <summary>The point of a 1" grid inside the area, outside every busy box, nearest the preferred point.</summary>
    public static Pt FreePoint(Box area, IReadOnlyList<Box> busy, Pt preferred)
    {
        Pt best = area.Center; double bestD = double.MaxValue;
        for (double x = Math.Ceiling(area.X0); x <= area.X1; x += 1)
            for (double y = Math.Ceiling(area.Y0); y <= area.Y1; y += 1)
            {
                var p = new Pt(x, y);
                if (busy.Any(b => b.Contains(p))) continue;
                double d = Pt.Dist(p, preferred);
                if (d < bestD) { bestD = d; best = p; }
            }
        return best;
    }

    private static List<DAnchor> Sorted(List<DAnchor> a) => a.OrderBy(x => x.At).ToList();

    public static Box DimBox(DDim d, DrawingView v)
    {
        double lo = d.Anchors.Min(a => a.At), hi = d.Anchors.Max(a => a.At), w = v.Mm(4);
        return d.Horizontal ? new Box(lo, d.LineAt - w, hi, d.LineAt + w) : new Box(d.LineAt - w, lo, d.LineAt + w, hi);
    }

    public static Box TextBox(DText t, DrawingView v)
    {
        var lines = t.Text.Split('\n');
        double w = v.Mm(lines.Max(l => l.Length) * t.HeightMm * CharWidth + (t.Ellipse ? 6 : 0)), h = v.Mm(lines.Length * t.HeightMm * 1.6 + (t.Ellipse ? 3 : 0));
        double x0 = t.Align switch { "left" => t.At.X, "right" => t.At.X - w, _ => t.At.X - w / 2 };
        return new Box(x0, t.At.Y - h / 2, x0 + w, t.At.Y + h / 2);
    }

    public static Box LabelBox(DLabel l, DrawingView v, double textMm)
    {
        var lines = l.Text.Split('\n');
        double w = v.Mm(lines.Max(x => x.Length) * textMm * CharWidth), h = v.Mm(lines.Length * textMm * 1.6);
        var text = l.Left ? new Box(l.TextAt.X - w, l.TextAt.Y - h / 2, l.TextAt.X, l.TextAt.Y + h / 2)
                          : new Box(l.TextAt.X, l.TextAt.Y - h / 2, l.TextAt.X + w, l.TextAt.Y + h / 2);
        return text.Union(Box.FromCorners(l.Elbow, l.Target));
    }

    /// <summary>Lengths as the Manawala sheets write them: 5', 4 1/2", 2'-6", 7'-0" for door sizes.</summary>
    public static string Compact(double inches)
    {
        string f = FtIn.Format(inches);
        return f;
    }

    /// <summary>"MALE TOILET" is written M/TOILET in the Manawala plans; other names are kept.</summary>
    public static string RoomName(string name) => name.ToUpperInvariant() switch
    {
        "MALE TOILET" => "M/TOILET",
        "FEMALE TOILET" => "F/TOILET",
        _ => name.ToUpperInvariant(),
    };
}
