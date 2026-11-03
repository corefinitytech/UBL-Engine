namespace Ubl.Engine.Core.Sheets;

public sealed record ArchScale(int Denominator, string Name)
{
    /// <summary>Paper millimetres per model inch.</summary>
    public double MmPerInch => 25.4 / Denominator;
}

/// <summary>Sheet geometry in paper millimetres, measured from the Manawala A3 tender sheets.</summary>
public sealed record SheetFormat(string Name, double Width, double Height, double InnerX0, double InnerY0, double InnerX1, double InnerY1, double TitleBlockTop)
{
    public static SheetFormat A3Portrait { get; } = new("A3", 297, 420, 14, 18.5, 282.5, 399, 48.5);

    public double DrawWidth => InnerX1 - InnerX0;
    public double DrawHeight => InnerY1 - TitleBlockTop;
    public (double X, double Y) DrawCenter => ((InnerX0 + InnerX1) / 2, (TitleBlockTop + InnerY1) / 2);
}

public static class ScalePicker
{
    public static IReadOnlyList<ArchScale> Standard { get; } = new[]
    {
        new ArchScale(48, "1/4\" = 1'-0\""),
        new ArchScale(64, "3/16\" = 1'-0\""),
        new ArchScale(96, "1/8\" = 1'-0\""),
        new ArchScale(128, "3/32\" = 1'-0\""),
        new ArchScale(192, "1/16\" = 1'-0\""),
    };

    /// <summary>
    /// Largest standard scale at which the plan plus its annotation margins fits the drawing area.
    /// Margins (mm) cover dimension tiers, neighbour labels, the parking and road band and the area note.
    /// Returns null when nothing fits, which means a bigger sheet or a split plan, never a squeezed drawing.
    /// </summary>
    public static ArchScale? Pick(double planWidthIn, double planHeightIn, SheetFormat sheet,
                                  double sideMarginsMm = 80, double verticalMarginsMm = 80)
    {
        foreach (var s in Standard)
        {
            double w = planWidthIn * s.MmPerInch + sideMarginsMm;
            double h = planHeightIn * s.MmPerInch + verticalMarginsMm;
            if (w <= sheet.DrawWidth && h <= sheet.DrawHeight) return s;
        }
        return null;
    }
}
