namespace Dissimilis.MrzScanner.Internal;

/// <summary>One recognized character cell with ranked candidates.</summary>
internal sealed class CellRead
{
    public CellRead(char[] chars, float[] scores, float[] bitmap)
    {
        Chars = chars;
        Scores = scores;
        Bitmap = bitmap;
        Chosen = chars[0];
        ChosenScore = scores[0];
    }

    /// <summary>Candidate characters, best first.</summary>
    public char[] Chars { get; }

    /// <summary>Correlation scores matching <see cref="Chars" />.</summary>
    public float[] Scores { get; }

    /// <summary>The normalized cell image in template space, for the adaptive pass.</summary>
    public float[] Bitmap { get; }

    public char Chosen { get; set; }
    public float ChosenScore { get; set; }

    /// <summary>Vertical sampling offset of the best alignment, for grid refitting.</summary>
    public int OffsetY { get; set; }

    public float ScoreFor(char c)
    {
        for (int i = 0; i < Chars.Length; i++)
        {
            if (Chars[i] == c)
                return Scores[i];
        }
        return -1f;
    }

    /// <summary>Score against any template character, not just the stored top candidates.</summary>
    public float ScoreAgainst(char c)
    {
        float known = ScoreFor(c);
        if (known >= -0.999f)
            return known;
        int index = OcrTemplates.IndexOf(c);
        if (index < 0)
            return -1f;
        float best = float.MinValue;
        foreach (float[] template in OcrTemplates.Variants[index])
        {
            float dot = MathKernels.Dot(Bitmap, template);
            if (dot > best)
                best = dot;
        }
        return best;
    }
}

/// <summary>A recognized MRZ band: lines of cells plus quality information.</summary>
internal sealed class BandRead
{
    public BandRead(List<List<CellRead>> lines, double meanScore, double geometryPenalty)
    {
        Lines = lines;
        MeanScore = meanScore;
        GeometryPenalty = geometryPenalty;
    }

    public List<List<CellRead>> Lines { get; }
    public double MeanScore { get; }

    /// <summary>Cells the checksum search replaced. High counts mean manufactured validity.</summary>
    public int Coercions { get; set; }

    /// <summary>
    /// Penalty for doubled glyph captures and large centering shifts. A wrong
    /// cell count reads individual glyphs fine but skips or doubles some.
    /// </summary>
    public double GeometryPenalty { get; }

    /// <summary>Score used to pick between cell count hypotheses.</summary>
    public double HypothesisScore => MeanScore - GeometryPenalty;

    public string LineText(int index)
    {
        var cells = Lines[index];
        var chars = new char[cells.Count];
        for (int i = 0; i < cells.Count; i++)
            chars[i] = cells[i].Chosen;
        return new string(chars);
    }

    public List<string> AllText()
    {
        var result = new List<string>(Lines.Count);
        for (int i = 0; i < Lines.Count; i++)
            result.Add(LineText(i));
        return result;
    }
}

/// <summary>
/// Recognizes the characters of a cropped MRZ band: deskews, segments lines and
/// cells on the fixed OCR-B pitch, and template matches every cell.
/// </summary>
internal static class BandRecognizer
{
    private const int TopK = 8;
    private const int MinPitchLag = 5;

    /// <summary>Diagnostic sink for the samples probe; unused in production.</summary>
    internal static Action<string>? Trace;

    /// <summary>Harness diagnostics: probe counts and time inside template matching.</summary>
    internal static long UniqueMatches;
    internal static long CachedMatches;
    internal static long MatchTicks;
    internal static long PrepareTicks;

    /// <summary>
    /// Recognizes the band. Commits to one cell count (a wrong count competing
    /// downstream gets coerced into a plausible wrong format), but keeps grid
    /// phase runners-up of that count: ink alone can't tell a phantom-margin
    /// grid from the true one, so check digits get to arbitrate.
    /// </summary>
    public static List<BandRead> Recognize(GrayImage crop, CancellationToken ct, bool thorough = true)
    {
        List<BandRead> all = RecognizeAllHypotheses(crop, ct, thorough);
        all.Sort((a, b) => b.HypothesisScore.CompareTo(a.HypothesisScore));
        var results = new List<BandRead>();
        const int maxSurvivors = 4;
        foreach (BandRead band in all)
        {
            if (results.Count >= maxSurvivors)
                break;
            if (results.Count > 0 &&
                (band.Lines.Count != results[0].Lines.Count ||
                 band.Lines[0].Count != results[0].Lines[0].Count))
            {
                continue;
            }
            results.Add(band);
        }

        // Also keep the best band of each losing count; on blurry crops the
        // hypothesis score sometimes picks wrong and ranking sorts it out.
        foreach (BandRead band in all)
        {
            bool shapeSeen = false;
            foreach (BandRead kept in results)
            {
                if (band.Lines.Count == kept.Lines.Count && band.Lines[0].Count == kept.Lines[0].Count)
                {
                    shapeSeen = true;
                    break;
                }
            }
            if (!shapeSeen)
                results.Add(band);
        }
        return results;
    }

    /// <summary>Harness diagnostic: build a band at a forced grid, no refinement.</summary>
    internal static (GrayImage Gray, byte[] Ink, List<(int Top, int Bottom)> Spans)? PrepareBand(GrayImage crop)
    {
        int maxInkRun = Math.Max(16, crop.Height / 2);
        byte[] ink = Binarize(crop, maxInkRun);
        double shear = FindBestShear(ink, crop.Width, crop.Height);
        GrayImage gray = crop;
        if (Math.Abs(shear) > 0.002)
        {
            gray = ApplyVerticalShear(crop, shear);
            ink = Binarize(gray, maxInkRun);
        }
        List<(int Top, int Bottom)> spans = SegmentLines(ink, gray.Width, gray.Height);
        return (gray, ink, spans);
    }

    internal static BandRead? BuildAtGrid(
        GrayImage gray, byte[] ink, List<(int Top, int Bottom)> spans, int count, double left, double pitch)
    {
        if (spans.Count == 3 && count != 30)
            spans = new List<(int Top, int Bottom)> { spans[1], spans[2] };
        if (spans.Count is not (2 or 3))
            return null;

        var lines = new List<List<CellRead>>(spans.Count);
        double scoreSum = 0;
        int cellCount = 0;
        double penaltySum = 0;
        foreach ((int top, int bottom) in spans)
        {
            int[] profile = ColumnProfile(ink, gray.Width, top, bottom);
            (List<CellRead> cells, double linePenalty) = BuildCells(
                gray, profile, top, bottom, count, left, pitch, new MatchCache(), rigid: true);
            lines.Add(cells);
            penaltySum += linePenalty;
            foreach (CellRead cell in cells)
            {
                scoreSum += cell.ChosenScore;
                cellCount++;
            }
        }
        return new BandRead(lines, scoreSum / Math.Max(1, cellCount), penaltySum / spans.Count);
    }

