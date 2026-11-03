using Ubl.Engine.Core.Cad;
using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Core.Sheets;
using Ubl.Engine.Core.Shell;
using Ubl.Engine.Core.Units;
using Xunit;

namespace Ubl.Engine.Core.Tests;

public class UnitsTests
{
    [Theory]
    [InlineData(191.5, "15'-11 1/2\"")]
    [InlineData(396.0, "33'-0\"")]
    [InlineData(1062.5, "88'-6 1/2\"")]
    [InlineData(25.0, "2'-1\"")]
    [InlineData(0.0, "0'-0\"")]
    public void Formats_feet_inches(double inches, string expected) => Assert.Equal(expected, FtIn.Format(inches));

    [Theory]
    [InlineData("10'-8\"", 128.0)]
    [InlineData("+3'-0\"", 36.0)]
    [InlineData("7'-11 1/2\"", 95.5)]
    [InlineData("10\"", 10.0)]
    [InlineData("-1'-6\"", -18.0)]
    public void Parses_feet_inches(string text, double expected) => Assert.Equal(expected, FtIn.Parse(text)!.Value, 6);

    [Fact]
    public void Rejects_non_lengths() => Assert.Null(FtIn.Parse("LIFT"));
}

public class TextDecodingTests
{
    [Theory]
    [InlineData("{\\fTechnic|b1|i0|c2|p2;\\LPARKING 40' WIDE...}", "PARKING 40' WIDE...")]
    [InlineData("\\A1;RECORD\\P12'-7{\\H0.7x;\\S1/2;}\"X5'-0\"", "RECORD\n12'-7 1/2\"X5'-0\"")]
    [InlineData(" \\P LEVEL = +3'-0\"", " \n LEVEL = +3'-0\"")]
    [InlineData("\\A1;\\pxqc;{\\C3;GREETER }", "GREETER ")]
    [InlineData("50\\U+00B0 C", "50\u00B0 C")]
    public void Decodes_mtext(string raw, string expected) => Assert.Equal(expected, MTextDecoder.Decode(raw));

    [Fact]
    public void Decodes_text_control_codes() => Assert.Equal("\u00D8 12 \u00B1 2", MTextDecoder.DecodeText("%%c 12 %%p 2"));
}

public class NorthTests
{
    private static CadText T(string s, double x, double y) => new("Text", new Pt(x, y), s);

    [Fact]
    public void Reads_north_from_a_compass()
    {
        // the new-branch compass: N above S, E to the right, about 0.7 degrees clockwise
        var texts = new List<CadText> { T("N", 26298.9, -6315.8), T("E", 26331.8, -6362.3), T("W", 26266.8, -6374.2), T("S", 26297.5, -6424.7) };
        var (deg, _) = ShellRecogniser.FindNorth(texts);
        Assert.NotNull(deg);
        Assert.Equal(-0.7, deg!.Value, 1);
    }

    [Fact]
    public void Rejects_a_mirrored_compass()
    {
        var texts = new List<CadText> { T("N", 0, 100), T("E", -50, 50), T("W", 50, 50), T("S", 0, 0) };
        Assert.Null(ShellRecogniser.FindNorth(texts).Degrees);
    }
}

public class GeometryTests
{
    [Fact]
    public void L_shaped_wall_splits_into_two_rectangles()
    {
        // rear wall 177 x 9 with a 9in leg running 80in down at its right end
        var l = new List<Pt> { new(0, 0), new(177, 0), new(177, -89), new(168, -89), new(168, -9), new(0, -9) };
        var parts = Poly.OrthoDecompose(l);
        Assert.Equal(2, parts.Count);
        Assert.Equal(Poly.Area(l), parts.Sum(p => p.Width * p.Height), 6);
        Assert.All(parts, p => Assert.Equal(9, p.Short, 6));
    }

    [Fact]
    public void Face_thickness_ignores_a_window_notch()
    {
        // 9in left wall, 191.5in long, with a 4.5in deep notch 40in long
        var w = new List<Pt> { new(0, 0), new(9, 0), new(9, 60), new(4.5, 60), new(4.5, 100), new(9, 100), new(9, 191.5), new(0, 191.5) };
        Assert.Equal(9.0, ShellRecogniser.FaceThickness(w, Face.Left, 0, 396, 191.5, 0), 6);
    }
}

