namespace Dissimilis.MrzScanner.Internal;

/// <summary>An 8 bit grayscale image, the working format of the whole pipeline.</summary>
internal sealed class GrayImage
{
    public GrayImage(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height];
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Row major, tightly packed.</summary>
    public byte[] Pixels { get; }

    public byte At(int x, int y) => Pixels[y * Width + x];

    /// <summary>Converts caller supplied pixels of any supported layout.</summary>
    public static GrayImage FromMrzImage(MrzImage source)
    {
        var gray = new GrayImage(source.Width, source.Height);
        byte[] src = source.Pixels;
        byte[] dst = gray.Pixels;
        int width = source.Width;
        int height = source.Height;
        int stride = source.Stride;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            int dstRow = y * width;
            switch (source.Layout)
            {
                case MrzImage.PixelLayout.Grayscale8:
                    Array.Copy(src, rowStart, dst, dstRow, width);
                    break;
                case MrzImage.PixelLayout.Rgb24:
                    for (int x = 0; x < width; x++)
                    {
                        int i = rowStart + x * 3;
                        dst[dstRow + x] = Luma(src[i], src[i + 1], src[i + 2]);
                    }
                    break;
                case MrzImage.PixelLayout.Bgr24:
                    for (int x = 0; x < width; x++)
                    {
                        int i = rowStart + x * 3;
                        dst[dstRow + x] = Luma(src[i + 2], src[i + 1], src[i]);
                    }
                    break;
                case MrzImage.PixelLayout.Rgba32:
                    for (int x = 0; x < width; x++)
                    {
                        int i = rowStart + x * 4;
                        dst[dstRow + x] = LumaOverWhite(src[i], src[i + 1], src[i + 2], src[i + 3]);
                    }
                    break;
                case MrzImage.PixelLayout.Bgra32:
                    for (int x = 0; x < width; x++)
                    {
                        int i = rowStart + x * 4;
                        dst[dstRow + x] = LumaOverWhite(src[i + 2], src[i + 1], src[i], src[i + 3]);
                    }
                    break;
            }
        }
        return gray;
    }

    /// <summary>Converts tightly packed RGBA (the decoder output format).</summary>
    public static GrayImage FromRgba(byte[] rgba, int width, int height)
    {
        var gray = new GrayImage(width, height);
        byte[] dst = gray.Pixels;
        int pixelCount = width * height;
        for (int p = 0; p < pixelCount; p++)
        {
            int i = p * 4;
            dst[p] = LumaOverWhite(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
        }
        return gray;
    }

    private static byte Luma(byte r, byte g, byte b) => (byte)((r * 77 + g * 150 + b * 29) >> 8);

    /// <summary>
    /// Luma with the pixel composited over a white background, so transparent
    /// regions of PNGs do not read as solid black.
    /// </summary>
    private static byte LumaOverWhite(byte r, byte g, byte b, byte a)
    {
        if (a == 255)
            return Luma(r, g, b);
        int luma = (r * 77 + g * 150 + b * 29) >> 8;
        return (byte)((luma * a + 255 * (255 - a)) / 255);
    }

    /// <summary>
    /// Downscales with area averaging so the longer side does not exceed
    /// <paramref name="maxDimension" />. Returns this instance when small enough.
    /// </summary>
    public GrayImage DownscaleTo(int maxDimension)
    {
        int longSide = Math.Max(Width, Height);
        if (longSide <= maxDimension)
            return this;

        double scale = (double)maxDimension / longSide;
        int newWidth = Math.Max(1, (int)Math.Round(Width * scale));
        int newHeight = Math.Max(1, (int)Math.Round(Height * scale));
        var result = new GrayImage(newWidth, newHeight);

        for (int y = 0; y < newHeight; y++)
        {
            int srcY0 = (int)((long)y * Height / newHeight);
            int srcY1 = Math.Max(srcY0 + 1, (int)((long)(y + 1) * Height / newHeight));
            for (int x = 0; x < newWidth; x++)
            {
                int srcX0 = (int)((long)x * Width / newWidth);
                int srcX1 = Math.Max(srcX0 + 1, (int)((long)(x + 1) * Width / newWidth));
                long sum = 0;
                for (int sy = srcY0; sy < srcY1; sy++)
                {
                    int row = sy * Width;
                    for (int sx = srcX0; sx < srcX1; sx++)
                        sum += Pixels[row + sx];
                }
                result.Pixels[y * newWidth + x] = (byte)(sum / ((long)(srcY1 - srcY0) * (srcX1 - srcX0)));
            }
        }
        return result;
    }

    /// <summary>Bilinear resize. Used to upscale tiny MRZ crops before matching.</summary>
    public GrayImage Resize(int newWidth, int newHeight)
    {
        var result = new GrayImage(newWidth, newHeight);
        double scaleX = Width / (double)newWidth;
        double scaleY = Height / (double)newHeight;
        for (int y = 0; y < newHeight; y++)
        {
            double sy = (y + 0.5) * scaleY - 0.5;
            int iy = (int)Math.Floor(sy);
            double fy = sy - iy;
            int y0 = Math.Max(0, Math.Min(Height - 1, iy));
            int y1 = Math.Max(0, Math.Min(Height - 1, iy + 1));
            for (int x = 0; x < newWidth; x++)
            {
                double sx = (x + 0.5) * scaleX - 0.5;
                int ix = (int)Math.Floor(sx);
                double fx = sx - ix;
                int x0 = Math.Max(0, Math.Min(Width - 1, ix));
                int x1 = Math.Max(0, Math.Min(Width - 1, ix + 1));
                double value =
                    Pixels[y0 * Width + x0] * (1 - fx) * (1 - fy) +
                    Pixels[y0 * Width + x1] * fx * (1 - fy) +
                    Pixels[y1 * Width + x0] * (1 - fx) * fy +
                    Pixels[y1 * Width + x1] * fx * fy;
                result.Pixels[y * newWidth + x] = (byte)Math.Round(value);
            }
        }
        return result;
    }

    /// <summary>Returns a copy rotated by 180 degrees.</summary>
    public GrayImage Rotate180()
    {
        var result = new GrayImage(Width, Height);
        int n = Pixels.Length;
        for (int i = 0; i < n; i++)
            result.Pixels[n - 1 - i] = Pixels[i];
        return result;
    }

    /// <summary>Returns a copy rotated 90 degrees clockwise.</summary>
    public GrayImage Rotate90()
    {
        var result = new GrayImage(Height, Width);
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            int targetX = Height - 1 - y;
            for (int x = 0; x < Width; x++)
                result.Pixels[x * Height + targetX] = Pixels[row + x];
        }
        return result;
    }
}
