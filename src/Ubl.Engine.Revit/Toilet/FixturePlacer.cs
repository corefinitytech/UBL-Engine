using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Core.Toilet;
using Ubl.Engine.Revit.Build;

namespace Ubl.Engine.Revit.Toilet;

/// <summary>Maps the planner's room frame (inches, origin at the inside south-west corner) to Revit model coordinates (feet).</summary>
internal sealed class RoomFrame(XYZ origin, XYZ ex, XYZ ey, double ffl)
{
    public XYZ Origin { get; } = origin;
    public XYZ Ex { get; } = ex;
    public XYZ Ey { get; } = ey;
    /// <summary>Finished floor level, feet (internal coordinates).</summary>
    public double Ffl { get; } = ffl;

    public XYZ P(Pt p, double zInches = 0) => new XYZ(Origin.X, Origin.Y, Ffl) + Ex * (p.X / 12) + Ey * (p.Y / 12) + XYZ.BasisZ * (zInches / 12);
    public XYZ V(Pt v) => (Ex * v.X + Ey * v.Y).Normalize();
    public Pt ToRoom(XYZ w)
    {
        var d = new XYZ(w.X - Origin.X, w.Y - Origin.Y, 0);
        return new Pt(d.DotProduct(Ex) * 12, d.DotProduct(Ey) * 12);
    }
}

/// <summary>A family's geometry extents in its own coordinates, in inches from its insertion point.</summary>
internal sealed record Measured(double X0, double X1, double Y0, double Y1, double Z0, double Z1, FamilyPlacementType Placement)
{
    public double SizeX => X1 - X0;
    public double SizeY => Y1 - Y0;
    public double SizeZ => Z1 - Z0;
    public override string ToString() =>
        $"{SizeX:0.##}\" x {SizeY:0.##}\" x {SizeZ:0.##}\" (X {X0:0.##}..{X1:0.##}, Y {Y0:0.##}..{Y1:0.##}, Z {Z0:0.##}..{Z1:0.##} from the insertion point)";
}

/// <summary>The outcome of placing one fixture, as measured after placement.</summary>
internal sealed record Placement(string Role, ElementId? Id, bool Ok, double PosErrIn, double AngErrDeg, double ZErrIn, int Corrections, string Note);

/// <summary>
/// Places a family exactly where the planner says, then proves it. The family is measured (not trusted): its real
/// extents tell where its back-centre is relative to its insertion point. The engine computes the rotation and
/// insertion point that put that back-centre on the target with the front facing the room, places it, then
/// re-measures the placed instance from Revit's own transform. Anything off by more than 1/16" or 0.1 degree is
/// corrected and measured again; if it still does not land, the instance is deleted and the report says why.
/// </summary>
internal sealed class FixturePlacer(Document doc, Level level, RoomFrame frame, string marker, List<string> log)
{
    public const double PosTolIn = 1.0 / 16;
    public const double AngTolDeg = 0.1;
    private readonly Dictionary<ElementId, Measured> _measured = new();

