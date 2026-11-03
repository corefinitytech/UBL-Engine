# UBL Engine: Toilet Demo in Revit 2026

Six steps on the laptop with Revit 2026 take you to the two demos: **Test Families** (checks the client's 12
bathroom families) and **Build Toilet** (builds a complete toilet from one click). Allow 30 to 40 minutes the first time.

## Before you start

Already installed on the laptop:

- Autodesk Revit 2026 (not Revit LT)
- .NET 8 SDK, x64
- Visual Studio 2026 Community (not needed for these steps)

You also need:

- `UblEngine-toilet-module.zip` (the engine), saved in the Downloads folder
- `Bath_Revit.zip` (the client's 12 bathroom families)
- An internet connection for the first build (it downloads two small packages)

Every command below goes into **Windows PowerShell**: press the Start button, type `PowerShell`, open
**Windows PowerShell**. Copy a whole grey box, paste it with a right-click, press Enter.

## Step 1: Put the files in place

The engine goes in `C:\UBL\UblEngine` and the families in `C:\UBL\Families\Bath`. If the zip is not in
Downloads, change the path in the second line.

```powershell
New-Item -ItemType Directory -Force C:\UBL\Families\Bath
Expand-Archive -Path "$env:USERPROFILE\Downloads\UblEngine-toilet-module.zip" -DestinationPath C:\UBL -Force
Get-ChildItem C:\UBL -Recurse | Unblock-File
Test-Path C:\UBL\UblEngine\verify.ps1
```

The last line must print `True`.

Now the families. `Bath_Revit.zip` holds a RAR file (`Bath Revit.rar`). Open it with 7-Zip or WinRAR (Windows 11
can also open it with right-click > Extract All) and copy the 12 `.rfa` files into `C:\UBL\Families\Bath`. Then check:

```powershell
(Get-ChildItem C:\UBL\Families\Bath\*.rfa).Count
```

It must print `12`. If it prints `0`, the files landed in a subfolder; this moves them up:

```powershell
Get-ChildItem C:\UBL\Families\Bath -Recurse -Filter *.rfa | Move-Item -Destination C:\UBL\Families\Bath
(Get-ChildItem C:\UBL\Families\Bath\*.rfa).Count
```

## Step 2: Build and install the add-in

One script tests the engine, builds the Revit add-in and installs it. **Close Revit first**: an open Revit locks
the add-in file.

Check the .NET SDK (one line must start with `8.0`):

```powershell
dotnet --list-sdks
```

Build and install (2 to 5 minutes the first time):

```powershell
cd C:\UBL\UblEngine
powershell -ExecutionPolicy Bypass -File .\verify.ps1 -BuildAddin
```

Among the output you should see these lines (some carry more text after them), and at the very end ALL CHECKS PASSED:

```
[PASS] .NET SDK 8.
[PASS] unit tests (59 expected)
[SKIP] engine on the drawing: pass -Dwg to run it
[PASS] toilet: Manawala arrangement
[PASS] toilet: WC centre-line 1'-6"
[PASS] toilet: 4'-0" x 4'-0" refused with a reason
[PASS] Revit 2026 add-in build
[PASS] add-in deployed

ALL CHECKS PASSED
```

Confirm the add-in is installed (must print `True`):

```powershell
Test-Path "$env:APPDATA\Autodesk\Revit\Addins\2026\Ubl.Engine.Revit.addin"
```

Any line that says FAIL: stop and send `C:\UBL\UblEngine\verify-report.txt`.

## Step 3: Open Revit and a new project

1. Start **Revit 2026**.
2. A **Security - Unsigned Add-In** box appears for `Ubl.Engine.Revit`: click **Always Load**.
3. On the start screen, under **Models**, click **New...**. In **Template file** pick **Imperial Multi-discipline**
   (not Metric), leave **Project** selected, click **OK**.
4. The ribbon now has a **UBL Engine** tab with two panels:
    - **Tender Set**: AR-21 As-Built Shell, Check Setup, Test Families
    - **Rooms**: Build Toilet

No UBL Engine tab: see the last section.

## Step 4: Demo 1, Test Families

This checks every bathroom family: how it can be placed, its real size, where its insertion point is, and whether
move, rotate, mirror and height land within 1/16" and 0.1 degree. Everything it places for testing is undone at the end.

1. Click **UBL Engine > Test Families**.
2. In the file box go to `C:\UBL\Families\Bath`, click any family (for example `WC.rfa`), click **Open**. All 12
   families in that folder are tested.
3. The box asks **Test 12 families from "Bath"?** Tick **Also leave a review layout...** and click **OK**.
4. Wait 1 to 2 minutes (Dryer and Basin are the heavy ones).
5. A summary box shows how many are PASS, FIXABLE and REJECT, one line per family. Expect mostly FIXABLE: the
   families are in the Furniture category and contain imported AutoCAD drawings. A REJECT is the result that matters.
6. The plan view **UBL Family Review** opens. Type `ZF` on the keyboard to zoom to fit, then take a screenshot
   (Windows key + Shift + S).
7. For the 3D view: **View** tab > **3D View** > **Default 3D View**, type `ZF`, take a screenshot.
8. Open the folder with the two report files, `family-test-report.txt` and `family-test.json`:

```powershell
explorer C:\UBL\Families\Bath
```

**Reading the review view (needed for Step 5).** Each family has a small cross and an arrow. The cross is its
insertion point. The arrow always points along the family's own +Y axis (up the screen). For each family, note
where its usable front is compared with the arrow: the WC's seat, the basin's bowl, the face of the dryer,
dispensers and mirror, the Muslim shower's spray head. One word each: **opposite**, **same**, **right** or
**left**. The floor trap and ceiling light are round, so skip them.

## Step 5: Tell the engine which side is the front

The front side is the only thing the engine cannot measure; it measures everything else in Revit. Every family is
set to **opposite the arrow** already, so only change the ones you noted differently. Revit can stay open.

Open the settings file in Notepad:

```powershell
notepad "$env:APPDATA\Autodesk\Revit\Addins\2026\Ubl.Engine.Revit\toilet.families.json"
```

| The front is... (arrow pointing up the screen) | Write |
| --- | --- |
| opposite the arrow (down) | `"-Y"` (already set) |
| the same way as the arrow (up) | `"+Y"` |
| to the right of the arrow | `"+X"` |
| to the left of the arrow | `"-X"` |

Example: the WC's front is the same way as its arrow. Change its line to:

```json
    "wc":              { "family": "WC",               "front": "+Y" },
```

Also put the families folder on the first line, so Build Toilet never has to ask for it. Use **two** backslashes:

```json
  "families_folder": "C:\\UBL\\Families\\Bath",
```

Keep every quote and comma, then save with Ctrl+S. Build Toilet reads this file each time it runs, so you don't
need to restart Revit.

If you run `verify.ps1 -BuildAddin` again later, it overwrites this file. Make the same changes in
`C:\UBL\UblEngine\src\Ubl.Engine.Revit\toilet.families.json` so they survive a rebuild.

## Step 6: Demo 2, Build Toilet

One click builds the whole toilet: walls, door, floor, ceiling, room and every fixture, each one checked after it
is placed, and then its eight drawing sheets (AR-141 to AR-148). Use the same project as Step 4; the families are
already loaded there.

1. Click **UBL Engine > Build Toilet** and choose **Manawala male toilet** (5'-0" x 6'-3").
2. Only if it asks: pick any `.rfa` in `C:\UBL\Families\Bath`.
3. The box **Build MALE TOILET?** shows the layout it chose and each family's measured size. Click **OK**.
4. A result box says **MALE TOILET: N of N fixtures placed and verified**. Every line should say OK with an error of 0.000.
   At the bottom it says **Drawings: 8 sheets, AR-141 to AR-148**.
5. Sheet **AR-141** opens. In the **Project Browser** under **Sheets** there are eight: layout, flooring, ceiling and
   sanitary plans, and wall elevations A to D. Open each one and take a screenshot. Until the firm's A3 title block is
   loaded, each sheet has a plain placeholder frame where the title block goes.
6. Open **Views > Floor Plans > MALE TOILET - LAYOUT**. Expect the WC on the left wall near the top, the basin on the same wall
   near the bottom with the mirror over it, and the door at the bottom right swinging in. Take a screenshot.
7. In the **Project Browser** open **Views > 3D Views > MALE TOILET - 3D**. Check that every fixture faces into the
   room and nothing floats or sinks. Take a screenshot.

Then show that it adapts to a different room:

8. **Build Toilet > New-branch toilet 1** (7'-11" x 5'-0" with a vanity counter). It is built 10 ft to the right,
   with views **TOILET 1 - LAYOUT** and **TOILET 1 - 3D**, and its own eight sheets numbered **AR-141-01 to AR-148-08**
   (the Manawala numbering for a second toilet). Take screenshots of the layout and the 3D view.
9. **Build Toilet > Manawala male toilet** again. It replaces the first one in place. Ctrl+Z undoes a whole build in one step.

A fixture turned the wrong way means its front in Step 5 is wrong. Fix that one line, save, and click Build Toilet
again; it replaces itself.

The reports (`MALE TOILET.toilet-report.txt`, a `.toilet-plan.svg` you can open in a browser, and a
`.toilet-plan.json`) are in Documents\UBL Engine:

```powershell
explorer (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'UBL Engine')
```

## Step 7: A bathroom that is already in the project

Use this for bathrooms someone has already drawn (walls and door in place). The engine reads the bathroom from the
model and fits it out. Its walls, doors and room are never changed.

1. Open the floor plan that shows the bathroom (**Project Browser > Views > Floor Plans**).
2. Click **UBL Engine > Build Toilet** and choose **In a bathroom already in the project...**
3. Click once anywhere inside the bathroom.
4. Choose the basin: **Half-pedestal basin on the wall** or **Basin in a vanity counter**.
5. Choose the wall with the plumbing behind it (as seen in the plan), or press **No** if nobody knows.
6. The box **Build <NAME>?** starts with what the engine read: the inside size, each wall's thickness, the door (its
   size, where it is and which way it swings) and the ceiling. Check these against the drawing. Any line starting
   with **CHECK** needs a look: something already standing in the room, a low window, or a door whose swing could not
   be read. Click **OK**.
7. The result box says the fixtures were placed and verified, **existing door kept**, and the number of sheets.
   Take a screenshot of the plan and of the 3D view.
8. Do the same for the second bathroom.

What it does and does not do:

- The size, wall thicknesses and door come from the model. If a bathroom has a ceiling, the engine uses its height;
  if it has none, it adds one at 7'-0". If there is no floor, it adds the tile finish.
- A bathroom with more than one door: every door is kept clear of fixtures.
- Views and sheets are named after the room's name and number, for example **TOILET 3 - LAYOUT**.
- Running it again in the same bathroom replaces only what the engine added.
- It refuses, with the reason, a bathroom that is not a rectangle, walls that do not close round it, a bathroom with
  no door, or one too small for the rules (the box then says what size would work).

## What to send back

Build Toilet saves the report, pictures (PNG) and DWG files of each toilet's views, a PDF of its eight sheets
(`MALE TOILET sheets.pdf`) and a picture of each sheet in Documents\UBL Engine.
Zip that folder to the Desktop and send the zip:

```powershell
Compress-Archive -Path (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'UBL Engine\*') -DestinationPath "$env:USERPROFILE\Desktop\ubl-toilet-output.zip" -Force
```

Also:

- [ ] `C:\UBL\UblEngine\verify-report.txt`
- [ ] `C:\UBL\Families\Bath\family-test-report.txt` and `family-test.json`
- [ ] Screenshots of the UBL Family Review plan and its 3D view
- [ ] The front word for each family (opposite, same, right, left), or the edited `toilet.families.json`
- [ ] `MALE TOILET.toilet-report.txt` and `TOILET 1.toilet-report.txt` from Documents\UBL Engine
- [ ] Screenshots of each toilet's LAYOUT and 3D views (the sheets themselves come in the zip as a PDF)
- [ ] Screenshots of the confirmation box for each existing bathroom (Step 7), so the readings can be checked
- [ ] Any `*.toilet-error.txt` file, if a build failed

## If something goes wrong

| What you see | Why | Fix |
| --- | --- | --- |
| `dotnet` is not recognized | The .NET 8 SDK is missing, or PowerShell was opened before it was installed | Install the .NET 8 SDK x64, close PowerShell, open it again |
| "running scripts is disabled" | Windows script policy | Use the exact command from Step 2, with `-ExecutionPolicy Bypass` |
| verify: "Revit is running and locks the add-in" | Revit is open | Close Revit and run Step 2 again |
| verify: unit tests FAIL with restore or NuGet errors | No internet on the first build | Connect to the internet and run Step 2 again |
| No UBL Engine tab in Revit | Add-in not installed, or Do Not Load was clicked | Run the `Test-Path` check from Step 2, then restart Revit and click Always Load |
| "Families needed... not in the project and not in ..." | Wrong families folder, or the family names differ | Check Step 1 shows 12 files, and set `families_folder` as in Step 5 |
| "... measures ... That is too big for a toilet fixture" | The family's imported AutoCAD drawing has stray objects or the wrong scale | Send the report: the client has to fix that family |
| A fixture faces the wrong way | Its front in Step 5 is wrong | Change that line, save, click Build Toilet again |
| "Open the floor plan that shows the bathroom" | Step 7 was started from a 3D view or a sheet | Open the floor plan, then click Build Toilet again |
| "This room cannot be fitted out: the room is not a rectangle" | The bathroom has a jog, a column or a curved wall | The engine plans rectangular toilets only. Send a screenshot of the plan |
| "No door was found in the walls of this room" | The door is not hosted in the bathroom's walls, or is on another level | Place the door in the bathroom wall and run Step 7 again |
| A fixture says FAIL in the result box | It did not land within 1/16" and 0.1 degree | Send the toilet report |
| The door swings the wrong way, or the report says the swing was NOT verified | The door family draws no swing arc in plan | Flip it with the door's arrows in Revit and mention it when you send the report |
| An error box after clicking OK | Something Revit refused | Send `<NAME>.toilet-error.txt` from Documents\UBL Engine. Nothing is left half-built: the whole build is undone |
