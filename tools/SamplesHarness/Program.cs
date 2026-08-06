using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dissimilis.MrzScanner;

// Stage timing diagnostics are compiled into the library but gated off by
// default; the harness always wants them.
Environment.SetEnvironmentVariable("MRZ_DIAGNOSTICS", "1");

// Local test harness for the samples directory.
//
// PRIVACY CONTRACT: the console output contains aggregate statistics only.
// It must never print recognized field values, file names paired with results,
// or any document content. Detailed per file results go to samples/results.json,
// which is inside the gitignored samples directory and stays on this machine.

// Probe mode: detailed single file diagnostics. Only for files the user has
// explicitly authorized for inspection; prints recognized content.
if (args.Length >= 2 && args[0] == "--probe")
{
    MrzHarness.SampleProbe.Run(args[1]);
    return 0;
}

// Verification against the labeled non-sensitive specimen set.
if (args.Length >= 2 && args[0] == "--verify")
    return MrzHarness.VerifyRunner.Run(args[1]);

// Rigid grid sweep diagnostics for a labeled non-sensitive specimen.
if (args.Length >= 2 && args[0] == "--gridsweep")
    return MrzHarness.GridSweepRunner.Run(args[1]);

// Render the README specimen image (synthetic ICAO example data only).
if (args.Length >= 2 && args[0] == "--render")
    return MrzHarness.RenderRunner.Run(args[1]);

// Aggregate pipeline funnel statistics; prints no document content.
if (args.Length >= 2 && args[0] == "--funnel")
    return MrzHarness.FunnelRunner.Run(args[1]);

// Learn empirical glyph templates from the labeled specimen set.
if (args.Length >= 2 && args[0] == "--learn")
{
    return MrzHarness.LearnRunner.Run(
        args[1],
        Path.Combine(Environment.CurrentDirectory, "src", "Dissimilis.MrzScanner", "Internal", "LearnedTemplates.cs"));
}

string samplesDir = args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, "samples");
if (!Directory.Exists(samplesDir))
{
    Console.WriteLine($"Samples directory not found: {samplesDir}");
    return 1;
}

var files = Directory.GetFiles(samplesDir)
    .Where(f => f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
    .OrderBy(f => f, StringComparer.Ordinal)
    .ToList();

var reader = new MrzScanner(new MrzScannerOptions
{
    SearchEffort = Environment.GetEnvironmentVariable("MRZ_EFFORT") == "single"
        ? MrzSearchEffort.SingleFrame
        : MrzSearchEffort.Exhaustive,
});
var stopwatch = Stopwatch.StartNew();

int decodeFailures = 0;
int notFound = 0;
int found = 0;
int fullyValid = 0;
int cleanChecks = 0;
var formatCounts = new Dictionary<string, int>();
var checkStats = new Dictionary<string, int[]>
{
    ["DocumentNumber"] = new int[3],
    ["BirthDate"] = new int[3],
    ["ExpiryDate"] = new int[3],
    ["OptionalData"] = new int[3],
    ["Composite"] = new int[3],
};
var confidences = new List<double>();
var perFileMs = new List<double>();
var details = new List<object>();
var issueCategories = new Dictionary<string, int>();

foreach (string file in files)
{
    var fileWatch = Stopwatch.StartNew();
    MrzResult result;
    try
    {
        result = reader.Read(file);
    }
    catch (Exception ex)
    {
        decodeFailures++;
        details.Add(new { File = Path.GetFileName(file), Error = ex.Message });
        continue;
    }
    fileWatch.Stop();
    perFileMs.Add(fileWatch.Elapsed.TotalMilliseconds);

    if (!result.MrzFound)
    {
        notFound++;
        details.Add(new
        {
            File = Path.GetFileName(file),
            MrzFound = false,
            Issues = result.Issues.Select(i => i.ToString()).ToList(),
        });
        continue;
    }

    found++;
    if (result.IsValid)
        fullyValid++;
    string format = result.Document?.Type.ToString() ?? "None";
    formatCounts[format] = formatCounts.TryGetValue(format, out int c) ? c + 1 : 1;
    Tally(checkStats["DocumentNumber"], result.Checks.DocumentNumber);
    Tally(checkStats["BirthDate"], result.Checks.BirthDate);
    Tally(checkStats["ExpiryDate"], result.Checks.ExpiryDate);
    Tally(checkStats["OptionalData"], result.Checks.OptionalData);
    Tally(checkStats["Composite"], result.Checks.Composite);
    confidences.Add(result.Confidence);

    // Aggregate issue categories (kind and field only, never values).
    // Issues on documents whose check digits are all valid are the interesting
    // ones: they show what blocks IsValid on otherwise clean reads.
    bool allChecksValid = result.Checks.AllValid;
    if (allChecksValid)
        cleanChecks++;
    foreach (MrzIssue issue in result.Issues)
    {
        string firstWord = issue.Message.Split(' ')[0];
        string category = $"{(allChecksValid ? "clean" : "dirty")} {issue.Kind}:{issue.Field}:{firstWord}";
        issueCategories[category] = issueCategories.TryGetValue(category, out int n) ? n + 1 : 1;
    }

    details.Add(new
    {
        File = Path.GetFileName(file),
        MrzFound = true,
        result.IsValid,
        Format = format,
        result.Confidence,
        Raw = result.Raw?.Lines,
        Document = result.Document is null ? null : new
        {
            result.Document.DocumentNumber,
            result.Document.PrimaryIdentifier,
            result.Document.SecondaryIdentifier,
            result.Document.IssuingCountry,
            result.Document.Nationality,
            BirthDate = result.Document.BirthDate.ToString(),
            ExpiryDate = result.Document.ExpiryDate.ToString(),
            Sex = result.Document.Sex.ToString(),
        },
        Checks = new
        {
            DocumentNumber = result.Checks.DocumentNumber.ToString(),
            BirthDate = result.Checks.BirthDate.ToString(),
            ExpiryDate = result.Checks.ExpiryDate.ToString(),
            OptionalData = result.Checks.OptionalData.ToString(),
            Composite = result.Checks.Composite.ToString(),
        },
        Issues = result.Issues.Select(i => i.ToString()).ToList(),
    });
}

stopwatch.Stop();

string resultsPath = Path.Combine(samplesDir, "results.json");
File.WriteAllText(resultsPath, JsonSerializer.Serialize(details, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
}));

