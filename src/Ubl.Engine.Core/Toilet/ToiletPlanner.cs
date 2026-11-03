using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Core.Units;

namespace Ubl.Engine.Core.Toilet;

/// <summary>
/// Plans a toilet. It does not copy a layout: it tries the WC in every corner of every wall and the basin in every
/// corner, places the accessories by their rules, rejects any arrangement that breaks a hard rule
/// (<see cref="ToiletChecker"/>), scores the rest and returns the best. If none passes it refuses, naming the rule
/// that failed and the room size that would pass. Same input, same output, every time.
/// </summary>
public sealed class ToiletPlanner
{
    private readonly FixtureCatalog _cat;
    private readonly ToiletRules _r;

    public ToiletPlanner(FixtureCatalog? catalog = null, ToiletRules? rules = null)
    {
        _cat = catalog ?? FixtureCatalog.Defaults();
        _r = rules ?? ToiletRules.Default;
    }

    public FixtureCatalog Catalog => _cat;
    public ToiletRules Rules => _r;

    public static IReadOnlyList<string> RequiredRoles(ToiletSpec spec)
    {
        var roles = new List<string> { Roles.Wc, Roles.Basin, Roles.Faucet, Roles.Mirror, Roles.TissueHolder, Roles.MuslimShower, Roles.FloorTrap, Roles.Exhaust, Roles.CeilingLight };
        if (spec.Basin == BasinKind.Vanity) roles.Add(Roles.Vanity);
        if (spec.HandDrying is HandDrying.Dryer or HandDrying.Both) roles.Add(Roles.Dryer);
        if (spec.HandDrying is HandDrying.TowelDispenser or HandDrying.Both) roles.Add(Roles.TowelDispenser);
        roles.RemoveAll(r => spec.Omit.Contains(r) && r is not (Roles.Wc or Roles.Basin or Roles.Vanity));
        return roles;
    }

    public ToiletPlan Plan(ToiletSpec spec, bool explainRefusal = true)
    {
        if (spec.Width <= 0 || spec.Depth <= 0)
            return Refuse(spec, "room size", "the room needs a width and a depth", null);
        if (spec.Variant == ToiletVariant.Pwd)
            return Refuse(spec, "variant", "the PWD toilet (manual E-41 to E-43) is not built yet", null);
        foreach (var role in RequiredRoles(spec))
            if (!_cat.Has(role)) return Refuse(spec, "families", $"no family is mapped for the {ToiletChecker.Label(role)}", null);
        var door = RoomGeometry.Door(spec);
        if (door is null)
            return Refuse(spec, "door", $"the {FtIn.Format(spec.Door.Width)} door at {FtIn.Format(spec.Door.Offset)} from the corner does not fit on the {RoomGeometry.Name(spec.Door.Wall)} wall", null);
        for (int k = 0; k < spec.OtherDoors.Count; k++)
            if (RoomGeometry.DoorOf(spec, spec.OtherDoors[k]) is null)
                return Refuse(spec, "door", $"door {k + 2} ({FtIn.Format(spec.OtherDoors[k].Width)}) does not fit on the {RoomGeometry.Name(spec.OtherDoors[k].Wall)} wall", null);

        Candidate? best = null, nearest = null;
        int tried = 0, valid = 0;
        foreach (var wcWall in RoomGeometry.Sides)
            foreach (var wcEnd in new[] { DoorCorner.Start, DoorCorner.End })
                foreach (var basinWall in RoomGeometry.Sides)
                    foreach (var basinEnd in new[] { DoorCorner.Start, DoorCorner.End })
                    {
                        if (basinWall == wcWall && basinEnd == wcEnd) continue;
                        if (spec.PinWc is { } pw && (pw.Wall != wcWall || pw.End != wcEnd)) continue;
                        if (spec.PinBasin is { } pb && (pb.Wall != basinWall || pb.End != basinEnd)) continue;
                        tried++;
                        var c = Build(spec, door, wcWall, wcEnd, basinWall, basinEnd);
                        if (c.Violations.Count == 0)
                        {
                            valid++;
                            c.Score = Score(spec, door, c);
                            if (best is null || c.Score > best.Score + 1e-9 || (Math.Abs(c.Score - best.Score) <= 1e-9 && string.CompareOrdinal(c.Key, best.Key) < 0))
                                best = c;
                        }
                        else if (nearest is null || c.Reached > nearest.Reached || (c.Reached == nearest.Reached && string.CompareOrdinal(c.Key, nearest.Key) < 0))
                            nearest = c;
                    }

        if (best is null && nearest is null)
            return Refuse(spec, "pinned fixtures", "the WC and the basin are pinned to the same corner", null);
        if (best is null)
        {
            var why = nearest!.Violations[0];
            string? pass = explainRefusal ? WouldPass(spec) : null;
            return Refuse(spec, why.Rule, $"no arrangement works: {why.Detail} (best of {tried} tried: {nearest.Describe(spec)})", pass) with
            {
                Door = door, CandidatesTried = tried,
            };
        }

        var plan = new ToiletPlan
        {
            Spec = spec,
            Door = door,
            Items = best.Items,
            Arrangement = best.Describe(spec),
            Score = Math.Round(best.Score, 3),
            CandidatesTried = tried,
            CandidatesValid = valid,
            Log = best.Log,
        };
        plan.Zones.AddRange(Zones(spec, door, best.Items));
        plan.Log.Insert(0, $"{valid} of {tried} arrangements pass every rule; chosen: {plan.Arrangement} (score {plan.Score})");
        return plan;
    }