    /// <summary>All cell count hypotheses, for diagnostics and selection.</summary>
    internal static List<BandRead> RecognizeAllHypotheses(GrayImage crop, CancellationToken ct, bool thorough = true)
    {
        var results = new List<BandRead>();
        if (crop.Width < 80 || crop.Height < 12)
            return results;

        // Strokes scale with line height; wider dark runs are background.
        long tp = System.Diagnostics.Stopwatch.GetTimestamp();
        int maxInkRun = Math.Max(16, crop.Height / 2);
        byte[] ink = Binarize(crop, maxInkRun);
        double shear = FindBestShear(ink, crop.Width, crop.Height);
        PrepareTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tp;
        Trace?.Invoke($"crop {crop.Width}x{crop.Height} shear {shear:F3}");
        GrayImage gray = crop;
        if (Math.Abs(shear) > 0.002)
        {
            gray = ApplyVerticalShear(crop, shear);
            ink = Binarize(gray, maxInkRun);
        }

        List<(int Top, int Bottom)> lines = SegmentLines(ink, gray.Width, gray.Height);
        Trace?.Invoke("spans: " + string.Join(" ", lines.Select(l => $"{l.Top}-{l.Bottom}")));

        // 4+ spans: the crop caught print above or below the MRZ. Take the
        // densest window of adjacent spans.
        if (lines.Count is > 3 and <= 7)
            lines = DensestSpanWindow(ink, gray.Width, lines);
        if (lines.Count is not (2 or 3))
            return results;

        ct.ThrowIfCancellationRequested();

        // Three spans is usually TD1, but a stray print span above/below a
        // 2-line MRZ must not force that, so the adjacent pairs compete too.
        var hypotheses = new List<(List<(int Top, int Bottom)> Spans, int Count)>();
        if (lines.Count == 3)
        {
            hypotheses.Add((lines, 30));
            var topPair = new List<(int Top, int Bottom)> { lines[0], lines[1] };
            var bottomPair = new List<(int Top, int Bottom)> { lines[1], lines[2] };
            foreach (var pair in new[] { topPair, bottomPair })
            {
                hypotheses.Add((pair, 44));
                hypotheses.Add((pair, 36));
            }
        }
        else
        {
            hypotheses.Add((lines, 44));
            hypotheses.Add((lines, 36));
        }

        var cache = new MatchCache();
        foreach ((List<(int Top, int Bottom)> spans, int count) in hypotheses)
        {
            ct.ThrowIfCancellationRequested();
            results.AddRange(RecognizeWithCount(gray, ink, spans, count, ct, cache, thorough));

            // A near-perfect read ends the search; each extra hypothesis is a
            // full matching pass.
            foreach (BandRead band in results)
            {
                if (band.HypothesisScore >= 0.90)
                    return results;
            }
        }
        return results;
    }

    /// <summary>Densest window of 3 (preferred) or 2 adjacent spans.</summary>
    private static List<(int Top, int Bottom)> DensestSpanWindow(
        byte[] ink, int width, List<(int Top, int Bottom)> spans)
    {
        var spanInk = new long[spans.Count];
        for (int i = 0; i < spans.Count; i++)
        {
            (int top, int bottom) = spans[i];
            long sum = 0;
            for (int y = top; y < bottom; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                    sum += ink[row + x];
            }
            spanInk[i] = sum;
        }

        List<(int Top, int Bottom)>? best = null;
        double bestInk = -1;
        foreach (int size in new[] { 3, 2 })
        {
            for (int start = 0; start + size <= spans.Count; start++)
            {
                double total = 0;
                for (int i = 0; i < size; i++)
                    total += spanInk[start + i];

                // A pair plus a weak neighbor always sums higher than the
                // pair alone, hence the margin.
                double weighted = size == 3 ? total : total * 1.15;
                if (weighted > bestInk)
                {
                    bestInk = weighted;
                    best = spans.GetRange(start, size);
                }
            }
        }
        return best ?? spans;
    }

