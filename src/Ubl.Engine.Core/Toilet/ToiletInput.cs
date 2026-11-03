using System.Text.Json;
using Ubl.Engine.Core.Units;

namespace Ubl.Engine.Core.Toilet;

/// <summary>
/// Reads a toilet from a small JSON file. Lengths may be numbers (inches) or text such as "5'-0\"".
/// <code>
/// {
///   "name": "MALE TOILET", "variant": "male",
///   "width": "5'-0\"", "depth": "6'-3\"",
///   "walls": { "south": 4.5, "east": 4.5, "north": 4.5, "west": 4.5 },
///   "ceiling_height": "7'-0\"",
///   "door": { "wall": "south", "from_corner": "east", "offset": 4.5, "width": "2'-6\"", "height": "7'-0\"", "swing": "in", "hinge": "corner" },
///   "basin": "half_pedestal", "hand_drying": "dryer", "wet_walls": ["west"],
///   "pin_wc": { "wall": "west", "corner": "north" }      (optional: the architect's choice)
/// }
/// </code>
/// The room frame: x runs west to east along the south wall, y south to north. "from_corner" names the end of the
/// door's wall the offset is measured from, as a compass direction (for the south wall: "west" or "east").
/// </summary>
public static class ToiletInput
{
    public static ToiletSpec Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var spec = new ToiletSpec
        {
            Name = Str(root, "name") ?? "TOILET",
            Variant = Enum<ToiletVariant>(root, "variant") ?? ToiletVariant.Male,
            Width = Len(root, "width") ?? throw new FormatException("\"width\" is required"),
            Depth = Len(root, "depth") ?? throw new FormatException("\"depth\" is required"),
            CeilingHeight = Len(root, "ceiling_height") ?? 84,
            SlabHeight = Len(root, "slab_height") ?? 128,
            Basin = Enum<BasinKind>(root, "basin") ?? BasinKind.HalfPedestal,
            HandDrying = Enum<HandDrying>(root, "hand_drying") ?? HandDrying.Dryer,
        };
        if (root.TryGetProperty("walls", out var walls))
        {
            if (walls.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            {
                double t = LenOf(walls) ?? 4.5;
                spec = spec with { WallSouth = t, WallEast = t, WallNorth = t, WallWest = t };
            }
            else
                spec = spec with
                {
                    WallSouth = Len(walls, "south") ?? 4.5,
                    WallEast = Len(walls, "east") ?? 4.5,
                    WallNorth = Len(walls, "north") ?? 4.5,
                    WallWest = Len(walls, "west") ?? 4.5,
                };
        }
        if (root.TryGetProperty("wet_walls", out var wet) && wet.ValueKind == JsonValueKind.Array)
            spec = spec with { WetWalls = wet.EnumerateArray().Select(e => ParseSide(e.GetString())).ToList() };
        spec = spec with { PinWc = Pin(root, "pin_wc"), PinBasin = Pin(root, "pin_basin") };
        if (root.TryGetProperty("door", out var d))
        {
            var side = ParseSide(Str(d, "wall") ?? "south");
            string from = (Str(d, "from_corner") ?? DefaultCorner(side)).ToLowerInvariant();
            spec = spec with
            {
                Door = new DoorSpec
                {
                    Wall = side,
                    FromCorner = CornerOf(side, from),
                    Offset = Len(d, "offset") ?? 4.5,
                    Width = Len(d, "width") ?? 30,
                    Height = Len(d, "height") ?? 84,
                    SwingIn = !string.Equals(Str(d, "swing"), "out", StringComparison.OrdinalIgnoreCase),
                    HingeAtCorner = !string.Equals(Str(d, "hinge"), "far", StringComparison.OrdinalIgnoreCase),
                },
            };
        }
        return spec;
    }

    /// <summary>"pin_wc": { "wall": "east", "corner": "north" } puts the WC on the east wall at its north end.</summary>
    private static FixturePin? Pin(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Object) return null;
        var side = ParseSide(Str(p, "wall"));
        return new FixturePin(side, CornerOf(side, (Str(p, "corner") ?? "").ToLowerInvariant()));
    }

    /// <summary>The compass name of a door wall's end.</summary>
    public static DoorCorner CornerOf(Side wall, string compass) => (wall, compass) switch
    {
        (Side.South, "west") or (Side.East, "south") or (Side.North, "east") or (Side.West, "north") => DoorCorner.Start,
        (Side.South, "east") or (Side.East, "north") or (Side.North, "west") or (Side.West, "south") => DoorCorner.End,
        _ => throw new FormatException($"\"from_corner\": \"{compass}\" is not an end of the {RoomGeometry.Name(wall)} wall"),
    };

    public static string CompassOf(Side wall, DoorCorner corner) => ToiletPlanner.EndName(wall, corner);

    private static string DefaultCorner(Side s) => s is Side.South or Side.North ? "east" : "north";

    public static Side ParseSide(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "south" or "s" => Side.South,
        "east" or "e" => Side.East,
        "north" or "n" => Side.North,
        "west" or "w" => Side.West,
        _ => throw new FormatException($"\"{s}\" is not a wall (south, east, north, west)"),
    };

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Len(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? LenOf(v) : null;

    private static double? LenOf(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.String => FtIn.Parse(v.GetString() ?? "") ?? throw new FormatException($"\"{v.GetString()}\" is not a length"),
        _ => null,
    };

    private static T? Enum<T>(JsonElement e, string name) where T : struct, System.Enum
    {
        var s = Str(e, name);
        if (s is null) return null;
        string norm = s.Replace("_", "").Replace("-", "").Replace(" ", "");
        return System.Enum.TryParse<T>(norm, true, out var v) ? v : throw new FormatException($"\"{name}\": \"{s}\" is not one of {string.Join(", ", System.Enum.GetNames<T>())}");
    }
}