    // ------------------------------------------------------------------ one candidate

    private sealed class Candidate
    {
        public Side WcWall, BasinWall;
        public DoorCorner WcEnd, BasinEnd;
        public readonly List<PlacedItem> Items = new();
        public readonly List<Violation> Violations = new();
        public readonly List<string> Log = new();
        public double Penalty, Score;
        public int Reached;   // how many fixtures were placed before the first failure
        public string Key => $"{(int)WcWall}{(int)WcEnd}{(int)BasinWall}{(int)BasinEnd}";

        public string Describe(ToiletSpec spec)
        {
            string wc = $"WC on the {RoomGeometry.Name(WcWall)} wall at its {EndName(WcWall, WcEnd)} end";
            string basin = WcWall == BasinWall
                ? $"basin on the same wall at its {EndName(BasinWall, BasinEnd)} end (one wet wall)"
                : $"basin on the {RoomGeometry.Name(BasinWall)} wall at its {EndName(BasinWall, BasinEnd)} end";
            return $"{wc}, {basin}";
        }
    }

    private Candidate Build(ToiletSpec spec, DoorGeometry door, Side wcWall, DoorCorner wcEnd, Side basinWall, DoorCorner basinEnd)
    {
        var c = new Candidate { WcWall = wcWall, WcEnd = wcEnd, BasinWall = basinWall, BasinEnd = basinEnd };
        var r = _r;
        double rim = r.BasinRimHeight;

        // WC: centre-line 1'-6" from the side wall, tank against the wall
        var wl = RoomGeometry.Wall(spec, wcWall);
        var wcSize = _cat[Roles.Wc];
        double wcS = wcEnd == DoorCorner.Start ? r.WcToSideWall : wl.Length - r.WcToSideWall;
        Side nearSide = wcEnd == DoorCorner.Start ? RoomGeometry.Prev(wcWall) : RoomGeometry.Next(wcWall);
        var wc = WallItem(Roles.Wc, wl, wcS, 0, wcSize, 0,
            $"centre-line {FtIn.Format(r.WcToSideWall)} from the {RoomGeometry.Name(nearSide)} wall, tank against the {RoomGeometry.Name(wcWall)} wall");
        if (!Add(c, spec, door, wc)) return c;

        // basin, on a vanity counter or half-pedestal
        var bl = RoomGeometry.Wall(spec, basinWall);
        var basinSize = _cat[Roles.Basin];
        Side basinSide = basinEnd == DoorCorner.Start ? RoomGeometry.Prev(basinWall) : RoomGeometry.Next(basinWall);
        double basinS;
        if (spec.Basin == BasinKind.Vanity)
        {
            var vSize = _cat[Roles.Vanity] with { Width = r.VanityWidth, Depth = r.VanityDepth, Height = rim };
            double vs = basinEnd == DoorCorner.Start ? r.VanityWidth / 2 : bl.Length - r.VanityWidth / 2;
            var vanity = WallItem(Roles.Vanity, bl, vs, 0, vSize, 0,
                $"{FtIn.Format(r.VanityWidth)} x {FtIn.Format(r.VanityDepth)} counter in the corner with the {RoomGeometry.Name(basinSide)} wall");
            if (!Add(c, spec, door, vanity)) return c;
            basinS = vs;
        }
        else basinS = basinEnd == DoorCorner.Start ? r.BasinToSideWall : bl.Length - r.BasinToSideWall;
        var basin = WallItem(Roles.Basin, bl, basinS, 0, basinSize, rim - basinSize.Height,
            spec.Basin == BasinKind.Vanity
                ? "centred on the counter, rim at the counter top"
                : $"centre-line {FtIn.Format(r.BasinToSideWall)} from the {RoomGeometry.Name(basinSide)} wall, rim at {FtIn.Format(rim)}");
        if (!Add(c, spec, door, basin)) return c;

        bool Omit(string role) => spec.Omit.Contains(role);
        if (!Omit(Roles.Faucet))
        {
            var faucet = WallItem(Roles.Faucet, bl, basinS, r.FaucetBackFromWall, _cat[Roles.Faucet], rim, "on the basin, on its centre-line");
            if (!Add(c, spec, door, faucet)) return c;
        }

        // mirror centred over the basin (slid along the wall only if it would run past the corner); when it is left out,
        // the accessories that go beside it are still set out from where it would be
        var mSize = Omit(Roles.Mirror) || !_cat.Has(Roles.Mirror) ? FixtureCatalog.Defaults()[Roles.Mirror] : _cat[Roles.Mirror];
        double ms = Math.Clamp(basinS, mSize.Width / 2, Math.Max(mSize.Width / 2, bl.Length - mSize.Width / 2));
        // a tall mirror comes down so its top stays clear of the ceiling (never below the basin rim)
        double mz = Math.Max(rim, Math.Min(r.MirrorBottom, spec.CeilingHeight - r.CeilingClearance - mSize.Height));
        string mWhy = Math.Abs(ms - basinS) < 1e-9 ? "centred over the basin" : $"over the basin, slid {Math.Abs(ms - basinS):0.#}\" to clear the corner";
        if (mz < r.MirrorBottom - 1e-9) mWhy += $", bottom lowered to {FtIn.Format(mz)} to keep {r.CeilingClearance:0.#}\" under the ceiling";
        var mirror = WallItem(Roles.Mirror, bl, ms, 0, mSize, mz, mWhy);
        if (!Omit(Roles.Mirror) && !Add(c, spec, door, mirror)) return c;

        // tissue holder: beside the WC towards the corner, else on the side wall
        double toCorner = wcEnd == DoorCorner.Start ? -1 : 1;
        var nl = RoomGeometry.Wall(spec, nearSide);
        double SideS(double distFromWcWall) => nearSide == RoomGeometry.Prev(wcWall) ? nl.Length - distFromWcWall : distFromWcWall;
        var thSize = Omit(Roles.TissueHolder) ? null : _cat[Roles.TissueHolder];
        if (thSize is not null && !TryAdd(c, spec, door, true,
                (0, () => WallItem(Roles.TissueHolder, wl, wcS + toCorner * r.TissueHolderOffset, 0, thSize, r.TissueHolderBottom,
                    $"beside the WC, {FtIn.Format(r.TissueHolderOffset)} from its centre-line")),
                (3, () => WallItem(Roles.TissueHolder, nl, SideS(wcSize.Depth + 8), 0, thSize, r.TissueHolderBottom,
                    $"on the {RoomGeometry.Name(nearSide)} wall, 8\" in front of the WC")),
                (3, () => WallItem(Roles.TissueHolder, nl, SideS(12), 0, thSize, r.TissueHolderBottom,
                    $"on the {RoomGeometry.Name(nearSide)} wall beside the WC")))) return c;

        // Muslim shower: on the side wall next to the WC
        var msSize = Omit(Roles.MuslimShower) ? null : _cat[Roles.MuslimShower];
        if (msSize is not null && !TryAdd(c, spec, door, true,
                (0, () => WallItem(Roles.MuslimShower, nl, SideS(r.MuslimShowerFromWcWall), 0, msSize, r.MuslimShowerBottom,
                    $"on the {RoomGeometry.Name(nearSide)} wall next to the WC")),
                (2, () => WallItem(Roles.MuslimShower, wl, wcS + toCorner * r.TissueHolderOffset, 0, msSize, r.MuslimShowerBottom,
                    "beside the WC towards the corner")),
                (3, () => WallItem(Roles.MuslimShower, wl, wcS - toCorner * (wcSize.Width / 2 + r.AccessoryGap + msSize.Width / 2), 0, msSize, r.MuslimShowerBottom,
                    "beside the WC on its open side")))) return c;

        // floor drain in the WC corner
        var ftSize = Omit(Roles.FloorTrap) ? null : _cat[Roles.FloorTrap];
        var corner = wl.At(wcEnd == DoorCorner.Start ? 0 : wl.Length);
        var along = wl.Dir * (wcEnd == DoorCorner.Start ? 1 : -1);
        double off = r.FloorTrapFromWalls;
        if (ftSize is not null && !TryAdd(c, spec, door, true,
                (0, () => FreeItem(Roles.FloorTrap, Mount.Floor, corner + along * (off + ftSize.Width / 2) + wl.Normal * (off + ftSize.Depth / 2), ftSize, 0, "in the WC corner")),
                (1, () => FreeItem(Roles.FloorTrap, Mount.Floor, corner + along * (off + ftSize.Width / 2) + wl.Normal * (wcSize.Depth + off + ftSize.Depth / 2), ftSize, 0, "in front of the WC, at the corner side")),
                (2, () => FreeItem(Roles.FloorTrap, Mount.Floor, wl.At(wcS, wcSize.Depth + 6 + ftSize.Depth / 2), ftSize, 0, "in front of the WC")))) return c;

        // soap dispenser beside the mirror (optional: skipped with a note if there is no room)
        var soapSize = _cat.Has(Roles.SoapDispenser) && !Omit(Roles.SoapDispenser) ? _cat[Roles.SoapDispenser] : null;
        if (soapSize is not null)
        {
            var opts = BesideMirror(bl, mirror, soapSize, Roles.SoapDispenser, r.SoapBottom, basinEnd, "beside the mirror", 0);
            if (!TryAdd(c, spec, door, false, opts))
                c.Log.Add("soap dispenser: no free wall space beside the mirror, left out");
        }

        // hand drying near the basin: beside the mirror, else on the wall at the basin's corner, else the nearest free wall
        foreach (var (role, bottom) in HandDryingRoles(spec).Where(h => !Omit(h.Role)))
            if (!TryAdd(c, spec, door, true, HandDryingOptions(spec, role, bottom, bl, mirror, basin, basinWall, basinEnd, basinSide))) return c;

        // ceiling: exhaust fan centred, light between the centre and the basin
        var exSize = _cat[Roles.Exhaust];
        var centre = new Pt(spec.Width / 2, spec.Depth / 2);
        if (Omit(Roles.Exhaust)) { }
        else if (exSize.Mount == Mount.Ceiling)
        {
            if (!TryAdd(c, spec, door, true,
                    (0, () => FreeItem(Roles.Exhaust, Mount.Ceiling, centre, exSize, spec.CeilingHeight - exSize.Height, "centred on the ceiling")))) return c;
        }
        else
        {
            // a wall-type exhaust: high on the wall opposite the door, else any wall
            var order = new List<Side> { RoomGeometry.Opposite(door.Wall), wcWall };
            order.AddRange(RoomGeometry.Sides.Where(s => !order.Contains(s)));
            var opts = new List<(double, Func<PlacedItem>)>();
            double z0 = spec.CeilingHeight - r.ExhaustBelowCeiling - exSize.Height;
            foreach (var side in order.Distinct())
            {
                var sl = RoomGeometry.Wall(spec, side);
                for (double k = 0; k <= sl.Length / 2; k += 2)
                    foreach (double s in k == 0 ? new[] { sl.Length / 2 } : new[] { sl.Length / 2 - k, sl.Length / 2 + k })
                    {
                        double ss = s;
                        opts.Add((side == order[0] ? 0 : 2, () => WallItem(Roles.Exhaust, sl, ss, 0, exSize, z0, $"high on the {RoomGeometry.Name(side)} wall")));
                    }
            }
            if (!TryAdd(c, spec, door, true, opts)) return c;
        }

        var liSize = _cat[Roles.CeilingLight];
        var basinFront = basin.Anchor + basin.Facing * basin.Depth;
        bool longX = spec.Width >= spec.Depth;
        var axis = longX ? new Pt(1, 0) : new Pt(0, 1);
        double quarter = (longX ? spec.Width : spec.Depth) / 4;
        double sign = (basinFront - centre).Dot(axis) >= 0 ? 1 : -1;
        var spots = new List<(double Penalty, Pt At, string Why)>
        {
            (0, centre + (basinFront - centre) * 0.5, "between the ceiling centre and the basin"),
            (1, centre + axis * (sign * quarter), "on the room's long axis, towards the basin"),
            (2, centre + axis * (-sign * quarter), "on the room's long axis"),
            (2, centre, "centred"),
        };
        if (!Omit(Roles.CeilingLight) && !TryAdd(c, spec, door, true, LightOptions(spec, spots, liSize))) return c;

        foreach (var i in c.Items) c.Log.Add($"{ToiletChecker.Label(i.Role)}: {i.Rule}");
        foreach (var o in spec.Omit) c.Log.Add($"{ToiletChecker.Label(o)}: LEFT OUT (its family could not be used)");
        return c;
    }

