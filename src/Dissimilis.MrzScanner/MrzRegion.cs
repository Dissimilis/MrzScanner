namespace Dissimilis.MrzScanner;

/// <summary>
/// A detected MRZ band in the supplied image, in original pixel coordinates.
/// </summary>
public sealed class MrzRegion
{
    internal MrzRegion(int left, int top, int width, int height, int rotationDegrees, int lineCount, double score)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        RotationDegrees = rotationDegrees;
        LineCount = lineCount;
        Score = score;
    }

    /// <summary>Left edge of the band in pixels of the supplied image.</summary>
    public int Left { get; }

    /// <summary>Top edge of the band in pixels of the supplied image.</summary>
    public int Top { get; }

    /// <summary>Width of the band in pixels.</summary>
    public int Width { get; }

    /// <summary>Height of the band in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// Clockwise rotation, in degrees (0, 90, 180, or 270), that brings the
    /// MRZ text upright. Regions from <see cref="MrzScanner.LocateMrz(MrzImage)" />
    /// report 0 or 90 only: without recognizing the characters, a band cannot
    /// be told apart from its 180 degree flip. Regions attached to a
    /// <see cref="MrzResult" /> carry the fully resolved rotation.
    /// </summary>
    public int RotationDegrees { get; }

    /// <summary>Estimated number of MRZ lines in the band (2 or 3).</summary>
    public int LineCount { get; }

    /// <summary>
    /// Detection strength, higher is stronger. Comparable between regions of
    /// one image; not calibrated across images.
    /// </summary>
    public double Score { get; }
}
