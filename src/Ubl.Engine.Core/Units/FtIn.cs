using System.Globalization;
using System.Text.RegularExpressions;

namespace Ubl.Engine.Core.Units;

/// <summary>Architectural feet-inches. All engine lengths are inches.</summary>
public static class FtIn
{
    /// <summary>Formats inches as 15'-11 1/2" rounded to the nearest half inch.</summary>
    public static string Format(double inches)
    {
        double v = Math.Abs(Math.Round(inches * 2, MidpointRounding.AwayFromZero) / 2);
        int ft = (int)Math.Floor(v / 12);
        double rest = v - ft * 12;
        int whole = (int)Math.Floor(rest);
        bool half = rest - whole >= 0.5;
        string sign = inches < 0 && v > 0 ? "-" : "";
        return $"{sign}{ft}'-{whole}{(half ? " 1/2" : "")}\"";
    }

    /// <summary>Formats a level such as +3'-0".</summary>
    public static string FormatLevel(double inches) => (inches < 0 ? "-" : "+") + Format(Math.Abs(inches));

    /// <summary>Parses 10'-8", +3'-0", 7'-11 1/2", 10" or 1'-0. Returns null when the text is not a length.</summary>
    public static double? Parse(string s)
    {
        s = s.Replace(" ", "").Replace("′", "'").Replace("″", "\"");
        var m = FeetInches().Match(s);
        if (m.Success)
        {
            double v = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 12;
            if (m.Groups[3].Success) v += double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (m.Groups[4].Success) v += double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) / double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
            return m.Groups[1].Value == "-" ? -v : v;
        }
        m = InchesOnly().Match(s);
        if (m.Success)
        {
            double v = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return m.Groups[1].Value == "-" ? -v : v;
        }
        return null;
    }

    private static readonly Regex FeetInchesRx = new("^([+-]?)(\\d+)'-?(\\d+(?:\\.\\d+)?)?(?:(\\d)/(\\d))?\"?$", RegexOptions.CultureInvariant);
    private static Regex FeetInches() => FeetInchesRx;

    private static readonly Regex InchesOnlyRx = new("^([+-]?)(\\d+(?:\\.\\d+)?)\"$", RegexOptions.CultureInvariant);
    private static Regex InchesOnly() => InchesOnlyRx;
}
