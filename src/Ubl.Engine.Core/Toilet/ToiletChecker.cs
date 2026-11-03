using Ubl.Engine.Core.Geometry;

namespace Ubl.Engine.Core.Toilet;

public sealed record Violation(string Rule, string Detail)
{
    public override string ToString() => $"{Rule}: {Detail}";
}

/// <summary>
/// Checks a placed toilet against every hard rule. The planner uses it to reject candidates, and the tests and
/// the sweep run it again on every final plan, so a layout that breaks a rule can never be returned.
/// </summary>
public static class ToiletChecker
{
    private const double Tol = 1.0 / 64;

    /// <summary>Pairs that may share plan area: the faucet sits on the basin, the basin on the vanity, the mirror above it.</summary>
    private static readonly HashSet<(string, string)> MayStack = new()
    {
        (Roles.Faucet, Roles.Basin), (Roles.Faucet, Roles.Vanity), (Roles.Basin, Roles.Vanity),
        (Roles.Mirror, Roles.Basin), (Roles.Mirror, Roles.Vanity), (Roles.SoapDispenser, Roles.Basin),
        (Roles.SoapDispenser, Roles.Vanity), (Roles.Mirror, Roles.Faucet), (Roles.SoapDispenser, Roles.Faucet),
    };

    /// <summary>Things that occupy the floor below them, so no wall item may hang over them.</summary>
    private static readonly HashSet<string> Occupiers = new() { Roles.Wc, Roles.Basin, Roles.Vanity };

    public static bool CanStack(string a, string b) => MayStack.Contains((a, b)) || MayStack.Contains((b, a));

    /// <summary>Flush with the floor (floor trap): can be walked over, but nothing may stand on it.</summary>
    public static bool IsFlush(PlacedItem i) => i.Role == Roles.FloorTrap || (i.Mount == Mount.Floor && i.Height <= 1.0 + 1e-9);

    public static List<Violation> Check(ToiletPlan plan, ToiletRules r, IEnumerable<string>? required = null)
    {
        var v = new List<Violation>();
        var spec = plan.Spec;
        var items = plan.Items;
        foreach (var role in required ?? Array.Empty<string>())
            if (!items.Any(i => i.Role == role)) v.Add(new("missing fixture", $"no place found for the {Label(role)}"));
        if (plan.Door is null) { v.Add(new("door", "the door does not fit on its wall")); return v; }
        CheckItems(spec, plan.Door, items, r, v, items.Count);
        return v;
    }

    /// <summary>
    /// Checks the last <paramref name="newCount"/> items of the list: their own rules, every pair that includes one
    /// of them, and the clear zones they could block or own. With newCount = items.Count this checks everything.
    /// </summary>
    internal static void CheckItems(ToiletSpec spec, DoorGeometry door, List<PlacedItem> items, ToiletRules r, List<Violation> v, int newCount)
    {
        int first = items.Count - newCount;
        for (int k = first; k < items.Count; k++) CheckOne(spec, door, items[k], items, r, v);
        for (int j = first; j < items.Count; j++)
            for (int i = 0; i < j; i++)
                CheckPair(items[i], items[j], r, v);

        for (int o = 0; o < items.Count; o++)
        {
            var owner = items[o];
            if (owner.Role is not (Roles.Wc or Roles.Basin)) continue;
            var zone = ClearZoneOf(spec, owner, items, r);
            if (zone is null) continue;
            if (o >= first && !RoomGeometry.Inside(spec, zone.Plan, Tol))
            {
                v.Add(new("clear space", $"the {zone.Name} does not fit in the room"));
                continue;
            }
            for (int k = (o >= first ? 0 : first); k < items.Count; k++)
            {
                var b = items[k];
                if (k == o || IsFlush(b) || b.Mount == Mount.Ceiling || CanStack(owner.Role, b.Role)) continue;
                bool blocks = b.Mount == Mount.Floor || Occupiers.Contains(b.Role) || b.Z0 < r.KneeHeight;
                if (blocks && RoomGeometry.Overlaps(zone.Plan, b.Plan, Tol))
                    v.Add(new("clear space", $"{Label(b.Role)} is in the {zone.Name}"));
            }
        }
    }

