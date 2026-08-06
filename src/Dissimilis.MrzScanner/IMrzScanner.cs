namespace Dissimilis.MrzScanner;

/// <summary>Reads the MRZ of an identity document from an image.</summary>
public interface IMrzScanner
{
    /// <summary>Reads the MRZ from an encoded image (JPEG, PNG, BMP) supplied as a stream.</summary>
    /// <param name="image">Stream positioned at the start of the encoded image. Must not be null.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MrzResult> ReadAsync(Stream image, CancellationToken ct = default);

    /// <summary>Reads the MRZ from an encoded image (JPEG, PNG, BMP) supplied as a byte array.</summary>
    /// <param name="image">The encoded image bytes. Must not be null.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MrzResult> ReadAsync(byte[] image, CancellationToken ct = default);

    /// <summary>Reads the MRZ from an encoded image file (JPEG, PNG, BMP).</summary>
    /// <param name="filePath">Path of the image file. Must not be null.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MrzResult> ReadAsync(string filePath, CancellationToken ct = default);

    /// <summary>Reads the MRZ from caller-decoded pixels, bypassing the built-in decoder.</summary>
    /// <param name="image">Pixel data created through a <see cref="MrzImage" /> factory method.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MrzResult> ReadAsync(MrzImage image, CancellationToken ct = default);

    /// <summary>Reads the MRZ from an encoded image (JPEG, PNG, BMP) supplied as a stream.</summary>
    /// <param name="image">Stream positioned at the start of the encoded image. Must not be null.</param>
    MrzResult Read(Stream image);

    /// <summary>Reads the MRZ from an encoded image (JPEG, PNG, BMP) supplied as a byte array.</summary>
    /// <param name="image">The encoded image bytes. Must not be null.</param>
    MrzResult Read(byte[] image);

    /// <summary>Reads the MRZ from an encoded image file (JPEG, PNG, BMP).</summary>
    /// <param name="filePath">Path of the image file. Must not be null.</param>
    MrzResult Read(string filePath);

    /// <summary>Reads the MRZ from caller-decoded pixels, bypassing the built-in decoder.</summary>
    /// <param name="image">Pixel data created through a <see cref="MrzImage" /> factory method.</param>
    MrzResult Read(MrzImage image);

    /// <summary>
    /// Detects MRZ shaped bands without recognizing their characters. Runs in
    /// a few milliseconds: suitable per camera frame for overlay guides,
    /// cropping, or deciding when a full read is worth starting. Regions come
    /// strongest first; detection alone cannot distinguish a band from its
    /// 180 degree flip, so <see cref="MrzRegion.RotationDegrees" /> is 0 or 90.
    /// </summary>
    /// <param name="image">Pixel data created through a <see cref="MrzImage" /> factory method.</param>
    IReadOnlyList<MrzRegion> LocateMrz(MrzImage image);

    /// <summary>Detects MRZ shaped bands in an encoded image (JPEG, PNG, BMP) without recognizing characters.</summary>
    /// <param name="image">The encoded image bytes. Must not be null.</param>
    IReadOnlyList<MrzRegion> LocateMrz(byte[] image);

    /// <summary>Detects MRZ shaped bands in an encoded image file without recognizing characters.</summary>
    /// <param name="filePath">Path of the image file. Must not be null.</param>
    IReadOnlyList<MrzRegion> LocateMrz(string filePath);
}
