using Dissimilis.MrzScanner.Internal;

namespace Dissimilis.MrzScanner;

/// <summary>
/// Aggregates MRZ reads across consecutive video frames until a stable, valid
/// result emerges. Individual frames suffer motion blur, glare, and focus
/// hunting; characters that are unreadable in one frame are usually readable
/// a few frames later. The session votes per character position across
/// frames, so the fused read can be valid even when no single frame was.
/// </summary>
/// <remarks>
/// Not thread safe: feed frames from one thread (or synchronize externally).
/// Typical use: feed camera frames until <see cref="IsStable" /> is true,
/// then take <see cref="Best" />.
/// </remarks>
public sealed class MrzVideoSession
{
    private readonly MrzScanner _reader;
    private readonly int _stableFrames;
    private readonly Dictionary<(int Lines, int Length), ShapeVotes> _votes = new();
    private readonly Dictionary<string, int> _validDocumentSightings = new();
    private MrzResult? _best;
    private int _bestFusedFrames;

    private sealed class ShapeVotes
    {
        public ShapeVotes(int lines, int length)
        {
            Weights = new double[lines][];
            for (int i = 0; i < lines; i++)
                Weights[i] = new double[length * AlphabetSize];
        }

        public const int AlphabetSize = 37;
        public double[][] Weights;
        public int Frames;

        /// <summary>Recent raw reads of this shape, for agreement counting.</summary>
        public readonly List<string[]> RecentReads = new List<string[]>();
    }

    /// <summary>Session with capture defaults: SingleFrame effort, stable after two agreeing frames.</summary>
    public MrzVideoSession()
        : this(new MrzScannerOptions { SearchEffort = MrzSearchEffort.SingleFrame })
    {
    }

    /// <summary>Creates a session with explicit reader options.</summary>
    /// <param name="options">
    /// Options for the per frame reads. <see cref="MrzSearchEffort.SingleFrame" />
    /// is recommended: it bounds the cost of hopeless frames. Must not be null.
    /// </param>
    /// <param name="stableFrames">
    /// How many valid frames must agree on the document number before
    /// <see cref="IsStable" /> turns true. Minimum 1, default 2.
    /// </param>
    public MrzVideoSession(MrzScannerOptions options, int stableFrames = 2)
    {
        if (options is null)
            throw new ArgumentNullException(nameof(options));
        if (stableFrames < 1)
            throw new ArgumentOutOfRangeException(nameof(stableFrames), stableFrames,
                "stableFrames must be at least 1.");
        _reader = new MrzScanner(options);
        _stableFrames = stableFrames;
    }

    /// <summary>Number of frames fed so far.</summary>
    public int FramesSeen { get; private set; }

    /// <summary>The best result the session has produced so far, or null before any find.</summary>
    public MrzResult? Best => _best;

    /// <summary>
    /// True once <see cref="Best" /> is fully valid and the required number of
    /// valid frames agreed on the same document number.
    /// </summary>
    public bool IsStable { get; private set; }

    /// <summary>
    /// Capture hints from the most recently fed frame, for live guidance
    /// overlays. <see cref="Best" /> may carry an older frame's result, so
    /// UIs should read guidance from here, not from the returned result.
    /// </summary>
    public IReadOnlyList<MrzCaptureHint> LastFrameHints { get; private set; } = Array.Empty<MrzCaptureHint>();

    /// <summary>
    /// Reads one frame and folds it into the session. Returns the session's
    /// best result so far, which may come from an earlier frame or from
    /// fusing several frames.
    /// </summary>
    /// <param name="frame">Pixel data created through a <see cref="MrzImage" /> factory method.</param>
    public MrzResult Feed(MrzImage frame)
    {
        MrzResult result = _reader.Read(frame);
        return Fold(result);
    }

    /// <summary>Reads one encoded frame (JPEG, PNG, BMP) and folds it into the session.</summary>
    /// <param name="frame">The encoded image bytes. Must not be null.</param>
    public MrzResult Feed(byte[] frame)
    {
        if (frame is null)
            throw new ArgumentNullException(nameof(frame));
        MrzResult result = _reader.Read(frame);
        return Fold(result);
    }

    /// <summary>Forgets everything, ready for the next document.</summary>
    public void Reset()
    {
        _votes.Clear();
        _validDocumentSightings.Clear();
        _best = null;
        FramesSeen = 0;
        IsStable = false;
        LastFrameHints = Array.Empty<MrzCaptureHint>();
    }

    private MrzResult Fold(MrzResult frameResult)
    {
        FramesSeen++;
        LastFrameHints = frameResult.CaptureHints;
        if (!frameResult.MrzFound || frameResult.Raw is null || frameResult.Raw.Lines.Count == 0)
            return _best ?? frameResult;

        AccumulateVotes(frameResult);

        if (frameResult.IsValid && frameResult.Document is not null)
        {
            string identity = DocumentIdentity(frameResult.Document);
            _validDocumentSightings.TryGetValue(identity, out int seen);
            _validDocumentSightings[identity] = seen + 1;
        }

        if (Preferred(frameResult, _best))
        {
            _best = frameResult;
            _bestFusedFrames = 0;
        }

        (MrzResult Result, int Frames)? fused = TryFuse();
        if (fused is not null && Preferred(fused.Value.Result, _best))
        {
            _best = fused.Value.Result;
            _bestFusedFrames = fused.Value.Frames;
        }

        IsStable = _best is not null && _best.IsValid && Corroboration() >= _stableFrames;
        return _best ?? frameResult;
    }

    /// <summary>Frames backing the current best: agreeing valid reads, or contributors to a fused one.</summary>
    private int Corroboration()
    {
        if (_best?.Document is null)
            return 0;
        _validDocumentSightings.TryGetValue(DocumentIdentity(_best.Document), out int seen);
        return Math.Max(seen, _bestFusedFrames);
    }

