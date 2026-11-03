using System.IO;
using System.Text.Json;
using Ubl.Engine.Core.Json;

namespace Ubl.Engine.Revit.Config;

/// <summary>
/// Firm-level settings: which families, types and templates the engine uses. Lives in engine.config.json
/// beside the add-in so the BIM lead can change it without a rebuild.
/// </summary>
public sealed class EngineConfig
{
    public string ColumnFamily { get; set; } = "Concrete-Rectangular-Column";
    public string? ColumnFamilyPath { get; set; }
    public string ColumnWidthParam { get; set; } = "b";
    public string ColumnDepthParam { get; set; } = "h";

    public string BeamFamily { get; set; } = "Concrete-Rectangular Beam";
    public string? BeamFamilyPath { get; set; }
    public string BeamWidthParam { get; set; } = "b";
    public string BeamDepthParam { get; set; } = "h";

    public string? BaseWallType { get; set; }
    public string BrickMaterial { get; set; } = "Masonry - Brick";
    public string ConcreteMaterial { get; set; } = "Concrete - Cast-in-Place Concrete";
    public string? BaseFloorType { get; set; }

    /// <summary>Roof slab thickness (inches). Not in the drawing: our default, flagged in the report.</summary>
    public double SlabThickness { get; set; } = 6;
    /// <summary>Ground floor slab thickness (inches), our default.</summary>
    public double FloorThickness { get; set; } = 4;
    /// <summary>True when "DROP BEAM 10"" means 10" below the slab soffit, so beam depth = slab + drop.</summary>
    public bool BeamNoteIsDropBelowSlab { get; set; } = true;
    /// <summary>Yard boundary wall height (inches), our default when the drawing gives none.</summary>
    public double YardWallHeight { get; set; } = 84;
    /// <summary>Used only when the drawing has no SLAB HEIGHT note.</summary>
    public double DefaultSlabHeight { get; set; } = 120;
    /// <summary>Used only when the drawing has no LEVEL note inside the building.</summary>
    public double DefaultFloorLevel { get; set; } = 0;

    public string FloorLevelName { get; set; } = "GF FFL";
    public string SlabLevelName { get; set; } = "GF SLAB";
    public string ExistingPhase { get; set; } = "Existing";

    public string? ViewTemplate { get; set; } = "UBL - As Built Plan";
    public string ViewName { get; set; } = "AR-21 AS BUILT LAYOUT PLAN (GROUND FLOOR)";
    public string SheetNumber { get; set; } = "AR-21";
    public string SheetName { get; set; } = "AS BUILT LAYOUT PLAN (GROUND FLOOR)";
    public string? TitleBlockFamily { get; set; } = "UBL A3 Tender";
    public string BeamLabel { get; set; } = "DROP BEAM {drop}";

    /// <summary>Text types the engine creates if missing: name, height in mm.</summary>
    public string TextFont { get; set; } = "Arial";

    /// <summary>Marker written to Comments on every element the engine creates, so a re-run can replace them.</summary>
    public string Marker { get; set; } = "UBL-ENGINE:SHELL";

    public static EngineConfig Load(string path)
    {
        if (!File.Exists(path)) return new EngineConfig();
        return JsonSerializer.Deserialize<EngineConfig>(File.ReadAllText(path), EngineJson.Options) ?? new EngineConfig();
    }
}