    private static void CheckOne(ToiletSpec spec, DoorGeometry door, PlacedItem a, List<PlacedItem> items, ToiletRules r, List<Violation> v)
    {
        if (!RoomGeometry.Inside(spec, a.Plan, Tol))
            v.Add(new("inside the room", $"{Label(a.Role)} sticks out of the room"));
        if (a.Mount != Mount.Ceiling && a.Z1 > spec.CeilingHeight + Tol)
            v.Add(new("ceiling height", $"{Label(a.Role)} reaches above the ceiling"));
        else if (a.Mount == Mount.Wall && a.Z1 > spec.CeilingHeight - r.CeilingClearance + Tol)
            v.Add(new("ceiling height", $"{Label(a.Role)} is closer than {r.CeilingClearance:0.#}\" to the ceiling"));

        CheckDoor(spec, door, a, r, v, "the door");
        if (spec.OtherDoors.Count > 0)
        {
            int n = 2;
            foreach (var other in RoomGeometry.OtherDoors(spec)) CheckDoor(spec, other, a, r, v, $"door {n++}");
        }

        if (a.Role == Roles.Wc) CheckWc(spec, a, r, v);
        if (a.Role == Roles.Basin) CheckBasin(spec, a, items, r, v);
    }

    /// <summary>A door's leaf sweep, the strip just inside it and its opening (with the frame margin) must stay clear.</summary>
    private static void CheckDoor(ToiletSpec spec, DoorGeometry door, PlacedItem a, ToiletRules r, List<Violation> v, string which)
    {
        bool belowHead = a.Mount != Mount.Ceiling && a.Z0 < door.Height - Tol;
        if (belowHead && !IsFlush(a) && RoomGeometry.HitsSwing(spec, door, a.Plan))
            v.Add(new("door swing", $"{Label(a.Role)} is in {which}'s swing"));
        if (((a.Mount == Mount.Floor && !IsFlush(a)) || Occupiers.Contains(a.Role)) && RoomGeometry.Overlaps(a.Plan, RoomGeometry.EntryZone(spec, door, r), Tol))
            v.Add(new("door entry", $"{Label(a.Role)} blocks the way in at {which}"));
        if (a.Wall == door.Wall && belowHead)
        {
            var wl = RoomGeometry.Wall(spec, door.Wall);
            double sa = wl.SOf(new Pt(a.Plan.X0, a.Plan.Y0)), sb = wl.SOf(new Pt(a.Plan.X1, a.Plan.Y1));
            double s0 = Math.Min(sa, sb), s1 = Math.Max(sa, sb);
            if (s0 < door.S1 + r.DoorFrameMargin - Tol && s1 > door.S0 - r.DoorFrameMargin + Tol)
                v.Add(new("door opening", $"{Label(a.Role)} is in {which}'s opening"));
        }
    }

    private static void CheckPair(PlacedItem a, PlacedItem b, ToiletRules r, List<Violation> v)
    {
        if (a.Mount == Mount.Ceiling && b.Mount == Mount.Ceiling)
        {
            if (RoomGeometry.Overlaps(a.Plan.Inflate(r.CeilingItemGap / 2), b.Plan.Inflate(r.CeilingItemGap / 2), Tol))
                v.Add(new("ceiling spacing", $"{Label(b.Role)} is closer than {r.CeilingItemGap}\" to the {Label(a.Role)}"));
            return;
        }
        if (CanStack(a.Role, b.Role)) return;
        bool zOverlap = a.Z0 < b.Z1 - Tol && b.Z0 < a.Z1 - Tol;
        if (zOverlap && RoomGeometry.Overlaps(a.Plan, b.Plan, Tol))
        {
            v.Add(new("overlap", $"{Label(b.Role)} overlaps the {Label(a.Role)}"));
            return;
        }
        bool aWall = a.Mount == Mount.Wall, bWall = b.Mount == Mount.Wall;
        if (aWall && bWall && zOverlap && a.Wall == b.Wall
            && RoomGeometry.Overlaps(a.Plan.Inflate(r.AccessoryGap / 2), b.Plan.Inflate(r.AccessoryGap / 2), Tol))
            v.Add(new("accessory gap", $"{Label(b.Role)} is closer than {r.AccessoryGap}\" to the {Label(a.Role)}"));
        var (hang, under) = aWall && Occupiers.Contains(b.Role) ? (a, b) : bWall && Occupiers.Contains(a.Role) ? (b, a) : (null, null);
        if (hang is not null && under is not null && hang.Role != Roles.Basin && RoomGeometry.Overlaps(hang.Plan, under.Plan, Tol))
            v.Add(new("over a fixture", $"{Label(hang.Role)} hangs over the {Label(under.Role)}"));
    }

