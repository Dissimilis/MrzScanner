using System.Text.Json;
using Dissimilis.MrzScanner;

namespace MrzHarness;

/// <summary>
/// Verifies the reader against the labeled, user-authorized samples in
/// samples/non-sensitive. These are specimen documents, so printing their
/// content is allowed. Expected lines may contain '?' for positions that are
/// not legible enough in the source image to serve as ground truth.
/// </summary>
internal static class VerifyRunner
{
    public static int Run(string directory)
    {
        string expectedPath = Path.Combine(directory, "expected.json");
        if (!File.Exists(expectedPath))
        {
            Console.WriteLine($"missing {expectedPath}");
            return 1;
        }
        var expectations = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
            File.ReadAllText(expectedPath))!;

        var reader = new MrzScanner(new MrzScannerOptions
        {
            SearchEffort = Environment.GetEnvironmentVariable("MRZ_EFFORT") == "single"
                ? MrzSearchEffort.SingleFrame
                : MrzSearchEffort.Exhaustive,
        });
        int totalChars = 0;
        int totalCorrect = 0;
        int perfectFiles = 0;

        foreach ((string file, string[] expectedLines) in expectations.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string path = Path.Combine(directory, file);
            if (!File.Exists(path))
            {
                Console.WriteLine($"{file}: MISSING");
                continue;
            }

            MrzResult result = reader.Read(path);
            var actualLines = result.Raw?.Lines ?? new List<string>();

            int fileChars = 0;
            int fileCorrect = 0;
            bool lineCountOk = actualLines.Count == expectedLines.Length;
            for (int i = 0; i < expectedLines.Length; i++)
            {
                string expected = expectedLines[i];
                string actual = i < actualLines.Count ? actualLines[i] : string.Empty;
                for (int c = 0; c < expected.Length; c++)
                {
                    if (expected[c] == '?')
                        continue;
                    fileChars++;
                    if (c < actual.Length && actual[c] == expected[c])
                        fileCorrect++;
                }
            }
            totalChars += fileChars;
            totalCorrect += fileCorrect;
            bool perfect = lineCountOk && fileCorrect == fileChars;
            if (perfect)
                perfectFiles++;

            Console.WriteLine($"{file}: {fileCorrect}/{fileChars} {(perfect ? "PERFECT" : "")} " +
                              $"(found {result.MrzFound}, valid {result.IsValid}, conf {result.Confidence:F2})");
            if (Environment.GetEnvironmentVariable("MRZ_CALIBRATION") is not null && fileChars > 0 && result.MrzFound)
                Console.WriteLine($"  calib {result.Confidence:F4} {fileCorrect / (double)fileChars:F4}");
            if (!perfect)
            {
                for (int i = 0; i < expectedLines.Length; i++)
                {
                    Console.WriteLine($"  exp {expectedLines[i]}");
                    Console.WriteLine($"  got {(i < actualLines.Count ? actualLines[i] : "(missing)")}");
                }
            }
        }

        Console.WriteLine($"TOTAL {totalCorrect}/{totalChars} ({100.0 * totalCorrect / Math.Max(1, totalChars):F1} percent), " +
                          $"perfect files {perfectFiles}/{expectations.Count}");
        return 0;
    }
}
