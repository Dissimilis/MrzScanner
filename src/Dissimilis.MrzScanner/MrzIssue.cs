namespace Dissimilis.MrzScanner;

/// <summary>Category of a problem found while reading or parsing an MRZ.</summary>
public enum MrzIssueKind
{
    /// <summary>No MRZ-shaped region was detected in the image, or the text matches no known format.</summary>
    NotFound = 0,

    /// <summary>A line or field does not have the expected shape (length, character class).</summary>
    BadFormat,

    /// <summary>A check digit verification failed.</summary>
    CheckDigitFailed,

    /// <summary>Characters were recognized with low confidence.</summary>
    LowConfidence,

    /// <summary>A field parsed but its value is not plausible (for example month 13).</summary>
    InvalidValue,

    /// <summary>
    /// A value is well formed but not on the known list (for example an
    /// issuing state code missing from the ICAO country tables). Informational:
    /// does not make the result invalid, because the tables can lag reality.
    /// </summary>
    UnknownValue,
}

/// <summary>A single problem found while reading or parsing an MRZ.</summary>
public sealed class MrzIssue
{
    internal MrzIssue(string field, MrzIssueKind kind, string message)
    {
        Field = field;
        Kind = kind;
        Message = message;
    }

    /// <summary>Name of the affected field, or an empty string for document level issues.</summary>
    public string Field { get; }

    /// <summary>Issue category.</summary>
    public MrzIssueKind Kind { get; }

    /// <summary>Human readable description.</summary>
    public string Message { get; }

    /// <inheritdoc />
    public override string ToString() =>
        Field.Length == 0 ? $"{Kind}: {Message}" : $"{Kind} ({Field}): {Message}";
}
