namespace ImageCoreService;

/// <summary>
/// Finds the outline of a sheet of paper in a photo. Pure managed code (no OpenCV), so it is
/// free of native dependencies and license questions.
///
/// Pipeline, all on a copy shrunk to ~480 px:
///  1. Edges: 5-tap blur, Sobel on each of R, G and B (keeping the strongest channel, so a
///     white sheet on a light table still shows up through color), non-maximum suppression,
///     hysteresis threshold. Every edge pixel keeps its gradient direction.
///  2. Lines: Hough transform where each edge pixel votes only for lines roughly
///     perpendicular to its gradient; strongest peaks are refined by a least-squares fit.
///     The four image borders are added as pseudo-lines so a sheet running out of frame
///     can still be closed.
///  3. Quads: pairs of near-parallel lines are combined into convex quadrilaterals. Each
///     candidate is scored by how much of its perimeter lies on real edges with the right
///     orientation, how rectangular it is, and how much of the frame it covers (text rows and
///     margins inside the page also form lines, but a quad built from them has no edge
///     support on its sides, and the outer sheet wins by area).
/// </summary>
public sealed class DocumentEdgeDetector : IEdgeDetector
{
    private const int AnalysisEdge = 480;
    private const int ThetaBins = 180;
    private const int MaxLines = 60;
    private const int MaxScoredCandidates = 3000;
    private const int VoteSpreadDeg = 3;
    private const float MaxVoteMagnitude = 48f;
    private const int SuppressThetaDeg = 4;   // peaks closer than this (and SuppressRho px) are one line
    private const int SuppressRho = 6;
    private const double PiOver180 = Math.PI / 180;

    /// <summary>Opposite sides of a sheet seen at a steep angle can differ by 40+ degrees.</summary>
    private const double MaxOppositeSideAngle = 50 * PiOver180;

    /// <summary>Smallest sheet (share of the frame) worth reporting: receipts, small notes.</summary>
    private const double MinAreaFraction = 0.04;

    /// <summary>How far outside the frame a corner may be when the first search found nothing (share of the short side
    /// of the picture). A sheet the frame
    /// cuts across a corner (photographed at an angle, one corner off the picture) has that corner well outside; with
    /// 4% such a sheet was not found at all on the owner's photos. Matches how far the outline editor lets a corner go.</summary>
    private const double OutsideMargin = 0.2;

    /// <summary>How far outside the frame a corner may be in the first search (share of the long side).</summary>
    private const double NearFrameMargin = 0.04;

    /// <summary>Below this score the detection is reported as failed.</summary>
    public double MinConfidence { get; init; } = 0.42;

    /// <summary>Margin (fraction of each side) of the fallback whole-frame quad.</summary>
    public double FallbackMargin { get; init; } = 0.03;

    /// <summary>Most straight lines taken from the Hough accumulator, strongest first.</summary>
    public int MaxLineCount { get; init; } = MaxLines;

    /// <summary>Most candidate outlines that get the (costly) edge scoring, best pre-ranked first.</summary>
    public int MaxScoredCount { get; init; } = MaxScoredCandidates;

    /// <summary>True (photos): a side may lie on the frame border, for a sheet that runs out of the picture. False
    /// (live camera): the whole sheet must be in view, which also stops a cluttered room from passing for a sheet
    /// the size of the frame.</summary>
    public bool AllowFrameBorders { get; init; } = true;

    /// <summary>Settings for the live camera preview: a few frames per second on a phone instead of one photo in a
    /// second or so. Fewer lines and candidates (a sheet the user is aiming at is among the strongest lines anyway),
    /// the whole sheet in view.</summary>
    public static DocumentEdgeDetector Live() => new() { MaxLineCount = 40, MaxScoredCount = 200, AllowFrameBorders = false };

    /// <summary>Long edge of the picture the live camera hands to <see cref="Live"/> detectors: edge finding costs in
    /// proportion to the pixels, and the camera outline only has to be good to a few pixels of the screen.</summary>
    public const int LiveAnalysisEdge = 320;

    /// <summary>Optional diagnostics sink (one line per event); used by tests and tuning.</summary>
    public Action<string>? Trace { get; init; }

    /// <summary>Edge-strength cut (percentile of thin-edge strengths) and absolute floor for
    /// each pass, strictest first. A strict pass keeps text-heavy pages from drowning the sheet
    /// outline in noise; a lax pass still sees a white sheet on a pale table, where the outline
    /// is far weaker than the printed text.</summary>
    private static readonly (double Percentile, float Floor)[] Passes = [(0.0, 6f)];

    /// <summary>A looser pass must beat the best of the stricter ones by this factor: it sees
    /// more edges, so any quad gets a little more "support" from noise.</summary>
    private const double LooserPassMargin = 1.03;

