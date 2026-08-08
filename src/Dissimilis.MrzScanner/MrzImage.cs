namespace Dissimilis.MrzScanner;

/// <summary>
/// A decoded image supplied by the caller, so the built-in decoder can be bypassed.
/// Create instances through the factory methods; they validate the buffer layout.
/// </summary>
public readonly struct MrzImage
{
    internal enum PixelLayout
    {
        Grayscale8,
        Rgb24,
        Bgr24,
        Rgba32,
        Bgra32,
    }

    private MrzImage(byte[] pixels, int width, int height, int stride, PixelLayout layout, int rotationDegrees)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        Layout = layout;
        RotationDegrees = rotationDegrees;
    }

    internal byte[] Pixels { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal int Stride { get; }
    internal PixelLayout Layout { get; }
    internal int RotationDegrees { get; }

    /// <summary>True when created through a factory method; default instances are invalid.</summary>
    public bool IsValid => Pixels is not null;

    /// <summary>Wraps an 8 bit per pixel grayscale buffer.</summary>
    /// <param name="pixels">Pixel rows, top to bottom.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public static MrzImage FromGrayscale8(byte[] pixels, int width, int height, int stride = 0)
        => Create(pixels, width, height, stride, 1, PixelLayout.Grayscale8, 0);

    /// <summary>
    /// Wraps an 8 bit grayscale buffer that needs rotating before reading.
    /// </summary>
    /// <param name="pixels">Pixel rows, top to bottom.</param>
    /// <param name="width">Width in pixels, before rotation.</param>
    /// <param name="height">Height in pixels, before rotation.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    /// <param name="rotationDegrees">
    /// Clockwise rotation (0, 90, 180 or 270) that brings the frame upright,
    /// matching the rotation cameras report for sensor oriented buffers.
    /// Regions on the result are in upright frame coordinates.
    /// </param>
    public static MrzImage FromGrayscale8(byte[] pixels, int width, int height, int stride, int rotationDegrees)
        => Create(pixels, width, height, stride, 1, PixelLayout.Grayscale8, rotationDegrees);

    /// <summary>Wraps a 24 bit per pixel RGB buffer.</summary>
    /// <param name="pixels">Pixel rows, top to bottom.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public static MrzImage FromRgb24(byte[] pixels, int width, int height, int stride = 0)
        => Create(pixels, width, height, stride, 3, PixelLayout.Rgb24, 0);

    /// <summary>Wraps a 24 bit per pixel BGR buffer.</summary>
    /// <param name="pixels">Pixel rows, top to bottom.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public static MrzImage FromBgr24(byte[] pixels, int width, int height, int stride = 0)
        => Create(pixels, width, height, stride, 3, PixelLayout.Bgr24, 0);

    /// <summary>Wraps a 32 bit per pixel RGBA buffer.</summary>
    /// <param name="pixels">Pixel rows, top to bottom.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public static MrzImage FromRgba32(byte[] pixels, int width, int height, int stride = 0)
        => Create(pixels, width, height, stride, 4, PixelLayout.Rgba32, 0);

    /// <summary>Wraps a 32 bit per pixel BGRA buffer.</summary>
    /// <param name="pixels">Pixel rows, top to bottom.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public static MrzImage FromBgra32(byte[] pixels, int width, int height, int stride = 0)
        => Create(pixels, width, height, stride, 4, PixelLayout.Bgra32, 0);

    /// <summary>
    /// Wraps an Android NV21 camera preview buffer. Only the luma plane at the
    /// start of the buffer is used; MRZ reading is grayscale, so the chroma
    /// plane is ignored and no color conversion happens at all.
    /// </summary>
    /// <param name="pixels">The full NV21 buffer, or just its Y plane.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="rowStride">Bytes per luma row; 0 means tightly packed.</param>
    public static MrzImage FromNv21(byte[] pixels, int width, int height, int rowStride = 0)
        => Create(pixels, width, height, rowStride, 1, PixelLayout.Grayscale8, 0);

    /// <summary>
    /// Wraps a sensor oriented NV21 buffer. Cameras report the clockwise
    /// rotation that brings a frame upright; passing it here saves rotating
    /// the buffer yourself. Regions on the result are in upright frame
    /// coordinates.
    /// </summary>
    /// <param name="pixels">The full NV21 buffer, or just its Y plane.</param>
    /// <param name="width">Frame width in pixels, before rotation.</param>
    /// <param name="height">Frame height in pixels, before rotation.</param>
    /// <param name="rowStride">Bytes per luma row; 0 means tightly packed.</param>
    /// <param name="rotationDegrees">Clockwise rotation: 0, 90, 180 or 270.</param>
    public static MrzImage FromNv21(byte[] pixels, int width, int height, int rowStride, int rotationDegrees)
        => Create(pixels, width, height, rowStride, 1, PixelLayout.Grayscale8, rotationDegrees);

    /// <summary>
    /// Wraps an NV12 buffer (iOS 420f/420v, Windows Media Foundation). Only
    /// the luma plane at the start of the buffer is used; the chroma plane is
    /// ignored and no color conversion happens at all.
    /// </summary>
    /// <param name="pixels">The full NV12 buffer, or just its Y plane.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="rowStride">Bytes per luma row; 0 means tightly packed.</param>
    public static MrzImage FromNv12(byte[] pixels, int width, int height, int rowStride = 0)
        => Create(pixels, width, height, rowStride, 1, PixelLayout.Grayscale8, 0);

    /// <summary>
    /// Wraps a sensor oriented NV12 buffer. Cameras report the clockwise
    /// rotation that brings a frame upright; passing it here saves rotating
    /// the buffer yourself. Regions on the result are in upright frame
    /// coordinates.
    /// </summary>
    /// <param name="pixels">The full NV12 buffer, or just its Y plane.</param>
    /// <param name="width">Frame width in pixels, before rotation.</param>
    /// <param name="height">Frame height in pixels, before rotation.</param>
    /// <param name="rowStride">Bytes per luma row; 0 means tightly packed.</param>
    /// <param name="rotationDegrees">Clockwise rotation: 0, 90, 180 or 270.</param>
    public static MrzImage FromNv12(byte[] pixels, int width, int height, int rowStride, int rotationDegrees)
        => Create(pixels, width, height, rowStride, 1, PixelLayout.Grayscale8, rotationDegrees);

    /// <summary>
    /// Wraps a planar I420/YV12 buffer (Android Camera2 YUV_420_888 with
    /// packed planes, WebRTC). Only the luma plane at the start of the buffer
    /// is used; the chroma planes are ignored.
    /// </summary>
    /// <param name="pixels">The full planar buffer, or just its Y plane.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="rowStride">Bytes per luma row; 0 means tightly packed.</param>
    public static MrzImage FromI420(byte[] pixels, int width, int height, int rowStride = 0)
        => Create(pixels, width, height, rowStride, 1, PixelLayout.Grayscale8, 0);

    /// <summary>
    /// Wraps a sensor oriented I420 buffer. Cameras report the clockwise
    /// rotation that brings a frame upright; passing it here saves rotating
    /// the buffer yourself. Regions on the result are in upright frame
    /// coordinates.
    /// </summary>
    /// <param name="pixels">The full I420 buffer, or just its Y plane.</param>
    /// <param name="width">Frame width in pixels, before rotation.</param>
    /// <param name="height">Frame height in pixels, before rotation.</param>
    /// <param name="rowStride">Bytes per luma row; 0 means tightly packed.</param>
    /// <param name="rotationDegrees">Clockwise rotation: 0, 90, 180 or 270.</param>
    public static MrzImage FromI420(byte[] pixels, int width, int height, int rowStride, int rotationDegrees)
        => Create(pixels, width, height, rowStride, 1, PixelLayout.Grayscale8, rotationDegrees);

    private static MrzImage Create(byte[] pixels, int width, int height, int stride, int bytesPerPixel, PixelLayout layout, int rotationDegrees)
    {
        if (pixels is null)
            throw new ArgumentNullException(nameof(pixels));
        if (rotationDegrees is not (0 or 90 or 180 or 270))
            throw new ArgumentOutOfRangeException(nameof(rotationDegrees), rotationDegrees,
                "Rotation must be 0, 90, 180 or 270 degrees.");
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");

        // Long arithmetic so extreme dimensions cannot wrap the validation.
        long minStrideLong = (long)width * bytesPerPixel;
        if (minStrideLong > int.MaxValue || (long)width * height > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Image dimensions are too large.");
        int minStride = (int)minStrideLong;
        if (stride == 0)
            stride = minStride;
        if (stride < minStride)
        {
            // A rotation passed positionally in the stride slot is the likely
            // cause when the value happens to be a rotation constant; the
            // rotation overloads take stride first.
            string suffix = stride is 90 or 180 or 270
                ? " If this value was meant as a rotation, pass the stride first: FromX(pixels, width, height, 0, rotationDegrees)."
                : string.Empty;
            throw new ArgumentOutOfRangeException(nameof(stride), stride,
                $"Stride must be at least {minStride} bytes for width {width}.{suffix}");
        }

        long required = (long)stride * (height - 1) + minStride;
        if (pixels.Length < required)
            throw new ArgumentException(
                $"Pixel buffer has {pixels.Length} bytes but the layout requires at least {required}.",
                nameof(pixels));

        return new MrzImage(pixels, width, height, stride, layout, rotationDegrees);
    }
}
