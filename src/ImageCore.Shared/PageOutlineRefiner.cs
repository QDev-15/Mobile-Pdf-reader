namespace ImageCoreService;

/// <summary>
/// Turns the edge detector's coarse outline (found at ~480 px, straight sides) into a precise one at the proxy's
/// resolution (~1600 px), with curved sides where the page is not flat.
///
/// For each side, the actual paper border is looked for at ~60 points along it: across the side (along its normal),
/// within a few percent of the page size, the outermost strong color step whose inner side looks like the paper and
/// whose outer side does not (a line of text near the edge has paper again just beyond it; the table, the hand or
/// the screen behind do not). The border offsets are then fitted as <c>a + b t + t(1-t)(c + d t)</c> along the side
/// with a robust (Tukey) fit, so fingers over the edge, a shadow or a gap in the evidence are outliers, not bends:
/// <c>a + b t</c> is the refined straight side, the cubic term its bulge (<see cref="PageBends"/>). The sides are then
/// intersected for new corners, and the whole is done a second time with a narrower search. Sides with too little
/// evidence (white paper on a white table) keep the detector's line.
/// </summary>
public static class PageOutlineRefiner
{
    public sealed record Result(Quad Outline, PageBends? Bends, int SidesRefined);

    /// <summary>Optional diagnostics (one line per side); used by the EdgeProbe tool.</summary>
    public static Action<string>? Trace { get; set; }

    private const int SamplesPerSide = 64;
    private const double MinStep = 14;           // gray levels across the border for a sample to count
    private const double PaperTolerance = 48;    // max channel difference to the paper color that still is "paper"

    /// <param name="image">The upright photo the outline is in (the screen proxy, ~1600 px).</param>
    /// <param name="outline">The coarse outline in pixels of <paramref name="image"/>.</param>
    public static Result Refine(RgbImage image, Quad outline)
    {
        (double R, double G, double B) paper = PaperColor(image, outline);
        Quad current = outline;
        PageBends? bends = null;
        int refined = 0;
        double diag = Math.Sqrt((double)image.Width * image.Width + (double)image.Height * image.Height);

        for (int pass = 0; pass < 2; pass++)
        {
            double minSide = MinSide(current);
            // First pass wide (the coarse outline can be ~5% off where it latched onto something else), second narrow.
            double range = Math.Clamp((pass == 0 ? 0.12 : 0.035) * minSide, 6, pass == 0 ? 110 : 35);
            var fits = new SideFit?[4];
            for (int side = 0; side < 4; side++)
            {
                (double s0, double s1) = Anchors(outline, current, side);
                fits[side] = FitSide(image, current, side, range, paper, s0, s1);
                // No border in reach at all: the detector latched onto a line of the table / background well away from
                // the paper. Look much further (first pass only; the second one starts from corrected corners).
                if (fits[side] == null && pass == 0) fits[side] = FitSide(image, current, side, Math.Min(200, 0.25 * minSide), paper, s0, s1);
            }

            // Refined straight lines (endpoint offsets along the old outward normals), then new corners where they meet.
            var lines = new (PointD P, PointD Q)[4];
            PointD[] c = current.ToArray();
            for (int side = 0; side < 4; side++)
            {
                PointD a = c[side], b = c[(side + 1) % 4];
                (double nx, double ny, _) = PageBends.OutwardNormal(current, side);
                SideFit? f = fits[side];
                double oa = f?.Start ?? 0, ob = f?.End ?? 0;
                lines[side] = (new PointD(a.X + nx * oa, a.Y + ny * oa), new PointD(b.X + nx * ob, b.Y + ny * ob));
            }
            var corners = new PointD[4];
            bool ok = true;
            for (int corner = 0; corner < 4 && ok; corner++)
            {
                // Corner k joins side k-1 (ending there) and side k (starting there).
                PointD? p = Intersect(lines[(corner + 3) % 4], lines[corner]);
                if (p == null) ok = false;
                else corners[corner] = p.Value;
            }
            var candidate = ok ? new Quad(corners[0], corners[1], corners[2], corners[3]) : current;
            if (!ok || !candidate.IsConvex || Math.Abs(candidate.Area / current.Area - 1) > 0.35
                || corners.Zip(c).Any(z => Dist(z.First, z.Second) > 0.12 * diag))
                break; // implausible: keep what we had

            current = candidate;
            refined = fits.Count(f => f != null);
            bends = new PageBends(Bend(fits[0], current, 0), Bend(fits[1], current, 1), Bend(fits[2], current, 2), Bend(fits[3], current, 3));
        }
        if (refined > 0) current = KeepInPicture(current, outline, image.Width, image.Height);
        return new Result(current, bends is { IsFlat: false } ? bends : null, refined);
    }

