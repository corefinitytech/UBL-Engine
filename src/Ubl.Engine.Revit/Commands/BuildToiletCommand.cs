using System.Diagnostics;
using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Ubl.Engine.Core.Json;
using Ubl.Engine.Core.Sheets;
using Ubl.Engine.Core.Toilet;
using Ubl.Engine.Core.Units;
using Ubl.Engine.Revit.Build;
using Ubl.Engine.Revit.Config;
using Ubl.Engine.Revit.Toilet;

namespace Ubl.Engine.Revit.Commands;

/// <summary>
/// Builds a complete toilet from one input: the room (walls, door, floor finish, ceiling, room element), the
/// fixtures planned by the engine core and placed by measurement, and a plan, ceiling plan and 3D view to check it.
/// Every fixture's final position is read back from Revit and reported against the plan. One undo step.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class BuildToiletCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var uidoc = data.Application.ActiveUIDocument;
        if (uidoc is null || uidoc.Document.IsFamilyDocument)
        {
            message = "Open a project (not a family) first.";
            return Result.Failed;
        }
        var doc = uidoc.Document;
        string addinDir = Path.GetDirectoryName(typeof(BuildToiletCommand).Assembly.Location)!;
        var cfg = ToiletFamilyConfig.Load(Path.Combine(addinDir, "toilet.families.json"));
        var engineCfg = EngineConfig.Load(Path.Combine(addinDir, "engine.config.json"));
        ToiletRules rules;
        DrawingRules drawingRules;
        try { rules = cfg.RulesOrDefault(); }
        catch (Exception ex) { TaskDialog.Show("UBL Engine", $"toilet.families.json has a bad \"rules\" section:\n\n{ex.Message}"); return Result.Failed; }
        try { drawingRules = cfg.DrawingsOrDefault(); }
        catch (Exception ex) { TaskDialog.Show("UBL Engine", $"toilet.families.json has a bad \"drawings\" section:\n\n{ex.Message}"); return Result.Failed; }

        // ---- 1. which toilet: a new one built from scratch, or a bathroom already in the project
        var (newSpec, inputPath, existingMode) = AskSpec();
        if (newSpec is null && !existingMode) return Result.Cancelled;
        var log = new List<string>();
        ToiletSpec spec;
        Level level;
        RoomFrame frame;
        string marker;
        ExistingToilet? existing = null;
        if (existingMode)
        {
            var picked = PickRoom(uidoc, log);
            if (picked is null) return Result.Cancelled;
            marker = $"{cfg.Marker}:ROOM:{picked.UniqueId}";
            try { existing = ExistingRoomReader.Read(doc, (ViewPlan)uidoc.ActiveView, picked, marker, cfg.Marker); }
            catch (EngineSetupException ex) { TaskDialog.Show("UBL Engine", ex.Message); return Result.Failed; }
            var basin = AskBasin();
            if (basin is null) return Result.Cancelled;
            var wet = AskWetWall(existing.Rect.Square);
            if (wet is null) return Result.Cancelled;
            spec = existing.Spec with { Basin = basin.Value, WetWalls = wet };
            // a toilet built from scratch already owns this name (its elements carry it): take a name that stays the same on every re-run
            if (Marked(doc, $"{cfg.Marker}:{spec.Name}").Any()) spec = spec with { Name = $"{spec.Name} (EXISTING)" };
            level = existing.Level;
            frame = existing.Frame;
            foreach (var n in existing.Notes) log.Add($"existing room: {n}");
            foreach (var w in existing.Warnings) log.Insert(0, $"CHECK {w}");
            if (!existing.Rect.Square) log.Add($"existing room: turned {existing.Rect.AngleDeg:0.#} degrees, so its plan sheets are drawn turned with it: check the notes read well");
        }
        else
        {
            spec = newSpec!;
            var lv = PickLevel(uidoc);
            if (lv is null) { message = "The project has no level."; return Result.Failed; }
            level = lv;
            marker = $"{cfg.Marker}:{spec.Name}";
            if (NameTakenByAnother(doc, spec.Name, marker))
            {
                spec = spec with { Name = FreeName(doc, spec.Name) };
                marker = $"{cfg.Marker}:{spec.Name}";
            }
            frame = new RoomFrame(ToiletOrigin(doc, cfg.Marker, marker, spec), XYZ.BasisX, XYZ.BasisY, level.ProjectElevation);
        }

        // ---- 2. families: find or load, then measure each one
        var required = ToiletPlanner.RequiredRoles(spec).Where(r => r != Roles.Vanity).ToList();
        var optional = new List<string> { Roles.SoapDispenser };
        var symbols = new Dictionary<string, FamilySymbol>();
        var placer = new FixturePlacer(doc, level, frame, marker, log);
        var catalog = FixtureCatalog.Defaults();
        var measured = new Dictionary<string, Measured>();
        var omitted = new Dictionary<string, string>();
        var sw = Stopwatch.StartNew();

        using var group = new TransactionGroup(doc, $"UBL Engine: {spec.Name}");
        group.Start();
        try
        {
            string? folder = cfg.FamiliesFolder;
            var missing = required.Concat(optional).Where(r => FindSymbol(doc, cfg.Roles[r]) is null).ToList();
            if (missing.Count > 0 && (folder is null || !Directory.Exists(folder)))
            {
                folder = PickFamiliesFolder(missing.Select(r => cfg.Roles[r].Family));
                if (folder is null) { group.RollBack(); return Result.Cancelled; }
            }
            Run(doc, "Load and measure the toilet families", log, () =>
            {
                foreach (var role in required.Concat(optional))
                {
                    var card = cfg.Roles[role];
                    var sym = FindSymbol(doc, card) ?? LoadSymbol(doc, card, folder, log);
                    if (sym is null)
                    {
                        if (required.Contains(role))
                            throw new EngineSetupException($"Family '{card.Family}' for the {ToiletChecker.Label(role)} is not in the project and not in '{folder}'. " +
                                                           "Load it, or set its name in toilet.families.json.");
                        log.Add($"{ToiletChecker.Label(role)}: family '{card.Family}' not found, left out");
                        continue;
                    }
                    Measured m;
                    try { m = placer.Measure(sym); }
                    catch (EngineSetupException ex) when (role is not (Roles.Wc or Roles.Basin))
                    {
                        // one unusable accessory family should not stop the toilet: leave it out and say why
                        omitted[role] = ex.Message;
                        continue;
                    }
                    measured[role] = m;
                    symbols[role] = sym;
                    catalog = catalog.With(role, SizeOf(role, m, card));
                }
            });
        }
        catch (EngineSetupException ex)
        {
            group.RollBack();
            TaskDialog.Show("UBL Engine", "Families needed before the toilet can be built:\n\n" + ex.Message);
            return Result.Failed;
        }
        catch (Exception ex)
        {
            group.RollBack();
            message = $"Could not load or measure the toilet families: {ex.Message}";
            return Result.Failed;
        }

        // ---- 3. plan with the real sizes
        if (!symbols.ContainsKey(Roles.SoapDispenser) && catalog.Has(Roles.SoapDispenser))
            catalog = new FixtureCatalog(catalog.Sizes.Where(kv => kv.Key != Roles.SoapDispenser).ToDictionary(kv => kv.Key, kv => kv.Value));
        if (omitted.Count > 0) spec = spec with { Omit = omitted.Keys.ToList() };
        var plan = new ToiletPlanner(catalog, rules).Plan(spec);
        if (plan.Refused)
        {
            group.RollBack();
            TaskDialog.Show("UBL Engine", $"The engine refused this toilet.\n\nRule: {plan.RefusalRule}\n{plan.RefusalReason}\n\n{plan.WouldPass}");
            return Result.Failed;
        }
        if (!Confirm(plan, measured, cfg, omitted, existing))
        {
            group.RollBack();
            return Result.Cancelled;
        }

        // ---- 4. replace an earlier build of the same toilet
        var previous = PreviousOutput(doc, marker, spec.Name);
        if (previous.Count > 0)
        {
            if (!LeaveViews(uidoc, previous)) { group.RollBack(); TaskDialog.Show("UBL Engine", "Open another view first: Revit cannot delete the active view."); return Result.Cancelled; }
            Run(doc, "Remove the previous build of this toilet", log, () => doc.Delete(previous));
            log.Add($"replaced the previous build ({previous.Count} elements)");
        }

        // ---- 5. build
        var results = new List<Placement>();
        ToiletViews? views = null;
        ToiletSheets? sheets = null;
        string doorNote = "";
        try
        {
            var room = new ToiletRoomBuilder(doc, level, frame, spec, cfg, engineCfg, marker, log);
            if (existing is null) Run(doc, "Toilet room", log, () => room.BuildRoom(plan));
            else Run(doc, "Toilet fit-out", log, () => room.FitOut(plan, existing.Walls, existing.HasFloor, existing.CeilingId));
            Run(doc, "Toilet views", log, () => views = room.BuildViews());
            if (existing is null) Run(doc, "Toilet door", log, () => doorNote = room.PlaceDoor(plan.Door!, views!.Plan));
            else doorNote = "existing door kept: " + string.Join("; ", new[] { spec.Door }.Concat(spec.OtherDoors).Select(ExistingRoom.Describe));
            Run(doc, "Toilet fixtures", log, () =>
            {
                foreach (var item in plan.Items)
                {
                    if (item.Role == Roles.Vanity) continue;
                    if (!symbols.TryGetValue(item.Role, out var sym)) { log.Add($"{item.Role}: no family, not placed"); continue; }
                    results.Add(placer.Place(item, sym, cfg.Roles[item.Role]));
                }
            });
            // ---- the drawing set (AR-141 to AR-148). A failure here is reported, never undoes the toilet.
            try
            {
                int variant = SheetVariant(doc, drawingRules);
                var set = ToiletDrawingPlanner.Plan(plan, drawingRules, SheetFormat.A3Portrait, variant);
                foreach (var n in set.Notes) log.Add($"drawings: {n}");
                bool IsCeiling(string role) => role is Roles.CeilingLight or Roles.Exhaust;
                var fixtureIds = results.Where(x => x.Id is not null && !IsCeiling(x.Role)).Select(x => x.Id!).ToList();
                var ceilingIds = results.Where(x => x.Id is not null && IsCeiling(x.Role)).Select(x => x.Id!).ToList();
                fixtureIds.AddRange(Marked(doc, marker).Where(e => e is DirectShape).Select(e => e.Id));
                var doorIds = existing?.DoorIds ?? Marked(doc, marker).Where(e => e.Category?.Id.Value == (long)BuiltInCategory.OST_Doors).Select(e => e.Id).ToList();
                // families that are not lighting or mechanical do not show in a ceiling plan: draw a symbol for them
                bool ceilingSymbols = ceilingIds.Any(id => doc.GetElement(id)?.Category?.Id.Value is long c &&
                                                    c != (long)BuiltInCategory.OST_LightingFixtures && c != (long)BuiltInCategory.OST_MechanicalEquipment);
                bool Step(string name, Action action)
                {
                    try { Run(doc, name, log, action); return true; }
                    catch (Exception ex) { log.Add($"drawings: '{name}' not made: {ex.Message}"); return false; }
                }
                sheets = new ToiletDrawingBuilder(doc, level, frame, set, room.Walls, engineCfg, log, Step).Build(views!, fixtureIds, ceilingIds, doorIds, ceilingSymbols, variant);
            }
            catch (Exception ex) { log.Add($"DRAWINGS not made: {ex.Message}"); }
            group.Assimilate();
        }
        catch (EngineSetupException ex)
        {
            group.RollBack();
            TaskDialog.Show("UBL Engine", "Setup needed:\n\n" + ex.Message);
            return Result.Failed;
        }
        catch (Exception ex)
        {
            group.RollBack();
            string where = SafeWrite(spec.Name, ".toilet-error.txt", ex.ToString(), inputPath);
            message = $"{ex.Message}\n\nDetails: {where}";
            return Result.Failed;
        }

        // ---- 6. report
        foreach (var (role, why) in omitted) log.Insert(0, $"LEFT OUT {ToiletChecker.Label(role)}: {why}");
        foreach (var w in FamilyWarnings(measured, cfg)) log.Insert(0, $"FAMILY CHECK {w}");
        string report = Report(spec, plan, measured, cfg, results, doorNote, log, sw.ElapsedMilliseconds);
        string reportPath = SafeWrite(spec.Name, ".toilet-report.txt", report, inputPath);
        SafeWrite(spec.Name, ".toilet-plan.json", EngineJson.Serialize(plan), inputPath);
        SafeWrite(spec.Name, ".toilet-plan.svg", ToiletSvg.Render(plan), inputPath);
        string? outFolder = File.Exists(reportPath) ? Path.GetDirectoryName(reportPath) : null;
        if (sheets is not null)
        {
            var text = new StringBuilder();
            text.AppendLine().AppendLine("DRAWINGS");
            foreach (var (sheet, view, dv) in sheets.Items)
                text.AppendLine($"  {sheet.SheetNumber,-10} {sheet.Name,-44} {view.Name,-28} {dv.Scale.Name}");
            if (sheets.PlaceholderTitleBlock) text.AppendLine("  title block: placeholder frame (load the firm's A3 title block and re-run)");
            try { File.AppendAllText(reportPath, text.ToString()); } catch (Exception) { }
        }
        if (views is not null && outFolder is not null)
        {
            var exported = ExportForReview(doc, views, sheets, outFolder, SafeName(spec.Name));
            try { File.AppendAllText(reportPath, Environment.NewLine + "EXPORTED" + Environment.NewLine + string.Join(Environment.NewLine, exported.Select(e => "  " + e)) + Environment.NewLine); }
            catch (Exception) { }
        }
        if (sheets?.Items.FirstOrDefault().Sheet is { } first) uidoc.ActiveView = first;
        else if (views is not null) uidoc.ActiveView = views.Plan;

        int ok = results.Count(r => r.Ok);
        new TaskDialog("UBL Engine: toilet")
        {
            MainInstruction = $"{spec.Name}: {ok} of {results.Count} fixtures placed and verified",
            MainContent = $"{plan.Arrangement}\n\n" +
                          string.Join("\n", results.Select(r => $"{(r.Ok ? "OK  " : "FAIL")}  {ToiletChecker.Label(r.Role)}: {(r.Ok ? $"within {Math.Max(r.PosErrIn, Math.Abs(r.ZErrIn)):0.###}\", {r.AngErrDeg:0.###} deg" : r.Note)}")) +
                          $"\n\nDoor: {doorNote}" +
                          (sheets is null ? "\n\nDrawings: not made (see the report)" :
                           $"\n\nDrawings: {sheets.Items.Count} sheets, {sheets.Items.FirstOrDefault().Sheet?.SheetNumber} to {sheets.Items.LastOrDefault().Sheet?.SheetNumber}" +
                           (sheets.PlaceholderTitleBlock ? " (placeholder title block)" : "")),
            FooterText = outFolder is null ? reportPath : $"Report, pictures and DWG files: {outFolder}",
        }.Show();
        return Result.Succeeded;
    }

    /// <summary>
    /// Saves pictures (PNG) and DWG files of the toilet's layout, ceiling and 3D views beside the report, so the result can
    /// be checked by someone without Revit: zip the folder and send it.
    /// </summary>
    private static List<string> ExportForReview(Document doc, ToiletViews v, ToiletSheets? sheets, string folder, string name)
    {
        var notes = new List<string>();
        if (sheets is { Items.Count: > 0 })
        {
            var sheetIds = sheets.Items.Select(x => x.Sheet.Id).ToList();
            try
            {
                var pdf = new PDFExportOptions
                {
                    FileName = $"{name} sheets",
                    Combine = true,
                    PaperFormat = ExportPaperFormat.ISO_A3,
                    PaperOrientation = PageOrientationType.Portrait,
                    ZoomType = ZoomType.Zoom,
                    ZoomPercentage = 100,
                    PaperPlacement = PaperPlacementType.Center,
                    HideCropBoundaries = true,
                    HideReferencePlane = true,
                    HideUnreferencedViewTags = true,
                };
                doc.Export(folder, sheetIds, pdf);
                notes.Add($"PDF of the {sheetIds.Count} sheets: {name} sheets.pdf");
            }
            catch (Exception ex) { notes.Add($"sheets PDF not saved: {ex.Message}"); }
            try
            {
                var img = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews,
                    FilePath = Path.Combine(folder, name),
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150,
                    ZoomType = ZoomFitType.FitToPage,
                    FitDirection = FitDirectionType.Vertical,
                    PixelSize = 2480,
                };
                img.SetViewsAndSheets(sheetIds);
                doc.ExportImage(img);
                notes.Add("pictures (PNG) of the sheets");
            }
            catch (Exception ex) { notes.Add($"sheet pictures not saved: {ex.Message}"); }
        }
        var ids = new List<ElementId> { v.Plan.Id, v.Ceiling.Id, v.ThreeD.Id };
        try
        {
            var img = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(folder, name),
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                FitDirection = FitDirectionType.Horizontal,
                PixelSize = 2400,
            };
            img.SetViewsAndSheets(ids);
            doc.ExportImage(img);
            notes.Add($"pictures (PNG) of the layout, ceiling and 3D views in {folder}");
        }
        catch (Exception ex) { notes.Add($"pictures not saved: {ex.Message}"); }
        try
        {
            var dwg = new DWGExportOptions { FileVersion = ACADVersion.R2018, ExportOfSolids = SolidGeometry.Polymesh, MergedViews = true };
            doc.Export(folder, name, ids, dwg);
            notes.Add($"DWG files of the layout, ceiling and 3D views in {folder}");
        }
        catch (Exception ex) { notes.Add($"DWG files not saved: {ex.Message}"); }
        return notes;
    }

    private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Fixture size in the planner's terms (along the wall, out from it, up) from the measured family and its front.</summary>
    internal static FixtureSize SizeOf(string role, Measured m, FamilyCard card)
    {
        var (along, outward) = PlacementMath.PlanSize(new PlacementMath.Extents(m.X0, m.X1, m.Y0, m.Y1, m.Z0, m.Z1), card.FrontLocal);
        var mount = role switch
        {
            Roles.Wc or Roles.FloorTrap => Mount.Floor,
            Roles.Faucet => Mount.OnBasin,
            Roles.Exhaust or Roles.CeilingLight => card.Mount?.ToLowerInvariant() switch
            {
                "wall" => Mount.Wall,
                "ceiling" => Mount.Ceiling,
                _ => m.SizeZ <= Math.Min(m.SizeX, m.SizeY) + 1e-6 ? Mount.Ceiling : Mount.Wall,
            },
            _ => Mount.Wall,
        };
        return new FixtureSize(Math.Round(along, 3), Math.Round(outward, 3), Math.Round(m.SizeZ, 3), mount);
    }

    /// <summary>
    /// Shapes that suggest a family was drawn the wrong way up. Level-based families can only turn about the vertical,
    /// so the engine cannot stand these up; it places them as drawn and says so.
    /// </summary>
    internal static IEnumerable<string> FamilyWarnings(Dictionary<string, Measured> measured, ToiletFamilyConfig cfg)
    {
        foreach (var (role, m) in measured)
        {
            if (role is Roles.Wc or Roles.Basin or Roles.FloorTrap or Roles.CeilingLight or Roles.Exhaust or Roles.Mirror or Roles.Faucet) continue;
            var (along, outward) = PlacementMath.PlanSize(new PlacementMath.Extents(m.X0, m.X1, m.Y0, m.Y1, m.Z0, m.Z1), cfg.Roles[role].FrontLocal);
            if (outward > 6 && outward > 1.5 * m.SizeZ)
                yield return $"{ToiletChecker.Label(role)} sticks out {outward:0.#}\" from the wall but is only {m.SizeZ:0.#}\" tall: the family looks drawn lying down. " +
                             "The engine places it as drawn; the family should be stood up.";
            double far = Math.Max(Math.Max(m.X0, -m.X1), Math.Max(m.Y0, -m.Y1));
            if (far > 12)
                yield return $"{ToiletChecker.Label(role)}: its insertion point is {far:0}\" away from the object. The engine allows for it (it measures the object), but the family should be fixed.";
        }
    }

    // ------------------------------------------------------------------ input

    private static (ToiletSpec? Spec, string? Path, bool Existing) AskSpec()
    {
        var td = new TaskDialog("UBL Engine: toilet")
        {
            MainInstruction = "Which toilet?",
            MainContent = "The engine builds the room, plans the fixtures by the rules and places the client's families by measurement.",
        };
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Manawala male toilet", "5'-0\" x 6'-3\" inside, door 2'-6\" at the corner of the short wall (AR-141)");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "New-branch toilet 1", "7'-11\" x 5'-0\" inside, vanity counter, WC on the wet (far) wall");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "From a toilet.json file...", "Any size, door position and options");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "In a bathroom already in the project...",
                          "Click inside it in the floor plan. Its walls, door and room are kept; the engine reads them and fits it out.");
        td.CommonButtons = TaskDialogCommonButtons.Cancel;
        switch (td.Show())
        {
            case TaskDialogResult.CommandLink1:
                return (new ToiletSpec
                {
                    Name = "MALE TOILET", Width = 60, Depth = 75,
                    Door = new DoorSpec { Wall = Side.South, FromCorner = DoorCorner.End, Offset = 4.5, Width = 30 },
                }, null, false);
            case TaskDialogResult.CommandLink2:
                return (new ToiletSpec
                {
                    Name = "TOILET 1", Width = 95, Depth = 60, Basin = BasinKind.Vanity, WetWalls = new() { Side.East },
                    Door = new DoorSpec { Wall = Side.West, FromCorner = DoorCorner.End, Offset = 4.5, Width = 30 },
                }, null, false);
            case TaskDialogResult.CommandLink3:
                var dlg = new FileOpenDialog("Toilet input (*.json)|*.json") { Title = "Toilet input file" };
                if (dlg.Show() != ItemSelectionDialogResult.Confirmed) return (null, null, false);
                string path = ModelPathUtils.ConvertModelPathToUserVisiblePath(dlg.GetSelectedModelPath());
                try { return (ToiletInput.Parse(File.ReadAllText(path)), path, false); }
                catch (Exception ex)
                {
                    TaskDialog.Show("UBL Engine", $"Could not read {Path.GetFileName(path)}:\n\n{ex.Message}");
                    return (null, null, false);
                }
            case TaskDialogResult.CommandLink4:
                return (null, null, true);
            default:
                return (null, null, false);
        }
    }

    /// <summary>
    /// The room the user clicks in, in the active floor plan. A bathroom drawn without a room element gets one
    /// (named TOILET), so the engine can read its boundary; walls that do not close round the point are refused.
    /// </summary>
    private static Room? PickRoom(UIDocument uidoc, List<string> log)
    {
        var doc = uidoc.Document;
        if (uidoc.ActiveView is not ViewPlan { ViewType: ViewType.FloorPlan, GenLevel: { } lvl } plan)
        {
            TaskDialog.Show("UBL Engine", "Open the floor plan that shows the bathroom, then click Build Toilet again.");
            return null;
        }
        if (plan.SketchPlane is null)
        {
            using var t = new Transaction(doc, "UBL Engine: work plane for picking");
            t.Start();
            plan.SketchPlane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, lvl.ProjectElevation)));
            t.Commit();
        }
        XYZ p;
        try { p = uidoc.Selection.PickPoint("Click inside the bathroom (Esc to cancel)"); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }
        var room = doc.GetRoomAtPoint(new XYZ(p.X, p.Y, lvl.ProjectElevation + 1));
        if (room is not null) return room;
        using (var t = new Transaction(doc, "UBL Engine: room for the toilet"))
        {
            t.Start();
            var r = doc.Create.NewRoom(lvl, new UV(p.X, p.Y));
            doc.Regenerate();
            if (r.Area <= 0)
            {
                t.RollBack();
                TaskDialog.Show("UBL Engine", "The walls do not close round the point you clicked, so there is no room to fit out. Close the walls and try again.");
                return null;
            }
            r.Name = "TOILET";
            t.Commit();
            log.Add($"existing room: it had no room element, so one was added ('{r.Name}' {r.Number})");
            return r;
        }
    }

    private static BasinKind? AskBasin()
    {
        var td = new TaskDialog("UBL Engine: toilet") { MainInstruction = "Which basin?", CommonButtons = TaskDialogCommonButtons.Cancel };
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Half-pedestal basin on the wall", "As in the Manawala toilets (AR-141)");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Basin in a vanity counter", "3'-0\" x 1'-9\" counter in a corner, as in the new-branch toilets");
        return td.Show() switch
        {
            TaskDialogResult.CommandLink1 => BasinKind.HalfPedestal,
            TaskDialogResult.CommandLink2 => BasinKind.Vanity,
            _ => null,
        };
    }

    /// <summary>The wall with the plumbing behind it (the planner prefers the WC there). Empty when the user does not know.</summary>
    private static List<Side>? AskWetWall(bool square)
    {
        var td = new TaskDialog("UBL Engine: toilet")
        {
            MainInstruction = "Which wall has the plumbing behind it?",
            MainContent = "The soil stack, a shaft or another wet room: the engine prefers the WC on that wall. Walls as seen in the floor plan" +
                          (square ? "." : " (the room is turned: take the wall nearest that side).") + "\n\nNot sure? Press No and the engine chooses.",
            CommonButtons = TaskDialogCommonButtons.No | TaskDialogCommonButtons.Cancel,
        };
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Top wall");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Right wall");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Bottom wall");
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "Left wall");
        return td.Show() switch
        {
            TaskDialogResult.CommandLink1 => new List<Side> { Side.North },
            TaskDialogResult.CommandLink2 => new List<Side> { Side.East },
            TaskDialogResult.CommandLink3 => new List<Side> { Side.South },
            TaskDialogResult.CommandLink4 => new List<Side> { Side.West },
            TaskDialogResult.No => new List<Side>(),
            _ => null,
        };
    }

    private static Level? PickLevel(UIDocument uidoc)
    {
        if (uidoc.ActiveView is ViewPlan { GenLevel: { } l }) return l;
        return new FilteredElementCollector(uidoc.Document).OfClass(typeof(Level)).Cast<Level>().OrderBy(x => x.ProjectElevation).FirstOrDefault();
    }

    private static bool Confirm(ToiletPlan plan, Dictionary<string, Measured> measured, ToiletFamilyConfig cfg, Dictionary<string, string> omitted, ExistingToilet? existing)
    {
        var s = plan.Spec;
        var text = new StringBuilder();
        if (existing is not null)
        {
            text.AppendLine("Existing room (walls, doors and room are kept as they are):");
            foreach (var n in existing.Notes) text.AppendLine($"  {n}");
            foreach (var w in existing.Warnings) text.AppendLine($"  CHECK {w}");
            text.AppendLine();
        }
        text.AppendLine($"{FtIn.Format(s.Width)} x {FtIn.Format(s.Depth)} inside, ceiling {FtIn.Format(s.CeilingHeight)}");
        text.AppendLine(plan.Arrangement);
        text.AppendLine($"({plan.CandidatesValid} of {plan.CandidatesTried} arrangements pass every rule)");
        text.AppendLine();
        text.AppendLine("Families as measured (front side from toilet.families.json):");
        foreach (var (role, m) in measured)
            text.AppendLine($"  {ToiletChecker.Label(role)}: {m.SizeX:0.#} x {m.SizeY:0.#} x {m.SizeZ:0.#}\", front {cfg.Roles[role].Front}");
        foreach (var (role, why) in omitted)
            text.AppendLine($"  LEFT OUT {ToiletChecker.Label(role)}: {why}");
        foreach (var w in FamilyWarnings(measured, cfg))
            text.AppendLine($"  CHECK {w}");
        var td = new TaskDialog("UBL Engine: toilet")
        {
            MainInstruction = $"Build {s.Name}?",
            MainContent = text.ToString(),
            CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
        };
        return td.Show() == TaskDialogResult.Ok;
    }

    private static string? PickFamiliesFolder(IEnumerable<string> missing)
    {
        TaskDialog.Show("UBL Engine", "These families are not loaded yet:\n\n" + string.Join(", ", missing) +
                                      "\n\nPick any .rfa in the folder that holds the toilet families (tip: set \"families_folder\" in toilet.families.json to skip this).");
        var dlg = new FileOpenDialog("Revit families (*.rfa)|*.rfa") { Title = "Any family in the toilet families folder" };
        if (dlg.Show() != ItemSelectionDialogResult.Confirmed) return null;
        return Path.GetDirectoryName(ModelPathUtils.ConvertModelPathToUserVisiblePath(dlg.GetSelectedModelPath()));
    }

    private static FamilySymbol? FindSymbol(Document doc, FamilyCard card)
    {
        var fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
            .FirstOrDefault(f => string.Equals(f.Name, card.Family, StringComparison.OrdinalIgnoreCase));
        if (fam is null) return null;
        var syms = fam.GetFamilySymbolIds().Select(id => (FamilySymbol)doc.GetElement(id)).ToList();
        return syms.FirstOrDefault(s => card.Type is not null && s.Name == card.Type) ?? syms.FirstOrDefault();
    }

    private static FamilySymbol? LoadSymbol(Document doc, FamilyCard card, string? folder, List<string> log)
    {
        if (folder is null) return null;
        string file = Path.Combine(folder, card.Family + ".rfa");
        if (!File.Exists(file)) return null;
        if (!doc.LoadFamily(file, out Family fam) || fam is null) return null;
        log.Add($"loaded family {card.Family} from {file}");
        return FindSymbol(doc, card);
    }

    // ------------------------------------------------------------------ previous output, transactions, files

    /// <summary>
    /// Where the toilet's inside south-west corner goes: where it was last time when it is being rebuilt, otherwise
    /// 10 ft to the right of every toilet the engine has already built (at the project origin for the first one).
    /// </summary>
    private static XYZ ToiletOrigin(Document doc, string markerPrefix, string thisMarker, ToiletSpec spec)
    {
        double? sameX = null, sameY = null, othersMaxX = null;
        foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
        {
            if (w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() is not { } c || !c.StartsWith(markerPrefix + ":")) continue;
            var bb = w.get_BoundingBox(null);
            if (bb is null) continue;
            if (c == thisMarker)
            {
                sameX = Math.Min(sameX ?? double.MaxValue, bb.Min.X);
                sameY = Math.Min(sameY ?? double.MaxValue, bb.Min.Y);
            }
            else othersMaxX = Math.Max(othersMaxX ?? double.MinValue, bb.Max.X);
        }
        if (sameX is not null) return new XYZ(sameX.Value + spec.WallWest / 12, sameY!.Value + spec.WallSouth / 12, 0);
        return othersMaxX is null ? XYZ.Zero : new XYZ(othersMaxX.Value + 10 + spec.WallWest / 12, 0, 0);
    }

    private static List<ElementId> PreviousOutput(Document doc, string marker, string name)
    {
        var ids = new List<ElementId>();
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            if (e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() == marker) ids.Add(e.Id);
        var kinds = new[] { "LAYOUT", "CEILING", "3D", "FLOORING", "SANITARY", "ELEVATION A", "ELEVATION B", "ELEVATION C", "ELEVATION D" };
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && v is not ViewSheet && kinds.Any(k => v.Name == $"{name} - {k}" || v.Name.StartsWith($"{name} - {k} (")))
            .Select(v => v.Id).ToHashSet();
        ids.AddRange(views);
        // this toilet's sheets: those showing its views
        foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            if (sheet.GetAllPlacedViews().Any(views.Contains)) ids.Add(sheet.Id);
        return ids;
    }

    /// <summary>True when another toilet already uses this name for its views (they would be replaced by this build otherwise).</summary>
    private static bool NameTakenByAnother(Document doc, string name, string marker) =>
        !Marked(doc, marker).Any() &&
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Any(v => !v.IsTemplate && v.Name == $"{name} - LAYOUT");

    private static string FreeName(Document doc, string name)
    {
        var names = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name).ToHashSet();
        for (int i = 2; ; i++) if (!names.Contains($"{name} ({i}) - LAYOUT")) return $"{name} ({i})";
    }

    /// <summary>Elements carrying this toilet's marker in Comments.</summary>
    private static IEnumerable<Element> Marked(Document doc, string marker) =>
        new FilteredElementCollector(doc).WhereElementIsNotElementType()
            .Where(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() == marker);

    /// <summary>0 for AR-141..AR-148 if free, 1 for AR-141-01..AR-148-08, and so on (the Manawala numbering for a second toilet).</summary>
    private static int SheetVariant(Document doc, DrawingRules r)
    {
        var used = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber).ToHashSet();
        for (int v = 0; ; v++)
            if (!Enumerable.Range(1, 8).Any(i => used.Contains(ToiletDrawingPlanner.SheetNumber(r, i, v)))) return v;
    }

    private static bool LeaveViews(UIDocument uidoc, List<ElementId> doomed)
    {
        if (!doomed.Contains(uidoc.ActiveView.Id)) return true;
        var target = new FilteredElementCollector(uidoc.Document).OfClass(typeof(View)).Cast<View>()
            .FirstOrDefault(v => !v.IsTemplate && !doomed.Contains(v.Id) && v.CanBePrinted && (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.ThreeD));
        if (target is null) return false;
        uidoc.ActiveView = target;
        return true;
    }

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

    /// <summary>Beside the input file, else in Documents\UBL Engine, else the temp folder.</summary>
    private static string SafeWrite(string name, string extension, string content, string? inputPath)
    {
        string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var folders = new List<string>();
        if (inputPath is not null) folders.Add(Path.GetDirectoryName(inputPath)!);
        folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "UBL Engine"));
        folders.Add(Path.GetTempPath());
        foreach (var f in folders)
        {
            try
            {
                Directory.CreateDirectory(f);
                string target = Path.Combine(f, safe + extension);
                File.WriteAllText(target, content);
                return target;
            }
            catch (Exception) { }
        }
        return $"(could not write {extension})";
    }

    private static string Report(ToiletSpec spec, ToiletPlan plan, Dictionary<string, Measured> measured, ToiletFamilyConfig cfg,
                                 List<Placement> results, string doorNote, List<string> log, long ms)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"UBL Engine toilet: {spec.Name}   {DateTime.Now:yyyy-MM-dd HH:mm}   ({ms} ms)");
        sb.AppendLine($"room {FtIn.Format(spec.Width)} x {FtIn.Format(spec.Depth)} inside, walls S {spec.WallSouth}\" E {spec.WallEast}\" N {spec.WallNorth}\" W {spec.WallWest}\", ceiling {FtIn.Format(spec.CeilingHeight)}");
        sb.AppendLine($"door {FtIn.Format(spec.Door.Width)} x {FtIn.Format(spec.Door.Height)} on the {RoomGeometry.Name(spec.Door.Wall)} wall, {FtIn.Format(spec.Door.Offset)} from its {ToiletInput.CompassOf(spec.Door.Wall, spec.Door.FromCorner)} end, swings {(spec.Door.SwingIn ? "in" : "out")}");
        sb.AppendLine($"tolerances: position {FixturePlacer.PosTolIn}\", facing {FixturePlacer.AngTolDeg} degrees");
        sb.AppendLine();
        sb.AppendLine("PLAN");
        foreach (var l in plan.Log) sb.AppendLine($"  {l}");
        sb.AppendLine();
        sb.AppendLine("FAMILIES (measured in Revit)");
        foreach (var (role, m) in measured)
            sb.AppendLine($"  {ToiletChecker.Label(role),-16} '{cfg.Roles[role].Family}', front {cfg.Roles[role].Front}, {m.Placement}: {m}");
        sb.AppendLine();
        sb.AppendLine("PLACED (read back from Revit after placing)");
        foreach (var r in results)
        {
            var item = plan.Item(r.Role)!;
            string where = item.Wall is { } w ? $"{RoomGeometry.Name(w)} wall, centre-line {FtIn.Format(item.S)} from its {ToiletPlanner.EndName(w, DoorCorner.Start)} end" : $"at ({FtIn.Format(item.Anchor.X)}, {FtIn.Format(item.Anchor.Y)})";
            sb.AppendLine($"  [{(r.Ok ? "ok" : "FAIL")}] {ToiletChecker.Label(r.Role),-16} {where}, bottom {FtIn.Format(item.Z0)}");
            sb.AppendLine($"         error {(double.IsNaN(r.PosErrIn) ? "-" : r.PosErrIn.ToString("0.####"))}\" plan, {(double.IsNaN(r.ZErrIn) ? "-" : r.ZErrIn.ToString("0.####"))}\" height, {(double.IsNaN(r.AngErrDeg) ? "-" : r.AngErrDeg.ToString("0.####"))} deg; {r.Note}");
        }
        sb.AppendLine();
        sb.AppendLine($"DOOR: {doorNote}");
        sb.AppendLine();
        sb.AppendLine("NOT BUILT YET: tile setting-out lines, interior elevations A-D, dimensions and tags, the 8 sheets (next module).");
        if (log.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("BUILD LOG");
            foreach (var l in log) sb.AppendLine($"  {l}");
        }
        return sb.ToString();
    }
}
