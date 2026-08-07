using System.Diagnostics;
using Dissimilis.MrzScanner;

namespace MrzHarness;

/// <summary>
/// End to end timing over a sample directory: median of three reads per
/// image, p50/p90 across images, for both search efforts.
/// </summary>
internal static class TimeRunner
{
    public static int Run(string directory)
    {
        string[] files = Directory.GetFiles(directory)
            .Where(f => f.EndsWith(".jpg") || f.EndsWith(".jpeg") || f.EndsWith(".png"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        foreach (MrzSearchEffort effort in new[] { MrzSearchEffort.Exhaustive, MrzSearchEffort.SingleFrame })
        {
            var reader = new MrzScanner(new MrzScannerOptions { SearchEffort = effort });
            reader.Read(files[0]);
            var medians = new List<double>();
            var sw = new Stopwatch();
            foreach (string file in files)
            {
                var runs = new double[3];
                for (int i = 0; i < runs.Length; i++)
                {
                    sw.Restart();
                    reader.Read(file);
                    sw.Stop();
                    runs[i] = sw.Elapsed.TotalMilliseconds;
                }
                Array.Sort(runs);
                medians.Add(runs[1]);
            }
            medians.Sort();
            double p50 = medians[medians.Count / 2];
            double p90 = medians[(int)(medians.Count * 0.9)];
            Console.WriteLine($"{effort}: files {medians.Count}, p50 {p50:F0} ms, p90 {p90:F0} ms, max {medians[^1]:F0} ms, total {medians.Sum():F0} ms");
        }
        return 0;
    }
}
