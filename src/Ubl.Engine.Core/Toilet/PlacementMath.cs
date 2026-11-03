using Ubl.Engine.Core.Geometry;

namespace Ubl.Engine.Core.Toilet;

/// <summary>
/// The arithmetic of putting a measured family exactly on its planned spot, kept free of Revit so it is tested
/// on every build. A family's geometry is measured in its own coordinates (inches from its insertion point); the
/// card says which local axis its front faces. From that, the back-centre (or centre) is known in local
/// coordinates, and the rotation and insertion point follow so that the back-centre lands on the target and the
/// front faces the room. Revit then reads back the placed instance's transform as an independent check.
/// </summary>
public static class PlacementMath
{
    /// <summary>Geometry extents in the family's own coordinates, inches from its insertion point.</summary>
    public readonly record struct Extents(double X0, double X1, double Y0, double Y1, double Z0, double Z1)
    {
        public double SizeX => X1 - X0;
        public double SizeY => Y1 - Y0;
        public double SizeZ => Z1 - Z0;
    }

    /// <summary>"-Y" (Revit convention: the side shown in the family's Front view), "+Y", "-X" or "+X".</summary>
    public static Pt Front(string? front) => (front ?? "-Y").Trim().ToUpperInvariant() switch
    {
        "+Y" or "Y" => new Pt(0, 1),
        "-X" => new Pt(-1, 0),
        "+X" or "X" => new Pt(1, 0),
        _ => new Pt(0, -1),
    };

    /// <summary>Back-centre of the family (the face opposite its front) or, for free items, its plan centre.</summary>
    public static Pt AnchorLocal(Extents m, Pt front, bool backed)
    {
        double cx = (m.X0 + m.X1) / 2, cy = (m.Y0 + m.Y1) / 2;
        if (!backed) return new Pt(cx, cy);
        if (front.Y < -0.5) return new Pt(cx, m.Y1);
        if (front.Y > 0.5) return new Pt(cx, m.Y0);
        if (front.X < -0.5) return new Pt(m.X1, cy);
        return new Pt(m.X0, cy);
    }

    /// <summary>Plan size in the planner's terms: along the wall, and out from it (for free items: along x, along y).</summary>
    public static (double Along, double Out) PlanSize(Extents m, Pt front) =>
        Math.Abs(front.Y) > 0.5 ? (m.SizeX, m.SizeY) : (m.SizeY, m.SizeX);

    /// <summary>
    /// Rotation (radians, counter-clockwise about the vertical), insertion point (room coordinates) and the
    /// insertion point's height above the floor that put the family's anchor on <paramref name="target"/> with its
    /// front along <paramref name="facing"/> and its bottom at <paramref name="z0"/>.
    /// </summary>
    public static (double Theta, Pt Insert, double InsertZ) Solve(Extents m, Pt front, bool backed, Pt target, Pt facing, double z0)
    {
        double theta = Math.Atan2(facing.Y, facing.X) - Math.Atan2(front.Y, front.X);
        theta = Math.IEEERemainder(theta, 2 * Math.PI);
        var a = AnchorLocal(m, front, backed);
        var insert = target - Rotate(a, theta);
        return (theta, insert, z0 - m.Z0);
    }

    public static Pt Rotate(Pt v, double theta) =>
        new(v.X * Math.Cos(theta) - v.Y * Math.Sin(theta), v.X * Math.Sin(theta) + v.Y * Math.Cos(theta));

    public static Pt LocalToRoom(Pt local, Pt insert, double theta) => insert + Rotate(local, theta);

    /// <summary>The family's plan rectangle in room coordinates once placed (for checking against the plan).</summary>
    public static Box PlacedPlan(Extents m, Pt insert, double theta) =>
        Box.Of(new[] { new Pt(m.X0, m.Y0), new Pt(m.X1, m.Y0), new Pt(m.X1, m.Y1), new Pt(m.X0, m.Y1) }.Select(p => LocalToRoom(p, insert, theta)));
}
