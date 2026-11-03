using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Revit.Config;
using DbWall = Autodesk.Revit.DB.Wall;
using ShellModel = Ubl.Engine.Core.Shell.ShellModel;

namespace Ubl.Engine.Revit.Build;

/// <summary>Maps drawing inches to Revit feet, with the plot's front-left corner at the project origin.</summary>
internal sealed class ModelFrame(double originX, double originY)
{
    public XYZ P(Pt p, double zFeet) => new((p.X - originX) / 12.0, (p.Y - originY) / 12.0, zFeet);
    public XYZ P(double x, double y, double zFeet) => P(new Pt(x, y), zFeet);
}

/// <summary>What the builder created, for the sheet builder (dimension references) and the report.</summary>
internal sealed class BuiltModel
{
    public required Level FloorLevel { get; init; }
    public required Level SlabLevel { get; init; }
    /// <summary>Same order as ShellModel.Columns, so a planner anchor index finds its instance.</summary>
    public List<FamilyInstance> Columns { get; } = new();
    public List<DbWall> Walls { get; } = new();
    public List<DbWall> YardWalls { get; } = new();
    public List<FamilyInstance> Beams { get; } = new();
    public Floor? GroundFloor { get; set; }
    public Floor? Slab { get; set; }
    public DbWall? RearYardWall { get; set; }
}

/// <summary>
/// Builds the existing shell in 3D: levels, boundary and yard walls, RCC core walls, columns, drop beams,
/// ground floor and roof slab, all in the Existing phase and marked so a re-run can replace them.
/// </summary>
internal sealed class ShellBuilder(Document doc, EngineConfig cfg, ModelFrame frame, List<string> log)
{
    private static double Ft(double inches) => inches / 12.0;

    public BuiltModel Build(ShellModel shell)
    {
        var types = new TypeFactory(doc, cfg, log);

        double ffl = shell.FloorLevel ?? Default(cfg.DefaultFloorLevel, "finished floor level");
        double slabHeight = shell.SlabHeight ?? Default(cfg.DefaultSlabHeight, "slab height");
        var floorLevel = GetOrCreateLevel(cfg.FloorLevelName, Ft(ffl));
        var slabLevel = GetOrCreateLevel(cfg.SlabLevelName, Ft(ffl + slabHeight));
        doc.Regenerate();   // level elevations must be current before geometry is placed on them
        var model = new BuiltModel { FloorLevel = floorLevel, SlabLevel = slabLevel };
        double zf = floorLevel.ProjectElevation, zs = slabLevel.ProjectElevation;

        // Boundary walls: a full-thickness strip along each envelope face, plus any leg turning into the building.
        foreach (var w in shell.Walls)
            foreach (var piece in w.Pieces.Where(p => p.Short >= 2))
            {
                var wall = CreateWall(piece, types.Wall(piece.Short, concrete: false), floorLevel, zf, Ft(slabHeight), structural: false);
                wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(slabLevel.Id);
                model.Walls.Add(wall);
            }

        // Yard walls: the drawing gives no height, so the configured default is used and reported.
        if (shell.YardWalls.Count > 0)
            log.Add($"yard walls built {TypeFactory.Inch(cfg.YardWallHeight)} high (engine default: the drawing gives no height)");
        foreach (var y in shell.YardWalls)
        {
            var wall = CreateWall(y.Bounds, types.Wall(y.Thickness, concrete: false), floorLevel, zf, Ft(cfg.YardWallHeight), structural: false);
            model.YardWalls.Add(wall);
        }
        var plotRear = shell.Envelope.YPlotRear;
        if (plotRear is double pr)
            model.RearYardWall = shell.YardWalls
                .Select((y, i) => (y, i))
                .Where(t => t.y.Bounds.Width > t.y.Bounds.Height && Math.Abs(t.y.Bounds.Y1 - pr) < 0.5)
                .Select(t => model.YardWalls[t.i])
                .FirstOrDefault();

        // RCC cores (lift shaft): structural concrete walls, one per rectangle of the core outline.
        foreach (var core in shell.Cores)
            foreach (var piece in core.Pieces.Where(p => p.Short >= 2))
            {
                var wall = CreateWall(piece, types.Wall(piece.Short, concrete: true), floorLevel, zf, Ft(slabHeight), structural: true);
                wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(slabLevel.Id);
                model.Walls.Add(wall);
            }

        // Columns: base at FFL, top at the slab level.
        foreach (var c in shell.Columns)
        {
            var sym = types.Column(c.Width, c.Depth);
            var fi = doc.Create.NewFamilyInstance(frame.P(c.Bounds.Center, zf), sym, floorLevel, StructuralType.Column);
            fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_PARAM)?.Set(slabLevel.Id);
            fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.Set(0.0);
            fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.Set(0.0);
            Mark(fi);
            model.Columns.Add(fi);
        }