    /// <summary>A refined corner is extrapolated from its two sides; where the detector saw the corner inside the picture,
    /// it is not let go more than 1% of the picture beyond the frame (a corner well outside the frame fills the
    /// straightened page with a white wedge). A sheet the frame really cuts can still be dragged out by hand.</summary>
    private static Quad KeepInPicture(Quad refinedOutline, Quad detected, int width, int height)
    {
        double m = 0.01 * Math.Min(width, height);
        PointD[] r = refinedOutline.ToArray(), d = detected.ToArray();
        for (int k = 0; k < 4; k++)
        {
            bool detectedInside = d[k].X >= -1 && d[k].Y >= -1 && d[k].X <= width + 1 && d[k].Y <= height + 1;
            if (!detectedInside) continue;
            r[k] = new PointD(Math.Clamp(r[k].X, -m, width + m), Math.Clamp(r[k].Y, -m, height + m));
        }
        var q = new Quad(r[0], r[1], r[2], r[3]);
        return q.IsConvex ? q : refinedOutline;
    }

    /// <summary>Where the detector's corners of <paramref name="side"/> lie relative to that side of
    /// <paramref name="current"/> (pixels along its outward normal): the prior for an end without evidence.</summary>
    private static (double Start, double End) Anchors(Quad detected, Quad current, int side)
    {
        PointD[] d = detected.ToArray(), c = current.ToArray();
        (double nx, double ny, _) = PageBends.OutwardNormal(current, side);
        int e = (side + 1) % 4;
        return ((d[side].X - c[side].X) * nx + (d[side].Y - c[side].Y) * ny,
                (d[e].X - c[e].X) * nx + (d[e].Y - c[e].Y) * ny);
    }

    /// <summary>Border offsets of one side: start / end of the refined straight line, and the bulge (all in pixels along
    /// the old outward normal).</summary>
    private sealed record SideFit(double Start, double End, double C, double D);

    private static SideBend Bend(SideFit? fit, Quad q, int side)
    {
        if (fit == null) return default;
        (_, _, double length) = PageBends.OutwardNormal(q, side);
        if (length < 1) return default;
        // Ignore bulges too small to matter (under ~0.4% of the side or 1.5 px): a flat page stays exactly straight.
        double max = 0;
        for (int i = 1; i < 20; i++)
        {
            double t = i / 20.0;
            max = Math.Max(max, Math.Abs(t * (1 - t) * (fit.C + fit.D * t)));
        }
        if (max < Math.Max(1.5, 0.004 * length)) return default;
        return new SideBend(fit.C / length, fit.D / length);
    }

    private static SideFit? FitSide(RgbImage img, Quad q, int side, double range, (double R, double G, double B) paper,
        double anchorStart, double anchorEnd)
    {
        PointD[] c = q.ToArray();
        PointD a = c[side], b = c[(side + 1) % 4];
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 20) return null;
        double tx = dx / len, ty = dy / len;
        (double nx, double ny, _) = PageBends.OutwardNormal(q, side);

