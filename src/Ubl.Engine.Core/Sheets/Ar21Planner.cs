using System.Globalization;
using System.Text.RegularExpressions;
using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Core.Shell;
using Ubl.Engine.Core.Units;

namespace Ubl.Engine.Core.Sheets;

// AR-21 "As Built Layout Plan (Ground Floor)" planned in model coordinates (inches). The planner decides what
// goes where; the Revit layer only turns each item into a dimension, text note or detail line. Keeping the
// placement rules here means they are unit-testable and identical in the preview and in Revit.

/// <summary>Which face a dimension witnesses. Front is min Y, Back is max Y, as in Revit family templates.</summary>
public enum AnchorSide { Left, Right, Front, Back }
public enum AnchorKind { Column, PlotRear }

public sealed record Anchor(AnchorKind Kind, int Index, AnchorSide Side, double Coord);

/// <summary>Axis 'x': horizontal string measuring along X, LineCoord is its Y. Axis 'y': vertical string, LineCoord is its X.</summary>
public sealed record DimPlan(string Name, char Axis, double LineCoord, IReadOnlyList<Anchor> Anchors);

public enum TextRole { BeamLabel, ColumnTag, StairLabel, CoreLabel, LevelNote, SiteLabel, AreaNote, Note, Placeholder }

public sealed record TextPlan(string Text, Pt At, TextRole Role, double RotationDeg = 0, Pt? LeaderFrom = null, string Align = "center");

public enum LineStyle { Thin, Medium, Dashed }
public sealed record LinePlan(Pt A, Pt B, LineStyle Style);
public sealed record CirclePlan(Pt Center, double Radius, LineStyle Style);

public sealed class Ar21Plan
{
    public required ArchScale Scale { get; init; }
    public required SheetFormat Sheet { get; init; }
    /// <summary>Model region shown by the viewport: the sheet's drawing area at the chosen scale.</summary>
    public required Box Crop { get; init; }
    public List<DimPlan> Dimensions { get; } = new();
    public List<TextPlan> Texts { get; } = new();
    public List<LinePlan> Lines { get; } = new();
    public List<CirclePlan> Circles { get; } = new();
    public List<string> Warnings { get; } = new();
}

public static class Ar21Planner
{
    // paper sizes in mm (from the Manawala sheet)
    public const double TagHeight = 1.35, LabelHeight = 2.6, DimTier1 = 7, DimTier2 = 14, FrontTier2 = 13;

