using System.Globalization;
using System.Text;
using Ubl.Engine.Core.Geometry;
using Ubl.Engine.Core.Units;

namespace Ubl.Engine.Core.Toilet;

/// <summary>Draws a toilet plan as SVG: a quick visual check of the planner, no Revit needed.</summary>
public static class ToiletSvg
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Render(ToiletPlan plan, double scale = 5)
    {
        var (w, h) = Size(plan.Spec, scale);
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(w)}\" height=\"{F(h + 70)}\" viewBox=\"0 0 {F(w)} {F(h + 70)}\" font-family=\"Arial, sans-serif\">");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#fff\"/>");
        sb.Append(Group(plan, 0, 0, scale, caption: true));
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>A grid of plans, each with its caption, for checking many rooms at a glance.</summary>
    public static string ContactSheet(IReadOnlyList<ToiletPlan> plans, int columns = 5, double scale = 2.2, string? title = null)
    {
        double cellW = plans.Max(p => Size(p.Spec, scale).W) + 20, cellH = plans.Max(p => Size(p.Spec, scale).H) + 70;
        int rows = (plans.Count + columns - 1) / columns;
        double top = title is null ? 0 : 40;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(cellW * columns)}\" height=\"{F(cellH * rows + top)}\" font-family=\"Arial, sans-serif\">");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#fff\"/>");
        if (title is not null) sb.Append($"<text x=\"10\" y=\"26\" font-size=\"18\" font-weight=\"bold\">{Esc(title)}</text>");
        for (int i = 0; i < plans.Count; i++)
            sb.Append(Group(plans[i], (i % columns) * cellW + 10, top + (i / columns) * cellH + 10, scale, caption: true));
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static (double W, double H) Size(ToiletSpec s, double k) =>
        ((s.Width + s.WallWest + s.WallEast) * k + 2, (s.Depth + s.WallSouth + s.WallNorth) * k + 2);

    private static string Group(ToiletPlan plan, double ox, double oy, double k, bool caption)
    {
        var s = plan.Spec;
        double x0 = ox + s.WallWest * k + 1, yTop = oy + s.WallNorth * k + 1;
        string X(double x) => F(x0 + x * k);
        string Y(double y) => F(yTop + (s.Depth - y) * k);
        var sb = new StringBuilder();
        sb.Append("<g>");

        // walls (outer rectangle filled, room cut out), then the door gap
        sb.Append($"<rect x=\"{X(-s.WallWest)}\" y=\"{Y(s.Depth + s.WallNorth)}\" width=\"{F((s.Width + s.WallWest + s.WallEast) * k)}\" height=\"{F((s.Depth + s.WallSouth + s.WallNorth) * k)}\" fill=\"#9a9a9a\"/>");
        sb.Append($"<rect x=\"{X(0)}\" y=\"{Y(s.Depth)}\" width=\"{F(s.Width * k)}\" height=\"{F(s.Depth * k)}\" fill=\"{(plan.Refused ? "#fdecec" : "#ffffff")}\"/>");
        var door = plan.Door ?? RoomGeometry.Door(s);
        if (door is not null)
        {
            var wl = RoomGeometry.Wall(s, door.Wall);
            double t = s.WallThickness(door.Wall);
            var a = wl.At(door.S0, -t);
            var b = wl.At(door.S1, 0);
            var box = Box.FromCorners(a, b);
            sb.Append($"<rect x=\"{X(box.X0)}\" y=\"{Y(box.Y1)}\" width=\"{F(box.Width * k)}\" height=\"{F(box.Height * k)}\" fill=\"#fff\"/>");
            var hinge = wl.At(door.HingeS);
            var latch = wl.At(door.LatchS);
            var n = door.SwingIn ? wl.Normal : wl.Normal * -1;
            var open = hinge + n * door.Width;
            sb.Append($"<line x1=\"{X(hinge.X)}\" y1=\"{Y(hinge.Y)}\" x2=\"{X(open.X)}\" y2=\"{Y(open.Y)}\" stroke=\"#333\" stroke-width=\"1.5\"/>");
            // arc from the open leaf tip to the latch jamb
            int sweep = Sweep(hinge, open, latch);
            sb.Append($"<path d=\"M {X(open.X)} {Y(open.Y)} A {F(door.Width * k)} {F(door.Width * k)} 0 0 {sweep} {X(latch.X)} {Y(latch.Y)}\" fill=\"none\" stroke=\"#333\" stroke-width=\"0.8\" stroke-dasharray=\"3,2\"/>");
        }

        // clear zones
        foreach (var z in plan.Zones.Where(z => z.Owner != "door" || z.Name == "door entry"))
            sb.Append($"<rect x=\"{X(z.Plan.X0)}\" y=\"{Y(z.Plan.Y1)}\" width=\"{F(z.Plan.Width * k)}\" height=\"{F(z.Plan.Height * k)}\" fill=\"#2a7de1\" fill-opacity=\"0.06\" stroke=\"#2a7de1\" stroke-opacity=\"0.5\" stroke-width=\"0.6\" stroke-dasharray=\"4,3\"/>");

        // fixtures: floor items solid, wall items orange, ceiling items dashed
        foreach (var i in plan.Items.OrderBy(i => Layer(i)))
        {
            string fill = i.Mount switch
            {
                Mount.Floor => i.Role == Roles.FloorTrap ? "#cfe8cf" : "#d7e6f7",
                Mount.Wall => "#f6d7b0",
                Mount.OnBasin => "#b9c7d6",
                _ => "none",
            };
            string dash = i.Mount == Mount.Ceiling ? " stroke-dasharray=\"3,2\"" : "";
            var p = i.Plan;
            sb.Append($"<rect x=\"{X(p.X0)}\" y=\"{Y(p.Y1)}\" width=\"{F(p.Width * k)}\" height=\"{F(p.Height * k)}\" fill=\"{fill}\" stroke=\"#2b2b2b\" stroke-width=\"0.8\"{dash}><title>{Esc(ToiletChecker.Label(i.Role))}: {Esc(i.Rule)}</title></rect>");
            if (i.Wall is not null)
            {
                var tip = i.Anchor + i.Facing * Math.Min(i.Depth, 6);
                sb.Append($"<line x1=\"{X(i.Anchor.X)}\" y1=\"{Y(i.Anchor.Y)}\" x2=\"{X(tip.X)}\" y2=\"{Y(tip.Y)}\" stroke=\"#c0392b\" stroke-width=\"1\"/>");
            }
            if (k >= 3 || i.Role is Roles.Wc or Roles.Basin or Roles.Vanity)
            {
                var c = p.Center;
                sb.Append($"<text x=\"{X(c.X)}\" y=\"{F(yTop + (s.Depth - c.Y) * k + 3)}\" font-size=\"{F(Math.Max(6, k * 1.8))}\" text-anchor=\"middle\" fill=\"#222\">{Code(i.Role)}</text>");
            }
        }

        if (caption)
        {
            double cy = oy + (s.Depth + s.WallSouth + s.WallNorth) * k + 16;
            string head = $"{FtIn.Format(s.Width)} x {FtIn.Format(s.Depth)}, door {RoomGeometry.Name(s.Door.Wall)} {(s.Door.SwingIn ? "in" : "out")}";
            sb.Append($"<text x=\"{F(ox)}\" y=\"{F(cy)}\" font-size=\"11\" font-weight=\"bold\">{Esc(head)}</text>");
            string line2 = plan.Refused ? $"REFUSED: {plan.RefusalRule}" : Shorten(plan.Arrangement, 70);
            sb.Append($"<text x=\"{F(ox)}\" y=\"{F(cy + 14)}\" font-size=\"9\" fill=\"{(plan.Refused ? "#b02a2a" : "#444")}\">{Esc(line2)}</text>");
            if (plan.Refused && plan.WouldPass is not null)
                sb.Append($"<text x=\"{F(ox)}\" y=\"{F(cy + 26)}\" font-size=\"9\" fill=\"#b02a2a\">{Esc(Shorten(plan.WouldPass, 70))}</text>");
        }
        sb.Append("</g>");
        return sb.ToString();
    }

    private static int Sweep(Pt hinge, Pt from, Pt to)
    {
        // SVG y points down, so a counter-clockwise turn in plan is clockwise on screen
        double cross = (from - hinge).Cross(to - hinge);
        return cross > 0 ? 0 : 1;
    }

    private static int Layer(PlacedItem i) => i.Mount switch { Mount.Floor => 0, Mount.Wall => 1, Mount.OnBasin => 2, _ => 3 };

    private static string Code(string role) => role switch
    {
        Roles.Wc => "WC",
        Roles.Basin => "B",
        Roles.Vanity => "",
        Roles.Faucet => "",
        Roles.Mirror => "M",
        Roles.TissueHolder => "TH",
        Roles.MuslimShower => "MS",
        Roles.SoapDispenser => "SD",
        Roles.Dryer => "HD",
        Roles.TowelDispenser => "TD",
        Roles.FloorTrap => "FD",
        Roles.Exhaust => "EF",
        Roles.CeilingLight => "L",
        _ => "?",
    };

    private static string Shorten(string s, int n) => s.Length <= n ? s : s[..(n - 3)] + "...";
    private static string F(double v) => v.ToString("0.##", Inv);
    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
