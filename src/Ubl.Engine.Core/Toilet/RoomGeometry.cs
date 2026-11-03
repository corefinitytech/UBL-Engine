using Ubl.Engine.Core.Geometry;

namespace Ubl.Engine.Core.Toilet;

/// <summary>
/// One inside wall face. Walls run counter-clockwise round the room (south, east, north, west), so the room is
/// always on the left: Start is the wall's first corner, Dir runs along it, Normal points into the room.
/// </summary>
public readonly record struct WallLine(Side Side, Pt Start, Pt Dir, Pt Normal, double Length)
{
    /// <summary>The point S along the wall and D into the room.</summary>
    public Pt At(double s, double d = 0) => Start + Dir * s + Normal * d;

    /// <summary>Plan rectangle of an item centred at S, Width along the wall, from Back to Back + Depth into the room.</summary>
    public Box Rect(double s, double width, double back, double depth) =>
        Box.FromCorners(At(s - width / 2, back), At(s + width / 2, back + depth));

    /// <summary>Position along this wall of a room point (its projection).</summary>
    public double SOf(Pt p) => (p - Start).Dot(Dir);

    /// <summary>Distance of a room point from this wall face.</summary>
    public double DOf(Pt p) => (p - Start).Dot(Normal);
}

public static class RoomGeometry
{
    public static readonly Side[] Sides = { Side.South, Side.East, Side.North, Side.West };

    public static WallLine Wall(double width, double depth, Side side) => side switch
    {
        Side.South => new(side, new Pt(0, 0), new Pt(1, 0), new Pt(0, 1), width),
        Side.East => new(side, new Pt(width, 0), new Pt(0, 1), new Pt(-1, 0), depth),
        Side.North => new(side, new Pt(width, depth), new Pt(-1, 0), new Pt(0, -1), width),
        _ => new(side, new Pt(0, depth), new Pt(0, -1), new Pt(1, 0), depth),
    };

    public static WallLine Wall(ToiletSpec spec, Side side) => Wall(spec.Width, spec.Depth, side);

    /// <summary>The wall that meets this one at its start corner.</summary>
    public static Side Prev(Side s) => (Side)(((int)s + 3) % 4);

    /// <summary>The wall that meets this one at its end corner.</summary>
    public static Side Next(Side s) => (Side)(((int)s + 1) % 4);

    public static Side Opposite(Side s) => (Side)(((int)s + 2) % 4);

    /// <summary>The door's opening and hinge along its wall, or null if it does not fit on the wall.</summary>
    public static DoorGeometry? Door(ToiletSpec spec) => DoorOf(spec, spec.Door);

    /// <summary>The geometry of the room's further doors (those that fit their walls).</summary>
    public static List<DoorGeometry> OtherDoors(ToiletSpec spec) =>
        spec.OtherDoors.Select(d => DoorOf(spec, d)).Where(g => g is not null).Select(g => g!).ToList();

    public static DoorGeometry? DoorOf(ToiletSpec spec, DoorSpec d)
    {
        double len = Wall(spec, d.Wall).Length;
        double s0 = d.FromCorner == DoorCorner.Start ? d.Offset : len - d.Offset - d.Width;
        double s1 = s0 + d.Width;
        if (s0 < -1e-9 || s1 > len + 1e-9 || d.Width <= 0) return null;
        bool hingeAtS0 = d.HingeAtCorner == (d.FromCorner == DoorCorner.Start);
        return new DoorGeometry(d.Wall, s0, s1, hingeAtS0 ? s0 : s1, d.SwingIn, d.Width, d.Height);
    }

    /// <summary>
    /// True when a plan rectangle cuts into the door leaf's sweep: the quarter circle of radius = leaf width
    /// centred on the hinge, between the closed and the 90-degree open positions. Touching does not count.
    /// </summary>
    public static bool HitsSwing(ToiletSpec spec, DoorGeometry door, Box b)
    {
        if (!door.SwingIn) return false;
        var wl = Wall(spec, door.Wall);
        var hinge = wl.At(door.HingeS);
        var u = door.LatchS > door.HingeS ? wl.Dir : wl.Dir * -1;
        var v = wl.Normal;
        double a0 = double.MaxValue, a1 = double.MinValue, b0 = double.MaxValue, b1 = double.MinValue;
        foreach (var c in b.Corners())
        {
            double a = (c - hinge).Dot(u), bb = (c - hinge).Dot(v);
            a0 = Math.Min(a0, a); a1 = Math.Max(a1, a); b0 = Math.Min(b0, bb); b1 = Math.Max(b1, bb);
        }
        a0 = Math.Max(a0, 0); b0 = Math.Max(b0, 0);
        const double eps = 1e-6;
        if (a1 - a0 <= eps || b1 - b0 <= eps) return false;
        return a0 * a0 + b0 * b0 < door.Width * door.Width - eps;
    }

    /// <summary>The square the door leaf sweeps (for drawings and reports).</summary>
    public static Box SwingSquare(ToiletSpec spec, DoorGeometry door)
    {
        var wl = Wall(spec, door.Wall);
        return wl.Rect((door.S0 + door.S1) / 2, door.Width, 0, door.Width);
    }

    /// <summary>Strip just inside the door that is kept free of anything standing on the floor.</summary>
    public static Box EntryZone(ToiletSpec spec, DoorGeometry door, ToiletRules r)
    {
        var wl = Wall(spec, door.Wall);
        return wl.Rect((door.S0 + door.S1) / 2, door.Width, 0, door.SwingIn ? r.DoorEntryDepth : r.DoorOutEntryDepth);
    }

    public static bool Inside(ToiletSpec spec, Box b, double tol = 1e-6) =>
        b.X0 >= -tol && b.Y0 >= -tol && b.X1 <= spec.Width + tol && b.Y1 <= spec.Depth + tol;

    /// <summary>Strict overlap with a tolerance, so items that only touch do not count.</summary>
    public static bool Overlaps(Box a, Box b, double tol = 1e-6) =>
        a.X0 < b.X1 - tol && b.X0 < a.X1 - tol && a.Y0 < b.Y1 - tol && b.Y0 < a.Y1 - tol;

    public static string Name(Side s) => s.ToString().ToLowerInvariant();
}
