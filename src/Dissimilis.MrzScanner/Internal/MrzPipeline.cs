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
        ct.ThrowIfCancellationRequested();
        Ranked? upright = ProcessOriented(image, options, parser, ct, rotatedPass: false);

        // Only a convincing upright read skips the sideways pass. SingleFrame
        // skips it outright; the next video frame is cheaper than rotating.
        if (upright is not null && IsConvincing(upright))
            return GateWeakFound(upright, options.SearchEffort);
        if (options.SearchEffort == MrzSearchEffort.SingleFrame)
        {
            return upright is null
                ? MrzResult.NotFound("No MRZ-shaped region was detected in the image.")
                : GateWeakFound(upright, options.SearchEffort);
        }

        ct.ThrowIfCancellationRequested();
        Ranked? rotated = ProcessOriented(image.Rotate90(), options, parser, ct, rotatedPass: true);
        Ranked? best = Better(upright, rotated);
        if (best is null)
            return MrzResult.NotFound("No MRZ-shaped region was detected in the image.");
        return GateWeakFound(best, options.SearchEffort);
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
        if (ranked.Quality >= 0.75 || (result.IsValid && validityTrusted))
            return result;
        return MrzResult.NotFound("An MRZ-like region was found but could not be read with enough evidence.");
    }

    /// <summary>A parsed interpretation with the recognition quality that produced it.</summary>
    private sealed class Ranked
    {
        public Ranked(MrzResult result, double hypothesisScore, int coercions, MrzRegion? region, double quality)
        {
            Result = result;
            HypothesisScore = hypothesisScore;
            Coercions = coercions;
            Region = region;
            Quality = quality;
        }

        public MrzResult Result { get; }
        public double HypothesisScore { get; }
        public int Coercions { get; }
        public MrzRegion? Region { get; }

        /// <summary>Calibrated recognition quality with no validity credit; the found gate's evidence.</summary>
        public double Quality { get; }
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
                        regionLeft, regionTop, regionWidth, regionHeight, image.Width,
                        rotatedPass, flipped: orientation == 1, band.Lines.Count, hypothesisScore);
                    MrzResult result = ParseBand(band, format, parser, region, out double quality);
                    best = Better(best, new Ranked(result, hypothesisScore, band.Coercions, region, quality));

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
                            best = Better(best, new Ranked(shifted, hypothesisScore, variant.Coercions, region, shiftedQuality));
                        }
                    }
                }
            }
        }

        return best;
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
