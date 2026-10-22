using ScaleMail.Core.Model;

namespace ScaleMail.Core.Detection;

public sealed class DetectionOptions
{
    /// <summary>Pixels darker than this (0-255) are treated as outline.</summary>
    public int WallThreshold { get; set; } = 140;
    /// <summary>Thickens outlines by this many pixels to close small gaps in the drawing.</summary>
    public int GapClosing { get; set; } = 0;
    /// <summary>Regions smaller than this fraction of the median region are not scales.</summary>
    public double MinAreaFraction { get; set; } = 0.25;
    /// <summary>Brightness split between plain and coloured scales; null = automatic.</summary>
    public double? ColorThreshold { get; set; }
    /// <summary>Scale length as a multiple of the row pitch (how deeply scales overlap).</summary>
    public double LengthInRows { get; set; } = 3.0;
}

/// <summary>Turns a line-art scale template into a <see cref="ScaleMesh"/>.</summary>
public static class ScaleDetector
{
    public static ScaleMesh Detect(byte[] rgba, int width, int height, DetectionOptions? options = null)
    {
        options ??= new DetectionOptions();
        var lum = new byte[width * height];
        for (int i = 0; i < lum.Length; i++)
        {
            int p = i * 4;
            int a = rgba[p + 3];
            // Composite on white so transparent PNGs behave.
            double r = (rgba[p] * a + 255 * (255 - a)) / 255.0;
            double g = (rgba[p + 1] * a + 255 * (255 - a)) / 255.0;
            double b = (rgba[p + 2] * a + 255 * (255 - a)) / 255.0;
            lum[i] = (byte)(0.299 * r + 0.587 * g + 0.114 * b);
        }
        return DetectGray(lum, width, height, options);
    }

    public static ScaleMesh DetectGray(byte[] lum, int width, int height, DetectionOptions options)
    {
        var wall = new bool[width * height];
        for (int i = 0; i < wall.Length; i++) wall[i] = lum[i] < options.WallThreshold;
        if (options.GapClosing > 0) wall = Dilate(wall, width, height, options.GapClosing);

        var (labels, regions) = Label(wall, lum, width, height);

        var mesh = new ScaleMesh { ImageWidth = width, ImageHeight = height };
        if (regions.Count == 0) return mesh;

        // Background = the biggest region touching the image border.
        var background = regions.Where(r => r.TouchesBorder).OrderByDescending(r => r.Area).FirstOrDefault();
        var candidates = regions.Where(r => r != background && r.Area >= 12).ToList();
        if (candidates.Count == 0) return mesh;

        double median = Median(candidates.Select(r => (double)r.Area));
        // Anything enormous is a stray background pocket, not a scale.
        candidates = candidates.Where(r => r.Area <= median * 6).ToList();
        median = Median(candidates.Select(r => (double)r.Area));

        double minArea = median * options.MinAreaFraction;
        var scaleRegions = candidates.Where(r => r.Area >= minArea).ToList();
        var holeRegions = candidates.Where(r => r.Area < minArea).ToList();

        mesh.DetectedHoles = holeRegions.Select(r => new Pt(r.SumX / r.Area, r.SumY / r.Area)).ToList();

        // Colour split.
        double split = options.ColorThreshold ?? AutoThreshold(scaleRegions.Select(r => r.MeanLum).ToList());

        var scales = new List<Scale>();
        var regionToScale = new Dictionary<int, int>();
        foreach (var r in scaleRegions)
        {
            var s = new Scale
            {
                Id = scales.Count,
                Center = new Pt(r.SumX / r.Area, r.SumY / r.Area),
                Tip = new Pt(r.TipX, r.MaxY),
                VisibleArea = r.Area,
                Shade = r.MeanLum,
                Colored = r.MeanLum < split,
            };
            regionToScale[r.Label] = s.Id;
            scales.Add(s);
        }
        mesh.Scales = scales;

        AssignRows(mesh);
        EstimateMetrics(mesh, options);
        MeshTopology.BuildNeighbors(mesh);
        return mesh;
    }

    // ---- labelling -------------------------------------------------------------------------

    private sealed class Region
    {
        public int Label;
        public int Area;
        public double SumX, SumY, SumLum;
        public int MaxY = int.MinValue, TipX;
        public bool TouchesBorder;
        public double MeanLum => SumLum / Area;
    }

    private static (int[] labels, List<Region> regions) Label(bool[] wall, byte[] lum, int w, int h)
    {
        var labels = new int[w * h];
        var regions = new List<Region>();
        var stack = new Stack<int>();
        for (int start = 0; start < labels.Length; start++)
        {
            if (wall[start] || labels[start] != 0) continue;
            var reg = new Region { Label = regions.Count + 1 };
            labels[start] = reg.Label;
            stack.Push(start);
            double tipXSum = 0; int tipCount = 0;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int x = p % w, y = p / w;
                reg.Area++; reg.SumX += x; reg.SumY += y; reg.SumLum += lum[p];
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) reg.TouchesBorder = true;
                if (y > reg.MaxY) { reg.MaxY = y; tipXSum = x; tipCount = 1; }
                else if (y == reg.MaxY) { tipXSum += x; tipCount++; }
                if (x > 0) Push(p - 1);
                if (x < w - 1) Push(p + 1);
                if (y > 0) Push(p - w);
                if (y < h - 1) Push(p + w);
            }
            reg.TipX = (int)Math.Round(tipXSum / Math.Max(1, tipCount));
            regions.Add(reg);