        var ts = new List<double>();
        var es = new List<double>();
        var ws = new List<double>();
        var offPicture = new List<double>(); // positions along the side where the search window left the picture
        int steps = (int)Math.Ceiling(range);
        var profile = new (double R, double G, double B)[2 * steps + 1];
        for (int k = 0; k < SamplesPerSide; k++)
        {
            double t = 0.04 + 0.92 * k / (SamplesPerSide - 1);
            double px = a.X + dx * t, py = a.Y + dy * t;
            // The search window is cut where it leaves the picture (instead of dropping the sample): near a corner by
            // the frame that is the only evidence of where a curved side ends up.
            int sLo = -steps, sHi = steps;
            for (int dir = 1; dir >= -1; dir -= 2)
                for (int s = dir > 0 ? 0 : -1; s >= -steps && s <= steps; s += dir)
                {
                    // Averaged over 5 px along the side: text strokes and noise blur out, the border does not.
                    double r = 0, g = 0, bl = 0;
                    bool inside = true;
                    for (int j = -2; j <= 2 && inside; j++)
                    {
                        double sx = px + nx * s + tx * j * 1.5, sy = py + ny * s + ty * j * 1.5;
                        if (!Read(img, sx, sy, out double cr, out double cg, out double cb)) inside = false;
                        r += cr; g += cg; bl += cb;
                    }
                    if (!inside)
                    {
                        if (dir > 0) sHi = s - 1; else sLo = s + 1;
                        break;
                    }
                    profile[s + steps] = (r / 5, g / 5, bl / 5);
                }
            int scanFrom = sLo + 3, scanTo = sHi - 7;
            if (scanTo < scanFrom || sHi < 8 || sLo > -4) { offPicture.Add(t); continue; } // off the picture: no evidence

            // The outermost strong step (inner side paper-like, outer side not, and not paper again a little further out),
            // located at its steepest point. The border of a photographed sheet is a soft ramp several pixels wide (lens,
            // JPEG, the sheet's shadow): every position on the ramp passes the test, and taking the outermost one put the
            // outline 1-2% outside the sheet (background wedges in the corners of the straightened page, owner's photos
            // 2026-09-28). So: the outermost run of positions that pass, and in it the one with the largest step.
            double best = 0, bestS = 0;
            double maxStep = 0;
            for (int s = scanFrom; s <= scanTo; s++)
                maxStep = Math.Max(maxStep, Diff(profile[s - 3 + steps], profile[s + 3 + steps]));
            if (maxStep < MinStep) continue;
            bool inRun = false;
            double runBest = 0, runBestS = 0;
            for (int s = scanFrom; s <= scanTo; s++)
            {
                (double, double, double) inner = profile[s - 3 + steps], outer = profile[s + 3 + steps], beyond = profile[s + 7 + steps];
                double step = Diff(inner, outer);
                bool passes = step >= Math.Max(MinStep, 0.5 * maxStep)
                              && Diff(inner, paper) <= PaperTolerance                                              // inner side must be paper
                              && !(Diff(outer, paper) < PaperTolerance * 0.5 && Diff(beyond, paper) < PaperTolerance * 0.5); // paper again: text
                if (passes)
                {
                    if (!inRun) { inRun = true; runBest = 0; }
                    if (step > runBest) { runBest = step; runBestS = s; }
                }
                else if (inRun)
                {
                    inRun = false;
                    best = runBest; bestS = runBestS; // a later (outer) run replaces it
                }
            }
            if (inRun) { best = runBest; bestS = runBestS; }
            if (best == 0) continue;
            ts.Add(t);
            es.Add(bestS);
            ws.Add(best);
        }
        Trace?.Invoke($"side {side}: {ts.Count} border points of {SamplesPerSide}, range {range:0}");
        if (ts.Count < 12) return null;

