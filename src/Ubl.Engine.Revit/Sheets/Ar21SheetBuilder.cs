using Autodesk.Revit.DB;
using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Core.Sheets;
using Ubl.Engine.Revit.Build;
using Ubl.Engine.Revit.Config;
using ShellModel = Ubl.Engine.Core.Shell.ShellModel;
using PlanLineStyle = Ubl.Engine.Core.Sheets.LineStyle;

namespace Ubl.Engine.Revit.Sheets;

/// <summary>
/// Creates the AR-21 floor plan view (scale, crop, Existing phase, template) and the A3 sheet with its title block
/// fields and viewport. Draft annotation is optional; when on, dimensions are real Revit dimensions on column and
/// wall faces, so they stay correct if the model is edited.
/// </summary>
internal sealed class Ar21SheetBuilder(Document doc, EngineConfig cfg, ModelFrame frame, List<string> log)
{
    public (ViewPlan View, ViewSheet Sheet) Build(ShellModel shell, Ar21Plan plan, BuiltModel model, ProjectInputs inputs, bool annotate)
    {
        var types = new TypeFactory(doc, cfg, log);
        double z = model.FloorLevel.ProjectElevation;

        // ---- the view
        var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                      .First(v => v.ViewFamily == ViewFamily.FloorPlan);
        var view = ViewPlan.Create(doc, vft.Id, model.FloorLevel.Id);
        view.Name = cfg.ViewName;
        if (cfg.ViewTemplate is not null)
        {
            var template = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                               .FirstOrDefault(v => v.IsTemplate && v.Name == cfg.ViewTemplate);
            if (template is not null) view.ViewTemplateId = template.Id;
            else log.Add($"view template '{cfg.ViewTemplate}' not found: view uses project defaults");
        }
        Try("view scale", () => view.Scale = plan.Scale.Denominator);
        Try("view phase", () =>
        {
            var phase = doc.Phases.Cast<Phase>().FirstOrDefault(p => p.Name == cfg.ExistingPhase);
            if (phase is not null) view.get_Parameter(BuiltInParameter.VIEW_PHASE).Set(phase.Id);
        });
        Try("crop", () =>
        {
            var c = plan.Crop;
            var box = new BoundingBoxXYZ { Min = frame.P(c.X0, c.Y0, z - 10), Max = frame.P(c.X1, c.Y1, z + 10) };
            view.CropBox = box;
            view.CropBoxActive = true;
            view.CropBoxVisible = false;
            view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.Set(0);
        });

        // ---- draft annotation (optional): dimensions, labels, tags, 2D stairs, site band.
        //      Off by default: the firm's draftsperson annotates and checks, the engine does the model, view and sheet.
        if (annotate)
        {
            // ---- dimensions on real faces
            foreach (var dp in plan.Dimensions)
            {
                var refs = new ReferenceArray();
                bool complete = true;
                foreach (var a in dp.Anchors)
                {
                    var r = Resolve(a, model);
                    if (r is null) { complete = false; break; }
                    refs.Append(r);
                }
                if (!complete || refs.Size < 2)
                {
                    log.Add($"dimension '{dp.Name}' skipped: a face reference was not available (check the family has Left/Right/Front/Back references)");
                    continue;
                }
                double lo = dp.Anchors.Min(a => a.Coord), hi = dp.Anchors.Max(a => a.Coord);
                var line = dp.Axis == 'y'
                    ? Line.CreateBound(frame.P(dp.LineCoord, lo, z), frame.P(dp.LineCoord, hi, z))
                    : Line.CreateBound(frame.P(lo, dp.LineCoord, z), frame.P(hi, dp.LineCoord, z));
                Try($"dimension '{dp.Name}'", () => doc.Create.NewDimension(view, line, refs));
            }

            // ---- text
            foreach (var t in plan.Texts)
            {
                var tt = t.Role switch
                {
                    TextRole.BeamLabel => types.Text("UBL 1.35mm MASK", 1.35, opaque: true),
                    TextRole.CoreLabel => types.Text("UBL 1.8mm MASK", 1.8, opaque: true),
                    TextRole.ColumnTag or TextRole.StairLabel => types.Text("UBL 1.4mm", 1.4, opaque: false),
                    TextRole.LevelNote => types.Text("UBL 1.8mm", 1.8, opaque: false),
                    TextRole.Note => types.Text("UBL 1.5mm", 1.5, opaque: false),
                    TextRole.AreaNote => types.Text("UBL 2.0mm", 2.0, opaque: false),
                    TextRole.Placeholder => types.Text("UBL 2.2mm INPUT REQUIRED", 2.2, opaque: false, color: 255),
                    _ => types.Text("UBL 2.6mm", 2.6, opaque: false),
                };
                var opts = new TextNoteOptions(tt.Id)
                {
                    HorizontalAlignment = t.Align switch { "left" => HorizontalTextAlignment.Left, "right" => HorizontalTextAlignment.Right, _ => HorizontalTextAlignment.Center },
                    VerticalAlignment = VerticalTextAlignment.Middle,
                    Rotation = t.RotationDeg * Math.PI / 180,
                };
                Try($"text '{t.Text}'", () => TextNote.Create(doc, view.Id, frame.P(t.At, z), t.Text.Replace("\n", "\r"), opts));
            }

            // ---- detail lines: tag leaders, lift cross, stairs, site band
            var styles = LineStyles();
            foreach (var l in plan.Lines)
            {
                if (Pt.Dist(l.A, l.B) < 0.05) continue;
                Try("detail line", () =>
                {
                    var dc = doc.Create.NewDetailCurve(view, Line.CreateBound(frame.P(l.A, z), frame.P(l.B, z)));
                    if (styles.TryGetValue(l.Style, out var gs)) dc.LineStyle = gs;
                });
            }
            foreach (var c in plan.Circles)
                Try("detail circle", () =>
                {
                    var center = frame.P(c.Center, z);
                    double r = c.Radius / 12.0;
                    foreach (var (a0, a1) in new[] { (0.0, Math.PI), (Math.PI, 2 * Math.PI) })
                    {
                        var dc = doc.Create.NewDetailCurve(view, Arc.Create(center, r, a0, a1, XYZ.BasisX, XYZ.BasisY));
                        if (styles.TryGetValue(c.Style, out var gs)) dc.LineStyle = gs;
                    }
                });
        }
        else log.Add("draft annotation not requested: view and sheet left for the draftsperson to annotate");

        // ---- the sheet
        var sheet = ViewSheet.Create(doc, TitleBlock(types));
        sheet.SheetNumber = cfg.SheetNumber;
        sheet.Name = cfg.SheetName;
        Set(sheet, BuiltInParameter.SHEET_DRAWN_BY, inputs.DrawnBy);
        if (inputs.Date is not null) Set(sheet, BuiltInParameter.SHEET_ISSUE_DATE, inputs.Date);
        // checked and approved are left for the people who check and approve
        var info = doc.ProjectInformation;
        Set(info, BuiltInParameter.CLIENT_NAME, inputs.Client);
        Set(info, BuiltInParameter.PROJECT_STATUS, inputs.Status);
        if (inputs.BranchName is not null) Set(info, BuiltInParameter.PROJECT_NAME, inputs.BranchName);
        else log.Add("branch name missing: project name left unchanged in the title block");
        if (inputs.Address is not null) Set(info, BuiltInParameter.PROJECT_ADDRESS, inputs.Address);
        if (inputs.ProjectNumber is not null) Set(info, BuiltInParameter.PROJECT_NUMBER, inputs.ProjectNumber);

        // viewport centred on the drawing area (title block origin at the paper's bottom-left corner)
        var (cxMm, cyMm) = plan.Sheet.DrawCenter;
        var centre = new XYZ(cxMm / 304.8, cyMm / 304.8, 0);
        if (Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
        {
            var vp = Viewport.Create(doc, sheet.Id, view.Id, centre);
            vp.SetBoxCenter(centre);
        }
        else log.Add("view could not be placed on the sheet (already on another sheet?)");

        return (view, sheet);
    }

    private Reference? Resolve(Anchor a, BuiltModel model)
    {
        switch (a.Kind)
        {
            case AnchorKind.Column:
                if (a.Index < 0 || a.Index >= model.Columns.Count) return null;
                var type = a.Side switch
                {
                    AnchorSide.Left => FamilyInstanceReferenceType.Left,
                    AnchorSide.Right => FamilyInstanceReferenceType.Right,
                    AnchorSide.Front => FamilyInstanceReferenceType.Front,
                    _ => FamilyInstanceReferenceType.Back,
                };
                return model.Columns[a.Index].GetReferences(type)?.FirstOrDefault();   // null when the family has no such reference
            case AnchorKind.PlotRear:
                var wall = model.RearYardWall;
                if (wall is null) return null;
                // the outer face is the side face furthest towards +Y
                var faces = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior)
                    .Concat(HostObjectUtils.GetSideFaces(wall, ShellLayerType.Interior));
                return faces
                    .Select(r => (r, f: wall.GetGeometryObjectFromReference(r) as PlanarFace))
                    .Where(x => x.f is not null)
                    .OrderByDescending(x => x.f!.Origin.Y)
                    .Select(x => x.r)
                    .FirstOrDefault();
            default:
                return null;
        }
    }

