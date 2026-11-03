namespace Ubl.Engine.Core.Geometry;

/// <summary>2D point or vector in drawing units (inches).</summary>
public readonly record struct Pt(double X, double Y)
{
    public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);
    public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
    public static Pt operator *(Pt a, double k) => new(a.X * k, a.Y * k);
    public double Dot(Pt o) => X * o.X + Y * o.Y;
    public double Cross(Pt o) => X * o.Y - Y * o.X;
    public double Length => Math.Sqrt(X * X + Y * Y);
    public Pt Unit => this * (1.0 / Length);
    public Pt Normal => new(-Y, X);
    public static double Dist(Pt a, Pt b) => (a - b).Length;
    public override string ToString() => $"({X:0.##}, {Y:0.##})";
}

/// <summary>Axis-aligned box. X0 &lt;= X1 and Y0 &lt;= Y1 always.</summary>
public readonly record struct Box(double X0, double Y0, double X1, double Y1)
{
    public double Width => X1 - X0;
    public double Height => Y1 - Y0;
    public double Long => Math.Max(Width, Height);
    public double Short => Math.Min(Width, Height);
    public Pt Center => new((X0 + X1) / 2, (Y0 + Y1) / 2);

    public static Box Of(IEnumerable<Pt> pts)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var p in pts)
        {
            x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y);
            x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
        }
        if (x0 > x1) throw new ArgumentException("Box.Of needs at least one point");
        return new Box(x0, y0, x1, y1);
    }

    public static Box FromCorners(Pt a, Pt b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public bool Contains(Pt p, double pad = 0) =>
        p.X >= X0 - pad && p.X <= X1 + pad && p.Y >= Y0 - pad && p.Y <= Y1 + pad;

    public bool Contains(Box b, double tol) =>
        X0 <= b.X0 + tol && Y0 <= b.Y0 + tol && X1 >= b.X1 - tol && Y1 >= b.Y1 - tol;

    public bool Intersects(Box o) => X0 < o.X1 && o.X0 < X1 && Y0 < o.Y1 && o.Y0 < Y1;

    public bool SameAs(Box o, double tol) =>
        Math.Abs(X0 - o.X0) < tol && Math.Abs(Y0 - o.Y0) < tol && Math.Abs(X1 - o.X1) < tol && Math.Abs(Y1 - o.Y1) < tol;

    /// <summary>Shortest distance between two boxes (0 if they touch or overlap).</summary>
    public double Gap(Box o)
    {
        double dx = Math.Max(0, Math.Max(X0, o.X0) - Math.Min(X1, o.X1));
        double dy = Math.Max(0, Math.Max(Y0, o.Y0) - Math.Min(Y1, o.Y1));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public Box Union(Box o) => new(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1));
    public Box Inflate(double d) => new(X0 - d, Y0 - d, X1 + d, Y1 + d);
    public IReadOnlyList<Pt> Corners() => new[] { new Pt(X0, Y0), new Pt(X1, Y0), new Pt(X1, Y1), new Pt(X0, Y1) };
    public override string ToString() => $"[{X0:0.##}, {Y0:0.##} .. {X1:0.##}, {Y1:0.##}]";
}

public static class Poly
{
    public static double SignedArea(IReadOnlyList<Pt> p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count];
            s += a.X * b.Y - b.X * a.Y;
        }
        return s / 2;
    }

    public static double Area(IReadOnlyList<Pt> p) => Math.Abs(SignedArea(p));

    public static double Perimeter(IReadOnlyList<Pt> p)
    {
        double s = 0;
        for (int i = 0; i < p.Count; i++) s += Pt.Dist(p[i], p[(i + 1) % p.Count]);
        return s;
    }

    /// <summary>Even-odd point in polygon.</summary>
    public static bool Contains(IReadOnlyList<Pt> p, Pt q)
    {
        bool inside = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
        {
            var a = p[i]; var b = p[j];
            if ((a.Y > q.Y) != (b.Y > q.Y))
            {
                double x = a.X + (q.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                if (x > q.X) inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>True when the loop fills its bounding box (an axis-aligned rectangle).</summary>
    public static bool IsAxisRectangle(IReadOnlyList<Pt> p, double areaTol = 1.0)
    {
        var b = Box.Of(p);
        return Math.Abs(Area(p) - b.Width * b.Height) < areaTol;
    }

    /// <summary>True when every edge is horizontal or vertical within tol.</summary>
    public static bool IsOrthogonal(IReadOnlyList<Pt> p, double tol = 0.05)
    {
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count];
            if (Math.Abs(a.X - b.X) > tol && Math.Abs(a.Y - b.Y) > tol) return false;
        }
        return true;
    }

    /// <summary>Removes a closing vertex equal to the first one and consecutive duplicates.</summary>
    public static List<Pt> Clean(IEnumerable<Pt> pts, double tol = 1e-6)
    {
        var r = new List<Pt>();
        foreach (var p in pts)
            if (r.Count == 0 || Pt.Dist(r[^1], p) > tol) r.Add(p);
        if (r.Count > 1 && Pt.Dist(r[0], r[^1]) <= tol) r.RemoveAt(r.Count - 1);
        return r;
    }

    /// <summary>
    /// Splits an orthogonal polygon into rectangles. Grid cells from the vertex coordinates are kept when their
    /// centre is inside; cells are then merged greedily along the polygon's long direction, so a wall strip
    /// comes out as one long rectangle and an L comes out as two.
    /// </summary>
    public static List<Box> OrthoDecompose(IReadOnlyList<Pt> p, double snap = 0.01)
    {
        var xs = p.Select(v => Math.Round(v.X / snap) * snap).Distinct().OrderBy(v => v).ToArray();
        var ys = p.Select(v => Math.Round(v.Y / snap) * snap).Distinct().OrderBy(v => v).ToArray();
        int nx = xs.Length - 1, ny = ys.Length - 1;
        if (nx < 1 || ny < 1) return new List<Box>();
        var cell = new bool[nx, ny];
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
                cell[i, j] = Contains(p, new Pt((xs[i] + xs[i + 1]) / 2, (ys[j] + ys[j + 1]) / 2));

        var bb = Box.Of(p);
        bool alongX = bb.Width >= bb.Height;
        var used = new bool[nx, ny];
        var result = new List<Box>();
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                if (!cell[i, j] || used[i, j]) continue;
                int i1 = i, j1 = j;
                if (alongX)
                {
                    while (i1 + 1 < nx && cell[i1 + 1, j] && !used[i1 + 1, j]) i1++;
                    while (j1 + 1 < ny && Enumerable.Range(i, i1 - i + 1).All(k => cell[k, j1 + 1] && !used[k, j1 + 1])) j1++;
                }
                else
                {
                    while (j1 + 1 < ny && cell[i, j1 + 1] && !used[i, j1 + 1]) j1++;
                    while (i1 + 1 < nx && Enumerable.Range(j, j1 - j + 1).All(k => cell[i1 + 1, k] && !used[i1 + 1, k])) i1++;
                }
                for (int a = i; a <= i1; a++)
                    for (int b = j; b <= j1; b++) used[a, b] = true;
                result.Add(new Box(xs[i], ys[j], xs[i1 + 1], ys[j1 + 1]));
            }
        return result;
    }
}
