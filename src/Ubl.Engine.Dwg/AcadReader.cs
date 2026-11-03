using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;
using Ubl.Engine.Core.Cad;
using Ubl.Engine.Core.Geometry;

namespace Ubl.Engine.Dwg;

/// <summary>
/// Reads DWG (AC1014 to AC1032) or DXF with ACadSharp and flattens it into the reader-neutral CadDrawing.
/// Block references are exploded with their transforms, so a column drawn inside a block is still found.
/// Entities on layer 0 inside a block take the block reference's layer, as AutoCAD displays them.
/// </summary>
public static class AcadReader
{
    private const int MaxNesting = 8;
    private const int ArcSegments = 16;

    public static CadDrawing Read(string path)
    {
        var warnings = new List<string>();
        void Notify(object sender, NotificationEventArgs e)
        {
            if (e.NotificationType is NotificationType.Warning or NotificationType.Error)
                warnings.Add(e.Message);
        }

        CadDocument doc = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".dwg" => DwgReader.Read(path, Notify),
            ".dxf" => DxfReader.Read(path, Notify),
            var ext => throw new NotSupportedException($"Unsupported drawing type '{ext}'. Send DWG or DXF."),
        };

        var d = new CadDrawing { Source = Path.GetFileName(path) };
        int count = 0;
        foreach (var e in doc.Entities)
        {
            count++;
            Add(d, e, null, 0);
        }
        d.EntityCount = count;
        d.ReaderWarnings.AddRange(warnings);
        return d;
    }

    private static void Add(CadDrawing d, Entity e, string? parentLayer, int depth)
    {
        string layer = e.Layer?.Name ?? "0";
        if (parentLayer is not null && layer == "0") layer = parentLayer;

        switch (e)
        {
            case Line l:
                d.Lines.Add(new CadLine(layer, P(l.StartPoint), P(l.EndPoint)));
                break;
            case Arc:
                break; // arcs carry no shell meaning yet (door swings); kept out to avoid false stair or wall hits
            case Circle c:
                d.Circles.Add(new CadCircle(layer, P(c.Center), c.Radius));
                break;
            case Ellipse el when Math.Abs(el.RadiusRatio - 1) < 1e-6:
                // Insert.Explode turns block circles into round ellipses
                d.Circles.Add(new CadCircle(layer, P(el.Center), new Pt(el.MajorAxisEndPoint.X, el.MajorAxisEndPoint.Y).Length));
                break;
            case LwPolyline pl:
                AddPolyline(d, layer, pl);
                break;
            case Hatch h:
                d.Hatches.Add(new CadHatch(layer, h.Pattern?.Name ?? "", Loops(h)));
                break;
            case MText mt:
                d.Texts.Add(new CadText(layer, P(mt.InsertPoint), MTextDecoder.Decode(mt.Value ?? "")));
                break;
            case TextEntity t:
                d.Texts.Add(new CadText(layer, P(t.InsertPoint), MTextDecoder.DecodeText(t.Value ?? "")));
                break;
            case Dimension dim:
                try { d.Dimensions.Add(new CadDimension(layer, dim.Measurement)); }
                catch (Exception ex) { d.ReaderWarnings.Add($"dimension skipped: {ex.Message}"); }
                break;
            case Insert ins when depth < MaxNesting:
                // one bad block (proxy entities, broken definitions) must not stop the whole drawing
                try
                {
                    foreach (var sub in ins.Explode()) Add(d, sub, layer, depth + 1);
                }
                catch (Exception ex)
                {
                    d.ReaderWarnings.Add($"block '{ins.Block?.Name}' on layer {layer} skipped: {ex.Message}");
                }
                break;
        }
    }

    private static void AddPolyline(CadDrawing d, string layer, LwPolyline pl)
    {
        var v = pl.Vertices;
        int n = v.Count;
        int segs = pl.IsClosed ? n : n - 1;
        for (int i = 0; i < segs; i++)
        {
            var a = v[i]; var b = v[(i + 1) % n];
            var pa = new Pt(a.Location.X, a.Location.Y);
            var pb = new Pt(b.Location.X, b.Location.Y);
            if (Math.Abs(a.Bulge) < 1e-9)
                d.Lines.Add(new CadLine(layer, pa, pb));
            // bulged segments are arcs: not used by the shell rules
        }
    }

    private static List<IReadOnlyList<Pt>> Loops(Hatch h)
    {
        var loops = new List<IReadOnlyList<Pt>>();
        foreach (var path in h.Paths)
        {
            var pts = new List<Pt>();
            foreach (var edge in path.Edges)
            {
                switch (edge)
                {
                    case Hatch.BoundaryPath.Polyline poly:
                        foreach (var vx in poly.Vertices) pts.Add(new Pt(vx.X, vx.Y));   // Z holds the bulge
                        break;
                    case Hatch.BoundaryPath.Line ln:
                        pts.Add(new Pt(ln.Start.X, ln.Start.Y));
                        break;
                    case Hatch.BoundaryPath.Arc arc:
                        pts.AddRange(Chord(arc));
                        break;
                }
            }
            var clean = Poly.Clean(pts);
            if (clean.Count >= 3) loops.Add(clean);
        }
        return loops;
    }

    private static IEnumerable<Pt> Chord(Hatch.BoundaryPath.Arc arc)
    {
        double a0 = arc.StartAngle, a1 = arc.EndAngle;
        if (!arc.CounterClockWise) (a0, a1) = (-a0, -a1);
        if (a1 < a0) a1 += 2 * Math.PI;
        for (int i = 0; i < ArcSegments; i++)
        {
            double a = a0 + (a1 - a0) * i / ArcSegments;
            yield return new Pt(arc.Center.X + arc.Radius * Math.Cos(a), arc.Center.Y + arc.Radius * Math.Sin(a));
        }
    }

    private static Pt P(XYZ p) => new(p.X, p.Y);
}
