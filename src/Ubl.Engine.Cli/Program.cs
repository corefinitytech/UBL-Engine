using System.Diagnostics;
using Ubl.Engine.Core.Cad;
using Ubl.Engine.Core.Json;
using Ubl.Engine.Core.Shell;
using Ubl.Engine.Core.Units;
using Ubl.Engine.Dwg;

// ubl-engine shell <drawing.dwg|dxf> [out.json] [--layers layers.json]
// ubl-engine toilet <toilet.json | width depth> [options]      (see ToiletCommands)
// ubl-engine toilet-sweep [--out folder] [--step 1]
// Runs the engine outside Revit: fast feedback while tuning rules, and the batch/CI entry point.

if (args.Length >= 1 && args[0] == "toilet") return Ubl.Engine.Cli.ToiletCommands.Plan(args[1..]);
if (args.Length >= 1 && args[0] == "toilet-sweep") return Ubl.Engine.Cli.ToiletCommands.Sweep(args[1..]);
if (args.Length < 2 || args[0] != "shell")
{
    Console.Error.WriteLine("usage: ubl-engine shell <drawing.dwg|dxf> [out.json] [--layers layers.json]");
    Console.Error.WriteLine("       ubl-engine toilet <toilet.json | width depth> [--door south:east:4.5:in] [--vanity] [--svg plan.svg] [--json plan.json] [--sheets folder]");
    Console.Error.WriteLine("       ubl-engine toilet-sweep [--out folder] [--step 1] [--explain]");
    return 2;
}

string input = args[1];
string? output = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : null;
int li = Array.IndexOf(args, "--layers");
var layers = li > 0 && li + 1 < args.Length ? LayerAliases.Load(args[li + 1]) : LayerAliases.Default;

var sw = Stopwatch.StartNew();
CadDrawing drawing;
try { drawing = AcadReader.Read(input); }
catch (Exception ex) { Console.Error.WriteLine($"could not read {input}: {ex.Message}"); return 3; }
long readMs = sw.ElapsedMilliseconds;

ShellModel shell;
try { shell = new ShellRecogniser(layers).Recognise(drawing); }
catch (RecognitionException ex) { Console.Error.WriteLine($"refused: {ex.Message}"); return 4; }
long totalMs = sw.ElapsedMilliseconds;

Console.WriteLine($"{drawing.Source}: {drawing.EntityCount} entities, read {readMs} ms, recognised in {totalMs - readMs} ms");
Console.WriteLine(shell.Summary());
foreach (var c in shell.Checks)
    Console.WriteLine($"  [{c.Status.ToString().ToLowerInvariant()}] {c.Name}: {c.Detail}");
foreach (var u in shell.Unclassified.GroupBy(u => u.Kind))
    Console.WriteLine($"  unclassified: {u.Count()} x {u.Key}");
Console.WriteLine($"  missing inputs: {string.Join("; ", shell.MissingInputs)}");
if (drawing.ReaderWarnings.Count > 0)
{
    Console.WriteLine($"  reader warnings: {drawing.ReaderWarnings.Count}");
    foreach (var w in drawing.ReaderWarnings.Distinct().Take(5)) Console.WriteLine($"    {w}");
}

if (output is not null)
{
    File.WriteAllText(output, EngineJson.Serialize(shell));
    Console.WriteLine($"wrote {output}");
}
return shell.Checks.Any(c => c.Status == CheckStatus.Fail) ? 1 : 0;
