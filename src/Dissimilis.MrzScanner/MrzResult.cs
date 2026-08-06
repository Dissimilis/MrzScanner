using System.Collections.Generic;

namespace Dissimilis.MrzScanner;

/// <summary>
/// Outcome of reading or parsing an MRZ. Never thrown; inspect the properties
/// to see what was found, what parsed, and what failed.
/// </summary>
public sealed class MrzResult
{
    internal MrzResult(
        bool mrzFound,
        MrzDocument? document,
        MrzRawData? raw,
        MrzChecks checks,
        IReadOnlyList<MrzIssue> issues,
        double confidence,
        MrzRegion? region = null,
        MrzFieldConfidence? fieldConfidence = null)
    {
        MrzFound = mrzFound;
        Document = document;
        Raw = raw;
        Checks = checks;
        Issues = issues is MrzIssue[] || issues is System.Collections.ObjectModel.ReadOnlyCollection<MrzIssue>
            ? issues
            : new System.Collections.ObjectModel.ReadOnlyCollection<MrzIssue>(new List<MrzIssue>(issues));
        Confidence = confidence;
        Region = region;
        FieldConfidence = fieldConfidence;
    }

    /// <summary>True when an MRZ-shaped region or text was found at all.</summary>
    public bool MrzFound { get; }

    /// <summary>
    /// True when an MRZ was found, all fields parsed, and every present check digit is valid.
    /// </summary>
    public bool IsValid =>
        MrzFound && Document is not null && Checks.AllValid && !HasErrors;

    /// <summary>The parsed document, or null when nothing could be parsed.</summary>
    public MrzDocument? Document { get; }

    /// <summary>The exact recognized characters, or null when no MRZ was found.</summary>
    public MrzRawData? Raw { get; }

    /// <summary>Check digit verification results.</summary>
    public MrzChecks Checks { get; }

    /// <summary>All problems found while reading and parsing.</summary>
    public IReadOnlyList<MrzIssue> Issues { get; }

    /// <summary>
    /// Estimate between 0 and 1 of how accurately the MRZ was read: roughly
    /// the expected fraction of characters that are correct, calibrated
    /// against measured accuracy on labeled documents. Checksum verified
    /// reads score near 1. Always 1.0 for text input.
    /// </summary>
    public double Confidence { get; }

    /// <summary>
    /// Per field confidence estimates, or null when no MRZ was found or the
    /// input was text.
    /// </summary>
    public MrzFieldConfidence? FieldConfidence { get; }

    /// <summary>
    /// Where the MRZ was found in the supplied image, with its resolved
    /// rotation. Null when no MRZ was found or the input was text.
    /// </summary>
    public MrzRegion? Region { get; }

    private bool HasErrors
    {
        get
        {
            for (int i = 0; i < Issues.Count; i++)
            {
                if (Issues[i].Kind is MrzIssueKind.NotFound or MrzIssueKind.BadFormat
                    or MrzIssueKind.CheckDigitFailed or MrzIssueKind.InvalidValue)
                    return true;
            }
            return false;
        }
    }

    internal static MrzResult NotFound(string message) => new(
        mrzFound: false,
        document: null,
        raw: null,
        checks: MrzChecks.Empty,
        issues: new[] { new MrzIssue(string.Empty, MrzIssueKind.NotFound, message) },
        confidence: 0.0);
}
