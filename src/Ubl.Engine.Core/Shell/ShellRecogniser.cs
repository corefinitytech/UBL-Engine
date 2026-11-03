using System.Globalization;
using System.Text.RegularExpressions;
using Ubl.Engine.Core.Cad;
using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Core.Units;

namespace Ubl.Engine.Core.Shell;

/// <summary>
/// Recognises the existing building shell from a branch drawing by geometry, hatch loops and layer aliases.
/// Block names are never trusted. Every number it cannot confirm against the architect's own dimensions or
/// notes is reported as a check, and anything it cannot classify is listed rather than guessed.
///
/// Current scope: orthogonal plots (walls parallel to X and Y). Raking plots such as Manawala need the
/// polygon envelope, which is the next step for this module.
/// </summary>
public sealed class ShellRecogniser
{
    private const double Tol = 0.25;
    private readonly LayerAliases _layers;

    public ShellRecogniser(LayerAliases? layers = null) => _layers = layers ?? LayerAliases.Default;

    public ShellModel Recognise(CadDrawing d)
    {
        var checks = new List<Check>();
        var unclassified = new List<Unclassified>();
        var texts = d.Texts
            .Select(t => t with { Text = Spaces().Replace(t.Text, " ").Trim() })
            .Where(t => t.Text.Length > 0)
            .ToList();

        // ---- 1. RCC hatches: small rectangles are columns; other loops are cores or proposed RCC
        var colRects = new List<Box>();
        var rccLoops = new List<IReadOnlyList<Pt>>();
        foreach (var h in d.Hatches.Where(h => _layers.Is(h.Layer, LayerKind.Column)))
            foreach (var loop in h.Loops.Where(l => l.Count >= 3))
            {
                var b = Box.Of(loop);
                if (Poly.IsAxisRectangle(loop) && b.Long <= 36) colRects.Add(b);
                else rccLoops.Add(loop);
            }
        var columns = new List<Column>();
        foreach (var b in colRects)
            if (!columns.Any(c => c.Bounds.SameAs(b, Tol))) columns.Add(new Column(b));
        if (colRects.Count != columns.Count)
            checks.Add(new Check("duplicate columns", CheckStatus.Info, $"{colRects.Count - columns.Count} duplicate column hatches ignored"));
        if (columns.Count < 2)
            throw new RecognitionException("Fewer than two RCC columns found. Check the column layer names in the layer alias file.");

        double x0 = columns.Min(c => c.Bounds.X0), x1 = columns.Max(c => c.Bounds.X1), yr = columns.Max(c => c.Bounds.Y1);

        // ---- 2. Wall hatch loops
        var wallLoops = d.Hatches.Where(h => _layers.Is(h.Layer, LayerKind.WallHatch))
            .SelectMany(h => h.Loops.Where(l => l.Count >= 3))
            .Select(l => (pts: l, b: Box.Of(l)))
            .ToList();
        bool OnLeft(Box b) => Math.Abs(b.X0 - x0) < 1 && b.Height >= b.Width;
        bool OnRight(Box b) => Math.Abs(b.X1 - x1) < 1 && b.Height >= b.Width;

        // ---- 3. Front of the covered area: walk each side's columns from the rear and stop at the first bay
        //         that has no side wall. Columns beyond that (verandah, porch) stand outside the covered area.
        double FrontOf(Func<Box, bool> colOnSide, Func<Box, bool> wallOnSide)
        {
            var sideCols = columns.Where(c => colOnSide(c.Bounds)).OrderByDescending(c => c.Bounds.Y1).ToList();
            var sideWalls = wallLoops.Where(w => wallOnSide(w.b)).Select(w => w.b).ToList();
            if (sideCols.Count == 0) return double.NaN;
            double front = sideCols[0].Bounds.Y0;
            for (int i = 0; i + 1 < sideCols.Count; i++)
            {
                double top = sideCols[i].Bounds.Y0, bottom = sideCols[i + 1].Bounds.Y1, gap = top - bottom;
                if (gap > Tol)
                {
                    double walled = sideWalls.Sum(w => Math.Max(0, Math.Min(w.Y1, top) - Math.Max(w.Y0, bottom)));
                    if (walled < 0.5 * gap) break;
                }
                front = sideCols[i + 1].Bounds.Y0;
            }
            return front;
        }
        double yfLeft = FrontOf(b => Math.Abs(b.X0 - x0) < 1, OnLeft);
        double yfRight = FrontOf(b => Math.Abs(b.X1 - x1) < 1, OnRight);
        if (double.IsNaN(yfLeft) || double.IsNaN(yfRight))
            throw new RecognitionException("Columns were not found on both side lines of the plot.");
        double yf = Math.Min(yfLeft, yfRight);
        if (Math.Abs(yfLeft - yfRight) > 1)
            checks.Add(new Check("covered front", CheckStatus.Warn,
                $"left walls end {FtIn.Format(yr - yfLeft)} from the rear, right walls {FtIn.Format(yr - yfRight)}; the deeper one is used"));
        double yfa = columns.Min(c => c.Bounds.Y0);

        // ---- 4. Boundary walls: a long face on the envelope line, thickness measured off that face
        var walls = new List<Wall>();
        foreach (var (pts, b) in wallLoops)
        {
            Face? face = null;
            if (OnLeft(b)) face = Face.Left;
            else if (OnRight(b)) face = Face.Right;
            else if (b.Width > b.Height && Math.Abs(b.Y1 - yr) < 1) face = Face.Rear;
            else if (b.Width > b.Height && Math.Abs(b.Y0 - yf) < 1) face = Face.Front;

            if (face is Face.Left or Face.Right && (b.Y0 < yf - 1 || b.Y1 > yr + 1))
            {
                unclassified.Add(new Unclassified($"wall hatch on the {face.ToString()!.ToLowerInvariant()} line but outside the covered area (verandah pier?)", b, Array.Empty<string>()));
                continue;
            }
            if (face is null)
            {
                if (StripThickness(pts) >= 8.5)
                    unclassified.Add(new Unclassified("interior 9in wall (existing or proposed? needs the architect)", b, Array.Empty<string>()));
                continue;
            }
            double t = FaceThickness(pts, face.Value, x0, x1, yr, yf);
            if (b.Long >= 12 && t is >= 4 and <= 14)
                walls.Add(new Wall(pts, b, t, face.Value, WallPieces(pts, b, face.Value, t, x0, x1, yr, yf, checks)));
            else
                unclassified.Add(new Unclassified($"{t:0.#}in thick hatch on the {face.Value.ToString().ToLowerInvariant()} face (pier or planter?)", b, Array.Empty<string>()));
        }
        double wallThickness = 0;
        if (walls.Count > 0)
        {
            wallThickness = walls.GroupBy(w => Math.Round(w.Thickness * 2) / 2).OrderByDescending(g => g.Count()).First().Key;
            foreach (var w in walls.Where(w => Math.Abs(w.Thickness - wallThickness) > 1).ToList())
            {
                walls.Remove(w);
                unclassified.Add(new Unclassified($"{w.Thickness:0.#}in piece on the {w.Face.ToString().ToLowerInvariant()} face, off the {wallThickness:0.#}in wall thickness", w.Bounds, Array.Empty<string>()));
            }
        }
        else checks.Add(new Check("boundary walls", CheckStatus.Warn, "no hatched boundary walls found on the envelope lines"));

        // ---- 5. RCC cores: a loop whose bounding box holds a LIFT or STAIR label is an existing core
        var cores = new List<Core>();
        foreach (var pts in rccLoops)
        {
            var b = Box.Of(pts);
            var labels = texts.Where(t => b.Contains(t.Insert)).Select(t => t.Text).ToList();
            string lab = string.Join(" ", labels).ToUpperInvariant();
            if (lab.Contains("LIFT") || lab.Contains("STAIR"))
            {
                var diag = d.Lines.Where(l => Math.Abs(l.A.X - l.B.X) > 12 && Math.Abs(l.A.Y - l.B.Y) > 12 && b.Contains(l.A) && b.Contains(l.B))
                                  .SelectMany(l => new[] { l.A, l.B }).ToList();
                cores.Add(new Core(pts, b, diag.Count > 0 ? Box.Of(diag) : null, lab.Contains("LIFT") ? "LIFT" : "STAIR", Pieces(pts, b, checks)));
            }
            else unclassified.Add(new Unclassified("RCC not labelled as lift or stair (taken as proposed)", b, labels));
        }

        // ---- 6. Beams: parallel line pairs on the beam layer, 6-30in apart, overlapping 60in or more
        var beams = FindBeams(d, checks);
        double? beamDrop = null, slabHeight = null;
        foreach (var t in texts)
        {
            string u = t.Text.ToUpperInvariant();
            var m = BeamNote().Match(u);
            if (m.Success && beamDrop is null) beamDrop = FtIn.Parse(m.Groups[1].Value);
            m = SlabNote().Match(u);
            if (m.Success) slabHeight = FtIn.Parse(m.Groups[1].Value);
        }
        beams = beams.Select(b => b with { Drop = beamDrop }).ToList();
        if (beams.Count > 0 && beamDrop is null)
            checks.Add(new Check("beam depth", CheckStatus.Warn, "beams found but no DROP/DOWN BEAM note gives their depth"));
        if (slabHeight is null)
            checks.Add(new Check("slab height", CheckStatus.Warn, "no SLAB HEIGHT note found; the 3D model needs it as an input"));

        var env = new Envelope(x0, x1, yr, yf, yfa, null);

        // ---- 7. Levels
        var levels = new List<LevelMark>();
        foreach (var t in texts)
            foreach (Match m in LevelNote().Matches(t.Text.ToUpperInvariant()))
            {
                string txt = m.Groups[1].Value.Replace(" ", "");
                if (FtIn.Parse(txt) is double v)
                    levels.Add(new LevelMark(v, txt, t.Insert, env.Covered.Contains(t.Insert)));
            }
        if (!levels.Any(l => l.Inside))
            checks.Add(new Check("floor level", CheckStatus.Warn, "no LEVEL note inside the building; finished floor level is an input"));

        // ---- 8. Stairs, ramps, spiral stairs
        var stairs = FindStairs(d, texts);

        // ---- 9. Yard behind the building: rear boundary line and its walls
        double? plotRear = null;
        foreach (var l in d.Lines)   // any layer: the boundary is often drawn on a site or low-wall layer
            if (l.IsHorizontal(Tol) && l.A.Y > yr + 24 && Math.Min(l.A.X, l.B.X) <= x0 + 1 && Math.Max(l.A.X, l.B.X) >= x1 - 1)
                plotRear = plotRear is null ? l.A.Y : Math.Max(plotRear.Value, l.A.Y);
        env = env with { YPlotRear = plotRear };
        var yardWalls = plotRear is null ? new List<YardWall>() : FindYardWalls(d, yr, plotRear.Value);

        // ---- 10. Checks against what the architect wrote
        double? stated = null;
        foreach (var t in texts)
        {
            var m = AreaNote().Match(t.Text.ToUpperInvariant());
            if (m.Success) stated = double.Parse(m.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
        }
        env = env with { StatedAreaSft = stated };
        if (stated is double s)
        {
            bool ok = Math.Abs(env.CoveredAreaSft - s) / s <= 0.005;
            string detail = $"computed {env.CoveredAreaSft:N0} sft ({FtIn.Format(env.Width)} x {FtIn.Format(env.Depth)} to outer faces) vs {s:N0} sft written on the drawing";
            if (!ok)
            {
                double alt = s * 144 / env.Width;
                detail += $"; the written figure equals a depth of {FtIn.Format(alt)}, {FtIn.Format(env.Depth - alt)} less than measured";
            }
            checks.Add(new Check("covered area", ok ? CheckStatus.Pass : CheckStatus.Warn, detail));
        }

        var arch = d.Dimensions.Select(x => x.Measurement).ToList();
        var left = columns.Where(c => Math.Abs(c.Bounds.X0 - x0) < 1).OrderByDescending(c => c.Bounds.Y1).ToList();
        var chain = new List<double>();
        double? prev = null;
        foreach (var c in left)
        {
            if (prev is double p) chain.Add(Math.Round(p - c.Bounds.Y1, 2));
            chain.Add(Math.Round(c.Depth, 2));
            prev = c.Bounds.Y0;
        }
        int matched = chain.Count(v => MatchesArchitect(v, arch));
        checks.Add(new Check("column bay chain", matched == chain.Count ? CheckStatus.Pass : CheckStatus.Warn,
            $"{matched}/{chain.Count} column sizes and bay lengths along the left wall agree with the architect's dimensions"));
        checks.Add(new Check("overall width", arch.Any(a => Math.Abs(a - env.Width) < Tol) ? CheckStatus.Pass : CheckStatus.Warn, $"overall width {FtIn.Format(env.Width)}"));
        checks.Add(new Check("covered depth", arch.Any(a => Math.Abs(a - env.Depth) < Tol) ? CheckStatus.Pass : CheckStatus.Warn, $"covered depth {FtIn.Format(env.Depth)}"));
        if (plotRear is double pr)
        {
            double pd = pr - yfa;
            checks.Add(new Check("plot depth", arch.Any(a => Math.Abs(a - pd) < Tol) ? CheckStatus.Pass : CheckStatus.Info,
                $"plot depth, rear boundary to front columns, {FtIn.Format(pd)}"));
        }

        var (northDeg, northAt) = FindNorth(texts);
        if (northDeg is double nd)
            checks.Add(new Check("north", CheckStatus.Info, $"compass found in the drawing: north is {Math.Abs(nd):0.#} degrees {(nd < 0 ? "clockwise" : "anticlockwise")} from up"));

        var missing = new List<string>();
        if (!texts.Any(t => RoadWord().IsMatch(t.Text.ToUpperInvariant()))) missing.Add("road name");
        if (northDeg is null) missing.Add("north direction (no compass found in the drawing)");
        missing.Add("neighbour labels (left, right, rear)");
        missing.Add("branch name and address for the title block");

        return new ShellModel
        {
            Source = d.Source,
            EntityCount = d.EntityCount,
            Envelope = env,
            Columns = columns,
            Walls = walls,
            WallThickness = wallThickness,
            YardWalls = yardWalls,
            Cores = cores,
            Beams = beams,
            BeamDrop = beamDrop,
            SlabHeight = slabHeight,
            Levels = levels,
            NorthDegrees = northDeg,
            NorthAt = northAt,
            Stairs = stairs,
            LeftBayChain = chain,
            Checks = checks,
            Unclassified = unclassified,
            MissingInputs = missing,
            SourceTexts = texts.Select(t => t.Text).ToList(),
        };
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// North from a compass drawn with single-letter N and S texts (E and W, when present, must agree, so a
    /// mirrored or unrelated set of letters is rejected). Often hidden inside a block; the reader explodes blocks.
    /// </summary>
    internal static (double? Degrees, Pt? At) FindNorth(List<CadText> texts)
    {
        var letters = texts.Where(t => t.Text.Trim().Length == 1).Select(t => (c: char.ToUpperInvariant(t.Text.Trim()[0]), p: t.Insert)).ToList();
        foreach (var n in letters.Where(x => x.c == 'N'))
        {
            var near = letters.Where(x => Pt.Dist(x.p, n.p) <= 300).ToList();
            var south = near.Where(x => x.c == 'S').OrderBy(x => Pt.Dist(x.p, n.p)).ToList();
            if (south.Count == 0) continue;
            var v = n.p - south[0].p;
            if (v.Length < 6) continue;
            var east = near.Where(x => x.c == 'E').ToList();
            var west = near.Where(x => x.c == 'W').ToList();
            if (east.Count > 0 && west.Count > 0 && v.Cross(east[0].p - west[0].p) > 0) continue;   // east must be to the right of north
            double deg = Math.Atan2(-v.X, v.Y) * 180 / Math.PI;
            return (Math.Round(deg, 1), new Pt((n.p.X + south[0].p.X) / 2, (n.p.Y + south[0].p.Y) / 2));
        }
        return (null, null);
    }

    private static IReadOnlyList<Box> Pieces(IReadOnlyList<Pt> pts, Box b, List<Check> checks)
    {
        if (Poly.IsOrthogonal(pts)) return Poly.OrthoDecompose(pts);
        checks.Add(new Check("non-orthogonal loop", CheckStatus.Warn, $"loop at {b} is not orthogonal; modelled by its bounding box"));
        return new[] { b };
    }

    /// <summary>
    /// A boundary wall as Revit walls: one full-thickness strip along the envelope face, plus any leg that leaves
    /// the face (the rear wall turning into the building). Window recesses and niches inside the strip are not
    /// modelled as separate thin walls; they are reported instead.
    /// </summary>
    internal static IReadOnlyList<Box> WallPieces(IReadOnlyList<Pt> pts, Box b, Face face, double t, double x0, double x1, double yr, double yf, List<Check> checks)
    {
        var strip = face switch
        {
            Face.Left => new Box(x0, b.Y0, x0 + t, b.Y1),
            Face.Right => new Box(x1 - t, b.Y0, x1, b.Y1),
            Face.Rear => new Box(b.X0, yr - t, b.X1, yr),
            _ => new Box(b.X0, yf, b.X1, yf + t),
        };
        var result = new List<Box> { strip };
        if (!Poly.IsOrthogonal(pts))
        {
            checks.Add(new Check("non-orthogonal wall", CheckStatus.Warn, $"wall loop at {b} is not orthogonal; modelled as a straight {t:0.#}in strip"));
            return result;
        }
        double inStrip = 0;
        foreach (var piece in Poly.OrthoDecompose(pts))
        {
            if (Intersection(piece, strip) is Box common) inStrip += common.Width * common.Height;
            foreach (var rest in Subtract(piece, strip))
                if (rest.Short >= 4 && rest.Long >= 12) result.Add(rest);
        }
        double missing = strip.Width * strip.Height - inStrip;
        if (missing > 12)
            checks.Add(new Check("wall recess", CheckStatus.Info, $"{face.ToString().ToLowerInvariant()} wall at {b} has a recess or niche of {missing:0} sq in; modelled as a solid {t:0.#}in wall"));
        return result;
    }

    private static Box? Intersection(Box a, Box b)
    {
        double ix0 = Math.Max(a.X0, b.X0), iy0 = Math.Max(a.Y0, b.Y0), ix1 = Math.Min(a.X1, b.X1), iy1 = Math.Min(a.Y1, b.Y1);
        return ix1 > ix0 && iy1 > iy0 ? new Box(ix0, iy0, ix1, iy1) : null;
    }

    /// <summary>a minus s, as up to four rectangles.</summary>
    private static IEnumerable<Box> Subtract(Box a, Box s)
    {
        if (!a.Intersects(s)) { yield return a; yield break; }
        if (a.X0 < s.X0) yield return new Box(a.X0, a.Y0, Math.Min(a.X1, s.X0), a.Y1);
        if (a.X1 > s.X1) yield return new Box(Math.Max(a.X0, s.X1), a.Y0, a.X1, a.Y1);
        double mx0 = Math.Max(a.X0, s.X0), mx1 = Math.Min(a.X1, s.X1);
        if (a.Y0 < s.Y0) yield return new Box(mx0, a.Y0, mx1, Math.Min(a.Y1, s.Y0));
        if (a.Y1 > s.Y1) yield return new Box(mx0, Math.Max(a.Y0, s.Y1), mx1, a.Y1);
    }

    /// <summary>Wall thickness from area and perimeter: exact for rectangles, close for L and T shapes.</summary>
    internal static double StripThickness(IReadOnlyList<Pt> pts)
    {
        double q = Poly.Perimeter(pts) / 4;
        return q - Math.Sqrt(Math.Max(0, q * q - Poly.Area(pts)));
    }

    /// <summary>
    /// Thickness measured off the envelope face: the length-weighted commonest distance of the edges that run
    /// parallel to the face. Handles notches, recesses and L-shaped loops, which a bounding box cannot.
    /// </summary>
    internal static double FaceThickness(IReadOnlyList<Pt> pts, Face face, double x0, double x1, double yr, double yf)
    {
        var weight = new Dictionary<double, double>();
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            double dist;
            if (face is Face.Left or Face.Right && Math.Abs(a.X - b.X) < Tol) dist = face == Face.Left ? a.X - x0 : x1 - a.X;
            else if (face is Face.Rear or Face.Front && Math.Abs(a.Y - b.Y) < Tol) dist = face == Face.Rear ? yr - a.Y : a.Y - yf;
            else continue;
            if (dist <= 1) continue;
            double key = Math.Round(dist * 2) / 2;
            weight[key] = weight.GetValueOrDefault(key) + Pt.Dist(a, b);
        }
        return weight.Count == 0 ? 0 : weight.MaxBy(kv => kv.Value).Key;
    }

    private static bool MatchesArchitect(double v, List<double> arch)
    {
        if (arch.Any(a => Math.Abs(v - a) < Tol)) return true;
        // the architect may split a bay at a door or opening: accept 2-4 consecutive dimensions that sum to it
        for (int i = 0; i < arch.Count; i++)
        {
            double s = arch[i];
            for (int k = i + 1; k < Math.Min(i + 4, arch.Count); k++)
            {
                s += arch[k];
                if (Math.Abs(s - v) < Tol) return true;
            }
        }
        return false;
    }

    private List<Beam> FindBeams(CadDrawing d, List<Check> checks)
    {
        var lines = new List<(Pt a, Pt b, Pt dir, double len)>();
        foreach (var l in d.Lines.Where(l => _layers.Is(l.Layer, LayerKind.Beam) && l.Length >= 6))
        {
            var a = l.A; var b = l.B;
            var dir = (b - a).Unit;
            if (dir.X < -1e-9 || (Math.Abs(dir.X) < 1e-9 && dir.Y < 0)) { (a, b) = (b, a); dir = dir * -1; }
            lines.Add((a, b, dir, l.Length));
        }
        var used = new HashSet<int>();
        var beams = new List<Beam>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (used.Contains(i)) continue;
            var (a, _, dir, len) = lines[i];
            var n = dir.Normal;
            (int j, double sep, double lo, double hi)? best = null;
            for (int j = i + 1; j < lines.Count; j++)
            {
                if (used.Contains(j)) continue;
                var (a2, b2, d2, _) = lines[j];
                if (Math.Abs(dir.Cross(d2)) > 0.01) continue;
                double sep = (a2 - a).Dot(n);
                if (Math.Abs(sep) < 6 || Math.Abs(sep) > 30) continue;
                double p1 = (a2 - a).Dot(dir), p2 = (b2 - a).Dot(dir);
                double lo = Math.Max(0, Math.Min(p1, p2)), hi = Math.Min(len, Math.Max(p1, p2));
                if (hi - lo >= 60 && (best is null || Math.Abs(sep) < Math.Abs(best.Value.sep)))
                    best = (j, sep, lo, hi);
            }
            if (best is { } bb)
            {
                used.Add(i); used.Add(bb.j);
                var off = n * (bb.sep / 2);
                beams.Add(new Beam(a + dir * bb.lo + off, a + dir * bb.hi + off, Math.Round(Math.Abs(bb.sep), 2), null));
            }
        }
        if (lines.Count > used.Count)
            checks.Add(new Check("beam lines", CheckStatus.Info, $"{lines.Count - used.Count} short or unpaired beam-layer lines ignored (lift cross, lintel)"));
        return beams;
    }

