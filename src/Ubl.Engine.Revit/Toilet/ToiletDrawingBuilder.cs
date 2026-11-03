using Autodesk.Revit.DB;
using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Core.Sheets;
using Ubl.Engine.Core.Toilet;
using Ubl.Engine.Revit.Build;
using Ubl.Engine.Revit.Config;

namespace Ubl.Engine.Revit.Toilet;

/// <summary>The sheets made for one toilet, in order (AR-141 to AR-148), with the view on each.</summary>
internal sealed record ToiletSheets(List<(ViewSheet Sheet, View View, DrawingView Plan)> Items, bool PlaceholderTitleBlock);

/// <summary>
/// Turns the drawing planner's output into Revit: the flooring and sanitary plans and the four sectional
/// elevations, every tile line, note, tag and dimension, and the eight A3 sheets. Each item is created on its own
/// and a failure is written to the log, so one refused note never costs the whole set. Dimensions to walls use
/// the walls' own faces (they stay right if a wall moves); fixture centre-lines and tile courses use invisible
/// lines, because the client's families have no reliable centre references.
/// </summary>
internal sealed class ToiletDrawingBuilder(Document doc, Level level, RoomFrame frame, ToiletDrawingSet set, IReadOnlyDictionary<Side, Wall> walls,
                                           EngineConfig engineCfg, List<string> log, Func<string, Action, bool> step)
{
    private readonly TypeFactory _types = new(doc, engineCfg, log);
    private readonly Dictionary<string, GraphicsStyle> _styles = new();
    private readonly Dictionary<FillKind, ElementId?> _fills = new();
    private ToiletSpec Spec => set.Plan.Spec;

    public ToiletSheets Build(ToiletViews views, IReadOnlyCollection<ElementId> fixtures, IReadOnlyCollection<ElementId> ceilingFixtures,
                              IReadOnlyCollection<ElementId> doors, bool ceilingSymbols, int variant)
    {
        LoadStyles();
        var made = new Dictionary<string, View>();
        var vfts = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().ToList();
        var floorPlanType = vfts.First(v => v.ViewFamily == ViewFamily.FloorPlan);
        var sectionType = vfts.FirstOrDefault(v => v.ViewFamily == ViewFamily.Section);

        // each view and each sheet is its own transaction: anything Revit refuses undoes that one drawing, not the set
        foreach (var dv in set.Views)
        {
            View? view = null;
            bool existing = dv.Key is "layout" or "ceiling";
            bool ok = step($"Toilet drawing: {dv.Name}", () =>
            {
                view = dv.Key switch
                {
                    "layout" => views.Plan,
                    "ceiling" => views.Ceiling,
                    "flooring" or "sanitary" => NewPlan(floorPlanType, dv.Name),
                    _ => sectionType is null ? throw new InvalidOperationException("the project has no section view type") : NewSection(sectionType, dv),
                };
                Prepare(view, dv);
                if (view is ViewPlan vp) CropPlan(vp, dv);
                if (dv.Kind is DrawingKind.Flooring) Hide(view, fixtures.Concat(ceilingFixtures).Concat(doors));
                if (dv.Kind is DrawingKind.Ceiling) { Hide(view, fixtures.Concat(doors)); CeilingRange((ViewPlan)view); }
                if (dv.Kind is DrawingKind.Layout or DrawingKind.Sanitary) Hide(view, ceilingFixtures);
                doc.Regenerate();   // new views and crops must exist before dimensions can reference anything in them
                Annotate(view, dv, ceilingSymbols);
            });
            if (view is not null && (ok || existing)) made[dv.Key] = view;
        }

        // ---- sheets
        ElementId? tb = null;
        step("Toilet title block", () => tb = TitleBlock());
        var result = new List<(ViewSheet, View, DrawingView)>();
        foreach (var s in set.Sheets)
        {
            var dv = set.View(s.ViewKey);
            if (!made.TryGetValue(s.ViewKey, out var view)) { log.Add($"sheet {s.Number}: its view was not made, sheet skipped"); continue; }
            ViewSheet? sheet = null;
            bool ok = step($"Toilet sheet {s.Number}", () =>
            {
                sheet = ViewSheet.Create(doc, tb ?? ElementId.InvalidElementId);
                sheet.SheetNumber = s.Number;
                sheet.Name = s.Name;
                if (tb is null) Placeholder(sheet, s, dv);
                var (cx, cy) = set.Sheet.DrawCenter;
                var centre = new XYZ(cx / 304.8, cy / 304.8, 0);
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id)) { log.Add($"sheet {s.Number}: view {view.Name} is already on another sheet"); return; }
                var port = Viewport.Create(doc, sheet.Id, view.Id, centre);
                port.SetBoxCenter(centre);
                if (NoTitleViewport() is { } nt) port.ChangeTypeId(nt);
                port.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.Set(dv.Letter ?? "1");
            });
            if (ok && sheet is not null) result.Add((sheet, view, dv));
        }
        if (tb is null) log.Add("no A3 title block loaded: sheets carry a placeholder frame in the title block's place. Load the firm's title block " +
                                $"as '{engineCfg.TitleBlockFamily}' (or set title_block_family in engine.config.json) and re-run.");
        return new ToiletSheets(result, tb is null);
    }

    // ================================================================== views

    private ViewPlan NewPlan(ViewFamilyType type, string name)
    {
        var v = ViewPlan.Create(doc, type.Id, level.Id);
        v.Name = name;
        return v;
    }

    private ViewSection NewSection(ViewFamilyType type, DrawingView dv)
    {
        var f = dv.Elevation!;
        var wl = RoomGeometry.Wall(Spec, f.Wall);
        var t = Transform.Identity;
        t.Origin = frame.P(wl.At(f.Length, f.CutDepth));                // u = 0 at the wall's left end, on the cut, at the floor
        t.BasisX = frame.V(wl.Dir * -1);
        t.BasisY = XYZ.BasisZ;
        t.BasisZ = frame.V(wl.Normal);                                   // towards the viewer, who stands in the room
        double behind = (f.CutDepth + Spec.WallThickness(f.Wall) + 3) / 12;
        var e = dv.Extent;
        var box = new BoundingBoxXYZ { Transform = t, Min = new XYZ(e.X0 / 12, e.Y0 / 12, -behind), Max = new XYZ(e.X1 / 12, e.Y1 / 12, 0) };
        var v = ViewSection.CreateSection(doc, type.Id, box);
        v.Name = dv.Name;
        if (!v.ViewDirection.IsAlmostEqualTo(t.BasisZ, 1e-6) || !v.RightDirection.IsAlmostEqualTo(t.BasisX, 1e-6))
            log.Add($"{dv.Name}: Revit oriented the section differently than asked (right {v.RightDirection}, towards viewer {v.ViewDirection})");
        v.CropBoxActive = true;
        v.CropBoxVisible = false;
        return v;
    }

    private void Prepare(View v, DrawingView dv)
    {
        if (v.ViewTemplateId != ElementId.InvalidElementId) v.ViewTemplateId = ElementId.InvalidElementId;   // the engine sets scale and visibility itself
        v.Scale = dv.Scale.Denominator;
        v.DetailLevel = ViewDetailLevel.Fine;
        v.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.Set(0);
        foreach (var cat in new[] { BuiltInCategory.OST_Levels, BuiltInCategory.OST_Grids, BuiltInCategory.OST_Elev, BuiltInCategory.OST_Sections, BuiltInCategory.OST_Callouts })
        {
            var id = new ElementId(cat);
            try { if (v.CanCategoryBeHidden(id)) v.SetCategoryHidden(id, true); }
            catch (Exception) { }
        }
    }

    private void CropPlan(ViewPlan v, DrawingView dv)
    {
        var c = dv.Extent.Corners().Select(p => frame.P(p)).ToList();   // all four: the room may be turned
        v.CropBox = new BoundingBoxXYZ
        {
            Min = new XYZ(c.Min(p => p.X), c.Min(p => p.Y), frame.Ffl - 10),
            Max = new XYZ(c.Max(p => p.X), c.Max(p => p.Y), frame.Ffl + 10),
        };
        v.CropBoxActive = true;
        v.CropBoxVisible = false;
    }

    /// <summary>A ceiling plan cut just under a low toilet ceiling, so the ceiling and what is on it show.</summary>
    private void CeilingRange(ViewPlan v)
    {
        try
        {
            var r = v.GetViewRange();
            r.SetLevelId(PlanViewPlane.CutPlane, level.Id);
            r.SetOffset(PlanViewPlane.CutPlane, (Spec.CeilingHeight - 9) / 12);
            r.SetLevelId(PlanViewPlane.TopClipPlane, level.Id);
            r.SetOffset(PlanViewPlane.TopClipPlane, Spec.SlabHeight / 12);
            r.SetLevelId(PlanViewPlane.ViewDepthPlane, level.Id);
            r.SetOffset(PlanViewPlane.ViewDepthPlane, Spec.SlabHeight / 12);
            v.SetViewRange(r);
        }
        catch (Exception ex) { log.Add($"ceiling plan view range not set ({ex.Message}): check the ceiling shows"); }
    }

    private void Hide(View v, IEnumerable<ElementId> ids)
    {
        var list = ids.Where(id => id != ElementId.InvalidElementId && doc.GetElement(id) is { } e && e.CanBeHidden(v)).Distinct().ToList();
        if (list.Count > 0) v.HideElements(list);
    }

    // ================================================================== annotation

    private void Annotate(View view, DrawingView dv, bool ceilingSymbols)
    {
        Func<Pt, XYZ> P = dv.Elevation is { } f ? ElevationPoint(f) : (p => frame.P(p));
        var r = set.Rules;

        foreach (var l in dv.Lines) Try("line", () => Line2D(view, P(l.A), P(l.B), l.Style));
        foreach (var c in dv.Circles) Try("circle", () => Circle(view, P(c.Center), c.Radius / 12, c.Style));
        if (ceilingSymbols)
        {
            foreach (var l in dv.SymbolLines) Try("ceiling symbol", () => Line2D(view, P(l.A), P(l.B), l.Style));
            foreach (var c in dv.SymbolCircles) Try("ceiling symbol", () => Circle(view, P(c.Center), c.Radius / 12, c.Style));
        }
        foreach (var fill in dv.Fills)
            Try($"filled region ({fill.Kind})", () =>
            {
                var type = FillType(fill.Kind);
                var pts = fill.Loop.Select(P).ToList();
                if (type is null) { for (int i = 0; i < pts.Count; i++) Line2D(view, pts[i], pts[(i + 1) % pts.Count], DLineStyle.Medium); return; }
                var loop = new CurveLoop();
                for (int i = 0; i < pts.Count; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
                FilledRegion.Create(doc, type, view.Id, new List<CurveLoop> { loop });
            });
        foreach (var t in dv.Texts)
            Try($"text '{t.Text.Split('\n')[0]}'", () =>
            {
                var note = Note(view, P(t.At), t.Text, t.HeightMm, t.Align);
                if (t.Underline)
                {
                    var ft = note.GetFormattedText();
                    ft.SetUnderlineStatus(true);
                    note.SetFormattedText(ft);
                }
                if (t.Ellipse)
                {
                    double w = (t.Text.Length * t.HeightMm * ToiletDrawingPlanner.CharWidth / 2 + 3) * dv.Scale.Denominator / 304.8;
                    double h = t.HeightMm * 1.1 * dv.Scale.Denominator / 304.8;
                    var c = P(t.At);
                    foreach (var (a0, a1) in new[] { (0.0, Math.PI), (Math.PI, 2 * Math.PI) })
                    {
                        var arc = Ellipse.CreateCurve(c, w, h, view.RightDirection, view.UpDirection, a0, a1);
                        doc.Create.NewDetailCurve(view, arc).LineStyle = Style("Thin Lines") ?? Style("Medium Lines");
                    }
                }
            });
        foreach (var l in dv.Labels)
            Try($"note '{l.Text.Split('\n')[0]}'", () =>
            {
                var note = Note(view, P(l.TextAt), l.Text, r.LabelMm, l.Left ? "right" : "left", leaderArrow: true);
                var leader = note.AddLeader(l.Left ? TextNoteLeaderTypes.TNLT_STRAIGHT_R : TextNoteLeaderTypes.TNLT_STRAIGHT_L);
                leader.End = P(l.Target);
                leader.Elbow = P(l.Elbow);
            });
        foreach (var d in dv.Dims) Try($"dimension '{d.Name}'", () => Dimension(view, dv, d, P));
    }

    /// <summary>A point of an elevation (u along the wall from its left end, v above the floor) on the section's cut plane.</summary>
    private Func<Pt, XYZ> ElevationPoint(ElevationFrame f)
    {
        var wl = RoomGeometry.Wall(Spec, f.Wall);
        return p => frame.P(wl.At(f.Length - p.X, f.CutDepth), p.Y);
    }

    private DetailCurve Line2D(View view, XYZ a, XYZ b, DLineStyle style)
    {
        var dc = doc.Create.NewDetailCurve(view, Line.CreateBound(a, b));
        var gs = style switch
        {
            DLineStyle.Medium => Style("Medium Lines"),
            DLineStyle.Hidden => Style("Hidden Lines"),
            DLineStyle.Invisible => Style("<Invisible lines>"),
            _ => Style("Thin Lines"),
        };
        if (gs is not null) dc.LineStyle = gs;
        return dc;
    }

    private void Circle(View view, XYZ c, double r, DLineStyle style)
    {
        foreach (var (a0, a1) in new[] { (0.0, Math.PI), (Math.PI, 2 * Math.PI) })
        {
            var dc = doc.Create.NewDetailCurve(view, Arc.Create(c, r, a0, a1, view.RightDirection, view.UpDirection));
            if ((style == DLineStyle.Hidden ? Style("Hidden Lines") : Style("Thin Lines")) is { } gs) dc.LineStyle = gs;
        }
    }

    private TextNote Note(View view, XYZ at, string text, double heightMm, string align, bool leaderArrow = false)
    {
        var tt = _types.Text($"UBL {heightMm:0.0#}mm{(leaderArrow ? " LEADER" : "")}", heightMm, opaque: false);
        if (leaderArrow) SetArrow(tt);
        var opts = new TextNoteOptions(tt.Id)
        {
            HorizontalAlignment = align switch { "left" => HorizontalTextAlignment.Left, "right" => HorizontalTextAlignment.Right, _ => HorizontalTextAlignment.Center },
            VerticalAlignment = VerticalTextAlignment.Middle,
        };
        return TextNote.Create(doc, view.Id, at, text.Replace("\n", "\r"), opts);
    }

    private bool _arrowSet;
    private void SetArrow(TextNoteType tt)
    {
        if (_arrowSet) return;
        _arrowSet = true;
        try
        {
            var arrows = new FilteredElementCollector(doc).OfClass(typeof(ElementType)).Cast<ElementType>()
                .Where(t => t.FamilyName.Contains("Arrowhead", StringComparison.OrdinalIgnoreCase)).ToList();
            var pick = arrows.FirstOrDefault(a => a.Name.Contains("Arrow", StringComparison.OrdinalIgnoreCase) && a.Name.Contains("Filled", StringComparison.OrdinalIgnoreCase))
                       ?? arrows.FirstOrDefault(a => a.Name.Contains("Arrow", StringComparison.OrdinalIgnoreCase));
            if (pick is not null) tt.get_Parameter(BuiltInParameter.LEADER_ARROWHEAD)?.Set(pick.Id);
        }
        catch (Exception ex) { log.Add($"leader arrowhead not set: {ex.Message}"); }
    }

    // ---- dimensions

    private void Dimension(View view, DrawingView dv, DDim d, Func<Pt, XYZ> P)
    {
        Pt At(double along, double across) => d.Horizontal ? new Pt(along, across) : new Pt(across, along);
        double lo = d.Anchors.Min(a => a.At), hi = d.Anchors.Max(a => a.At);
        var line = Line.CreateBound(P(At(lo, d.LineAt)), P(At(hi, d.LineAt)));
        var expected = new List<double>();
        for (int i = 0; i + 1 < d.Anchors.Count; i++) expected.Add(d.Anchors[i + 1].At - d.Anchors[i].At);

        Dimension? Make(bool useFaces)
        {
            var refs = new ReferenceArray();
            foreach (var a in d.Anchors)
            {
                Reference? r = null;
                if (useFaces && a.Kind != DAnchorKind.Coord && a.Wall is Side s) r = WallFace(s, a.Kind == DAnchorKind.WallOuter, d.Horizontal, a.At);
                if (r is null)
                {
                    double from = a.From ?? d.LineAt - dv.Mm(1.5);
                    if (Math.Abs(from - d.LineAt) < 0.05) from = d.LineAt - dv.Mm(1.5);
                    var w = Line2D(view, P(At(a.At, from)), P(At(a.At, d.LineAt + Math.Sign(d.LineAt - from) * dv.Mm(1))), DLineStyle.Invisible);
                    r = w.GeometryCurve.Reference;
                }
                refs.Append(r);
            }
            return doc.Create.NewDimension(view, line, refs);
        }

        var dim = Make(useFaces: true);
        if (dim is not null && !Matches(dim, expected))
        {
            log.Add($"dimension '{d.Name}' in {dv.Name}: wall faces did not measure as planned, used plain witness lines instead");
            doc.Delete(dim.Id);
            dim = Make(useFaces: false);
        }
        if (dim is not null && !Matches(dim, expected)) log.Add($"dimension '{d.Name}' in {dv.Name}: CHECK, it does not read as planned ({string.Join(", ", expected.Select(x => $"{x:0.##}\""))})");
    }

    private static bool Matches(Dimension dim, List<double> expectedIn)
    {
        var got = new List<double>();
        if (dim.NumberOfSegments == 0) { if (dim.Value is double v) got.Add(v * 12); }
        else foreach (DimensionSegment s in dim.Segments) if (s.Value is double v) got.Add(v * 12);
        return got.Count == expectedIn.Count && got.Zip(expectedIn).All(x => Math.Abs(x.First - x.Second) < 1.0 / 32);
    }

    /// <summary>The side face of a wall at the given room coordinate (x for a horizontal dimension, y for a vertical one).</summary>
    private Reference? WallFace(Side side, bool outer, bool horizontal, double at)
    {
        if (!walls.TryGetValue(side, out var wall)) return null;
        var refs = HostObjectUtils.GetSideFaces(wall, ShellLayerType.Exterior).Concat(HostObjectUtils.GetSideFaces(wall, ShellLayerType.Interior));
        foreach (var r in refs)
        {
            if (wall.GetGeometryObjectFromReference(r) is not PlanarFace f) continue;
            var p = frame.ToRoom(f.Origin);
            double c = horizontal ? p.X : p.Y;
            double nx = f.FaceNormal.DotProduct(frame.Ex), ny = f.FaceNormal.DotProduct(frame.Ey);   // in the room's frame
            bool perpendicular = horizontal ? Math.Abs(nx) > 0.99 : Math.Abs(ny) > 0.99;
            if (perpendicular && Math.Abs(c - at) < 1.0 / 32) return r;
        }
        return null;
    }

    // ================================================================== styles, fills, title block

    private void LoadStyles()
    {
        var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        foreach (Category sub in lines.SubCategories)
            if (sub.GetGraphicsStyle(GraphicsStyleType.Projection) is { } gs) _styles[sub.Name] = gs;
    }

    private GraphicsStyle? Style(string name) => _styles.TryGetValue(name, out var gs) ? gs : null;

    /// <summary>Filled region types made once per project: grey threshold, hatched sample tile, black marker, concrete slab.</summary>
    private ElementId? FillType(FillKind kind)
    {
        if (_fills.TryGetValue(kind, out var cached)) return cached;
        ElementId? id = null;
        try
        {
            string name = kind switch
            {
                FillKind.Threshold => "UBL Threshold",
                FillKind.TileSample => "UBL Tile Sample",
                FillKind.Marker => "UBL Marker",
                _ => "UBL Slab",
            };
            var all = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();
            var t = all.FirstOrDefault(x => x.Name == name);
            if (t is null && all.Count > 0)
            {
                t = (FilledRegionType)all[0].Duplicate(name);
                var patterns = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                    .Where(p => p.GetFillPattern().Target == FillPatternTarget.Drafting).ToList();
                var solid = patterns.FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
                FillPatternElement? Named(params string[] keys) =>
                    keys.Select(k => patterns.FirstOrDefault(p => p.Name.Contains(k, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(p => p is not null);
                var (pattern, color) = kind switch
                {
                    FillKind.Threshold => (solid, new Color(110, 110, 110)),
                    FillKind.Marker => (solid, new Color(0, 0, 0)),
                    FillKind.TileSample => (Named("Diagonal up", "Diagonal down", "Diagonal") ?? solid, new Color(0, 0, 0)),
                    _ => (Named("Concrete", "Sand", "Diagonal crosshatch", "Crosshatch") ?? solid, new Color(80, 80, 80)),
                };
                if (pattern is not null) t.ForegroundPatternId = pattern.Id;
                t.ForegroundPatternColor = color;
                t.BackgroundPatternId = ElementId.InvalidElementId;
                if (kind is FillKind.TileSample or FillKind.Slab && pattern == solid) t.ForegroundPatternColor = new Color(200, 200, 200);
            }
            id = t?.Id;
        }
        catch (Exception ex) { log.Add($"filled region type for {kind} not made ({ex.Message}): outline drawn instead"); }
        _fills[kind] = id;
        return id;
    }

    /// <summary>The firm's A3 title block if it is in the project (or in the families folder); never a title block of another size.</summary>
    private ElementId? TitleBlock()
    {
        var all = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().Cast<FamilySymbol>().ToList();
        var name = engineCfg.TitleBlockFamily;
        var tb = name is null ? null : all.FirstOrDefault(s => string.Equals(s.FamilyName, name, StringComparison.OrdinalIgnoreCase));
        if (tb is null) return null;
        if (!tb.IsActive) { tb.Activate(); doc.Regenerate(); }
        log.Add($"title block: {tb.FamilyName} : {tb.Name}");
        return tb.Id;
    }

    /// <summary>The A3 frame and title strip drawn in the title block's place, with the sheet's own fields, until the firm's title block is loaded.</summary>
    private void Placeholder(ViewSheet sheet, DrawingSheet s, DrawingView dv)
    {
        var f = set.Sheet;
        XYZ M(double x, double y) => new(x / 304.8, y / 304.8, 0);
        void L(double x0, double y0, double x1, double y1, string style) =>
            Try("title block placeholder", () =>
            {
                var dc = doc.Create.NewDetailCurve(sheet, Line.CreateBound(M(x0, y0), M(x1, y1)));
                if (Style(style) is { } gs) dc.LineStyle = gs;
            });
        L(0, 0, f.Width, 0, "Thin Lines"); L(f.Width, 0, f.Width, f.Height, "Thin Lines"); L(f.Width, f.Height, 0, f.Height, "Thin Lines"); L(0, f.Height, 0, 0, "Thin Lines");
        L(f.InnerX0, f.InnerY0, f.InnerX1, f.InnerY0, "Wide Lines"); L(f.InnerX1, f.InnerY0, f.InnerX1, f.InnerY1, "Wide Lines");
        L(f.InnerX1, f.InnerY1, f.InnerX0, f.InnerY1, "Wide Lines"); L(f.InnerX0, f.InnerY1, f.InnerX0, f.InnerY0, "Wide Lines");
        L(f.InnerX0, f.TitleBlockTop, f.InnerX1, f.TitleBlockTop, "Medium Lines");
        L(f.InnerX0 + 145, f.InnerY0, f.InnerX0 + 145, f.TitleBlockTop, "Thin Lines");
        L(f.InnerX0 + 195, f.InnerY0, f.InnerX0 + 195, f.TitleBlockTop, "Thin Lines");
        string project = doc.ProjectInformation?.Name is { Length: > 0 } pn && pn != "Project Name" ? pn : "PROJECT NAME";
        void T(double x, double y, string text, double mm) =>
            Try("title block placeholder text", () =>
            {
                var tt = _types.Text($"UBL {mm:0.0#}mm", mm, opaque: false);
                TextNote.Create(doc, sheet.Id, M(x, y), text, new TextNoteOptions(tt.Id) { HorizontalAlignment = HorizontalTextAlignment.Left, VerticalAlignment = VerticalTextAlignment.Middle });
            });
        T(f.InnerX0 + 3, f.TitleBlockTop - 6, $"Title: {s.Name}", 2.6);
        T(f.InnerX0 + 3, f.TitleBlockTop - 13, "Client: UNITED BANK LIMITED", 2.4);
        T(f.InnerX0 + 3, f.TitleBlockTop - 20, $"Project: {project}", 2.4);
        T(f.InnerX0 + 148, f.TitleBlockTop - 6, "Drg No.", 2.0);
        T(f.InnerX0 + 148, f.TitleBlockTop - 14, s.Number, 4.0);
        T(f.InnerX0 + 148, f.TitleBlockTop - 23, "TENDER DRAWING", 2.2);
        T(f.InnerX0 + 198, f.TitleBlockTop - 6, $"Scale: {dv.Scale.Name}", 2.2);
        T(f.InnerX0 + 198, f.TitleBlockTop - 14, "DRAWN / CHECKED / APPROVED", 2.0);
        T(f.InnerX0 + 3, f.InnerY0 + 3, "TITLE BLOCK PLACEHOLDER: load the firm's A3 title block and re-run", 1.8);
    }

    private ElementId? _noTitle;
    private bool _noTitleLooked;
    private ElementId? NoTitleViewport()
    {
        if (_noTitleLooked) return _noTitle;
        _noTitleLooked = true;
        _noTitle = new FilteredElementCollector(doc).OfClass(typeof(ElementType)).Cast<ElementType>()
            .FirstOrDefault(t => t.FamilyName == "Viewport" && t.Name.Contains("No Title", StringComparison.OrdinalIgnoreCase))?.Id;
        return _noTitle;
    }

    private void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { log.Add($"drawings: {what}: {ex.Message}"); }
    }
}