            void Push(int q)
            {
                if (!wall[q] && labels[q] == 0) { labels[q] = reg.Label; stack.Push(q); }
            }
        }
        return (labels, regions);
    }

    private static bool[] Dilate(bool[] src, int w, int h, int radius)
    {
        var cur = src;
        for (int it = 0; it < radius; it++)
        {
            var next = (bool[])cur.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = y * w + x;
                    if (cur[p]) continue;
                    if ((x > 0 && cur[p - 1]) || (x < w - 1 && cur[p + 1]) || (y > 0 && cur[p - w]) || (y < h - 1 && cur[p + w]))
                        next[p] = true;
                }
            cur = next;
        }
        return cur;
    }

    // ---- statistics ------------------------------------------------------------------------

    internal static double Median(IEnumerable<double> values)
    {
        var a = values.OrderBy(v => v).ToArray();
        if (a.Length == 0) return 0;
        return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    /// <summary>Otsu split of region brightness; returns -1 (nothing coloured) when there is a single cluster.</summary>
    internal static double AutoThreshold(List<double> shades)
    {
        if (shades.Count == 0) return -1;
        var sorted = shades.OrderBy(v => v).ToList();
        double best = -1, bestVar = -1, bestGap = 0;
        double total = sorted.Sum();
        double sumLow = 0;
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            sumLow += sorted[i];
            int nLow = i + 1, nHigh = sorted.Count - nLow;
            double mLow = sumLow / nLow, mHigh = (total - sumLow) / nHigh;
            double between = nLow * nHigh * (mLow - mHigh) * (mLow - mHigh);
            if (between > bestVar)
            {
                bestVar = between; best = (sorted[i] + sorted[i + 1]) / 2; bestGap = mHigh - mLow;
            }
        }
        // Without a clear gap in brightness every scale is the same colour.
        return bestGap < 12 ? -1 : best;
    }

    // ---- lattice ---------------------------------------------------------------------------

    private static void AssignRows(ScaleMesh mesh)
    {
        var scales = mesh.Scales;
        if (scales.Count == 0) return;
        // Rows are clustered on the tip y: tips of one row line up even when edge scales are clipped.
        var ordered = scales.OrderBy(s => s.Tip.Y).ToList();
        var gaps = new List<double>();
        for (int i = 1; i < ordered.Count; i++) gaps.Add(ordered[i].Tip.Y - ordered[i - 1].Tip.Y);
        // Typical in-row jitter is small, row-to-row jumps are large: split at gaps above 40% of the biggest "typical" jump.
        var big = gaps.Where(g => g > 2).OrderBy(g => g).ToList();
        double pitch = big.Count == 0 ? 1 : EstimatePitch(big);
        double tol = pitch * 0.45;

        int row = 0;
        double rowRef = ordered[0].Tip.Y;
        var rowMembers = new List<Scale> { ordered[0] };
        var rows = new List<List<Scale>> { rowMembers };
        foreach (var s in ordered.Skip(1))
        {
            if (s.Tip.Y - rowMembers.Average(m => m.Tip.Y) > tol)
            {
                rowMembers = new List<Scale>();
                rows.Add(rowMembers);
                row++;
            }
            rowMembers.Add(s);
            s.Row = row;
        }
        ordered[0].Row = 0;
        foreach (var r in rows)
        {
            int c = 0;
            foreach (var s in r.OrderBy(s => s.Center.X)) s.Col = c++;
        }
        mesh.RowCount = rows.Count;
    }

    /// <summary>The row pitch is the typical size of the "large" gaps between sorted tip positions.</summary>
    private static double EstimatePitch(List<double> sortedGaps)
    {
        // The biggest gap cluster starts at the largest jump in the sorted list.
        int cut = 0; double bestJump = 0;
        for (int i = 1; i < sortedGaps.Count; i++)
        {
            double j = sortedGaps[i] - sortedGaps[i - 1];
            if (j > bestJump) { bestJump = j; cut = i; }
        }
        var upper = sortedGaps.Skip(cut).ToList();
        return Median(upper);
    }

    private static void EstimateMetrics(ScaleMesh mesh, DetectionOptions options)
    {
        var rowCenters = mesh.Scales.GroupBy(s => s.Row).OrderBy(g => g.Key).Select(g => g.Average(s => s.Tip.Y)).ToList();
        var diffs = new List<double>();
        for (int i = 1; i < rowCenters.Count; i++) diffs.Add(rowCenters[i] - rowCenters[i - 1]);
        mesh.RowPitch = diffs.Count > 0 ? Median(diffs) : 0;

        // Width: spacing of neighbours in the same row is half a scale in staggered layouts, so use the
        // spacing between scales two columns apart in the same row... measure directly from same-row gaps instead.
        var gaps = new List<double>();
        foreach (var g in mesh.Scales.GroupBy(s => s.Row))
        {
            var xs = g.OrderBy(s => s.Center.X).Select(s => s.Center.X).ToList();
            for (int i = 1; i < xs.Count; i++) gaps.Add(xs[i] - xs[i - 1]);
        }
        mesh.ScaleWidth = gaps.Count > 0 ? Median(gaps) : 0;
        mesh.ScaleLength = mesh.RowPitch * options.LengthInRows;
    }
}
