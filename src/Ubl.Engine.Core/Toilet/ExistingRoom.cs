using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Core.Shell;
using Ubl.Engine.Core.Units;

namespace Ubl.Engine.Core.Toilet;

/// <summary>
/// A rectangular room read from a model: its inside corner, its x direction, its inside size, and which side of
/// the rectangle each boundary segment lies on. The room may be turned against the model axes; the toilet is then
/// planned in the room's own frame, whose x axis is the room edge closest to the model's x axis.
/// </summary>
public sealed record RoomRect(Pt Origin, Pt Ex, double Width, double Depth, double AngleDeg, IReadOnlyList<Side?> SegmentSides)
{
    public Pt Ey => new(-Ex.Y, Ex.X);
    public Pt ToRoom(Pt p) { var d = p - Origin; return new Pt(d.Dot(Ex), d.Dot(Ey)); }
    public Pt ToModel(Pt q) => Origin + Ex * q.X + Ey * q.Y;
    /// <summary>True when the room is square to the model axes (its plan sheets come out upright).</summary>
    public bool Square => Math.Abs(AngleDeg) < 0.01;
}

/// <summary>Reading an existing room (its boundary and its doors) into the planner's terms. No Revit here: the add-in passes points in.</summary>
public static class ExistingRoom
{
    private const double AngleTolDeg = 0.5, PosTol = 0.25;

    /// <summary>
    /// The rectangle of a room boundary given as its segments in order (model plan coordinates, inches). Collinear
    /// neighbours (one wall split by a join) are merged. Anything that is not a rectangle is refused with the reason.
    /// </summary>
    public static RoomRect Rectangle(IReadOnlyList<(Pt A, Pt B)> segments)
    {
        if (segments.Count < 4) throw new RecognitionException($"the room boundary has only {segments.Count} sides");
        foreach (var (a, b) in segments)
            if (Pt.Dist(a, b) < 1e-6) throw new RecognitionException("the room boundary has a zero-length side");

        // merge consecutive segments running the same way
        var edges = new List<(Pt A, Pt B, List<int> Segs)>();
        for (int i = 0; i < segments.Count; i++)
        {
            var (a, b) = segments[i];
            if (edges.Count > 0 && Parallel(edges[^1].B - edges[^1].A, b - a) && Pt.Dist(edges[^1].B, a) < PosTol)
            {
                var e = edges[^1];
                edges[^1] = (e.A, b, e.Segs.Append(i).ToList());
            }
            else edges.Add((a, b, new List<int> { i }));
        }
        if (edges.Count > 1 && Parallel(edges[^1].B - edges[^1].A, edges[0].B - edges[0].A) && Pt.Dist(edges[^1].B, edges[0].A) < PosTol)
        {
            var last = edges[^1];
            edges[0] = (last.A, edges[0].B, last.Segs.Concat(edges[0].Segs).ToList());
            edges.RemoveAt(edges.Count - 1);
        }
        if (edges.Count != 4)
            throw new RecognitionException($"the room is not a rectangle: its boundary has {edges.Count} sides. The engine plans rectangular toilets only.");
        for (int i = 0; i < 4; i++)
        {
            var d0 = (edges[i].B - edges[i].A).Unit; var d1 = (edges[(i + 1) % 4].B - edges[(i + 1) % 4].A).Unit;
            if (Math.Abs(d0.Dot(d1)) > Math.Sin(AngleTolDeg * Math.PI / 180))
                throw new RecognitionException("the room is not a rectangle: two of its walls do not meet at a right angle");
            if (Pt.Dist(edges[i].B, edges[(i + 1) % 4].A) > PosTol)
                throw new RecognitionException("the room boundary is not closed");
        }

        // x axis: of the four edge directions (and their reverses), the one closest to the model's x axis
        var dirs = edges.SelectMany(e => new[] { (e.B - e.A).Unit, (e.A - e.B).Unit }).ToList();
        var ex = dirs.OrderByDescending(d => d.X).First();
        var ey = new Pt(-ex.Y, ex.X);
        var pts = edges.Select(e => e.A).ToList();
        double u0 = pts.Min(p => p.Dot(ex)), u1 = pts.Max(p => p.Dot(ex)), v0 = pts.Min(p => p.Dot(ey)), v1 = pts.Max(p => p.Dot(ey));
        var origin = ex * u0 + ey * v0;
        double angle = Math.Atan2(ex.Y, ex.X) * 180 / Math.PI;
        if (Math.Abs(angle) < 1e-7) angle = 0;
        var rect = new RoomRect(origin, ex, u1 - u0, v1 - v0, angle, Array.Empty<Side?>());

        // side of each original segment, by its midpoint
        var sides = new Side?[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            var (a, b) = segments[i];
            var m = rect.ToRoom((a + b) * 0.5);
            sides[i] = Math.Abs(m.Y) < PosTol ? Side.South
                     : Math.Abs(m.X - rect.Width) < PosTol ? Side.East
                     : Math.Abs(m.Y - rect.Depth) < PosTol ? Side.North
                     : Math.Abs(m.X) < PosTol ? Side.West
                     : null;
        }
        return rect with { SegmentSides = sides };
    }

