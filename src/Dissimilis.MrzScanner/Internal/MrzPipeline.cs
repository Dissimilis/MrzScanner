namespace Dissimilis.MrzScanner.Internal;

/// <summary>
/// Runs the full image pipeline: downscale, locate candidate bands, recognize
/// each in both orientations and every plausible format, arbitrate with
/// checksums, parse, and pick the best interpretation. Falls back to 90 degree
/// rotation when the upright pass finds nothing convincing.
/// </summary>
internal static class MrzPipeline
{
    private const double MinimumMeanScore = 0.30;
    private const int CropMaxWidth = 1200;

    /// <summary>
    /// Harness diagnostics, enabled by the MRZ_DIAGNOSTICS environment
    /// variable before first use. Off by default: the counters are plain
    /// fields and only meaningful for single threaded harness runs.
    /// </summary>
    internal static readonly bool Diagnostics =
        Environment.GetEnvironmentVariable("MRZ_DIAGNOSTICS") == "1";

    /// <summary>Cumulative stage timings in stopwatch ticks; harness diagnostics only.</summary>
    internal static long LocateTicks;
    internal static long RecognizeTicks;
    internal static long ArbitrateTicks;

    public static MrzResult Process(GrayImage image, MrzScannerOptions options, MrzParser parser, CancellationToken ct)
    {
        Ranked? best = ProcessAllPasses(image, options, parser, ct);

        // Tilted photos defeat the locator's row projections before any
        // candidate exists to deskew, so a whole image skew estimate gets one
        // rerun on a leveled copy. Only when nothing parsed at all: a frame
        // that produced candidates had its chance, and re-rolling arbitration
        // on marginal rows manufactures reads on MRZ-less card fronts.
        if (best is null)
        {
            // The locator itself shrugs off a degree or two of tilt, and most
            // handheld photos carry that much; a rescue below this threshold
            // would double the cost of every slightly crooked non-document
            // frame for a pass the level read effectively already had.
            const double rescueThreshold = 2.5;
            GrayImage working = image.DownscaleTo(options.MaxImageDimension);
            double correction = Deskew.EstimateCorrectionDegrees(working);
            if (Math.Abs(correction) >= rescueThreshold)
            {
                ct.ThrowIfCancellationRequested();

                // The capped working image is rotated, not the original:
                // rotating a full resolution photo would cost more than the
                // retry itself, and the crops the retry reads come out of the
                // rotated image anyway. Upright pass only: the estimator reads
                // horizontal row structure, so a sideways document cannot have
                // produced the correction that got us here.
                Ranked? leveled = ProcessOriented(
                    working.RotateBy(correction), options, parser, ct, rotatedPass: false);

                // The rerun re-rolls the arbitration dice on whatever text
                // rows the frame has, so on MRZ-less card fronts it can
                // manufacture a passing read the first pass rightly rejected.
                // A rescue pass only counts when it is convincing on its own:
                // checksum agreement that did not come from wholesale coercion.
                if (leveled is not null && IsConvincing(leveled))
                    best = Better(best, MapRescuedRegion(leveled, working, image, correction));
            }
        }

        if (best is null)
            return MrzResult.NotFound("No MRZ-shaped region was detected in the image.", NotFoundHints(image));
        return GateWeakFound(best, options.SearchEffort);
    }

