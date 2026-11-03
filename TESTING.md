# Testing the engine tomorrow: step by step

Do these in order. Each step tells you what you should see. If a step doesn't match, stop and send me the
output; don't move on to Revit with a failing step.

## 0. What was already verified (31 Oct)

In my sandbox, with the same C# compiler (Roslyn), the .NET 8 and .NET 10 runtimes, ACadSharp 3.8.30 and
the real Revit 2026 and Revit 2027 API assemblies:

* all five projects compile with 0 errors and 0 warnings (nullable checks on);
* all 59 tests pass, including the golden test that reads the client's DWG directly (28 Oct) and the toilet tests;
* the toilet sweep: 85,264 rooms, each a valid toilet or a refusal naming a rule, no crash, same output twice;
* the command-line engine on the DWG gives exactly the numbers below;
* the C# output matches the Python reference column for column, wall for wall, beam for beam.

Not verified: building through Visual Studio/MSBuild and NuGet (I compiled the files directly), and
anything that happens inside Revit itself. Steps 2 and 5 below are the first time those run.

## 1. Install

* Revit 2026 (with its content library). A Revit trial usually installs the latest version, 2027: that works
  too (checked), but then install the **.NET 10 SDK** and add `-p:RevitVersion=2027` to the build command in
  step 5. Revit LT will not work: it does not run add-ins.
* .NET 8 SDK x64 (Revit 2026) or .NET 10 SDK x64 (Revit 2027): `dotnet --list-sdks` shows it.
* Visual Studio 2026 Community, ".NET desktop development" workload.

## Shortcut: steps 2 to 4 in one command

```
powershell -ExecutionPolicy Bypass -File verify.ps1 -Dwg "C:\path\to\new_block.dwg"
```

It runs the tests, the golden test, the command-line engine and the toilet planner, checks each expected result, and writes
`verify-report.txt`. Add `-BuildAddin` (with Revit closed) to also build and deploy the add-in, and
`-RevitVersion 2027` if that's your version. If everything says PASS, go straight to step 5.

## 2. Build and run the tests (no Revit needed)

Open a terminal in the `UblEngine` folder:

```
dotnet test tests\Ubl.Engine.Core.Tests
```

Expect: `Passed! - Failed: 0, Passed: 59` (the toilet sweep test takes about 20 seconds). The first run downloads ACadSharp 3.8.30 and xUnit from NuGet.

## 3. The golden test on the real drawing

```
set UBL_NEWBRANCH_DWG=C:\path\to\new_block.dwg
dotnet test tests\Ubl.Engine.Core.Tests
```

Expect 54 passed again (the golden test now runs instead of skipping).

## 4. The command-line engine

```
dotnet run --project src\Ubl.Engine.Cli -- shell C:\path\to\new_block.dwg C:\path\to\shell.json
```

Expect (timings will differ):

```
new_block.dwg: 1005 entities, read ... ms, recognised in ... ms
33'-0" x 88'-6 1/2" covered (2,922 sft); columns 10 x 18"X25", 2 x 24"X24", 2 x 18"X24"; 12 boundary walls 9"; 9 beams; 1 core(s); 5 stair element(s)
  [info] wall recess: left wall at [...] has a recess or niche of 161 sq in; modelled as a solid 9in wall
  [info] beam lines: 5 short or unpaired beam-layer lines ignored (lift cross, lintel)
  [warn] covered area: computed 2,922 sft (...) vs 2,888 sft written on the drawing; ... 87'-6", 1'-0 1/2" less than measured
  [pass] column bay chain: 13/13 column sizes and bay lengths along the left wall agree with the architect's dimensions
  [pass] overall width: overall width 33'-0"
  [pass] covered depth: covered depth 88'-6 1/2"
  [pass] plot depth: plot depth, rear boundary to front columns, 106'-9"
  [info] north: compass found in the drawing: north is 0.7 degrees clockwise from up
  unclassified: 11 x interior 9in wall (existing or proposed? needs the architect)
  unclassified: 1 x wall hatch on the right line but outside the covered area (verandah pier?)
  unclassified: 1 x RCC not labelled as lift or stair (taken as proposed)
  missing inputs: road name; neighbour labels (left, right, rear); branch name and address for the title block
  reader warnings: 1
    ACadSharp.Tables.TextStyle table reference ... not found ...   (harmless)
```

## 5. Revit

