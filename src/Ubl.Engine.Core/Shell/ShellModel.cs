using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Core.Units;

namespace Ubl.Engine.Core.Shell;

// The existing building shell, as recognised from the client's drawing. This is the contract between the
// recogniser and every consumer (Revit builder, sheet builder, CLI JSON). Units: inches, drawing coordinates.

public enum Face { Left, Right, Rear, Front }

public sealed record Column(Box Bounds)
{
    public double Width => Bounds.Width;
    public double Depth => Bounds.Height;
    /// <summary>Tag text, e.g. 18"X25" (width along X first, as the architect writes it).</summary>
    public string Size => $"{Width:0.#}\"X{Depth:0.#}\"";
}

/// <summary>A boundary wall of the covered area, from one hatch loop. Pieces are its rectangles (an L gives two).</summary>
public sealed record Wall(IReadOnlyList<Pt> Outline, Box Bounds, double Thickness, Face Face, IReadOnlyList<Box> Pieces);

/// <summary>A wall of the open yard (rear courtyard, low boundary wall), from a pair of parallel lines.</summary>
public sealed record YardWall(Box Bounds, double Thickness);

/// <summary>Existing RCC core (lift or stair shaft). Shaft is the clear inside, from the cross drawn in it.</summary>
public sealed record Core(IReadOnlyList<Pt> Outline, Box Bounds, Box? Shaft, string Label, IReadOnlyList<Box> Pieces);

/// <summary>Drop beam on its centre line. Drop is how far it projects below the slab, read from the beam note.</summary>
public sealed record Beam(Pt P0, Pt P1, double Width, double? Drop)
{
    public double Length => Pt.Dist(P0, P1);
    public bool IsVertical => Math.Abs(P1.X - P0.X) < Math.Abs(P1.Y - P0.Y);
}

public sealed record LevelMark(double Value, string Text, Pt At, bool Inside);

public enum StairKind { Flight, Ramp, Spiral }

public sealed record Stair
{
    public StairKind Kind { get; init; }
    public Box Bounds { get; init; }
    /// <summary>'x' when the treads are vertical lines (you walk along X), 'y' when they are horizontal.</summary>
    public char RunAxis { get; init; }
    public double Width { get; init; }
    public double Going { get; init; }
    public int Treads { get; init; }
    public Pt? Center { get; init; }
    public double Radius { get; init; }
    public string? Label { get; init; }
    public Pt? LabelAt { get; init; }
    /// <summary>"up" or "down" from the label; the walking line starts at the end nearest the label.</summary>
    public string? Direction { get; init; }
    /// <summary>Index of the labelled flight this one belongs to (second flight of a dog-leg or L stair).</summary>
    public int? SameStairAs { get; init; }
}

public sealed record Envelope(double X0, double X1, double YRear, double YFront, double YFrontAll, double? YPlotRear)
{
    public double Width => X1 - X0;
    public double Depth => YRear - YFront;
    public double CoveredAreaSft => Width * Depth / 144.0;
    public double? StatedAreaSft { get; init; }
    public Box Covered => new(X0, YFront, X1, YRear);
}

public enum CheckStatus { Pass, Info, Warn, Fail }

public sealed record Check(string Name, CheckStatus Status, string Detail);

public sealed record Unclassified(string Kind, Box Bounds, IReadOnlyList<string> Labels);

public sealed class ShellModel
{
    public string Units => "inch";
    public required string Source { get; init; }
    public int EntityCount { get; init; }
    public required Envelope Envelope { get; init; }
    public required IReadOnlyList<Column> Columns { get; init; }
    public required IReadOnlyList<Wall> Walls { get; init; }
    public double WallThickness { get; init; }
    public required IReadOnlyList<YardWall> YardWalls { get; init; }
    public required IReadOnlyList<Core> Cores { get; init; }
    public required IReadOnlyList<Beam> Beams { get; init; }
    public double? BeamDrop { get; init; }
    /// <summary>Slab height above finished floor, from the "SLAB HEIGHT" note.</summary>
    public double? SlabHeight { get; init; }
    public required IReadOnlyList<LevelMark> Levels { get; init; }
    /// <summary>North from the drawing's compass (N, S, E, W letters), anticlockwise from drawing +Y, in degrees.</summary>
    public double? NorthDegrees { get; init; }
    public Pt? NorthAt { get; init; }
    public required IReadOnlyList<Stair> Stairs { get; init; }
    /// <summary>Column sizes and bay lengths down the left wall, rear to front.</summary>
    public required IReadOnlyList<double> LeftBayChain { get; init; }
    public required IReadOnlyList<Check> Checks { get; init; }
    public required IReadOnlyList<Unclassified> Unclassified { get; init; }
    public required IReadOnlyList<string> MissingInputs { get; init; }
    public required IReadOnlyList<string> SourceTexts { get; init; }

    public double? FloorLevel => Levels.Where(l => l.Inside).Select(l => (double?)l.Value).FirstOrDefault();

    public string Summary()
    {
        var e = Envelope;
        var sizes = string.Join(", ", Columns.GroupBy(c => c.Size).Select(g => $"{g.Count()} x {g.Key}"));
        return $"{FtIn.Format(e.Width)} x {FtIn.Format(e.Depth)} covered ({e.CoveredAreaSft:N0} sft); " +
               $"columns {sizes}; {Walls.Count} boundary walls {WallThickness:0.#}\"; {Beams.Count} beams; " +
               $"{Cores.Count} core(s); {Stairs.Count(s => s.Kind != StairKind.Ramp)} stair element(s)";
    }
}

public sealed class RecognitionException(string message) : Exception(message);