    /// <summary>Measures a family type's 3D geometry in its own coordinates. Call inside an open transaction.</summary>
    public Measured Measure(FamilySymbol symbol)
    {
        if (_measured.TryGetValue(symbol.Id, out var m)) return m;
        var family = symbol.Family;
        var placement = family.FamilyPlacementType;
        if (placement is not (FamilyPlacementType.OneLevelBased or FamilyPlacementType.WorkPlaneBased))
            throw new EngineSetupException($"Family '{family.Name}' is {placement}. The engine places level-based and work-plane-based families; " +
                                           "this one needs a host. Ask for it as an unhosted (level-based) or face-based family.");
        if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
        using var st = new SubTransaction(doc);
        st.Start();
        try
        {
            var fi = Create(symbol, placement, new XYZ(0, 0, level.ProjectElevation), XYZ.BasisX);
            doc.Regenerate();
            var ext = new Extents();
            var inv = fi.GetTransform().Inverse;
            var geo = fi.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false });
            if (geo is not null)
                foreach (var obj in geo)
                {
                    if (obj is GeometryInstance gi) ext.Add(gi.GetSymbolGeometry(), Transform.Identity);
                    else ext.Add(obj, inv);
                }
            if (ext.All.Empty) throw new EngineSetupException($"Family '{family.Name}' has no 3D geometry the engine can measure.");
            // the fixture's size is the size of its 3D body; loose lines inside the family only count when it has no body
            var e = ext.Body.Empty ? ext.All : ext.Body;
            m = new Measured(e.X0 * 12, e.X1 * 12, e.Y0 * 12, e.Y1 * 12, e.Z0 * 12, e.Z1 * 12, placement);
            if (!ext.Body.Empty && !ext.Body.SameAs(ext.All))
                log.Add($"{family.Name}: loose lines inside the family reach beyond its 3D body; measured the body only");
        }
        finally { st.RollBack(); }
        if (Math.Max(m.SizeX, Math.Max(m.SizeY, m.SizeZ)) > 96)
            throw new EngineSetupException($"Family '{family.Name}' measures {m}. That is too big for a toilet fixture: " +
                                           "the imported CAD inside it is probably at the wrong scale or has stray objects far from the fixture.");
        _measured[symbol.Id] = m;
        return m;
    }

    public Placement Place(PlacedItem item, FamilySymbol symbol, FamilyCard card)
    {
        var m = Measure(symbol);
        var f = card.FrontLocal;
        var ext = new PlacementMath.Extents(m.X0, m.X1, m.Y0, m.Y1, m.Z0, m.Z1);
        var a = PlacementMath.AnchorLocal(ext, f, item.Wall is not null);
        // solve in the room frame (tested in the core), then turn it into model coordinates
        var (thetaRoom, insertRoom, _) = PlacementMath.Solve(ext, f, item.Wall is not null, item.Anchor, item.Facing, item.Z0);
        double theta = thetaRoom + Math.Atan2(frame.Ex.Y, frame.Ex.X);
        var facing = frame.V(item.Facing);
        var target = frame.P(item.Anchor, item.Z0);                       // where the back-centre (at its bottom) must end up
        var ip = frame.P(insertRoom);
        var insert = new XYZ(ip.X, ip.Y, level.ProjectElevation);

        FamilyInstance fi;
        try
        {
            if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
            fi = Create(symbol, m.Placement, insert, Rz(XYZ.BasisX, theta));
            if (m.Placement == FamilyPlacementType.OneLevelBased && Math.Abs(theta) > 1e-9)
                ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(insert, insert + XYZ.BasisZ), theta);
            SetHeight(fi, m, (item.Z0 - m.Z0) / 12 + (frame.Ffl - level.ProjectElevation));
            fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(marker);
            doc.Regenerate();
        }
        catch (Exception ex)
        {
            return new Placement(item.Role, null, false, double.NaN, double.NaN, double.NaN, 0, $"Revit would not place it: {ex.Message}");
        }

        // measure, correct, measure again
        int corrections = 0;
        (double pos, double ang, double dz) err = default;
        for (int round = 0; round < 4; round++)
        {
            err = Error(fi, m, a, f, target, facing);
            bool ok = err.pos <= PosTolIn && Math.Abs(err.ang) <= AngTolDeg && Math.Abs(err.dz) <= PosTolIn && !fi.Mirrored;
            if (ok)
                return new Placement(item.Role, fi.Id, true, err.pos, Math.Abs(err.ang), err.dz, corrections,
                    corrections == 0 ? "landed first time" : $"landed after {corrections} correction(s)");
            if (round == 3 || fi.Mirrored) break;
            corrections++;
            var t = fi.GetTransform();
            if (Math.Abs(err.ang) > AngTolDeg)
                ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(t.Origin, t.Origin + XYZ.BasisZ), err.ang * Math.PI / 180);
            else if (err.pos > PosTolIn)
            {
                var aw = t.OfPoint(new XYZ(a.X / 12, a.Y / 12, m.Z0 / 12));
                ElementTransformUtils.MoveElement(doc, fi.Id, new XYZ(target.X - aw.X, target.Y - aw.Y, 0));
            }
            else
                ElementTransformUtils.MoveElement(doc, fi.Id, new XYZ(0, 0, -err.dz / 12));
            doc.Regenerate();
        }
        string why = fi.Mirrored ? "the placed instance is mirrored" :
            $"still off by {err.pos:0.###}\" and {Math.Abs(err.ang):0.###} degrees after {corrections} corrections";
        doc.Delete(fi.Id);
        return new Placement(item.Role, null, false, err.pos, Math.Abs(err.ang), err.dz, corrections, $"removed: {why}");
    }

    /// <summary>Position error (inches, plan), facing error (degrees, signed) and height error (inches) of a placed instance.</summary>
    private static (double pos, double ang, double dz) Error(FamilyInstance fi, Measured m, Pt a, Pt f, XYZ target, XYZ facing)
    {
        var t = fi.GetTransform();
        var aw = t.OfPoint(new XYZ(a.X / 12, a.Y / 12, m.Z0 / 12));
        double pos = Math.Sqrt(Math.Pow(aw.X - target.X, 2) + Math.Pow(aw.Y - target.Y, 2)) * 12;
        var fw = t.OfVector(new XYZ(f.X, f.Y, 0));
        fw = new XYZ(fw.X, fw.Y, 0).Normalize();
        double ang = fw.AngleOnPlaneTo(facing, XYZ.BasisZ) * 180 / Math.PI;
        if (ang > 180) ang -= 360;
        double dz = (aw.Z - target.Z) * 12;
        return (pos, ang, dz);
    }

    private FamilyInstance Create(FamilySymbol symbol, FamilyPlacementType placement, XYZ at, XYZ xDirection) =>
        placement == FamilyPlacementType.WorkPlaneBased
            ? doc.Create.NewFamilyInstance(level.GetPlaneReference(), at, xDirection, symbol)
            : doc.Create.NewFamilyInstance(at, symbol, level, StructuralType.NonStructural);

    /// <summary>Sets the insertion point's height above the level: by the elevation (or offset) parameter, else by moving it.</summary>
    private void SetHeight(FamilyInstance fi, Measured m, double offsetFt)
    {
        var p = m.Placement == FamilyPlacementType.WorkPlaneBased
            ? fi.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM)
            : fi.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
        if (p is not null && !p.IsReadOnly) { p.Set(offsetFt); return; }
        doc.Regenerate();
        double now = fi.GetTransform().Origin.Z - level.ProjectElevation;
        if (Math.Abs(offsetFt - now) > 1e-9)
        {
            ElementTransformUtils.MoveElement(doc, fi.Id, new XYZ(0, 0, offsetFt - now));
            log.Add($"{fi.Symbol.FamilyName}: no elevation parameter, height set by moving it");
        }
    }

    private static XYZ Rz(XYZ v, double theta) =>
        new(v.X * Math.Cos(theta) - v.Y * Math.Sin(theta), v.X * Math.Sin(theta) + v.Y * Math.Cos(theta), v.Z);

    /// <summary>Accumulates the extents of any Revit geometry, following nested instances: the 3D body (solids and
    /// meshes) separately from everything (curves and points as well).</summary>
    private sealed class Extents
    {
        public Box3 Body { get; } = new();
        public Box3 All { get; } = new();

        public void Add(GeometryElement ge, Transform t)
        {
            foreach (var obj in ge) Add(obj, t);
        }

        public void Add(GeometryObject obj, Transform t)
        {
            switch (obj)
            {
                case GeometryInstance gi:
                    Add(gi.GetSymbolGeometry(), t.Multiply(gi.Transform));
                    break;
                case GeometryElement ge:
                    Add(ge, t);
                    break;
                case Solid s when s.Faces.Size > 0:
                    foreach (Edge e in s.Edges)
                        foreach (var p in e.Tessellate()) AddPoint(t.OfPoint(p), true);
                    break;
                case Mesh mesh:
                    foreach (var p in mesh.Vertices) AddPoint(t.OfPoint(p), true);
                    break;
                case Curve c when c.IsBound:
                    foreach (var p in c.Tessellate()) AddPoint(t.OfPoint(p), false);
                    break;
                case PolyLine pl:
                    foreach (var p in pl.GetCoordinates()) AddPoint(t.OfPoint(p), false);
                    break;
                case Point pt:
                    AddPoint(t.OfPoint(pt.Coord), false);
                    break;
            }
        }

        private void AddPoint(XYZ p, bool body)
        {
            All.Add(p);
            if (body) Body.Add(p);
        }
    }

    private sealed class Box3
    {
        public double X0 = double.MaxValue, Y0 = double.MaxValue, Z0 = double.MaxValue, X1 = double.MinValue, Y1 = double.MinValue, Z1 = double.MinValue;
        public bool Empty => X0 > X1;

        public void Add(XYZ p)
        {
            X0 = Math.Min(X0, p.X); Y0 = Math.Min(Y0, p.Y); Z0 = Math.Min(Z0, p.Z);
            X1 = Math.Max(X1, p.X); Y1 = Math.Max(Y1, p.Y); Z1 = Math.Max(Z1, p.Z);
        }

        public bool SameAs(Box3 o) =>
            Math.Abs(X0 - o.X0) + Math.Abs(Y0 - o.Y0) + Math.Abs(Z0 - o.Z0) + Math.Abs(X1 - o.X1) + Math.Abs(Y1 - o.Y1) + Math.Abs(Z1 - o.Z1) < 1e-6;
    }
}
