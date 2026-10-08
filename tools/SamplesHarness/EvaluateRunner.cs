using System.Diagnostics;
using System.Text.Json;
using Dissimilis.MrzScanner;

namespace MrzHarness;

/// <summary>Local regression gate. Only aggregate metrics leave the evaluator.</summary>
internal static class EvaluateRunner
{
    internal sealed class Metrics
    {
        public int Images { get; set; }
        public int Missing { get; set; }
        public int Characters { get; set; }
        public int Correct { get; set; }
        public int Exact { get; set; }
        public int WrongValid { get; set; }
        public int FalsePositive { get; set; }
        public int Found { get; set; }
        public int Valid { get; set; }
        public int CompositeConflicts { get; set; }
        public int NameErrors { get; set; }
        public double Milliseconds { get; set; }
        public long AllocatedBytes { get; set; }
    }

    public static int Run(string directory, string? baseline, string? output)
    {
        // Read before writing output, including when both paths name one file.
        Metrics? previous = baseline is null ? null :
            JsonSerializer.Deserialize<Metrics>(File.ReadAllText(baseline)) ??
                throw new InvalidDataException("Invalid baseline metrics.");
        string expectedPath = Path.Combine(directory, "expected.json");
        if (!File.Exists(expectedPath))
        {
            Console.WriteLine("No local expectation manifest available.");
            return 1;
        }
        var expectations = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
            File.ReadAllText(expectedPath))!;
        var reader = new MrzScanner(new MrzScannerOptions
        {
            SearchEffort = Environment.GetEnvironmentVariable("MRZ_EFFORT") == "single"
                ? MrzSearchEffort.SingleFrame : MrzSearchEffort.Exhaustive,
        });
        var metrics = new Metrics();
        // Resolve and validate every manifest entry before opening any document.
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var cases = expectations.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => (Path: Path.GetFullPath(Path.Combine(root, p.Key)), Lines: p.Value)).ToList();
        if (cases.Any(p => !p.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Expectation paths must stay inside the evaluation directory.");
        var watch = Stopwatch.StartNew();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        foreach (var item in cases)
        {
            if (!File.Exists(item.Path)) { metrics.Missing++; continue; }
            MrzResult result = reader.Read(item.Path);
            metrics.Images++;
            if (result.MrzFound) metrics.Found++;
            if (result.IsValid) metrics.Valid++;
            if (result.Checks.Composite == CheckDigitStatus.Invalid &&
                new[] { result.Checks.DocumentNumber, result.Checks.BirthDate, result.Checks.ExpiryDate }
                    .Count(check => check == CheckDigitStatus.Valid) >= 2)
                metrics.CompositeConflicts++;
            if (item.Lines.Length == 0)
            {
                if (result.MrzFound) metrics.FalsePositive++;
                else metrics.Exact++;
                continue;
            }
            var actual = result.Raw?.Lines ?? Array.Empty<string>();
            var format = Dissimilis.MrzScanner.Internal.MrzFormat.Detect(item.Lines);
            var nameField = format?.Field(Dissimilis.MrzScanner.Internal.FieldId.Name);
            bool exact = actual.Count == item.Lines.Length;
            for (int row = 0; row < item.Lines.Length; row++)
            {
                string expected = item.Lines[row];
                string observed = row < actual.Count ? actual[row] : string.Empty;
                exact &= expected.Length == observed.Length;
                for (int col = 0; col < expected.Length; col++)
                {
                    if (expected[col] == '?') continue;
                    metrics.Characters++;
                    if (col < observed.Length && observed[col] == expected[col]) metrics.Correct++;
                    else
                    {
                        exact = false;
                        if (nameField is not null && row == nameField.Line &&
                            col >= nameField.Start && col < nameField.Start + nameField.Length)
                            metrics.NameErrors++;
                    }
                }
            }
            if (exact) metrics.Exact++;
            else if (result.IsValid) metrics.WrongValid++;
        }
        metrics.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        metrics.Milliseconds = watch.Elapsed.TotalMilliseconds;
        string json = JsonSerializer.Serialize(metrics);
        Console.WriteLine(json);
        if (output is not null) File.WriteAllText(output, json);
        if (metrics.Missing > 0 || metrics.Images == 0) return 1;
        if (previous is null) return 0;
        bool passed = metrics.Images == previous.Images && metrics.Characters == previous.Characters &&
            metrics.Correct >= previous.Correct && metrics.Exact >= previous.Exact &&
            metrics.WrongValid <= previous.WrongValid && metrics.FalsePositive <= previous.FalsePositive;
        Console.WriteLine(passed ? "Regression gate: PASS" : "Regression gate: FAIL");
        return passed ? 0 : 2;
    }
}