    private List<Stair> FindStairs(CadDrawing d, List<CadText> texts)
    {
        var segs = d.Lines.Where(l => _layers.Is(l.Layer, LayerKind.Stair)).ToList();
        var circles = d.Circles.Where(c => _layers.Is(c.Layer, LayerKind.Stair)).ToList();
        var stairs = new List<Stair>();

        // Flights: three or more equally spaced parallel lines of the same length, 8-14in apart.
        var groups = new Dictionary<(char o, double s0, double s1), List<(double pos, double s0, double s1)>>();
        foreach (var l in segs)
        {
            if (l.IsHorizontal(Tol) && Math.Abs(l.A.X - l.B.X) >= 24)
            {
                double s0 = Math.Min(l.A.X, l.B.X), s1 = Math.Max(l.A.X, l.B.X);
                var key = ('h', Math.Round(s0), Math.Round(s1));
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = new();
                g.Add((l.A.Y, s0, s1));
            }
            else if (l.IsVertical(Tol) && Math.Abs(l.A.Y - l.B.Y) >= 24)
            {
                double s0 = Math.Min(l.A.Y, l.B.Y), s1 = Math.Max(l.A.Y, l.B.Y);
                var key = ('v', Math.Round(s0), Math.Round(s1));
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = new();
                g.Add((l.A.X, s0, s1));
            }
        }
        foreach (var (key, members) in groups)
        {
            var pos = members.Select(m => Math.Round(m.pos, 2)).Distinct().OrderBy(v => v).ToList();
            double s0 = members.Min(m => m.s0), s1 = members.Max(m => m.s1);
            var runs = new List<List<double>>();
            var run = new List<double> { pos[0] };
            foreach (var p in pos.Skip(1))
            {
                double g = p - run[^1];
                if (g is >= 8 and <= 14 && (run.Count < 2 || Math.Abs(g - (run[1] - run[0])) < 0.5)) run.Add(p);
                else { runs.Add(run); run = new List<double> { p }; }
            }
            runs.Add(run);
            foreach (var r in runs.Where(r => r.Count >= 3))
            {
                var b = key.o == 'h' ? new Box(s0, r[0], s1, r[^1]) : new Box(r[0], s0, r[^1], s1);
                int diag = segs.Count(l => !l.IsHorizontal(Tol) && !l.IsVertical(Tol) && b.Contains(l.A, 1) && b.Contains(l.B, 1));
                stairs.Add(new Stair
                {
                    Kind = diag >= 2 ? StairKind.Ramp : StairKind.Flight,
                    Bounds = b,
                    RunAxis = key.o == 'h' ? 'y' : 'x',
                    Width = Math.Round(s1 - s0, 2),
                    Going = Math.Round(r[1] - r[0], 2),
                    Treads = r.Count - 1,
                });
            }
        }

        // Spiral stairs: a circle of radius 18in or more with eight or more radial lines meeting at its centre.
        var done = new HashSet<(double, double)>();
        foreach (var c in circles.OrderByDescending(c => c.Radius))
        {
            var key = (Math.Round(c.Center.X), Math.Round(c.Center.Y));
            if (done.Contains(key) || c.Radius < 18) continue;
            int radial = segs.Count(l => Pt.Dist(l.A, c.Center) < 0.25 * c.Radius || Pt.Dist(l.B, c.Center) < 0.25 * c.Radius);
            if (radial < 8) continue;
            done.Add(key);
            stairs.Add(new Stair
            {
                Kind = StairKind.Spiral,
                Center = c.Center,
                Radius = c.Radius,
                Treads = radial,
                Bounds = new Box(c.Center.X - c.Radius, c.Center.Y - c.Radius, c.Center.X + c.Radius, c.Center.Y + c.Radius),
            });
        }

        // Labels: nearest STEP / STAIR / UP / DN note within 60in.
        for (int i = 0; i < stairs.Count; i++)
        {
            var st = stairs[i];
            CadText? best = null;
            double bestGap = double.MaxValue;
            foreach (var t in texts.Where(t => StairWord().IsMatch(t.Text.ToUpperInvariant())))
            {
                double g = st.Bounds.Gap(new Box(t.Insert.X, t.Insert.Y, t.Insert.X, t.Insert.Y));
                if (g <= 60 && g < bestGap) { best = t; bestGap = g; }
            }
            if (best is not null)
                stairs[i] = st with
                {
                    Label = best.Text,
                    LabelAt = best.Insert,
                    Direction = DownWord().IsMatch(best.Text.ToUpperInvariant()) ? "down" : "up",
                };
        }
        // An unlabelled flight next to a labelled one is the other flight of the same stair.
        for (int i = 0; i < stairs.Count; i++)
        {
            var st = stairs[i];
            if (st.Kind != StairKind.Flight || st.Label is not null) continue;
            var near = stairs.Select((o, j) => (o, j))
                .Where(x => x.j != i && x.o.Kind == StairKind.Flight && x.o.Label is not null && st.Bounds.Gap(x.o.Bounds) <= 72)
                .OrderBy(x => st.Bounds.Gap(x.o.Bounds)).ToList();
            if (near.Count > 0) stairs[i] = st with { SameStairAs = near[0].j };
        }
        return stairs;
    }