    /// <summary>
    /// A rescued read located its band in the rotated, downscaled working
    /// image, but MrzRegion promises coordinates of the supplied image. The
    /// rect's corners are pushed through the same transform the rotation
    /// sampled with, then scaled back up and clamped.
    /// </summary>
    private static Ranked MapRescuedRegion(Ranked ranked, GrayImage working, GrayImage original, double correctionDegrees)
    {
        MrzRegion? region = ranked.Result.Region;
        if (region is null)
            return ranked;

        double radians = correctionDegrees * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double cx = (working.Width - 1) / 2.0;
        double cy = (working.Height - 1) / 2.0;
        double scaleX = original.Width / (double)working.Width;
        double scaleY = original.Height / (double)working.Height;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        Span<double> xs = stackalloc double[] { region.Left, region.Left + region.Width, region.Left, region.Left + region.Width };
        Span<double> ys = stackalloc double[] { region.Top, region.Top, region.Top + region.Height, region.Top + region.Height };
        for (int i = 0; i < 4; i++)
        {
            double dx = xs[i] - cx;
            double dy = ys[i] - cy;
            double sx = (cx + dx * cos - dy * sin) * scaleX;
            double sy = (cy + dx * sin + dy * cos) * scaleY;
            minX = Math.Min(minX, sx);
            maxX = Math.Max(maxX, sx);
            minY = Math.Min(minY, sy);
            maxY = Math.Max(maxY, sy);
        }

        int left = Math.Max(0, (int)minX);
        int top = Math.Max(0, (int)minY);
        var mapped = new MrzRegion(
            left,
            top,
            Math.Min(original.Width, (int)Math.Ceiling(maxX)) - left,
            Math.Min(original.Height, (int)Math.Ceiling(maxY)) - top,
            region.RotationDegrees,
            region.LineCount,
            region.Score);
        return new Ranked(
            ranked.Result.WithRegion(mapped), ranked.HypothesisScore, ranked.Coercions,
            mapped, ranked.Quality, ranked.Stats);
    }

    private static Ranked? ProcessAllPasses(GrayImage image, MrzScannerOptions options, MrzParser parser, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Ranked? upright = ProcessOriented(image, options, parser, ct, rotatedPass: false);

        // Only a convincing upright read skips the sideways pass. SingleFrame
        // skips it outright; the next video frame is cheaper than rotating.
        if (upright is not null && IsConvincing(upright))
            return upright;
        if (options.SearchEffort == MrzSearchEffort.SingleFrame)
            return upright;

        ct.ThrowIfCancellationRequested();
        Ranked? rotated = ProcessOriented(image.Rotate90(), options, parser, ct, rotatedPass: true);
        return Better(upright, rotated);
    }

    /// <summary>Hints for a frame with no candidate at all: nothing to measure but the frame itself.</summary>
    private static IReadOnlyList<MrzCaptureHint> NotFoundHints(GrayImage image)
    {
        var hints = new List<MrzCaptureHint> { MrzCaptureHint.NoMrzDetected };
        var histogram = new int[256];
        byte[] pixels = image.Pixels;
        int step = Math.Max(1, pixels.Length / 65536);
        int sampled = 0;
        for (int i = 0; i < pixels.Length; i += step)
        {
            histogram[pixels[i]]++;
            sampled++;
        }
        if (Percentile(histogram, sampled, 95) - Percentile(histogram, sampled, 5) < 50)
            hints.Add(MrzCaptureHint.LowContrast);
        return hints;
    }

    /// <summary>
    /// Turns the winning candidate's measured conditions into user guidance.
    /// A confidently valid read gets none; hints are for frames the caller
    /// should retake, and for weak reads they say what to change.
    /// </summary>
    private static IReadOnlyList<MrzCaptureHint> BuildHints(Ranked ranked)
    {
        MrzResult result = ranked.Result;
        if (result.IsValid && ranked.Coercions <= 2 && ranked.Quality >= 0.75)
            return Array.Empty<MrzCaptureHint>();

        var hints = new List<MrzCaptureHint>();
        CaptureStats stats = ranked.Stats;
        int lineLength = result.Raw is not null && result.Raw.Lines.Count > 0
            ? result.Raw.Lines[0].Length
            : 36;
        if (stats.RegionWidth / lineLength < 10)
            hints.Add(MrzCaptureHint.TooSmall);
        if (stats.TouchesEdge)
            hints.Add(MrzCaptureHint.CutOff);
        if (stats.GlareFraction > 0.10)
            hints.Add(MrzCaptureHint.Glare);
        if (stats.ContrastRange < 50)
            hints.Add(MrzCaptureHint.LowContrast);
        if (hints.Count == 0 && ranked.Quality < 0.75)
            hints.Add(MrzCaptureHint.Blurry);
        return hints;
    }

