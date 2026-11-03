using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Ubl.Engine.Core.Json;
using Ubl.Engine.Core.Toilet;
using Ubl.Engine.Core.Units;

namespace Ubl.Engine.Cli;

/// <summary>The toilet planner from the command line: one room, or the full sweep of sizes and door positions.</summary>
public static class ToiletCommands
{
    /// <summary>
    /// ubl-engine toilet toilet.json [--svg plan.svg] [--json plan.json] [--sheets folder]
    /// ubl-engine toilet 5'-0" 6'-3" [--door south:east:4.5:in] [--vanity] [--towel|--both|--no-drying] [--wet west,north]
    /// </summary>
    public static int Plan(string[] a)
    {
        if (a.Length == 0) { Console.Error.WriteLine("usage: ubl-engine toilet <toilet.json | width depth> [options]"); return 2; }
        ToiletSpec spec;
        try { spec = ReadSpec(a); }
        catch (Exception ex) { Console.Error.WriteLine($"bad input: {ex.Message}"); return 2; }

        var sw = Stopwatch.StartNew();
        var plan = new ToiletPlanner().Plan(spec);
        long ms = sw.ElapsedMilliseconds;
        Print(plan, ms);

        string? svg = Opt(a, "--svg"), json = Opt(a, "--json"), sheets = Opt(a, "--sheets");
        if (svg is not null) { File.WriteAllText(svg, ToiletSvg.Render(plan)); Console.WriteLine($"wrote {svg}"); }
        if (json is not null) { File.WriteAllText(json, EngineJson.Serialize(plan)); Console.WriteLine($"wrote {json}"); }
        if (sheets is not null && !plan.Refused)
        {
            // previews of the eight A3 sheets (AR-141 to AR-148) the Revit add-in makes for this toilet
            Directory.CreateDirectory(sheets);
            var set = ToiletDrawingPlanner.Plan(plan);
            foreach (var n in set.Notes) Console.WriteLine($"  drawings: {n}");
            foreach (var sh in set.Sheets)
            {
                var v = set.View(sh.ViewKey);
                string file = Path.Combine(sheets, $"{sh.Number} {sh.Name}.svg".Replace('/', '-').Replace('"', '\''));
                File.WriteAllText(file, ToiletDrawingSvg.Render(set, sh));
                Console.WriteLine($"  {sh.Number,-10} {sh.Name,-44} {v.Scale.Name,-14} {Path.GetFileName(file)}");
            }
        }
        return plan.Refused ? 5 : 0;
    }

    public static void Print(ToiletPlan plan, long ms)
    {
        var s = plan.Spec;
        Console.WriteLine($"{s.Name}: {FtIn.Format(s.Width)} x {FtIn.Format(s.Depth)} inside, door {FtIn.Format(s.Door.Width)} on the {RoomGeometry.Name(s.Door.Wall)} wall " +
                          $"{FtIn.Format(s.Door.Offset)} from its {ToiletInput.CompassOf(s.Door.Wall, s.Door.FromCorner)} end, swings {(s.Door.SwingIn ? "in" : "out")}  ({ms} ms)");
        if (plan.Refused)
        {
            Console.WriteLine($"REFUSED [{plan.RefusalRule}] {plan.RefusalReason}");
            if (plan.WouldPass is not null) Console.WriteLine($"  {plan.WouldPass}");
            return;
        }
        foreach (var l in plan.Log) Console.WriteLine($"  {l}");
        Console.WriteLine();
        Console.WriteLine($"  {"fixture",-16} {"wall",-6} {"centre-line",-26} {"bottom",-8} size (w x d x h)");
        foreach (var i in plan.Items)
        {
            string where = i.Wall is { } w ? FromNearCorner(s, w, i.S) : $"at ({FtIn.Format(i.Anchor.X)}, {FtIn.Format(i.Anchor.Y)})";
            Console.WriteLine($"  {ToiletChecker.Label(i.Role),-16} {(i.Wall is null ? "-" : RoomGeometry.Name(i.Wall.Value)),-6} {where,-26} {FtIn.Format(i.Z0),-8} {i.Width:0.#} x {i.Depth:0.#} x {i.Height:0.#}");
        }
    }