1. **Close Revit** (it locks the add-in DLL), then:
   `dotnet build src\Ubl.Engine.Revit -c Debug`
   This copies the add-in to `%AppData%\Autodesk\Revit\Addins\2026\`.
2. Start Revit 2026. When it warns about an unsigned add-in, choose **Always Load**.
3. New project from the **Imperial Multi-discipline** template (not Metric).
4. Optional: put a `project.json` beside the DWG (copy `project.example.json` and fill in road, neighbours,
   branch name). Without it the sheet shows red INPUT REQUIRED placeholders.
5. Ribbon **UBL Engine > Check Setup** first. It changes nothing; it lists whether the families, materials,
   title block and phases are there. Fix anything marked MISSING (CHECK items are only notes).
6. Ribbon **UBL Engine > AR-21 As-Built Shell**, pick the DWG.
7. The confirm dialog lists the checks. Leave "Also add draft dimensions and labels" **unticked** for the
   first run. Click OK.

Expect after a few seconds:

* Levels `GF FFL` at 3'-0" and `GF SLAB` at 13'-8".
* 14 structural columns (types `18"X25" RCC COLUMN`, `18"X24" RCC COLUMN`, `24"X24" RCC COLUMN`).
* 25 walls: 13 boundary walls (`Brick 9" (Existing)`), 5 lift-core walls (`RCC 9" (Existing)`), 7 yard walls.
* 9 beams (`18"X16" RCC BEAM` and `15"X16" RCC BEAM`: 6" slab + 10" drop).
* Ground floor and roof slab over the covered area.
* View `AR-21 AS BUILT LAYOUT PLAN (GROUND FLOOR)` at 3/32" = 1'-0", and sheet AR-21 with it placed.
* A final dialog, and `new_block.engine-report.txt` beside the DWG.

Look at the 3D view: walls between the columns on both sides, the L-shaped rear wall, the lift shaft with
its door gap, beams under the slab. Then run it a second time: it should offer to replace the first run.

Then try once with the draft dimensions box ticked, and look at the sheet.

## 5b. Test the bathroom families (Test Families button)

1. Unzip the client's families into one folder (for example `C:\UBL\Families\Bath`), nothing else in it.
2. In Revit: **New project** from the **Imperial Multi-discipline** template (a fresh one, not the AR-21 project).
3. Ribbon **UBL Engine > Test Families**, pick any `.rfa` in that folder. Tick **"Also leave a review layout"**. OK.

Expect after about a minute (the 7.7 MB Dryer and 5.1 MB Basin are the slow ones):

* A summary dialog: so many PASS / FIXABLE / REJECT, one line per family. From what I could read without
  Revit, expect **no PASS**: all 12 are in the Furniture category and 11 contain an imported DWG, which makes
  them FIXABLE at best. The question the test answers is whether any is a **REJECT** (cannot be placed, or a
  move/rotate/mirror/height test does not land exactly).
* `family-test-report.txt` and `family-test.json` in the families' folder (or in %TEMP% if it is read-only).
* A plan view `UBL Family Review <date>` opens: each family with an arrow and a cross. The arrow is the side
  Revit treats as the front; the cross is the insertion point. Write down every family whose arrow does not
  point at the side a person uses (WC: towards the seat opening; basin: towards the user; dispensers: away
  from the wall), and any whose cross is not on or at the back edge of the object.

Send me: the report, the json, and a screenshot of the review view (zoomed to fit) plus one 3D view of it.

## 5c. Build a toilet (Build Toilet button)

Do 5b first and set the `front` of any family whose front is not opposite the review arrow in
`%AppData%\Autodesk\Revit\Addins\<version>\Ubl.Engine.Revit\toilet.families.json`
(`"-Y"` = opposite the arrow, `"+Y"` = same as the arrow, `"+X"` / `"-X"` = to the right / left of it).
Optionally set `"families_folder"` to the folder of the bath families so it does not ask each time.

1. A new project from the Imperial Multi-discipline template (or the one from 5b).
2. **UBL Engine > Build Toilet > Manawala male toilet**. If the families are not loaded it asks for their folder.
3. A dialog shows the arrangement and each family as measured. OK.

Expect after a few seconds:

* Walls `Brick 4 1/2"` round a 5'-0" x 6'-3" room, a 2'-6" door in the south wall at its east end swinging in,
  floor finish, ceiling at 7'-0", a room named MALE TOILET.
* WC on the west wall, centre-line 1'-6" from the north wall; basin on the same wall 1'-6" from the south wall;
  mirror over the basin, tap on the basin, tissue holder beside the WC, Muslim shower on the north wall, floor
  drain in the WC corner, soap dispenser by the mirror, dryer by the door, exhaust and light on the ceiling
  (the exhaust goes high on a wall instead if its family is a wall unit).
* Views `MALE TOILET - LAYOUT` (opens), `- CEILING` and `- 3D`.
* A final dialog: "N of N fixtures placed and verified", each with its measured error (should be 0.000).
* `MALE TOILET.toilet-report.txt`, `.toilet-plan.json` and `.toilet-plan.svg` in `Documents\UBL Engine`.

Then: Build Toilet again with **New-branch toilet 1**, and once more with Manawala male to see it replace itself.
In the 3D view, check each fixture's front faces into the room and nothing floats or sinks. If one is turned the
wrong way, its `front` in `toilet.families.json` is wrong: change it and run again (no rebuild needed).

Send me: the report, a screenshot of the LAYOUT view and of the 3D view, for each toilet.

## 6. If something goes wrong

| What you see | Cause and fix |
|---|---|
| No UBL Engine tab | The `.addin` file is not in `%AppData%\Autodesk\Revit\Addins\2026\`. Build in Debug, or copy it by hand. |
| "Family 'Concrete-Rectangular-Column' is not loaded..." | Load it once with Insert > Load Family (Structural Columns > Concrete), or fix the path in `engine.config.json`. The engine also searches the Revit library by itself. |
| "Family has no type parameter 'b'" | Your column/beam family uses other parameter names: set them in `engine.config.json`. |
| Walls with no hatch in the plan | Your template's materials are named differently; the report says which material it used. |
| Sheet has no title block | None was loaded; the report says so. Load any title block for now. |
| Error dialog after OK | Send me `new_block.engine-error.txt` (beside the DWG, or in %TEMP%). Nothing is half-built: the whole run rolls back. |
| Build error "file is being used by another process" | Revit is open. Close it and build again. |

## 7. Send me

* `verify-report.txt` (or the output of steps 2, 3 and 4).
* A screenshot of the Check Setup dialog.
* `new_block.engine-report.txt`.
* Screenshots: the 3D view, the AR-21 view, the sheet.
* For the families: `family-test-report.txt`, `family-test.json`, screenshots of the review view.
* For each toilet: `<NAME>.toilet-report.txt`, screenshots of its LAYOUT and 3D views.