    /// <summary>
    /// Card fronts have digit rows that read as MRZ-shaped garbage. Found
    /// needs evidence: passing checks or quality garbage doesn't reach.
    /// </summary>
    private static MrzResult GateWeakFound(Ranked ranked, MrzSearchEffort effort)
    {
        MrzResult result = ranked.Result;
        if (!result.MrzFound)
            return result;

        // The arbitration search can manufacture passing checks on a digit
        // row. Exhaustive search weeds those out through competition; the
        // bounded SingleFrame search can't, so there validity only counts if
        // it came without wholesale coercion.
        bool validityTrusted = effort == MrzSearchEffort.Exhaustive || ranked.Coercions <= 2;
        IReadOnlyList<MrzCaptureHint> hints = BuildHints(ranked);
        if (ranked.Quality >= 0.75 || (result.IsValid && validityTrusted))
            return hints.Count == 0 ? result : result.WithCaptureHints(hints);
        return MrzResult.NotFound(
            "An MRZ-like region was found but could not be read with enough evidence.", hints);
    }

    /// <summary>A parsed interpretation with the recognition quality that produced it.</summary>
    private sealed class Ranked
    {
        public Ranked(MrzResult result, double hypothesisScore, int coercions, MrzRegion? region, double quality, CaptureStats stats)
        {
            Result = result;
            HypothesisScore = hypothesisScore;
            Coercions = coercions;
            Region = region;
            Quality = quality;
            Stats = stats;
        }

        public MrzResult Result { get; }
        public double HypothesisScore { get; }
        public int Coercions { get; }
        public MrzRegion? Region { get; }

        /// <summary>Calibrated recognition quality with no validity credit; the found gate's evidence.</summary>
        public double Quality { get; }

        /// <summary>Capture conditions of the candidate this read came from, for hint building.</summary>
        public CaptureStats Stats { get; }
    }

    /// <summary>Measured capture conditions of a candidate band, the raw material for capture hints.</summary>
    private sealed class CaptureStats
    {
        /// <summary>Band width in original image pixels.</summary>
        public double RegionWidth;

        /// <summary>Whether the candidate touches the frame border in the pass it was found in.</summary>
        public bool TouchesEdge;

        /// <summary>Fraction of near saturated pixels inside the band crop.</summary>
        public double GlareFraction;

        /// <summary>Tonal range between the 5th and 95th percentile of the crop.</summary>
        public int ContrastRange;
    }

    /// <summary>Enough honestly valid check digits to stop searching.</summary>
    private static bool IsConvincing(Ranked ranked)
    {
        return (ranked.Result.IsValid || CountValidChecks(ranked.Result.Checks) >= 3) &&
               ranked.Coercions < 3;
    }

    /// <summary>
    /// Quality is the primary arbiter; coercion can fake check digits but
    /// can't make junk correlate like OCR-B. Comparable quality falls
    /// through to the check/grammar ranking.
    /// </summary>
    private const double QualityMargin = 0.10;

    private static Ranked? Better(Ranked? a, Ranked? b)
    {
        if (a is null)
            return b;
        if (b is null)
            return a;
        if (a.HypothesisScore > b.HypothesisScore + QualityMargin)
            return a;
        if (b.HypothesisScore > a.HypothesisScore + QualityMargin)
            return b;
        (int Checks, double Confidence) keyA = RankKey(a.Result, a.Coercions, a.Quality);
        (int Checks, double Confidence) keyB = RankKey(b.Result, b.Coercions, b.Quality);
        if (keyA.Checks != keyB.Checks)
            return keyA.Checks > keyB.Checks ? a : b;
        if (Math.Abs(keyA.Confidence - keyB.Confidence) > 0.02)
            return keyA.Confidence > keyB.Confidence ? a : b;

        // Near tie: the recognizer's quality score decides.
        return a.HypothesisScore >= b.HypothesisScore ? a : b;
    }