    private IEnumerable<(double, Func<PlacedItem>)> HandDryingOptions(ToiletSpec spec, string role, double bottom, WallLine bl, PlacedItem mirror,
        PlacedItem basin, Side basinWall, DoorCorner basinEnd, Side cornerWall)
    {
        var size = _cat[role];
        foreach (var o in BesideMirror(bl, mirror, size, role, bottom, basinEnd, "beside the mirror", 0)) yield return o;
        var cl = RoomGeometry.Wall(spec, cornerWall);
        bool sharedCornerIsEnd = cornerWall == RoomGeometry.Prev(basinWall);
        for (double d = 6 + size.Width / 2; d <= 36; d += 1)
        {
            double s = sharedCornerIsEnd ? cl.Length - d : d, dd = d;
            yield return (2, () => WallItem(role, cl, s, 0, size, bottom, $"on the {RoomGeometry.Name(cornerWall)} wall, {dd - size.Width / 2:0}\" from the basin corner"));
        }
        var scan = new List<(double Dist, Side Side, double S)>();
        foreach (var side in RoomGeometry.Sides)
        {
            var sl = RoomGeometry.Wall(spec, side);
            for (double s = size.Width / 2; s <= sl.Length - size.Width / 2 + 1e-9; s += 2)
                scan.Add((Pt.Dist(sl.At(s), basin.Anchor), side, s));
        }
        foreach (var p in scan.OrderBy(p => p.Dist).ThenBy(p => (int)p.Side).ThenBy(p => p.S))
        {
            var sl = RoomGeometry.Wall(spec, p.Side);
            var pp = p;
            yield return (5, () => WallItem(role, sl, pp.S, 0, size, bottom, $"on the {RoomGeometry.Name(pp.Side)} wall, nearest free space to the basin"));
        }
    }

