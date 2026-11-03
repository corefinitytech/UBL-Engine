# UBL Engine: shell module (page 7, AR-21) and toilet module

Reads the client's branch DWG, recognises the existing building shell, builds it in Revit (Existing phase) and
sets up the **AR-21 As Built Layout Plan (Ground Floor)** view and A3 sheet. The firm's draftsperson annotates,
checks and exports the PDF from Revit. One click, one undo.

## Projects

| Project | Target | What it does |
|---|---|---|
| `Ubl.Engine.Core` | net8.0, no packages | Recognition rules, ShellModel, checks, scale picker, AR-21 layout planner; toilet planner, rule checker and placement maths. No Revit, no CAD library. |
| `Ubl.Engine.Dwg` | net8.0 + ACadSharp (MIT) | Reads DWG (AC1014 to AC1032) or DXF into the reader-neutral `CadDrawing`. Explodes blocks. |
| `Ubl.Engine.Cli` | net8.0 exe | `ubl-engine shell drawing.dwg out.json` (shell recogniser), `ubl-engine toilet 60 75 --svg plan.svg` (one toilet), `ubl-engine toilet-sweep` (85,264 rooms). |
| `Ubl.Engine.Revit` | net8.0-windows (Revit 2025/2026), net10.0-windows (2027) | Ribbon tab **UBL Engine**: **Check Setup** (read-only), **AR-21 As-Built Shell**, **Test Families** (checks a folder of families before the engine places them), and **Build Toilet**. |
| `Ubl.Engine.Core.Tests` | xUnit | Units, text decoding, north detection, geometry, the recogniser, a 1,715-case size sweep, a golden test on the real drawing; toilet golden cases, rules, a 2,704-room toilet sweep and 800 random placement solves. |
| `reference/` | Python | The spike that produced the preview PDF. Same rules; use it to compare outputs. |

## Build and test

Quickest: `powershell -ExecutionPolicy Bypass -File verify.ps1 -Dwg "C:\path\new_block.dwg"` (see TESTING.md).

```
dotnet test tests/Ubl.Engine.Core.Tests                          # all Revit-free logic
set UBL_NEWBRANCH_DWG=C:\path\new_block.dwg && dotnet test       # adds the golden test on the real drawing
dotnet run --project src/Ubl.Engine.Cli -- shell C:\path\new_block.dwg shell.json
dotnet build src/Ubl.Engine.Revit -c Debug -p:RevitVersion=2026  # deploys to %AppData%\Autodesk\Revit\Addins\2026
```

**Verification status (31 Oct).** All projects compile with 0 errors and 0 warnings against .NET 8, ACadSharp
3.8.30 and the Revit 2026 API, and against .NET 10 and the Revit 2027 API. All 59 tests pass. The shell module
has run on your laptop (verify.ps1). Not yet exercised: anything inside Revit (AR-21, Test Families, Build Toilet).
`TESTING.md` is the step-by-step check, with the output to expect.

## Toilet module (the prototype room)