    /// <summary>
    /// Valid checks minus violations minus coercion, then confidence. The
    /// violation penalty is what lets an honest read beat a coerced wrong
    /// format full of letters in date fields. (Penalizing failed checks
    /// instead was tried; honest partial reads carry those too.)
    /// </summary>
    private static (int, double) RankKey(MrzResult result, int coercions, double quality)
    {
        int violations = 0;
        for (int i = 0; i < result.Issues.Count; i++)
        {
            if (result.Issues[i].Kind is MrzIssueKind.BadFormat or MrzIssueKind.InvalidValue)
                violations++;
        }

        // A couple of coerced cells is the normal price of a noisy photo;
        // wholesale coercion is manufactured validity. Tiebreak on the
        // validity-blind quality, never the floored confidence.
        return (CountValidChecks(result.Checks) - violations - coercions / 3, quality);
    }

    /// <summary>
    /// Pass-space rect back to original coordinates, plus the clockwise
    /// rotation that brings the text upright. The rotated pass sees the
    /// original turned 90 CW, so its coordinates rotate back the other way.
    /// </summary>
    internal static MrzRegion MakeRegion(
        int left, int top, int width, int height, int passWidth,
        bool rotatedPass, bool flipped, int lineCount, double score)
    {
        int rotation = rotatedPass ? (flipped ? 270 : 90) : (flipped ? 180 : 0);
        if (!rotatedPass)
            return new MrzRegion(left, top, width, height, rotation, lineCount, score);
        return new MrzRegion(top, passWidth - left - width, height, width, rotation, lineCount, score);
    }