    private static IEnumerable<(double, Func<PlacedItem>)> LightOptions(ToiletSpec spec, List<(double Penalty, Pt At, string Why)> spots, FixtureSize size)
    {
        PlacedItem Make(Pt at, string why) => FreeItem(Roles.CeilingLight, Mount.Ceiling, at, size, spec.CeilingHeight - size.Height, why);
        foreach (var sp in spots) yield return (sp.Penalty, () => Make(sp.At, sp.Why));
        // then any free spot on the ceiling, nearest the preferred one first
        var want = spots[0].At;
        double mx = size.Width / 2 + 2, my = size.Depth / 2 + 2;
        var grid = new List<Pt>();
        for (double x = mx; x <= spec.Width - mx + 1e-9; x += 2)
            for (double y = my; y <= spec.Depth - my + 1e-9; y += 2)
                grid.Add(new Pt(x, y));
        foreach (var g in grid.OrderBy(g => Pt.Dist(g, want)).ThenBy(g => g.X).ThenBy(g => g.Y).Take(400))
            yield return (3, () => Make(g, "nearest free spot on the ceiling to the basin"));
    }

    private static List<(double, Func<PlacedItem>)> BesideMirror(WallLine bl, PlacedItem mirror, FixtureSize size, string role, double bottom, DoorCorner basinEnd, string why, double penalty)
    {
        // away from the basin's corner first (towards the room), then towards the corner
        double away = basinEnd == DoorCorner.Start ? 1 : -1;
        var list = new List<(double, Func<PlacedItem>)>();
        foreach (double dir in new[] { away, -away })
            for (double gap = 3; gap <= 30; gap += 1)
            {
                double s = mirror.S + dir * (mirror.Width / 2 + gap + size.Width / 2);
                list.Add((penalty + (dir == away ? 0 : 1), () => WallItem(role, bl, s, 0, size, bottom, why)));
            }
        return list;
    }