/// <summary>Builds a branch drawing the way the architects draw it: hatched columns and walls, dashed beam pairs, notes.</summary>
internal static class Synthetic
{
    public static CadDrawing Shell(double width, IReadOnlyList<double> bays, double colW = 18, double colD = 25, double wall = 9, double ox = 26392.8, double oy = -6324.2)
    {
        var d = new CadDrawing { Source = "synthetic" };
        double y = oy;
        var rows = new List<(double y0, double y1)>();
        for (int i = 0; i <= bays.Count; i++)
        {
            rows.Add((y, y + colD));
            y += colD + (i < bays.Count ? bays[i] : 0);
        }
        foreach (var (y0, y1) in rows)
        {
            d.Hatches.Add(Rect("COL", ox, y0, ox + colW, y1));
            d.Hatches.Add(Rect("COL", ox + width - colW, y0, ox + width, y1));
            double yc = (y0 + y1) / 2;
            d.Lines.Add(new CadLine("BEAM", new Pt(ox + colW, yc - 9), new Pt(ox + width - colW, yc - 9)));
            d.Lines.Add(new CadLine("BEAM", new Pt(ox + colW, yc + 9), new Pt(ox + width - colW, yc + 9)));
        }
        for (int i = 0; i < bays.Count; i++)
        {
            double y0 = rows[i].y1, y1 = rows[i + 1].y0;
            d.Hatches.Add(Rect("WALL", ox, y0, ox + wall, y1));
            d.Hatches.Add(Rect("WALL", ox + width - wall, y0, ox + width, y1));
            d.Dimensions.Add(new CadDimension("DIM", bays[i]));
        }
        d.Dimensions.Add(new CadDimension("DIM", colD));
        d.Dimensions.Add(new CadDimension("DIM", width));
        d.Dimensions.Add(new CadDimension("DIM", y - oy));
        double cx = ox + width / 2, cy = (oy + y) / 2;
        d.Texts.Add(new CadText("Text", new Pt(cx, cy), "SLAB HEIGHT 10'-8\""));
        d.Texts.Add(new CadText("Text", new Pt(cx, cy - 10), "DROP BEAM 10\""));
        d.Texts.Add(new CadText("Text", new Pt(cx, cy - 30), "LEVEL = +3'-0\""));
        return d;
    }

    private static CadHatch Rect(string layer, double x0, double y0, double x1, double y1) =>
        new(layer, "SOLID", new[] { (IReadOnlyList<Pt>)new[] { new Pt(x0, y0), new Pt(x1, y0), new Pt(x1, y1), new Pt(x0, y1) } });
}

public class RecogniserTests
{
    [Fact]
    public void Recognises_a_typical_branch_shell()
    {
        var bays = new[] { 191.5, 192, 191, 190, 149 };
        var s = new ShellRecogniser().Recognise(Synthetic.Shell(396, bays));
        Assert.Equal(12, s.Columns.Count);
        Assert.Equal(396, s.Envelope.Width, 6);
        Assert.Equal(bays.Sum() + 6 * 25, s.Envelope.Depth, 6);
        Assert.Equal(10, s.Walls.Count);
        Assert.Equal(9.0, s.WallThickness, 6);
        Assert.Equal(6, s.Beams.Count);
        Assert.All(s.Beams, b => Assert.Equal(18, b.Width, 6));
        Assert.Equal(10.0, s.BeamDrop!.Value, 6);
        Assert.Equal(128.0, s.SlabHeight!.Value, 6);
        Assert.Equal(36.0, s.FloorLevel!.Value, 6);
        Assert.Contains(s.Checks, c => c.Name == "column bay chain" && c.Status == CheckStatus.Pass);
    }

    /// <summary>
    /// The dynamic-dimension proof for the shell module: every width from 16' to 40' in 6" steps, three to seven
    /// bays of 10' to 20'. Each case must come back with the exact envelope, column count and beam count.
    /// </summary>
    [Fact]
    public void Sweep_of_widths_and_bays_recovers_every_envelope_exactly()
    {
        var recogniser = new ShellRecogniser();
        int cases = 0;
        for (double w = 192; w <= 480; w += 6)
            for (int n = 3; n <= 7; n++)
                for (double bay = 120; bay <= 240; bay += 20)
                {
                    var bays = Enumerable.Range(0, n).Select(i => bay + (i % 2) * 6.5).ToArray();
                    var s = recogniser.Recognise(Synthetic.Shell(w, bays));
                    Assert.Equal(w, s.Envelope.Width, 6);
                    Assert.Equal(bays.Sum() + (n + 1) * 25, s.Envelope.Depth, 6);
                    Assert.Equal(2 * (n + 1), s.Columns.Count);
                    Assert.Equal(n + 1, s.Beams.Count);
                    Assert.Equal(2 * n, s.Walls.Count);
                    Assert.NotNull(ScalePicker.Pick(s.Envelope.Width, s.Envelope.Depth, SheetFormat.A3Portrait));
                    cases++;
                }
        Assert.Equal(49 * 5 * 7, cases);
    }