// Aggregate console report. No content, no per file pairing.
var report = new StringBuilder();
report.AppendLine($"images:          {files.Count}");
report.AppendLine($"decode failures: {decodeFailures}");
report.AppendLine($"mrz found:       {found}");
report.AppendLine($"mrz not found:   {notFound}");
report.AppendLine($"fully valid:     {fullyValid}");
report.AppendLine($"all checks ok:   {cleanChecks}");
foreach (var pair in formatCounts.OrderByDescending(p => p.Value))
    report.AppendLine($"format {pair.Key}:      {pair.Value}");
foreach (var pair in checkStats)
{
    int[] s = pair.Value;
    report.AppendLine($"check {pair.Key,-14} valid {s[0],4}  invalid {s[1],4}  absent {s[2],4}");
}
foreach (var pair in issueCategories.Where(p => p.Key.StartsWith("clean")).OrderByDescending(p => p.Value))
    report.AppendLine($"issue {pair.Key}: {pair.Value}");
foreach (var pair in issueCategories.Where(p => p.Key.StartsWith("dirty")).OrderByDescending(p => p.Value).Take(8))
    report.AppendLine($"issue {pair.Key}: {pair.Value}");
if (confidences.Count > 0)
{
    confidences.Sort();
    report.AppendLine($"confidence mean: {confidences.Average():F3}");
    report.AppendLine($"confidence p25:  {Percentile(confidences, 0.25):F3}");
    report.AppendLine($"confidence p50:  {Percentile(confidences, 0.50):F3}");
}
if (perFileMs.Count > 0)
{
    perFileMs.Sort();
    report.AppendLine($"ms per image p50: {Percentile(perFileMs, 0.50):F0}");
    report.AppendLine($"ms per image p90: {Percentile(perFileMs, 0.90):F0}");
}
report.AppendLine($"total seconds:    {stopwatch.Elapsed.TotalSeconds:F1}");
double tickMs = 1000.0 / Stopwatch.Frequency;
report.AppendLine($"locate seconds:    {Dissimilis.MrzScanner.Internal.MrzPipeline.LocateTicks * tickMs / 1000:F1}");
report.AppendLine($"recognize seconds: {Dissimilis.MrzScanner.Internal.MrzPipeline.RecognizeTicks * tickMs / 1000:F1}");
report.AppendLine($"arbitrate seconds: {Dissimilis.MrzScanner.Internal.MrzPipeline.ArbitrateTicks * tickMs / 1000:F1}");
report.AppendLine($"match seconds:     {Dissimilis.MrzScanner.Internal.BandRecognizer.MatchTicks * tickMs / 1000:F1}");
report.AppendLine($"prepare seconds:   {Dissimilis.MrzScanner.Internal.BandRecognizer.PrepareTicks * tickMs / 1000:F1}");
report.AppendLine($"unique matches:    {Dissimilis.MrzScanner.Internal.BandRecognizer.UniqueMatches}");
report.AppendLine($"cached matches:    {Dissimilis.MrzScanner.Internal.BandRecognizer.CachedMatches}");
report.AppendLine($"details written to samples/results.json (local only)");
Console.Write(report.ToString());
return 0;

// Nearest-rank percentile on a sorted list.
static double Percentile(List<double> sorted, double p)
{
    int rank = (int)Math.Ceiling(p * sorted.Count);
    return sorted[Math.Max(0, Math.Min(sorted.Count - 1, rank - 1))];
}

static void Tally(int[] slot, CheckDigitStatus status)
{
    switch (status)
    {
        case CheckDigitStatus.Valid:
            slot[0]++;
            break;
        case CheckDigitStatus.Invalid:
            slot[1]++;
            break;
        default:
            slot[2]++;
            break;
    }
}