    private static string FromNearCorner(ToiletSpec s, Side wall, double sAlong)
    {
        var wl = RoomGeometry.Wall(s, wall);
        bool fromStart = sAlong <= wl.Length - sAlong;
        double d = fromStart ? sAlong : wl.Length - sAlong;
        var other = fromStart ? RoomGeometry.Prev(wall) : RoomGeometry.Next(wall);
        return $"{FtIn.Format(d)} from the {RoomGeometry.Name(other)} wall";
    }

    /// <summary>
    /// Every inside size from 4'-0" to 10'-0" in 1" steps, with the door on each wall, at either end, swinging in or
    /// out: 85,264 rooms. Every one must come out as a toilet that passes every rule or as a refusal that names the
    /// rule. Writes a summary, the refused list and a contact sheet of 100 random rooms.
    /// </summary>
    public static int Sweep(string[] a)
    {
        double from = double.Parse(Opt(a, "--from") ?? "48"), to = double.Parse(Opt(a, "--to") ?? "120"), step = double.Parse(Opt(a, "--step") ?? "1");
        string outDir = Opt(a, "--out") ?? "toilet-sweep";
        bool explain = a.Contains("--explain");
        Directory.CreateDirectory(outDir);
        var planner = new ToiletPlanner();
        var rules = planner.Rules;

        var cases = new List<ToiletSpec>();
        for (double w = from; w <= to + 1e-9; w += step)
            for (double d = from; d <= to + 1e-9; d += step)
                foreach (var side in RoomGeometry.Sides)
                    foreach (var corner in new[] { DoorCorner.Start, DoorCorner.End })
                        foreach (bool swingIn in new[] { true, false })
                            cases.Add(new ToiletSpec
                            {
                                Width = w, Depth = d,
                                Door = new DoorSpec { Wall = side, FromCorner = corner, Offset = 4.5, Width = 30, SwingIn = swingIn },
                            });

        var results = new ToiletPlan[cases.Count];
        var checkerFailures = new ConcurrentBag<string>();
        var crashes = new ConcurrentBag<string>();
        var sw = Stopwatch.StartNew();
        Parallel.For(0, cases.Count, i =>
        {
            try
            {
                var p = planner.Plan(cases[i], explain);
                results[i] = p;
                if (!p.Refused)
                {
                    var v = ToiletChecker.Check(p, rules, ToiletPlanner.RequiredRoles(p.Spec));
                    if (v.Count > 0) checkerFailures.Add($"{Describe(cases[i])}: {v[0]}");
                }
                else if (string.IsNullOrEmpty(p.RefusalRule)) checkerFailures.Add($"{Describe(cases[i])}: refused without a rule");
            }
            catch (Exception ex) { crashes.Add($"{Describe(cases[i])}: {ex.GetType().Name}: {ex.Message}"); }
        });
        double perCase = sw.Elapsed.TotalMilliseconds / cases.Count * Environment.ProcessorCount;

        // determinism: re-run every 97th case and compare the serialised plans
        int mismatches = 0, rechecked = 0;
        for (int i = 0; i < cases.Count; i += 97)
        {
            rechecked++;
            if (results[i] is null) continue;
            if (EngineJson.Serialize(planner.Plan(cases[i], explain)) != EngineJson.Serialize(results[i])) mismatches++;
        }

        int valid = results.Count(r => r is { Refused: false });
        int refused = results.Count(r => r is { Refused: true });
        var sb = new StringBuilder();
        sb.AppendLine($"UBL Engine toilet sweep  {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"sizes {FtIn.Format(from)} to {FtIn.Format(to)} in {step}\" steps, door 2'-6\" 4 1/2\" from a corner, on each wall, both ends, swinging in or out");
        sb.AppendLine($"cases: {cases.Count:N0}");
        sb.AppendLine($"valid toilets: {valid:N0}   refused with a named rule: {refused:N0}   crashes: {crashes.Count}   rule breaks in valid toilets: {checkerFailures.Count}");
        sb.AppendLine($"determinism: {rechecked:N0} cases re-run, {mismatches} differences");
        sb.AppendLine($"time: {sw.Elapsed.TotalSeconds:0.0} s on {Environment.ProcessorCount} threads, about {perCase:0.0} ms per toilet per thread");
        sb.AppendLine();
        sb.AppendLine("refusals by rule:");
        foreach (var g in results.Where(r => r is { Refused: true }).GroupBy(r => r.RefusalRule).OrderByDescending(g => g.Count()))
            sb.AppendLine($"  {g.Count(),7:N0}  {g.Key}");
        sb.AppendLine();
        sb.AppendLine("smallest valid rooms (door swinging in):");
        foreach (var p in results.Where(r => r is { Refused: false } && r.Spec.Door.SwingIn).OrderBy(r => r.Spec.Width * r.Spec.Depth).Take(5))
            sb.AppendLine($"  {FtIn.Format(p.Spec.Width)} x {FtIn.Format(p.Spec.Depth)}, door {RoomGeometry.Name(p.Spec.Door.Wall)}: {p.Arrangement}");
        foreach (var c in crashes.Take(20)) sb.AppendLine($"CRASH {c}");
        foreach (var c in checkerFailures.Take(20)) sb.AppendLine($"RULE BREAK {c}");
        File.WriteAllText(Path.Combine(outDir, "sweep-summary.txt"), sb.ToString());
        Console.Write(sb.ToString());

        var csv = new StringBuilder("width_in,depth_in,door_wall,door_from,swing,rule,reason,would_pass\n");
        foreach (var p in results.Where(r => r is { Refused: true }))
            csv.AppendLine($"{p.Spec.Width},{p.Spec.Depth},{RoomGeometry.Name(p.Spec.Door.Wall)},{ToiletInput.CompassOf(p.Spec.Door.Wall, p.Spec.Door.FromCorner)},{(p.Spec.Door.SwingIn ? "in" : "out")},{Csv(p.RefusalRule)},{Csv(p.RefusalReason)},{Csv(p.WouldPass)}");
        File.WriteAllText(Path.Combine(outDir, "refused.csv"), csv.ToString());

        var rnd = new Random(7);
        var sample = Enumerable.Range(0, 100).Select(_ => results[rnd.Next(results.Length)]).Where(p => p is not null).ToList();
        var sampleExplained = sample.Select(p => p.Refused ? planner.Plan(p.Spec, true) : p).ToList();
        File.WriteAllText(Path.Combine(outDir, "contact-sheet.svg"), ToiletSvg.ContactSheet(sampleExplained, 10, 2.0, "100 random rooms from the sweep (red = refused, with the reason and the size that would pass)"));
        Console.WriteLine($"wrote {outDir}/sweep-summary.txt, refused.csv, contact-sheet.svg");
        return crashes.IsEmpty && checkerFailures.IsEmpty && mismatches == 0 ? 0 : 1;
    }

