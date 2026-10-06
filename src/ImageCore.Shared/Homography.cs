namespace ImageCoreService;

/// <summary>
/// 3x3 projective transform solved from four point pairs (h33 fixed to 1). Used to unwarp a
/// photographed page into a rectangle and, in tests, to render synthetic photos of a page.
/// </summary>
public sealed class Homography
{
    private readonly double[] _h; // row-major h11 h12 h13 h21 h22 h23 h31 h32 h33

    private Homography(double[] h) => _h = h;

    /// <summary>The transform that maps each <paramref name="src"/> point onto the matching
    /// <paramref name="dst"/> point.</summary>
    public static Homography FromPoints(IReadOnlyList<PointD> src, IReadOnlyList<PointD> dst)
    {
        if (src.Count != 4 || dst.Count != 4) throw new ArgumentException("Exactly four point pairs are required.");

        // Unknowns h11 h12 h13 h21 h22 h23 h31 h32; two equations per pair.
        var a = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = src[i].X, y = src[i].Y, u = dst[i].X, v = dst[i].Y;
            int r = i * 2;
            a[r, 0] = x; a[r, 1] = y; a[r, 2] = 1; a[r, 6] = -x * u; a[r, 7] = -y * u; a[r, 8] = u;
            a[r + 1, 3] = x; a[r + 1, 4] = y; a[r + 1, 5] = 1; a[r + 1, 6] = -x * v; a[r + 1, 7] = -y * v; a[r + 1, 8] = v;
        }

        // Gauss-Jordan with partial pivoting.
        for (int col = 0; col < 8; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 8; r++)
                if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            if (Math.Abs(a[pivot, col]) < 1e-10) throw new InvalidOperationException("Degenerate point set.");
            if (pivot != col)
                for (int c = 0; c < 9; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);

            double div = a[col, col];
            for (int c = col; c < 9; c++) a[col, c] /= div;
            for (int r = 0; r < 8; r++)
            {
                if (r == col) continue;
                double f = a[r, col];
                if (f == 0) continue;
                for (int c = col; c < 9; c++) a[r, c] -= f * a[col, c];
            }
        }

        var h = new double[9];
        for (int i = 0; i < 8; i++) h[i] = a[i, 8];
        h[8] = 1;
        return new Homography(h);
    }

    /// <summary>Element i of the row-major 3x3 matrix (h11 h12 h13 h21 h22 h23 h31 h32 h33).</summary>
    public double this[int i] => _h[i];

    public PointD Apply(double x, double y)
    {
        double w = _h[6] * x + _h[7] * y + _h[8];
        return new PointD((_h[0] * x + _h[1] * y + _h[2]) / w, (_h[3] * x + _h[4] * y + _h[5]) / w);
    }

    public Homography Inverse()
    {
        double[] m = _h;
        double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (Math.Abs(det) < 1e-14) throw new InvalidOperationException("Singular transform.");
        var inv = new[]
        {
            e * i - f * h, c * h - b * i, b * f - c * e,
            f * g - d * i, a * i - c * g, c * d - a * f,
            d * h - e * g, b * g - a * h, a * e - b * d,
        };
        double s = inv[8];
        for (int k = 0; k < 9; k++) inv[k] /= s;
        return new Homography(inv);
    }
}
