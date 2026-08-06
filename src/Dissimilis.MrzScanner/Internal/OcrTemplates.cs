namespace Dissimilis.MrzScanner.Internal;

/// <summary>
/// Templates for the 37 character MRZ alphabet, rasterized from procedural
/// stroke definitions approximating OCR-B. Original work, no font data embedded.
/// Templates are zero mean and unit norm so a dot product with an equally
/// normalized cell is a normalized cross correlation.
/// </summary>
internal static class OcrTemplates
{
    public const int Width = 16;
    public const int Height = 20;
    public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789<";

    /// <summary>
    /// Stroke width variants rasterized per character. Real MRZ prints and
    /// upscaled low resolution crops vary considerably in apparent boldness,
    /// and matching takes the best variant per character.
    /// </summary>
    private static readonly float[] StrokeRadii = { 0.075f, 0.115f };

    /// <summary>
    /// Real MRZ glyphs are about 1.55 mm wide on a 2.54 mm pitch, so a glyph
    /// occupies roughly 61 percent of its cell, but printers vary and some
    /// documents run visibly narrower. The stroke definitions draw at 84
    /// percent width for legibility; these factors compress them to the pitch
    /// proportions seen in practice, and matching tries every variant.
    /// Declared before the variant banks below, which consume it during type
    /// initialization.
    /// </summary>
    private static readonly float[] PitchCompressions = { 0.72f, 0.55f };

    /// <summary>Normalized template variants per alphabet character, in alphabet order.</summary>
    public static readonly float[][][] Variants = BuildVariants();

    /// <summary>Primary normalized template per character (thin stroke variant).</summary>
    public static readonly float[][] Templates = ExtractPrimary();

    public const int CoarseWidth = 8;
    public const int CoarseHeight = 10;

    /// <summary>
    /// Half resolution prefilter templates, one per character: the best coarse
    /// correlation over a character's crisp variants. A cheap coarse pass
    /// ranks the alphabet before the expensive full resolution correlation
    /// runs on the survivors.
    /// </summary>
    public static readonly float[][][] Coarse = BuildCoarse();

    private static float[][][] BuildCoarse()
    {
        var result = new float[Alphabet.Length][][];
        for (int i = 0; i < Alphabet.Length; i++)
        {
            // At half resolution the stroke width variants are nearly
            // indistinguishable; one coarse template per pitch compression
            // (plus the learned shape when present) ranks just as well at a
            // fraction of the cost. Crisp variants sit at even indices in
            // radius major order, so stride 4 walks the distinct compressions.
            var list = new List<float[]>();
            float[][] variants = Variants[i];
            for (int v = 0; v < variants.Length; v += 4)
            {
                float[] coarse = Downsample(variants[v]);
                Normalize(coarse);
                list.Add(coarse);
            }
            result[i] = list.ToArray();
        }
        return result;
    }

    /// <summary>Averages 2x2 blocks of a full resolution template space image.</summary>
    public static float[] Downsample(float[] cell)
    {
        var coarse = new float[CoarseWidth * CoarseHeight];
        for (int y = 0; y < CoarseHeight; y++)
        {
            for (int x = 0; x < CoarseWidth; x++)
            {
                int sy = y * 2;
                int sx = x * 2;
                coarse[y * CoarseWidth + x] =
                    (cell[sy * Width + sx] + cell[sy * Width + sx + 1] +
                     cell[(sy + 1) * Width + sx] + cell[(sy + 1) * Width + sx + 1]) * 0.25f;
            }
        }
        return coarse;
    }

    /// <summary>Raw ink images in [0,1], for rendering synthetic MRZs in tests.</summary>
    public static readonly float[][] Ink = BuildInkAll();

    public static int IndexOf(char c) => Alphabet.IndexOf(c);

    /// <summary>Normalizes a cell to zero mean and unit norm, matching the template space.</summary>
    public static void Normalize(float[] cell)
    {
        double mean = 0;
        for (int i = 0; i < cell.Length; i++)
            mean += cell[i];
        mean /= cell.Length;
        double norm = 0;
        for (int i = 0; i < cell.Length; i++)
        {
            cell[i] = (float)(cell[i] - mean);
            norm += cell[i] * cell[i];
        }
        norm = Math.Sqrt(norm);
        if (norm < 1e-6)
            return;
        for (int i = 0; i < cell.Length; i++)
            cell[i] = (float)(cell[i] / norm);
    }