        // Consensus line first: a least-squares start is dragged by a run of wrong points (the edge of the hand
        // holding the sheet gave offsets swinging +50 .. -57 px on the owner's photos) and Tukey then converges on a
        // tilted line. Only the points near the consensus line go on to the fit.
        double tol = Math.Max(2.5, 0.006 * len);
        (double A, double B)? consensus = Consensus(ts, es, ws, tol);
        if (consensus is not { } cl) return null;
        var kt = new List<double>(); var ke = new List<double>(); var kw = new List<double>();
        for (int i = 0; i < ts.Count; i++)
            if (Math.Abs(es[i] - (cl.A + cl.B * ts[i])) <= 3 * tol) { kt.Add(ts[i]); ke.Add(es[i]); kw.Add(ws[i]); }
        if (kt.Count < 12) return null;
        double lo = kt.Min(), hi = kt.Max();

        // Evidence along a short stretch only (the rest under a hand, off the picture): extrapolating its slope to the
        // corners turns small errors into big ones, so the side is moved, not turned.
        if (hi - lo < 0.35)
        {
            double shift = Median(ke.ToArray());
            Trace?.Invoke($"side {side}: shift only {shift:0.0} ({kt.Count} points in {lo:0.00}..{hi:0.00})");
            return new SideFit(shift, shift, 0, 0);
        }

        // Evidence that stops short of a corner because the corner is at the picture's frame (the search
        // window has no room outside it) leaves that end to extrapolation, which put corners 3-6% of the width
        // outside the sheet on the owner's photos (a side bulging out in the middle and coming back at the corner).
        // There the detector's own corner is added as a prior: a group of points the robust fit can still overrule.
        double realLo = lo, realHi = hi;
        double prior = kw.Max();
        // Only where the evidence stops because the picture does (most samples beyond are off it): a border that is
        // merely faint there (pale paper on a pale table) keeps the extrapolated line, the detector may be the one that
        // is wrong (it put a side 140 px into the table on one of the photos).
        bool offStart = offPicture.Count(v => v < lo) >= 0.5 * (lo - 0.04) * SamplesPerSide / 0.92;
        bool offEnd = offPicture.Count(v => v > hi) >= 0.5 * (0.96 - hi) * SamplesPerSide / 0.92;
        if (lo > 0.12 && offStart) { for (int i = 0; i < 6; i++) { kt.Add(0); ke.Add(anchorStart); kw.Add(prior); } lo = 0; }
        if (hi < 0.88 && offEnd) { for (int i = 0; i < 6; i++) { kt.Add(1); ke.Add(anchorEnd); kw.Add(prior); } hi = 1; }

