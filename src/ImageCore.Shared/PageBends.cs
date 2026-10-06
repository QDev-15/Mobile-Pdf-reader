namespace ImageCoreService;

/// <summary>How far one side of a page bulges out of its straight chord: at chord parameter t (0 at the side's first
/// corner, 1 at its second), the edge lies <c>t(1-t)(A + B t)</c> x the chord length away from the chord, outward
/// (away from the page's middle) when positive. Zero at both corners by construction; A alone is a symmetric bow, B tilts
/// it toward one end (a page held at one corner).</summary>
public readonly record struct SideBend(double A, double B)
{
    public double Offset(double t) => t * (1 - t) * (A + B * t);

    public bool IsFlat => A == 0 && B == 0;
}

/// <summary>
/// The curved outline of a page that is not lying flat (held in a hand, a book, a curled sheet): one
/// <see cref="SideBend"/> per side, in the corner order TL -> TR (top), TR -> BR (right), BR -> BL (bottom), BL -> TL (left).
/// Sizes are relative to each side's own length and bulges are measured from the page's middle, so the same values hold
/// for the photo at any scale, turned or mirrored (EXIF), and for the proxy and the full-size original alike.
/// </summary>
public sealed record PageBends(SideBend Top, SideBend Right, SideBend Bottom, SideBend Left)
{
    public static readonly PageBends Flat = new(default, default, default, default);

    public bool IsFlat => Top.IsFlat && Right.IsFlat && Bottom.IsFlat && Left.IsFlat;

    public SideBend this[int side] => side switch { 0 => Top, 1 => Right, 2 => Bottom, _ => Left };

    /// <summary>A0, B0, ..., A3, B3: the flat form stored in doc.json.</summary>
    public double[] ToValues() => [Top.A, Top.B, Right.A, Right.B, Bottom.A, Bottom.B, Left.A, Left.B];

    public static PageBends? FromValues(IReadOnlyList<double>? v) =>
        v is not { Count: 8 } ? null : new PageBends(new(v[0], v[1]), new(v[2], v[3]), new(v[4], v[5]), new(v[6], v[7]));

    /// <summary>After the outline is turned a quarter clockwise (<c>ImageGeometry.RotateQuadClockwise</c> renames the
    /// corners so the old bottom-left becomes the top-left), the old left side is the new top, and so on.</summary>
    public PageBends RotateClockwise() => new(Left, Top, Right, Bottom);

    /// <summary>The point of side <paramref name="side"/> at chord parameter <paramref name="t"/>, for an outline in
    /// pixels (<paramref name="q"/>): the chord point pushed out by the bend.</summary>
    public PointD PointOnSide(Quad q, int side, double t)
    {
        PointD[] c = q.ToArray();
        PointD a = c[side], b = c[(side + 1) % 4];
        PointD chord = new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        (double nx, double ny, double length) = OutwardNormal(q, side);
        double d = this[side].Offset(t) * length;
        return new PointD(chord.X + nx * d, chord.Y + ny * d);
    }

    /// <summary>Unit normal of a side pointing away from the outline's middle, and the side's length.</summary>
    public static (double Nx, double Ny, double Length) OutwardNormal(Quad q, int side)
    {
        PointD[] c = q.ToArray();
        PointD a = c[side], b = c[(side + 1) % 4];
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return (0, 0, 0);
        double nx = -dy / len, ny = dx / len;
        double mx = (c[0].X + c[1].X + c[2].X + c[3].X) / 4 - a.X, my = (c[0].Y + c[1].Y + c[2].Y + c[3].Y) / 4 - a.Y;
        if (nx * mx + ny * my > 0) { nx = -nx; ny = -ny; } // was pointing inward
        return (nx, ny, len);
    }
}
