using Dissimilis.MrzScanner;
using Dissimilis.MrzScanner.Internal;

namespace MrzHarness;

/// <summary>
/// Renders a synthetic specimen card (ICAO 9303 example data, no real
/// document), runs detection on it, draws the detected region, and writes a
/// BMP for the README. Everything in the image comes from the library's own
/// glyph templates.
/// </summary>
internal static class RenderRunner
{
    private static readonly string[] SpecimenLines =
    {
        "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<",
        "L898902C36UTO7408122F1204159ZE184226B<<<<<10",
    };

    public static int Run(string outputPath)
    {
        int scale = 2;
        int cellWidth = OcrTemplates.Width * scale;
        int cellHeight = OcrTemplates.Height * scale;
        int lineGap = cellHeight / 3;
        int margin = cellWidth * 2;

        int bandWidth = SpecimenLines[0].Length * cellWidth;
        int cardWidth = bandWidth + margin * 2;
        int bandHeight = SpecimenLines.Length * (cellHeight + lineGap);
        int cardHeight = bandHeight + margin * 3;
        int photoWidth = cardWidth + margin;
        int photoHeight = cardHeight + margin;

        var photo = new byte[photoWidth * photoHeight];
        for (int i = 0; i < photo.Length; i++)
            photo[i] = 148;

        int cardLeft = margin / 2;
        int cardTop = margin / 2;
        for (int y = 0; y < cardHeight; y++)
        {
            int row = (cardTop + y) * photoWidth + cardLeft;
            for (int x = 0; x < cardWidth; x++)
                photo[row + x] = 237;
        }

        int bandTop = cardTop + cardHeight - bandHeight - margin / 2;
        for (int lineIndex = 0; lineIndex < SpecimenLines.Length; lineIndex++)
        {
            string line = SpecimenLines[lineIndex];
            int y0 = bandTop + lineIndex * (cellHeight + lineGap);
            for (int charIndex = 0; charIndex < line.Length; charIndex++)
            {
                int glyph = OcrTemplates.IndexOf(line[charIndex]);
                if (glyph < 0)
                    continue;
                float[] ink = OcrTemplates.Ink[glyph];
                int x0 = cardLeft + margin + charIndex * cellWidth;
                for (int ty = 0; ty < cellHeight; ty++)
                {
                    for (int tx = 0; tx < cellWidth; tx++)
                    {
                        float value = ink[(ty / scale) * OcrTemplates.Width + tx / scale];
                        if (value <= 0.05f)
                            continue;
                        int index = (y0 + ty) * photoWidth + x0 + tx;
                        photo[index] = (byte)Math.Max(0, photo[index] - (int)(value * 215));
                    }
                }
            }
        }

        var image = MrzImage.FromGrayscale8(photo, photoWidth, photoHeight);
        IReadOnlyList<MrzRegion> regions = MrzScanner.Default.LocateMrz(image);
        MrzResult result = MrzScanner.Default.Read(image);
        Console.WriteLine($"regions {regions.Count}, valid {result.IsValid}, doc {result.Document?.DocumentNumber}");

        // Grayscale to color, with the detected region outlined.
        var rgb = new byte[photoWidth * photoHeight * 3];
        for (int i = 0; i < photo.Length; i++)
        {
            rgb[i * 3] = photo[i];
            rgb[i * 3 + 1] = photo[i];
            rgb[i * 3 + 2] = photo[i];
        }
        if (regions.Count > 0)
        {
            MrzRegion r = regions[0];
            for (int t = 0; t < 3; t++)
                DrawRect(rgb, photoWidth, photoHeight, r.Left - t, r.Top - t, r.Width + 2 * t, r.Height + 2 * t);
        }

        WriteBmp24(rgb, photoWidth, photoHeight, outputPath);
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    private static void DrawRect(byte[] rgb, int width, int height, int left, int top, int rectWidth, int rectHeight)
    {
        void Set(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
                return;
            int i = (y * width + x) * 3;
            rgb[i] = 214;
            rgb[i + 1] = 40;
            rgb[i + 2] = 57;
        }
        for (int x = left; x < left + rectWidth; x++)
        {
            Set(x, top);
            Set(x, top + rectHeight - 1);
        }
        for (int y = top; y < top + rectHeight; y++)
        {
            Set(left, y);
            Set(left + rectWidth - 1, y);
        }
    }

    private static void WriteBmp24(byte[] rgb, int width, int height, string path)
    {
        int stride = (width * 3 + 3) & ~3;
        int dataSize = stride * height;
        using var stream = new FileStream(path, FileMode.Create);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(54 + dataSize);
        writer.Write(0);
        writer.Write(54);
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1);
        writer.Write((short)24);
        writer.Write(0);
        writer.Write(dataSize);
        writer.Write(2835);
        writer.Write(2835);
        writer.Write(0);
        writer.Write(0);
        var row = new byte[stride];
        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                row[x * 3] = rgb[i + 2];
                row[x * 3 + 1] = rgb[i + 1];
                row[x * 3 + 2] = rgb[i];
            }
            writer.Write(row);
        }
    }
}
