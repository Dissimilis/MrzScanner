namespace Dissimilis.MrzScanner.Internal;

/// <summary>A candidate MRZ band region in image coordinates.</summary>
internal sealed class BandCandidate
{
    public BandCandidate(int top, int bottom, int left, int right, int lineCountEstimate, double score)
    {
        Top = top;
        Bottom = bottom;
        Left = left;
        Right = right;
        LineCountEstimate = lineCountEstimate;
        Score = score;
    }

    public int Top { get; }
    public int Bottom { get; }
    public int Left { get; }
    public int Right { get; }
    public int LineCountEstimate { get; }
    public double Score { get; }

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Finds MRZ-shaped bands: groups of 2 or 3 parallel rows of dense, wide,
/// monospaced text. Works on gradients, so it is orientation symmetric and
/// insensitive to polarity and moderate contrast changes.
/// </summary>
internal static class MrzLocator
{
    /// <summary>Diagnostic sink for the samples probe; unused in production.</summary>
    internal static Action<string>? Trace;

    private sealed class TextRun
    {
        public int Top;
        public int Bottom;
        public int Left;
        public int Right;
        public double Density;

        public int Height => Bottom - Top;
        public int Width => Right - Left;
    }

    /// <summary>Returns candidate bands, best first.</summary>
    public static List<BandCandidate> Locate(GrayImage image)
    {
        int width = image.Width;
        int height = image.Height;
        byte[] pixels = image.Pixels;

        // Horizontal gradient magnitude; MRZ characters produce dense vertical edges.
        var strong = new byte[width * height];
        long gradientSum = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 1; x < width - 1; x++)
            {
                int g = Math.Abs(pixels[row + x + 1] - pixels[row + x - 1]);
                gradientSum += g;
                if (g > 255)
                    g = 255;
                strong[row + x] = (byte)g;
            }
        }
        int threshold = Math.Max(18, (int)(3 * gradientSum / (width * (long)height)));