    private List<YardWall> FindYardWalls(CadDrawing d, double yr, double plotRear)
    {
        var lines = new List<(char o, double c, double s0, double s1)>();
        foreach (var l in d.Lines.Where(l => _layers.Is(l.Layer, LayerKind.WallLines)))
        {
            if (Math.Min(l.A.Y, l.B.Y) < yr - 0.5 || l.Length < 6) continue;
            if (l.IsHorizontal(Tol)) lines.Add(('h', l.A.Y, Math.Min(l.A.X, l.B.X), Math.Max(l.A.X, l.B.X)));
            else if (l.IsVertical(Tol)) lines.Add(('v', l.A.X, Math.Min(l.A.Y, l.B.Y), Math.Max(l.A.Y, l.B.Y)));
        }
        var found = new List<YardWall>();
        for (int i = 0; i < lines.Count; i++)
            for (int j = i + 1; j < lines.Count; j++)
            {
                var (o, c, s0, s1) = lines[i];
                var (o2, c2, t0, t1) = lines[j];
                double th = Math.Abs(c2 - c);
                if (o2 != o || th < 4 || th > 14) continue;
                double lo = Math.Max(s0, t0), hi = Math.Min(s1, t1);
                if (hi - lo < 6) continue;
                double lo2 = Math.Min(s0, t0), hi2 = Math.Max(s1, t1);
                if (lo - lo2 <= th + 1) lo = lo2;   // fill the corner where the outer line runs on
                if (hi2 - hi <= th + 1) hi = hi2;
                double a0 = Math.Min(c, c2), a1 = Math.Max(c, c2);
                var r = o == 'h' ? new Box(lo, a0, hi, a1) : new Box(a0, lo, a1, hi);
                if (r.Y1 <= plotRear + 0.5) found.Add(new YardWall(r, Math.Round(th, 2)));
            }
        var keep = new List<YardWall>();
        for (int i = 0; i < found.Count; i++)
        {
            bool dup = found.Where((o, j) => j != i && o.Bounds.Contains(found[i].Bounds, Tol) &&
                                             (!found[i].Bounds.Contains(o.Bounds, Tol) || j < i)).Any();
            if (!dup) keep.Add(found[i]);
        }
        return keep;
    }

