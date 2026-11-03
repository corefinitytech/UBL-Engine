using System.IO;
using Autodesk.Revit.DB;
using Ubl.Engine.Revit.Config;

namespace Ubl.Engine.Revit.Build;

/// <summary>Raised when the Revit project or family library is missing something the engine needs.</summary>
public sealed class EngineSetupException(string message) : Exception(message);

/// <summary>
/// Finds or creates the types the shell needs, named after what the drawing says ("18"X25" RCC COLUMN"),
/// so a Type Name tag prints the same text as the Manawala sheets. Types are created once and reused.
/// </summary>
internal sealed class TypeFactory(Document doc, EngineConfig cfg, List<string> log)
{
    private readonly Dictionary<string, ElementType> _cache = new();

    public static double Ft(double inches) => inches / 12.0;

    /// <summary>9" -> 9", 4.5" -> 4 1/2", 25" -> 25". Inches only, as type names in the Manawala set read.</summary>
    public static string Inch(double v)
    {
        double r = Math.Round(v * 2, MidpointRounding.AwayFromZero) / 2;
        int whole = (int)Math.Floor(r);
        return $"{whole}{(r - whole >= 0.5 ? " 1/2" : "")}\"";
    }

    public WallType Wall(double thickness, bool concrete, bool existing = true)
    {
        string name = $"{(concrete ? "RCC" : "Brick")} {Inch(thickness)}{(existing ? " (Existing)" : "")}";
        if (_cache.TryGetValue(name, out var cached)) return (WallType)cached;
        var all = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
        var wt = all.FirstOrDefault(t => t.Name == name);
        if (wt is null)
        {
            var baseType = (cfg.BaseWallType is null ? null : all.FirstOrDefault(t => t.Name == cfg.BaseWallType && t.Kind == WallKind.Basic))
                           ?? all.FirstOrDefault(t => t.Kind == WallKind.Basic)
                           ?? throw new EngineSetupException("The project has no basic wall type to copy. Start from the firm's template.");
            wt = (WallType)baseType.Duplicate(name);
            var cs = CompoundStructure.CreateSingleLayerCompoundStructure(
                MaterialFunctionAssignment.Structure, Ft(thickness), MaterialId(concrete ? cfg.ConcreteMaterial : cfg.BrickMaterial));
            TrySetStructuralLayer(cs);
            wt.SetCompoundStructure(cs);
            log.Add($"created wall type {name}");
        }
        _cache[name] = wt;
        return wt;
    }

    public FloorType Floor(double thickness, string label)
    {
        string name = $"{label} {Inch(thickness)} (Existing)";
        if (_cache.TryGetValue(name, out var cached)) return (FloorType)cached;
        var all = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Where(t => !t.IsFoundationSlab).ToList();
        var ft = all.FirstOrDefault(t => t.Name == name);
        if (ft is null)
        {
            var baseType = (cfg.BaseFloorType is null ? null : all.FirstOrDefault(t => t.Name == cfg.BaseFloorType))
                           ?? all.FirstOrDefault()
                           ?? throw new EngineSetupException("The project has no floor type to copy. Start from the firm's template.");
            ft = (FloorType)baseType.Duplicate(name);
            var cs = CompoundStructure.CreateSingleLayerCompoundStructure(MaterialFunctionAssignment.Structure, Ft(thickness), MaterialId(cfg.ConcreteMaterial));
            cs.EndCap = EndCapCondition.NoEndCap;   // floors and roofs must use NoEndCap (found on the first Revit run)
            TrySetStructuralLayer(cs);
            ft.SetCompoundStructure(cs);
            log.Add($"created floor type {name}");
        }
        _cache[name] = ft;
        return ft;
    }

    public FamilySymbol Column(double width, double depth) =>
        Sized(cfg.ColumnFamily, cfg.ColumnFamilyPath, $"{Inch(width)}X{Inch(depth)} RCC COLUMN",
              (cfg.ColumnWidthParam, width), (cfg.ColumnDepthParam, depth));

    public FamilySymbol Beam(double width, double depth) =>
        Sized(cfg.BeamFamily, cfg.BeamFamilyPath, $"{Inch(width)}X{Inch(depth)} RCC BEAM",
              (cfg.BeamWidthParam, width), (cfg.BeamDepthParam, depth));

