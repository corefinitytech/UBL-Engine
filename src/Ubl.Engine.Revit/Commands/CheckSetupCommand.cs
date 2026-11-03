using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ubl.Engine.Revit.Build;
using Ubl.Engine.Revit.Config;

namespace Ubl.Engine.Revit.Commands;

/// <summary>
/// Read-only check of everything the engine needs in the open project: phases, families, materials, title
/// block, view template, units. Run it before the first build so problems show up as a list, not as an error.
/// Changes nothing in the model.
/// </summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class CheckSetupCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var uidoc = data.Application.ActiveUIDocument;
        if (uidoc is null || uidoc.Document.IsFamilyDocument)
        {
            message = "Open the branch project (not a family) first.";
            return Result.Failed;
        }
        var doc = uidoc.Document;
        string addinDir = Path.GetDirectoryName(typeof(CheckSetupCommand).Assembly.Location)!;
        string cfgPath = Path.Combine(addinDir, "engine.config.json");
        var cfg = EngineConfig.Load(cfgPath);
        var log = new List<string>();
        var types = new TypeFactory(doc, cfg, log);
        var lines = new List<(string Status, string Text)>();
        void Ok(string t) => lines.Add(("OK", t));
        void Check(string t) => lines.Add(("CHECK", t));
        void Missing(string t) => lines.Add(("MISSING", t));

        var app = doc.Application;
        Ok($"Revit {app.VersionNumber} ({app.VersionBuild})");
        if (File.Exists(cfgPath)) Ok("engine.config.json found");
        else Check("engine.config.json not found beside the add-in: built-in defaults are used");

        // units: the engine works in feet internally, this only affects how dimensions read
        try
        {
            var unit = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();
            if (unit == UnitTypeId.FeetFractionalInches || unit == UnitTypeId.FractionalInches || unit == UnitTypeId.Feet)
                Ok("length units are imperial");
            else Check($"length units are {LabelUtils.GetLabelForUnit(unit)}: dimensions will read metric. Use an imperial template for UBL sheets");
        }
        catch (Exception ex) { Check($"could not read the project units: {ex.Message}"); }

        // phase
        var phases = doc.Phases.Cast<Phase>().Select(p => p.Name).ToList();
        if (phases.Contains(cfg.ExistingPhase)) Ok($"phase '{cfg.ExistingPhase}' exists");
        else Check($"phase '{cfg.ExistingPhase}' not found (phases: {string.Join(", ", phases)}): the shell will stay in the default phase");

        // families
        FamilyCheck(doc, types, cfg.ColumnFamily, cfg.ColumnFamilyPath, new[] { cfg.ColumnWidthParam, cfg.ColumnDepthParam }, Ok, Check, Missing);
        FamilyCheck(doc, types, cfg.BeamFamily, cfg.BeamFamilyPath, new[] { cfg.BeamWidthParam, cfg.BeamDepthParam }, Ok, Check, Missing);

        // base types to copy
        if (new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().Any(t => t.Kind == WallKind.Basic)) Ok("a basic wall type exists to copy");
        else Missing("no basic wall type in the project");
        if (new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Any(t => !t.IsFoundationSlab)) Ok("a floor type exists to copy");
        else Missing("no floor type in the project");
        if (new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Any()) Ok("a text type exists to copy");
        else Missing("no text type in the project");

        // materials (they give the brick and concrete cut patterns on the plan)
        foreach (var name in new[] { cfg.BrickMaterial, cfg.ConcreteMaterial })
        {
            var (m, exact) = types.FindMaterial(name);
            if (m is null) Check($"no material like '{name}': walls will have no cut pattern");
            else if (exact) Ok($"material '{name}'");
            else Check($"material '{name}' not found: will use '{m.Name}'");
        }

        // title block
        var titleBlocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsElementType().Cast<FamilySymbol>().ToList();
        if (titleBlocks.Any(t => t.FamilyName == cfg.TitleBlockFamily)) Ok($"title block '{cfg.TitleBlockFamily}'");
        else if (titleBlocks.Count > 0) Check($"title block '{cfg.TitleBlockFamily}' not loaded: will use '{titleBlocks[0].FamilyName}'");
        else if (types.FindInLibrary("A3 metric.rfa") is string tb) Check($"no title block loaded: will load {Path.GetFileName(tb)} from the library");
        else Check("no title block available: the sheet will be created without one");

        // view template (optional)
        if (cfg.ViewTemplate is null) Ok("no view template configured");
        else if (new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Any(v => v.IsTemplate && v.Name == cfg.ViewTemplate))
            Ok($"view template '{cfg.ViewTemplate}'");
        else Check($"view template '{cfg.ViewTemplate}' not found (optional): project defaults are used");

        // previous output
        int previous = new FilteredElementCollector(doc).WhereElementIsNotElementType()
            .Count(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() == cfg.Marker);
        if (previous > 0) Check($"{previous} elements from an earlier engine run: the next build offers to replace them");

        var text = new StringBuilder();
        foreach (var (status, t) in lines) text.AppendLine($"{status,-8} {t}");
        int missing = lines.Count(l => l.Status == "MISSING");
        int checks = lines.Count(l => l.Status == "CHECK");
        var td = new TaskDialog("UBL Engine: setup check")
        {
            MainInstruction = missing > 0 ? $"{missing} thing(s) missing before the engine can build"
                            : checks > 0 ? $"Ready to build, with {checks} note(s)" : "Ready to build",
            MainContent = text.ToString(),
        };
        td.Show();
        return Result.Succeeded;
    }

    private static void FamilyCheck(Document doc, TypeFactory types, string name, string? path, string[] parameters,
                                    Action<string> ok, Action<string> check, Action<string> missing)
    {
        var family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == name);
        if (family is not null)
        {
            var symbol = family.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol).FirstOrDefault(s => s is not null);
            var absent = parameters.Where(p => symbol?.LookupParameter(p) is null).ToList();
            if (absent.Count == 0) ok($"family '{name}' loaded, type parameters {string.Join(", ", parameters)} present");
            else missing($"family '{name}' has no type parameter {string.Join(", ", absent)}: set the names in engine.config.json");
            return;
        }
        if (path is not null && File.Exists(path)) { check($"family '{name}' not loaded: will load it from {path}"); return; }
        if (types.FindInLibrary(name + ".rfa") is string found) { check($"family '{name}' not loaded: will load it from {found}"); return; }
        missing($"family '{name}' not loaded and not found in the Revit library: load it with Insert > Load Family");
    }
}
