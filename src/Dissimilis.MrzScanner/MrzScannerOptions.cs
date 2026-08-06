namespace Dissimilis.MrzScanner;

/// <summary>Options for <see cref="MrzScanner" />.</summary>
public sealed class MrzScannerOptions
{
    /// <summary>
    /// Images whose longer side exceeds this are downscaled before processing.
    /// Default 2000. Lower is faster, higher preserves more detail for small MRZs.
    /// </summary>
    public int MaxImageDimension { get; set; } = 2000;

    /// <summary>
    /// How much of the search space one read explores. Exhaustive (default)
    /// tries every located band in every orientation; right for one-off
    /// photos, but an image without an MRZ can take seconds. SingleFrame
    /// bounds the cost per frame for video, where the next frame is a better
    /// investment than a deeper search of this one.
    /// </summary>
    public MrzSearchEffort SearchEffort { get; set; } = MrzSearchEffort.Exhaustive;
}

/// <summary>Search effort presets for <see cref="MrzScannerOptions.SearchEffort" />.</summary>
public enum MrzSearchEffort
{
    /// <summary>Full search: every candidate band, both orientations, sideways fallback.</summary>
    Exhaustive = 0,

    /// <summary>
    /// Bounded search for continuous capture: two best bands, no sideways
    /// fallback. Readable frames still read; hopeless ones return fast.
    /// </summary>
    SingleFrame = 1,
}