    private static List<BandRead> RecognizeWithCount(
        GrayImage gray, byte[] ink, List<(int Top, int Bottom)> lineSpans, int count, CancellationToken ct, MatchCache cache,
        bool thorough = true)
    {
        // All lines share one physical grid. Estimate per line and let the
        // best-aligned line govern; chevron-heavy name lines mislead their own.
        var analyses = new (int[] ColumnInk, List<(double Left, double Pitch, bool Narrow)> Grids, double Score)[lineSpans.Count];
        int bestLine = -1;
        for (int i = 0; i < lineSpans.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            (int top, int bottom) = lineSpans[i];
            (int[]? columnInk, List<(double, double, bool)> grids, double score) = AnalyzeLineGrid(gray, ink, top, bottom, count);
            if (columnInk is null)
                return new List<BandRead>();
            analyses[i] = (columnInk, grids, score);
            if (bestLine < 0 || score > analyses[bestLine].Score)
                bestLine = i;
        }

        // Ink alignment can't always pick the grid on tiny blurry crops, so
        // every distinct candidate is built in full and judged by template fit.
        // Governing line first; the cap trims the least trusted.
        var sharedGrids = new List<(double Left, double Pitch, bool Narrow)>(analyses[bestLine].Grids);
        for (int i = 0; i < lineSpans.Count; i++)
        {
            if (i == bestLine)
                continue;
            foreach ((double left, double pitch, bool narrow) in analyses[i].Grids)
            {
                bool duplicate = false;
                foreach ((double seenLeft, double seenPitch, _) in sharedGrids)
                {
                    if (Math.Abs(seenLeft - left) <= 3 && Math.Abs(seenPitch - pitch) <= 0.2)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                    sharedGrids.Add((left, pitch, narrow));
            }
        }
        // Video effort: marginal grids rarely win and each costs a full build.
        int gridBudget = thorough ? 8 : 2;
        if (sharedGrids.Count > gridBudget)
            sharedGrids.RemoveRange(gridBudget, sharedGrids.Count - gridBudget);

        var built = new List<BandRead>();
        foreach ((double sharedLeft, double sharedPitch, bool narrow) in sharedGrids)
        {
            ct.ThrowIfCancellationRequested();
            var lines = new List<List<CellRead>>(lineSpans.Count);
            double scoreSum = 0;
            int cellCount = 0;
            double penaltySum = 0;
            for (int i = 0; i < lineSpans.Count; i++)
            {
                (int top, int bottom) = lineSpans[i];
                (List<CellRead> cells, double linePenalty) = BuildCells(
                    gray, analyses[i].ColumnInk, top, bottom, count, sharedLeft, sharedPitch, cache, narrowRefine: narrow);
                lines.Add(cells);
                penaltySum += linePenalty;
                foreach (CellRead cell in cells)
                {
                    scoreSum += cell.ChosenScore;
                    cellCount++;
                }
            }
            var band = new BandRead(lines, scoreSum / Math.Max(1, cellCount), penaltySum / lineSpans.Count);
            Trace?.Invoke($"grid left {sharedLeft:F1} pitch {sharedPitch:F2}: mean {band.MeanScore:F3} penalty {band.GeometryPenalty:F3} hyp {band.HypothesisScore:F3}");
            built.Add(band);
        }

        // Keep near-tied runners-up; check digits decide downstream.
        built.Sort((a, b) => b.HypothesisScore.CompareTo(a.HypothesisScore));
        const double margin = 0.08;
        const int maxSurvivors = 3;
        var survivors = new List<BandRead>();
        foreach (BandRead band in built)
        {
            if (survivors.Count >= maxSurvivors)
                break;
            if (survivors.Count > 0 && band.HypothesisScore < survivors[0].HypothesisScore - margin)
                break;
            survivors.Add(band);
        }
        return survivors;
    }

    /// <summary>
    /// Estimates a line's character grid from its column ink profile: pitch
    /// from autocorrelation (harmonics included, chevrons have strong sub-cell
    /// structure), phase from a window alignment search.
    /// </summary>
    private static (int[]? ColumnInk, List<(double Left, double Pitch, bool Narrow)> Grids, double Score) AnalyzeLineGrid(
        GrayImage gray, byte[] ink, int top, int bottom, int count)
    {
        int width = gray.Width;
        var columnInk = new int[width];
        for (int y = top; y < bottom; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (ink[row + x] != 0)
                    columnInk[x]++;
            }
        }

        long totalInk = 0;
        for (int x = 0; x < width; x++)
            totalInk += columnInk[x];

        // extent/count is always a pitch candidate; autocorrelation sometimes
        // locks onto decoration instead.
        List<int> lags = EstimatePitchCandidates(columnInk, width);
        (int runLeft, int runRight) = MrzLocator.WidestActiveRun(
            columnInk,
            minCount: Math.Max(1, (bottom - top) / 8),
            gapTolerance: Math.Max(4, width / count));
        int extentLag = (int)Math.Round((runRight - runLeft) / (double)count);
        if (extentLag >= MinPitchLag && !lags.Contains(extentLag))
            lags.Add(extentLag);

        // Penalize uncovered ink: a compressed grid can win on alignment
        // alone while skipping edge glyphs.
        var placements = new List<(double Left, double Pitch, double Score)>();
        foreach (int lag in lags)
        {
            // Must fit the strip; below half its width is degenerate.
            if (lag * count > width || lag * count < width / 2)
                continue;
            foreach ((double left, double lagPitch, double score) in BestWindows(columnInk, width, count, lag))
            {
                long covered = InkBetween(columnInk, left, left + lagPitch * count);
                double adjusted = score - (totalInk - covered) / (0.5 * lagPitch);
                Trace?.Invoke($"  lag {lag} -> left {left:F1} pitch {lagPitch:F2} raw {score:F1} adj {adjusted:F1}");

                // Neighboring lags rediscover the same placement.
                bool duplicate = false;
                for (int i = 0; i < placements.Count; i++)
                {
                    if (Math.Abs(placements[i].Left - left) <= 3 && Math.Abs(placements[i].Pitch - lagPitch) <= 0.2)
                    {
                        if (adjusted > placements[i].Score)
                            placements[i] = (left, lagPitch, adjusted);
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                    placements.Add((left, lagPitch, adjusted));
            }
        }

        double bestWindowScore;
        if (placements.Count > 0)
        {
            placements.Sort((a, b) => b.Score.CompareTo(a.Score));
            bestWindowScore = placements[0].Score;
            Trace?.Invoke($"line {top}-{bottom} count {count}: window left {placements[0].Left:F1} pitch {placements[0].Pitch:F2} score {bestWindowScore:F1}");
        }
        else
        {
            // Fallback: extent of the widest dense column run.
            double fallbackPitch = (runRight - runLeft) / (double)count;
            bestWindowScore = 0;
            placements.Add((runLeft, fallbackPitch, 0));
            Trace?.Invoke($"line {top}-{bottom} count {count}: no pitch, extent {runLeft}-{runRight} pitch {fallbackPitch:F2}");
        }

        // Window grids refine widely (pitch is a guess); extent grids
        // narrowly (wide refinement walks away from a good pitch).
        var grids = new List<(double Left, double Pitch, bool Narrow)>();
        foreach ((double left, double gridPitch, _) in placements)
        {
            if (gridPitch >= 3.0)
                grids.Add((left, gridPitch, false));
        }

        // Extent-anchored grid: right even when the window landscape is too
        // blurry. Span = count-1 pitches + one glyph body (~0.7 pitch).
        double extentPitch = (runRight - runLeft) / (count - 0.3);
        if (extentPitch >= 3.0)
            grids.Add((runLeft, extentPitch, true));

        // Laminate edges merged into the run give the extent a phantom lead
        // cell; the one-pitch-shifted variant covers that case.
        if (extentPitch >= 3.0)
        {
            double shiftedLeft = runLeft + extentPitch;
            double shiftedPitch = (runRight - shiftedLeft) / (count - 0.3);
            if (shiftedPitch >= 3.0)
                grids.Add((shiftedLeft, shiftedPitch, true));
        }
        if (grids.Count == 0)
            return (null, grids, 0);
        return (columnInk, grids, bestWindowScore);
    }

    private static int[] ColumnProfile(byte[] ink, int width, int top, int bottom)
    {
        var columnInk = new int[width];
        for (int y = top; y < bottom; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (ink[row + x] != 0)
                    columnInk[x]++;
            }
        }
        return columnInk;
    }

    private static long InkBetween(int[] columnInk, double left, double right)
    {
        int a = Math.Max(0, (int)Math.Round(left));
        int b = Math.Min(columnInk.Length, (int)Math.Round(right));
        long sum = 0;
        for (int x = a; x < b; x++)
            sum += columnInk[x];
        return sum;
    }

    private static (List<CellRead> Cells, double GeometryPenalty) BuildCells(
        GrayImage gray, int[] columnInk, int top, int bottom, int count, double gridLeft, double pitch,
        MatchCache cache, bool rigid = false, bool narrowRefine = false)
    {
        int width = gray.Width;

        // Refine offset/pitch so boundaries land on ink minima.
        double refinedLeft = gridLeft;
        if (!rigid)
            (refinedLeft, pitch) = RefineGrid(columnInk, (int)gridLeft, pitch, count, narrowRefine ? 4 : 12);

        // Fixed window width per cell (varying widths break template scale).
        // The grid position tracks found glyph centers with a bounded gain:
        // perspective makes local pitch vary, and a rigid grid fitted to one
        // half of the line drifts a full cell on the other.
        int cellWidth = Math.Max(4, (int)Math.Round(pitch));
        var cells = new List<CellRead>(count);
        var centers = new int[count];
        int previousCenter = int.MinValue;
        int duplicateCaptures = 0;
        double shiftSum = 0;
        double runningLeft = refinedLeft;
        for (int i = 0; i < count; i++)
        {
            int x0 = Math.Max(0, Math.Min(width - 2, (int)Math.Round(runningLeft)));
            int x1 = Math.Max(x0 + 2, Math.Min(width - 1, (int)Math.Round(runningLeft + pitch)));
            double idealCenter = runningLeft + pitch / 2.0;
            int center = CenterOnGlyph(columnInk, x0, x1, width);
            shiftSum += Math.Abs(center - idealCenter) / pitch;
            if (previousCenter != int.MinValue && center - previousCenter < pitch * 0.55)
                duplicateCaptures++;
            previousCenter = center;
            centers[i] = center;
            cells.Add(MatchCell(gray, center - cellWidth / 2, center - cellWidth / 2 + cellWidth, top, bottom, cache));

            double correction = Math.Max(-0.3 * pitch, Math.Min(0.3 * pitch, center - idealCenter));
            runningLeft += pitch + 0.5 * correction;
        }

        // Second pass: fit a line through confident centers, rematch the weak.
        if (!rigid)
            RefitFromConfidentCells(gray, cells, centers, cellWidth, top, bottom, cache);

        // Wrong counts double up on glyphs, shift a lot, and leave ink
        // outside the span (36 cells over 44 glyphs leaves 8 uncovered).
        long totalInk = 0;
        long insideInk = 0;
        int spanLeft = (int)Math.Round(refinedLeft);
        int spanRight = (int)Math.Round(runningLeft);
        for (int x = 0; x < width; x++)
        {
            totalInk += columnInk[x];
            if (x >= spanLeft && x < spanRight)
                insideInk += columnInk[x];
        }
        double uncovered = totalInk > 0 ? 1.0 - insideInk / (double)totalInk : 0;
        double penalty = 1.2 * duplicateCaptures / count + 0.4 * shiftSum / count + 0.8 * uncovered;
        return (cells, penalty);
    }

    /// <summary>
    /// Fits center = a + b*i through confident cells and rematches the weak
    /// ones from the fitted grid. Removes accumulated drift.
    /// </summary>
    private static void RefitFromConfidentCells(
        GrayImage gray, List<CellRead> cells, int[] centers, int cellWidth, int top, int bottom, MatchCache cache)
    {
        const float confidentScore = 0.75f;
        int n = 0;
        double sumI = 0;
        double sumC = 0;
        double sumII = 0;
        double sumIC = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i].ChosenScore < confidentScore)
                continue;
            n++;
            sumI += i;
            sumC += centers[i];
            sumII += (double)i * i;
            sumIC += (double)i * centers[i];
        }
        if (n < 6)
            return;

        double denominator = n * sumII - sumI * sumI;
        if (Math.Abs(denominator) < 1e-9)
            return;
        double slope = (n * sumIC - sumI * sumC) / denominator;
        double intercept = (sumC - slope * sumI) / n;
        if (slope < 3)
            return;

        // One baseline per line; the median confident offset is it.
        var offsets = new List<int>();
        for (int i = 0; i < cells.Count; i++)
        {
            if (cells[i].ChosenScore >= confidentScore)
                offsets.Add(cells[i].OffsetY);
        }
        offsets.Sort();
        int baselineDy = offsets[offsets.Count / 2];

        // Rematch on the fitted grid; it wins unless clearly worse.
        for (int i = 0; i < cells.Count; i++)
        {
            int fittedCenter = (int)Math.Round(intercept + slope * i);
            int x0 = fittedCenter - cellWidth / 2;
            CellRead rematched = MatchCell(gray, x0, x0 + cellWidth, top + baselineDy, bottom + baselineDy, cache);
            if (rematched.ChosenScore > cells[i].ChosenScore - 0.12f)
            {
                cells[i] = rematched;
                centers[i] = fittedCenter;
            }
        }
    }

    /// <summary>
    /// Plausible character pitches from the periodicity of the column ink
    /// profile: the autocorrelation peak and its sub-harmonics. Empty when no
    /// confident period exists.
    /// </summary>
    private static List<int> EstimatePitchCandidates(int[] columnInk, int width)
    {
        int maxLag = width / 20;
        const int minLag = MinPitchLag;
        if (maxLag <= minLag)
            return new List<int>();

        double mean = 0;
        for (int x = 0; x < width; x++)
            mean += columnInk[x];
        mean /= width;

        double zeroLag = 0;
        for (int x = 0; x < width; x++)
        {
            double d = columnInk[x] - mean;
            zeroLag += d * d;
        }
        if (zeroLag < 1e-9)
            return new List<int>();

        double Correlation(int lag)
        {
            double sum = 0;
            for (int x = 0; x + lag < width; x++)
                sum += (columnInk[x] - mean) * (columnInk[x + lag] - mean);
            return sum / zeroLag;
        }

        int bestLag = 0;
        double bestValue = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double normalized = Correlation(lag);
            if (normalized > bestValue)
            {
                bestValue = normalized;
                bestLag = lag;
            }
        }
        if (bestValue < 0.25)
            return new List<int>();

        // The peak can be a multiple of the period; sub-harmonics compete.
        var candidates = new List<int> { bestLag };
        foreach (int divisor in new[] { 2, 3 })
        {
            int candidate = (int)Math.Round(bestLag / (double)divisor);
            if (candidate >= minLag && Correlation(candidate) >= 0.4 * bestValue)
                candidates.Add(candidate);
        }
        // ...and the fundamental can be weaker than its half, so multiples too.
        foreach (int factor in new[] { 2, 3 })
        {
            int candidate = bestLag * factor;
            if (candidate <= maxLag && Correlation(candidate) >= 0.4 * bestValue)
                candidates.Add(candidate);
        }
        return candidates;
    }

    /// <summary>
    /// Best grid placements for a given lag: bodies should hold ink,
    /// boundaries shouldn't. Sub-pixel pitch, since half a pixel of error is
    /// a full cell of drift across 44 cells.
    /// </summary>
    private static List<(double Left, double Pitch, double Score)> BestWindows(
        int[] columnInk, int width, int count, int pitchLag)
    {
        var prefix = new long[width + 1];
        for (int x = 0; x < width; x++)
            prefix[x + 1] = prefix[x] + columnInk[x];

        double Range(double a, double b)
        {
            int ia = Math.Max(0, Math.Min(width, (int)Math.Round(a)));
            int ib = Math.Max(ia, Math.Min(width, (int)Math.Round(b)));
            return prefix[ib] - prefix[ia];
        }

        // Chevron-heavy lines flatten the landscape; keep distinct runners-up
        // and let template fit arbitrate later.
        var top = new List<(double Left, double Pitch, double Score)>();
        void Offer(double start, double pitch, double score)
        {
            for (int i = 0; i < top.Count; i++)
            {
                if (Math.Abs(top[i].Left - start) <= pitch / 3 && Math.Abs(top[i].Pitch - pitch) < 0.5)
                {
                    if (score > top[i].Score)
                        top[i] = (start, pitch, score);
                    return;
                }
            }
            top.Add((start, pitch, score));
            top.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (top.Count > 3)
                top.RemoveAt(top.Count - 1);
        }

        for (double pitch = pitchLag - 1.6; pitch <= pitchLag + 1.65; pitch += 0.1)
        {
            int maxStart = width - (int)Math.Ceiling(pitch * count);
            if (maxStart < 0)
                continue;
            for (int start = 0; start <= maxStart; start++)
            {
                double score = 0;
                for (int i = 0; i < count; i++)
                {
                    double boundary = start + i * pitch;
                    double body = Range(boundary + 0.25 * pitch, boundary + 0.75 * pitch) / (0.5 * pitch);
                    double gap = Range(boundary - 0.12 * pitch, boundary + 0.12 * pitch) / (0.24 * pitch);
                    score += body - gap;
                }
                Offer(start, pitch, score);
            }
        }
        return top;
    }

    /// <summary>
    /// Center of the ink run nearest the cell middle, shift bounded so a
    /// neighbor glyph can't capture the window.
    /// </summary>
    private static int CenterOnGlyph(int[] columnInk, int x0, int x1, int width)
    {
        int cellWidth = x1 - x0;
        int extension = Math.Max(2, (int)(cellWidth * 0.35));
        int scanLeft = Math.Max(0, x0 - extension);
        int scanRight = Math.Min(width, x1 + extension);

        // Low activity threshold on purpose: a high one fragments D and L and
        // centers on the stem alone. Merged neighbors make the run too wide
        // and centering declines, which is the safe failure.
        const int activeThreshold = 2;
        double windowCenter = (x0 + x1) / 2.0;
        double bestCenter = double.NaN;
        double bestDistance = double.MaxValue;
        int runStart = -1;
        int emptyStreak = 0;
        for (int x = scanLeft; x <= scanRight; x++)
        {
            bool active = x < scanRight && columnInk[x] >= activeThreshold;
            if (active)
            {
                if (runStart < 0)
                    runStart = x;
                emptyStreak = 0;
            }
            else if (runStart >= 0 && (++emptyStreak >= 2 || x >= scanRight))
            {
                int runEnd = x - emptyStreak + 1;
                int runWidth = runEnd - runStart;
                // Ignore runs wider than a cell (merged neighbors) and tiny specks.
                if (runWidth >= 2 && runWidth <= cellWidth * 1.2)
                {
                    double center = (runStart + runEnd) / 2.0;
                    double distance = Math.Abs(center - windowCenter);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestCenter = center;
                    }
                }
                runStart = -1;
            }
        }

        if (double.IsNaN(bestCenter))
            return (int)Math.Round(windowCenter);
        int shift = (int)Math.Round(bestCenter - windowCenter);
        int maxShift = (int)(cellWidth * 0.35);
        shift = Math.Max(-maxShift, Math.Min(maxShift, shift));
        return (int)Math.Round(windowCenter) + shift;
    }

    /// <summary>
    /// Searches small offset and pitch corrections so that interior cell boundaries
    /// cross as little ink as possible over the whole line.
    /// </summary>
    private static (double Left, double Pitch) RefineGrid(int[] columnInk, int left, double pitch, int count, int scaleRange)
    {
        double bestLeft = left;
        double bestPitch = pitch;
        // Range covers per-line deviation from the shared seed (perspective
        // widens the nearer line); trusted seeds refine narrowly.
        long bestCost = long.MaxValue;
        for (int offsetStep = -10; offsetStep <= 10; offsetStep++)
        {
            double candidateLeft = left + offsetStep * pitch / 20.0;
            for (int scaleStep = -scaleRange; scaleStep <= scaleRange; scaleStep++)
            {
                double candidatePitch = pitch * (1 + scaleStep * 0.005);
                long cost = 0;
                for (int i = 1; i < count; i++)
                {
                    int x = (int)Math.Round(candidateLeft + i * candidatePitch);
                    if (x < 0 || x >= columnInk.Length)
                    {
                        cost += 1000;
                        continue;
                    }
                    cost += columnInk[x];
                }
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestLeft = candidateLeft;
                    bestPitch = candidatePitch;
                }
            }
        }
        return (bestLeft, bestPitch);
    }

    /// <summary>
    /// Match cache per crop, keyed by exact sampling rect. Competing grids
    /// probe overlapping windows constantly. Cloned on return, arbitration
    /// mutates cells in place.
    /// </summary>
    internal sealed class MatchCache
    {
        public readonly Dictionary<long, CellRead> Map = new Dictionary<long, CellRead>();

        public static long Key(int x0, int x1, int y0, int y1)
        {
            return ((long)(ushort)(x0 + 8192) << 48) |
                   ((long)(ushort)(x1 + 8192) << 32) |
                   ((long)(ushort)(y0 + 8192) << 16) |
                   (ushort)(y1 + 8192);
        }
    }

    private static CellRead CloneCell(CellRead cell)
    {
        return new CellRead((char[])cell.Chars.Clone(), (float[])cell.Scores.Clone(), cell.Bitmap)
        {
            Chosen = cell.Chosen,
            ChosenScore = cell.ChosenScore,
            OffsetY = cell.OffsetY,
        };
    }

    private static CellRead MatchCell(GrayImage gray, int x0, int x1, int y0, int y1, MatchCache cache)
    {
        // Phase error hits round glyphs first (O reads as C); stretched spans
        // shift glyphs vertically (T reads as Y). Probe offsets in both axes.
        int stepX = Math.Max(1, (x1 - x0) / 12);
        int stepY = Math.Max(1, (y1 - y0) / 16);
        CellRead? best = null;
        for (int dy = -stepY; dy <= stepY; dy += stepY)
        {
            for (int dx = -2 * stepX; dx <= 2 * stepX; dx += stepX)
            {
                CellRead candidate = CachedMatchCellAt(gray, x0 + dx, x1 + dx, y0 + dy, y1 + dy, cache);
                if (best is null || candidate.ChosenScore > best.ChosenScore)
                {
                    best = candidate;
                    best.OffsetY = dy;
                }
            }
        }
        return best!;
    }

    private static CellRead CachedMatchCellAt(GrayImage gray, int x0, int x1, int y0, int y1, MatchCache cache)
    {
        long key = MatchCache.Key(x0, x1, y0, y1);
        if (cache.Map.TryGetValue(key, out CellRead? cached))
        {
            CachedMatches++;
            return CloneCell(cached);
        }
        UniqueMatches++;
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        CellRead computed = MatchCellAt(gray, x0, x1, y0, y1);
        MatchTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t;
        cache.Map[key] = CloneCell(computed);
        return computed;
    }

    private static CellRead MatchCellAt(GrayImage gray, int x0, int x1, int y0, int y1)
    {
        // Bilinear resample of the inverted cell into template space.
        var cell = new float[OcrTemplates.Width * OcrTemplates.Height];
        double scaleX = (x1 - x0) / (double)OcrTemplates.Width;
        double scaleY = (y1 - y0) / (double)OcrTemplates.Height;
        for (int ty = 0; ty < OcrTemplates.Height; ty++)
        {
            double sy = y0 + (ty + 0.5) * scaleY - 0.5;
            int iy = (int)Math.Floor(sy);
            double fy = sy - iy;
            for (int tx = 0; tx < OcrTemplates.Width; tx++)
            {
                double sx = x0 + (tx + 0.5) * scaleX - 0.5;
                int ix = (int)Math.Floor(sx);
                double fx = sx - ix;
                double value =
                    Sample(gray, ix, iy) * (1 - fx) * (1 - fy) +
                    Sample(gray, ix + 1, iy) * fx * (1 - fy) +
                    Sample(gray, ix, iy + 1) * (1 - fx) * fy +
                    Sample(gray, ix + 1, iy + 1) * fx * fy;
                cell[ty * OcrTemplates.Width + tx] = (float)(255.0 - value);
            }
        }
        OcrTemplates.Normalize(cell);

        // Coarse prefilter: rank the alphabet at half resolution, full
        // correlation only for the leaders. Generous margin; the coarse pass
        // just has to keep the true char in the pool.
        var coarseCell = OcrTemplates.Downsample(cell);
        OcrTemplates.Normalize(coarseCell);
        Span<float> coarseScores = stackalloc float[OcrTemplates.Alphabet.Length];
        for (int t = 0; t < OcrTemplates.Coarse.Length; t++)
        {
            float best = float.MinValue;
            foreach (float[] coarse in OcrTemplates.Coarse[t])
            {
                float dot = MathKernels.Dot(coarseCell, coarse);
                if (dot > best)
                    best = dot;
            }
            coarseScores[t] = best;
        }
        const int survivors = 16;
        Span<int> keep = stackalloc int[survivors];
        Span<float> keepScores = stackalloc float[survivors];
        keepScores.Fill(float.MinValue);
        for (int t = 0; t < coarseScores.Length; t++)
        {
            for (int k = 0; k < survivors; k++)
            {
                if (coarseScores[t] > keepScores[k])
                {
                    for (int shift = survivors - 1; shift > k; shift--)
                    {
                        keepScores[shift] = keepScores[shift - 1];
                        keep[shift] = keep[shift - 1];
                    }
                    keepScores[k] = coarseScores[t];
                    keep[k] = t;
                    break;
                }
            }
        }

        // Crisp variants only here. Blurred/bold variants of complex glyphs
        // degrade into blobs that over-match anything round; they may boost a
        // plausible candidate below but never introduce one.
        var topChars = new char[TopK];
        var topScores = new float[TopK];
        for (int i = 0; i < TopK; i++)
            topScores[i] = float.MinValue;

        for (int s = 0; s < survivors; s++)
        {
            int t = keep[s];
            float dot = float.MinValue;
            float[][] variants = OcrTemplates.Variants[t];
            for (int v = 0; v < variants.Length; v += 2)
            {
                float variantDot = MathKernels.Dot(cell, variants[v]);
                if (variantDot > dot)
                    dot = variantDot;
            }

            for (int k = 0; k < TopK; k++)
            {
                if (dot > topScores[k])
                {
                    for (int shift = TopK - 1; shift > k; shift--)
                    {
                        topScores[shift] = topScores[shift - 1];
                        topChars[shift] = topChars[shift - 1];
                    }
                    topScores[k] = dot;
                    topChars[k] = OcrTemplates.Alphabet[t];
                    break;
                }
            }
        }

        // Confirmation with the blurred variants (odd indices); the crisp
        // ones were already scored above.
        for (int k = 0; k < TopK; k++)
        {
            int index = OcrTemplates.IndexOf(topChars[k]);
            if (index < 0)
                continue;
            float[][] all = OcrTemplates.Variants[index];
            for (int v = 1; v < all.Length; v += 2)
            {
                float dot = MathKernels.Dot(cell, all[v]);
                if (dot > topScores[k])
                    topScores[k] = dot;
            }
        }
        for (int a = 0; a < TopK - 1; a++)
        {
            for (int b = a + 1; b < TopK; b++)
            {
                if (topScores[b] > topScores[a])
                {
                    (topScores[a], topScores[b]) = (topScores[b], topScores[a]);
                    (topChars[a], topChars[b]) = (topChars[b], topChars[a]);
                }
            }
        }
        ApplyShapeTiebreaks(topChars, topScores, cell);
        return new CellRead(topChars, topScores, cell);
    }

    /// <summary>
    /// Some pairs differ in one small region that whole-template correlation
    /// can't see: Y vs T at top center, 6 vs D at top left. Close races
    /// among them get decided by that region directly.
    /// </summary>
    private static void ApplyShapeTiebreaks(char[] chars, float[] scores, float[] cell)
    {
        // Only pairs that measurably helped on the labeled set. Wider packs
        // of corner features flipped more correct cells than they fixed.
        DecidePair(chars, scores, cell, 'T', 'Y', TopCenterInk, higherMeansFirst: true);
        DecidePair(chars, scores, cell, 'D', '6', TopLeftInk, higherMeansFirst: true);
        DecidePair(chars, scores, cell, 'M', 'H', UpperCenterInk, higherMeansFirst: true);
    }

    private static void DecidePair(
        char[] chars, float[] scores, float[] cell, char first, char second, Func<float[], float> feature, bool higherMeansFirst)
    {
        int firstIndex = Array.IndexOf(chars, first);
        int secondIndex = Array.IndexOf(chars, second);
        if (firstIndex < 0 || secondIndex < 0)
            return;
        int leader = Math.Min(firstIndex, secondIndex);
        int trailer = Math.Max(firstIndex, secondIndex);
        if (leader != 0 || Math.Abs(scores[firstIndex] - scores[secondIndex]) > 0.15f)
            return;

        // Marginal measurements at low resolution must not override the
        // template ordering.
        float value = feature(cell);
        if (Math.Abs(value) < 0.025f)
            return;
        char preferred = value > 0 == higherMeansFirst ? first : second;
        if (chars[leader] == preferred)
            return;

        // Swap only the chars, not the scores; the refiner re-sorts by score
        // later and would undo a paired swap.
        (chars[leader], chars[trailer]) = (chars[trailer], chars[leader]);
    }

    /// <summary>Ink centroid and top/bottom inked rows; features track the glyph, not the window.</summary>
    private static (float CentroidX, int TopRow, int BottomRow) GlyphAnchor(float[] cell)
    {
        float weightSum = 0;
        float xSum = 0;
        int topRow = 0;
        int bottomRow = OcrTemplates.Height - 1;
        bool topFound = false;
        for (int y = 0; y < OcrTemplates.Height; y++)
        {
            bool rowInked = false;
            for (int x = 0; x < OcrTemplates.Width; x++)
            {
                float v = cell[y * OcrTemplates.Width + x];
                if (v <= 0.01f)
                    continue;
                weightSum += v;
                xSum += v * x;
                rowInked = true;
            }
            if (rowInked)
            {
                if (!topFound)
                {
                    topRow = y;
                    topFound = true;
                }
                bottomRow = y;
            }
        }
        return (weightSum > 0 ? xSum / weightSum : OcrTemplates.Width / 2f, topRow, bottomRow);
    }

    private static float RegionInk(float[] cell, int y0, int y1, int x0, int x1)
    {
        y0 = Math.Max(0, y0);
        y1 = Math.Min(OcrTemplates.Height, y1);
        x0 = Math.Max(0, x0);
        x1 = Math.Min(OcrTemplates.Width, Math.Max(x0 + 1, x1));
        float sum = 0;
        int n = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                sum += cell[y * OcrTemplates.Width + x];
                n++;
            }
        }
        return n > 0 ? sum / n : 0;
    }




    /// <summary>
    /// Upper half between the stems: M has diagonal ink there, H nothing
    /// until the crossbar. (The very top can't separate them; OCR-B's narrow
    /// M has the same notch as H.)
    /// </summary>
    private static float UpperCenterInk(float[] cell)
    {
        (float cx, int topRow, int bottomRow) = GlyphAnchor(cell);
        int y0 = topRow + (bottomRow - topRow) / 6;
        int y1 = topRow + (bottomRow - topRow) / 2;
        return RegionInk(cell, y0, y1,
            (int)(cx - OcrTemplates.Width / 8f), (int)(cx + OcrTemplates.Width / 8f) + 1);
    }

    /// <summary>Mean normalized ink just below the glyph top around its centroid column.</summary>
    internal static float TopCenterInk(float[] cell)
    {
        (float cx, int topRow, _) = GlyphAnchor(cell);
        int x0 = Math.Max(0, (int)(cx - OcrTemplates.Width / 8f));
        int x1 = Math.Min(OcrTemplates.Width, (int)(cx + OcrTemplates.Width / 8f) + 1);
        int y1 = Math.Min(OcrTemplates.Height, topRow + OcrTemplates.Height / 5);
        float sum = 0;
        int n = 0;
        for (int y = topRow; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                sum += cell[y * OcrTemplates.Width + x];
                n++;
            }
        }
        return n > 0 ? sum / n : 0;
    }

    /// <summary>Mean normalized ink just below the glyph top, left of its centroid column.</summary>
    private static float TopLeftInk(float[] cell)
    {
        (float cx, int topRow, _) = GlyphAnchor(cell);
        int x0 = Math.Max(0, (int)(cx - OcrTemplates.Width * 0.32f));
        int x1 = Math.Max(x0 + 1, (int)(cx - OcrTemplates.Width * 0.10f));
        int y1 = Math.Min(OcrTemplates.Height, topRow + OcrTemplates.Height / 4);
        float sum = 0;
        int n = 0;
        for (int y = topRow; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                sum += cell[y * OcrTemplates.Width + x];
                n++;
            }
        }
        return n > 0 ? sum / n : 0;
    }

    private static double Sample(GrayImage gray, int x, int y)
    {
        if (x < 0)
            x = 0;
        else if (x >= gray.Width)
            x = gray.Width - 1;
        if (y < 0)
            y = 0;
        else if (y >= gray.Height)
            y = gray.Height - 1;
        return gray.Pixels[y * gray.Width + x];
    }

    /// <summary>
    /// Adaptive threshold via integral image. Dark runs wider than
    /// <paramref name="maxRunWidth" /> get erased; strokes are thin, borders
    /// and shading are wide and flood the profiles otherwise.
    /// </summary>
    internal static byte[] Binarize(GrayImage image, int maxRunWidth = int.MaxValue)
    {
        int width = image.Width;
        int height = image.Height;
        byte[] pixels = image.Pixels;
        var integral = new long[(width + 1) * (height + 1)];
        for (int y = 0; y < height; y++)
        {
            long rowSum = 0;
            int row = y * width;
            int integralRow = (y + 1) * (width + 1);
            int integralPrev = y * (width + 1);
            for (int x = 0; x < width; x++)
            {
                rowSum += pixels[row + x];
                integral[integralRow + x + 1] = integral[integralPrev + x + 1] + rowSum;
            }
        }

        int window = Math.Max(9, Math.Min(width, height) / 3) | 1;
        int half = window / 2;
        var ink = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            int y0 = Math.Max(0, y - half);
            int y1 = Math.Min(height - 1, y + half);
            for (int x = 0; x < width; x++)
            {
                int x0 = Math.Max(0, x - half);
                int x1 = Math.Min(width - 1, x + half);
                long area = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
                long sum = integral[(y1 + 1) * (width + 1) + x1 + 1]
                         - integral[y0 * (width + 1) + x1 + 1]
                         - integral[(y1 + 1) * (width + 1) + x0]
                         + integral[y0 * (width + 1) + x0];
                int mean = (int)(sum / area);
                if (pixels[y * width + x] < mean - 8)
                    ink[y * width + x] = 1;
            }
        }

        if (maxRunWidth < width)
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                int runStart = -1;
                for (int x = 0; x <= width; x++)
                {
                    bool inkHere = x < width && ink[row + x] != 0;
                    if (inkHere && runStart < 0)
                    {
                        runStart = x;
                    }
                    else if (!inkHere && runStart >= 0)
                    {
                        if (x - runStart > maxRunWidth)
                        {
                            for (int e = runStart; e < x; e++)
                                ink[row + e] = 0;
                        }
                        runStart = -1;
                    }
                }
            }

            // Vertical counterpart: glyphs are at most one line tall, while
            // border transition bands run the full strip height.
            int maxRunHeight = Math.Max(maxRunWidth, height / 2);
            for (int x = 0; x < width; x++)
            {
                int runStart = -1;
                for (int y = 0; y <= height; y++)
                {
                    bool inkHere = y < height && ink[y * width + x] != 0;
                    if (inkHere && runStart < 0)
                    {
                        runStart = y;
                    }
                    else if (!inkHere && runStart >= 0)
                    {
                        if (y - runStart > maxRunHeight)
                        {
                            for (int e = runStart; e < y; e++)
                                ink[e * width + x] = 0;
                        }
                        runStart = -1;
                    }
                }
            }
        }
        return ink;
    }

    /// <summary>
    /// Finds the vertical shear (line tilt as dy/dx) that maximizes the sharpness
    /// of the horizontal projection, correcting small rotations.
    /// </summary>
    private static double FindBestShear(byte[] ink, int width, int height)
    {
        double bestShear = 0;
        double bestVariance = double.MinValue;
        for (int step = -8; step <= 8; step++)
        {
            double shear = step * 0.01;
            var profile = new int[height];
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (ink[row + x] == 0)
                        continue;
                    int shifted = y + (int)Math.Round(shear * (x - width / 2.0));
                    if (shifted >= 0 && shifted < height)
                        profile[shifted]++;
                }
            }
            double mean = 0;
            for (int y = 0; y < height; y++)
                mean += profile[y];
            mean /= height;
            double variance = 0;
            for (int y = 0; y < height; y++)
            {
                double d = profile[y] - mean;
                variance += d * d;
            }
            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestShear = shear;
            }
        }
        return bestShear;
    }

    private static GrayImage ApplyVerticalShear(GrayImage source, double shear)
    {
        var result = new GrayImage(source.Width, source.Height);
        int width = source.Width;
        int height = source.Height;
        for (int x = 0; x < width; x++)
        {
            double offset = shear * (x - width / 2.0);
            for (int y = 0; y < height; y++)
            {
                double sy = y - offset;
                int iy = (int)Math.Floor(sy);
                double fy = sy - iy;
                double a = Sample(source, x, iy);
                double b = Sample(source, x, iy + 1);
                result.Pixels[y * width + x] = (byte)(a * (1 - fy) + b * fy);
            }
        }
        return result;
    }

    private static List<(int Top, int Bottom)> SegmentLines(byte[] ink, int width, int height)
    {
        var profile = new int[height];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int count = 0;
            for (int x = 0; x < width; x++)
                count += ink[row + x];
            profile[y] = count;
        }
        int max = 0;
        for (int y = 0; y < height; y++)
            max = Math.Max(max, profile[y]);
        Trace?.Invoke("profile: " + string.Join(" ", profile));
        if (max < 10)
            return new List<(int, int)>();

        int threshold = Math.Max(3, max / 6);
        var runs = new List<(int Top, int Bottom)>();
        int start = -1;
        for (int y = 0; y <= height; y++)
        {
            bool active = y < height && profile[y] >= threshold;
            if (active && start < 0)
            {
                start = y;
            }
            else if (!active && start >= 0)
            {
                runs.Add((start, y));
                start = -1;
            }
        }

        // Merge runs separated by tiny gaps (broken characters), drop specks.
        var merged = new List<(int Top, int Bottom)>();
        foreach ((int top, int bottom) in runs)
        {
            if (merged.Count > 0)
            {
                (int prevTop, int prevBottom) = merged[merged.Count - 1];
                int lineHeight = Math.Max(prevBottom - prevTop, bottom - top);
                if (top - prevBottom <= Math.Max(1, lineHeight / 4))
                {
                    merged[merged.Count - 1] = (prevTop, bottom);
                    continue;
                }
            }
            merged.Add((top, bottom));
        }
        merged.RemoveAll(run => run.Bottom - run.Top < 5);

        // Drop slivers: annotations, page edges, and print above or below the
        // MRZ produce shallow runs, while the true lines are the dominant runs
        // of roughly equal height.
        int tallest = 0;
        foreach ((int top, int bottom) in merged)
            tallest = Math.Max(tallest, bottom - top);
        merged.RemoveAll(run => (run.Bottom - run.Top) * 2 < tallest);

        // Blur and low contrast can merge adjacent MRZ lines into one span.
        // A span tall enough to hold two lines splits at a qualifying internal
        // profile valley; a clean single line has no such valley. Refinement
        // of split parts stays inside their split boundaries, or the parts
        // would grow back over the valley and overlap.
        var refined = new List<(int Top, int Bottom)>(merged.Count);
        foreach ((int top, int bottom) in merged)
        {
            List<(int Top, int Bottom)> parts = SplitMergedSpan(profile, top, bottom, 0);
            foreach ((int partTop, int partBottom) in parts)
            {
                int minTop = parts.Count > 1 ? partTop : 0;
                int maxBottom = parts.Count > 1 ? partBottom : height;
                refined.Add(RefineLineSpan(profile, partTop, partBottom, minTop, maxBottom));
            }
        }
        return refined;
    }

    private static List<(int Top, int Bottom)> SplitMergedSpan(int[] profile, int top, int bottom, int depth)
    {
        var result = new List<(int Top, int Bottom)>();
        int spanHeight = bottom - top;
        if (depth >= 2 || spanHeight < 28)
        {
            result.Add((top, bottom));
            return result;
        }

        int localMax = 0;
        for (int y = top; y < bottom; y++)
            localMax = Math.Max(localMax, profile[y]);

        // The deepest valley away from the edges; both halves must be line
        // sized for the split to be a plausible line boundary.
        int quarter = Math.Max(6, spanHeight / 4);
        int valley = -1;
        int valleyValue = int.MaxValue;
        for (int y = top + quarter; y < bottom - quarter; y++)
        {
            if (profile[y] < valleyValue)
            {
                valleyValue = profile[y];
                valley = y;
            }
        }
        // A line boundary is near empty, and both halves are full lines of
        // comparable density. A chevron heavy line also has a sparse internal
        // band (above the chevron apexes, below the cap tops), but its sparse
        // half peaks far lower than the dense half.
        if (valley < 0 || valleyValue > localMax * 25 / 100)
        {
            result.Add((top, bottom));
            return result;
        }
        int topPeak = 0;
        for (int y = top; y < valley; y++)
            topPeak = Math.Max(topPeak, profile[y]);
        int bottomPeak = 0;
        for (int y = valley + 1; y < bottom; y++)
            bottomPeak = Math.Max(bottomPeak, profile[y]);
        if (Math.Min(topPeak, bottomPeak) * 100 < localMax * 60)
        {
            result.Add((top, bottom));
            return result;
        }

        result.AddRange(SplitMergedSpan(profile, top, valley, depth + 1));
        result.AddRange(SplitMergedSpan(profile, valley + 1, bottom, depth + 1));
        return result;
    }

    private static (int Top, int Bottom) RefineLineSpan(int[] profile, int top, int bottom, int minTop, int maxBottom)
    {
        int localMax = 0;
        for (int y = top; y < bottom; y++)
            localMax = Math.Max(localMax, profile[y]);
        int enter = Math.Max(1, localMax / 12);
        int margin = Math.Max(2, (bottom - top) / 2);

        int newTop = top;
        while (newTop > minTop && newTop > top - margin && profile[newTop - 1] >= enter)
            newTop--;
        int newBottom = bottom;
        while (newBottom < maxBottom && newBottom < bottom + margin && profile[newBottom] >= enter)
            newBottom++;

        // Trim faint edges: neighboring print (signatures, patterns) can pull
        // the span past the glyph band, which shifts every glyph vertically
        // within its sampling window and breaks template alignment. Real cap
        // and base rows are dense; edge rows well below that are not the text.
        int trimThreshold = localMax / 3;
        int maxTrim = (newBottom - newTop) / 4;
        int trimmedTop = newTop;
        while (trimmedTop < newBottom - 1 && trimmedTop - newTop < maxTrim && profile[trimmedTop] < trimThreshold)
            trimmedTop++;
        int trimmedBottom = newBottom;
        while (trimmedBottom > trimmedTop + 1 && newBottom - trimmedBottom < maxTrim && profile[trimmedBottom - 1] < trimThreshold)
            trimmedBottom--;
        return (trimmedTop, trimmedBottom);
    }
}
