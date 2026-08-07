using Xunit;

namespace Dissimilis.MrzScanner.Tests;

/// <summary>
/// Covers the camera oriented surface: raw YUV frame input, tilted frame
/// deskew, and capture hints.
/// </summary>
public class CameraCaptureTests
{
    private static readonly string[] Td3Lines =
    {
        "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<",
        "L898902C36UTO7408122F1204159ZE184226B<<<<<10",
    };

    [Fact]
    public void Reads_an_nv21_camera_buffer()
    {
        (byte[] gray, int width, int height) = SyntheticMrz.Render(Td3Lines);

        // NV21: full size Y plane followed by interleaved VU at quarter size.
        var nv21 = new byte[width * height + width * height / 2];
        Array.Copy(gray, nv21, gray.Length);
        for (int i = gray.Length; i < nv21.Length; i++)
            nv21[i] = 128;

        MrzResult result = MrzScanner.Default.Read(MrzImage.FromNv21(nv21, width, height));

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal("L898902C3", result.Document!.DocumentNumber);
    }

    [Fact]
    public void Reads_a_bare_luma_plane_as_i420()
    {
        (byte[] gray, int width, int height) = SyntheticMrz.Render(Td3Lines);

        MrzResult result = MrzScanner.Default.Read(MrzImage.FromI420(gray, width, height));

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
    }

    [Fact]
    public void Rejects_a_luma_buffer_smaller_than_the_frame()
    {
        Assert.Throws<ArgumentException>(() => MrzImage.FromNv12(new byte[100], 100, 100));
    }

    [Theory]
    [InlineData(4.0)]
    [InlineData(-4.0)]
    [InlineData(6.5)]
    public void Reads_a_tilted_document(double degrees)
    {
        (byte[] gray, int width, int height) = SyntheticMrz.Render(Td3Lines);
        byte[] tilted = Rotate(gray, width, height, degrees);

        MrzResult result = MrzScanner.Default.Read(MrzImage.FromGrayscale8(tilted, width, height));

        Assert.True(result.IsValid,
            $"tilt {degrees}: " + string.Join("; ", result.Issues));
        Assert.Equal("L898902C3", result.Document!.DocumentNumber);
    }

    [Fact]
    public void Blank_frame_reports_no_mrz_detected()
    {
        var flat = new byte[640 * 480];
        for (int i = 0; i < flat.Length; i++)
            flat[i] = 128;

        MrzResult result = MrzScanner.Default.Read(MrzImage.FromGrayscale8(flat, 640, 480));

        Assert.False(result.MrzFound);
        Assert.Contains(MrzCaptureHint.NoMrzDetected, result.CaptureHints);
        Assert.Contains(MrzCaptureHint.LowContrast, result.CaptureHints);
    }

    [Fact]
    public void Clean_read_carries_no_hints()
    {
        (byte[] gray, int width, int height) = SyntheticMrz.Render(Td3Lines);

        MrzResult result = MrzScanner.Default.Read(MrzImage.FromGrayscale8(gray, width, height));

        Assert.True(result.IsValid);
        Assert.Empty(result.CaptureHints);
    }

    [Fact]
    public void Tiny_mrz_reports_too_small()
    {
        (byte[] gray, int width, int height) = SyntheticMrz.Render(Td3Lines);

        // Shrink until the character pitch drops well under readable size.
        int factor = 8;
        int smallWidth = width / factor;
        int smallHeight = height / factor;
        var small = new byte[smallWidth * smallHeight];
        for (int y = 0; y < smallHeight; y++)
        {
            for (int x = 0; x < smallWidth; x++)
                small[y * smallWidth + x] = gray[y * factor * width + x * factor];
        }

        MrzResult result = MrzScanner.Default.Read(MrzImage.FromGrayscale8(small, smallWidth, smallHeight));

        // Depending on how much survives the shrink the band is either found
        // and measured as too small, or not detected at all; both must guide.
        if (!result.IsValid)
        {
            Assert.True(
                result.CaptureHints.Contains(MrzCaptureHint.TooSmall) ||
                result.CaptureHints.Contains(MrzCaptureHint.NoMrzDetected),
                "hints: " + string.Join(", ", result.CaptureHints));
        }
    }

