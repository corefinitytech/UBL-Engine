using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Revit.Build;
using RecognitionException = Ubl.Engine.Core.Shell.RecognitionException;
using Wall = Autodesk.Revit.DB.Wall;
using Ubl.Engine.Core.Toilet;
using Ubl.Engine.Core.Units;

namespace Ubl.Engine.Revit.Toilet;

/// <summary>A bathroom already in the project, read into the planner's terms. Its walls, doors and room are never changed.</summary>
internal sealed record ExistingToilet(
    ToiletSpec Spec, RoomFrame Frame, Level Level, Room Room, Dictionary<Side, Wall> Walls, ElementId? CeilingId, bool HasFloor,
    List<ElementId> DoorIds, List<string> Notes, List<string> Warnings, RoomRect Rect);

/// <summary>
/// Reads an existing bathroom: the inside rectangle from the room's finish boundary (turned rooms too), each wall's
/// thickness, every door in its walls (position and width from the door, swing and hinge side from the swing arc it
/// draws in the plan), an existing ceiling's height, whether a floor is already there, the walls' height, and
/// anything already standing in the room that the engine did not place.
/// </summary>
internal static class ExistingRoomReader
{
    private static Pt In(XYZ p) => new(p.X * 12, p.Y * 12);

    public static ExistingToilet Read(Document doc, ViewPlan view, Room room, string marker, string markerPrefix)
    {
        var notes = new List<string>(); var warnings = new List<string>();
        var level = room.Level ?? view.GenLevel ?? throw new EngineSetupException("The room has no level.");
        double ffl = level.ProjectElevation;

        // ---- the rectangle
        var loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish });
        if (loops is null || loops.Count == 0 || room.Area <= 0)
            throw new EngineSetupException("The room has no boundary: its walls must close all the way round it.");
        if (loops.Count > 1) warnings.Add($"the room has {loops.Count - 1} island(s) inside it (a column or shaft?): ignored, check nothing is placed on it");
        var segs = new List<(Pt, Pt)>(); var owners = new List<ElementId>();
        foreach (var bs in loops[0])
        {
            if (bs.GetCurve() is not Line line) throw new EngineSetupException("The room has a curved wall. The engine plans rectangular toilets only.");
            segs.Add((In(line.GetEndPoint(0)), In(line.GetEndPoint(1))));
            owners.Add(bs.ElementId);
        }
        RoomRect rect;
        try { rect = ExistingRoom.Rectangle(segs); }
        catch (RecognitionException ex) { throw new EngineSetupException($"This room cannot be fitted out: {ex.Message}"); }

        // ---- walls by side (the longest piece on each side)
        var walls = new Dictionary<Side, Wall>();
        var longest = new Dictionary<Side, double>();
        for (int i = 0; i < segs.Count; i++)
        {
            if (rect.SegmentSides[i] is not Side s || doc.GetElement(owners[i]) is not Wall w) continue;
            double len = Pt.Dist(segs[i].Item1, segs[i].Item2);
            if (!longest.TryGetValue(s, out var l) || len > l) { walls[s] = w; longest[s] = len; }
        }
        foreach (var w in walls.Values)
            if (w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() is { } c && c.StartsWith(markerPrefix + ":") && !c.Contains(":ROOM:"))
                throw new EngineSetupException($"This room was built by Build Toilet ('{c[(markerPrefix.Length + 1)..]}'). Rebuild it with that option, not as an existing room.");
        double T(Side s)
        {
            if (walls.TryGetValue(s, out var w)) return Math.Round(w.Width * 12, 3);
            warnings.Add($"no wall on the {RoomGeometry.Name(s)} side (a room separation line?): 4 1/2\" assumed");
            return 4.5;
        }
        var spec = new ToiletSpec
        {
            Name = ToiletName(room),
            Width = Math.Round(rect.Width, 3), Depth = Math.Round(rect.Depth, 3),
            WallSouth = T(Side.South), WallEast = T(Side.East), WallNorth = T(Side.North), WallWest = T(Side.West),
        };
        var frame = new RoomFrame(new XYZ(rect.Origin.X / 12, rect.Origin.Y / 12, 0), new XYZ(rect.Ex.X, rect.Ex.Y, 0), new XYZ(rect.Ey.X, rect.Ey.Y, 0), ffl);
        notes.Add($"room '{room.Name}' {room.Number}: {FtIn.Format(spec.Width)} x {FtIn.Format(spec.Depth)} inside, walls S {FtIn.Format(spec.WallSouth)} E {FtIn.Format(spec.WallEast)} " +
                  $"N {FtIn.Format(spec.WallNorth)} W {FtIn.Format(spec.WallWest)}" + (rect.Square ? "" : $", turned {rect.AngleDeg:0.#} degrees from the project axes"));
        bool Inside(XYZ p, double pad = 0) { var q = rect.ToRoom(In(p)); return q.X > -pad && q.X < rect.Width + pad && q.Y > -pad && q.Y < rect.Depth + pad; }
        bool Ours(Element e) => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() == marker;

        // ---- doors
        var wallIds = walls.Values.Select(w => w.Id).ToHashSet();
        var doors = new List<(DoorSpec Spec, ElementId Id, string How)>();
        foreach (var fi in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
        {
            if (fi.Location is not LocationPoint lp) continue;
            if (fi.Host is not null && !wallIds.Contains(fi.Host.Id) && !Inside(lp.Point, 12)) continue;
            var at = ExistingRoom.WallOf(spec, rect.ToRoom(In(lp.Point)));
            if (at is null) continue;
            double? width = Length(fi, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM, "Width");
            double height = Length(fi, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM, "Height") ?? 84;
            var arc = SwingArc(fi, view, width / 12);
            double? hingeS = null; bool swingIn = true; string how;
            var wl = RoomGeometry.Wall(spec, at.Value.Wall);
            if (arc is { } a)
            {
                var hinge = rect.ToRoom(In(a.Hinge)); var mid = rect.ToRoom(In(a.Mid));
                hingeS = wl.SOf(hinge);
                swingIn = wl.DOf(mid) > 0;
                width ??= a.Radius * 12;
                how = "swing read from its plan arc";
            }
            else how = "no swing arc in the plan: assumed to swing in, hinged at the nearer corner";
            if (width is null) { warnings.Add($"door '{fi.Symbol.FamilyName} : {fi.Symbol.Name}': no width found, 2'-6\" assumed"); width = 30; }
            doors.Add((ExistingRoom.Door(spec, at.Value.Wall, at.Value.S, width.Value, height, hingeS, swingIn), fi.Id, how));
        }
        if (doors.Count == 0)
            throw new EngineSetupException("No door was found in the walls of this room. The engine keeps the door clear, so the room needs its door first.");
        doors = doors.OrderByDescending(d => d.Spec.Width).ToList();
        foreach (var (d, _, how) in doors) notes.Add($"door kept: {ExistingRoom.Describe(d)} ({how})");
        spec = spec with { Door = doors[0].Spec, OtherDoors = doors.Skip(1).Select(d => d.Spec).ToList() };

        // ---- windows low enough to matter
        foreach (var w in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            if (w.Host is not null && wallIds.Contains(w.Host.Id) && (w.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 99) * 12 < 84)
                warnings.Add($"window '{w.Symbol.FamilyName}' in the walls with its sill below 7'-0\": the engine does not keep windows clear, check the wall items round it");

        // ---- ceiling, floor, wall height
        var centre = rect.ToModel(new Pt(rect.Width / 2, rect.Depth / 2));
        bool OverCentre(BoundingBoxXYZ bb) => bb.Min.X * 12 <= centre.X && bb.Max.X * 12 >= centre.X && bb.Min.Y * 12 <= centre.Y && bb.Max.Y * 12 >= centre.Y;
        // a ceiling that covers the room's centre (one ceiling may span several rooms)
        var ceiling = new FilteredElementCollector(doc).OfClass(typeof(Ceiling)).Cast<Ceiling>()
            .FirstOrDefault(c => !Ours(c) && c.LevelId == level.Id && c.get_BoundingBox(null) is { } bb && OverCentre(bb));
        if (ceiling?.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.AsDouble() is double ch && ch > 0)
        {
            spec = spec with { CeilingHeight = Math.Round(ch * 12, 3) };
            notes.Add($"ceiling kept: '{ceiling.Name}' at {FtIn.Format(spec.CeilingHeight)}");
        }
        else notes.Add($"no ceiling in the room: one is added at {FtIn.Format(spec.CeilingHeight)}");
        bool hasFloor = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>()
            .Any(f => !Ours(f) && f.get_BoundingBox(null) is { } bb && OverCentre(bb) && Math.Abs(bb.Max.Z - ffl) * 12 < 3);
        notes.Add(hasFloor ? "floor kept: a floor is already there, no tile finish added" : "no floor in the room: a 1\" tile finish is added");
        var tops = walls.Values.Select(w => w.get_BoundingBox(null)).Where(b => b is not null).Select(b => (b!.Max.Z - ffl) * 12).ToList();
        if (tops.Count > 0) spec = spec with { SlabHeight = Math.Round(Math.Max(tops.Min(), spec.CeilingHeight + 6), 3) };

        // ---- anything already standing in the room that the engine did not place
        var foreign = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Where(f => f.Location is LocationPoint p && Inside(p.Point) && f.LevelId == level.Id
                        && f.Category?.Id.Value is long cat && cat != (long)BuiltInCategory.OST_Doors && cat != (long)BuiltInCategory.OST_Windows
                        && !(f.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").StartsWith(markerPrefix + ":"))
            .Select(f => f.Symbol.FamilyName).Distinct().ToList();
        if (foreign.Count > 0)
            warnings.Add($"already in the room and not placed by the engine: {string.Join(", ", foreign.Take(6))}. The engine plans as if the room were empty: delete them, or cancel.");

        return new ExistingToilet(spec, frame, level, room, walls, ceiling?.Id, hasFloor, doors.Select(d => d.Id).ToList(), notes, warnings, rect);
    }

    /// <summary>The name the toilet's views and sheets carry: the room's name and number, so two bathrooms never share it.</summary>
    public static string ToiletName(Room room)
    {
        string name = (room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "").Trim();
        string number = (room.Number ?? "").Trim();
        if (number.Length > 0 && !name.EndsWith(" " + number) && name != number) name = $"{name} {number}";
        if (name.Length == 0) name = "TOILET";
        return name.ToUpperInvariant();
    }

    private static double? Length(FamilyInstance fi, BuiltInParameter a, BuiltInParameter b, string name)
    {
        foreach (var e in new Element[] { fi, fi.Symbol })
        {
            var p = e.get_Parameter(a) ?? e.get_Parameter(b) ?? e.LookupParameter(name);
            if (p is { StorageType: StorageType.Double } && p.AsDouble() > 0) return Math.Round(p.AsDouble() * 12, 3);
        }
        return null;
    }

    /// <summary>The door's swing arc as drawn in the plan: its centre is the hinge, its middle shows the side it swings to.</summary>
    internal static (XYZ Hinge, XYZ Mid, double Radius)? SwingArc(FamilyInstance door, View view, double? radiusFt)
    {
        var geo = door.get_Geometry(new Options { View = view, IncludeNonVisibleObjects = false });
        if (geo is null) return null;
        var arcs = new List<Arc>();
        void Scan(GeometryElement ge)
        {
            foreach (var o in ge)
            {
                if (o is GeometryInstance gi) Scan(gi.GetInstanceGeometry());
                else if (o is Arc arc && arc.IsBound) arcs.Add(arc);
            }
        }
        Scan(geo);
        Arc? best = radiusFt is double r
            ? arcs.Where(x => Math.Abs(x.Radius - r) <= 0.15 * r).OrderBy(x => Math.Abs(x.Radius - r)).FirstOrDefault()
            : arcs.Where(x => x.Radius >= 1.5 && x.Radius <= 4.5).OrderByDescending(x => x.Radius).FirstOrDefault();
        if (best is null) return null;
        return (best.Center, best.Evaluate(0.5, true), best.Radius);
    }
}