    private static Ranked? ProcessOriented(
        GrayImage image, MrzScannerOptions options, MrzParser parser, CancellationToken ct, bool rotatedPass)
    {
        GrayImage working = image.DownscaleTo(options.MaxImageDimension);
        double scaleX = image.Width / (double)working.Width;
        double scaleY = image.Height / (double)working.Height;

        long t0 = Diagnostics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        List<BandCandidate> candidates = MrzLocator.Locate(working);
        if (Diagnostics)
            LocateTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;

        Ranked? best = null;

        int candidateBudget = options.SearchEffort == MrzSearchEffort.SingleFrame
            ? Math.Min(2, candidates.Count)
            : candidates.Count;
        for (int c = 0; c < candidateBudget; c++)
        {
            BandCandidate candidate = candidates[c];
            ct.ThrowIfCancellationRequested();

            // Candidates come best first; stop once one read convincingly.
            if (best is not null && IsConvincing(best))
                break;

            GrayImage crop = PrepareCrop(image, candidate, scaleX, scaleY);
            int regionLeft = (int)(candidate.Left * scaleX);
            int regionTop = (int)(candidate.Top * scaleY);
            int regionWidth = (int)Math.Ceiling(candidate.Right * scaleX) - regionLeft;
            int regionHeight = (int)Math.Ceiling(candidate.Bottom * scaleY) - regionTop;
            CaptureStats stats = ComputeStats(crop, candidate, working, regionWidth);

            best = RecognizeCrop(
                crop, image.Width, options, parser, ct, rotatedPass,
                regionLeft, regionTop, regionWidth, regionHeight, stats, best);

            // A tilted band smears the row projections the grid search relies
            // on. When the level read wasn't convincing and the crop measures
            // a real skew, one rotated retry competes with the level read.
            // Same evidence bar as the whole image rescue: a deskew rerun
            // re-rolls arbitration and must not surface coerced junk the
            // level pass rightly rejected. Top candidate only; paying the
            // retry for every hopeless candidate on a non-document image
            // costs half again the whole read.
            if (c == 0 && (best is null || !IsConvincing(best)))
            {
                // Two degrees and under the level grid read handles on its
                // own; the retry is for tilts that actually bend the grid.
                double correction = Deskew.EstimateCorrectionDegrees(crop);
                if (Math.Abs(correction) >= 2.0)
                {
                    Ranked? rescued = RecognizeCrop(
                        crop.RotateBy(correction), image.Width, options, parser, ct, rotatedPass,
                        regionLeft, regionTop, regionWidth, regionHeight, stats, best: null);
                    if (rescued is not null && IsConvincing(rescued))
                        best = Better(best, rescued);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Recognizes one prepared crop in the allowed orientations and folds every
    /// parsed interpretation into the running best.
    /// </summary>
    private static Ranked? RecognizeCrop(
        GrayImage crop, int imageWidth, MrzScannerOptions options, MrzParser parser, CancellationToken ct,
        bool rotatedPass, int regionLeft, int regionTop, int regionWidth, int regionHeight,
        CaptureStats stats, Ranked? best)
    {
        int orientationBudget = options.SearchEffort == MrzSearchEffort.SingleFrame ? 1 : 2;
        for (int orientation = 0; orientation < orientationBudget; orientation++)
        {
            // 180 pass only when upright wasn't convincing; it exists
            // for upside-down documents.
            if (orientation == 1 && best is not null && IsConvincing(best))
                break;
            GrayImage oriented = orientation == 0 ? crop : crop.Rotate180();
            long t1 = Diagnostics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            List<BandRead> bands = BandRecognizer.Recognize(
                oriented, ct, thorough: options.SearchEffort == MrzSearchEffort.Exhaustive);
            if (Diagnostics)
                RecognizeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t1;
            foreach (BandRead band in bands)
            {
                if (band.MeanScore < MinimumMeanScore)
                    continue;

                List<string> text = band.AllText();
                MrzFormat? format = MrzFormat.Detect(text);
                if (format is null)
                    continue;

                double hypothesisScore = band.HypothesisScore;

                // Snapshot shift variants before arbitration mutates the
                // cells; a phantom head cell shifts the whole line and no
                // per-cell search undoes that.
                List<BandRead> shiftVariants = options.SearchEffort == MrzSearchEffort.Exhaustive
                    ? BuildShiftVariants(band, format)
                    : new List<BandRead>();

                long t2 = Diagnostics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                ChecksumArbitrator.Arbitrate(band, format, ct);
                AdaptiveRefiner.Refine(band, format);
                ChecksumArbitrator.Arbitrate(band, format, ct);
                if (Diagnostics)
                    ArbitrateTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t2;

                MrzRegion region = MakeRegion(
                    regionLeft, regionTop, regionWidth, regionHeight, imageWidth,
                    rotatedPass, flipped: orientation == 1, band.Lines.Count, hypothesisScore);
                MrzResult result = ParseBand(band, format, parser, region, out double quality);
                best = Better(best, new Ranked(result, hypothesisScore, band.Coercions, region, quality, stats));

                // Variants only compete when the direct read looks bad.
                if (CountValidChecks(result.Checks) < 3 || band.Coercions >= 3)
                {
                    foreach (BandRead variant in shiftVariants)
                    {
                        long t3 = Diagnostics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
                        ChecksumArbitrator.Arbitrate(variant, format, ct);
                        AdaptiveRefiner.Refine(variant, format);
                        ChecksumArbitrator.Arbitrate(variant, format, ct);
                        if (Diagnostics)
                            ArbitrateTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t3;
                        MrzResult shifted = ParseBand(variant, format, parser, region, out double shiftedQuality);
                        best = Better(best, new Ranked(shifted, hypothesisScore, variant.Coercions, region, shiftedQuality, stats));
                    }
                }
            }
        }
        return best;
    }

    /// <summary>Measures the capture conditions of a candidate band from its crop.</summary>
    private static CaptureStats ComputeStats(GrayImage crop, BandCandidate candidate, GrayImage working, int regionWidth)
    {
        var histogram = new int[256];
        byte[] pixels = crop.Pixels;
        int saturated = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            // Only hard clipping counts as glare; well lit white paper sits
            // in the 230s and 240s and must not trip the hint.
            histogram[pixels[i]]++;
            if (pixels[i] >= 254)
                saturated++;
        }
        int p5 = Percentile(histogram, pixels.Length, 5);
        int p95 = Percentile(histogram, pixels.Length, 95);
        return new CaptureStats
        {
            RegionWidth = regionWidth,
            TouchesEdge = candidate.Left <= 1 || candidate.Top <= 1 ||
                          candidate.Right >= working.Width - 2 || candidate.Bottom >= working.Height - 2,
            GlareFraction = pixels.Length > 0 ? saturated / (double)pixels.Length : 0,
            ContrastRange = p95 - p5,
        };
    }

    private static int Percentile(int[] histogram, int total, int percent)
    {
        long target = (long)total * percent / 100;
        long seen = 0;
        for (int v = 0; v < histogram.Length; v++)
        {
            seen += histogram[v];
            if (seen >= target)
                return v;
        }
        return histogram.Length - 1;
    }

    /// <summary>
    /// Band variants with one leading cell dropped per line (all lines, or
    /// each alone) and a synthesized tail cell. Left-edge junk can occupy the
    /// first cell and shift the whole line right; when the vacated tail is a
    /// check digit position it gets recomputed from the shifted content.
    /// </summary>
    private static List<BandRead> BuildShiftVariants(BandRead band, MrzFormat format)
    {
        int lineCount = band.Lines.Count;
        var masks = new List<bool[]>();
        var all = new bool[lineCount];
        for (int i = 0; i < all.Length; i++)
            all[i] = true;
        masks.Add(all);
        if (lineCount > 1)
        {
            for (int i = 0; i < lineCount; i++)
            {
                var single = new bool[lineCount];
                single[i] = true;
                masks.Add(single);
            }
        }

        var variants = new List<BandRead>(masks.Count);
        foreach (bool[] mask in masks)
        {
            var lines = new List<List<CellRead>>(lineCount);
            for (int ln = 0; ln < lineCount; ln++)
            {
                List<CellRead> source = band.Lines[ln];
                var cells = new List<CellRead>(source.Count);
                if (mask[ln])
                {
                    for (int x = 1; x < source.Count; x++)
                        cells.Add(CloneCell(source[x]));
                    cells.Add(SynthesizeCell(format.ClassAt(ln, source.Count - 1)));
                }
                else
                {
                    foreach (CellRead cell in source)
                        cells.Add(CloneCell(cell));
                }
                lines.Add(cells);
            }
            var variant = new BandRead(lines, band.MeanScore, band.GeometryPenalty);
            RecomputeTrailingChecks(variant, format, mask);
            variants.Add(variant);
        }
        return variants;
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

    /// <summary>
    /// A placeholder cell for the position vacated by a shift: candidates cover
    /// the position's class with equal low scores, so checksum arbitration is
    /// free to pick whichever satisfies the checks.
    /// </summary>
    private static CellRead SynthesizeCell(CharClass allowed)
    {
        string alphabet = allowed switch
        {
            CharClass.Digit => "0123456789",
            CharClass.SexChar => "<MF",
            _ => "<0123456789",
        };
        var chars = alphabet.ToCharArray();
        var scores = new float[chars.Length];
        for (int i = 0; i < scores.Length; i++)
            scores[i] = 0.25f;
        return new CellRead(chars, scores, new float[OcrTemplates.Width * OcrTemplates.Height]);
    }

    /// <summary>
    /// When a shifted line's final position carries a check digit, computes it
    /// from the shifted content so the synthesized tail starts out consistent.
    /// </summary>
    private static void RecomputeTrailingChecks(BandRead band, MrzFormat format, bool[] shifted)
    {
        foreach (CheckRelation relation in format.Checks)
        {
            FieldDef? checkField = format.Field(relation.CheckField);
            if (checkField is null || !shifted[checkField.Line])
                continue;
            if (checkField.Start != band.Lines[checkField.Line].Count - 1)
                continue;

            int sum = 0;
            int index = 0;
            bool computable = true;
            foreach ((int line, int start, int length) in relation.Protects)
            {
                for (int i = 0; i < length && computable; i++)
                {
                    int value = CheckDigit.CharValue(band.Lines[line][start + i].Chosen);
                    if (value < 0)
                        computable = false;
                    else
                        sum += value * ((index % 3) switch { 0 => 7, 1 => 3, _ => 1 });
                    index++;
                }
            }
            if (!computable)
                continue;
            CellRead cell = band.Lines[checkField.Line][checkField.Start];
            char digit = (char)('0' + sum % 10);
            cell.Chosen = digit;
            cell.ChosenScore = 0.3f;
        }
    }

    private static MrzResult ParseBand(BandRead band, MrzFormat format, MrzParser parser, MrzRegion? region, out double quality)
    {
        double meanScore = 0;
        int cells = 0;
        foreach (List<CellRead> line in band.Lines)
        {
            foreach (CellRead cell in line)
            {
                meanScore += Math.Max(0, Math.Min(1, cell.ChosenScore));
                cells++;
            }
        }
        meanScore = cells > 0 ? meanScore / cells : 0;

        var issues = new List<MrzIssue>();
        MrzResult parsed = parser.ParseNormalized(format, band.AllText(), issues, meanScore);

        // Blend checksum health into a raw quality score, then calibrate it
        // against measured character accuracy so the reported confidence is an
        // estimate of the fraction of characters read correctly.
        int present = 0;
        int valid = 0;
        CountChecks(parsed.Checks, ref present, ref valid);
        double raw = present > 0
            ? 0.75 * meanScore + 0.25 * (valid / (double)present)
            : meanScore;
        bool isValid = parsed.Document is not null && parsed.Checks.AllValid;
        (double confidence, quality) = CalibrateConfidence(raw, isValid, band.Coercions);

        if (confidence < 0.5)
        {
            issues.Add(new MrzIssue(string.Empty, MrzIssueKind.LowConfidence,
                "Characters were recognized with low confidence."));
        }

        return new MrzResult(
            mrzFound: true,
            document: parsed.Document,
            raw: parsed.Raw,
            checks: parsed.Checks,
            issues: issues,
            confidence: confidence,
            region: region,
            fieldConfidence: ComputeFieldConfidence(band, format, parsed.Checks));
    }

    /// <summary>
    /// Maps the raw quality blend to an estimate of character accuracy. The
    /// curve comes from the labeled verification set: raw scores above 0.94
    /// read essentially perfectly, the band between 0.80 and 0.94 averages
    /// about 72 to 95 percent correct, and full check digit validity that was
    /// read rather than coerced is checksum grade evidence on its own.
    /// </summary>
    /// <summary>
    /// Returns the reported confidence and the raw quality used for gating.
    /// The quality never benefits from check digit validity, because the
    /// arbitration search can manufacture passing check digits on a garbage
    /// row; the found gate must judge the pixels alone. The reported
    /// confidence does credit validity, tiered by how much coercion it took:
    /// on a surfaced read, checksum agreement is strong evidence.
    /// </summary>
    private static (double Confidence, double Quality) CalibrateConfidence(double raw, bool isValid, int coercions)
    {
        double quality;
        if (raw >= 0.94)
            quality = 0.97 + (raw - 0.94) * 0.5;
        else if (raw >= 0.80)
            quality = 0.70 + (raw - 0.80) * (0.27 / 0.14);
        else
            quality = raw * 0.85;
        quality = Math.Max(0, Math.Min(0.999, quality));

        double confidence = quality;
        if (isValid)
            confidence = Math.Max(confidence, coercions <= 2 ? 0.95 : 0.85);
        return (Math.Min(0.999, confidence), quality);
    }

    /// <summary>
    /// Per field confidence from the field's own cell scores, boosted when a
    /// valid check digit corroborates the field and cut when one contradicts it.
    /// </summary>
    private static MrzFieldConfidence ComputeFieldConfidence(BandRead band, MrzFormat format, MrzChecks checks)
    {
        return new MrzFieldConfidence(
            documentNumber: FieldScore(band, format, FieldId.DocumentNumber, checks.DocumentNumber),
            name: FieldScore(band, format, FieldId.Name, CheckDigitStatus.NotPresent),
            birthDate: FieldScore(band, format, FieldId.BirthDate, checks.BirthDate),
            expiryDate: FieldScore(band, format, FieldId.ExpiryDate, checks.ExpiryDate),
            sex: FieldScore(band, format, FieldId.Sex, CheckDigitStatus.NotPresent),
            nationality: FieldScore(band, format, FieldId.Nationality, CheckDigitStatus.NotPresent),
            issuingCountry: FieldScore(band, format, FieldId.IssuingCountry, CheckDigitStatus.NotPresent),
            optionalData: Math.Max(
                FieldScore(band, format, FieldId.OptionalData1, checks.OptionalData),
                FieldScore(band, format, FieldId.OptionalData2, checks.OptionalData)));
    }

    private static double FieldScore(BandRead band, MrzFormat format, FieldId fieldId, CheckDigitStatus check)
    {
        FieldDef? field = format.Field(fieldId);
        if (field is null)
            return 0;
        double sum = 0;
        int count = 0;
        bool anyContent = false;
        for (int i = 0; i < field.Length; i++)
        {
            CellRead cell = band.Lines[field.Line][field.Start + i];
            sum += Math.Max(0, Math.Min(1, cell.ChosenScore));
            count++;
            if (cell.Chosen != '<')
                anyContent = true;
        }
        if (count == 0 || !anyContent)
            return 0;
        double score = sum / count;

        // A verified check digit corroborates every cell it protects; a failed
        // one means at least one of them is wrong.
        if (check == CheckDigitStatus.Valid)
            score = 1 - (1 - score) * 0.3;
        else if (check == CheckDigitStatus.Invalid)
            score *= 0.5;
        return Math.Max(0, Math.Min(0.999, score));
    }

    private static int CountValidChecks(MrzChecks checks)
    {
        int present = 0;
        int valid = 0;
        CountChecks(checks, ref present, ref valid);
        return valid;
    }

    private static void CountChecks(MrzChecks checks, ref int present, ref int valid)
    {
        Tally(checks.DocumentNumber, ref present, ref valid);
        Tally(checks.BirthDate, ref present, ref valid);
        Tally(checks.ExpiryDate, ref present, ref valid);
        Tally(checks.OptionalData, ref present, ref valid);
        Tally(checks.Composite, ref present, ref valid);
    }

    private static void Tally(CheckDigitStatus status, ref int present, ref int valid)
    {
        if (status == CheckDigitStatus.NotPresent)
            return;
        present++;
        if (status == CheckDigitStatus.Valid)
            valid++;
    }

    /// <summary>
    /// Prepares the recognition strip for a candidate band: the full width
    /// horizontal strip at the band's rows from the original resolution image.
    /// Horizontal cropping is deliberately avoided: the locator's gradient
    /// extent can clip faint edge characters, and fixed margins either truncate
    /// or invite clamping asymmetries. The recognizer's pitch based grid search
    /// locates the character grid within the strip and resists flanking
    /// decorations. Tiny bands are upscaled to a workable character pitch.
    /// </summary>
    internal static GrayImage PrepareCrop(GrayImage image, BandCandidate candidate, double scaleX, double scaleY)
    {
        // Expand horizontally by 15 percent of the band width per side: enough
        // to recover edge characters the gradient extent clipped, small enough
        // to keep nearby decorations (corner patterns, borders) out of the crop.
        int expandX = (int)(candidate.Width * 0.15);
        GrayImage crop = Crop(
            image,
            (int)((candidate.Left - expandX) * scaleX),
            (int)(candidate.Top * scaleY),
            (int)Math.Ceiling((candidate.Width + 2.0 * expandX) * scaleX),
            (int)Math.Ceiling(candidate.Height * scaleY));
        double cropScale = 1.0;
        if (crop.Width > CropMaxWidth)
        {
            cropScale = CropMaxWidth / (double)crop.Width;
            crop = crop.DownscaleTo(CropMaxWidth);
        }

        // An MRZ line has 30 to 44 cells; aim for roughly 14+ pixels of pitch.
        double bandWidthInCrop = candidate.Width * scaleX * cropScale;
        if (bandWidthInCrop > 0 && bandWidthInCrop < 620)
        {
            int factor = Math.Min(3, (int)Math.Ceiling(620 / bandWidthInCrop));
            if (factor > 1)
                crop = crop.Resize(crop.Width * factor, crop.Height * factor);
        }
        return crop;
    }

    private static GrayImage Crop(GrayImage source, int left, int top, int width, int height)
    {
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        width = Math.Min(width, source.Width - left);
        height = Math.Min(height, source.Height - top);
        var result = new GrayImage(width, height);
        for (int y = 0; y < height; y++)
            Array.Copy(source.Pixels, (top + y) * source.Width + left, result.Pixels, y * width, width);
        return result;
    }
}
