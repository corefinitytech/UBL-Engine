You are continuing an existing project with me. Read this whole brief before doing anything, then read the repo files it points to. Don't start coding until you've confirmed what you found.

# 1. The project

Isbah Hassan & Associates (IHA, Lahore) is an architecture firm. They draw tender sets for UBL (United Bank Limited) bank branches. We are building an "engine": a Revit 2026 add-in (C#) that produces drawings from a single input.
- The input is the client's DWG/furniture layout. The rules come from the UBL Ameen Branch Design Manual (2nd edition 2025) and from IHA's own Manawala branch set ("UBL-MANAWALA WORKING PLANS.pdf").
- The engine produces everything up to the drawing output. IHA annotates and exports the final PDFs.
- It must be professional and deterministic: no glitchy or sloppy output, every decision explainable, refusals with reasons rather than guesses.
- CURRENT SCOPE: TOILETS ONLY. Don't start other rooms unless I ask.

Testing setup: a friend's Windows laptop has Revit 2026 (not LT), the .NET 8 SDK and the Imperial Multi-discipline template. Neither of us is a civil engineer, so instructions must be simple, step by step, with full commands. The client supplied 12 bathroom families (Bath_Revit.zip), which go in C:\UBL\Families\Bath.

# 2. The repo (UblEngine)

Solution layout:
- src/Ubl.Engine.Core: no Revit reference. All decisions live here: planner, checker, drawing planner, existing-room reading. Unit tested.
- src/Ubl.Engine.Revit: the thin Revit add-in. It multi-targets Revit 2026 (.NET 8) and 2027 (.NET 10). The ribbon tab is "UBL Engine", with panels Tender Set (AR-21 As-Built Shell, Check Setup, Test Families) and Rooms (Build Toilet).
- src/Ubl.Engine.Dwg: ACadSharp DWG reading (used by the AR-21 shell module).
- src/Ubl.Engine.Cli: command-line tool, ubl-engine.
- tests/Ubl.Engine.Core.Tests: xUnit. 59 tests must pass.
- verify.ps1: runs the tests, the CLI checks, builds the add-in and deploys it (-BuildAddin).
- START-HERE.md: the friend's step-by-step guide (Steps 1 to 7).
- TESTING.md and README.md.

Config files beside the add-in (they are copied to %APPDATA%\Autodesk\Revit\Addins\2026\Ubl.Engine.Revit\):
- toilet.families.json:
  - families_folder;
  - the role-to-family cards, each with "front" ("-Y", "+Y", "-X" or "+X") and an optional "mount";
  - door_family "Single-Flush" and door_type_name "D-03";
  - a "rules" section that overrides ToiletRules (snake_case);
  - a "drawings" section that overrides DrawingRules.
- engine.config.json: title_block_family (default "UBL A3 Tender"), text font Arial, and the AR-21 settings.

Key toilet files:
- Core/Toilet/ToiletModel.cs:
  - Side { South, East, North, West }, Mount, BasinKind, HandDrying, DoorCorner, Roles.
  - FixtureSize, FixtureCatalog.
  - ToiletRules: every value tagged W = Manawala, D = new-branch DWG, M = manual, E = engine default to confirm.
  - DoorSpec, ToiletSpec (including OtherDoors, PinWc/PinBasin, Omit, WetWalls), PlacedItem, DoorGeometry, ToiletPlan.
- Core/Toilet/RoomGeometry.cs: WallLine (walls run counter-clockwise S, E, N, W; Start/Dir/Normal into the room; S along the wall; DOf), door geometry, swing test, entry zone.
- Core/Toilet/ToiletChecker.cs: all hard rules. Door swing, entry and opening are checked for every door.
- Core/Toilet/ToiletPlanner.cs:
  - 56 arrangements (WC and basin corners); places accessories; scores; refuses with WouldPass.
  - Mirror bottom = max(rim, min(42", ceiling − 2" − mirror height)).
  - Unusable accessory families are left out (Omit).
- Core/Toilet/PlacementMath.cs: solves the rotation and insertion point from the measured family extents and its front.
- Core/Toilet/ToiletDrawings.cs: ToiletDrawingPlanner, DrawingRules, DrawingView and the DLine/DFill/DText/DLabel/DDim records.
- Core/Toilet/ToiletDrawingSvg.cs: SVG previews of the sheets.
- Core/Toilet/ExistingRoom.cs:
  - RoomRect: the rectangle from boundary segments; merges split pieces; handles turned rooms; refuses non-rectangles.
  - WallOf(): which wall a door sits in.
  - Door(): builds a DoorSpec from what was read.
- Revit/Commands/BuildToiletCommand.cs: the whole flow.
- Revit/Toilet/FixturePlacer.cs: measures each family in a SubTransaction, places it, reads the transform back, corrects it, and verifies to 1/16" and 0.1°.
- Revit/Toilet/ToiletRoomBuilder.cs: walls, door (swing verified from the plan arc; wall cut verified), floor finish, ceiling, room, vanity DirectShape with the basin hole, views, and FitOut() for existing rooms.
- Revit/Toilet/ToiletDrawingBuilder.cs: plan views, sections A–D, detail lines, filled regions, text notes with leaders, dimensions, sheets, placeholder title block.
- Revit/Toilet/ExistingRoomReader.cs: reads an existing bathroom.
- Revit/Toilet/ToiletFamilyConfig.cs.

Conventions:
- All lengths are inches.
- Room frame: origin at the inside south-west corner, x east, y north. The inside of the room is [0,W] x [0,D].
- RoomFrame maps room coordinates to the model and supports turned rooms.

# 3. What Build Toilet does now

Build Toilet offers four choices:
1. Manawala male toilet: 5'-0" x 6'-3", door on the south wall 4.5" from its east end.
2. New-branch toilet 1: 7'-11" x 5'-0", vanity counter, wet wall east, door on the west wall.
3. A toilet.json file.
4. "In a bathroom already in the project..."

Flow:
1. Load the families and measure them.
2. Plan.
3. Confirmation dialog (it lists LEFT OUT and CHECK items).
4. Delete this toilet's previous output, found by its marker in the Comments parameter plus its view and sheet names.
5. Build the room, or fit out an existing one.
6. Views.
7. Door (new toilets only).
8. Fixtures.
9. Drawing set: each view and each sheet in its own transaction, so a failure is logged and the rest goes on.
10. Report, plan JSON/SVG, and exports to Documents\UBL Engine:
    - PNG and DWG files of the views;
    - a combined PDF and a PNG of each sheet.

Drawing set (modelled on Manawala PDF pages 35–41 and 50, A3 portrait):
- The sheets:
  - AR-141 layout plan;
  - AR-142 flooring plan;
  - AR-143 ceiling plan;
  - AR-144 sanitary plan;
  - AR-145 to AR-148 wall elevations A–D.
- Elevations are true sections cut at mid-room.
- A second toilet's sheets are numbered AR-141-01 to AR-148-08.
- Letters rule: C = WC wall, D = the wall opposite it, B = the side wall with the door (else the one with the basin), A = the other side wall.
- Tiles are 12"x24". On the floor, the long side runs along the door wall, with full tiles from the door corner. On the walls, 1' courses go up to the ceiling with 24" joints, and lines stop at fixtures.
- Dimensions to walls use the wall side faces, verified against plan values, with plain invisible-line witnesses as fallback.
- If the "UBL A3 Tender" title block isn't loaded, the sheets get a placeholder frame in the title block's position (SheetFormat.A3Portrait).

Existing-bathroom mode (choice 4):
- The user clicks inside the bathroom in a floor plan. If there's no room element, one named TOILET is created.
- The engine reads:
  - the rectangle from the Finish boundary;
  - the wall thicknesses;
  - every hosted door (width and height from parameters; swing and hinge from the plan arc);
  - an existing ceiling height (no ceiling added if one is there);
  - an existing floor (no tile finish added if one is there);
  - the slab height from the wall tops;
  - foreign items in the room and low windows, which become CHECK warnings.
- It asks for the basin type and the plumbing wall.
- Walls, doors and the room are never changed.
- The marker is "UBL-ENGINE:TOILET:ROOM:<room UniqueId>".
- The name is the room's name and number. If a scratch-built toilet already owns that name, " (EXISTING)" is added.

# 4. Proven, and not proven yet

Proven:
- Everything compiles with 0 errors and 0 warnings for 2026 and 2027.
- 59 tests pass. They cover golden cases (Manawala reproduces AR-141: WC and basin on the west wall, centre-lines 1'-6" | 3'-3" | 1'-6"), an 85,264-room sweep with 0 rule breaks, placement maths, the client families' measured sizes, drawing rules (no overlapping notes, no crossing leaders, every sheet fits A3), and existing-room reading.

First real Revit run (31 Oct), with the older build:
- 11/11 fixtures placed at 0.000" and 0° in both demo toilets, confirmed independently from Revit's DWG export.
- Bugs found and fixed since: floor finish EndCap (floors need NoEndCap); a door that didn't cut its wall (the door type must be regenerated before placing); exported pictures zoomed out (fixed with annotation crop and hidden datums); the basin hidden under the vanity (fixed by cutting a hole); the mirror touching the ceiling (fixed by a 2" clearance rule).

NOT yet run in Revit:
- the drawing set;
- the existing-bathroom mode;
- the multi-door logic.
The friend must run the latest build. Expect API surprises:
- view range on the ceiling plan;
- section orientation;
- leader end and elbow positions;
- filled region types;
- dimension references;
- PDF export options.

# 5. Client family problems (the client promised fixes)

- Muslim Shower, Soap Dispenser and Tissue Dispenser are drawn lying down.
- The Muslim Shower's geometry is about 206" from its insertion point; the Dryer's is 15" off.
- All 12 families are in the Furniture category, so the ceiling light and exhaust don't show in the ceiling plan. The drawings draw symbols for them meanwhile.
- The mirror is 2'-0" x 3'-6"; Manawala has 3'x3'.

Still waiting from the client:
- the A3 title block (to be loaded as "UBL A3 Tender");
- confirmation of the ceiling height (7'-0" manual vs 7'-2"/8'-0" in Manawala);
- confirmation of the mounting heights (all E defaults);
- the tile setting-out rule;
- whether tiles go up to the ceiling;
- the elevation letter rule;
- whether there's a vanity cabinet (only the 1½" top is modelled).

When the fixed families arrive: copy them into C:\UBL\Families\Bath, adjust the "front" values in toilet.families.json if needed, re-run Build Toilet, and check the FAMILY CHECK lines disappear.

# 6. Immediate next steps

1. Have the friend rebuild and deploy, run Build Toilet (choices 1 and 2) with the current families, and run choice 4 on the team's two custom-dimensioned bathrooms. The .rvt files go in C:\UBL\Projects; work on copies.
   - Commands:
     cd C:\UBL\UblEngine
     powershell -ExecutionPolicy Bypass -File .\verify.ps1 -BuildAddin
   - Expect: ALL CHECKS PASSED and "unit tests (59 expected)".
   - Revit must be closed while deploying.
2. Collect the results:
   - Compress-Archive -Path (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'UBL Engine\*') -DestinationPath "$env:USERPROFILE\Desktop\ubl-toilet-output.zip" -Force
   - plus screenshots of each confirmation box.
   - Read every *.toilet-report.txt, especially the "drawings:" lines, CHECK lines and DRAWINGS section, and any *.toilet-error.txt.
   - Open the sheet PDFs and PNGs and compare them with the Manawala sheets.
   - If you are running on that Windows laptop yourself, you can read Documents\UBL Engine directly.
3. Fix whatever Revit refuses or draws wrong. For each fix, add or adjust a Core test where the logic is testable. Keep the Revit layer thin.
4. Swap in the fixed families and the title block when the client sends them.

Later (only when I say so):
- female toilet (the mirror image) and the PWD variant (manual E-41 to E-43);
- reading toilets and wet walls directly from the client's DWG (the new-branch DWG has two 7'-11" x 5'-0" toilets);
- vanity cabinet;
- the pantry module.

# 7. Rules for how you work

- Decisions go in Core (testable, no Revit). The Revit code only executes them.
- Every rule value lives in ToiletRules or DrawingRules with a source tag (W/D/M/E).
- Never guess silently: refuse with the rule and the reason, or log a CHECK line.
- Wrap each Revit item in its own try/transaction so one failure never kills the build. Report everything in the toilet report.
- Run the tests after every change. Update verify.ps1's expected test count and START-HERE.md when behaviour changes.
- Re-running a build must replace only that toilet's own output, never another toilet's views or sheets, and never existing walls, doors or rooms.
- Never send images to the client's engineers that aren't straight from Revit. Earlier I made isometric renders from a DWG export (with three corrections applied); they were renders, not Revit output. That renderer isn't in the repo.
- Talk to me in plain words. Focus on the work, no extra chatter. Ask before big or irreversible decisions.
- Don't commit or push unless I ask.

Start by:
- reading START-HERE.md, BuildToiletCommand.cs, ToiletDrawingBuilder.cs and ExistingRoomReader.cs;
- running the tests (dotnet test, or verify.ps1);
- telling me what you found and whether the friend's Revit results are available yet.