    private static float[][][] BuildVariants()
    {
        var result = new float[Alphabet.Length][][];
        for (int i = 0; i < Alphabet.Length; i++)
        {
            // Per glyph width and stroke width: a crisp variant and a blurred
            // one that models the point spread of low resolution photos
            // upscaled for matching.
            var variants = new List<float[]>();
            foreach (float compression in PitchCompressions)
            {
                foreach (float radius in StrokeRadii)
                {
                    float[] crisp = Rasterize(Strokes(Alphabet[i]), radius, compression);
                    float[] blurred = Blur3(crisp);
                    Normalize(crisp);
                    Normalize(blurred);
                    variants.Add(crisp);
                    variants.Add(blurred);
                }
            }

            // Empirical template learned from labeled real documents, when one
            // exists. Added as a crisp/blurred pair to keep the even index
            // convention of candidate generation.
            float[]? learned = LearnedTemplates.Ink[i];
            if (learned is not null)
            {
                var crispLearned = (float[])learned.Clone();
                float[] blurredLearned = Blur3(crispLearned);
                Normalize(crispLearned);
                Normalize(blurredLearned);
                variants.Add(crispLearned);
                variants.Add(blurredLearned);
            }
            result[i] = variants.ToArray();
        }
        return result;
    }

    private static float[] Blur3(float[] source)
    {
        var result = new float[source.Length];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                float sum = 0;
                int n = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = y + dy;
                    if (yy < 0 || yy >= Height)
                        continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx;
                        if (xx < 0 || xx >= Width)
                            continue;
                        sum += source[yy * Width + xx];
                        n++;
                    }
                }
                result[y * Width + x] = sum / n;
            }
        }
        return result;
    }

    private static float[][] ExtractPrimary()
    {
        var result = new float[Variants.Length][];
        for (int i = 0; i < Variants.Length; i++)
            result[i] = Variants[i][0];
        return result;
    }

    private static float[][] BuildInkAll()
    {
        var result = new float[Alphabet.Length][];
        for (int i = 0; i < Alphabet.Length; i++)
            result[i] = Rasterize(Strokes(Alphabet[i]), StrokeRadii[0]);
        return result;
    }

    private const int Super = 4;

    private static float CompressX(float x, float compression) => 0.5f + (x - 0.5f) * compression;

    private static float[] Rasterize(float[][] strokes, float strokeRadius)
        => Rasterize(strokes, strokeRadius, PitchCompressions[0]);

    private static float[] Rasterize(float[][] strokes, float strokeRadius, float compression)
    {
        int sw = Width * Super;
        int sh = Height * Super;
        var canvas = new float[sw * sh];
        float radius = strokeRadius * sw;

        foreach (float[] stroke in strokes)
        {
            for (int p = 0; p + 3 < stroke.Length; p += 2)
            {
                float x0 = CompressX(stroke[p], compression) * (sw - 1);
                float y0 = stroke[p + 1] * (sh - 1);
                float x1 = CompressX(stroke[p + 2], compression) * (sw - 1);
                float y1 = stroke[p + 3] * (sh - 1);
                float length = (float)Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                int steps = Math.Max(1, (int)(length * 2));
                for (int s = 0; s <= steps; s++)
                {
                    float t = s / (float)steps;
                    StampDisk(canvas, sw, sh, x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, radius);
                }
            }
        }

        // Box downsample to the template resolution.
        var result = new float[Width * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                float sum = 0;
                for (int sy = 0; sy < Super; sy++)
                {
                    int row = (y * Super + sy) * sw + x * Super;
                    for (int sx = 0; sx < Super; sx++)
                        sum += canvas[row + sx];
                }
                result[y * Width + x] = sum / (Super * Super);
            }
        }
        return result;
    }

    private static void StampDisk(float[] canvas, int sw, int sh, float cx, float cy, float radius)
    {
        int x0 = Math.Max(0, (int)(cx - radius));
        int x1 = Math.Min(sw - 1, (int)(cx + radius) + 1);
        int y0 = Math.Max(0, (int)(cy - radius));
        int y1 = Math.Min(sh - 1, (int)(cy + radius) + 1);
        float r2 = radius * radius;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = x - cx;
                float dy = y - cy;
                if (dx * dx + dy * dy <= r2)
                    canvas[y * sw + x] = 1f;
            }
        }
    }

    private static float[] Arc(float cx, float cy, float rx, float ry, float startDeg, float endDeg)
    {
        int steps = Math.Max(8, (int)(Math.Abs(endDeg - startDeg) / 10));
        var points = new float[(steps + 1) * 2];
        for (int i = 0; i <= steps; i++)
        {
            double a = (startDeg + (endDeg - startDeg) * i / steps) * Math.PI / 180.0;
            points[i * 2] = (float)(cx + rx * Math.Cos(a));
            points[i * 2 + 1] = (float)(cy + ry * Math.Sin(a));
        }
        return points;
    }

    private static float[] Line(params float[] points) => points;

    private static float[][] Strokes(char c) => c switch
    {
        'A' => new[]
        {
            Line(0.50f, 0.04f, 0.10f, 0.96f),
            Line(0.50f, 0.04f, 0.90f, 0.96f),
            Line(0.24f, 0.64f, 0.76f, 0.64f),
        },
        'B' => new[]
        {
            Line(0.14f, 0.04f, 0.14f, 0.96f),
            Line(0.14f, 0.04f, 0.50f, 0.04f),
            Arc(0.50f, 0.26f, 0.32f, 0.22f, -90, 90),
            Line(0.50f, 0.48f, 0.14f, 0.48f),
            Arc(0.52f, 0.72f, 0.34f, 0.24f, -90, 90),
            Line(0.52f, 0.96f, 0.14f, 0.96f),
        },
        'C' => new[] { Arc(0.52f, 0.50f, 0.38f, 0.46f, 40, 320) },
        'D' => new[]
        {
            Line(0.14f, 0.04f, 0.14f, 0.96f),
            Line(0.14f, 0.04f, 0.45f, 0.04f),
            Arc(0.45f, 0.50f, 0.40f, 0.46f, -90, 90),
            Line(0.45f, 0.96f, 0.14f, 0.96f),
        },
        'E' => new[]
        {
            Line(0.14f, 0.04f, 0.14f, 0.96f),
            Line(0.14f, 0.04f, 0.88f, 0.04f),
            Line(0.14f, 0.50f, 0.76f, 0.50f),
            Line(0.14f, 0.96f, 0.88f, 0.96f),
        },
        'F' => new[]
        {
            Line(0.16f, 0.04f, 0.16f, 0.96f),
            Line(0.16f, 0.04f, 0.88f, 0.04f),
            Line(0.16f, 0.50f, 0.74f, 0.50f),
        },
        'G' => new[]
        {
            Arc(0.52f, 0.50f, 0.38f, 0.46f, 40, 320),
            Line(0.55f, 0.58f, 0.90f, 0.58f),
            Line(0.90f, 0.58f, 0.90f, 0.80f),
        },
        'H' => new[]
        {
            Line(0.14f, 0.04f, 0.14f, 0.96f),
            Line(0.86f, 0.04f, 0.86f, 0.96f),
            Line(0.14f, 0.52f, 0.86f, 0.52f),
        },
        'I' => new[]
        {
            Line(0.50f, 0.04f, 0.50f, 0.96f),
            Line(0.30f, 0.04f, 0.70f, 0.04f),
            Line(0.30f, 0.96f, 0.70f, 0.96f),
        },
        'J' => new[]
        {
            Line(0.80f, 0.04f, 0.80f, 0.70f),
            Arc(0.48f, 0.70f, 0.32f, 0.26f, 0, 180),
        },
        'K' => new[]
        {
            Line(0.14f, 0.04f, 0.14f, 0.96f),
            Line(0.86f, 0.04f, 0.18f, 0.56f),
            Line(0.38f, 0.44f, 0.88f, 0.96f),
        },
        'L' => new[]
        {
            Line(0.16f, 0.04f, 0.16f, 0.96f),
            Line(0.16f, 0.96f, 0.88f, 0.96f),
        },
        'M' => new[]
        {
            Line(0.12f, 0.96f, 0.12f, 0.04f),
            Line(0.12f, 0.04f, 0.50f, 0.60f),
            Line(0.50f, 0.60f, 0.88f, 0.04f),
            Line(0.88f, 0.04f, 0.88f, 0.96f),
        },
        'N' => new[]
        {
            Line(0.14f, 0.96f, 0.14f, 0.04f),
            Line(0.14f, 0.04f, 0.86f, 0.96f),
            Line(0.86f, 0.96f, 0.86f, 0.04f),
        },
        'O' => new[] { Arc(0.50f, 0.50f, 0.38f, 0.47f, 0, 360) },
        'P' => new[]
        {
            Line(0.14f, 0.04f, 0.14f, 0.96f),
            Line(0.14f, 0.04f, 0.50f, 0.04f),
            Arc(0.50f, 0.28f, 0.34f, 0.24f, -90, 90),
            Line(0.50f, 0.52f, 0.14f, 0.52f),
        },
        'Q' => new[]
        {
            Arc(0.50f, 0.50f, 0.38f, 0.47f, 0, 360),
            Line(0.60f, 0.72f, 0.90f, 0.98f),
        },
        'R' => new[]
        {
            Line(0.14f, 0.04f, 0.14f, 0.96f),
            Line(0.14f, 0.04f, 0.50f, 0.04f),
            Arc(0.50f, 0.28f, 0.34f, 0.24f, -90, 90),
            Line(0.50f, 0.52f, 0.14f, 0.52f),
            Line(0.40f, 0.52f, 0.88f, 0.96f),
        },
        'S' => new[]
        {
            Arc(0.52f, 0.27f, 0.36f, 0.24f, -20, -190),
            Arc(0.48f, 0.73f, 0.36f, 0.24f, -100, 170),
        },
        'T' => new[]
        {
            Line(0.08f, 0.04f, 0.92f, 0.04f),
            Line(0.50f, 0.04f, 0.50f, 0.96f),
        },
        'U' => new[]
        {
            Line(0.14f, 0.04f, 0.14f, 0.70f),
            Line(0.86f, 0.04f, 0.86f, 0.70f),
            Arc(0.50f, 0.70f, 0.36f, 0.26f, 0, 180),
        },
        'V' => new[]
        {
            Line(0.08f, 0.04f, 0.50f, 0.96f),
            Line(0.92f, 0.04f, 0.50f, 0.96f),
        },
        'W' => new[]
        {
            Line(0.06f, 0.04f, 0.26f, 0.96f),
            Line(0.26f, 0.96f, 0.50f, 0.30f),
            Line(0.50f, 0.30f, 0.74f, 0.96f),
            Line(0.74f, 0.96f, 0.94f, 0.04f),
        },
        'X' => new[]
        {
            Line(0.10f, 0.04f, 0.90f, 0.96f),
            Line(0.90f, 0.04f, 0.10f, 0.96f),
        },
        'Y' => new[]
        {
            Line(0.08f, 0.04f, 0.50f, 0.48f),
            Line(0.92f, 0.04f, 0.50f, 0.48f),
            Line(0.50f, 0.48f, 0.50f, 0.96f),
        },
        'Z' => new[]
        {
            Line(0.12f, 0.04f, 0.88f, 0.04f),
            Line(0.88f, 0.04f, 0.12f, 0.96f),
            Line(0.12f, 0.96f, 0.88f, 0.96f),
        },
        // The OCR-B zero is a slightly narrower oval than the letter O.
        '0' => new[] { Arc(0.50f, 0.50f, 0.34f, 0.47f, 0, 360) },
        '1' => new[]
        {
            Line(0.28f, 0.20f, 0.52f, 0.04f),
            Line(0.52f, 0.04f, 0.52f, 0.96f),
        },
        '2' => new[]
        {
            Arc(0.50f, 0.28f, 0.36f, 0.24f, -160, 20),
            Line(0.84f, 0.36f, 0.14f, 0.96f),
            Line(0.14f, 0.96f, 0.88f, 0.96f),
        },
        '3' => new[]
        {
            Arc(0.48f, 0.27f, 0.35f, 0.23f, -140, 70),
            Arc(0.48f, 0.73f, 0.36f, 0.24f, -70, 140),
        },
        '4' => new[]
        {
            Line(0.68f, 0.04f, 0.10f, 0.68f),
            Line(0.10f, 0.68f, 0.92f, 0.68f),
            Line(0.68f, 0.04f, 0.68f, 0.96f),
        },
        '5' => new[]
        {
            Line(0.82f, 0.04f, 0.18f, 0.04f),
            Line(0.18f, 0.04f, 0.18f, 0.42f),
            Line(0.18f, 0.42f, 0.46f, 0.39f),
            Arc(0.50f, 0.66f, 0.36f, 0.28f, -75, 160),
        },
        '6' => new[]
        {
            Line(0.78f, 0.06f, 0.42f, 0.28f, 0.20f, 0.55f),
            Arc(0.50f, 0.68f, 0.34f, 0.28f, 0, 360),
        },
        '7' => new[]
        {
            Line(0.10f, 0.04f, 0.90f, 0.04f),
            Line(0.90f, 0.04f, 0.34f, 0.96f),
        },
        '8' => new[]
        {
            Arc(0.50f, 0.27f, 0.33f, 0.23f, 0, 360),
            Arc(0.50f, 0.735f, 0.36f, 0.235f, 0, 360),
        },
        '9' => new[]
        {
            Arc(0.50f, 0.32f, 0.34f, 0.28f, 0, 360),
            Line(0.83f, 0.45f, 0.58f, 0.72f, 0.22f, 0.94f),
        },
        '<' => new[]
        {
            Line(0.82f, 0.10f, 0.16f, 0.50f),
            Line(0.16f, 0.50f, 0.82f, 0.90f),
        },
        _ => throw new InvalidOperationException($"No strokes defined for '{c}'."),
    };
}