    public static Ar21Plan Plan(ShellModel shell, ProjectInputs inputs, SheetFormat? sheet = null, string beamLabel = "DROP BEAM {drop}")
    {
        sheet ??= SheetFormat.A3Portrait;
        var e = shell.Envelope;
        double yp = e.YPlotRear ?? e.YRear;
        var scale = ScalePicker.Pick(e.Width, yp - e.YFrontAll, sheet)
                    ?? throw new RecognitionException("The plan does not fit A3 at any standard scale. Use A2 or split the plan.");
        double k = scale.MmPerInch;
        double Mm(double mm) => mm / k;                 // paper millimetres to model inches

        double cx = (e.X0 + e.X1) / 2;
        double top = yp + Mm(16);
        var crop = new Box(cx - Mm(sheet.DrawWidth) / 2, top - Mm(sheet.DrawHeight), cx + Mm(sheet.DrawWidth) / 2, top);
        var plan = new Ar21Plan { Scale = scale, Sheet = sheet, Crop = crop };

        var cols = shell.Columns;
        int Idx(Column c) => cols.ToList().IndexOf(c);

        // ---- dimensions
        var leftCols = cols.Where(c => Math.Abs(c.Bounds.X0 - e.X0) < 1).OrderByDescending(c => c.Bounds.Y1).ToList();
        var rightCols = cols.Where(c => Math.Abs(c.Bounds.X1 - e.X1) < 1).OrderByDescending(c => c.Bounds.Y1).ToList();
        var frontCols = cols.Where(c => Math.Abs(c.Bounds.Y0 - e.YFrontAll) < 1).OrderBy(c => c.Bounds.X0).ToList();
        var plotRear = e.YPlotRear is double pr ? new Anchor(AnchorKind.PlotRear, 0, AnchorSide.Back, pr) : null;

        var leftChain = new List<Anchor>();
        if (plotRear is not null) leftChain.Add(plotRear);
        foreach (var c in leftCols)
        {
            leftChain.Add(new Anchor(AnchorKind.Column, Idx(c), AnchorSide.Back, c.Bounds.Y1));
            leftChain.Add(new Anchor(AnchorKind.Column, Idx(c), AnchorSide.Front, c.Bounds.Y0));
        }
        plan.Dimensions.Add(new DimPlan("left column chain", 'y', e.X0 - Mm(DimTier1), Dedup(leftChain)));

        if (rightCols.Count > 0)
        {
            var rear = rightCols[0];
            var coveredFront = rightCols.Where(c => c.Bounds.Y0 >= e.YFront - 1).OrderBy(c => c.Bounds.Y0).First();
            var front = rightCols[^1];
            var zones = new List<Anchor>();
            if (plotRear is not null) zones.Add(plotRear);
            zones.Add(new Anchor(AnchorKind.Column, Idx(rear), AnchorSide.Back, rear.Bounds.Y1));
            zones.Add(new Anchor(AnchorKind.Column, Idx(coveredFront), AnchorSide.Front, coveredFront.Bounds.Y0));
            zones.Add(new Anchor(AnchorKind.Column, Idx(front), AnchorSide.Front, front.Bounds.Y0));
            plan.Dimensions.Add(new DimPlan("right zones", 'y', e.X1 + Mm(DimTier1), Dedup(zones)));
            plan.Dimensions.Add(new DimPlan("right overall", 'y', e.X1 + Mm(DimTier2), Dedup(new List<Anchor> { zones[0], zones[^1] })));
        }
        if (frontCols.Count >= 2)
        {
            var chain = new List<Anchor>();
            foreach (var c in frontCols)
            {
                chain.Add(new Anchor(AnchorKind.Column, Idx(c), AnchorSide.Left, c.Bounds.X0));
                chain.Add(new Anchor(AnchorKind.Column, Idx(c), AnchorSide.Right, c.Bounds.X1));
            }
            plan.Dimensions.Add(new DimPlan("front chain", 'x', e.YFrontAll - Mm(DimTier1), Dedup(chain)));
            plan.Dimensions.Add(new DimPlan("front overall", 'x', e.YFrontAll - Mm(FrontTier2), new[] { chain[0], chain[^1] }));
        }
        if (leftCols.Count > 0 && rightCols.Count > 0)
            plan.Dimensions.Add(new DimPlan("rear overall", 'x', yp + Mm(5), new[]
            {
                new Anchor(AnchorKind.Column, Idx(leftCols[0]), AnchorSide.Left, leftCols[0].Bounds.X0),
                new Anchor(AnchorKind.Column, Idx(rightCols[0]), AnchorSide.Right, rightCols[0].Bounds.X1),
            }));

        // ---- obstacles for annotation placement
        var obstacles = new List<Box>();
        obstacles.AddRange(shell.Stairs.Where(s => s.Kind != StairKind.Ramp).Select(s => s.Bounds));
        obstacles.AddRange(shell.Cores.Select(c => c.Bounds));
        obstacles.AddRange(cols.Select(c => c.Bounds));
        Box TextBox(Pt at, string text, double hMm, bool vertical)
        {
            double w = Mm(TextWidthMm(text, hMm) + 1), h = Mm(hMm + 1);
            return vertical ? new Box(at.X - h / 2, at.Y - w / 2, at.X + h / 2, at.Y + w / 2)
                            : new Box(at.X - w / 2, at.Y - h / 2, at.X + w / 2, at.Y + h / 2);
        }

        // ---- beam labels: slide along the beam to the first spot clear of stairs, cores and columns
        foreach (var b in shell.Beams)
        {
            string text = beamLabel.Replace("{drop}", b.Drop is double dr ? (dr < 12 ? $"{dr:0.#}\"" : FtIn.Format(dr)) : "?");
            Pt? spot = null;
            foreach (double f in new[] { 0.5, 0.35, 0.65, 0.25, 0.75, 0.15, 0.85 })
            {
                var p = b.P0 + (b.P1 - b.P0) * f;
                var box = TextBox(p, text, TagHeight, b.IsVertical);
                if (!obstacles.Any(o => o.Intersects(box))) { spot = p; obstacles.Add(box); break; }
            }
            if (spot is null) plan.Warnings.Add($"beam label at {b.P0}-{b.P1} has no clear spot; placed at mid-span");
            plan.Texts.Add(new TextPlan(text, spot ?? b.P0 + (b.P1 - b.P0) * 0.5, TextRole.BeamLabel, b.IsVertical ? 90 : 0));
        }

        // ---- column tags: one per size, leader from the inner face towards the middle of the building
        var tagged = new HashSet<string>();
        double midY = (e.YRear + e.YFront) / 2;
        foreach (var c in cols.OrderByDescending(c => c.Bounds.Y1))
        {
            bool onLeft = Math.Abs(c.Bounds.X0 - e.X0) < 1, onRight = Math.Abs(c.Bounds.X1 - e.X1) < 1;
            if ((!onLeft && !onRight) || !tagged.Add(c.Size)) continue;
            double sx = onLeft ? 1 : -1, sy = c.Bounds.Center.Y < midY ? 1 : -1;
            var from = new Pt(onLeft ? c.Bounds.X1 : c.Bounds.X0, c.Bounds.Center.Y);
            var elbow = from + new Pt(sx * Mm(5), sy * Mm(4));
            plan.Lines.Add(new LinePlan(from, elbow, LineStyle.Thin));
            plan.Lines.Add(new LinePlan(elbow, elbow + new Pt(sx * Mm(1.5), 0), LineStyle.Thin));
            plan.Texts.Add(new TextPlan($"{c.Size} RCC\nCOLUMN", elbow + new Pt(sx * Mm(2), 0), TextRole.ColumnTag, 0, null, onLeft ? "left" : "right"));
        }

        // ---- RCC cores: cross in the shaft and the label
        foreach (var core in shell.Cores)
        {
            if (core.Shaft is Box s)
            {
                plan.Lines.Add(new LinePlan(new Pt(s.X0, s.Y0), new Pt(s.X1, s.Y1), LineStyle.Thin));
                plan.Lines.Add(new LinePlan(new Pt(s.X0, s.Y1), new Pt(s.X1, s.Y0), LineStyle.Thin));
            }
            var at = (core.Shaft ?? core.Bounds).Center;
            plan.Texts.Add(new TextPlan(core.Label, at - new Pt(0, Mm(2.6)), TextRole.CoreLabel));
        }

        // ---- stairs (2D: stair 3D needs basement and first-floor heights the drawing does not give)
        foreach (var st in shell.Stairs)
        {
            if (st.Kind == StairKind.Ramp)
            {
                plan.Warnings.Add($"ramp at {st.Bounds} treated as proposed (portable MS ramp) and left off the as-built");
                continue;
            }
            if (st.Kind == StairKind.Spiral && st.Center is Pt c)
            {
                plan.Circles.Add(new CirclePlan(c, st.Radius, LineStyle.Medium));
                plan.Circles.Add(new CirclePlan(c, Mm(0.35), LineStyle.Thin));
                for (int i = 0; i < st.Treads; i++)
                {
                    double a = 2 * Math.PI * i / st.Treads;
                    var u = new Pt(Math.Cos(a), Math.Sin(a));
                    plan.Lines.Add(new LinePlan(c + u * Mm(0.35), c + u * st.Radius, LineStyle.Thin));
                }
                plan.Texts.Add(new TextPlan("MS ROUND STAIR\n(TO BASEMENT)", c + new Pt(st.Radius + Mm(1.2), 0), TextRole.StairLabel, 0, null, "left"));
                continue;
            }
            var b = st.Bounds;
            foreach (var (p, q) in Edges(b)) plan.Lines.Add(new LinePlan(p, q, LineStyle.Medium));
            for (int i = 1; i < st.Treads; i++)
            {
                if (st.RunAxis == 'y') { double y = b.Y0 + b.Height * i / st.Treads; plan.Lines.Add(new LinePlan(new Pt(b.X0, y), new Pt(b.X1, y), LineStyle.Thin)); }
                else { double x = b.X0 + b.Width * i / st.Treads; plan.Lines.Add(new LinePlan(new Pt(x, b.Y0), new Pt(x, b.Y1), LineStyle.Thin)); }
            }
            if (st.Label is null || st.LabelAt is not Pt la) continue;
            // walking line starts at the end nearest the label and points along the run
            Pt start, end;
            double inset = Mm(0.8);
            if (st.RunAxis == 'y')
            {
                double xm = b.Center.X;
                bool fromBottom = Math.Abs(la.Y - b.Y0) < Math.Abs(la.Y - b.Y1);
                start = new Pt(xm, fromBottom ? b.Y0 + inset : b.Y1 - inset);
                end = new Pt(xm, fromBottom ? b.Y1 - inset : b.Y0 + inset);
            }
            else
            {
                double ym = b.Center.Y;
                bool fromLeft = Math.Abs(la.X - b.X0) < Math.Abs(la.X - b.X1);
                start = new Pt(fromLeft ? b.X0 + inset : b.X1 - inset, ym);
                end = new Pt(fromLeft ? b.X1 - inset : b.X0 + inset, ym);
            }
            plan.Lines.Add(new LinePlan(start, end, LineStyle.Thin));
            var dir = (end - start).Unit;
            var n = dir.Normal;
            double hl = Mm(1.4), hw = Mm(0.5);
            plan.Lines.Add(new LinePlan(end, end - dir * hl + n * hw, LineStyle.Thin));
            plan.Lines.Add(new LinePlan(end, end - dir * hl - n * hw, LineStyle.Thin));
            plan.Circles.Add(new CirclePlan(start, Mm(0.45), LineStyle.Thin));
            plan.Texts.Add(new TextPlan(st.Label.ToUpperInvariant(), la, TextRole.StairLabel));
        }

        // ---- site band: parking and road below the front dimensions (symbolic; the label carries the width)
        double bandTop = e.YFrontAll - Mm(17), bandBot = bandTop - Mm(26);
        double siteX0 = crop.X0 + Mm(4), siteX1 = crop.X1 - Mm(4);
        plan.Lines.Add(new LinePlan(new Pt(siteX0, bandBot), new Pt(siteX1, bandBot), LineStyle.Medium));
        plan.Lines.Add(new LinePlan(new Pt(siteX0, bandBot - Mm(14)), new Pt(siteX1, bandBot - Mm(14)), LineStyle.Medium));
        var parking = shell.SourceTexts.FirstOrDefault(t => t.Contains("PARKING", StringComparison.OrdinalIgnoreCase));
        if (parking is not null)
            plan.Texts.Add(new TextPlan(BareFeet().Replace(parking.ToUpperInvariant(), m => m.Groups[1].Value + "'-0\""), new Pt(cx, bandTop - Mm(19)), TextRole.SiteLabel));
        plan.Texts.Add(Required(inputs.RoadName, "[ROAD NAME: INPUT REQUIRED]", new Pt(cx, bandBot - Mm(9)), TextRole.SiteLabel));

        // ---- levels: outside levels move into the parking band so they never sit on a dimension string
        foreach (var lv in shell.Levels)
        {
            var at = lv.Inside ? lv.At : new Pt(Math.Clamp(lv.At.X, crop.X0 + Mm(40), crop.X1 - Mm(60)), bandTop - Mm(9));
            plan.Texts.Add(new TextPlan($"LEVEL = {lv.Text}", at, TextRole.LevelNote, 0, null, "left"));
        }

        // ---- neighbours
        double coveredMid = (e.YRear + e.YFront) / 2;
        plan.Texts.Add(Required(inputs.NeighbourLeft, "[NEIGHBOUR:\nINPUT]", new Pt(e.X0 - Mm(30), coveredMid), TextRole.SiteLabel));
        plan.Texts.Add(Required(inputs.NeighbourRight, "[NEIGHBOUR:\nINPUT]", new Pt(e.X1 + Mm(30), coveredMid), TextRole.SiteLabel));
        plan.Texts.Add(Required(inputs.NeighbourRear, "[REAR NEIGHBOUR: INPUT]", new Pt(cx, yp + Mm(9)), TextRole.SiteLabel));

        // ---- area statement and notes
        var areaAt = new Pt(crop.X0 + Mm(6), bandTop - Mm(2));
        plan.Texts.Add(new TextPlan($"GROUND FLOOR\nCOVERED AREA = {e.CoveredAreaSft.ToString("N0", CultureInfo.InvariantCulture)} SFT.", areaAt, TextRole.AreaNote, 0, null, "left"));
        if (e.StatedAreaSft is double sa && Math.Abs(e.CoveredAreaSft - sa) / sa > 0.005)
            plan.Texts.Add(new TextPlan($"CHECK: source drawing states {sa.ToString("N0", CultureInfo.InvariantCulture)} SFT", areaAt - new Pt(0, Mm(7)), TextRole.Placeholder, 0, null, "left"));
        if (shell.SlabHeight is double sh)
        {
            var notesAt = new Pt(crop.X1 - Mm(6), bandTop - Mm(2));
            string text = $"SLAB HEIGHT {FtIn.Format(sh)} (FROM F.F.L.)";
            if (shell.BeamDrop is double bd) text += $"\nDROP BEAMS {bd:0.#}\" DEEP, CLEAR UNDER BEAM {FtIn.Format(sh - bd)}";
            plan.Texts.Add(new TextPlan(text, notesAt, TextRole.Note, 0, null, "right"));
        }
        // ---- north: project input wins; else the drawing's own compass; else a red placeholder
        double? north = inputs.NorthDegrees ?? shell.NorthDegrees;
        if (inputs.NorthDegrees is double ni && shell.NorthDegrees is double ns && Math.Abs(ni - ns) > 5)
            plan.Warnings.Add($"project.json north ({ni:0.#}) differs from the drawing's compass ({ns:0.#}) by more than 5 degrees");
        var nc = new Pt(crop.X1 - Mm(22), bandBot - Mm(27));
        if (north is double deg)
        {
            double a = deg * Math.PI / 180;
            var dir = new Pt(-Math.Sin(a), Math.Cos(a));
            var side = dir.Normal;
            plan.Circles.Add(new CirclePlan(nc, Mm(5), LineStyle.Medium));
            var tip = nc + dir * Mm(4.5);
            var tail = nc - dir * Mm(4.5);
            plan.Lines.Add(new LinePlan(tail, tip, LineStyle.Medium));
            plan.Lines.Add(new LinePlan(tip, tip - dir * Mm(2.2) + side * Mm(1.2), LineStyle.Medium));
            plan.Lines.Add(new LinePlan(tip, tip - dir * Mm(2.2) - side * Mm(1.2), LineStyle.Medium));
            plan.Texts.Add(new TextPlan("N", nc + dir * Mm(7.5), TextRole.SiteLabel));
        }
        else
            plan.Texts.Add(new TextPlan("N ?\nNORTH: INPUT REQUIRED", nc, TextRole.Placeholder));

        return plan;
    }

    private static TextPlan Required(string? value, string placeholder, Pt at, TextRole role) =>
        string.IsNullOrWhiteSpace(value) ? new TextPlan(placeholder, at, TextRole.Placeholder) : new TextPlan(value.ToUpperInvariant(), at, role);

    private static IReadOnlyList<Anchor> Dedup(List<Anchor> a)
    {
        var r = new List<Anchor>();
        foreach (var x in a.OrderBy(x => x.Coord))
            if (r.Count == 0 || Math.Abs(r[^1].Coord - x.Coord) > 0.05) r.Add(x);
        return r;
    }

    private static IEnumerable<(Pt, Pt)> Edges(Box b)
    {
        var c = b.Corners();
        for (int i = 0; i < 4; i++) yield return (c[i], c[(i + 1) % 4]);
    }

    /// <summary>Approximate width of a single text line in mm for a sans font at cap height h.</summary>
    public static double TextWidthMm(string text, double h) =>
        text.Split('\n').Max(l => l.Length) * 0.78 * h;

    private static readonly Regex BareFeetRx = new("(\\d+)'(?!-)", RegexOptions.CultureInvariant);
    private static Regex BareFeet() => BareFeetRx;
}
