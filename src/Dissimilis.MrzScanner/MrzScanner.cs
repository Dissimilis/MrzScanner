using Dissimilis.MrzScanner.Internal;

namespace Dissimilis.MrzScanner;

/// <summary>
/// Reads the MRZ of an identity document from an image: locates the zone,
/// recognizes the characters, parses and validates the fields.
/// Instances are stateless and thread safe.
/// </summary>
/// <remarks>
/// Reading is CPU bound; the async overloads offload the work to the thread
/// pool so a UI thread stays responsive. Callers that manage their own
/// concurrency can use the synchronous overloads directly. Buffers passed to
/// the async overloads must not be modified until the returned task completes.
/// </remarks>
public sealed class MrzScanner : IMrzScanner
{
    private readonly MrzScannerOptions _options;
    private readonly MrzParser _parser = new();

    /// <summary>Creates a reader with default options.</summary>
    public MrzScanner()
        : this(new MrzScannerOptions())
    {
    }

    /// <summary>Creates a reader.</summary>
    /// <param name="options">Reader options. Must not be null.</param>
    public MrzScanner(MrzScannerOptions options)
    {
        if (options is null)
            throw new ArgumentNullException(nameof(options));
        if (options.MaxImageDimension < 200)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxImageDimension,
                "MaxImageDimension must be at least 200 pixels.");
        if (options.SearchEffort is not (MrzSearchEffort.Exhaustive or MrzSearchEffort.SingleFrame))
            throw new ArgumentOutOfRangeException(nameof(options), options.SearchEffort,
                "SearchEffort must be a defined MrzSearchEffort value.");
        _options = new MrzScannerOptions
        {
            MaxImageDimension = options.MaxImageDimension,
            SearchEffort = options.SearchEffort,
        };
    }

    /// <summary>A shared instance with default options.</summary>
    public static MrzScanner Default { get; } = new();

    /// <inheritdoc />
    public async Task<MrzResult> ReadAsync(Stream image, CancellationToken ct = default)
    {
        if (image is null)
            throw new ArgumentNullException(nameof(image));
        byte[] data = await ReadAllBytesAsync(image, ct).ConfigureAwait(false);
        return await Task.Run(() => ReadEncoded(data, ct), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<MrzResult> ReadAsync(byte[] image, CancellationToken ct = default)
    {
        if (image is null)
            throw new ArgumentNullException(nameof(image));
        return Task.Run(() => ReadEncoded(image, ct), ct);
    }

    /// <inheritdoc />
    public async Task<MrzResult> ReadAsync(string filePath, CancellationToken ct = default)
    {
        if (filePath is null)
            throw new ArgumentNullException(nameof(filePath));
        byte[] data;
        using (FileStream stream = File.OpenRead(filePath))
        {
            data = await ReadAllBytesAsync(stream, ct).ConfigureAwait(false);
        }
        return await Task.Run(() => ReadEncoded(data, ct), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<MrzResult> ReadAsync(MrzImage image, CancellationToken ct = default)
    {
        ValidateImage(image);
        return Task.Run(() => Process(GrayImage.FromMrzImage(image), ct), ct);
    }

    /// <inheritdoc />
    public MrzResult Read(Stream image)
    {
        if (image is null)
            throw new ArgumentNullException(nameof(image));
        using var buffer = new LimitedMemoryStream(MaxEncodedBytes);
        image.CopyTo(buffer);
        return ReadEncoded(buffer.ToArray(), CancellationToken.None);
    }

    /// <inheritdoc />
    public MrzResult Read(byte[] image)
    {
        if (image is null)
            throw new ArgumentNullException(nameof(image));
        return ReadEncoded(image, CancellationToken.None);
    }

    /// <inheritdoc />
    public MrzResult Read(string filePath)
    {
        if (filePath is null)
            throw new ArgumentNullException(nameof(filePath));
        return ReadEncoded(File.ReadAllBytes(filePath), CancellationToken.None);
    }

    /// <inheritdoc />
    public MrzResult Read(MrzImage image)
    {
        ValidateImage(image);
        return Process(GrayImage.FromMrzImage(image), CancellationToken.None);
    }

    private static void ValidateImage(MrzImage image)
    {
        if (!image.IsValid)
            throw new ArgumentException(
                "The image was not created through a MrzImage factory method.", nameof(image));
    }

    private MrzResult ReadEncoded(byte[] data, CancellationToken ct)
    {
        (GrayImage? gray, ImageDecoder.Status status) = ImageDecoder.Decode(data);
        if (gray is null)
        {
            return MrzResult.NotFound(status switch
            {
                ImageDecoder.Status.TooLarge => "The image is too large to process safely.",
                ImageDecoder.Status.UnsupportedWebP =>
                    "The data is a WebP image, which the built-in decoder does not support. " +
                    "Decode it with your own library and use the MrzImage overloads.",
                _ => "The data is not a decodable JPEG, PNG, or BMP image.",
            });
        }
        return Process(gray, ct);
    }

    private MrzResult Process(GrayImage image, CancellationToken ct)
    {
        return MrzPipeline.Process(image, _options, _parser, ct);
    }

    /// <inheritdoc />
    public IReadOnlyList<MrzRegion> LocateMrz(MrzImage image)
    {
        ValidateImage(image);
        return Locate(GrayImage.FromMrzImage(image));
    }

    /// <inheritdoc />
    public IReadOnlyList<MrzRegion> LocateMrz(byte[] image)
    {
        if (image is null)
            throw new ArgumentNullException(nameof(image));
        (GrayImage? gray, _) = ImageDecoder.Decode(image);
        return gray is null ? Array.Empty<MrzRegion>() : Locate(gray);
    }

    /// <inheritdoc />
    public IReadOnlyList<MrzRegion> LocateMrz(string filePath)
    {
        if (filePath is null)
            throw new ArgumentNullException(nameof(filePath));
        return LocateMrz(File.ReadAllBytes(filePath));
    }

    private IReadOnlyList<MrzRegion> Locate(GrayImage image)
    {
        var regions = new List<MrzRegion>();
        AddRegions(image, rotatedPass: false, regions);
        AddRegions(image.Rotate90(), rotatedPass: true, regions);
        regions.Sort((a, b) => b.Score.CompareTo(a.Score));
        return regions;
    }

    private void AddRegions(GrayImage passImage, bool rotatedPass, List<MrzRegion> regions)
    {
        GrayImage working = passImage.DownscaleTo(_options.MaxImageDimension);
        double scaleX = passImage.Width / (double)working.Width;
        double scaleY = passImage.Height / (double)working.Height;
        foreach (BandCandidate candidate in MrzLocator.Locate(working))
        {
            int left = (int)(candidate.Left * scaleX);
            int top = (int)(candidate.Top * scaleY);
            regions.Add(MrzPipeline.MakeRegion(
                left,
                top,
                (int)Math.Ceiling(candidate.Right * scaleX) - left,
                (int)Math.Ceiling(candidate.Bottom * scaleY) - top,
                passImage.Width,
                rotatedPass,
                flipped: false,
                candidate.LineCountEstimate,
                candidate.Score));
        }
    }

    /// <summary>
    /// Encoded inputs above this size are rejected before decoding: no real
    /// document photo comes close, and buffering an unbounded stream would
    /// exhaust memory before the decoder could classify it as bad input.
    /// </summary>
    internal const int MaxEncodedBytes = 128 * 1024 * 1024;

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new LimitedMemoryStream(MaxEncodedBytes);
#if NET8_0_OR_GREATER
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
#else
        await stream.CopyToAsync(buffer, 81920, ct).ConfigureAwait(false);
#endif
        return buffer.ToArray();
    }

    /// <summary>A MemoryStream that stops accepting bytes past a limit instead of growing unbounded.</summary>
    private sealed class LimitedMemoryStream : MemoryStream
    {
        private readonly int _limit;

        public LimitedMemoryStream(int limit) => _limit = limit;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > _limit)
                throw new IOException("The input stream exceeds the maximum supported image size.");
            base.Write(buffer, offset, count);
        }
    }
}