    private static bool Parallel(Pt a, Pt b) => Math.Abs(a.Unit.Cross(b.Unit)) < Math.Sin(AngleTolDeg * Math.PI / 180) && a.Dot(b) > 0;

    /// <summary>
    /// Which wall a door sits in and where along it, from its location point in room coordinates (doors stand on
    /// the wall's centre line). Null when it is not within a wall's thickness of the room.
    /// </summary>
    public static (Side Wall, double S)? WallOf(ToiletSpec spec, Pt p, double slack = 2)
    {
        (Side, double, double)? best = null;
        foreach (var side in RoomGeometry.Sides)
        {
            var wl = RoomGeometry.Wall(spec, side);
            double s = wl.SOf(p), d = -wl.DOf(p);                 // d: how far out from the inside face
            if (s < -slack || s > wl.Length + slack) continue;
            double t = spec.WallThickness(side);
            if (d < -slack || d > t + slack) continue;
            double score = Math.Abs(d - t / 2);
            if (best is null || score < best.Value.Item3) best = (side, s, score);
        }
        return best is { } b ? (b.Item1, b.Item2) : null;
    }

    /// <summary>
    /// A door in the planner's terms from what was read: its wall, opening centre and width, the hinge position
    /// along the wall and whether it swings into the room. The offset is measured from the nearer corner.
    /// </summary>
    public static DoorSpec Door(ToiletSpec spec, Side wall, double centreS, double width, double height, double? hingeS, bool swingIn)
    {
        double len = RoomGeometry.Wall(spec, wall).Length;
        double s0 = Math.Max(0, centreS - width / 2), s1 = Math.Min(len, centreS + width / 2);
        bool fromStart = s0 <= len - s1;
        double hinge = hingeS ?? (fromStart ? s0 : s1);
        bool hingeAtCorner = fromStart ? Math.Abs(hinge - s0) <= Math.Abs(hinge - s1) : Math.Abs(hinge - s1) <= Math.Abs(hinge - s0);
        return new DoorSpec
        {
            Wall = wall,
            FromCorner = fromStart ? DoorCorner.Start : DoorCorner.End,
            Offset = Math.Round(fromStart ? s0 : len - s1, 3),
            Width = Math.Round(s1 - s0, 3),
            Height = height,
            SwingIn = swingIn,
            HingeAtCorner = hingeAtCorner,
        };
    }

    /// <summary>One line about a door for the confirmation box and the report.</summary>
    public static string Describe(DoorSpec d)
    {
        string end = ToiletInput.CompassOf(d.Wall, d.FromCorner);
        return $"{FtIn.Format(d.Width)} x {FtIn.Format(d.Height)} on the {RoomGeometry.Name(d.Wall)} wall, {FtIn.Format(d.Offset)} from its {end} end, swings {(d.SwingIn ? "in" : "out")}";
    }
}