    public QuadDetection Detect(RgbImage image)
    {
        var fallback = new QuadDetection(Quad.Inset(FallbackMargin), 0, false);
        if (image.Width < 32 || image.Height < 32) return fallback;

        int factor = Math.Max(1, (int)Math.Round(Math.Max(image.Width, image.Height) / (double)AnalysisEdge));
        RgbImage s = factor > 1 ? image.Downscale(factor) : image;
        Trace?.Invoke($"analysis {s.Width}x{s.Height} (factor {factor})");

        Quad? bestQuad = null;
        double bestScore = 0;
        var passes = new List<(List<Line> Lines, EdgeMap Edges)>();
        foreach ((double percentile, float floor) in Passes)
        {
            EdgeMap edges = EdgeMap.Compute(s, percentile, floor);
            List<Line> lines = FindLines(edges, MaxLineCount);
            Trace?.Invoke($"pass p={percentile}: {edges.Points.Count} edge px, {lines.Count} lines");
            if (Trace != null)
                foreach (Line l in lines)
                    Trace($"  line theta={l.Theta / PiOver180:0.0} rho={l.Rho:0.0} votes={l.Votes} strength={l.Strength:0}");
            if (AllowFrameBorders) AddBorderLines(lines, s.Width, s.Height);
            passes.Add((lines, edges));

            (Quad quad, double score)? found = BestQuad(lines, edges, Trace, MaxScoredCount, NearFrameMargin);
            Trace?.Invoke($"pass p={percentile}: best score {(found?.score ?? 0):0.000}");
            if (found != null && found.Value.score > bestScore * LooserPassMargin)
            {
                bestScore = found.Value.score;
                bestQuad = found.Value.quad;
            }
        }

        // Nothing with its corners at most a hair outside the picture: perhaps a sheet the frame cuts across a corner.
        // Only then are outlines with a corner well outside considered; allowing them from the start let outlines
        // reach past the sheet into the table on photos that were fine before.
        double limit = NearFrameMargin;
        if (AllowFrameBorders && (bestQuad == null || bestScore < MinConfidence))
        {
            foreach ((List<Line> lines, EdgeMap edges) in passes)
            {
                (Quad quad, double score)? found = BestQuad(lines, edges, Trace, MaxScoredCount, OutsideMargin);
                Trace?.Invoke($"corner outside the frame: best score {(found?.score ?? 0):0.000}");
                if (found != null && found.Value.score > bestScore * LooserPassMargin)
                {
                    bestScore = found.Value.score;
                    bestQuad = found.Value.quad;
                    limit = OutsideMargin;
                }
            }
        }

        double confidence = Math.Clamp(bestScore, 0, 1);
        if (bestQuad == null || confidence < MinConfidence) return fallback with { Confidence = confidence };

        Quad px = Order(bestQuad.Value);
        // Pixel centers -> 0..1. Normally clamped to the frame (corners may poke a hair outside it); a sheet the frame
        // cuts keeps its corners outside.
        double lo = limit == NearFrameMargin ? 0 : -limit, hi = 1 - lo;
        PointD Norm(PointD p) => new(Math.Clamp((p.X + 0.5) / s.Width, lo, hi), Math.Clamp((p.Y + 0.5) / s.Height, lo, hi));
        var normalized = new Quad(Norm(px.TopLeft), Norm(px.TopRight), Norm(px.BottomRight), Norm(px.BottomLeft));
        return new QuadDetection(normalized, confidence, true);
    }

    #region Edges

    /// <summary>
    /// The big per-picture arrays, kept for the next call on the same thread instead of being allocated anew (several MB
    /// per call). The live camera runs the detector a few times a second, and on Android every collection of those
    /// large objects also stops the Java side (the GC bridge): 70 ms to over a second per collection was measured on
    /// the emulator. Only the thread that calls <see cref="Detect"/> takes buffers (the parallel loops inside share the
    /// ones it took), a call runs start to end on its thread, and nothing taken escapes the call, so concurrent
    /// detectors on other threads never share them. Every buffer is cleared or fully overwritten before use, so the
    /// results are exactly those of fresh arrays.
    /// </summary>
    private sealed class Scratch
    {
        [ThreadStatic] private static Scratch? t_current;
        public static Scratch Current => t_current ??= new Scratch();

        private readonly float[]?[] _floats = new float[16][];
        private readonly long[]?[] _longs = new long[3][];
        private bool[]? _bools;
        private int[]? _ints;

        public float[] Floats(int slot, int n) => Take(ref _floats[slot], n);
        public long[] Longs(int slot, int n) => Take(ref _longs[slot], n);
        public bool[] Bools(int n) => Take(ref _bools, n);
        public int[] Ints(int n) => Take(ref _ints, n);

        private static T[] Take<T>(ref T[]? slot, int n)
        {
            if (slot == null || slot.Length != n) slot = new T[n];
            else Array.Clear(slot);
            return slot;
        }
    }

    private sealed class EdgeMap
    {
        public int W, H;
        public RgbImage Image = null!;
        public long[] IntR = [], IntG = [], IntB = []; // integral images, (W+1) x (H+1)
        public bool[] Edge = [];
        public float[] Theta = []; // gradient direction folded to [0, pi)
        public List<(int X, int Y, float Theta, float Mag)> Points = [];

