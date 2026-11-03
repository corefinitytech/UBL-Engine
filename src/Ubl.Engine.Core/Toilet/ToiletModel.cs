using Ubl.Engine.Core.Geometry;

namespace Ubl.Engine.Core.Toilet;

// All lengths are inches. The room frame has its origin at the inside south-west corner, x east, y north,
// so the inside of the room is [0, Width] x [0, Depth]. Wall faces are the inside finished faces.

/// <summary>Room walls, named by the side of the room they bound.</summary>
public enum Side { South, East, North, West }

/// <summary>How a fixture is held: standing on the floor, fixed to a wall, on the ceiling, or on the basin.</summary>
public enum Mount { Floor, Wall, Ceiling, OnBasin }

public enum ToiletVariant { Male, Female, Pwd }
public enum BasinKind { HalfPedestal, Vanity }
public enum HandDrying { Dryer, TowelDispenser, Both, None }
public enum DoorCorner { Start, End }

/// <summary>Role names. These are the keys used in the family cards and the reports.</summary>
public static class Roles
{
    public const string Wc = "wc";
    public const string Basin = "basin";
    public const string Vanity = "vanity";
    public const string Faucet = "faucet";
    public const string Mirror = "mirror";
    public const string TissueHolder = "tissue_holder";
    public const string MuslimShower = "muslim_shower";
    public const string SoapDispenser = "soap_dispenser";
    public const string Dryer = "dryer";
    public const string TowelDispenser = "towel_dispenser";
    public const string FloorTrap = "floor_trap";
    public const string Exhaust = "exhaust";
    public const string CeilingLight = "ceiling_light";

    public static readonly string[] All =
        { Wc, Basin, Vanity, Faucet, Mirror, TissueHolder, MuslimShower, SoapDispenser, Dryer, TowelDispenser, FloorTrap, Exhaust, CeilingLight };
}

/// <summary>
/// A fixture's real size, in its own frame: Width along the wall it backs onto, Depth out from the wall, Height up.
/// For items that do not back onto a wall (floor trap, ceiling items) Width is along x and Depth along y.
/// In Revit these come from measuring the family, not from a hand-written table.
/// </summary>
public sealed record FixtureSize(double Width, double Depth, double Height, Mount Mount);

/// <summary>Sizes per role. <see cref="Defaults"/> are typical sizes for running the planner without Revit.</summary>
public sealed class FixtureCatalog
{
    private readonly Dictionary<string, FixtureSize> _sizes;

    public FixtureCatalog(IDictionary<string, FixtureSize> sizes) => _sizes = new Dictionary<string, FixtureSize>(sizes);

    public FixtureSize this[string role] => _sizes[role];
    public bool Has(string role) => _sizes.ContainsKey(role);
    public IReadOnlyDictionary<string, FixtureSize> Sizes => _sizes;

    public FixtureCatalog With(string role, FixtureSize size)
    {
        var d = new Dictionary<string, FixtureSize>(_sizes) { [role] = size };
        return new FixtureCatalog(d);
    }

    public static FixtureCatalog Defaults() => new(new Dictionary<string, FixtureSize>
    {
        [Roles.Wc] = new(15, 27, 30, Mount.Floor),
        [Roles.Basin] = new(20, 16, 18, Mount.Wall),
        [Roles.Vanity] = new(36, 21, 32, Mount.Floor),
        [Roles.Faucet] = new(2.5, 6, 8, Mount.OnBasin),
        [Roles.Mirror] = new(36, 1, 36, Mount.Wall),
        [Roles.TissueHolder] = new(6, 5, 5, Mount.Wall),
        [Roles.MuslimShower] = new(4, 4, 8, Mount.Wall),
        [Roles.SoapDispenser] = new(5, 4, 8, Mount.Wall),
        [Roles.Dryer] = new(11, 7, 11, Mount.Wall),
        [Roles.TowelDispenser] = new(11, 5, 14, Mount.Wall),
        [Roles.FloorTrap] = new(6, 6, 1, Mount.Floor),
        [Roles.Exhaust] = new(10, 10, 6, Mount.Ceiling),
        [Roles.CeilingLight] = new(12, 12, 3, Mount.Ceiling),
    });
}

