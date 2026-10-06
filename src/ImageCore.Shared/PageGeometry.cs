namespace ImageCoreService;

/// <summary>
/// The true proportions of a photographed rectangular sheet. Under perspective the outline's side lengths say little
/// about the sheet's shape (the far edge is shorter, and so are the sides running away from the camera), which is what
/// made straightened pages look squashed or stretched. The rectangle's aspect ratio follows from its four image corners
/// and the camera's focal length, and the focal length itself from the corners (Zhang & He, "Whiteboard scanning and
/// image enhancement", Digital Signal Processing 17, 2007): two vanishing directions of a rectangle must be orthogonal.
/// Pure geometry, no patents, no dependencies.
/// </summary>
public static class PageGeometry
{
    /// <summary>A typical phone main camera: focal length ~0.75 x the image diagonal (26 mm equivalent). Used when the
    /// outline is too close to a front view for the focal length to be measured (then it barely matters).</summary>
    public const double TypicalFocalPerDiagonal = 0.75;

    /// <summary>How close to sqrt 2 (relative) the perspective-corrected shape may be to count as A4 even when the outline's
    /// own side ratio is outside the sheet range (a sheet seen at a steep angle).</summary>
    public const double A4Tolerance = 0.12;

    /// <summary>Width / height of the real rectangle whose photo is <paramref name="q"/> (TL, TR, BR, BL, in pixels of a
    /// photo of <paramref name="imageWidth"/> x <paramref name="imageHeight"/>, principal point at its center).</summary>
    public static double TrueAspect(Quad q, int imageWidth, int imageHeight)
    {
        double diag = Math.Sqrt((double)imageWidth * imageWidth + (double)imageHeight * imageHeight);
        double focal = TypicalFocalPerDiagonal * diag;
        if (MeasureFocal(q, imageWidth, imageHeight) is { } measured && measured > 0.35 && measured < 3.0) focal = measured * diag;
        return AspectForFocal(q, imageWidth, imageHeight, focal);
    }

    /// <summary>The camera's focal length (as a multiple of the image diagonal) implied by the outline being a
    /// rectangle, or null when the outline has no usable vanishing points (a frontal view).</summary>
    public static double? MeasureFocal(Quad q, int imageWidth, int imageHeight)
    {
        if (VanishingDirections(q, imageWidth, imageHeight) is not (var n2, var n3)) return null;
        double diag = Math.Sqrt((double)imageWidth * imageWidth + (double)imageHeight * imageHeight);
        double denominator = n2.Z * n3.Z;
        if (Math.Abs(n2.Z) <= 1e-6 || Math.Abs(n3.Z) <= 1e-6 || Math.Abs(denominator) <= 1e-12) return null;
        double f2 = -(n2.X * n3.X + n2.Y * n3.Y) / denominator;
        return f2 > 0 ? Math.Sqrt(f2) / diag : null;
    }

    /// <summary>Width / height of the rectangle photographed as <paramref name="q"/> by a camera of focal length
    /// <paramref name="focalPx"/> pixels (principal point at the image center).</summary>
    public static double AspectForFocal(Quad q, int imageWidth, int imageHeight, double focalPx)
    {
        if (VanishingDirections(q, imageWidth, imageHeight) is not (var n2, var n3)) return SideRatio(q);
        double f2 = focalPx * focalPx;
        double width2 = n2.X * n2.X + n2.Y * n2.Y + f2 * n2.Z * n2.Z;
        double height2 = n3.X * n3.X + n3.Y * n3.Y + f2 * n3.Z * n3.Z;
        if (width2 <= 0 || height2 <= 0) return SideRatio(q);
        double aspect = Math.Sqrt(width2 / height2);
        return double.IsFinite(aspect) && aspect > 0.05 && aspect < 20 ? aspect : SideRatio(q);
    }

    /// <summary>Zhang &amp; He's n2 (along the top side) and n3 (along the left side), homogeneous, principal point at the
    /// image center; null for a degenerate outline.</summary>
    private static (V3, V3)? VanishingDirections(Quad q, int imageWidth, int imageHeight)
    {
        double cx = imageWidth / 2.0, cy = imageHeight / 2.0;
        // Homogeneous corners centered on the principal point: m1 TL, m2 TR, m3 BL, m4 BR (the paper's numbering).
        var m1 = new V3(q.TopLeft.X - cx, q.TopLeft.Y - cy, 1);
        var m2 = new V3(q.TopRight.X - cx, q.TopRight.Y - cy, 1);
        var m3 = new V3(q.BottomLeft.X - cx, q.BottomLeft.Y - cy, 1);
        var m4 = new V3(q.BottomRight.X - cx, q.BottomRight.Y - cy, 1);
        double d2 = V3.Dot(V3.Cross(m2, m4), m3), d3 = V3.Dot(V3.Cross(m3, m4), m2);
        if (Math.Abs(d2) < 1e-9 || Math.Abs(d3) < 1e-9) return null;
        double k2 = V3.Dot(V3.Cross(m1, m4), m3) / d2;
        double k3 = V3.Dot(V3.Cross(m1, m4), m2) / d3;
        return (k2 * m2 - m1, k3 * m3 - m1);
    }

