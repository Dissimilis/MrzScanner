using System.Globalization;
using System.Text;
using System.Text.Json;
using Dissimilis.MrzScanner;
using Dissimilis.MrzScanner.Internal;

namespace MrzHarness;

/// <summary>
/// Learns empirical glyph templates from the labeled non-sensitive specimen
/// set: for every file whose recognized grid aligns with the expected lines,
/// cell bitmaps at labeled positions are averaged per character and written
/// into LearnedTemplates.cs, joining the procedural template bank.
/// </summary>
internal static class LearnRunner
{
    public static int Run(string directory, string outputPath)
    {
        var expectations = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
            File.ReadAllText(Path.Combine(directory, "expected.json")))!;

        int length = OcrTemplates.Width * OcrTemplates.Height;
        var sums = new double[OcrTemplates.Alphabet.Length][];
        var counts = new int[OcrTemplates.Alphabet.Length];

        foreach ((string file, string[] expectedLines) in expectations)
        {
            string path = Path.Combine(directory, file);
            if (!File.Exists(path))
                continue;
            (GrayImage? image, _) = ImageDecoder.Decode(File.ReadAllBytes(path));
            if (image is null)
                continue;

            var options = new MrzScannerOptions();
            GrayImage working = image.DownscaleTo(options.MaxImageDimension);
            double scaleX = image.Width / (double)working.Width;
            double scaleY = image.Height / (double)working.Height;

            foreach (BandCandidate candidate in MrzLocator.Locate(working))
            {
                GrayImage crop = MrzPipeline.PrepareCrop(image, candidate, scaleX, scaleY);
                foreach (BandRead band in BandRecognizer.Recognize(crop, CancellationToken.None))
                {
                    if (band.Lines.Count != expectedLines.Length)
                        continue;

                    // Trust the alignment only when the raw read already agrees
                    // with the label on a solid majority of cells.
                    int match = 0;
                    int total = 0;
                    for (int i = 0; i < band.Lines.Count; i++)
                    {
                        string expected = expectedLines[i];
                        if (band.Lines[i].Count != expected.Length)
                        {
                            total = -1;
                            break;
                        }
                        for (int x = 0; x < expected.Length; x++)
                        {
                            if (expected[x] == '?')
                                continue;
                            total++;
                            if (band.Lines[i][x].Chosen == expected[x])
                                match++;
                        }
                    }
                    if (total <= 0 || match * 10 < total * 7)
                        continue;

                    for (int i = 0; i < band.Lines.Count; i++)
                    {
                        string expected = expectedLines[i];
                        for (int x = 0; x < expected.Length; x++)
                        {
                            char c = expected[x];
                            if (c == '?')
                                continue;

                            // Only cells the raw read already got right donate:
                            // a misread cell usually means a misaligned window,
                            // and its bitmap would poison the template of the
                            // labeled character with a neighbor's shape.
                            if (band.Lines[i][x].Chosen != c)
                                continue;
                            int index = OcrTemplates.IndexOf(c);
                            if (index < 0)
                                continue;
                            sums[index] ??= new double[length];
                            float[] bitmap = band.Lines[i][x].Bitmap;
                            for (int p = 0; p < length; p++)
                                sums[index][p] += bitmap[p];
                            counts[index]++;
                        }
                    }
                    Console.WriteLine($"{file}: contributed ({match}/{total} raw agreement)");
                }
            }
        }

        var source = new StringBuilder();
        source.AppendLine("namespace Dissimilis.MrzScanner.Internal;");
        source.AppendLine();
        source.AppendLine("/// <summary>");
        source.AppendLine("/// Empirical glyph templates learned from labeled specimen documents by the");
        source.AppendLine("/// samples harness (--learn). Entries follow <see cref=\"OcrTemplates.Alphabet\" />");
        source.AppendLine("/// order; null means no learned template for that character yet. Regenerate");
        source.AppendLine("/// with: dotnet run --project tools/SamplesHarness -- --learn samples/non-sensitive");
        source.AppendLine("/// </summary>");
        source.AppendLine("internal static class LearnedTemplates");
        source.AppendLine("{");
        source.AppendLine("    public static readonly float[]?[] Ink =");
        source.AppendLine("    {");
        for (int i = 0; i < OcrTemplates.Alphabet.Length; i++)
        {
            if (counts[i] < 2)
            {
                source.AppendLine($"        null, // {OcrTemplates.Alphabet[i]}");
                continue;
            }
            var values = new StringBuilder();
            for (int p = 0; p < length; p++)
            {
                float value = (float)(sums[i][p] / counts[i]);
                values.Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append("f,");
            }
            source.AppendLine($"        // {OcrTemplates.Alphabet[i]} ({counts[i]} samples)");
            source.AppendLine($"        new float[] {{ {values} }},");
        }
        source.AppendLine("    };");
        source.AppendLine("}");
        File.WriteAllText(outputPath, source.ToString());

        int learned = counts.Count(c => c >= 2);
        Console.WriteLine($"learned {learned} of {OcrTemplates.Alphabet.Length} characters -> {outputPath}");
        return 0;
    }
}
