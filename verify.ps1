<#
  One-command check of the engine on your laptop.

  Run from the UblEngine folder:
    powershell -ExecutionPolicy Bypass -File verify.ps1 -Dwg "C:\path\to\new_block.dwg"
  Add -BuildAddin to also build the Revit add-in (close Revit first), and -RevitVersion 2027 if you have 2027.

  It writes verify-report.txt next to this script: send me that file.
#>
param(
    [string] $Dwg = "",
    [string] $RevitVersion = "2026",
    [switch] $BuildAddin
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
$report = New-Object System.Collections.Generic.List[string]
$failed = 0

function Say([string] $text) { Write-Host $text; $report.Add($text) }
function Result([string] $name, [bool] $ok, [string] $detail) {
    if ($ok) { $tag = "PASS" } else { $tag = "FAIL"; $script:failed++ }
    Say ("[{0}] {1}{2}" -f $tag, $name, $(if ($detail) { ": $detail" } else { "" }))
}

Say "UBL Engine verification  $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
Say "folder: $root"

# 1. .NET SDK
$needed = "8."
if ([int]$RevitVersion -ge 2027) { $needed = "10." }
$sdks = (& dotnet --list-sdks 2>&1) -join "`n"
Result ".NET SDK $needed" ($sdks -match ("(?m)^" + [regex]::Escape($needed))) ($sdks -replace "`n", "; ")

# 2. tests (the golden test runs when a DWG is given)
if ($Dwg -ne "") {
    if (-not (Test-Path $Dwg)) { Result "drawing" $false "not found: $Dwg"; $Dwg = "" }
    else { $env:UBL_NEWBRANCH_DWG = (Resolve-Path $Dwg).Path }
}
$testOut = (& dotnet test "tests\Ubl.Engine.Core.Tests" -c Release 2>&1) -join "`n"
$testLine = ($testOut -split "`n" | Where-Object { $_ -match "Passed!|Failed!|error" } | Select-Object -First 3) -join " | "
Result "unit tests (59 expected)" ($LASTEXITCODE -eq 0 -and $testOut -match "Passed:\s+59") $testLine
if ($LASTEXITCODE -ne 0) { $report.Add("---- test output ----"); $report.Add($testOut) }

# 3. command-line engine on the drawing
if ($Dwg -ne "") {
    $json = Join-Path $env:TEMP "ubl-shell.json"
    $cli = (& dotnet run --project "src\Ubl.Engine.Cli" -c Release -- shell $env:UBL_NEWBRANCH_DWG $json 2>&1) -join "`n"
    $report.Add("---- engine output ----"); $report.Add($cli)
    Write-Host $cli
    $expect = [ordered]@{
        "covered envelope 33'-0`" x 88'-6 1/2`"" = "33'-0`" x 88'-6 1/2`" covered";
        "columns 10 + 2 + 2"                      = "10 x 18`"X25`", 2 x 24`"X24`", 2 x 18`"X24`"";
        "12 boundary walls 9`""                   = "12 boundary walls 9`"";
        "9 beams"                                 = "; 9 beams;";
        "lift core"                               = "1 core(s)";
        "bay chain 13/13"                         = "13/13 column sizes";
        "north from the compass"                  = "north is 0.7 degrees clockwise";
        "area discrepancy flagged"                = "vs 2,888 sft written on the drawing"
    }
    foreach ($k in $expect.Keys) { Result $k ($cli.Contains($expect[$k])) "" }
}
else { Say "[SKIP] engine on the drawing: pass -Dwg to run it" }

# 3b. toilet planner (no drawing needed): the Manawala male toilet, 5'-0" x 6'-3" inside, door at the corner
$toilet = (& dotnet run --project "src\Ubl.Engine.Cli" -c Release -- toilet 60 75 --door south:east:4.5:in 2>&1) -join "`n"
$report.Add("---- toilet planner output ----"); $report.Add($toilet)
Write-Host $toilet
Result "toilet: Manawala arrangement" ($toilet.Contains("WC on the west wall at its north end, basin on the same wall at its south end (one wet wall)")) ""
Result "toilet: WC centre-line 1'-6`"" ($toilet -match "WC\s+west\s+1'-6`" from the north wall") ""
$small = (& dotnet run --project "src\Ubl.Engine.Cli" -c Release -- toilet 48 48 2>&1) -join "`n"
Result "toilet: 4'-0`" x 4'-0`" refused with a reason" ($small.Contains("REFUSED") -and $small.Contains("would pass")) (($small -split "`n" | Select-Object -Last 2) -join " ")

# 4. Revit add-in build
if ($BuildAddin) {
    if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
        Result "Revit add-in build" $false "Revit is running and locks the add-in: close it and run again"
    }
    else {
        $build = (& dotnet build "src\Ubl.Engine.Revit" -c Debug "-p:RevitVersion=$RevitVersion" 2>&1) -join "`n"
        $ok = ($LASTEXITCODE -eq 0)
        $addin = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion\Ubl.Engine.Revit.addin"
        Result "Revit $RevitVersion add-in build" $ok ""
        Result "add-in deployed" (Test-Path $addin) $addin
        if (-not $ok) { $report.Add("---- build output ----"); $report.Add($build) }
    }
}
else { Say "[SKIP] Revit add-in build: pass -BuildAddin (close Revit first)" }

Say ""
if ($failed -eq 0) { Say "ALL CHECKS PASSED" } else { Say "$failed CHECK(S) FAILED: send verify-report.txt" }
$report | Set-Content -Path (Join-Path $root "verify-report.txt") -Encoding UTF8
Write-Host "report: $(Join-Path $root 'verify-report.txt')"
