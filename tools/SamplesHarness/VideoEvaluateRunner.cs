using System.Text.Json;
using Dissimilis.MrzScanner;
using Dissimilis.MrzScanner.Internal;

namespace MrzHarness;

/// <summary>Local A/B of hard votes and alternative evidence on perturbed stills.</summary>
internal static class VideoEvaluateRunner
{
    public static int Run(string directory)
    {
        var labels = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
            File.ReadAllText(Path.Combine(directory, "expected.json")))!;
        int sequences = 0, hardExact = 0, softExact = 0, hardWrongValid = 0, softWrongValid = 0;
        int softRegressions = 0, softGains = 0;
        int hardWrongStable = 0, softWrongStable = 0;
        var reader = new MrzScanner(new MrzScannerOptions { SearchEffort = MrzSearchEffort.SingleFrame }, true);
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        foreach (var label in labels.OrderBy(p => p.Key, StringComparer.Ordinal).Where(p => p.Value.Length > 0).Take(20))
        {
            string path = Path.GetFullPath(Path.Combine(root, label.Key));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid local expectation path.");
            var (gray, _) = ImageDecoder.Decode(File.ReadAllBytes(path));
            if (gray is null) continue;
            gray = gray.DownscaleTo(1600);
            var hard = new MrzVideoSession();
            var soft = new MrzVideoSession();
            for (int frame = 0; frame < 4; frame++)
            {
                byte[] pixels = (byte[])gray.Pixels.Clone();
                var random = new Random(104729 + frame);
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = (byte)Math.Clamp(pixels[i] + random.Next(-18, 19), 0, 255);
                MrzResult result = reader.Read(MrzImage.FromGrayscale8(pixels, gray.Width, gray.Height));
                soft.Fold(result);
                hard.Fold(new MrzResult(result.MrzFound, result.Document, result.Raw, result.Checks,
                    result.Issues, result.Confidence, result.Region, result.FieldConfidence, result.CaptureHints));
                if (hard.IsStable && !Exact(hard.Best, label.Value)) hardWrongStable++;
                if (soft.IsStable && !Exact(soft.Best, label.Value)) softWrongStable++;
            }
            sequences++;
            bool h = Exact(hard.Best, label.Value), s = Exact(soft.Best, label.Value);
            if (h) hardExact++;
            if (s) softExact++;
            if (!h && hard.Best?.IsValid == true) hardWrongValid++;
            if (!s && soft.Best?.IsValid == true) softWrongValid++;
            if (h && !s) softRegressions++;
            if (s && !h) softGains++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            sequences, hardExact, softExact, hardWrongValid, softWrongValid, softRegressions, softGains,
            hardWrongStable, softWrongStable,
        }));
        return sequences > 0 && softRegressions == 0 && softWrongValid <= hardWrongValid &&
            softWrongStable <= hardWrongStable ? 0 : 2;
    }

    private static bool Exact(MrzResult? result, string[] expected)
    {
        if (result?.Raw is null || result.Raw.Lines.Count != expected.Length) return false;
        for (int row = 0; row < expected.Length; row++)
        {
            string actual = result.Raw.Lines[row];
            if (actual.Length != expected[row].Length) return false;
            for (int col = 0; col < actual.Length; col++)
                if (expected[row][col] != '?' && actual[col] != expected[row][col]) return false;
        }
        return true;
    }
}
