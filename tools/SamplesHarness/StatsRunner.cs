using Dissimilis.MrzScanner;

namespace MrzHarness;

/// <summary>
/// Aggregate read statistics over a sample directory: found, fully valid,
/// and confidence distribution. Prints counts only, never document content,
/// per the samples privacy contract.
/// </summary>
internal static class StatsRunner
{
    public static int Run(string directory)
    {
        var files = Directory.GetFiles(directory)
            .Where(f => f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var reader = new MrzScanner();
        int found = 0;
        int valid = 0;
        int withHints = 0;
        double confidenceSum = 0;
        foreach (string file in files)
        {
            MrzResult result = reader.Read(file);
            if (result.MrzFound)
            {
                found++;
                confidenceSum += result.Confidence;
            }
            if (result.IsValid)
                valid++;
            if (result.CaptureHints.Count > 0)
                withHints++;
        }

        Console.WriteLine($"files {files.Count}, found {found}, valid {valid}, " +
                          $"mean confidence {(found > 0 ? confidenceSum / found : 0):F3}, with hints {withHints}");
        return 0;
    }
}