    private IEnumerable<(string Role, double Bottom)> HandDryingRoles(ToiletSpec spec)
    {
        if (spec.HandDrying is HandDrying.Dryer or HandDrying.Both) yield return (Roles.Dryer, _r.DryerBottom);
        if (spec.HandDrying is HandDrying.TowelDispenser or HandDrying.Both) yield return (Roles.TowelDispenser, _r.TowelBottom);
    }

    /// <summary>Adds one item; on any violation it stays out and the candidate is marked failed.</summary>
    private bool Add(Candidate c, ToiletSpec spec, DoorGeometry door, PlacedItem item)
    {
        c.Items.Add(item);
        var v = new List<Violation>();
        ToiletChecker.CheckItems(spec, door, c.Items, _r, v, 1);
        if (v.Count == 0) { c.Reached++; return true; }
        c.Items.RemoveAt(c.Items.Count - 1);
        c.Violations.AddRange(v);
        return false;
    }

    /// <summary>Tries the options in order and keeps the first that breaks no rule.</summary>
    private bool TryAdd(Candidate c, ToiletSpec spec, DoorGeometry door, bool required, params (double Penalty, Func<PlacedItem> Make)[] options) =>
        TryAdd(c, spec, door, required, (IEnumerable<(double, Func<PlacedItem>)>)options);

