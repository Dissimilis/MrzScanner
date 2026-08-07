namespace Dissimilis.MrzScanner;

/// <summary>
/// Machine usable feedback about why a frame did not produce a confident
/// read, so capture UIs can guide the user. Hints describe the frame that
/// was just processed; a clean valid read carries none.
/// </summary>
public enum MrzCaptureHint
{
    /// <summary>Nothing MRZ shaped was detected. Point the camera at the MRZ side of the document.</summary>
    NoMrzDetected,

    /// <summary>The MRZ is too small in the frame to read reliably. Move closer.</summary>
    TooSmall,

    /// <summary>The MRZ region touches the frame edge and may be cropped. Fit the whole document.</summary>
    CutOff,

    /// <summary>The MRZ was located but characters are too soft to read. Hold steady or refocus.</summary>
    Blurry,

    /// <summary>
    /// A hard clipped highlight covers a large part of the MRZ band. Tilt the
    /// document away from the light. Detection needs full range luma; video
    /// range buffers (studio swing, luma capped near 235) never clip, so this
    /// hint does not fire for them.
    /// </summary>
    Glare,

    /// <summary>The image has very little tonal range. Improve the lighting.</summary>
    LowContrast,
}
