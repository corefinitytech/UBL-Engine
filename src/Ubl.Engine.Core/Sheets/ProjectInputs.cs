using System.Text.Json;
using Ubl.Engine.Core.Json;

namespace Ubl.Engine.Core.Sheets;

/// <summary>
/// Facts the drawing does not contain and the engine must never invent: road, neighbours, north, branch name.
/// Read from project.json next to the DWG. Missing values print as red INPUT REQUIRED placeholders.
/// </summary>
public sealed class ProjectInputs
{
    public string? BranchName { get; set; }
    public string? Address { get; set; }
    public string? RoadName { get; set; }
    public string? NeighbourLeft { get; set; }
    public string? NeighbourRight { get; set; }
    public string? NeighbourRear { get; set; }
    /// <summary>Angle of north measured anticlockwise from drawing +Y, in degrees. Null when unknown.</summary>
    public double? NorthDegrees { get; set; }
    public string? ProjectNumber { get; set; }
    public string Client { get; set; } = "UNITED BANK LIMITED";
    public string Status { get; set; } = "ENGINE PREVIEW";
    public string DrawnBy { get; set; } = "ENGINE";
    public string? Date { get; set; }

    public static ProjectInputs LoadOrDefault(string? path)
    {
        if (path is null || !File.Exists(path)) return new ProjectInputs();
        return JsonSerializer.Deserialize<ProjectInputs>(File.ReadAllText(path), EngineJson.Options) ?? new ProjectInputs();
    }
}