    private bool TryAdd(Candidate c, ToiletSpec spec, DoorGeometry door, bool required, IEnumerable<(double Penalty, Func<PlacedItem> Make)> options)
    {
        List<Violation>? firstFail = null;
        string? role = null;
        foreach (var (penalty, make) in options)
        {
            var item = make();
            role ??= item.Role;
            c.Items.Add(item);
            var v = new List<Violation>();
            ToiletChecker.CheckItems(spec, door, c.Items, _r, v, 1);
            if (v.Count == 0)
            {
                c.Penalty += penalty;
                c.Reached++;
                if (firstFail is { Count: > 0 }) c.Log.Add($"{ToiletChecker.Label(item.Role)}: first choice not possible because {firstFail[0].Detail}");
                return true;
            }
            c.Items.RemoveAt(c.Items.Count - 1);
            firstFail ??= v;
        }
        if (required)
        {
            if (firstFail is { Count: > 0 }) c.Violations.Add(new("missing fixture", $"no place for the {ToiletChecker.Label(role ?? "?")}: first try failed because {firstFail[0].Detail}"));
            else c.Violations.Add(new("missing fixture", "no place for a required fixture"));
        }
        return !required;
    }

    // ------------------------------------------------------------------ scoring

    private double Score(ToiletSpec spec, DoorGeometry door, Candidate c)
    {
        var wc = c.Items.First(i => i.Role == Roles.Wc);
        var basin = c.Items.First(i => i.Role == Roles.Basin);
        var dl = RoomGeometry.Wall(spec, door.Wall);
        var doorPt = dl.At((door.S0 + door.S1) / 2);
        var r = _r;
        double score = 0;
        if (c.WcWall == c.BasinWall) score += r.ScoreOneWetWall;
        if (spec.WetWalls.Contains(c.WcWall)) score += r.ScoreWcOnWetWall;
        if (spec.WetWalls.Contains(c.BasinWall)) score += r.ScoreBasinOnWetWall;
        score += r.ScoreWcFromDoor * Pt.Dist(wc.Plan.Center, doorPt);
        score -= r.ScoreBasinToDoor * Pt.Dist(basin.Plan.Center, doorPt);
        var zone = ToiletChecker.ClearZoneOf(spec, wc, c.Items, r);
        if (zone is not null && RoomGeometry.HitsSwing(spec, door, zone.Plan)) score -= r.ScoreSwingOverWcSpace;
        score -= c.Penalty;
        return score;
    }

