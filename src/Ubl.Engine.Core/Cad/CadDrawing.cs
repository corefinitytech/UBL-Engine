using System.Text.Json;
using Ubl.Engine.Core.Geometry;

namespace Ubl.Engine.Core.Cad;

// Reader-neutral drawing model. The recogniser only sees these types, so the DWG library can be swapped
// (ACadSharp today, ODA or a Revit import tomorrow) without touching the rules. Units are inches.

public sealed record CadLine(string Layer, Pt A, Pt B)
{
    public double Length => Pt.Dist(A, B);
    public bool IsHorizontal(double tol) => Math.Abs(A.Y - B.Y) < tol;
    public bool IsVertical(double tol) => Math.Abs(A.X - B.X) < tol;
}

public sealed record CadCircle(string Layer, Pt Center, double Radius);

/// <summary>A hatch reduced to its boundary loops (arcs are chorded by the reader).</summary>
public sealed record CadHatch(string Layer, string Pattern, IReadOnlyList<IReadOnlyList<Pt>> Loops);

public sealed record CadText(string Layer, Pt Insert, string Text);

public sealed record CadDimension(string Layer, double Measurement);

public sealed class CadDrawing
{
    public string Source { get; init; } = "";
    public int EntityCount { get; set; }
    public List<CadLine> Lines { get; } = new();
    public List<CadCircle> Circles { get; } = new();
    public List<CadHatch> Hatches { get; } = new();
    public List<CadText> Texts { get; } = new();
    public List<CadDimension> Dimensions { get; } = new();
    public List<string> ReaderWarnings { get; } = new();
}

public enum LayerKind { Column, WallHatch, WallLines, Beam, Stair }

/// <summary>
/// Which source layers mean what. Each client and draughtsman names layers differently, so this is data, not code:
/// extend the JSON file when a new drawing arrives instead of changing the recogniser.
/// </summary>
public sealed class LayerAliases
{
    private readonly Dictionary<LayerKind, HashSet<string>> _map;

    public LayerAliases(Dictionary<LayerKind, HashSet<string>> map)
    {
        _map = map.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase));
    }

    public bool Is(string layer, LayerKind kind) => _map.TryGetValue(kind, out var set) && set.Contains(layer.Trim());

    public static LayerAliases Default { get; } = new(new()
    {
        [LayerKind.Column] = new() { "COL", "COLUMN", "COLUMNS", "S-COLS", "S-COLUMN" },
        [LayerKind.WallHatch] = new() { "WALL", "A-WALL", "A-WALL-PATT", "A-WALL-EXST" },
        [LayerKind.WallLines] = new() { "WALLS", "L.H.W", "A-WALL", "BOUNDARY", "A-WALL-EXST" },
        [LayerKind.Beam] = new() { "BEAM", "BEAMS", "S-BEAM" },
        [LayerKind.Stair] = new() { "STAIR STEP", "STAIR", "STAIRS", "A-FLOR-STRS", "MATEL" },
    });

    /// <summary>Loads {"Column": ["COL", ...], "Beam": [...]} from a JSON file.</summary>
    public static LayerAliases Load(string path)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path))
                  ?? throw new InvalidDataException($"Empty layer alias file {path}");
        var map = new Dictionary<LayerKind, HashSet<string>>();
        foreach (var (k, v) in raw)
            map[Enum.Parse<LayerKind>(k, ignoreCase: true)] = new HashSet<string>(v, StringComparer.OrdinalIgnoreCase);
        return new LayerAliases(map);
    }
}