/// <summary>
/// Every rule value the planner uses, in one place. Source: W = Manawala drawings, D = the new-branch DWG,
/// M = UBL manual, E = engine default to confirm with the client.
/// </summary>
public sealed record ToiletRules
{
    public double WcToSideWall { get; init; } = 18;               // W, D
    public double BasinToSideWall { get; init; } = 18;            // W
    public double WcBasinMinSpacing { get; init; } = 30;          // E (Manawala has 39)
    public double WcFrontClear { get; init; } = 24;               // E
    public double WcClearWidth { get; init; } = 30;               // E
    public double BasinFrontClear { get; init; } = 18;            // E
    public double DoorEntryDepth { get; init; } = 12;             // E: floor kept clear just inside the door
    public double DoorOutEntryDepth { get; init; } = 18;          // E: same, when the door swings out
    public double DoorFrameMargin { get; init; } = 2;             // E: no wall item within this of the opening
    public double DoorHeadHeight { get; init; } = 84;             // W: D-03 is 2'-6" x 7'-0"
    public double TissueHolderOffset { get; init; } = 12;         // D: about 1'-0" from the WC centre-line
    public double MuslimShowerFromWcWall { get; init; } = 9;      // E
    public double AccessoryGap { get; init; } = 3;                // E: between items on a wall
    public double BasinRimHeight { get; init; } = 32;             // E
    public double FaucetBackFromWall { get; init; } = 2;          // E
    public double MirrorBottom { get; init; } = 42;               // E
    public double SoapBottom { get; init; } = 42;                 // E
    public double DryerBottom { get; init; } = 40;                // E
    public double TowelBottom { get; init; } = 42;                // E
    public double TissueHolderBottom { get; init; } = 24;         // E
    public double MuslimShowerBottom { get; init; } = 18;         // E
    public double KneeHeight { get; init; } = 27;                 // E: wall items lower than this block floor clearances
    public double FloorTrapFromWalls { get; init; } = 3;          // E: edge of the drain from both walls
    public double CeilingItemGap { get; init; } = 6;              // E
    public double ExhaustBelowCeiling { get; init; } = 2;         // E: top of a wall-type exhaust under the ceiling
    public double CeilingClearance { get; init; } = 2;            // E: wall fixtures keep this far below the ceiling
    public double VanityWidth { get; init; } = 36;                // D
    public double VanityDepth { get; init; } = 21;                // D

    // scoring: how the planner ranks the arrangements that pass every rule (E, tune with the client)
    public double ScoreOneWetWall { get; init; } = 30;            // WC and basin on one wall
    public double ScoreWcOnWetWall { get; init; } = 40;           // WC on a wall marked wet (soil stack, shaft, back-to-back)
    public double ScoreBasinOnWetWall { get; init; } = 5;
    public double ScoreWcFromDoor { get; init; } = 0.25;          // per inch between the door and the WC
    public double ScoreBasinToDoor { get; init; } = 0.10;         // per inch between the door and the basin (subtracted)
    public double ScoreSwingOverWcSpace { get; init; } = 10;      // subtracted when the door swings over the WC's clear space

    public static ToiletRules Default { get; } = new();
}

/// <summary>
/// The door. Offset is from the named corner of its wall to the near edge of the opening. HingeAtCorner puts the
/// hinge on the jamb nearer that corner (the usual case: the open leaf then lies against the side wall).
/// </summary>
public sealed record DoorSpec
{
    public Side Wall { get; init; } = Side.South;
    public DoorCorner FromCorner { get; init; } = DoorCorner.End;
    public double Offset { get; init; } = 4.5;
    public double Width { get; init; } = 30;
    public double Height { get; init; } = 84;
    public bool SwingIn { get; init; } = true;
    public bool HingeAtCorner { get; init; } = true;
}