    [Fact]
    public void Video_session_exposes_last_frame_hints()
    {
        var session = new MrzVideoSession();
        var flat = new byte[640 * 480];
        for (int i = 0; i < flat.Length; i++)
            flat[i] = 128;

        session.Feed(MrzImage.FromGrayscale8(flat, 640, 480));

        Assert.Contains(MrzCaptureHint.NoMrzDetected, session.LastFrameHints);

        session.Reset();
        Assert.Empty(session.LastFrameHints);
    }

    [Fact]
    public void Estimator_returns_zero_for_featureless_frames()
    {
        var flat = new Internal.GrayImage(320, 240);
        for (int i = 0; i < flat.Pixels.Length; i++)
            flat.Pixels[i] = 128;
        Assert.Equal(0, Internal.Deskew.EstimateCorrectionDegrees(flat));

        var random = new Random(7);
        var noise = new Internal.GrayImage(320, 240);
        for (int i = 0; i < noise.Pixels.Length; i++)
            noise.Pixels[i] = (byte)(120 + random.Next(0, 12));
        Assert.Equal(0, Internal.Deskew.EstimateCorrectionDegrees(noise));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Reads_a_sensor_oriented_buffer_with_rotation_metadata(int rotation)
    {
        (byte[] gray, int width, int height) = SyntheticMrz.Render(Td3Lines);

        // Undo the reported rotation to fake what the sensor would deliver:
        // a buffer that needs rotating clockwise by that much to sit upright.
        (byte[] sensor, int sw, int sh) = RotateExact(gray, width, height, 360 - rotation);

        // SingleFrame never tries other orientations on its own, so a pass
        // proves the rotation metadata was applied, not searched for.
        var reader = new MrzScanner(new MrzScannerOptions { SearchEffort = MrzSearchEffort.SingleFrame });
        MrzResult result = reader.Read(MrzImage.FromGrayscale8(sensor, sw, sh, 0, rotation));

        Assert.True(result.IsValid, $"rotation {rotation}: " + string.Join("; ", result.Issues));
    }

    [Fact]
    public void Rejects_invalid_rotation_metadata()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MrzImage.FromNv21(new byte[100 * 100], 100, 100, 0, 45));
    }

    [Fact]
    public void Capture_hints_cannot_be_mutated_through_a_cast()
    {
        var flat = new byte[640 * 480];
        for (int i = 0; i < flat.Length; i++)
            flat[i] = 128;

        MrzResult result = MrzScanner.Default.Read(MrzImage.FromGrayscale8(flat, 640, 480));

        Assert.NotEmpty(result.CaptureHints);
        Assert.Null(result.CaptureHints as List<MrzCaptureHint>);
    }

    /// <summary>Exact 90 degree step rotation, clockwise, for building sensor oriented buffers.</summary>
    private static (byte[] Pixels, int Width, int Height) RotateExact(byte[] pixels, int width, int height, int degrees)
    {
        degrees %= 360;
        if (degrees == 0)
            return (pixels, width, height);
        if (degrees == 180)
        {
            var flipped = new byte[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
                flipped[pixels.Length - 1 - i] = pixels[i];
            return (flipped, width, height);
        }
        var rotated = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (degrees == 90)
                    rotated[x * height + (height - 1 - y)] = pixels[y * width + x];
                else
                    rotated[(width - 1 - x) * height + y] = pixels[y * width + x];
            }
        }
        return (rotated, height, width);
    }

    private static byte[] Rotate(byte[] pixels, int width, int height, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double cx = (width - 1) / 2.0;
        double cy = (height - 1) / 2.0;
        var result = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double dx = x - cx;
                double dy = y - cy;
                double sx = cx + dx * cos - dy * sin;
                double sy = cy + dx * sin + dy * cos;
                int ix = Math.Max(0, Math.Min(width - 1, (int)Math.Round(sx)));
                int iy = Math.Max(0, Math.Min(height - 1, (int)Math.Round(sy)));
                result[y * width + x] = pixels[iy * width + ix];
            }
        }
        return result;
    }
}
