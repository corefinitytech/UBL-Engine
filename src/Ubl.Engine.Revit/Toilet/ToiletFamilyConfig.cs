using System.IO;
using System.Text.Json;
using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Core.Json;
using Ubl.Engine.Core.Toilet;

namespace Ubl.Engine.Revit.Toilet;

/// <summary>
/// One family per role, and the one thing about it the engine cannot measure: which side is its front.
/// Size, insertion point and height are measured from the family itself every time it is used.
/// </summary>
public sealed class FamilyCard
{
    public string Family { get; set; } = "";
    /// <summary>Type name; the family's first type when not set.</summary>
    public string? Type { get; set; }
    /// <summary>The family's own axis its usable front faces: "-Y" (Revit convention, the side seen in the Front view), "+Y", "-X" or "+X".</summary>
    public string Front { get; set; } = "-Y";
    /// <summary>Optional: "ceiling" or "wall" for the exhaust and light; worked out from the family's shape when not set.</summary>
    public string? Mount { get; set; }

    public Pt FrontLocal => PlacementMath.Front(Front);

    public bool FrontIsY => Math.Abs(FrontLocal.Y) > 0.5;
}

/// <summary>toilet.families.json beside the add-in. The BIM lead edits it; no rebuild needed.</summary>
public sealed class ToiletFamilyConfig
{
    /// <summary>Folder the .rfa files are loaded from when they are not in the project yet. Asked for when not set.</summary>
    public string? FamiliesFolder { get; set; }
    public Dictionary<string, FamilyCard> Roles { get; set; } = DefaultRoles();
    public string DoorFamily { get; set; } = "Single-Flush";
    public string DoorTypeName { get; set; } = "D-03";
    public double FloorFinishThickness { get; set; } = 1;
    public string Marker { get; set; } = "UBL-ENGINE:TOILET";
    /// <summary>Rule values (mounting heights, clearances, scoring) overriding the engine defaults. Snake_case names as in ToiletRules.</summary>
    public JsonElement? Rules { get; set; }

    /// <summary>Drawing rules (tile size, sheet numbering, note texts, floor name) overriding the engine defaults. Snake_case names as in DrawingRules.</summary>
    public JsonElement? Drawings { get; set; }

    public DrawingRules DrawingsOrDefault()
    {
        if (Drawings is not { ValueKind: JsonValueKind.Object } d) return DrawingRules.Default;
        return JsonSerializer.Deserialize<DrawingRules>(d.GetRawText(), EngineJson.Options) ?? DrawingRules.Default;
    }

    public ToiletRules RulesOrDefault()
    {
        if (Rules is not { ValueKind: JsonValueKind.Object } r) return ToiletRules.Default;
        return JsonSerializer.Deserialize<ToiletRules>(r.GetRawText(), EngineJson.Options) ?? ToiletRules.Default;
    }

    public static Dictionary<string, FamilyCard> DefaultRoles() => new()
    {
        [Ubl.Engine.Core.Toilet.Roles.Wc] = new() { Family = "WC" },
        [Ubl.Engine.Core.Toilet.Roles.Basin] = new() { Family = "Basin" },
        [Ubl.Engine.Core.Toilet.Roles.Faucet] = new() { Family = "Faucet" },
        [Ubl.Engine.Core.Toilet.Roles.Mirror] = new() { Family = "Mirror" },
        [Ubl.Engine.Core.Toilet.Roles.TissueHolder] = new() { Family = "Tissue Holder" },
        [Ubl.Engine.Core.Toilet.Roles.MuslimShower] = new() { Family = "Muslim Shower" },
        [Ubl.Engine.Core.Toilet.Roles.SoapDispenser] = new() { Family = "Soap Dispenser" },
        [Ubl.Engine.Core.Toilet.Roles.Dryer] = new() { Family = "Dryer" },
        [Ubl.Engine.Core.Toilet.Roles.TowelDispenser] = new() { Family = "Tissue Dispenser" },
        [Ubl.Engine.Core.Toilet.Roles.FloorTrap] = new() { Family = "Floor Trap" },
        [Ubl.Engine.Core.Toilet.Roles.Exhaust] = new() { Family = "Exhaust" },
        [Ubl.Engine.Core.Toilet.Roles.CeilingLight] = new() { Family = "Ceiling Light" },
    };

    public static ToiletFamilyConfig Load(string path)
    {
        if (!File.Exists(path)) return new ToiletFamilyConfig();
        var cfg = JsonSerializer.Deserialize<ToiletFamilyConfig>(File.ReadAllText(path), EngineJson.Options) ?? new ToiletFamilyConfig();
        foreach (var (role, card) in DefaultRoles())
            cfg.Roles.TryAdd(role, card);
        return cfg;
    }
}
