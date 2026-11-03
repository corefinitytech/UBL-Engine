using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Core.Toilet;
using Ubl.Engine.Revit.Build;
using Ubl.Engine.Revit.Config;

namespace Ubl.Engine.Revit.Toilet;

/// <summary>The toilet's views, made so the result can be checked straight away.</summary>
internal sealed record ToiletViews(ViewPlan Plan, ViewPlan Ceiling, View3D ThreeD);

/// <summary>
/// Builds the room around the plan: 4 1/2" brick walls on the inside faces, the door (its swing and hinge side
/// checked against the plan by reading the door's own swing arc), a tiled floor finish, the ceiling at the
/// project's ceiling height, a Revit room named after the toilet, the vanity counter when there is one, and a
/// plan, ceiling plan and 3D view of it. Every element carries the toilet's marker so a re-run can replace it.
/// </summary>
internal sealed class ToiletRoomBuilder(Document doc, Level level, RoomFrame frame, ToiletSpec spec, ToiletFamilyConfig cfg, EngineConfig engineCfg, string marker, List<string> log)
{
    private readonly TypeFactory _types = new(doc, engineCfg, log);
    private readonly Dictionary<Side, Wall> _walls = new();
    private ElementId? _ceiling;

    public IReadOnlyDictionary<Side, Wall> Walls => _walls;

    public void BuildRoom(ToiletPlan plan)
    {
        double h = spec.SlabHeight / 12;
        // wall centre-lines: the inside faces pushed out by half of each wall's thickness, meeting at the corners
        double tS = spec.WallSouth, tE = spec.WallEast, tN = spec.WallNorth, tW = spec.WallWest;
        var sw = frame.P(new Pt(-tW / 2, -tS / 2));
        var se = frame.P(new Pt(spec.Width + tE / 2, -tS / 2));
        var ne = frame.P(new Pt(spec.Width + tE / 2, spec.Depth + tN / 2));
        var nw = frame.P(new Pt(-tW / 2, spec.Depth + tN / 2));
        foreach (var (side, a, b) in new[] { (Side.South, sw, se), (Side.East, se, ne), (Side.North, ne, nw), (Side.West, nw, sw) })
        {
            var type = _types.Wall(spec.WallThickness(side), concrete: false, existing: false);
            var wall = Wall.Create(doc, Line.CreateBound(a, b), type.Id, level.Id, h, (frame.Ffl - level.ProjectElevation), false, false);
            Mark(wall);
            _walls[side] = wall;
        }
        doc.Regenerate();
        FloorFinish();
        CeilingElement();
        RoomElement();
        var vanity = plan.Item(Roles.Vanity);
        if (vanity is not null) BuildVanity(vanity, plan.Item(Roles.Basin));
    }

    /// <summary>
    /// Fits out a bathroom that is already in the project: its walls, doors and room stay as they are. A tile floor
    /// finish and a ceiling are added only when the room has none; the vanity counter is modelled as for a new toilet.
    /// </summary>
    public void FitOut(ToiletPlan plan, IReadOnlyDictionary<Side, Wall> walls, bool hasFloor, ElementId? ceiling)
    {
        foreach (var (side, w) in walls) _walls[side] = w;
        if (!hasFloor) FloorFinish();
        if (ceiling is null) CeilingElement();
        else _ceiling = ceiling;
        var vanity = plan.Item(Roles.Vanity);
        if (vanity is not null) BuildVanity(vanity, plan.Item(Roles.Basin));
    }

    private void FloorFinish()
    {
        try
        {
            var floorType = FloorFinishType();
            var floor = Floor.Create(doc, new List<CurveLoop> { InsideLoop() }, floorType.Id, level.Id);
            Mark(floor);
        }
        catch (Exception ex) { log.Add($"floor finish not created: {ex.Message}"); }
    }

