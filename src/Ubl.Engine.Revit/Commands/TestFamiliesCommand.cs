using System.IO;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Ubl.Engine.Core.Json;

namespace Ubl.Engine.Revit.Commands;

/// <summary>
/// Tests every family in a folder for what the placement engine needs. For each family it reads what is inside
/// (category, placement type, imported CAD and where that CAD sits relative to the insertion point, size
/// parameters, reference planes), tries every way of placing it (on a level, on a level's plane, hosted in a
/// wall, on a wall face), then measures the placed instance (size, where the insertion point is on the object,
/// facing) and checks that move, rotate, mirror, flip and mounting height land exactly (1/16", 0.1 degree).
/// All test placements are rolled back. Optionally leaves a review layout so a person can confirm which side
/// of each object Revit treats as its front.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class TestFamiliesCommand : IExternalCommand
{
    private const double PosTol = 1.0 / 16 / 12;        // 1/16 inch, in feet
    private const double AngTolDeg = 0.1;

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var uidoc = data.Application.ActiveUIDocument;
        if (uidoc is null || uidoc.Document.IsFamilyDocument)
        {
            message = "Open a project (a new empty project is best), not a family.";
            return Result.Failed;
        }
        var doc = uidoc.Document;

        var dlg = new FileOpenDialog("Revit families (*.rfa)|*.rfa") { Title = "Pick any family: every .rfa in that folder is tested" };
        if (dlg.Show() != ItemSelectionDialogResult.Confirmed) return Result.Cancelled;
        string picked = ModelPathUtils.ConvertModelPathToUserVisiblePath(dlg.GetSelectedModelPath());
        string folder = Path.GetDirectoryName(picked)!;
        var files = Directory.GetFiles(folder, "*.rfa").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).FirstOrDefault();
        if (level is null) { message = "The project has no level."; return Result.Failed; }

        var ask = new TaskDialog("UBL Engine: family test")
        {
            MainInstruction = $"Test {files.Count} families from \"{Path.GetFileName(folder)}\"?",
            MainContent = "The families are loaded into this project. Each one is then placed every possible way, measured, moved, "
                        + "rotated, mirrored and flipped; all those test placements are undone at the end. Takes about a minute.",
            CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
            VerificationText = "Also leave a review layout: each family with its facing arrow and insertion point, in a new plan view",
        };
        if (ask.Show() != TaskDialogResult.Ok) return Result.Cancelled;
        bool review = ask.WasVerificationChecked();

        var results = new List<FamilyResult>();
        var loaded = new List<(FamilyResult R, Family Family)>();

        // 1. load (kept: the review layout and later builds need the families in the project)
        using (var t = new Transaction(doc, "UBL: load families for test"))
        {
            t.Start();
            foreach (var f in files)
            {
                var r = new FamilyResult { File = Path.GetFileName(f), SizeMb = Math.Round(new FileInfo(f).Length / 1e6, 2) };
                results.Add(r);
                try
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (!doc.LoadFamily(f, new OverwriteOptions(), out Family? fam) || fam is null)
                        fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(x => x.Name == name);
                    if (fam is null) r.Reject("could not be loaded (saved in a newer Revit, or not a loadable family)");
                    else loaded.Add((r, fam));
                }
                catch (Exception ex) { r.Reject("load failed: " + ex.Message); }
            }
            t.Commit();
        }

        // 2. what is inside each family (family documents can only be opened outside a transaction)
        foreach (var (r, fam) in loaded) Inspect(doc, fam, r);

        // 3. placement and transform tests, all rolled back together
        using (var group = new TransactionGroup(doc, "UBL: family placement tests"))
        {
            group.Start();
            for (int i = 0; i < loaded.Count; i++)
            {
                var (r, fam) = loaded[i];
                try { PlacementTests(doc, fam, level, 1000, 1000 + 60 * i, r); }
                catch (Exception ex) { r.Reject("placement test crashed: " + ex.Message); }
            }
            group.RollBack();
        }

        // 4. optional review layout, kept
        string? reviewView = null;
        if (review)
        {
            try { reviewView = ReviewLayout(doc, loaded, level); }
            catch (Exception ex) { results.ForEach(r => r.Notes.Add($"review layout failed: {ex.Message}")); }
        }

        foreach (var r in results) r.Grade();
        string text = Report(results, folder, reviewView);
        string reportPath = Write(folder, "family-test-report.txt", text);
        Write(folder, "family-test.json", JsonSerializer.Serialize(results, EngineJson.Options));

        if (reviewView is not null)
        {
            var v = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(x => x.Name == reviewView);
            if (v is not null) uidoc.ActiveView = v;
        }

        new TaskDialog("UBL Engine: family test")
        {
            MainInstruction = $"{results.Count(r => r.Verdict == "PASS")} pass, {results.Count(r => r.Verdict == "FIXABLE")} fixable, {results.Count(r => r.Verdict == "REJECT")} reject",
            MainContent = string.Join("\n", results.Select(r => $"{r.Verdict}  {r.File}  {r.Issues.FirstOrDefault()}")),
            FooterText = reportPath,
        }.Show();
        return Result.Succeeded;
    }

    // ------------------------------------------------------------------ what is inside the family

    private static void Inspect(Document doc, Family fam, FamilyResult r)
    {
        r.Category = fam.FamilyCategory?.Name ?? "?";
        r.PlacementType = fam.FamilyPlacementType.ToString();
        r.Types = fam.GetFamilySymbolIds().Select(id => doc.GetElement(id)?.Name ?? "?").ToList();
        r.SuggestedCategory = SuggestedCategory(r.File);
        if (r.SuggestedCategory is not null && !string.Equals(r.SuggestedCategory, r.Category, StringComparison.OrdinalIgnoreCase))
            r.Fix($"category is {r.Category}, should be {r.SuggestedCategory} (decides which plans, schedules and filters show it)");

        Document? fd = null;
        try
        {
            fd = doc.EditFamily(fam);
            var owner = fd.OwnerFamily;
            r.WorkPlaneBased = owner.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED)?.AsInteger() == 1;
            r.AlwaysVertical = owner.get_Parameter(BuiltInParameter.FAMILY_ALWAYS_VERTICAL)?.AsInteger() == 1;
            r.Shared = owner.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.AsInteger() == 1;
            r.Host = owner.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR)?.AsValueString();

            var imports = new FilteredElementCollector(fd).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
            r.ImportedCad = imports.Count;
            r.NativeSolids = new FilteredElementCollector(fd).OfClass(typeof(GenericForm)).GetElementCount()
                           + new FilteredElementCollector(fd).OfClass(typeof(FreeFormElement)).GetElementCount();
            r.NestedFamilies = new FilteredElementCollector(fd).OfClass(typeof(FamilyInstance)).GetElementCount();
            foreach (var imp in imports)
            {
                var bb = imp.get_BoundingBox(null);
                if (bb is null) continue;
                bool overOrigin = bb.Min.X <= 1e-6 && bb.Max.X >= -1e-6 && bb.Min.Y <= 1e-6 && bb.Max.Y >= -1e-6;
                r.CadExtents.Add($"X {In(bb.Min.X)} to {In(bb.Max.X)}, Y {In(bb.Min.Y)} to {In(bb.Max.Y)}, Z {In(bb.Min.Z)} to {In(bb.Max.Z)}"
                                 + (overOrigin ? "" : "  (does not cover the insertion point)"));
            }
            foreach (ReferencePlane rp in new FilteredElementCollector(fd).OfClass(typeof(ReferencePlane)))
            {
                bool origin = rp.get_Parameter(BuiltInParameter.DATUM_PLANE_DEFINES_ORIGIN)?.AsInteger() == 1;
                r.ReferencePlanes.Add(rp.Name + (origin ? " (origin)" : ""));
            }
            foreach (FamilyParameter p in fd.FamilyManager.Parameters)
            {
                if (p.Definition.GetDataType() != SpecTypeId.Length) continue;
                r.LengthParameters.Add($"{p.Definition.Name} ({(p.IsInstance ? "instance" : "type")}{(p.IsDeterminedByFormula ? ", formula" : "")})");
            }
        }
        catch (Exception ex) { r.Notes.Add($"family document not inspected: {ex.Message}"); }
        finally { fd?.Close(false); }

        if (r.ImportedCad > 0)
            r.Fix($"{r.ImportedCad} imported CAD object(s) inside: plans and elevations show the CAD linework, no Revit materials, cannot be resized");
        if (r.SizeMb > 3) r.Fix($"heavy for a fixture ({r.SizeMb} MB): slows every model it is placed in");
        if (!r.LengthParameters.Any(p => p.StartsWith("Width") || p.StartsWith("Depth") || p.StartsWith("Length")))
            r.Notes.Add("fixed size (no width/depth parameters): fine for a standard fixture, otherwise one family per size");
    }

    private static string? SuggestedCategory(string file)
    {
        string n = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        if (new[] { "wc", "basin", "faucet", "shower", "trap", "urinal", "sink", "commode", "tap" }.Any(n.Contains)) return "Plumbing Fixtures";
        if (n.Contains("light") || n.Contains("lamp")) return "Lighting Fixtures";
        if (n.Contains("exhaust") || n.Contains("fan")) return "Mechanical Equipment";
        if (new[] { "dryer", "dispenser", "holder", "mirror", "grab" }.Any(n.Contains)) return "Specialty Equipment";
        return null;
    }

    // ------------------------------------------------------------------ placement and transforms

    private enum Way { LevelPoint, LevelPlane, WallHosted, WallFace }

    private static Way[] Preferred(FamilyPlacementType t) => t switch
    {
        FamilyPlacementType.OneLevelBased => new[] { Way.LevelPoint, Way.LevelPlane, Way.WallFace, Way.WallHosted },
        FamilyPlacementType.OneLevelBasedHosted => new[] { Way.WallHosted, Way.WallFace, Way.LevelPoint, Way.LevelPlane },
        FamilyPlacementType.WorkPlaneBased => new[] { Way.LevelPlane, Way.WallFace, Way.LevelPoint, Way.WallHosted },
        _ => new[] { Way.LevelPoint, Way.LevelPlane, Way.WallHosted, Way.WallFace },
    };

    private static FamilyInstance Place(Document doc, FamilySymbol symbol, Level level, XYZ at, Way way)
    {
        switch (way)
        {
            case Way.LevelPoint:
                return doc.Create.NewFamilyInstance(at, symbol, level, StructuralType.NonStructural);
            case Way.LevelPlane:
                return doc.Create.NewFamilyInstance(level.GetPlaneReference(), at, XYZ.BasisX, symbol);
            case Way.WallHosted:
                return doc.Create.NewFamilyInstance(at, symbol, TestWall(doc, level, at, 10), level, StructuralType.NonStructural);
            default:
                var wall = TestWall(doc, level, at, 10);
                var faceRef = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior).First();
                var face = (Face)wall.GetGeometryObjectFromReference(faceRef);
                var p = face.Project(at + 3 * XYZ.BasisZ)?.XYZPoint ?? throw new InvalidOperationException("could not find the wall face");
                return doc.Create.NewFamilyInstance(faceRef, p, XYZ.BasisX, symbol);
        }
    }

    private static Wall TestWall(Document doc, Level level, XYZ at, double halfLength)
    {
        var wall = Wall.Create(doc, Line.CreateBound(at - halfLength * XYZ.BasisX, at + halfLength * XYZ.BasisX), level.Id, false);
        doc.Regenerate();
        return wall;
    }

    private static void PlacementTests(Document doc, Family fam, Level level, double x, double y, FamilyResult r)
    {
        var symbol = fam.GetFamilySymbolIds().Select(id => (FamilySymbol)doc.GetElement(id)).First();
        double z = level.ProjectElevation;
        Tx(doc, "activate", () => { if (!symbol.IsActive) symbol.Activate(); });

        // try every way of placing it; each attempt stays (at its own spot) until the group is rolled back
        var placed = new Dictionary<Way, FamilyInstance>();
        foreach (var way in Enum.GetValues<Way>())
        {
            FamilyInstance? fi = null;
            var at = new XYZ(x + 30 * (int)way, y, z);
            string? err = Tx(doc, "place " + way, () =>
            {
                fi = Place(doc, symbol, level, at, way);
                doc.Regenerate();
                if (fi.get_BoundingBox(null) is null) throw new InvalidOperationException("placed but has no geometry");
                if (way is Way.WallHosted or Way.WallFace && fi.Host is not Wall)
                    throw new InvalidOperationException("Revit placed it but did not attach it to the wall");
            });
            if (err is null && fi is not null && fi.IsValidObject) { placed[way] = fi; r.Placements.Add($"{WayName(way)}: works"); }
            else r.Placements.Add($"{WayName(way)}: no ({Short(err)})");
        }
        var primary = Preferred(fam.FamilyPlacementType).Where(placed.ContainsKey).Select(w => (Way?)w).FirstOrDefault();
        if (primary is null) { r.Reject($"cannot be placed in any way (placement type {fam.FamilyPlacementType})"); return; }
        r.Primary = WayName(primary.Value);
        r.PrimaryWay = primary.Value.ToString();
        var inst = placed[primary.Value];
        bool hosted = primary is Way.WallHosted or Way.WallFace;
        r.Hosted = hosted;

        // size and where the insertion point is, in the family's own axes
        Test(doc, r, "measure", () =>
        {
            var t = inst.GetTransform();
            var inv = t.Inverse;
            var bb = inst.get_BoundingBox(null);
            var local = Corners(bb).Select(inv.OfPoint).ToList();
            double x0 = local.Min(p => p.X), x1 = local.Max(p => p.X), y0 = local.Min(p => p.Y), y1 = local.Max(p => p.Y);
            double z0 = local.Min(p => p.Z), z1 = local.Max(p => p.Z);
            r.SizeIn = new[] { R2((x1 - x0) * 12), R2((y1 - y0) * 12), R2((z1 - z0) * 12) };
            r.InsertionPoint = $"{R2(-x0 * 12)}\" in from its -X side, {R2(-y0 * 12)}\" in from its -Y side, {R2(-z0 * 12)}\" up from its bottom";
            r.BottomAboveLevelIn = R2((bb.Min.Z - z) * 12);
            r.Facing = Dir(inst.FacingOrientation);
            r.Hand = Dir(inst.HandOrientation);
            double gap = Math.Max(Math.Max(x0, -x1), Math.Max(y0, -y1));
            if (gap > 1e-6) r.Fix($"insertion point is {R2(gap * 12)}\" outside the object in plan");
            if (r.SizeIn.Take(2).Max() > 120) r.Fix($"plan size {r.SizeIn[0]}\" x {r.SizeIn[1]}\" is implausible for a fixture: the CAD inside is probably at the wrong scale or far from the origin");
        });

        // move
        Test(doc, r, "move", () =>
        {
            var before = inst.GetTransform().Origin;
            var delta = hosted ? new XYZ(1.2345, 0, 0) : new XYZ(1.2345, -0.789, 0);
            ElementTransformUtils.MoveElement(doc, inst.Id, delta);
            doc.Regenerate();
            double err = (inst.GetTransform().Origin - (before + delta)).GetLength();
            r.Check("move", err <= PosTol, $"off by {err * 12:0.0000}\"");
        });

        // rotate about the insertion point
        foreach (double deg in new[] { 90.0, 37.0, 180.0 })
        {
            Test(doc, r, $"rotate {deg}", () =>
            {
                var t0 = inst.GetTransform();
                ElementTransformUtils.RotateElement(doc, inst.Id, Line.CreateBound(t0.Origin, t0.Origin + XYZ.BasisZ), deg * Math.PI / 180);
                doc.Regenerate();
                var t1 = inst.GetTransform();
                double got = t0.BasisX.AngleOnPlaneTo(t1.BasisX, XYZ.BasisZ) * 180 / Math.PI;
                double angErr = Math.Abs(((got - deg) % 360 + 540) % 360 - 180);
                double posErr = (t1.Origin - t0.Origin).GetLength();
                r.Check($"rotate {deg}", angErr <= AngTolDeg && posErr <= PosTol, $"off by {angErr:0.000} deg and {posErr * 12:0.0000}\"");
            }, failureIsNote: hosted ? "a hosted family turns with its wall, not on its own (expected)" : null);
        }

        // mirror a copy about a plane through a point 3 ft to the side
        Test(doc, r, "mirror", () =>
        {
            if (!ElementTransformUtils.CanMirrorElement(doc, inst.Id)) { r.Check("mirror", false, "Revit refuses to mirror it"); return; }
            var t0 = inst.GetTransform();
            var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisX, t0.Origin + 3 * XYZ.BasisX);
            var ids = ElementTransformUtils.MirrorElements(doc, new List<ElementId> { inst.Id }, plane, true);
            doc.Regenerate();
            var copy = ids.Select(doc.GetElement).OfType<FamilyInstance>().FirstOrDefault();
            if (copy is null) { r.Check("mirror", false, "no mirrored copy was made"); return; }
            double err = (copy.GetTransform().Origin - (t0.Origin + 6 * XYZ.BasisX)).GetLength();
            r.MirroredFlag = copy.Mirrored;
            r.Check("mirror", err <= PosTol, $"off by {err * 12:0.0000}\", copy reports Mirrored = {copy.Mirrored}");
        });

        // flips
        Test(doc, r, "flip", () =>
        {
            r.CanFlipFacing = inst.CanFlipFacing;
            r.CanFlipHand = inst.CanFlipHand;
            if (inst.CanFlipFacing)
            {
                var f0 = inst.FacingOrientation;
                inst.flipFacing();
                doc.Regenerate();
                r.Check("flip facing", inst.FacingOrientation.IsAlmostEqualTo(-f0), "");
            }
            if (inst.CanFlipHand)
            {
                var h0 = inst.HandOrientation;
                inst.flipHand();
                doc.Regenerate();
                r.Check("flip hand", inst.HandOrientation.IsAlmostEqualTo(-h0), "");
            }
        });

        // mounting height: by the elevation parameter where there is one, otherwise by moving it up
        Test(doc, r, "mounting height", () =>
        {
            double b0 = inst.get_BoundingBox(null).Min.Z;
            var p = inst.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            if ((p is null || p.IsReadOnly) && primary == Way.LevelPlane) p = inst.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            if (p is not null && !p.IsReadOnly)
            {
                p.Set(p.AsDouble() + 3.0);
                r.HeightBy = $"parameter \"{p.Definition.Name}\"";
            }
            else
            {
                ElementTransformUtils.MoveElement(doc, inst.Id, 3 * XYZ.BasisZ);
                r.HeightBy = "moving it up (no elevation parameter)";
            }
            doc.Regenerate();
            double dz = inst.get_BoundingBox(null).Min.Z - b0;
            r.Check("mounting height", Math.Abs(dz - 3.0) <= PosTol, $"{r.HeightBy}: rose {dz * 12:0.000}\" for 36\"");
        });
    }

    private static void Test(Document doc, FamilyResult r, string name, Action action, string? failureIsNote = null)
    {
        string? err = Tx(doc, name, action);
        if (err is null) return;
        if (failureIsNote is not null) r.Notes.Add($"{name}: {failureIsNote}");
        else r.Check(name, false, Short(err));
    }

    /// <summary>Runs one step in its own transaction. Warnings are dropped; an error rolls the step back and is returned.</summary>
    private static string? Tx(Document doc, string name, Action action)
    {
        using var t = new Transaction(doc, "UBL test: " + name);
        var capture = new FailureCapture();
        var opts = t.GetFailureHandlingOptions();
        opts.SetFailuresPreprocessor(capture);
        opts.SetClearAfterRollback(true);
        t.SetFailureHandlingOptions(opts);
        t.Start();
        try { action(); }
        catch (Exception ex)
        {
            if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
            return ex.Message;
        }
        var status = t.Commit();
        if (status == TransactionStatus.Committed) return null;
        return capture.Errors.Count > 0 ? string.Join("; ", capture.Errors) : $"Revit rolled the step back ({status})";
    }

    // ------------------------------------------------------------------ review layout

    private static string ReviewLayout(Document doc, List<(FamilyResult R, Family Family)> loaded, Level level)
    {
        string name = $"UBL Family Review {DateTime.Now:yyyy-MM-dd HHmm}";
        using var t = new Transaction(doc, "UBL: family review layout");
        var opts = t.GetFailureHandlingOptions();
        opts.SetFailuresPreprocessor(new FailureCapture());
        t.SetFailureHandlingOptions(opts);
        t.Start();
        var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(v => v.ViewFamily == ViewFamily.FloorPlan);
        var view = ViewPlan.Create(doc, vft.Id, level.Id);
        view.Name = name;
        view.Scale = 24;
        view.DetailLevel = ViewDetailLevel.Fine;
        var textType = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstElementId();
        double z = level.ProjectElevation;
        var crop = new List<XYZ>();

        for (int i = 0; i < loaded.Count; i++)
        {
            var (r, fam) = loaded[i];
            if (r.PrimaryWay is null || !Enum.TryParse(r.PrimaryWay, out Way way)) continue;
            var at = new XYZ(500 + (i % 4) * 10, 500 - (i / 4) * 10, z);
            using var st = new SubTransaction(doc);
            st.Start();
            try
            {
                var symbol = fam.GetFamilySymbolIds().Select(id => (FamilySymbol)doc.GetElement(id)).First();
                if (!symbol.IsActive) symbol.Activate();
                var fi = Place(doc, symbol, level, at, way);
                doc.Regenerate();
                var o = fi.GetTransform().Origin;
                var f = fi.FacingOrientation;
                var flat = new XYZ(f.X, f.Y, 0);
                flat = flat.GetLength() < 1e-6 ? XYZ.BasisY : flat.Normalize();
                var side = new XYZ(-flat.Y, flat.X, 0);
                var o2 = new XYZ(o.X, o.Y, z);
                var tip = o2 + 2.5 * flat;
                void L(XYZ a, XYZ b) => doc.Create.NewDetailCurve(view, Line.CreateBound(a, b));
                L(o2, tip);
                L(tip, tip - 0.5 * flat + 0.25 * side);
                L(tip, tip - 0.5 * flat - 0.25 * side);
                L(o2 - 0.3 * XYZ.BasisX, o2 + 0.3 * XYZ.BasisX);
                L(o2 - 0.3 * XYZ.BasisY, o2 + 0.3 * XYZ.BasisY);
                TextNote.Create(doc, view.Id, new XYZ(at.X - 3.5, at.Y - 3.5, z), $"{fam.Name}\r{r.Primary}", textType);
                st.Commit();
                crop.Add(at);
            }
            catch (Exception ex)
            {
                st.RollBack();
                r.Notes.Add($"not placed in the review layout: {ex.Message}");
            }
        }
        if (crop.Count > 0)
        {
            view.CropBox = new BoundingBoxXYZ
            {
                Min = new XYZ(crop.Min(p => p.X) - 6, crop.Min(p => p.Y) - 6, z - 10),
                Max = new XYZ(crop.Max(p => p.X) + 6, crop.Max(p => p.Y) + 6, z + 10),
            };
            view.CropBoxActive = true;
        }
        if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit rolled the review layout back");
        return name;
    }

    // ------------------------------------------------------------------ report

    private static string Report(List<FamilyResult> results, string folder, string? reviewView)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"UBL Engine family test   {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"folder: {folder}");
        sb.AppendLine($"tolerances: position 1/16\", angle {AngTolDeg} deg");
        sb.AppendLine("PASS = place as is.  FIXABLE = places and transforms exactly, but has issues to fix before production.");
        sb.AppendLine("REJECT = cannot be placed, or a move/rotate/mirror/flip/height test did not land exactly.");
        if (reviewView is not null)
            sb.AppendLine($"Review view \"{reviewView}\": the arrow is the side Revit treats as the front (facing), the cross is the insertion point. "
                        + "Note any family whose arrow does not point to the side a person uses.");
        foreach (var r in results)
        {
            sb.AppendLine();
            sb.AppendLine($"== {r.Verdict}  {r.File}  ({r.SizeMb} MB)");
            sb.AppendLine($"   category: {r.Category}{(r.SuggestedCategory is null || r.SuggestedCategory == r.Category ? "" : $" (should be {r.SuggestedCategory})")}");
            sb.AppendLine($"   placement type: {r.PlacementType}; work-plane based {YesNo(r.WorkPlaneBased)}, always vertical {YesNo(r.AlwaysVertical)}, shared {YesNo(r.Shared)}, host: {r.Host ?? "?"}");
            sb.AppendLine($"   types: {string.Join(", ", r.Types)}");
            sb.AppendLine($"   inside: {r.ImportedCad} imported CAD, {r.NativeSolids} native solids, {r.NestedFamilies} nested families");
            foreach (var c in r.CadExtents) sb.AppendLine($"   imported CAD extents (from the insertion point): {c}");
            sb.AppendLine($"   length parameters: {(r.LengthParameters.Count == 0 ? "none" : string.Join(", ", r.LengthParameters))}");
            sb.AppendLine($"   reference planes: {(r.ReferencePlanes.Count == 0 ? "none" : string.Join(", ", r.ReferencePlanes))}");
            foreach (var p in r.Placements) sb.AppendLine($"   placement {p}");
            if (r.Primary is not null) sb.AppendLine($"   tested as: {r.Primary}");
            if (r.SizeIn is not null)
            {
                sb.AppendLine($"   size along its own axes: X {r.SizeIn[0]}\", Y {r.SizeIn[1]}\", Z {r.SizeIn[2]}\"; bottom {r.BottomAboveLevelIn}\" above the level");
                sb.AppendLine($"   insertion point: {r.InsertionPoint}");
                sb.AppendLine($"   facing {r.Facing}, hand {r.Hand}; can flip facing {YesNo(r.CanFlipFacing)}, hand {YesNo(r.CanFlipHand)}");
            }
            foreach (var c in r.Checks) sb.AppendLine($"   [{(c.Ok ? "pass" : "FAIL")}] {c.Name}{(c.Detail == "" ? "" : ": " + c.Detail)}");
            foreach (var i in r.Issues) sb.AppendLine($"   issue: {i}");
            foreach (var n in r.Notes) sb.AppendLine($"   note: {n}");
        }
        return sb.ToString();
    }

    private static string Write(string folder, string name, string content)
    {
        string target = Path.Combine(folder, name);
        try { File.WriteAllText(target, content); return target; }
        catch
        {
            target = Path.Combine(Path.GetTempPath(), name);
            File.WriteAllText(target, content);
            return target;
        }
    }

    // ------------------------------------------------------------------ small helpers

    private static IEnumerable<XYZ> Corners(BoundingBoxXYZ bb)
    {
        foreach (var px in new[] { bb.Min.X, bb.Max.X })
            foreach (var py in new[] { bb.Min.Y, bb.Max.Y })
                foreach (var pz in new[] { bb.Min.Z, bb.Max.Z })
                    yield return new XYZ(px, py, pz);
    }

    private static string WayName(Way w) => w switch
    {
        Way.LevelPoint => "on the level",
        Way.LevelPlane => "on the level's work plane",
        Way.WallHosted => "hosted in a wall",
        _ => "on a wall face",
    };

    private static double R2(double v) => Math.Round(v, 2);
    private static string In(double feet) => $"{feet * 12:0.##}\"";
    private static string Dir(XYZ v) => $"({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###})";
    private static string YesNo(bool b) => b ? "yes" : "no";
    private static string Short(string? s) => s is null ? "" : s.Length <= 160 ? s : s[..160] + "...";

    private sealed class OverwriteOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = true; return true; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }

    private sealed class FailureCapture : IFailuresPreprocessor
    {
        public List<string> Errors { get; } = new();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            bool error = false;
            foreach (var f in a.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning) { a.DeleteWarning(f); continue; }
                Errors.Add(f.GetDescriptionText());
                error = true;
            }
            return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}