    public static ClearZone? ClearZoneOf(ToiletSpec spec, PlacedItem owner, List<PlacedItem> items, ToiletRules r)
    {
        if (owner.Wall is null) return null;
        var wl = RoomGeometry.Wall(spec, owner.Wall.Value);
        if (owner.Role == Roles.Wc)
            return new ClearZone("clear space in front of the WC", wl.Rect(owner.S, Math.Max(r.WcClearWidth, owner.Width), owner.Depth, r.WcFrontClear), Roles.Wc);
        if (owner.Role == Roles.Basin)
        {
            double front = owner.Depth;
            var vanity = items.FirstOrDefault(i => i.Role == Roles.Vanity);
            if (vanity is not null && vanity.Wall == owner.Wall) front = Math.Max(front, vanity.Depth);
            return new ClearZone("clear space in front of the basin", wl.Rect(owner.S, owner.Width, front, r.BasinFrontClear), Roles.Basin);
        }
        return null;
    }

    private static void CheckWc(ToiletSpec spec, PlacedItem wc, ToiletRules r, List<Violation> v)
    {
        if (wc.Wall is null) { v.Add(new("WC", "the WC must stand against a wall")); return; }
        var wl = RoomGeometry.Wall(spec, wc.Wall.Value);
        double toSide = Math.Min(wc.S, wl.Length - wc.S);
        if (Math.Abs(toSide - r.WcToSideWall) > Tol)
            v.Add(new("WC centre-line", $"WC centre-line is {toSide:0.##}\" from the side wall, rule is {r.WcToSideWall}\""));
        if (Math.Abs(wc.BackOffset) > Tol) v.Add(new("WC", "the WC tank must be against the wall"));
    }

    private static void CheckBasin(ToiletSpec spec, PlacedItem basin, List<PlacedItem> items, ToiletRules r, List<Violation> v)
    {
        if (basin.Wall is null) { v.Add(new("basin", "the basin must be on a wall")); return; }
        var wl = RoomGeometry.Wall(spec, basin.Wall.Value);
        double toSide = Math.Min(basin.S, wl.Length - basin.S);
        double rule = spec.Basin == BasinKind.Vanity ? r.VanityWidth / 2 : r.BasinToSideWall;
        if (Math.Abs(toSide - rule) > Tol)
            v.Add(new("basin centre-line", $"basin centre-line is {toSide:0.##}\" from the side wall, rule is {rule}\""));
        var wc = items.FirstOrDefault(i => i.Role == Roles.Wc);
        if (wc is not null && wc.Wall == basin.Wall && Math.Abs(wc.S - basin.S) < r.WcBasinMinSpacing - Tol)
            v.Add(new("WC to basin", $"WC and basin centre-lines are {Math.Abs(wc.S - basin.S):0.##}\" apart, minimum {r.WcBasinMinSpacing}\""));
    }

    public static string Label(string role) => role switch
    {
        Roles.Wc => "WC",
        Roles.FloorTrap => "floor drain",
        Roles.TowelDispenser => "towel dispenser",
        Roles.TissueHolder => "tissue holder",
        Roles.MuslimShower => "Muslim shower",
        Roles.SoapDispenser => "soap dispenser",
        Roles.CeilingLight => "ceiling light",
        Roles.Exhaust => "exhaust fan",
        _ => role.Replace('_', ' '),
    };
}
