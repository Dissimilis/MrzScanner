using System.Text.Json;
using Dissimilis.MrzScanner;
using Dissimilis.MrzScanner.Internal;

namespace MrzHarness;

/// <summary>
/// Brute force sweep of rigid (left, pitch) grids for one labeled specimen,
/// scored against the expected text. Separates "the engine picked the wrong
/// grid" from "no rigid grid can read this crop": if the best swept grid reads
/// nearly everything, grid selection is the problem; if none does, the crop is
/// beyond the rigid grid plus template matcher combination.
/// </summary>
internal static class GridSweepRunner
{
    public static int Run(string path)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var expectations = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
            File.ReadAllText(Path.Combine(directory, "expected.json")))!;
        if (!expectations.TryGetValue(Path.GetFileName(path), out string[]? expected))
        {
            Console.WriteLine("no expectation for file");
            return 1;
        }

        byte[] data = File.ReadAllBytes(path);
        (GrayImage? image, _) = ImageDecoder.Decode(data);
        if (image is null)
        {
            Console.WriteLine("decode failed");
            return 1;
        }

        var options = new MrzScannerOptions();
        GrayImage working = image.DownscaleTo(options.MaxImageDimension);
        List<BandCandidate> candidates = MrzLocator.Locate(working);
        if (candidates.Count == 0)
        {
            Console.WriteLine("no candidates");
            return 1;
        }
        double scaleX = image.Width / (double)working.Width;
        double scaleY = image.Height / (double)working.Height;
        GrayImage crop = MrzPipeline.PrepareCrop(image, candidates[0], scaleX, scaleY);
        Console.WriteLine($"crop {crop.Width}x{crop.Height}");

        int count = expected[0].Length;
        double pitch0 = crop.Width / (double)(count + 4);
        var prepared = BandRecognizer.PrepareBand(crop);
        if (prepared is null)
        {
            Console.WriteLine("prepare failed");
            return 1;
        }
        (GrayImage gray, byte[] ink, List<(int Top, int Bottom)> spans) = prepared.Value;
        Console.WriteLine("spans: " + string.Join(" ", spans.Select(s => $"{s.Top}-{s.Bottom}")));

        string? dump = Environment.GetEnvironmentVariable("MRZ_DUMP_CROP");
        if (dump is not null)
        {
            WriteBmp(gray, Path.Combine(dump, Path.GetFileNameWithoutExtension(path) + "_crop.bmp"));
            var inkImage = new GrayImage(gray.Width, gray.Height);
            for (int i = 0; i < ink.Length; i++)
                inkImage.Pixels[i] = ink[i] != 0 ? (byte)0 : (byte)255;
            WriteBmp(inkImage, Path.Combine(dump, Path.GetFileNameWithoutExtension(path) + "_ink.bmp"));
            Console.WriteLine($"crop dumped to {dump}");
            return 0;
        }
        string? forced = Environment.GetEnvironmentVariable("MRZ_GRID_AT");
        if (forced is not null)
        {
            string[] parts = forced.Split(',');
            double forcedLeft = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            double forcedPitch = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            BandRead? forcedBand = BandRecognizer.BuildAtGrid(gray, ink, spans, count, forcedLeft, forcedPitch);
            if (forcedBand is null)
            {
                Console.WriteLine("build failed");
                return 1;
            }
            Console.WriteLine($"forced left {forcedLeft} pitch {forcedPitch} score {Score(forcedBand, expected)} mean {forcedBand.MeanScore:F3} penalty {forcedBand.GeometryPenalty:F3}");
            for (int i = 0; i < forcedBand.Lines.Count; i++)
            {
                Console.WriteLine($"  exp {expected[i]}");
                Console.WriteLine($"  got {forcedBand.LineText(i)}");
            }
            return 0;
        }

        var best = new List<(int Score, double Left, double Pitch, string[] Lines)>();
        for (double pitch = pitch0 * 0.85; pitch <= pitch0 * 1.15; pitch += Math.Max(0.05, pitch0 * 0.004))
        {
            double maxLeft = crop.Width - pitch * count;
            for (double left = 0; left <= maxLeft; left += 2.0)
            {
                BandRead? band = BandRecognizer.BuildAtGrid(gray, ink, spans, count, left, pitch);
                if (band is null || band.Lines.Count != expected.Length)
                    continue;
                int score = Score(band, expected);
                best.Add((score, left, pitch, band.AllText().ToArray()));
            }
        }
        if (best.Count == 0)
        {
            Console.WriteLine("no grids built");
            return 1;
        }
        best.Sort((a, b) => b.Score.CompareTo(a.Score));
        int totalChars = 0;
        foreach (string line in expected)
        {
            foreach (char c in line)
            {
                if (c != '?')
                    totalChars++;
            }
        }
        Console.WriteLine($"total gradable chars {totalChars}");
        foreach ((int score, double left, double pitch, string[] lines) in best.Take(5))
        {
            Console.WriteLine($"score {score} left {left:F1} pitch {pitch:F2}");
            for (int i = 0; i < lines.Length; i++)
            {
                Console.WriteLine($"  exp {expected[i]}");
                Console.WriteLine($"  got {lines[i]}");
            }
        }
        return 0;
    }

    private static void WriteBmp(GrayImage image, string path)
    {
        int width = image.Width;
        int height = image.Height;
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
                byte v = image.Pixels[y * width + x];
                row[x * 3] = v;
                row[x * 3 + 1] = v;
                row[x * 3 + 2] = v;
            }
            writer.Write(row);
        }
    }

    private static int Score(BandRead band, string[] expected)
    {
        int correct = 0;
        for (int i = 0; i < expected.Length && i < band.Lines.Count; i++)
        {
            string text = band.LineText(i);
            for (int c = 0; c < expected[i].Length && c < text.Length; c++)
            {
                if (expected[i][c] != '?' && text[c] == expected[i][c])
                    correct++;
            }
        }
        return correct;
    }
}
