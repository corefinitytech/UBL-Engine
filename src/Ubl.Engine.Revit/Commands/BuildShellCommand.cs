using System.Diagnostics;
using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Ubl.Engine.Core.Cad;
using Ubl.Engine.Core.Json;
using Ubl.Engine.Core.Sheets;
using Ubl.Engine.Dwg;
using Ubl.Engine.Revit.Build;
using Ubl.Engine.Revit.Config;
using Ubl.Engine.Revit.Sheets;
using CheckStatus = Ubl.Engine.Core.Shell.CheckStatus;
using RecognitionException = Ubl.Engine.Core.Shell.RecognitionException;
using ShellModel = Ubl.Engine.Core.Shell.ShellModel;
using ShellRecogniser = Ubl.Engine.Core.Shell.ShellRecogniser;

namespace Ubl.Engine.Revit.Commands;

/// <summary>
/// Page 7 of the tender set: pick the client's DWG, recognise the existing shell, build it in 3D in the
/// Existing phase, and set up the AR-21 view and sheet for the draftsperson to annotate and export to PDF.
/// Draft annotation is an opt-in checkbox. One click, one undo step.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class BuildShellCommand : IExternalCommand
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

        string? path = PickDrawing();
        if (path is null) return Result.Cancelled;

        string addinDir = Path.GetDirectoryName(typeof(BuildShellCommand).Assembly.Location)!;
        var cfg = EngineConfig.Load(Path.Combine(addinDir, "engine.config.json"));
        string layersFile = Path.Combine(addinDir, "layers.json");
        var layers = File.Exists(layersFile) ? LayerAliases.Load(layersFile) : LayerAliases.Default;
        var inputs = ProjectInputs.LoadOrDefault(Path.Combine(Path.GetDirectoryName(path)!, "project.json"));

        // ---- recognise (pure C#, no Revit)
        var sw = Stopwatch.StartNew();
        ShellModel shell;
        Ar21Plan plan;
        try
        {
            shell = new ShellRecogniser(layers).Recognise(AcadReader.Read(path));
            plan = Ar21Planner.Plan(shell, inputs, null, cfg.BeamLabel);
        }
        catch (RecognitionException ex)
        {
            TaskDialog.Show("UBL Engine", "The engine refused this drawing:\n\n" + ex.Message);
            return Result.Failed;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("UBL Engine", $"Could not read {Path.GetFileName(path)}:\n\n{ex.Message}\n\n" +
                                          "If it is open in AutoCAD, close it or copy the file, then try again.");
            return Result.Failed;
        }
        long recogniseMs = sw.ElapsedMilliseconds;

        var (go, annotate) = Confirm(shell, plan);
        if (!go) return Result.Cancelled;

        // ---- replace a previous run, if any
        var previous = PreviousOutput(doc, cfg);
        if (previous.Count > 0)
        {
            var ask = new TaskDialog("UBL Engine")
            {
                MainInstruction = "Replace the previous engine output?",
                MainContent = $"{previous.Count} elements from an earlier run (marked {cfg.Marker}, the {cfg.ViewName} view and sheet {cfg.SheetNumber}) will be deleted and rebuilt.",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            };
            if (ask.Show() != TaskDialogResult.Yes) return Result.Cancelled;
            if (!LeaveViews(uidoc, previous))
            {
                TaskDialog.Show("UBL Engine", "Open any other plan or 3D view first: Revit cannot delete the view that is currently active.");
                return Result.Cancelled;
            }
        }

        // ---- build: one transaction group, so the whole thing is one undo
        var log = new List<string>();
        var origin = new ModelFrame(shell.Envelope.X0, shell.Envelope.YFrontAll);
        ViewSheet? sheet = null;
        using (var group = new TransactionGroup(doc, "UBL Engine: AR-21 as-built shell"))
        {
            group.Start();
            try
            {
                if (previous.Count > 0)
                    Run(doc, "Clear previous engine output", log, () => doc.Delete(previous));
                BuiltModel? model = null;
                Run(doc, "Build existing shell", log, () => model = new ShellBuilder(doc, cfg, origin, log).Build(shell));
                Run(doc, "AR-21 view and sheet", log, () => sheet = new Ar21SheetBuilder(doc, cfg, origin, log).Build(shell, plan, model!, inputs, annotate).Sheet);
                group.Assimilate();
            }
            catch (EngineSetupException ex)
            {
                group.RollBack();
                TaskDialog.Show("UBL Engine", "Setup needed before the engine can build:\n\n" + ex.Message);
                return Result.Failed;
            }
            catch (Exception ex)
            {
                group.RollBack();
                string where = SafeWrite(path, ".engine-error.txt", ex.ToString());
                message = $"{ex.Message}\n\nDetails: {where}";
                return Result.Failed;
            }
        }
        long totalMs = sw.ElapsedMilliseconds;

        // ---- report next to the drawing
        SafeWrite(path, ".shell.json", EngineJson.Serialize(shell));
        var report = new StringBuilder();
        report.AppendLine($"UBL Engine as-built shell: {Path.GetFileName(path)}");
        report.AppendLine($"recognised in {recogniseMs} ms, built in {totalMs - recogniseMs} ms, scale {plan.Scale.Name}");
        report.AppendLine(shell.Summary());
        foreach (var c in shell.Checks) report.AppendLine($"[{c.Status.ToString().ToLowerInvariant()}] {c.Name}: {c.Detail}");
        foreach (var w in plan.Warnings) report.AppendLine($"[plan] {w}");
        foreach (var l in log) report.AppendLine($"[build] {l}");
        report.AppendLine("missing inputs: " + string.Join("; ", shell.MissingInputs));
        string reportPath = SafeWrite(path, ".engine-report.txt", report.ToString());

        if (sheet is not null) uidoc.ActiveView = sheet;
        TaskDialog.Show("UBL Engine",
            $"AR-21 built in {totalMs / 1000.0:0.0} s.\n\n{shell.Summary()}\n\n" +
            $"{log.Count} build notes and the full check list are in:\n{reportPath}");
        return Result.Succeeded;
    }

    /// <summary>Writes beside the drawing; falls back to the temp folder when that folder is read-only.</summary>
    private static string SafeWrite(string drawingPath, string extension, string content)
    {
        string target = Path.ChangeExtension(drawingPath, extension);
        try { File.WriteAllText(target, content); return target; }
        catch (Exception)
        {
            target = Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(drawingPath) + extension);
            try { File.WriteAllText(target, content); return target; }
            catch (Exception ex) { return $"(could not write {extension}: {ex.Message})"; }
        }
    }

    private static string? PickDrawing()
    {
        var dlg = new FileOpenDialog("Drawings (*.dwg;*.dxf)|*.dwg;*.dxf") { Title = "Client drawing for the as-built shell" };
        if (dlg.Show() != ItemSelectionDialogResult.Confirmed) return null;
        return ModelPathUtils.ConvertModelPathToUserVisiblePath(dlg.GetSelectedModelPath());
    }

    private static (bool Go, bool Annotate) Confirm(ShellModel shell, Ar21Plan plan)
    {
        var text = new StringBuilder();
        text.AppendLine(shell.Summary());
        text.AppendLine();
        foreach (var c in shell.Checks.Where(c => c.Status != CheckStatus.Info))
            text.AppendLine($"{(c.Status == CheckStatus.Pass ? "OK  " : "CHECK")}  {c.Name}: {c.Detail}");
        if (shell.Unclassified.Count > 0)
            text.AppendLine($"\n{shell.Unclassified.Count} items left out as not part of the existing shell (listed in the report).");
        text.AppendLine($"\nMissing inputs (shown in red on the sheet): {string.Join("; ", shell.MissingInputs)}");
        text.AppendLine($"Sheet scale: {plan.Scale.Name} on {plan.Sheet.Name}");
        var td = new TaskDialog("UBL Engine: as-built shell")
        {
            MainInstruction = "Build this shell, the AR-21 view and the sheet?",
            MainContent = text.ToString(),
            CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
            VerificationText = "Also add draft dimensions and labels (the draftsperson still checks them)",
        };
        bool go = td.Show() == TaskDialogResult.Ok;
        return (go, go && td.WasVerificationChecked());
    }

    /// <summary>Revit cannot delete the active view, so step off the old AR-21 sheet or view before replacing it.</summary>
    private static bool LeaveViews(UIDocument uidoc, List<ElementId> doomed)
    {
        var doc = uidoc.Document;
        if (!doomed.Contains(uidoc.ActiveView.Id)) return true;
        var target = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && !doomed.Contains(v.Id) && v.CanBePrinted &&
                        (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.ThreeD))
            .OrderBy(v => v.ViewType == ViewType.ThreeD ? 1 : 0)
            .FirstOrDefault();
        if (target is null) return false;
        uidoc.ActiveView = target;
        return true;
    }

    private static List<ElementId> PreviousOutput(Document doc, EngineConfig cfg)
    {
        var ids = new List<ElementId>();
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            var p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (p is not null && p.AsString() == cfg.Marker) ids.Add(e.Id);
        }
        ids.AddRange(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
            .Where(s => s.SheetNumber == cfg.SheetNumber).Select(s => s.Id));
        ids.AddRange(new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
            .Where(v => !v.IsTemplate && v.Name == cfg.ViewName).Select(v => v.Id));
        return ids;
    }

    /// <summary>Runs one transaction; Revit warnings (joins, overlaps) are logged and dismissed, errors roll back.</summary>
    private static void Run(Document doc, string name, List<string> log, Action action)
    {
        using var t = new Transaction(doc, name);
        var opts = t.GetFailureHandlingOptions();
        opts.SetFailuresPreprocessor(new LogWarnings(log));
        t.SetFailureHandlingOptions(opts);
        t.Start();
        action();
        if (t.Commit() != TransactionStatus.Committed)
            throw new InvalidOperationException($"Revit refused the '{name}' step. See the build log.");
    }

    private sealed class LogWarnings(List<string> log) : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var f in accessor.GetFailureMessages())
            {
                if (f.GetSeverity() != FailureSeverity.Warning) continue;
                log.Add($"revit warning: {f.GetDescriptionText()}");
                accessor.DeleteWarning(f);
            }
            return FailureProcessingResult.Continue;
        }
    }
}