    private FamilySymbol Sized(string familyName, string? familyPath, string typeName, params (string param, double inches)[] sizes)
    {
        if (_cache.TryGetValue(typeName, out var cached)) return (FamilySymbol)cached;
        var family = LoadFamily(familyName, familyPath);
        var symbols = family.GetFamilySymbolIds().Select(id => (FamilySymbol)doc.GetElement(id)).ToList();
        var sym = symbols.FirstOrDefault(s => s.Name == typeName);
        if (sym is null)
        {
            sym = (FamilySymbol)symbols.First().Duplicate(typeName);
            foreach (var (param, inches) in sizes)
            {
                var p = sym.LookupParameter(param)
                        ?? throw new EngineSetupException($"Family '{familyName}' has no type parameter '{param}'. Set the parameter name in engine.config.json.");
                p.Set(Ft(inches));
            }
            log.Add($"created type {familyName} : {typeName}");
        }
        if (!sym.IsActive)
        {
            sym.Activate();
            doc.Regenerate();
        }
        _cache[typeName] = sym;
        return sym;
    }

    internal Family LoadFamily(string name, string? path)
    {
        var family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == name);
        if (family is not null) return family;
        var file = path is not null && File.Exists(path) ? path : FindInLibrary(name + ".rfa");
        if (file is not null && doc.LoadFamily(file, out Family loaded))
        {
            log.Add($"loaded family {name} from {file}");
            return loaded;
        }
        throw new EngineSetupException($"Family '{name}' is not loaded in this project and was not found at '{path ?? "(not set)"}' " +
                                       $"or in the Revit {doc.Application.VersionNumber} content library. Load it once (Insert > Load Family), " +
                                       "or set its path in engine.config.json.");
    }

    /// <summary>Finds a family file in this Revit version's installed content library, preferring Imperial content.</summary>
    public string? FindInLibrary(string fileName)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                   "Autodesk", $"RVT {doc.Application.VersionNumber}", "Libraries");
        if (!Directory.Exists(root)) return null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        try
        {
            return Directory.EnumerateFiles(root, fileName, options)
                .OrderByDescending(f => f.Contains("Imperial", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            log.Add($"library search for {fileName} failed: {ex.Message}");
            return null;
        }
    }

    public TextNoteType Text(string name, double heightMm, bool opaque, int color = 0)
    {
        if (_cache.TryGetValue(name, out var cached)) return (TextNoteType)cached;
        var all = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
        var tt = all.FirstOrDefault(t => t.Name == name);
        if (tt is null)
        {
            var baseType = all.FirstOrDefault() ?? throw new EngineSetupException("The project has no text type to copy.");
            tt = (TextNoteType)baseType.Duplicate(name);
            tt.get_Parameter(BuiltInParameter.TEXT_SIZE).Set(heightMm / 304.8);
            tt.get_Parameter(BuiltInParameter.TEXT_FONT).Set(cfg.TextFont);
            tt.get_Parameter(BuiltInParameter.TEXT_BACKGROUND).Set(opaque ? 0 : 1);
            tt.get_Parameter(BuiltInParameter.LINE_COLOR).Set(color);
        }
        _cache[name] = tt;
        return tt;
    }

    private void TrySetStructuralLayer(CompoundStructure cs)
    {
        try { cs.StructuralMaterialIndex = 0; }
        catch (Exception ex) { log.Add($"structural layer not set: {ex.Message}"); }
    }

    /// <summary>
    /// Exact material name first; then the first material whose name has the key word (templates name them
    /// differently, e.g. "Masonry - Brick" or "Brick, Common", "Concrete - Cast-in-Place Concrete" or
    /// "Concrete, Cast-in-Place gray").
    /// </summary>
    private ElementId MaterialId(string name)
    {
        var (m, exact) = FindMaterial(name);
        if (m is not null && !exact) log.Add($"material '{name}' not found: used '{m.Name}'");
        if (m is null)
        {
            log.Add($"material '{name}' not found: layer left as 'By Category' (cut pattern will not show)");
            return ElementId.InvalidElementId;
        }
        return m.Id;
    }

    /// <summary>The material the engine will use for a configured name, and whether it was an exact match.</summary>
    public (Material? Material, bool Exact) FindMaterial(string name)
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();
        var m = all.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (m is not null) return (m, true);
        string key = name.Contains("brick", StringComparison.OrdinalIgnoreCase) ? "brick" : "concrete";
        m = all.Where(x => x.Name.Contains(key, StringComparison.OrdinalIgnoreCase))
               .OrderByDescending(x => x.Name.Contains("cast", StringComparison.OrdinalIgnoreCase))
               .FirstOrDefault();
        return (m, false);
    }
}