        public static EdgeMap Compute(RgbImage img, double percentile, float floorHigh)
        {
            int w = img.Width, h = img.Height, n = w * h;
            Scratch scratch = Scratch.Current;
            var chan = new float[3][];
            var work = new float[9][];
            for (int k = 0; k < 9; k++) work[k] = scratch.Floats(k, n);
            Parallel.For(0, 3, ParallelScope.Options, c =>
            {
                float[] f = work[3 * c];
                for (int i = 0; i < n; i++) f[i] = img.Data[i * 3 + c];
                chan[c] = Blur(f, work[3 * c + 1], work[3 * c + 2], w, h);
            });

            float[] mag = scratch.Floats(9, n);
            float[] gxBest = scratch.Floats(10, n);
            float[] gyBest = scratch.Floats(11, n);
            Parallel.For(1, h - 1, ParallelScope.Options, y =>
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    float bestM = 0, bx = 0, by = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        float[] p = chan[c];
                        float gx = ((p[i - w + 1] + 2 * p[i + 1] + p[i + w + 1]) - (p[i - w - 1] + 2 * p[i - 1] + p[i + w - 1])) / 4f;
                        float gy = ((p[i + w - 1] + 2 * p[i + w] + p[i + w + 1]) - (p[i - w - 1] + 2 * p[i - w] + p[i - w + 1])) / 4f;
                        float m = gx * gx + gy * gy;
                        if (m > bestM) { bestM = m; bx = gx; by = gy; }
                    }
                    mag[i] = MathF.Sqrt(bestM);
                    gxBest[i] = bx;
                    gyBest[i] = by;
                }
            });

            // Non-maximum suppression along the (quantized) gradient direction.
            float[] nms = scratch.Floats(12, n);
            Parallel.For(1, h - 1, ParallelScope.Options, y =>
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int i = y * w + x;
                    float m = mag[i];
                    if (m <= 0) continue;
                    double a = Math.Atan2(gyBest[i], gxBest[i]);
                    int sector = (int)Math.Round(a / (Math.PI / 4));
                    sector = ((sector % 4) + 4) % 4;
                    int o1 = sector switch { 0 => 1, 1 => w + 1, 2 => w, _ => w - 1 };
                    if (m >= mag[i + o1] && m >= mag[i - o1]) nms[i] = m;
                }
            });
            var strengths = new List<float>();
            foreach (float m in nms)
                if (m > 0) strengths.Add(m);

            // Adaptive thresholds: edges above the given percentile of thin-edge strengths seed
            // the hysteresis. The floor keeps a smooth, noisy background from being mistaken
            // for structure.
            float high = floorHigh;
            if (strengths.Count > 0)
            {
                strengths.Sort();
                high = Math.Max(high, strengths[(int)(strengths.Count * percentile)]);
            }
            float low = Math.Max(floorHigh * 0.5f, high * 0.4f);

            var map = new EdgeMap { W = w, H = h, Image = img, Edge = scratch.Bools(n), Theta = scratch.Floats(13, n) };
            map.BuildIntegrals(scratch);
            var stack = new Stack<int>();
            for (int i = 0; i < n; i++)
            {
                if (nms[i] < high || map.Edge[i]) continue;
                map.Edge[i] = true;
                stack.Push(i);
                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    int px = p % w, py = p / w;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = py + dy;
                        if ((uint)ny >= (uint)h) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = px + dx;
                            if ((uint)nx >= (uint)w) continue;
                            int q = ny * w + nx;
                            if (!map.Edge[q] && nms[q] >= low) { map.Edge[q] = true; stack.Push(q); }
                        }
                    }
                }
            }

            for (int i = 0; i < n; i++)
            {
                if (!map.Edge[i]) continue;
                double a = Math.Atan2(gyBest[i], gxBest[i]);
                if (a < 0) a += Math.PI;
                if (a >= Math.PI) a -= Math.PI;
                map.Theta[i] = (float)a;
                map.Points.Add((i % w, i / w, (float)a, mag[i]));
            }
            return map;
        }

        private void BuildIntegrals(Scratch scratch)
        {
            int iw = W + 1;
            IntR = scratch.Longs(0, iw * (H + 1));
            IntG = scratch.Longs(1, iw * (H + 1));
            IntB = scratch.Longs(2, iw * (H + 1));
            for (int y = 0; y < H; y++)
            {
                long rr = 0, gg = 0, bb = 0;
                for (int x = 0; x < W; x++)
                {
                    int o = (y * W + x) * 3;
                    rr += Image.Data[o]; gg += Image.Data[o + 1]; bb += Image.Data[o + 2];
                    int i = (y + 1) * iw + x + 1, up = y * iw + x + 1;
                    IntR[i] = IntR[up] + rr; IntG[i] = IntG[up] + gg; IntB[i] = IntB[up] + bb;
                }
            }
        }

        /// <summary>Mean color of the (2r+1)^2 window centred on (fx, fy); false when the window
        /// would leave the frame.</summary>
        public bool MeanColor(double fx, double fy, int rad, out double r, out double g, out double b)
        {
            int cx = (int)Math.Round(fx), cy = (int)Math.Round(fy);
            r = g = b = 0;
            if (cx < rad || cy < rad || cx >= W - rad || cy >= H - rad) return false;
            int iw = W + 1;
            int x0 = cx - rad, x1 = cx + rad + 1, y0 = cy - rad, y1 = cy + rad + 1;
            double area = (2 * rad + 1) * (2 * rad + 1);
            r = (IntR[y1 * iw + x1] - IntR[y0 * iw + x1] - IntR[y1 * iw + x0] + IntR[y0 * iw + x0]) / area;
            g = (IntG[y1 * iw + x1] - IntG[y0 * iw + x1] - IntG[y1 * iw + x0] + IntG[y0 * iw + x0]) / area;
            b = (IntB[y1 * iw + x1] - IntB[y0 * iw + x1] - IntB[y1 * iw + x0] + IntB[y0 * iw + x0]) / area;
            return true;
        }

        /// <summary>Separable binomial [1 4 6 4 1] / 16, edge pixels replicated.</summary>
        private static float[] Blur(float[] src, float[] tmp, float[] dst, int w, int h)
        {
            for (int y = 0; y < h; y++)
            {
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    float a = src[o + Math.Max(0, x - 2)], b = src[o + Math.Max(0, x - 1)], c = src[o + x];
                    float d = src[o + Math.Min(w - 1, x + 1)], e = src[o + Math.Min(w - 1, x + 2)];
                    tmp[o + x] = (a + 4 * b + 6 * c + 4 * d + e) / 16f;
                }
            }
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - 2) * w, y1 = Math.Max(0, y - 1) * w, y2 = y * w;
                int y3 = Math.Min(h - 1, y + 1) * w, y4 = Math.Min(h - 1, y + 2) * w;
                for (int x = 0; x < w; x++)
                    dst[y2 + x] = (tmp[y0 + x] + 4 * tmp[y1 + x] + 6 * tmp[y2 + x] + 4 * tmp[y3 + x] + tmp[y4 + x]) / 16f;
            }
            return dst;
        }
    }

    #endregion

    #region Lines

    /// <summary>Line x*cos(theta) + y*sin(theta) = rho; theta in [0, pi).</summary>
    private readonly record struct Line(double Theta, double Rho, int Votes, bool IsBorder, double Strength = 30)
    {
        public double Nx => Math.Cos(Theta);
        public double Ny => Math.Sin(Theta);
    }

    private static List<Line> FindLines(EdgeMap e, int maxLines)
    {
        int diag = (int)Math.Ceiling(Math.Sqrt((double)e.W * e.W + (double)e.H * e.H));
        int rhoBins = 2 * diag + 1;
        int[] acc = Scratch.Current.Ints(ThetaBins * rhoBins);
        var cos = new double[ThetaBins];
        var sin = new double[ThetaBins];
        for (int t = 0; t < ThetaBins; t++)
        {
            cos[t] = Math.Cos(t * PiOver180);
            sin[t] = Math.Sin(t * PiOver180);
        }

        foreach ((int x, int y, float theta, float mag) in e.Points)
        {
            // Stronger edges vote harder: the outline of a sheet against the table differs by tens
            // to hundreds of gray levels, the grain or weave of the table by a handful.
            int weight = 1 + (int)Math.Min(mag, MaxVoteMagnitude) / 4;
            int centre = (int)Math.Round(theta / PiOver180) % ThetaBins;
            for (int d = -VoteSpreadDeg; d <= VoteSpreadDeg; d++)
            {
                int t = (centre + d + ThetaBins) % ThetaBins;
                int r = (int)Math.Round(x * cos[t] + y * sin[t]) + diag;
                acc[t * rhoBins + r] += weight;
            }
        }

        int minVotes = Math.Max(20, (int)(0.2 * Math.Min(e.W, e.H)));
        int minPeak = minVotes * 2; // accumulator cells hold weighted votes
        var peaks = new List<Line>();
        for (int k = 0; k < maxLines; k++)
        {
            int bestIdx = 0, bestVal = 0;
            for (int i = 0; i < acc.Length; i++)
                if (acc[i] > bestVal) { bestVal = acc[i]; bestIdx = i; }
            if (bestVal < minPeak) break;

            int bt = bestIdx / rhoBins, br = bestIdx % rhoBins - diag;
            // Suppress the neighbourhood; across the theta wrap (0 <-> 180) rho changes sign.
            for (int dt = -SuppressThetaDeg; dt <= SuppressThetaDeg; dt++)
            {
                int t = bt + dt, rc = br;
                if (t < 0) { t += ThetaBins; rc = -br; }
                else if (t >= ThetaBins) { t -= ThetaBins; rc = -br; }
                for (int dr = -SuppressRho; dr <= SuppressRho; dr++)
                {
                    int r = rc + dr + diag;
                    if ((uint)r < (uint)rhoBins) acc[t * rhoBins + r] = 0;
                }
            }

            peaks.Add(new Line(bt * PiOver180, br, bestVal, false));
        }

        // Refining (a pass over every edge pixel per peak) does not affect the peak search, so the
        // peaks are refined in parallel and filtered afterwards in their original order.
        var refinedPeaks = new Line[peaks.Count];
        Parallel.For(0, peaks.Count, ParallelScope.Options, k => refinedPeaks[k] = Refine(peaks[k], e));
        var lines = new List<Line>();
        foreach (Line refined in refinedPeaks)
        {
            // Two neighbouring peaks can refine onto the same physical line; keep the first.
            bool duplicate = lines.Any(l => AngleDiff(l.Theta, refined.Theta) < 1.5 * PiOver180 && SameRho(l, refined) < 3);
            if (refined.Votes >= minVotes && !duplicate) lines.Add(refined);
        }
        return lines;
    }

    /// <summary>Distance between two near-parallel lines, measured along the first one's normal
    /// (handles the theta wrap, where the normals point opposite ways).</summary>
    private static double SameRho(Line a, Line b)
    {
        double sign = a.Nx * b.Nx + a.Ny * b.Ny >= 0 ? 1 : -1;
        return Math.Abs(a.Rho - sign * b.Rho);
    }

    /// <summary>Least-squares re-fit of a Hough peak to the edge pixels lying on it (the 1
    /// degree / 1 px accumulator cells are too coarse for a 500 px long side).</summary>
    private static Line Refine(Line line, EdgeMap e)
    {
        double theta = line.Theta, rho = line.Rho, strength = 0;
        int votes = line.Votes;
        foreach (double tol in new[] { 4.0, 2.5, 2.0 })
        {
            double nx = Math.Cos(theta), ny = Math.Sin(theta);
            double sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0, sm = 0;
            int n = 0;
            foreach ((int x, int y, float t, float m) in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(e.Points))
            {
                if (Math.Abs(x * nx + y * ny - rho) > tol) continue;
                if (AngleDiff(t, theta) > 20 * PiOver180) continue;
                n++;
                sm += m;
                sx += x; sy += y; sxx += (double)x * x; sxy += (double)x * y; syy += (double)y * y;
            }
            if (n < 8) break;
            votes = n;
            strength = sm / n;

            double mx = sx / n, my = sy / n;
            double cxx = sxx / n - mx * mx, cxy = sxy / n - mx * my, cyy = syy / n - my * my;
            // Principal direction of the point cloud = the line direction.
            double dirAngle = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
            double dx = Math.Cos(dirAngle), dy = Math.Sin(dirAngle);
            double lnx = -dy, lny = dx; // normal
            if (lnx * nx + lny * ny < 0) { lnx = -lnx; lny = -lny; }
            theta = Math.Atan2(lny, lnx);
            rho = lnx * mx + lny * my;
            if (theta < 0) { theta += Math.PI; rho = -rho; }
            else if (theta >= Math.PI) { theta -= Math.PI; rho = -rho; }
        }
        return new Line(theta, rho, votes, false, strength);
    }

    private static void AddBorderLines(List<Line> lines, int w, int h)
    {
        lines.Add(new Line(Math.PI / 2, 0, 0, true));          // top    y = 0
        lines.Add(new Line(Math.PI / 2, h - 1, 0, true));      // bottom y = h-1
        lines.Add(new Line(0, 0, 0, true));                    // left   x = 0
        lines.Add(new Line(0, w - 1, 0, true));                // right  x = w-1
    }

    /// <summary>Smallest angle between two orientations folded to [0, pi), in radians.</summary>
    private static double AngleDiff(double a, double b)
    {
        // Every orientation here lies in [0, pi], so |a - b| < 2 pi and one subtraction is the
        // exact fmod (Sterbenz), at a fraction of its cost: this runs millions of times per photo.
        double d = Math.Abs(a - b);
        if (d >= Math.PI) d -= Math.PI;
        return Math.Min(d, Math.PI - d);
    }

    #endregion

    #region Quads

    private readonly record struct Candidate(Quad Quad, int[] SideLine, double PreScore);

    private static (Quad Quad, double Score)? BestQuad(List<Line> lines, EdgeMap e, Action<string>? trace, int maxScored, double outsideMargin)
    {
        int w = e.W, h = e.H, minDim = Math.Min(w, h);
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0;

        // Opposite-side pairs: near parallel and clearly apart.
        var pairs = new List<(int A, int B)>();
        for (int i = 0; i < lines.Count; i++)
        {
            for (int j = i + 1; j < lines.Count; j++)
            {
                if (lines[i].IsBorder && lines[j].IsBorder) continue;
                if (AngleDiff(lines[i].Theta, lines[j].Theta) > MaxOppositeSideAngle) continue;
                double di = lines[i].Nx * cx + lines[i].Ny * cy - lines[i].Rho;
                double dj = lines[j].Nx * cx + lines[j].Ny * cy - lines[j].Rho;
                double s = lines[i].Nx * lines[j].Nx + lines[i].Ny * lines[j].Ny >= 0 ? 1 : -1;
                if (Math.Abs(di - s * dj) < 0.15 * minDim) continue;
                pairs.Add((i, j));
            }
        }

        var candidates = new List<Candidate>();
        // The near-frame margin is a share of the long side (as it always was), the wide one of the short side, so that
        // it stays within what the result is clamped to.
        double margin = outsideMargin * (outsideMargin > NearFrameMargin ? Math.Min(w, h) : Math.Max(w, h));
        for (int p = 0; p < pairs.Count; p++)
        {
            for (int q = p + 1; q < pairs.Count; q++)
            {
                (int pa, int pb) = pairs[p];
                (int qa, int qb) = pairs[q];
                if (pa == qa || pa == qb || pb == qa || pb == qb) continue;
                if (AngleDiff(lines[pa].Theta, lines[qa].Theta) < 30 * PiOver180) continue;
                if (AngleDiff(lines[pa].Theta, lines[qb].Theta) < 30 * PiOver180) continue;
                if (AngleDiff(lines[pb].Theta, lines[qa].Theta) < 30 * PiOver180) continue;
                if (AngleDiff(lines[pb].Theta, lines[qb].Theta) < 30 * PiOver180) continue;

                int borders = (lines[pa].IsBorder ? 1 : 0) + (lines[pb].IsBorder ? 1 : 0)
                            + (lines[qa].IsBorder ? 1 : 0) + (lines[qb].IsBorder ? 1 : 0);
                if (borders > 2) continue;

                PointD? c1 = Intersect(lines[pa], lines[qa]), c2 = Intersect(lines[pa], lines[qb]);
                PointD? c3 = Intersect(lines[pb], lines[qb]), c4 = Intersect(lines[pb], lines[qa]);
                if (c1 == null || c2 == null || c3 == null || c4 == null) continue;

                var quad = new Quad(c1.Value, c2.Value, c3.Value, c4.Value);
                if (!quad.IsConvex || quad.Area < MinAreaFraction * w * h) continue;
                if (!InsideFrame(quad, w, h, margin)) continue;
                if (MinSide(quad) < 0.10 * minDim) continue;
                if (MaxAngleDeviation(quad) > 45 * PiOver180) continue;

                // Cheap ranking so only the best few hundred pay for edge sampling. Votes per unit
                // of side length (not raw votes): a page full of text rows has dozens of lines
                // with far more votes than the sheet's own edges, which are long but thin.
                int[] sideLine = [pa, qb, pb, qa];
                PointD[] cp = quad.ToArray();
                double pre = 0;
                for (int i = 0; i < 4; i++)
                {
                    PointD a = cp[i], b = cp[(i + 1) % 4];
                    double len = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
                    Line l = lines[sideLine[i]];
                    // fraction of the side that has edge pixels on its line x how strong those edges are
                    pre += l.IsBorder ? 0.55 : Math.Min(1.0, l.Votes / Math.Max(1.0, len)) * (0.55 + 0.45 * Math.Clamp(l.Strength / 30, 0.2, 1));
                }
                pre *= 0.6 + 0.4 * Math.Sqrt(VisibleArea(quad, w, h) / ((double)w * h));
                candidates.Add(new Candidate(quad, sideLine, pre));
            }
        }
        if (candidates.Count == 0) return null;

        // Scored in parallel. Anything that cannot get within reach of the best so far is dropped
        // early (kept when tracing, so the diagnostics can list runners-up). The bound is an upper
        // bound of the candidate's score, so a dropped candidate could never be picked below, and
        // which ones get dropped (it depends on thread timing) does not change the result.
        Candidate[] ranked = candidates.OrderByDescending(c => c.PreScore).Take(maxScored).ToArray();
        var results = new Scored[ranked.Length];
        long bestBits = BitConverter.DoubleToInt64Bits(0.0);
        Parallel.For(0, ranked.Length, ParallelScope.Options, i =>
        {
            double bestSoFar = BitConverter.Int64BitsToDouble(Interlocked.Read(ref bestBits));
            Scored r = Evaluate(ranked[i], lines, e, trace == null ? bestSoFar * NearBest : 0);
            results[i] = r;
            if (r.Score <= 0) return;
            // Positive doubles order like their bit patterns: a lock-free max.
            long bits = BitConverter.DoubleToInt64Bits(r.Score), seen;
            while (bits > (seen = Interlocked.Read(ref bestBits)) && Interlocked.CompareExchange(ref bestBits, bits, seen) != seen) { }
        });
        // In ranking order, as the sequential loop produced them (MaxBy keeps the first of equals).
        var scored = results.Where(r => r.Score > 0).ToList();
        if (scored.Count == 0) return null;

        // Several near-identical outlines often score alike: the sheet itself, and the same
        // outline nudged onto the shadow, a grain line of the table or the printed border. Among
        // those, the sheet is the one whose edges are strongest: paper against table differs
        // by tens of gray levels, the alternatives by a handful.
        Scored best = scored.MaxBy(x => x.Score);
        Scored pick = scored
            .Where(x => x.Score >= NearBest * best.Score && x.Strength >= MuchStronger * best.Strength
                        && QuadIou(x.C.Quad, best.C.Quad, e.W, e.H) >= 0.8)
            .DefaultIfEmpty(best)
            .MaxBy(x => x.Strength);

        if (trace != null)
        {
            trace($"  {candidates.Count} candidates");
            if (!ReferenceEquals(pick.C.SideLine, best.C.SideLine))
                trace($"  tie-break: {best.Score:0.000}/{best.Strength:0} -> {pick.Score:0.000}/{pick.Strength:0}");
        }
        return (pick.C.Quad, pick.Score);
    }

    /// <summary>A candidate scoring at least this share of the best one still counts as a rival.</summary>
    private const double NearBest = 0.8;

    /// <summary>A rival replaces the best-scoring outline only when its edges are this many times
    /// stronger: the sheet (tens to hundreds of gray levels against the table) versus the grain of
    /// the table (a handful), not one sheet edge versus the shadow next to it.</summary>
    private const double MuchStronger = 2.0;

    private readonly record struct Scored(Candidate C, double Score, double Strength);

    /// <summary>Intersection over union of two quads, on a coarse grid over the frame.</summary>
    private static double QuadIou(Quad a, Quad b, int w, int h)
    {
        int inter = 0, union = 0;
        for (int y = 0; y < h; y += 4)
        {
            for (int x = 0; x < w; x += 4)
            {
                bool ia = a.Contains(x, y), ib = b.Contains(x, y);
                if (ia && ib) inter++;
                if (ia || ib) union++;
            }
        }
        return union == 0 ? 0 : (double)inter / union;
    }

    /// <summary>Per-side support of a candidate. A side lying on the image border (the sheet runs
    /// out of frame there) has no edge to look at; it is judged instead by whether the strip
    /// just inside the frame still looks like the sheet rather than like the table. Returns an
    /// empty array when the candidate cannot beat <paramref name="toBeat"/>.</summary>
    private static double[] SideSupports(Candidate c, List<Line> lines, EdgeMap e, out double strength, double toBeat = 0, double shape = 1)
    {
        strength = 0;
        PointD[] pts = c.Quad.ToArray();
        var edgeOnly = new double[4];
        double edgeSum = 0;
        int real = 0;

        // Phase A (cheap): how much of each side has an edge pixel of the right orientation
        // nearby. That fraction is an upper bound of the side's final support, so a hopeless
        // candidate is dropped before any color statistics are computed.
        for (int i = 0; i < 4; i++)
        {
            if (lines[c.SideLine[i]].IsBorder) { edgeOnly[i] = 1; edgeSum += 1; continue; }
            edgeOnly[i] = EdgeFraction(pts[i], pts[(i + 1) % 4], e);
            if (edgeOnly[i] < 0.15) return [];
            edgeSum += edgeOnly[i];
            real++;
        }
        if (toBeat > 0 && Math.Pow(edgeSum / 4, 1.5) * shape <= toBeat) return [];

        // Phase B: color statistics.
        var centroid = new PointD(pts.Average(p => p.X), pts.Average(p => p.Y));
        Interior interior = InteriorStats(c.Quad, e);
        var support = new double[4];
        var outside = new ColorAccumulator();
        double strengthSum = 0, measured = 0;
        int done = 0;
        for (int i = 0; i < 4; i++)
        {
            if (lines[c.SideLine[i]].IsBorder) continue;
            support[i] = SideSupport(pts[i], pts[(i + 1) % 4], centroid, interior.Paper, e, outside, edgeOnly[i], out double sideStrength);
            strengthSum += sideStrength;
            measured += support[i];
            done++;
            // Upper bound of the final mean with the other sides at 1.0; below the score to beat = give up.
            if (toBeat > 0 && Math.Pow((measured + (4 - done)) / 4, 1.5) * shape <= toBeat) return [];
        }

        for (int i = 0; i < 4; i++)
            if (lines[c.SideLine[i]].IsBorder)
                support[i] = outside.Count == 0 ? 0.55 : BorderSupport(pts[i], pts[(i + 1) % 4], centroid, interior.Mean, outside.Mean, e);
        strength = done == 0 ? 0 : strengthSum / done;
        return support;
    }

    /// <summary>Fraction of the points along a side that have an edge pixel nearby whose gradient
    /// is perpendicular to the side.</summary>
    private static double EdgeFraction(PointD a, PointD b, EdgeMap e)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return 0;
        double normalAngle = Math.Atan2(dx, -dy);
        if (normalAngle < 0) normalAngle += Math.PI;

        int n = Math.Max(8, (int)(len / 2)), samples = 0, hits = 0;
        for (int k = 0; k < n; k++)
        {
            double t = (k + 0.5) / n;
            int x = (int)Math.Round(a.X + dx * t), y = (int)Math.Round(a.Y + dy * t);
            if (x < 0 || y < 0 || x >= e.W || y >= e.H) continue; // off-frame part: no evidence either way
            samples++;
            if (HasEdgeNear(e, x, y, normalAngle)) hits++;
        }
        return samples < 5 ? 0.3 : (double)hits / samples;
    }

    private sealed class ColorAccumulator
    {
        public double R, G, B;
        public int Count;
        public void Add(double r, double g, double b) { R += r; G += g; B += b; Count++; }
        public (double R, double G, double B) Mean => Count == 0 ? (0, 0, 0) : (R / Count, G / Count, B / Count);
    }

    private static double ColorDistance((double R, double G, double B) a, double r, double g, double b) =>
        Math.Max(Math.Abs(a.R - r), Math.Max(Math.Abs(a.G - g), Math.Abs(a.B - b)));

    /// <summary>Fraction of a border side whose neighbourhood (a few px inside the frame) is closer in
    /// color to the sheet's interior than to the table, scaled into 0.3..1.</summary>
    private static double BorderSupport(PointD a, PointD b, PointD quadCentre,
        (double R, double G, double B) interiorMean, (double R, double G, double B) tableMean, EdgeMap e)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return 0.55;
        double nx = -dy / len, ny = dx / len;
        if (nx * (quadCentre.X - a.X) + ny * (quadCentre.Y - a.Y) < 0) { nx = -nx; ny = -ny; }

        int n = Math.Max(8, (int)(len / 2)), samples = 0, insideLike = 0;
        for (int k = 0; k < n; k++)
        {
            double t = (k + 0.5) / n;
            double px = a.X + dx * t + nx * (WindowRadius + 2), py = a.Y + dy * t + ny * (WindowRadius + 2);
            if (!e.MeanColor(px, py, WindowRadius, out double r, out double g, out double bl)) continue;
            samples++;
            if (ColorDistance(interiorMean, r, g, bl) < ColorDistance(tableMean, r, g, bl)) insideLike++;
        }
        return samples < 5 ? 0.55 : 0.3 + 0.7 * insideLike / samples;
    }

    private static string Describe(Candidate c, List<Line> lines, EdgeMap e) =>
        string.Join(' ', c.Quad.ToArray().Select(p => $"({p.X:0},{p.Y:0})")) + " sides " +
        string.Join('/', SideSupports(c, lines, e, out _).Select((v, i) => lines[c.SideLine[i]].IsBorder ? "B" : v.ToString("0.00")));

    private static Scored Evaluate(Candidate c, List<Line> lines, EdgeMap e, double toBeat)
    {
        // Only the part inside the picture counts, and a part outside it costs a little: otherwise an outline reaching
        // past the frame (into the table, beyond the sheet) grows for free and beats the sheet.
        double visible = VisibleArea(c.Quad, e.W, e.H);
        double areaFraction = visible / ((double)e.W * e.H);
        double outsideShare = 1 - visible / Math.Max(1, c.Quad.Area);
        double angleDev = MaxAngleDeviation(c.Quad) / PiOver180;
        double angleFactor = 1 - Math.Clamp((angleDev - 15) / 45, 0, 0.6);
        double shape = (0.35 + 0.65 * Math.Sqrt(areaFraction)) * angleFactor * (1 - 0.3 * outsideShare);
        // Best score this candidate could reach if every side not yet measured were perfect.
        // Most candidates are hopeless after one or two sides, so bail out early.
        if (shape <= toBeat) return new Scored(c, 0, 0);

        double[] support = SideSupports(c, lines, e, out double strength, toBeat, shape);
        if (support.Length == 0) return new Scored(c, 0, 0);
        double mean = support.Average(), min = support.Min();
        if (min < 0.15) return new Scored(c, 0, 0); // a side with no edge under it is not a sheet edge
        return new Scored(c, Math.Pow(mean, 1.5) * (0.5 + 0.5 * min) * shape, strength);
    }

    /// <summary>
    /// How much a side looks like the boundary of a sheet: the fraction of its length that has
    /// an edge pixel nearby whose gradient is perpendicular to the side, discounted by two
    /// checks on what lies on either side of the line:
    ///  - the two sides must differ in color over a window wide enough to span a couple of text
    ///    rows (a row of text inside a page has text on both sides, so it fails);
    ///  - the outside must not look like the paper itself (the blank margin outside a block of
    ///    text has plenty of edges and a real color change, but it is paper).
    /// </summary>
    private static double SideSupport(PointD a, PointD b, PointD quadCentre, (double R, double G, double B) paper, EdgeMap e, ColorAccumulator outside, double edgeFraction, out double edgeStrength)
    {
        edgeStrength = 0;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return 0;
        // Unit normal pointing into the quad.
        double nx = -dy / len, ny = dx / len;
        if (nx * (quadCentre.X - a.X) + ny * (quadCentre.Y - a.Y) < 0) { nx = -nx; ny = -ny; }

        int n = Math.Max(8, (int)(len / 2));
        int samples = 0, windowSamples = 0, contrastHits = 0, paperLikeHits = 0;
        double deltaSum = 0;
        for (int k = 0; k < n; k++)
        {
            double t = (k + 0.5) / n;
            double px = a.X + dx * t, py = a.Y + dy * t;
            int x = (int)Math.Round(px), y = (int)Math.Round(py);
            if (x < 0 || y < 0 || x >= e.W || y >= e.H) continue; // off-frame part: no evidence either way
            samples++;

            if (!e.MeanColor(px + nx * ContrastOffset, py + ny * ContrastOffset, WindowRadius, out double ri, out double gi, out double bi)) continue;
            if (!e.MeanColor(px - nx * ContrastOffset, py - ny * ContrastOffset, WindowRadius, out double ro, out double go, out double bo)) continue;
            windowSamples++;
            outside.Add(ro, go, bo);
            double delta = Math.Max(Math.Abs(ri - ro), Math.Max(Math.Abs(gi - go), Math.Abs(bi - bo)));
            deltaSum += delta;
            if (delta >= MinSideContrast) contrastHits++;
            if (Math.Max(Math.Abs(ro - paper.R), Math.Max(Math.Abs(go - paper.G), Math.Abs(bo - paper.B))) < PaperLikeTolerance) paperLikeHits++;
        }
        if (samples < 5) return 0.3;

        // Windows that would leave the frame say nothing: stay neutral rather than penalize.
        double contrastFraction = windowSamples < 5 ? 0.7 : (double)contrastHits / windowSamples;
        edgeStrength = windowSamples < 5 ? 0 : deltaSum / windowSamples;
        double paperLikeFraction = windowSamples < 5 ? 0 : (double)paperLikeHits / windowSamples;
        return edgeFraction * Math.Clamp(contrastFraction / 0.6, 0, 1) * (1 - 0.9 * paperLikeFraction);
    }

    private const double ContrastOffset = 7;        // px either side of the line (analysis scale)
    private const double MinSideContrast = 10;      // gray levels of difference in the strongest channel
    private const double PaperLikeTolerance = 14;   // outside this close to the paper color = still paper
    private const int WindowRadius = 4;             // 9x9 window: spans a couple of text rows

    private readonly record struct Interior((double R, double G, double B) Paper, (double R, double G, double B) Mean);

    /// <summary>The quad's interior colors: <c>Paper</c> = mean of the bright end (75th-80th
    /// percentile of luminance), which largely ignores ink and any table poking in at the
    /// corners; <c>Mean</c> = plain average (paper plus ink), what a window over the page looks like.</summary>
    private static Interior InteriorStats(Quad q, EdgeMap e)
    {
        PointD[] p = q.ToArray();
        int x0 = Math.Max(0, (int)p.Min(v => v.X)), x1 = Math.Min(e.W - 1, (int)Math.Ceiling(p.Max(v => v.X)));
        int y0 = Math.Max(0, (int)p.Min(v => v.Y)), y1 = Math.Min(e.H - 1, (int)Math.Ceiling(p.Max(v => v.Y)));
        var count = new int[256];
        var sumR = new long[256];
        var sumG = new long[256];
        var sumB = new long[256];
        int total = 0;
        for (int y = y0; y <= y1; y += 3)
        {
            // x-extent of the (convex) quad on this row: intersect the row with its four sides
            double left = double.MaxValue, right = double.MinValue;
            for (int i = 0; i < 4; i++)
            {
                PointD a = p[i], b = p[(i + 1) % 4];
                if ((y < a.Y && y < b.Y) || (y > a.Y && y > b.Y) || a.Y == b.Y) continue;
                double x = a.X + (b.X - a.X) * (y - a.Y) / (b.Y - a.Y);
                left = Math.Min(left, x);
                right = Math.Max(right, x);
            }
            if (left > right) continue;
            int xs = Math.Max(0, (int)Math.Ceiling(left)), xe = Math.Min(e.W - 1, (int)Math.Floor(right));
            for (int x = xs; x <= xe; x += 3)
            {
                int o = (y * e.W + x) * 3;
                byte r = e.Image.Data[o], g = e.Image.Data[o + 1], b = e.Image.Data[o + 2];
                int lum = (r * 299 + g * 587 + b * 114) / 1000;
                count[lum]++; sumR[lum] += r; sumG[lum] += g; sumB[lum] += b;
                total++;
            }
        }
        if (total == 0) return new Interior((255, 255, 255), (255, 255, 255));

        long from = (long)(total * 0.70), to = (long)(total * 0.80), seen = 0;
        double r0 = 0, g0 = 0, b0 = 0, n0 = 0, ra = 0, ga = 0, ba = 0;
        for (int l = 0; l < 256; l++)
        {
            ra += sumR[l]; ga += sumG[l]; ba += sumB[l];
            long lo = seen, hi = seen + count[l];
            seen = hi;
            if (hi <= from || lo >= to || count[l] == 0) continue;
            r0 += sumR[l]; g0 += sumG[l]; b0 += sumB[l]; n0 += count[l];
        }
        (double, double, double) paper = n0 == 0 ? (255, 255, 255) : (r0 / n0, g0 / n0, b0 / n0);
        return new Interior(paper, (ra / total, ga / total, ba / total));
    }

    private static bool HasEdgeNear(EdgeMap e, int x, int y, double normalAngle)
    {
        for (int yy = Math.Max(0, y - 2); yy <= Math.Min(e.H - 1, y + 2); yy++)
        {
            for (int xx = Math.Max(0, x - 2); xx <= Math.Min(e.W - 1, x + 2); xx++)
            {
                int i = yy * e.W + xx;
                if (e.Edge[i] && AngleDiff(e.Theta[i], normalAngle) <= 25 * PiOver180) return true;
            }
        }
        return false;
    }

    private static PointD? Intersect(Line a, Line b)
    {
        double det = a.Nx * b.Ny - a.Ny * b.Nx;
        if (Math.Abs(det) < 0.3) return null; // |sin(angle)| < 0.3: nearly parallel
        double x = (a.Rho * b.Ny - a.Ny * b.Rho) / det;
        double y = (a.Nx * b.Rho - a.Rho * b.Nx) / det;
        return new PointD(x, y);
    }

    /// <summary>Area of the quad inside the picture (the quad clipped to the frame, Sutherland-Hodgman).</summary>
    private static double VisibleArea(Quad q, int w, int h)
    {
        var poly = new List<PointD>(q.ToArray());
        // Each frame edge as (normal axis, limit, keep-below): x >= 0, x <= w, y >= 0, y <= h.
        foreach ((bool onX, double limit, bool below) in new[] { (true, 0.0, false), (true, (double)w, true), (false, 0.0, false), (false, (double)h, true) })
        {
            if (poly.Count == 0) break;
            var next = new List<PointD>(poly.Count + 2);
            for (int i = 0; i < poly.Count; i++)
            {
                PointD a = poly[i], b = poly[(i + 1) % poly.Count];
                double va = onX ? a.X : a.Y, vb = onX ? b.X : b.Y;
                bool inA = below ? va <= limit : va >= limit, inB = below ? vb <= limit : vb >= limit;
                if (inA) next.Add(a);
                if (inA != inB)
                {
                    double t = (limit - va) / (vb - va);
                    next.Add(new PointD(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
                }
            }
            poly = next;
        }
        double area = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            PointD a = poly[i], b = poly[(i + 1) % poly.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(area) / 2;
    }

    private static bool InsideFrame(Quad q, int w, int h, double margin)
    {
        foreach (PointD p in q.ToArray())
            if (p.X < -margin || p.Y < -margin || p.X > w - 1 + margin || p.Y > h - 1 + margin) return false;
        return true;
    }

    private static double MinSide(Quad q)
    {
        PointD[] p = q.ToArray();
        double m = double.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            PointD a = p[i], b = p[(i + 1) % 4];
            m = Math.Min(m, Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)));
        }
        return m;
    }

    /// <summary>Largest deviation of any interior angle from 90 degrees, in radians.</summary>
    private static double MaxAngleDeviation(Quad q)
    {
        PointD[] p = q.ToArray();
        double worst = 0;
        for (int i = 0; i < 4; i++)
        {
            PointD prev = p[(i + 3) % 4], cur = p[i], next = p[(i + 1) % 4];
            double ux = prev.X - cur.X, uy = prev.Y - cur.Y, vx = next.X - cur.X, vy = next.Y - cur.Y;
            double cosA = (ux * vx + uy * vy) / (Math.Sqrt(ux * ux + uy * uy) * Math.Sqrt(vx * vx + vy * vy) + 1e-12);
            double angle = Math.Acos(Math.Clamp(cosA, -1, 1));
            worst = Math.Max(worst, Math.Abs(angle - Math.PI / 2));
        }
        return worst;
    }

    /// <summary>Corners in TL, TR, BR, BL order: clockwise on screen, starting at the
    /// corner nearest the top-left of the frame.</summary>
    private static Quad Order(Quad q)
    {
        PointD[] p = q.ToArray();
        double mx = p.Average(v => v.X), my = p.Average(v => v.Y);
        PointD[] byAngle = p.OrderBy(v => Math.Atan2(v.Y - my, v.X - mx)).ToArray();
        int start = 0;
        for (int i = 1; i < 4; i++)
            if (byAngle[i].X + byAngle[i].Y < byAngle[start].X + byAngle[start].Y) start = i;
        return new Quad(byAngle[start], byAngle[(start + 1) % 4], byAngle[(start + 2) % 4], byAngle[(start + 3) % 4]);
    }

    #endregion
}