/// <summary>Everything the planner needs to know about one toilet room.</summary>
public sealed record ToiletSpec
{
    public string Name { get; init; } = "TOILET";
    public ToiletVariant Variant { get; init; } = ToiletVariant.Male;
    public double Width { get; init; }                            // inside, along x (south and north walls)
    public double Depth { get; init; }                            // inside, along y (east and west walls)
    public double WallSouth { get; init; } = 4.5;
    public double WallEast { get; init; } = 4.5;
    public double WallNorth { get; init; } = 4.5;
    public double WallWest { get; init; } = 4.5;
    public double CeilingHeight { get; init; } = 84;             // M says 7'-0"; W shows 7'-2" and 8'-0": project record decides
    public double SlabHeight { get; init; } = 128;               // D: 10'-8"
    public DoorSpec Door { get; init; } = new();
    /// <summary>Further doors of an existing room. They are kept clear (swing, entry, opening) like the main door.</summary>
    public List<DoorSpec> OtherDoors { get; init; } = new();
    public BasinKind Basin { get; init; } = BasinKind.HalfPedestal;
    public HandDrying HandDrying { get; init; } = HandDrying.Dryer;
    /// <summary>Walls that back onto another wet room or a plumbing shaft: the planner prefers putting the WC there.</summary>
    public List<Side> WetWalls { get; init; } = new();
    /// <summary>The architect's choice, if any: the WC (or basin) goes in this corner and everything else is planned round it.</summary>
    public FixturePin? PinWc { get; init; }
    public FixturePin? PinBasin { get; init; }
    /// <summary>Accessories to leave out (for example when their family cannot be used); everything else is planned as normal.
    /// The WC and the basin cannot be left out.</summary>
    public List<string> Omit { get; init; } = new();

    public double WallThickness(Side s) => s switch
    {
        Side.South => WallSouth,
        Side.East => WallEast,
        Side.North => WallNorth,
        _ => WallWest,
    };
}

/// <summary>A fixture fixed by the architect to one wall, at the end of it nearest the named corner.</summary>
public sealed record FixturePin(Side Wall, DoorCorner End);

/// <summary>One fixture, placed. Plan is its floor-plan rectangle in room coordinates, Z0 its bottom above FFL.</summary>
public sealed record PlacedItem
{
    public string Role { get; init; } = "";
    public Mount Mount { get; init; }
    /// <summary>The wall it backs onto; null for items that do not (floor trap, ceiling items).</summary>
    public Side? Wall { get; init; }
    /// <summary>Centre-line position along the wall, measured from the wall's start corner (counter-clockwise).</summary>
    public double S { get; init; }
    /// <summary>Gap between the wall face and the back of the item (0 for items fixed to the wall).</summary>
    public double BackOffset { get; init; }
    public double Width { get; init; }
    public double Depth { get; init; }
    public double Height { get; init; }
    public double Z0 { get; init; }
    public double Z1 => Z0 + Height;
    public Box Plan { get; init; }
    /// <summary>Unit vector pointing out of the front of the item into the room.</summary>
    public Pt Facing { get; init; }
    /// <summary>Centre of the item's back face in plan (for free items: its centre). This is what Revit places.</summary>
    public Pt Anchor { get; init; }
    /// <summary>Plain-language reason it sits where it does.</summary>
    public string Rule { get; init; } = "";
}

/// <summary>An area that must stay clear (door swing, entry, space in front of a fixture).</summary>
public sealed record ClearZone(string Name, Box Plan, string Owner);

public sealed record DoorGeometry(Side Wall, double S0, double S1, double HingeS, bool SwingIn, double Width, double Height)
{
    public double LatchS => HingeS == S0 ? S1 : S0;
}

/// <summary>The planner's answer: a placed toilet, or a refusal with the rule that failed and what would pass.</summary>
public sealed record ToiletPlan
{
    public ToiletSpec Spec { get; init; } = new();
    public bool Refused { get; init; }
    public string? RefusalRule { get; init; }
    public string? RefusalReason { get; init; }
    public string? WouldPass { get; init; }
    public DoorGeometry? Door { get; init; }
    public List<PlacedItem> Items { get; init; } = new();
    public List<ClearZone> Zones { get; init; } = new();
    public string Arrangement { get; init; } = "";
    public double Score { get; init; }
    public int CandidatesTried { get; init; }
    public int CandidatesValid { get; init; }
    public List<string> Log { get; init; } = new();

    public PlacedItem? Item(string role) => Items.FirstOrDefault(i => i.Role == role);
}
