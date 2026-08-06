using Dissimilis.MrzScanner.Internal;

namespace Dissimilis.MrzScanner.Tests;

/// <summary>
/// Renders synthetic document photos from the library's own glyph templates:
/// a card on a background with the MRZ band printed on it. No real data.
/// </summary>
internal static class SyntheticMrz
{
    public sealed class RenderOptions
    {
        public int Scale { get; set; } = 2;
        public int NoiseAmplitude { get; set; }
        public bool Blur { get; set; }
        public bool Rotate180 { get; set; }
        public int Seed { get; set; } = 12345;
    }

    /// <summary>Renders MRZ lines onto a card and returns a grayscale photo.</summary>
    public static (byte[] Pixels, int Width, int Height) Render(string[] lines, RenderOptions? options = null)
    {
        options ??= new RenderOptions();
        int scale = options.Scale;
        int cellWidth = OcrTemplates.Width * scale;
        int cellHeight = OcrTemplates.Height * scale;
        int lineGap = cellHeight / 3;
        int margin = cellWidth * 2;

        int bandWidth = lines[0].Length * cellWidth;
        int cardWidth = bandWidth + margin * 2;
        int bandHeight = lines.Length * (cellHeight + lineGap);

        // The card carries some non MRZ content above the band, like a real document.
        int cardHeight = bandHeight + margin * 3;
        int photoWidth = cardWidth + margin;
        int photoHeight = cardHeight + margin;

        var photo = new byte[photoWidth * photoHeight];
        for (int i = 0; i < photo.Length; i++)
            photo[i] = 140;

        int cardLeft = margin / 2;
        int cardTop = margin / 2;
        for (int y = 0; y < cardHeight; y++)
        {
            int row = (cardTop + y) * photoWidth + cardLeft;
            for (int x = 0; x < cardWidth; x++)
                photo[row + x] = 235;
        }

        int bandTop = cardTop + cardHeight - bandHeight - margin / 2;
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex];
            int y0 = bandTop + lineIndex * (cellHeight + lineGap);
            for (int charIndex = 0; charIndex < line.Length; charIndex++)
            {
                int glyph = OcrTemplates.IndexOf(line[charIndex]);
                if (glyph < 0)
                    continue;
                float[] ink = OcrTemplates.Ink[glyph];
                int x0 = cardLeft + margin + charIndex * cellWidth;
                for (int gy = 0; gy < cellHeight; gy++)
                {
                    int templateRow = gy / scale * OcrTemplates.Width;
                    int photoRow = (y0 + gy) * photoWidth + x0;
                    for (int gx = 0; gx < cellWidth; gx++)
                    {
                        float value = ink[templateRow + gx / scale];
                        if (value > 0.05f)
                        {
                            int existing = photo[photoRow + gx];
                            int dark = 235 - (int)(value * 205);
                            if (dark < existing)
                                photo[photoRow + gx] = (byte)dark;
                        }
                    }
                }
            }
        }

        if (options.NoiseAmplitude > 0)
        {
            var random = new Random(options.Seed);
            for (int i = 0; i < photo.Length; i++)
            {
                int noisy = photo[i] + random.Next(-options.NoiseAmplitude, options.NoiseAmplitude + 1);
                photo[i] = (byte)Math.Max(0, Math.Min(255, noisy));
            }
        }

        if (options.Blur)
            photo = BoxBlur(photo, photoWidth, photoHeight);

        if (options.Rotate180)
        {
            var rotated = new byte[photo.Length];
            for (int i = 0; i < photo.Length; i++)
                rotated[photo.Length - 1 - i] = photo[i];
            photo = rotated;
        }

        return (photo, photoWidth, photoHeight);
    }

    private static byte[] BoxBlur(byte[] pixels, int width, int height)
    {
        var result = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sum = 0;
                int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= height)
                        continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx;
                        if (xx < 0 || xx >= width)
                            continue;
                        sum += pixels[yy * width + xx];
                        n++;
                    }
                }
                result[y * width + x] = (byte)(sum / n);
            }
        }
        return result;
    }

    /// <summary>Encodes grayscale pixels as an uncompressed 24 bit BMP.</summary>
    public static byte[] ToBmp(byte[] pixels, int width, int height)
    {
        int rowSize = (width * 3 + 3) & ~3;
        int dataSize = rowSize * height;
        int fileSize = 54 + dataSize;
        var bmp = new byte[fileSize];

        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        WriteInt(bmp, 2, fileSize);
        WriteInt(bmp, 10, 54);
        WriteInt(bmp, 14, 40);
        WriteInt(bmp, 18, width);
        WriteInt(bmp, 22, height);
        bmp[26] = 1;
        bmp[28] = 24;
        WriteInt(bmp, 34, dataSize);

        for (int y = 0; y < height; y++)
        {
            int sourceRow = (height - 1 - y) * width;
            int targetRow = 54 + y * rowSize;
            for (int x = 0; x < width; x++)
            {
                byte value = pixels[sourceRow + x];
                bmp[targetRow + x * 3] = value;
                bmp[targetRow + x * 3 + 1] = value;
                bmp[targetRow + x * 3 + 2] = value;
            }
        }
        return bmp;
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