    /// <summary>A phone's main camera (24-26 mm equivalent): focal length ~0.60 x the diagonal of a 4:3 photo, ~0.65 of a
    /// 16:9 one. What pages are straightened with (see <see cref="OutputAspect"/>).</summary>
    public const double PhoneFocalPerDiagonal = 0.62;

    /// <summary>Most the perspective correction may change the outline's own side ratio, either way.</summary>
    public const double MaxPerspectiveCorrection = 1.2;

    /// <summary>
    /// The aspect (width / height) to straighten the outline into.
    ///
    /// The outline's side ratio, corrected for perspective with a phone camera's typical focal length, the correction
    /// kept within <see cref="MaxPerspectiveCorrection"/>. The focal length is NOT measured from the outline
    /// (<see cref="TrueAspect"/>): on real photos that measurement is dominated by the corners' imprecision and by the
    /// sheet not being flat (a hand-held page bends). On the owner's 10 pages of "Tài liệu 1" (one camera) it came out
    /// anywhere between 0.19 and 2.38 x the diagonal, or undefined, and straightened A4 pages 1.57 to 3.07 : 1 long, each
    /// adjustment of a corner giving another length; with the fixed focal length all ten come out 1.34 to 1.62 : 1.
    ///
    /// In A4 mode a sheet (outline side ratio 1.15-1.75, or corrected shape within <see cref="A4Tolerance"/> of A4) becomes
    /// exactly A4, portrait or landscape; a clearly different shape (receipt, card, a sheet cut by the photo) keeps its own.
    /// </summary>
    public static double OutputAspect(Quad q, int imageWidth, int imageHeight, bool a4)
    {
        double diag = Math.Sqrt((double)imageWidth * imageWidth + (double)imageHeight * imageHeight);
        double side = SideRatio(q);
        double corrected = Math.Clamp(AspectForFocal(q, imageWidth, imageHeight, PhoneFocalPerDiagonal * diag),
            side / MaxPerspectiveCorrection, side * MaxPerspectiveCorrection);
        if (!a4) return corrected;
        double sideLong = side >= 1 ? side : 1 / side, correctedLong = corrected >= 1 ? corrected : 1 / corrected;
        bool sheet = sideLong is >= PerspectiveWarp.MinA4Like and <= PerspectiveWarp.MaxA4Like
                     || Math.Abs(correctedLong / PerspectiveWarp.A4Ratio - 1) <= A4Tolerance;
        if (!sheet) return corrected;
        return corrected >= 1 ? PerspectiveWarp.A4Ratio : 1 / PerspectiveWarp.A4Ratio;
    }

    /// <summary>True when <paramref name="aspect"/> is (to rounding) an A4 sheet, either way round.</summary>
    public static bool IsA4(double aspect) =>
        Math.Abs((aspect >= 1 ? aspect : 1 / aspect) - PerspectiveWarp.A4Ratio) < 0.005;

    /// <summary>Plain ratio of the average horizontal to the average vertical side (no perspective correction).</summary>
    public static double SideRatio(Quad q)
    {
        double w = (Dist(q.TopLeft, q.TopRight) + Dist(q.BottomLeft, q.BottomRight)) / 2;
        double h = (Dist(q.TopLeft, q.BottomLeft) + Dist(q.TopRight, q.BottomRight)) / 2;
        return h < 1e-9 ? 1 : w / h;
    }

    /// <summary>
    /// Output size for straightening <paramref name="q"/> into a rectangle of the given <paramref name="aspect"/>
    /// (width / height): no less resolution than the outline's longest horizontal and vertical sides offer, then shrunk
    /// (keeping the aspect) to fit <paramref name="maxLongEdge"/> and <paramref name="maxPixels"/>.
    /// </summary>
    public static (int Width, int Height) SizeFor(Quad q, double aspect, int maxLongEdge, long maxPixels)
    {
        double hw = Math.Max(Dist(q.TopLeft, q.TopRight), Dist(q.BottomLeft, q.BottomRight));
        double hv = Math.Max(Dist(q.TopLeft, q.BottomLeft), Dist(q.TopRight, q.BottomRight));
        double h = Math.Max(hv, hw / aspect), w = h * aspect;
        double scale = Math.Min(1.0, Math.Min(maxLongEdge / Math.Max(w, h), Math.Sqrt(maxPixels / (w * h))));
        return (Math.Max(16, (int)Math.Round(w * scale)), Math.Max(16, (int)Math.Round(h * scale)));
    }

    private static double Dist(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private readonly record struct V3(double X, double Y, double Z)
    {
        public static V3 operator *(double k, V3 v) => new(k * v.X, k * v.Y, k * v.Z);
        public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }
}