        // Binary strong-edge map and per row statistics.
        var rowCount = new int[height];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int count = 0;
            for (int x = 1; x < width - 1; x++)
            {
                if (strong[row + x] > threshold)
                {
                    strong[row + x] = 1;
                    count++;
                }
                else
                {
                    strong[row + x] = 0;
                }
            }
            rowCount[y] = count;
        }

        List<TextRun> runs = FindTextRuns(strong, rowCount, width, height);
        foreach (TextRun run in runs)
            Trace?.Invoke($"run y {run.Top}-{run.Bottom} x {run.Left}-{run.Right} density {run.Density:F3}");
        return GroupRunsIntoBands(runs, width, height);
    }

    private static List<TextRun> FindTextRuns(byte[] strong, int[] rowCount, int width, int height)
    {
        // Smooth the profile a little so single weak rows do not split a line.
        var smooth = new int[height];
        for (int y = 0; y < height; y++)
        {
            int sum = 0;
            int n = 0;
            for (int k = -1; k <= 1; k++)
            {
                int yy = y + k;
                if (yy >= 0 && yy < height)
                {
                    sum += rowCount[yy];
                    n++;
                }
            }
            smooth[y] = sum / n;
        }

        int max = 0;
        for (int y = 0; y < height; y++)
            max = Math.Max(max, smooth[y]);
        if (max < 20)
            return new List<TextRun>();

        int enter = Math.Max(12, max / 4);
        var runs = new List<TextRun>();
        int runStart = -1;
        for (int y = 0; y <= height; y++)
        {
            bool active = y < height && smooth[y] >= enter;
            if (active && runStart < 0)
            {
                runStart = y;
            }
            else if (!active && runStart >= 0)
            {
                AddRun(runs, runStart, y, strong, width, height);
                runStart = -1;
            }
        }
        return runs;
    }

    private static void AddRun(List<TextRun> runs, int top, int bottom, byte[] strong, int width, int height)
    {
        int runHeight = bottom - top;
        if (runHeight < 6 || runHeight > height / 4)
            return;

        // Column density inside the run rows. The extent comes from the widest
        // dense column run, so isolated vertical edges (card borders, photo
        // frames) far from the text do not stretch the band.
        var columnCount = new int[width];
        long strongTotal = 0;
        for (int y = top; y < bottom; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (strong[row + x] != 0)
                {
                    columnCount[x]++;
                    strongTotal++;
                }
            }
        }

        int minCount = Math.Max(1, runHeight / 5);
        int gapTolerance = Math.Max(8, width / 40);
        (int left, int right) = WidestActiveRun(columnCount, minCount, gapTolerance);
        int extent = right - left;
        if (extent < Math.Max(120, width / 5))
            return;

        runs.Add(new TextRun
        {
            Top = top,
            Bottom = bottom,
            Left = left,
            Right = right,
            Density = strongTotal / (double)(runHeight * Math.Max(1, extent)),
        });
    }

    /// <summary>
    /// Widest run of active columns, tolerating small inactive gaps
    /// (spacing between characters).
    /// </summary>
    internal static (int Left, int Right) WidestActiveRun(int[] columnCount, int minCount, int gapTolerance)
    {
        int bestLeft = 0;
        int bestRight = 0;
        int currentLeft = -1;
        int lastActive = -1;
        for (int x = 0; x <= columnCount.Length; x++)
        {
            bool active = x < columnCount.Length && columnCount[x] >= minCount;
            if (active)
            {
                if (currentLeft < 0 || x - lastActive > gapTolerance)
                    currentLeft = x;
                lastActive = x;
                if (lastActive + 1 - currentLeft > bestRight - bestLeft)
                {
                    bestLeft = currentLeft;
                    bestRight = lastActive + 1;
                }
            }
            else if (currentLeft >= 0 && (x == columnCount.Length || x - lastActive > gapTolerance))
            {
                currentLeft = -1;
            }
        }
        return (bestLeft, bestRight);
    }

    private static List<BandCandidate> GroupRunsIntoBands(List<TextRun> runs, int width, int height)
    {
        var candidates = new List<BandCandidate>();

        // Consecutive runs with similar extent and small gaps form a band.
        for (int first = 0; first < runs.Count; first++)
        {
            var group = new List<TextRun> { runs[first] };
            for (int next = first + 1; next < runs.Count && group.Count < 3; next++)
            {
                TextRun previous = group[group.Count - 1];
                TextRun candidate = runs[next];
                int gap = candidate.Top - previous.Bottom;
                int lineHeight = Math.Max(previous.Height, candidate.Height);
                bool similarHeight = Math.Min(previous.Height, candidate.Height) * 2 >= lineHeight;

                // Column overlap, not endpoint distance: a run's extent can
                // stretch into flanking decoration (photo ghosts, card edges)
                // by far more than a few line heights without changing where
                // the text actually is.
                int overlap = Math.Min(candidate.Right, previous.Right) - Math.Max(candidate.Left, previous.Left);
                int minWidth = Math.Min(previous.Width, candidate.Width);
                int maxWidth = Math.Max(previous.Width, candidate.Width);
                bool similarExtent = overlap >= (int)(0.75 * minWidth) && minWidth * 100 >= maxWidth * 55;

                // The gap allowance is generous because faint print thins the
                // detected runs to their dense core, which exaggerates the gap
                // between two normally spaced MRZ lines.
                if (gap >= 0 && gap <= lineHeight * 3 && similarHeight && similarExtent)
                    group.Add(candidate);
                else
                    break;
            }

            if (group.Count < 2)
            {
                // A single tall run can be an MRZ whose line gaps blurred together.
                TextRun run = group[0];
                double aspect = run.Width / (double)run.Height;
                if (aspect > 6 && aspect < 26 && run.Height > 14)
                {
                    candidates.Add(MakeCandidate(group, width, height, lineEstimate: run.Height * 3 > run.Width / 8 ? 3 : 2));
                }
                continue;
            }

            candidates.Add(MakeCandidate(group, width, height, group.Count));
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (candidates.Count > 4)
            candidates.RemoveRange(4, candidates.Count - 4);
        return candidates;
    }

    private static BandCandidate MakeCandidate(List<TextRun> group, int width, int height, int lineEstimate)
    {
        int top = group[0].Top;
        int bottom = group[group.Count - 1].Bottom;
        int left = int.MaxValue;
        int right = 0;
        double density = 0;
        foreach (TextRun run in group)
        {
            left = Math.Min(left, run.Left);
            right = Math.Max(right, run.Right);
            density += run.Density;
        }
        density /= group.Count;

        int bandWidth = right - left;
        double widthScore = bandWidth / (double)width;
        double lineScore = group.Count >= 2 ? 1.0 : 0.55;

        // Character aspect plausibility: an MRZ line is 30 to 44 cells wide, and cells
        // are taller than wide, so line height should sit near width / cells * 1.2.
        double lineHeight = (bottom - top) / (double)Math.Max(1, group.Count == 1 ? lineEstimate : group.Count);
        double expectedMin = bandWidth / 50.0;
        double expectedMax = bandWidth / 14.0;
        double aspectScore = lineHeight >= expectedMin && lineHeight <= expectedMax ? 1.0 : 0.4;

        double score = widthScore * lineScore * aspectScore * Math.Min(1.0, density * 4);

        // Crop margins: half a line height vertically. Horizontally three line
        // heights, because the gradient threshold can clip faint edge fillers
        // (busy backgrounds inflate the global gradient mean); the recognizer's
        // adaptive binarization re-measures the true extent inside the crop.
        int margin = Math.Max(3, (int)(lineHeight / 2));
        int marginX = Math.Max(6, (int)lineHeight);
        top = Math.Max(0, top - margin);
        bottom = Math.Min(height, bottom + margin);
        left = Math.Max(0, left - marginX);
        right = Math.Min(width, right + marginX);

        return new BandCandidate(top, bottom, left, right, lineEstimate, score);
    }
}