        // A bulge only when the evidence reaches both ends of the side: otherwise the cubic term extrapolates freely
        // and throws the chord's end (the corner) tens of pixels off.
        bool curve = kt.Count >= 24 && lo < 0.12 && hi > 0.88 && realHi - realLo > 0.6;
        double[]? p = curve ? RobustFit(kt, ke, kw, 4) : null;
        // Guard against wild bulges (a hand edge fitted as the border): at most 12% of the side.
        if (p != null && Math.Abs(0.25 * p[2]) + Math.Abs(0.15 * p[3]) > 0.12 * len) p = null;
        p ??= RobustFit(kt, ke, kw, 2);
        if (p == null) { Trace?.Invoke($"side {side}: robust fit failed"); return null; }
        double c2 = p.Length == 4 ? p[2] : 0, d2 = p.Length == 4 ? p[3] : 0;
        Trace?.Invoke($"side {side}: start {p[0]:0.0} end {p[0] + p[1]:0.0} bulge {c2:0.0}/{d2:0.0} samples " +
                      string.Join(" ", ts.Zip(es).Select(z => $"{z.First:0.00}:{z.Second:0}")));
        return new SideFit(p[0], p[0] + p[1], c2, d2);
    }

    /// <summary>The line e = A + B t through two of the points that most (strength-weighted) points lie within
    /// <paramref name="tol"/> of; every pair is tried (at most 64 points), so the result is deterministic.</summary>
    private static (double A, double B)? Consensus(List<double> ts, List<double> es, List<double> ws, double tol)
    {
        int n = ts.Count;
        double bestScore = 0;
        (double A, double B)? best = null;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double dt = ts[j] - ts[i];
                if (dt < 0.1) continue; // too close together to fix a direction
                double b = (es[j] - es[i]) / dt, a = es[i] - b * ts[i];
                double score = 0;
                for (int k = 0; k < n; k++)
                {
                    double r = Math.Abs(es[k] - (a + b * ts[k]));
                    if (r <= tol) score += Math.Sqrt(ws[k]) * (1 - 0.5 * r / tol);
                }
                if (score > bestScore) { bestScore = score; best = (a, b); }
            }
        return best;
    }

    /// <summary>Weighted least squares of e(t) = p0 + p1 t [+ t(1-t)(p2 + p3 t)], reweighted with Tukey's biweight so
    /// points far from the fit (fingers over the edge, a shadow line) stop counting.</summary>
    private static double[]? RobustFit(List<double> ts, List<double> es, List<double> ws, int terms)
    {
        int n = ts.Count;
        var weight = ws.Select(w => Math.Sqrt(w)).ToArray();
        double[]? p = null;
        for (int iter = 0; iter < 6; iter++)
        {
            var ata = new double[terms, terms];
            var atb = new double[terms];
            for (int i = 0; i < n; i++)
            {
                if (weight[i] <= 0) continue;
                double[] row = Basis(ts[i], terms);
                for (int r = 0; r < terms; r++)
                {
                    atb[r] += weight[i] * row[r] * es[i];
                    for (int col = 0; col < terms; col++) ata[r, col] += weight[i] * row[r] * row[col];
                }
            }
            double[]? next = Solve(ata, atb, terms);
            if (next == null) return p;
            p = next;
            var residuals = new double[n];
            for (int i = 0; i < n; i++) residuals[i] = es[i] - Eval(p, ts[i]);
            double mad = Median(residuals.Select(Math.Abs).ToArray());
            double scale = Math.Max(1.0, 1.4826 * mad) * 4.685;
            int kept = 0;
            for (int i = 0; i < n; i++)
            {
                double u = residuals[i] / scale;
                double tukey = Math.Abs(u) >= 1 ? 0 : (1 - u * u) * (1 - u * u);
                weight[i] = Math.Sqrt(ws[i]) * tukey;
                if (tukey > 0) kept++;
            }
            if (kept < Math.Max(8, terms * 3)) return null;
        }
        return p;
    }

    private static double[] Basis(double t, int terms) =>
        terms == 2 ? [1, t] : [1, t, t * (1 - t), t * t * (1 - t)];

    private static double Eval(double[] p, double t)
    {
        double v = p[0] + p[1] * t;
        if (p.Length == 4) v += t * (1 - t) * (p[2] + p[3] * t);
        return v;
    }

    private static double[]? Solve(double[,] a, double[] b, int n)
    {
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (int i = 0; i < n; i++)
        {
            int pivot = i;
            for (int r = i + 1; r < n; r++) if (Math.Abs(m[r, i]) > Math.Abs(m[pivot, i])) pivot = r;
            if (Math.Abs(m[pivot, i]) < 1e-9) return null;
            if (pivot != i)
            {
                for (int col = 0; col < n; col++) (m[i, col], m[pivot, col]) = (m[pivot, col], m[i, col]);
                (x[i], x[pivot]) = (x[pivot], x[i]);
            }
            for (int r = i + 1; r < n; r++)
            {
                double f = m[r, i] / m[i, i];
                for (int col = i; col < n; col++) m[r, col] -= f * m[i, col];
                x[r] -= f * x[i];
            }
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double s = x[i];
            for (int col = i + 1; col < n; col++) s -= m[i, col] * x[col];
            x[i] = s / m[i, i];
        }
        return x;
    }

    private static double Median(double[] v)
    {
        if (v.Length == 0) return 0;
        Array.Sort(v);
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    /// <summary>The paper's color: the bright end of the outline's middle (ink and anything overlapping the corners left out).</summary>
    private static (double R, double G, double B) PaperColor(RgbImage img, Quad q)
    {
        PointD[] c = q.ToArray();
        var samples = new List<(double L, double R, double G, double B)>();
        for (int i = 1; i < 20; i++)
            for (int j = 1; j < 20; j++)
            {
                double u = 0.2 + 0.6 * i / 20.0, v = 0.2 + 0.6 * j / 20.0;
                double x = (1 - u) * (1 - v) * c[0].X + u * (1 - v) * c[1].X + u * v * c[2].X + (1 - u) * v * c[3].X;
                double y = (1 - u) * (1 - v) * c[0].Y + u * (1 - v) * c[1].Y + u * v * c[2].Y + (1 - u) * v * c[3].Y;
                if (Read(img, x, y, out double r, out double g, out double b)) samples.Add((0.299 * r + 0.587 * g + 0.114 * b, r, g, b));
            }
        if (samples.Count == 0) return (255, 255, 255);
        var bright = samples.OrderBy(s => s.L).Skip(samples.Count * 6 / 10).Take(Math.Max(1, samples.Count / 4)).ToList();
        return (bright.Average(s => s.R), bright.Average(s => s.G), bright.Average(s => s.B));
    }

    private static double Diff((double R, double G, double B) a, (double R, double G, double B) b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    /// <summary>Bilinear read at pixel-edge coordinates; false outside the image.</summary>
    private static bool Read(RgbImage img, double x, double y, out double r, out double g, out double b)
    {
        double sx = x - 0.5, sy = y - 0.5;
        r = g = b = 0;
        if (sx < 0 || sy < 0 || sx > img.Width - 1 || sy > img.Height - 1) return false;
        int x0 = (int)sx, y0 = (int)sy;
        int x1 = Math.Min(x0 + 1, img.Width - 1), y1 = Math.Min(y0 + 1, img.Height - 1);
        double fx = sx - x0, fy = sy - y0;
        byte[] d = img.Data;
        int w = img.Width;
        int i00 = (y0 * w + x0) * 3, i10 = (y0 * w + x1) * 3, i01 = (y1 * w + x0) * 3, i11 = (y1 * w + x1) * 3;
        double w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
        r = d[i00] * w00 + d[i10] * w10 + d[i01] * w01 + d[i11] * w11;
        g = d[i00 + 1] * w00 + d[i10 + 1] * w10 + d[i01 + 1] * w01 + d[i11 + 1] * w11;
        b = d[i00 + 2] * w00 + d[i10 + 2] * w10 + d[i01 + 2] * w01 + d[i11 + 2] * w11;
        return true;
    }

    private static PointD? Intersect((PointD P, PointD Q) l1, (PointD P, PointD Q) l2)
    {
        double x1 = l1.P.X, y1 = l1.P.Y, x2 = l1.Q.X, y2 = l1.Q.Y, x3 = l2.P.X, y3 = l2.P.Y, x4 = l2.Q.X, y4 = l2.Q.Y;
        double den = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
        if (Math.Abs(den) < 1e-9) return null;
        double a = x1 * y2 - y1 * x2, b = x3 * y4 - y3 * x4;
        return new PointD((a * (x3 - x4) - (x1 - x2) * b) / den, (a * (y3 - y4) - (y1 - y2) * b) / den);
    }

    private static double MinSide(Quad q)
    {
        PointD[] c = q.ToArray();
        double m = double.MaxValue;
        for (int i = 0; i < 4; i++) m = Math.Min(m, Dist(c[i], c[(i + 1) % 4]));
        return m;
    }

    private static double Dist(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