    [Fact]
    public void Refuses_a_drawing_without_columns()
    {
        var d = new CadDrawing { Source = "empty" };
        Assert.Throws<RecognitionException>(() => new ShellRecogniser().Recognise(d));
    }

    [Fact]
    public void Verandah_columns_stay_outside_the_covered_area()
    {
        var d = Synthetic.Shell(396, new[] { 190.0, 190, 149 });
        // two 24x24 columns 100in in front of the shopfront, with no side walls between
        d.Hatches.Add(new CadHatch("COL", "SOLID", new[] { (IReadOnlyList<Pt>)new Box(26392.8, -6448.2, 26416.8, -6424.2).Corners() }));
        d.Hatches.Add(new CadHatch("COL", "SOLID", new[] { (IReadOnlyList<Pt>)new Box(26764.8, -6448.2, 26788.8, -6424.2).Corners() }));
        var s = new ShellRecogniser().Recognise(d);
        Assert.Equal(-6324.2, s.Envelope.YFront, 6);
        Assert.Equal(-6448.2, s.Envelope.YFrontAll, 6);
    }
}

/// <summary>
/// Golden test on the real new-branch drawing. Set UBL_NEWBRANCH_DWG to the DWG path to run it; the numbers are
/// the ones the reference spike produced and the architect's dimension strings confirm.
/// </summary>
public class NewBranchGoldenTests
{
    [Fact]
    public void New_branch_drawing_matches_the_reference()
    {
        var path = Environment.GetEnvironmentVariable("UBL_NEWBRANCH_DWG");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;   // skipped when the drawing is not on this machine

        var s = new ShellRecogniser().Recognise(Ubl.Engine.Dwg.AcadReader.Read(path));
        Assert.Equal(396, s.Envelope.Width, 2);
        Assert.Equal(1062.5, s.Envelope.Depth, 2);
        Assert.Equal(14, s.Columns.Count);
        Assert.Equal(10, s.Columns.Count(c => c.Size == "18\"X25\""));
        Assert.Equal(2, s.Columns.Count(c => c.Size == "18\"X24\""));
        Assert.Equal(2, s.Columns.Count(c => c.Size == "24\"X24\""));
        Assert.Equal(12, s.Walls.Count);
        Assert.Equal(9.0, s.WallThickness, 6);
        Assert.Equal(9, s.Beams.Count);
        Assert.Single(s.Cores, c => c.Label == "LIFT");
        Assert.Equal(128.0, s.SlabHeight!.Value, 6);
        Assert.Equal(10.0, s.BeamDrop!.Value, 6);
        Assert.Equal(4, s.Stairs.Count(x => x.Kind == StairKind.Flight));
        Assert.Single(s.Stairs, x => x.Kind == StairKind.Ramp);
        Assert.Single(s.Stairs, x => x.Kind == StairKind.Spiral);
        Assert.Contains(s.Checks, c => c.Name == "column bay chain" && c.Status == CheckStatus.Pass);
        Assert.Contains(s.Checks, c => c.Name == "covered area" && c.Status == CheckStatus.Warn);
        Assert.Equal(-0.7, s.NorthDegrees!.Value, 1);
        Assert.DoesNotContain(s.MissingInputs, m => m.StartsWith("north"));
        Assert.Contains(s.SourceTexts, t => t == "PARKING 40' WIDE...");

        var plan = Ar21Planner.Plan(s, new ProjectInputs());
        Assert.Equal(128, plan.Scale.Denominator);
        Assert.Equal(6, plan.Dimensions.Count);
        Assert.Equal(9, plan.Texts.Count(t => t.Role == TextRole.BeamLabel));
        Assert.Contains(plan.Texts, t => t.Text == "PARKING 40'-0\" WIDE...");
        Assert.DoesNotContain(plan.Texts, t => t.Text.StartsWith("N ?"));
    }
}