    // ------------------------------------------------------------------ refusals

    private string? WouldPass(ToiletSpec spec)
    {
        for (int grow = 1; grow <= 48; grow++)
        {
            foreach (var (s, what) in new[] { (spec with { Width = spec.Width + grow }, "width"), (spec with { Depth = spec.Depth + grow }, "depth") })
            {
                var p = Plan(s, explainRefusal: false);
                if (!p.Refused)
                    return $"needs {grow}\" more {what}: {FtIn.Format(s.Width)} x {FtIn.Format(s.Depth)} would pass";
            }
        }
        return "no size up to 4'-0\" larger passes: check the door and the fixture sizes";
    }

    private static ToiletPlan Refuse(ToiletSpec spec, string rule, string reason, string? wouldPass) => new()
    {
        Spec = spec,
        Refused = true,
        RefusalRule = rule,
        RefusalReason = reason,
        WouldPass = wouldPass,
        Log = { $"REFUSED ({rule}): {reason}" + (wouldPass is null ? "" : $"; {wouldPass}") },
    };

    private IEnumerable<ClearZone> Zones(ToiletSpec spec, DoorGeometry door, List<PlacedItem> items)
    {
        foreach (var owner in items.Where(i => i.Role is Roles.Wc or Roles.Basin))
            if (ToiletChecker.ClearZoneOf(spec, owner, items, _r) is { } z) yield return z;
        if (door.SwingIn) yield return new ClearZone("door swing", RoomGeometry.SwingSquare(spec, door), "door");
        yield return new ClearZone("door entry", RoomGeometry.EntryZone(spec, door, _r), "door");
    }

    // ------------------------------------------------------------------ item builders

    internal static PlacedItem WallItem(string role, WallLine wl, double s, double back, FixtureSize size, double z0, string rule) => new()
    {
        Role = role,
        Mount = role == Roles.Faucet ? Mount.OnBasin : size.Mount == Mount.Ceiling ? Mount.Wall : size.Mount,
        Wall = wl.Side,
        S = Math.Round(s, 6),
        BackOffset = back,
        Width = size.Width,
        Depth = size.Depth,
        Height = size.Height,
        Z0 = Math.Round(z0, 6),
        Plan = wl.Rect(s, size.Width, back, size.Depth),
        Facing = wl.Normal,
        Anchor = wl.At(s, back),
        Rule = rule,
    };

    internal static PlacedItem FreeItem(string role, Mount mount, Pt centre, FixtureSize size, double z0, string rule) => new()
    {
        Role = role,
        Mount = mount,
        Wall = null,
        Width = size.Width,
        Depth = size.Depth,
        Height = size.Height,
        Z0 = Math.Round(z0, 6),
        Plan = new Box(centre.X - size.Width / 2, centre.Y - size.Depth / 2, centre.X + size.Width / 2, centre.Y + size.Depth / 2),
        Facing = new Pt(0, -1),
        Anchor = centre,
        Rule = rule,
    };

    public static string EndName(Side side, DoorCorner end) => side switch
    {
        Side.South => end == DoorCorner.Start ? "west" : "east",
        Side.East => end == DoorCorner.Start ? "south" : "north",
        Side.North => end == DoorCorner.Start ? "east" : "west",
        _ => end == DoorCorner.Start ? "north" : "south",
    };
}
