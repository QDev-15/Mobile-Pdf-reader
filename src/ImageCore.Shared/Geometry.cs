namespace ImageCoreService;

public readonly record struct PointD(double X, double Y);

/// <summary>
/// Four corners in the order top-left, top-right, bottom-right, bottom-left (clockwise on
/// screen, y pointing down). Used both for pixel coordinates and for coordinates normalized
/// to 0..1 of an upright image.
/// </summary>
public readonly record struct Quad(PointD TopLeft, PointD TopRight, PointD BottomRight, PointD BottomLeft)
{
    public PointD[] ToArray() => [TopLeft, TopRight, BottomRight, BottomLeft];

    /// <summary>x0,y0,x1,y1,x2,y2,x3,y3 (TL, TR, BR, BL): the flat form stored in doc.json.</summary>
    public double[] ToValues() =>
        [TopLeft.X, TopLeft.Y, TopRight.X, TopRight.Y, BottomRight.X, BottomRight.Y, BottomLeft.X, BottomLeft.Y];

    public static Quad FromValues(IReadOnlyList<double> v)
    {
        if (v.Count != 8) throw new ArgumentException("A quad needs 8 numbers.", nameof(v));
        return new Quad(new PointD(v[0], v[1]), new PointD(v[2], v[3]), new PointD(v[4], v[5]), new PointD(v[6], v[7]));
    }

    /// <summary>The whole image, shrunk by <paramref name="margin"/> (fraction of each side).</summary>
    public static Quad Inset(double margin) => new(
        new PointD(margin, margin), new PointD(1 - margin, margin),
        new PointD(1 - margin, 1 - margin), new PointD(margin, 1 - margin));

    public static Quad Full => Inset(0);

    public Quad Scale(double sx, double sy) => new(
        new PointD(TopLeft.X * sx, TopLeft.Y * sy), new PointD(TopRight.X * sx, TopRight.Y * sy),
        new PointD(BottomRight.X * sx, BottomRight.Y * sy), new PointD(BottomLeft.X * sx, BottomLeft.Y * sy));

    /// <summary>Unsigned polygon area (shoelace).</summary>
    public double Area
    {
        get
        {
            PointD[] p = ToArray();
            double s = 0;
            for (int i = 0; i < 4; i++)
            {
                PointD a = p[i], b = p[(i + 1) % 4];
                s += a.X * b.Y - b.X * a.Y;
            }
            return Math.Abs(s) / 2;
        }
    }

    /// <summary>True when every turn has the same direction (a proper, non-self-crossing quad).</summary>
    public bool IsConvex
    {
        get
        {
            PointD[] p = ToArray();
            int sign = 0;
            for (int i = 0; i < 4; i++)
            {
                PointD a = p[i], b = p[(i + 1) % 4], c = p[(i + 2) % 4];
                double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
                if (Math.Abs(cross) < 1e-12) return false;
                int s = Math.Sign(cross);
                if (sign == 0) sign = s;
                else if (s != sign) return false;
            }
            return true;
        }
    }

    /// <summary>Point-in-convex-quad test (either winding).</summary>
    public bool Contains(double x, double y)
    {
        PointD[] p = ToArray();
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            PointD a = p[i], b = p[(i + 1) % 4];
            double cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            int s = Math.Sign(cross);
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }
}