    private Dictionary<PlanLineStyle, GraphicsStyle> LineStyles()
    {
        var map = new Dictionary<PlanLineStyle, GraphicsStyle>();
        var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        foreach (Category sub in lines.SubCategories)
        {
            var gs = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
            if (gs is null) continue;
            if (sub.Name == "Thin Lines") map[PlanLineStyle.Thin] = gs;
            else if (sub.Name == "Medium Lines") map[PlanLineStyle.Medium] = gs;
            else if (sub.Name == "Hidden Lines") map[PlanLineStyle.Dashed] = gs;
        }
        return map;
    }

    /// <summary>
    /// The configured title block; else any loaded one; else an A3 title block from the Revit content library;
    /// else none (the sheet is still created, and the report says so).
    /// </summary>
    private ElementId TitleBlock(TypeFactory types)
    {
        List<FamilySymbol> Loaded() => new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsElementType().Cast<FamilySymbol>().ToList();
        var all = Loaded();
        var tb = all.FirstOrDefault(s => s.FamilyName == cfg.TitleBlockFamily);
        if (tb is not null) return tb.Id;
        if (all.Count == 0)
        {
            var file = types.FindInLibrary("A3 metric.rfa") ?? types.FindInLibrary("A3*.rfa");
            if (file is not null && doc.LoadFamily(file, out Family _))
            {
                log.Add($"no title block in the project: loaded {Path.GetFileName(file)} from the content library");
                all = Loaded();
            }
        }
        tb = all.FirstOrDefault();
        if (tb is null)
        {
            log.Add("no title block available: sheet created without one. Load the firm's A3 tender title block.");
            return ElementId.InvalidElementId;
        }
        log.Add($"title block '{cfg.TitleBlockFamily}' not found: used '{tb.FamilyName} : {tb.Name}'");
        return tb.Id;
    }

    private void Set(Element e, BuiltInParameter bip, string value)
    {
        var p = e.get_Parameter(bip);
        if (p is not null && !p.IsReadOnly) p.Set(value);
    }

    private void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { log.Add($"{what}: {ex.Message}"); }
    }
}