    private static readonly Regex SpacesRx = new("\\s+", RegexOptions.CultureInvariant);
    private static Regex Spaces() => SpacesRx;
    private static readonly Regex BeamNoteRx = new("(?:DROP|DOWN)\\s*BEAM\\s*(\\d+'\\s*-?\\s*\\d+\"|\\d+\")", RegexOptions.CultureInvariant);
    private static Regex BeamNote() => BeamNoteRx;
    private static readonly Regex SlabNoteRx = new("SLAB\\s*HEIGHT\\s*(\\d+'\\s*-?\\s*\\d+\")", RegexOptions.CultureInvariant);
    private static Regex SlabNote() => SlabNoteRx;
    private static readonly Regex LevelNoteRx = new("LEVEL\\s*=?\\s*([+-]\\s*\\d+'\\s*-?\\s*\\d+\")", RegexOptions.CultureInvariant);
    private static Regex LevelNote() => LevelNoteRx;
    private static readonly Regex AreaNoteRx = new("COVERED\\s*AREA.*?(\\d[\\d,]*)\\s*S", RegexOptions.CultureInvariant);
    private static Regex AreaNote() => AreaNoteRx;
    private static readonly Regex StairWordRx = new("STEP|STAIR|\\bUP\\b|\\bDN\\b", RegexOptions.CultureInvariant);
    private static Regex StairWord() => StairWordRx;
    private static readonly Regex DownWordRx = new("\\bDN\\b|DOWN", RegexOptions.CultureInvariant);
    private static Regex DownWord() => DownWordRx;
    private static readonly Regex RoadWordRx = new("\\bROAD\\b", RegexOptions.CultureInvariant);
    private static Regex RoadWord() => RoadWordRx;
}