    private static ToiletSpec ReadSpec(string[] a)
    {
        if (a[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return ToiletInput.Parse(File.ReadAllText(a[0]));
        if (a.Length < 2) throw new FormatException("give a toilet.json, or a width and a depth");
        double w = Length(a[0]), d = Length(a[1]);
        var spec = new ToiletSpec { Width = w, Depth = d };
        if (Opt(a, "--door") is { } door)
        {
            var p = door.Split(':');
            var side = ToiletInput.ParseSide(p[0]);
            spec = spec with
            {
                Door = new DoorSpec
                {
                    Wall = side,
                    FromCorner = p.Length > 1 ? ToiletInput.CornerOf(side, p[1].ToLowerInvariant()) : DoorCorner.End,
                    Offset = p.Length > 2 ? Length(p[2]) : 4.5,
                    SwingIn = p.Length <= 3 || p[3] != "out",
                },
            };
        }
        if (a.Contains("--vanity")) spec = spec with { Basin = BasinKind.Vanity };
        if (a.Contains("--towel")) spec = spec with { HandDrying = HandDrying.TowelDispenser };
        if (a.Contains("--both")) spec = spec with { HandDrying = HandDrying.Both };
        if (a.Contains("--no-drying")) spec = spec with { HandDrying = HandDrying.None };
        if (Opt(a, "--wet") is { } wet) spec = spec with { WetWalls = wet.Split(',').Select(ToiletInput.ParseSide).ToList() };
        return spec;
    }

    /// <summary>5'-0", 60" or plain 60 (inches).</summary>
    private static double Length(string text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v
        : FtIn.Parse(text) ?? throw new FormatException($"{text} is not a length");

    private static string Describe(ToiletSpec s) =>
        $"{s.Width}x{s.Depth} door {RoomGeometry.Name(s.Door.Wall)}/{ToiletInput.CompassOf(s.Door.Wall, s.Door.FromCorner)}/{(s.Door.SwingIn ? "in" : "out")}";

    private static string? Opt(string[] a, string name)
    {
        int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static string Csv(string? s) => s is null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
}