    private void CeilingElement()
    {
        try
        {
            var ceilingType = new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).Cast<CeilingType>()
                .OrderByDescending(c => c.Name.Contains("Gypsum", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("GWB", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (ceilingType is null) log.Add("no ceiling type in the project: ceiling not created");
            else
            {
                var ceiling = Ceiling.Create(doc, new List<CurveLoop> { InsideLoop() }, ceilingType.Id, level.Id);
                ceiling.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.Set(spec.CeilingHeight / 12 + (frame.Ffl - level.ProjectElevation));
                Mark(ceiling);
                _ceiling = ceiling.Id;
                log.Add($"ceiling: '{ceilingType.Name}' at {Ubl.Engine.Core.Units.FtIn.Format(spec.CeilingHeight)} (moisture-resistant gypsum per the manual: set the type in the template)");
            }
        }
        catch (Exception ex) { log.Add($"ceiling not created: {ex.Message}"); }
    }

    private void RoomElement()
    {
        try
        {
            var c = frame.P(new Pt(spec.Width / 2, spec.Depth / 2));
            var room = doc.Create.NewRoom(level, new UV(c.X, c.Y));
            room.Name = spec.Name;
            room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET)?.Set(spec.CeilingHeight / 12);
            Mark(room);
        }
        catch (Exception ex) { log.Add($"room not created: {ex.Message}"); }
    }

    /// <summary>The vanity counter: no family was supplied, so the engine models it as a casework solid from the plan.</summary>
    private void BuildVanity(PlacedItem v, PlacedItem? basin)
    {
        var c = v.Plan.Corners().Select(p => frame.P(p)).ToList();
        var loop = CurveLoop.Create(new List<Curve>
        {
            Line.CreateBound(c[0], c[1]), Line.CreateBound(c[1], c[2]), Line.CreateBound(c[2], c[3]), Line.CreateBound(c[3], c[0]),
        });
        double slab = 1.5 / 12, top = v.Height / 12;
        var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, slab);
        solid = SolidUtils.CreateTransformed(solid, Transform.CreateTranslation(XYZ.BasisZ * (top - slab)));
        // cut the basin's opening out of the top, 1" inside its outline, so the bowl shows in plan
        var hole = basin?.Plan.Inflate(-1);
        if (hole is { } h && h.Width > 2 && h.Height > 2)
        {
            try
            {
                var hc = h.Corners().Select(p => frame.P(p)).ToList();
                var hl = CurveLoop.Create(new List<Curve>
                {
                    Line.CreateBound(hc[0], hc[1]), Line.CreateBound(hc[1], hc[2]), Line.CreateBound(hc[2], hc[3]), Line.CreateBound(hc[3], hc[0]),
                });
                var cutter = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { hl }, XYZ.BasisZ, slab + 2.0 / 12);
                cutter = SolidUtils.CreateTransformed(cutter, Transform.CreateTranslation(XYZ.BasisZ * (top - slab - 1.0 / 12)));
                solid = BooleanOperationsUtils.ExecuteBooleanOperation(solid, cutter, BooleanOperationsType.Difference);
            }
            catch (Exception ex) { log.Add($"vanity: basin opening not cut ({ex.Message})"); }
        }
        var ds = DirectShape.CreateElement(doc, new ElementId(BuiltInCategory.OST_Casework));
        ds.ApplicationId = "UBL Engine";
        ds.ApplicationDataId = marker;
        ds.SetShape(new List<GeometryObject> { solid });
        ds.SetName("VANITY COUNTER 3'-0\" x 1'-9\"");
        Mark(ds);
        log.Add("vanity counter modelled as a casework solid: top slab with the basin's opening cut out");
    }

    public ToiletViews BuildViews()
    {
        var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().ToList();
        var fp = plans.First(v => v.ViewFamily == ViewFamily.FloorPlan);
        var cp = plans.FirstOrDefault(v => v.ViewFamily == ViewFamily.CeilingPlan) ?? fp;
        var td = plans.First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
        var box = RoomBox(24);

        var plan = ViewPlan.Create(doc, fp.Id, level.Id);
        plan.Name = Unique($"{spec.Name} - LAYOUT");
        Crop(plan, box);
        var ceiling = ViewPlan.Create(doc, cp.Id, level.Id);
        ceiling.Name = Unique($"{spec.Name} - CEILING");
        Crop(ceiling, box);
        var three = View3D.CreateIsometric(doc, td.Id);
        three.Name = Unique($"{spec.Name} - 3D");
        var a = box.Min; var b = box.Max;
        three.SetSectionBox(new BoundingBoxXYZ { Min = new XYZ(a.X, a.Y, frame.Ffl - 0.5), Max = new XYZ(b.X, b.Y, frame.Ffl + spec.CeilingHeight / 12 + 0.25) });
        three.DetailLevel = ViewDetailLevel.Fine;
        three.DisplayStyle = DisplayStyle.Shading;
        foreach (var cat in new[] { BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids })
        {
            var id = new ElementId(cat);
            if (three.CanCategoryBeHidden(id)) three.SetCategoryHidden(id, true);   // datums would make the picture zoom out to them
        }
        if (_ceiling is not null) three.HideElements(new List<ElementId> { _ceiling });   // so the 3D view looks into the room
        return new ToiletViews(plan, ceiling, three);
    }

    private void Crop(ViewPlan v, BoundingBoxXYZ box)
    {
        v.Scale = 24;   // 1/2" = 1'-0", as the Manawala toilet sheets
        v.DetailLevel = ViewDetailLevel.Fine;
        v.CropBox = box;
        v.CropBoxActive = true;
        v.CropBoxVisible = false;
        // keep the template's elevation markers and level heads out of the view (and out of its exported picture)
        v.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.Set(1);
    }

    /// <summary>
    /// Places the door and makes it swing the way the plan says. Door families differ, so the engine does not
    /// assume which flip does what: it reads the swing arc the door draws in the plan view (the arc's centre is the
    /// hinge, the arc's side is the swing side), flips facing or hand until it matches, and reads it again.
    /// </summary>
    public string PlaceDoor(DoorGeometry door, ViewPlan planView)
    {
        var symbol = DoorType(door);
        if (symbol is null) return "no door family could be loaded: the opening is left empty. Load a door family and re-run.";
        var host = _walls[door.Wall];
        var wl = RoomGeometry.Wall(spec, door.Wall);
        double t = spec.WallThickness(door.Wall);
        var at = frame.P(wl.At((door.S0 + door.S1) / 2, -t / 2));
        var fi = Insert(at, symbol, host);
        string cut = "; opening cut through the wall";
        if (!OpeningCut(host, wl, door, t))
        {
            // seen once in Revit 2026: a door placed in the transaction that set up its type left its wall uncut
            doc.Delete(fi.Id);
            doc.Regenerate();
            fi = Insert(at, symbol, host);
            cut = OpeningCut(host, wl, door, t)
                ? "; opening cut through the wall (door placed a second time to cut it)"
                : "; the wall is NOT cut at the door: delete the door and place it again by hand";
        }
        return Swing(fi, door, planView, symbol, wl) + cut;
    }

    private FamilyInstance Insert(XYZ at, FamilySymbol symbol, Wall host)
    {
        var fi = doc.Create.NewFamilyInstance(at, symbol, host, level, StructuralType.NonStructural);
        Mark(fi);
        doc.Regenerate();
        return fi;
    }

    /// <summary>True when the wall has no material at the middle of the door opening, through its whole thickness.</summary>
    private bool OpeningCut(Wall wall, WallLine wl, DoorGeometry door, double t)
    {
        var mid = (door.S0 + door.S1) / 2;
        var a = frame.P(wl.At(mid, -t - 0.5), door.Height / 2);
        var b = frame.P(wl.At(mid, 0.5), door.Height / 2);
        var line = Line.CreateBound(a, b);
        var geo = wall.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
        if (geo is null) return true;
        foreach (var o in geo)
            if (o is Solid { Volume: > 0 } solid && solid.IntersectWithCurve(line, new SolidCurveIntersectionOptions()).SegmentCount > 0)
                return false;
        return true;
    }

    private string Swing(FamilyInstance fi, DoorGeometry door, ViewPlan planView, FamilySymbol symbol, WallLine wl)
    {
        var wantHinge = frame.P(wl.At(door.HingeS));
        var inward = frame.V(wl.Normal);
        for (int round = 0; round < 3; round++)
        {
            var arc = SwingArc(fi, planView, door.Width / 12);
            if (arc is null)
                return $"door '{symbol.FamilyName} : {symbol.Name}' placed; its swing arc could not be read in the plan view, so its swing and hinge side are NOT verified: check them";
            var (hinge, mid) = arc.Value;
            bool swingsIn = (mid - hinge).DotProduct(inward) > 0;
            double hingeErr = Math.Sqrt(Math.Pow(hinge.X - wantHinge.X, 2) + Math.Pow(hinge.Y - wantHinge.Y, 2)) * 12;
            bool hingeOk = hingeErr <= spec.WallThickness(door.Wall) + 3;   // the arc centre sits on the hinge jamb, within the wall's thickness
            if (swingsIn == door.SwingIn && hingeOk)
                return $"door '{symbol.FamilyName} : {symbol.Name}' placed; swing {(swingsIn ? "in" : "out")} and hinge side verified from its plan arc" +
                       (round == 0 ? "" : $" after {round} flip(s)");
            if (swingsIn != door.SwingIn && fi.CanFlipFacing) fi.flipFacing();
            else if (!hingeOk && fi.CanFlipHand) fi.flipHand();
            else break;
            doc.Regenerate();
        }
        return $"door '{symbol.FamilyName} : {symbol.Name}' placed, but its swing could not be made to match the plan: check it";
    }

    /// <summary>The hinge point and the arc's mid point of the door's swing arc as drawn in the plan view.</summary>
    private static (XYZ Hinge, XYZ Mid)? SwingArc(FamilyInstance door, View view, double radiusFt)
    {
        var geo = door.get_Geometry(new Options { View = view, IncludeNonVisibleObjects = false });
        if (geo is null) return null;
        Arc? best = null;
        void Scan(GeometryElement ge)
        {
            foreach (var o in ge)
            {
                if (o is GeometryInstance gi) Scan(gi.GetInstanceGeometry());
                else if (o is Arc arc && arc.IsBound && Math.Abs(arc.Radius - radiusFt) <= 0.15 * radiusFt)
                    if (best is null || Math.Abs(arc.Radius - radiusFt) < Math.Abs(best.Radius - radiusFt)) best = arc;
            }
        }
        Scan(geo);
        if (best is null) return null;
        return (best.Center, best.Evaluate(0.5, true));
    }

    private FamilySymbol? DoorType(DoorGeometry door)
    {
        string typeName = $"{cfg.DoorTypeName} {TypeFactory.Inch(door.Width)} x {TypeFactory.Inch(door.Height)}";
        var existing = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_Doors)
            .Cast<FamilySymbol>().ToList();
        var sym = existing.FirstOrDefault(s => s.Name == typeName);
        if (sym is null)
        {
            Family? fam = existing.Select(s => s.Family).FirstOrDefault(f => f.Name == cfg.DoorFamily);
            if (fam is null)
            {
                try { fam = _types.LoadFamily(cfg.DoorFamily, null); }
                catch (EngineSetupException) { fam = existing.Select(s => s.Family).FirstOrDefault(); }
            }
            if (fam is null) return null;
            var first = (FamilySymbol)doc.GetElement(fam.GetFamilySymbolIds().First());
            sym = (FamilySymbol)first.Duplicate(typeName);
            SetLength(sym, door.Width / 12, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM, "Width");
            SetLength(sym, door.Height / 12, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM, "Height");
            log.Add($"created door type {fam.Name} : {typeName}");
        }
        if (!sym.IsActive) sym.Activate();
        doc.Regenerate();   // the new size must be in the type before a door is cut into a wall
        return sym;
    }

    private void SetLength(FamilySymbol s, double ft, BuiltInParameter a, BuiltInParameter b, string name)
    {
        var p = s.get_Parameter(a) is { IsReadOnly: false } pa ? pa : s.get_Parameter(b) is { IsReadOnly: false } pb ? pb : s.LookupParameter(name);
        if (p is null || p.IsReadOnly) { log.Add($"door type: no writable {name} parameter, kept the family's size"); return; }
        p.Set(ft);
    }

    private FloorType FloorFinishType()
    {
        double th = cfg.FloorFinishThickness;
        string name = $"Toilet floor tile {TypeFactory.Inch(th)}";
        var all = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Where(t => !t.IsFoundationSlab).ToList();
        var ft = all.FirstOrDefault(t => t.Name == name);
        if (ft is not null) return ft;
        ft = (FloorType)(all.FirstOrDefault() ?? throw new EngineSetupException("The project has no floor type to copy.")).Duplicate(name);
        var mats = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();
        var tile = mats.FirstOrDefault(m => m.Name.Contains("Porcelain", StringComparison.OrdinalIgnoreCase))
                   ?? mats.FirstOrDefault(m => m.Name.Contains("Tile", StringComparison.OrdinalIgnoreCase))
                   ?? mats.FirstOrDefault(m => m.Name.Contains("Ceramic", StringComparison.OrdinalIgnoreCase));
        var cs = CompoundStructure.CreateSingleLayerCompoundStructure(MaterialFunctionAssignment.Structure, th / 12, tile?.Id ?? ElementId.InvalidElementId);
        cs.EndCap = EndCapCondition.NoEndCap;   // floors and roofs must use NoEndCap
        ft.SetCompoundStructure(cs);
        return ft;
    }

    private CurveLoop InsideLoop()
    {
        var p = new[] { new Pt(0, 0), new Pt(spec.Width, 0), new Pt(spec.Width, spec.Depth), new Pt(0, spec.Depth) }.Select(x => frame.P(x)).ToList();
        return CurveLoop.Create(new List<Curve> { Line.CreateBound(p[0], p[1]), Line.CreateBound(p[1], p[2]), Line.CreateBound(p[2], p[3]), Line.CreateBound(p[3], p[0]) });
    }

    private BoundingBoxXYZ RoomBox(double marginIn)
    {
        // all four corners: the room may be turned against the model axes
        var c = new[]
        {
            new Pt(-spec.WallWest - marginIn, -spec.WallSouth - marginIn), new Pt(spec.Width + spec.WallEast + marginIn, -spec.WallSouth - marginIn),
            new Pt(spec.Width + spec.WallEast + marginIn, spec.Depth + spec.WallNorth + marginIn), new Pt(-spec.WallWest - marginIn, spec.Depth + spec.WallNorth + marginIn),
        }.Select(p => frame.P(p)).ToList();
        return new BoundingBoxXYZ
        {
            Min = new XYZ(c.Min(p => p.X), c.Min(p => p.Y), frame.Ffl - 10),
            Max = new XYZ(c.Max(p => p.X), c.Max(p => p.Y), frame.Ffl + 10),
        };
    }

    private string Unique(string name)
    {
        var names = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name).ToHashSet();
        if (!names.Contains(name)) return name;
        for (int i = 2; ; i++) if (!names.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    private void Mark(Element e) => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(marker);
}