    /// <summary>
    /// Sightings key by document type, issuer, and number together: two
    /// different documents sharing a number must not corroborate each other.
    /// </summary>
    private static string DocumentIdentity(MrzDocument document) =>
        $"{document.Type}|{document.IssuingCountry}|{document.DocumentNumber}";

    private void AccumulateVotes(MrzResult frameResult)
    {
        IReadOnlyList<string> lines = frameResult.Raw!.Lines;
        var shape = (lines.Count, lines[0].Length);
        foreach (string line in lines)
        {
            if (line.Length != shape.Item2)
                return;
        }
        if (!_votes.TryGetValue(shape, out ShapeVotes? votes))
        {
            votes = new ShapeVotes(shape.Item1, shape.Item2);
            _votes[shape] = votes;
        }
        votes.Frames++;

        var snapshot = new string[lines.Count];
        for (int i = 0; i < lines.Count; i++)
            snapshot[i] = lines[i];
        votes.RecentReads.Add(snapshot);

        // The window bounds fused corroboration, so it must be able to hold
        // at least stableFrames agreeing reads or a fusion-only session
        // could never turn stable.
        int window = Math.Max(16, _stableFrames);
        if (votes.RecentReads.Count > window)
            votes.RecentReads.RemoveAt(0);

        // A frame's vote weight is its confidence, with a bonus for full
        // validity: a checksum backed read should outvote two blurry ones.
        double weight = Math.Max(0.05, frameResult.Confidence);
        if (frameResult.IsValid)
            weight += 0.75;

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            double[] weights = votes.Weights[i];
            for (int x = 0; x < line.Length; x++)
            {
                int glyph = OcrTemplates.IndexOf(line[x]);
                if (glyph >= 0)
                    weights[x * ShapeVotes.AlphabetSize + glyph] += weight;
            }
        }
    }

    private (MrzResult Result, int Frames)? TryFuse()
    {
        // Fuse the shape most frames agreed on; a lone dissenting shape is
        // usually a misdetection.
        ShapeVotes? dominant = null;
        (int Lines, int Length) dominantShape = default;
        foreach (KeyValuePair<(int, int), ShapeVotes> pair in _votes)
        {
            if (dominant is null || pair.Value.Frames > dominant.Frames)
            {
                dominant = pair.Value;
                dominantShape = pair.Key;
            }
        }
        if (dominant is null || dominant.Frames < 2)
            return null;

        var lines = new string[dominantShape.Lines];
        double agreement = 0;
        int cells = 0;
        for (int i = 0; i < dominantShape.Lines; i++)
        {
            var chars = new char[dominantShape.Length];
            double[] weights = dominant.Weights[i];
            for (int x = 0; x < dominantShape.Length; x++)
            {
                int bestGlyph = 0;
                double bestWeight = double.MinValue;
                double total = 0;
                for (int g = 0; g < ShapeVotes.AlphabetSize; g++)
                {
                    double w = weights[x * ShapeVotes.AlphabetSize + g];
                    total += w;
                    if (w > bestWeight)
                    {
                        bestWeight = w;
                        bestGlyph = g;
                    }
                }
                chars[x] = OcrTemplates.Alphabet[bestGlyph];
                agreement += total > 0 ? bestWeight / total : 0;
                cells++;
            }
            lines[i] = new string(chars);
        }

        MrzResult parsed = MrzParser.ParseText(string.Join("\n", lines));
        if (!parsed.MrzFound)
            return null;

        // Corroboration counts only the frames that actually agree with the
        // fused text; frames of the same shape reading a different document
        // must not certify a chimera as stable.
        int agreeingFrames = 0;
        foreach (string[] read in dominant.RecentReads)
        {
            int matches = 0;
            int total = 0;
            for (int i = 0; i < read.Length && i < lines.Length; i++)
            {
                string fusedLine = lines[i];
                string readLine = read[i];
                for (int x = 0; x < fusedLine.Length && x < readLine.Length; x++)
                {
                    total++;
                    if (fusedLine[x] == readLine[x])
                        matches++;
                }
            }
            if (total > 0 && matches * 100 >= total * 92)
                agreeingFrames++;
        }

        // Report the vote agreement as the fused confidence instead of the
        // text parser's fixed 1.0, and keep the latest frame's region.
        var fused = new MrzResult(
            mrzFound: true,
            document: parsed.Document,
            raw: parsed.Raw,
            checks: parsed.Checks,
            issues: parsed.Issues,
            confidence: cells > 0 ? agreement / cells : 0,
            region: _best?.Region);
        return (fused, agreeingFrames);
    }

    private static bool Preferred(MrzResult candidate, MrzResult? incumbent)
    {
        if (incumbent is null)
            return true;
        if (candidate.IsValid != incumbent.IsValid)
            return candidate.IsValid;
        int candidateChecks = ValidChecks(candidate);
        int incumbentChecks = ValidChecks(incumbent);
        if (candidateChecks != incumbentChecks)
            return candidateChecks > incumbentChecks;
        return candidate.Confidence > incumbent.Confidence;
    }

    private static int ValidChecks(MrzResult result)
    {
        int count = 0;
        if (result.Checks.DocumentNumber == CheckDigitStatus.Valid)
            count++;
        if (result.Checks.BirthDate == CheckDigitStatus.Valid)
            count++;
        if (result.Checks.ExpiryDate == CheckDigitStatus.Valid)
            count++;
        if (result.Checks.OptionalData == CheckDigitStatus.Valid)
            count++;
        if (result.Checks.Composite == CheckDigitStatus.Valid)
            count++;
        return count;
    }
}