        // Drop beams on the slab level, top of beam at the level. Depth = slab + drop when the note gives the drop.
        if (shell.Beams.Count > 0)
        {
            double drop = shell.BeamDrop ?? Default(12, "beam drop");
            double depth = drop + (cfg.BeamNoteIsDropBelowSlab ? cfg.SlabThickness : 0);
            log.Add($"beams {TypeFactory.Inch(depth)} deep = {(cfg.BeamNoteIsDropBelowSlab ? $"{TypeFactory.Inch(cfg.SlabThickness)} slab (engine default) + " : "")}{TypeFactory.Inch(drop)} drop from the beam note");
            foreach (var b in shell.Beams)
            {
                var sym = types.Beam(b.Width, depth);
                var curve = Line.CreateBound(frame.P(b.P0, zs), frame.P(b.P1, zs));
                var fi = doc.Create.NewFamilyInstance(curve, sym, slabLevel, StructuralType.Beam);
                Mark(fi);
                model.Beams.Add(fi);
            }
        }

        // Ground floor and roof slab over the covered area.
        var covered = shell.Envelope.Covered;
        model.GroundFloor = CreateFloor(covered, types.Floor(cfg.FloorThickness, "GF FLOOR"), floorLevel, zf, structural: false);
        model.Slab = CreateFloor(covered, types.Floor(cfg.SlabThickness, "RCC SLAB"), slabLevel, zs, structural: true);
        log.Add($"roof slab {TypeFactory.Inch(cfg.SlabThickness)} and ground floor {TypeFactory.Inch(cfg.FloorThickness)} thick (engine defaults)");

        SetExistingPhase(model);
        log.Add($"built {model.Columns.Count} columns, {model.Walls.Count} walls, {model.YardWalls.Count} yard walls, {model.Beams.Count} beams, 2 floors");
        return model;
    }

    private double Default(double value, string what)
    {
        log.Add($"{what} not in the drawing: engine default {TypeFactory.Inch(value)} used");
        return value;
    }

    private Level GetOrCreateLevel(string name, double elevationFt)
    {
        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == name);
        if (level is null)
        {
            level = Level.Create(doc, elevationFt);
            level.Name = name;
        }
        else if (Math.Abs(level.Elevation - elevationFt) > 1e-6)
        {
            log.Add($"level {name} moved from {level.Elevation * 12:0.##}\" to {elevationFt * 12:0.##}\" to match the drawing");
            level.Elevation = elevationFt;
        }
        return level;
    }

    private DbWall CreateWall(Box piece, WallType type, Level level, double z, double heightFt, bool structural)
    {
        // centre line along the long side of the rectangle; Revit's default location line is the wall centre
        Pt a, b;
        if (piece.Width >= piece.Height)
        {
            double y = piece.Center.Y;
            a = new Pt(piece.X0, y); b = new Pt(piece.X1, y);
        }
        else
        {
            double x = piece.Center.X;
            a = new Pt(x, piece.Y0); b = new Pt(x, piece.Y1);
        }
        var wall = DbWall.Create(doc, Line.CreateBound(frame.P(a, z), frame.P(b, z)), type.Id, level.Id, heightFt, 0, false, structural);
        Mark(wall);
        return wall;
    }

    private Floor CreateFloor(Box area, FloorType type, Level level, double z, bool structural)
    {
        var c = area.Corners();
        var loop = new CurveLoop();
        for (int i = 0; i < 4; i++)
            loop.Append(Line.CreateBound(frame.P(c[i], z), frame.P(c[(i + 1) % 4], z)));
        var floor = Floor.Create(doc, new List<CurveLoop> { loop }, type.Id, level.Id, structural, null, 0.0);
        Mark(floor);
        return floor;
    }

    private void Mark(Element e) =>
        e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(cfg.Marker);

    private void SetExistingPhase(BuiltModel m)
    {
        var phase = doc.Phases.Cast<Phase>().FirstOrDefault(p => p.Name == cfg.ExistingPhase);
        if (phase is null)
        {
            log.Add($"phase '{cfg.ExistingPhase}' not found: shell left in the default phase");
            return;
        }
        IEnumerable<Element> all = m.Columns.Cast<Element>().Concat(m.Walls).Concat(m.YardWalls).Concat(m.Beams);
        if (m.GroundFloor is not null) all = all.Append(m.GroundFloor);
        if (m.Slab is not null) all = all.Append(m.Slab);
        foreach (var e in all)
        {
            try { e.CreatedPhaseId = phase.Id; }
            catch (Exception ex) { log.Add($"phase not set on {e.Id}: {ex.Message}"); }
        }
    }
}
