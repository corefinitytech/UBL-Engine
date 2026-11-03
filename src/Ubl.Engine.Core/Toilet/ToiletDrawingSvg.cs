using System.Globalization;
using System.Text;
using Ubl.Engine.Core.Geometry;

namespace Ubl.Engine.Core.Toilet;

/// <summary>
/// Preview of one toilet sheet as SVG (A3, paper millimetres): the walls, door and fixtures as Revit will show
/// them, plus every line, note and dimension the drawing planner made. For checking the drawings without Revit.
/// </summary>
public static class ToiletDrawingSvg
{
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Render(ToiletDrawingSet set, DrawingSheet sheet)
    {
        var v = set.View(sheet.ViewKey);
        var plan = set.Plan; var spec = plan.Spec; var fmt = set.Sheet;
        double k = 25.4 / v.Scale.Denominator;
        var (cx, cy) = fmt.DrawCenter;
        var e = v.Extent;
        double PX(double x) => cx + (x - e.Center.X) * k;
        double PY(double y) => fmt.Height - (cy + (y - e.Center.Y) * k);
        string P(Pt p) => $"{F(PX(p.X))},{F(PY(p.Y))}";

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' width='{F(fmt.Width * 3)}' height='{F(fmt.Height * 3)}' viewBox='0 0 {F(fmt.Width)} {F(fmt.Height)}' font-family='Arial, Helvetica, sans-serif'>");
        sb.Append("<defs><pattern id='hatch' width='1.6' height='1.6' patternUnits='userSpaceOnUse' patternTransform='rotate(45)'><line x1='0' y1='0' x2='0' y2='1.6' stroke='#333' stroke-width='0.12'/></pattern>");
        sb.Append("<pattern id='conc' width='3' height='3' patternUnits='userSpaceOnUse'><circle cx='0.7' cy='0.8' r='0.18' fill='#555'/><circle cx='2.1' cy='2.2' r='0.12' fill='#555'/><path d='M1.6 0.6 l0.5 0.3 l-0.4 0.3z' fill='#777'/></pattern>");
        sb.Append("<marker id='arr' viewBox='0 0 10 10' refX='10' refY='5' markerWidth='4' markerHeight='4' orient='auto-start-reverse'><path d='M0,1 L10,5 L0,9 z' fill='#111'/></marker></defs>");
        sb.Append($"<rect width='{F(fmt.Width)}' height='{F(fmt.Height)}' fill='white'/>");
        // frame and a placeholder title strip in the Manawala positions
        sb.Append($"<rect x='{F(fmt.InnerX0)}' y='{F(fmt.Height - fmt.InnerY1)}' width='{F(fmt.InnerX1 - fmt.InnerX0)}' height='{F(fmt.InnerY1 - fmt.InnerY0)}' fill='none' stroke='#000' stroke-width='0.5'/>");
        sb.Append($"<line x1='{F(fmt.InnerX0)}' y1='{F(fmt.Height - fmt.TitleBlockTop)}' x2='{F(fmt.InnerX1)}' y2='{F(fmt.Height - fmt.TitleBlockTop)}' stroke='#000' stroke-width='0.35'/>");
        double ty = fmt.Height - fmt.TitleBlockTop;
        sb.Append($"<text x='{F(fmt.InnerX0 + 3)}' y='{F(ty + 7)}' font-size='3'>Title: {Esc(sheet.Name)}</text>");
        sb.Append($"<text x='{F(fmt.InnerX0 + 3)}' y='{F(ty + 14)}' font-size='2.6'>Client: UNITED BANK LIMITED</text>");
        sb.Append($"<text x='{F(fmt.InnerX0 + 150)}' y='{F(ty + 7)}' font-size='2.4'>Drg No.</text><text x='{F(fmt.InnerX0 + 150)}' y='{F(ty + 14)}' font-size='4' font-weight='bold'>{Esc(sheet.Number)}</text>");
        sb.Append($"<text x='{F(fmt.InnerX0 + 200)}' y='{F(ty + 7)}' font-size='2.4'>Scale: {Esc(v.Scale.Name)}</text>");
        sb.Append($"<text x='{F(fmt.InnerX0 + 3)}' y='{F(ty + 24)}' font-size='2.2' fill='#a00'>PLACEHOLDER TITLE BLOCK: the firm's A3 title block replaces this strip</text>");

        if (v.Elevation is { } f) Elevation(sb, set, v, f, P);
        else Plan(sb, set, v, P);

        // ---- annotation from the planner
        foreach (var fill in v.Fills)
        {
            string style = fill.Kind switch
            {
                FillKind.Threshold => "fill='#666' stroke='#000' stroke-width='0.2'",
                FillKind.TileSample => "fill='url(#hatch)' stroke='#000' stroke-width='0.2'",
                FillKind.Marker => "fill='#000'",
                _ => "fill='url(#conc)' stroke='#000' stroke-width='0.3'",
            };
            sb.Append($"<polygon points='{string.Join(" ", fill.Loop.Select(P))}' {style}/>");
        }
        foreach (var l in v.Lines)
        {
            string style = l.Style switch
            {
                DLineStyle.Tile => "stroke='#555' stroke-width='0.13'",
                DLineStyle.Medium => "stroke='#000' stroke-width='0.35'",
                DLineStyle.Hidden => "stroke='#000' stroke-width='0.18' stroke-dasharray='1 0.6'",
                _ => "stroke='#000' stroke-width='0.18'",
            };
            sb.Append($"<line x1='{F(PX(l.A.X))}' y1='{F(PY(l.A.Y))}' x2='{F(PX(l.B.X))}' y2='{F(PY(l.B.Y))}' {style}/>");
        }
        foreach (var c in v.Circles.Concat(v.SymbolCircles))
            sb.Append($"<circle cx='{F(PX(c.Center.X))}' cy='{F(PY(c.Center.Y))}' r='{F(c.Radius * k)}' fill='none' stroke='#000' stroke-width='0.2'/>");
        foreach (var l in v.SymbolLines)
            sb.Append($"<line x1='{F(PX(l.A.X))}' y1='{F(PY(l.A.Y))}' x2='{F(PX(l.B.X))}' y2='{F(PY(l.B.Y))}' stroke='#000' stroke-width='0.2' {(l.Style == DLineStyle.Hidden ? "stroke-dasharray='1 0.6'" : "")}/>");
        foreach (var d in v.Dims) Dim(sb, d, PX, PY);
        foreach (var t in v.Texts) Text(sb, t, PX(t.At.X), PY(t.At.Y));
        foreach (var l in v.Labels)
        {
            sb.Append($"<polyline points='{P(l.TextAt)} {P(l.Elbow)} {P(l.Target)}' fill='none' stroke='#111' stroke-width='0.18' marker-end='url(#arr)'/>");
            Text(sb, new DText(l.Text, l.TextAt, set.Rules.LabelMm, l.Left ? "right" : "left"), PX(l.TextAt.X) + (l.Left ? -1 : 1), PY(l.TextAt.Y));
        }
        if (!v.FitsSheet) sb.Append($"<text x='20' y='20' font-size='4' fill='red'>DOES NOT FIT THE SHEET</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void Plan(StringBuilder sb, ToiletDrawingSet set, DrawingView v, Func<Pt, string> P)
    {
        var plan = set.Plan; var spec = plan.Spec; var door = plan.Door!;
        double W = spec.Width, D = spec.Depth, tS = spec.WallSouth, tE = spec.WallEast, tN = spec.WallNorth, tW = spec.WallWest;
        var walls = new Dictionary<Side, Box>
        {
            [Side.South] = new(-tW, -tS, W + tE, 0), [Side.North] = new(-tW, D, W + tE, D + tN),
            [Side.West] = new(-tW, 0, 0, D), [Side.East] = new(W, 0, W + tE, D),
        };
        var dwl = RoomGeometry.Wall(spec, door.Wall);
        var gap = Box.FromCorners(dwl.At(door.S0, 0), dwl.At(door.S1, -spec.WallThickness(door.Wall)));
        foreach (var (side, b) in walls)
        {
            var parts = new List<Box> { b };
            if (side == door.Wall)
                parts = side is Side.South or Side.North
                    ? new List<Box> { new(b.X0, b.Y0, gap.X0, b.Y1), new(gap.X1, b.Y0, b.X1, b.Y1) }
                    : new List<Box> { new(b.X0, b.Y0, b.X1, gap.Y0), new(b.X0, gap.Y1, b.X1, b.Y1) };
            foreach (var p in parts) sb.Append($"<polygon points='{string.Join(" ", p.Corners().Select(P))}' fill='url(#hatch)' stroke='#000' stroke-width='0.35'/>");
        }
        if (v.Kind is DrawingKind.Layout or DrawingKind.Sanitary)
        {
            var hinge = dwl.At(door.HingeS, 0);
            var latch = dwl.At(door.LatchS, 0);
            var open = hinge + dwl.Normal * (door.SwingIn ? door.Width : -door.Width);
            sb.Append($"<line x1='{P(hinge).Split(',')[0]}' y1='{P(hinge).Split(',')[1]}' x2='{P(open).Split(',')[0]}' y2='{P(open).Split(',')[1]}' stroke='#000' stroke-width='0.3'/>");
            sb.Append($"<path d='M{P(open)} A{F(door.Width * 25.4 / v.Scale.Denominator)},{F(door.Width * 25.4 / v.Scale.Denominator)} 0 0 {(Sweep(dwl, door) ? 1 : 0)} {P(latch)}' fill='none' stroke='#000' stroke-width='0.15' stroke-dasharray='1 0.6'/>");
        }
        if (v.ShowFixtures)
            foreach (var i in plan.Items.Where(i => i.Mount != Mount.Ceiling))
                sb.Append($"<polygon points='{string.Join(" ", i.Plan.Corners().Select(P))}' fill='{(i.Role == Roles.Vanity ? "none" : "#f4f4f4")}' stroke='#000' stroke-width='0.22'/>");
    }

    private static bool Sweep(WallLine wl, DoorGeometry door)
    {
        // SVG sweep flag for the arc from the open leaf back to the latch jamb: the direction depends on which way the wall runs
        var dir = door.LatchS > door.HingeS ? wl.Dir : wl.Dir * -1;
        var n = wl.Normal * (door.SwingIn ? 1 : -1);
        return dir.Cross(n) < 0;
    }

    private static void Elevation(StringBuilder sb, ToiletDrawingSet set, DrawingView v, ElevationFrame f, Func<Pt, string> P)
    {
        var spec = set.Plan.Spec; var door = set.Plan.Door!;
        double L = f.Length, slab = spec.SlabHeight, CH = spec.CeilingHeight;
        foreach (var b in new[] { new Box(-f.LeftWall, 0, 0, slab), new Box(L, 0, L + f.RightWall, slab) })
            sb.Append($"<polygon points='{string.Join(" ", b.Corners().Select(P))}' fill='url(#hatch)' stroke='#000' stroke-width='0.35'/>");
        sb.Append($"<line x1='{P(new Pt(0, CH)).Split(',')[0]}' y1='{P(new Pt(0, CH)).Split(',')[1]}' x2='{P(new Pt(L, CH)).Split(',')[0]}' y2='{P(new Pt(L, CH)).Split(',')[1]}' stroke='#000' stroke-width='0.5'/>");
        var wl = RoomGeometry.Wall(spec, f.Wall);
        foreach (var i in set.Plan.Items)
        {
            if (i.Mount == Mount.Ceiling || ToiletChecker.IsFlush(i)) continue;
            var cs = i.Plan.Corners();
            if (cs.Min(c => wl.DOf(c)) >= f.CutDepth) continue;
            double u0 = Math.Max(0, f.U(cs.Max(c => wl.SOf(c)))), u1 = Math.Min(L, f.U(cs.Min(c => wl.SOf(c))));
            if (u1 - u0 < 0.1) continue;
            double z0 = i.Role == Roles.Vanity ? i.Z1 - 1.5 : i.Z0;
            string fill = i.Role == Roles.Mirror ? "#cfe0ea" : "#9a9a9a";
            sb.Append($"<polygon points='{string.Join(" ", new Box(u0, z0, u1, i.Z1).Corners().Select(P))}' fill='{fill}' stroke='#000' stroke-width='0.2'/>");
        }
        if (door.Wall == f.Wall)
        {
            var d = new Box(f.U(door.S1), 0, f.U(door.S0), door.Height);
            sb.Append($"<polygon points='{string.Join(" ", d.Corners().Select(P))}' fill='none' stroke='#000' stroke-width='0.3'/>");
            sb.Append($"<polygon points='{string.Join(" ", new Box(d.X0 - 2, 0, d.X1 + 2, d.Y1 + 2).Corners().Select(P))}' fill='none' stroke='#000' stroke-width='0.2'/>");
        }
    }

    private static void Dim(StringBuilder sb, DDim d, Func<double, double> PX, Func<double, double> PY)
    {
        var a = d.Anchors.Select(x => x.At).ToList();
        if (d.Horizontal)
        {
            double y = PY(d.LineAt);
            sb.Append($"<line x1='{F(PX(a.First()))}' y1='{F(y)}' x2='{F(PX(a.Last()))}' y2='{F(y)}' stroke='#000' stroke-width='0.15'/>");
            foreach (var x in a) sb.Append($"<line x1='{F(PX(x) - 0.8)}' y1='{F(y + 0.8)}' x2='{F(PX(x) + 0.8)}' y2='{F(y - 0.8)}' stroke='#000' stroke-width='0.3'/><line x1='{F(PX(x))}' y1='{F(y - 1.5)}' x2='{F(PX(x))}' y2='{F(y + 1.5)}' stroke='#000' stroke-width='0.12'/>");
            for (int i = 0; i + 1 < a.Count; i++)
                sb.Append($"<text x='{F((PX(a[i]) + PX(a[i + 1])) / 2)}' y='{F(y - 0.9)}' font-size='2' text-anchor='middle'>{Esc(DimText(a[i + 1] - a[i]))}</text>");
        }
        else
        {
            double x = PX(d.LineAt);
            sb.Append($"<line x1='{F(x)}' y1='{F(PY(a.First()))}' x2='{F(x)}' y2='{F(PY(a.Last()))}' stroke='#000' stroke-width='0.15'/>");
            foreach (var yv in a) sb.Append($"<line x1='{F(x - 0.8)}' y1='{F(PY(yv) + 0.8)}' x2='{F(x + 0.8)}' y2='{F(PY(yv) - 0.8)}' stroke='#000' stroke-width='0.3'/><line x1='{F(x - 1.5)}' y1='{F(PY(yv))}' x2='{F(x + 1.5)}' y2='{F(PY(yv))}' stroke='#000' stroke-width='0.12'/>");
            for (int i = 0; i + 1 < a.Count; i++)
            {
                double ym = (PY(a[i]) + PY(a[i + 1])) / 2;
                sb.Append($"<text x='{F(x - 0.9)}' y='{F(ym)}' font-size='2' text-anchor='middle' transform='rotate(-90 {F(x - 0.9)} {F(ym)})'>{Esc(DimText(a[i + 1] - a[i]))}</text>");
            }
        }
    }

    /// <summary>Dimension text the way the Manawala sheets write it: 4 1/2", 5', 1'-6".</summary>
    public static string DimText(double inches)
    {
        double v = Math.Round(Math.Abs(inches) * 2) / 2;
        if (v < 12) return (v % 1 == 0 ? $"{v:0}" : $"{Math.Floor(v):0} 1/2") + "\"";
        int ft = (int)(v / 12); double rest = v - ft * 12;
        if (rest == 0) return $"{ft}'";
        return $"{ft}'-{(rest % 1 == 0 ? $"{rest:0}" : $"{Math.Floor(rest):0} 1/2")}\"";
    }

    private static void Text(StringBuilder sb, DText t, double x, double y)
    {
        var lines = t.Text.Split('\n');
        double lh = t.HeightMm * 1.5;
        string anchor = t.Align switch { "left" => "start", "right" => "end", _ => "middle" };
        double y0 = y - (lines.Length - 1) * lh / 2 + t.HeightMm * 0.36;
        for (int i = 0; i < lines.Length; i++)
            sb.Append($"<text x='{F(x)}' y='{F(y0 + i * lh)}' font-size='{F(t.HeightMm)}' text-anchor='{anchor}'{(t.Underline ? " text-decoration='underline'" : "")}>{Esc(lines[i])}</text>");
        if (t.Ellipse)
        {
            double w = lines.Max(l => l.Length) * t.HeightMm * ToiletDrawingPlanner.CharWidth / 2 + 3, h = t.HeightMm * 1.1;
            sb.Append($"<ellipse cx='{F(x)}' cy='{F(y)}' rx='{F(w)}' ry='{F(h)}' fill='none' stroke='#000' stroke-width='0.2'/>");
        }
    }

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