One input (a size and a door, or a `toilet.json`) gives a complete toilet in Revit: 4 1/2" brick walls, the door
(swing and hinge checked by reading the door's own plan arc), a tiled floor finish, the ceiling, a Revit room,
the vanity counter when there is one, every fixture placed, and a plan, ceiling plan and 3D view to check it.

**How the layout is decided (Core, no Revit).** `ToiletPlanner` tries the WC in every corner of every wall and
the basin in every corner (56 arrangements), places the accessories by their rules, rejects any arrangement that
breaks a hard rule (`ToiletChecker`: overlaps, clear space in front of the WC and basin, the door's swing and
entry, the door opening, wall items over the WC, ceiling spacing, the 1'-6" centre-line rules), scores the rest
(one wet wall, WC on a wet wall, WC away from the door, basin near it) and returns the best, with a log of why
each item sits where it does. If nothing passes it refuses, naming the rule and the size that would pass
("needs 4\" more depth"). The architect can pin the WC or basin to a corner (`pin_wc`) and the rest is planned
round it. Every rule value is in `ToiletRules`, marked W (Manawala), D (new-branch DWG), M (manual) or E (our
default, to confirm with the client); mounting heights and scoring weights can be overridden in
`toilet.families.json` without a rebuild.

**How a family is placed (Revit).** Nothing about a family is typed in by hand except which side is its front
(`toilet.families.json`, default `-Y`, the Revit convention; the Test Families review view shows it). Each family
is measured from its own geometry in Revit, so the engine knows exactly where its back-centre is relative to its
insertion point, however the family was drawn. `PlacementMath` (Core, tested on 800 random families) computes
the rotation and insertion point that put the back-centre on the planned spot with the front facing the room.
After placing, the add-in reads the instance's transform back from Revit and compares it with the plan; anything
off by more than 1/16" or 0.1 degree is corrected and measured again, and if it still does not land it is removed
and reported. This replaces the earlier approach of a hand-written JSON of default direction and size per family.

**Proof.** Golden cases: the Manawala male and female toilets reproduce AR-141 (WC and basin on one wet wall,
centre-lines 1'-6" from the walls, 3'-3" apart, mirror over the basin); the two new-branch toilets (7'-11" x 5'-0",
vanity) come out as described, given the wet wall. The full sweep (`ubl-engine toilet-sweep`): every inside size
from 4'-0" to 10'-0" in 1" steps, door on each wall, at either end, swinging in or out, 85,264 rooms: every one
is a toilet that passes every rule or a refusal that names the rule; same input, same output.

**Not built yet:** tile setting-out lines, interior elevations A-D, dimensions and tags, the 8 sheets
(AR-141 to AR-148), the PWD variant, building a toilet from a room the architect selects, hosted families.

## One-time Revit setup (BIM lead, about an hour)

1. **Title block family `UBL A3 Tender`**: A3 portrait, origin at the paper's bottom-left corner, laid out like
   the Manawala title block. Map its labels to Sheet Number, Sheet Name, Drawn By, Designed By, Checked By,
   Approved By, Sheet Issue Date, and Project Information (Client Name, Project Name, Project Address,
   Project Number, Project Status).
2. **Families**: `Concrete-Rectangular-Column` and `Concrete-Rectangular Beam` (Revit library, type parameters
   `b` and `h`). Either load them in the template or set their paths in `engine.config.json`.
3. **Materials** `Masonry - Brick` (diagonal cut pattern) and `Concrete - Cast-in-Place Concrete` (concrete
   cut pattern). The wall and column poché on the sheet comes from these.
4. Optional **view template** `UBL - As Built Plan`. If it controls the scale, the engine's scale is ignored.

## Test Families

Before the engine places a family, run **UBL Engine > Test Families** on its folder (in a new empty imperial
project). For each family it reports what is inside (category, placement type, imported CAD and where it sits
relative to the insertion point, size parameters), which ways it can be placed (on a level, on a level's work
plane, hosted in a wall, on a wall face), its measured size and insertion point, and whether move, rotate,
mirror, flip and mounting height land within 1/16" and 0.1 degree. Verdict per family: PASS, FIXABLE or REJECT.
All test placements are rolled back; only the loaded families stay. `family-test-report.txt` and
`family-test.json` are written in the families' folder. Tick the box to also get a review plan view with each
family's facing arrow and insertion point, for a person to confirm which side Revit treats as the front.

## Inputs the drawing does not contain

Put a `project.json` beside the DWG (see `project.example.json`): branch name, address, road, neighbours.
North is read from the drawing's compass when it has one (the new branch does, inside a block); a value in
`project.json` overrides it. Anything missing prints in red as INPUT REQUIRED. The engine never invents these.

## What it builds

* Levels `GF FFL` (from the LEVEL note inside) and `GF SLAB` (FFL + SLAB HEIGHT note).
* Boundary walls from the hatched wall loops on the envelope lines: one full-thickness wall along each face,
  plus any leg turning into the building. Recesses and niches are reported, not modelled as thin walls.
* Yard walls behind the building from line pairs (height is an engine default: the DWG gives none).
* Lift core as structural RCC walls; columns sized from the hatches; drop beams at the slab level.
* Ground floor and roof slab over the covered area.
* AR-21 view at the largest standard scale that fits A3 (3/32" = 1'-0" for the new branch), cropped, in the
  Existing phase, with the view template applied.
* Sheet AR-21 with the title block fields filled (checked and approved are left blank) and the view placed.
* Optional, off by default (checkbox in the confirm dialog): draft dimensions on column and wall faces, beam
  labels, column tags, levels, area statement, 2D stairs and the site band. The draftsperson checks them.
* The PDF is exported by the firm from Revit (File > Export > PDF), as they do today.

Everything it creates is in the Existing phase and carries `UBL-ENGINE:SHELL` in Comments, so a re-run
offers to replace the previous output. A report (`*.engine-report.txt`) and the recognised shell
(`*.shell.json`) are written beside the DWG.

## Known limits (next steps for this module)

* Orthogonal plots only. Raking plots like Manawala need the polygon envelope (walls traced as a polygon,
  rotated columns). This is the next piece of work on the shell.
* Stairs are 2D on the sheet. 3D stairs need basement and first-floor heights the drawing does not give.
* Interior 9" walls are listed, not built: the drawing does not say whether they are existing or proposed.
  Ask the client to put existing work on its own layers, or send the as-built DWG separately.
* Level markers are text; a level-symbol annotation family would make them match the Manawala symbol.