public sealed class FamilyResult
{
    public string File { get; set; } = "";
    public double SizeMb { get; set; }
    public string Category { get; set; } = "";
    public string? SuggestedCategory { get; set; }
    public string PlacementType { get; set; } = "";
    public bool WorkPlaneBased { get; set; }
    public bool AlwaysVertical { get; set; }
    public bool Shared { get; set; }
    public string? Host { get; set; }
    public List<string> Types { get; set; } = new();
    public int ImportedCad { get; set; }
    public List<string> CadExtents { get; set; } = new();
    public int NativeSolids { get; set; }
    public int NestedFamilies { get; set; }
    public List<string> ReferencePlanes { get; set; } = new();
    public List<string> LengthParameters { get; set; } = new();
    public List<string> Placements { get; set; } = new();
    public string? Primary { get; set; }
    public string? PrimaryWay { get; set; }
    public bool Hosted { get; set; }
    public double[]? SizeIn { get; set; }
    public double BottomAboveLevelIn { get; set; }
    public string? InsertionPoint { get; set; }
    public string? Facing { get; set; }
    public string? Hand { get; set; }
    public bool CanFlipFacing { get; set; }
    public bool CanFlipHand { get; set; }
    public string? HeightBy { get; set; }
    public bool? MirroredFlag { get; set; }
    public List<TestCheck> Checks { get; set; } = new();
    public List<string> Issues { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public string Verdict { get; set; } = "";
    private bool _reject, _fixable;

    public void Check(string name, bool ok, string detail) => Checks.Add(new TestCheck(name, ok, detail));
    public void Fix(string issue) { Issues.Add(issue); _fixable = true; }
    public void Reject(string why) { Issues.Insert(0, why); _reject = true; }

    public void Grade()
    {
        if (Checks.Any(c => !c.Ok)) _reject = true;
        Verdict = _reject ? "REJECT" : _fixable ? "FIXABLE" : "PASS";
    }
}

public sealed record TestCheck(string Name, bool Ok, string Detail);
