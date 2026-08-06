using Dissimilis.MrzScanner;
using Dissimilis.MrzScanner.Internal;

namespace MrzHarness;

/// <summary>
/// Stage by stage diagnostics for a single explicitly authorized sample file.
/// </summary>
internal static class SampleProbe
{
    public static void Run(string path)
    {
        BandRecognizer.Trace = message => Console.WriteLine($"   trace {message}");
        MrzLocator.Trace = message => Console.WriteLine($"   locator {message}");
        byte[] data = File.ReadAllBytes(path);
        (GrayImage? image, ImageDecoder.Status status) = ImageDecoder.Decode(data);
        if (image is null)
        {
            Console.WriteLine($"decode failed: {status}");
            return;
        }
        Console.WriteLine($"image: {image.Width}x{image.Height}");

        var options = new MrzScannerOptions();
        GrayImage working = image.DownscaleTo(options.MaxImageDimension);
        Console.WriteLine($"working: {working.Width}x{working.Height}");

        List<BandCandidate> candidates = MrzLocator.Locate(working);
        Console.WriteLine($"candidates: {candidates.Count}");
        foreach (BandCandidate c in candidates)
        {
            Console.WriteLine($"  band {c.Left},{c.Top} {c.Width}x{c.Height} lines~{c.LineCountEstimate} score {c.Score:F3}");
        }

        double scaleX = image.Width / (double)working.Width;
        double scaleY = image.Height / (double)working.Height;
        foreach (BandCandidate c in candidates)
        {
            GrayImage crop = MrzPipeline.PrepareCrop(image, c, scaleX, scaleY);
            Console.WriteLine($"-- candidate crop {crop.Width}x{crop.Height}");
            foreach (BandRead band in BandRecognizer.RecognizeAllHypotheses(crop, CancellationToken.None))
            {
                Console.WriteLine($"   raw meanScore {band.MeanScore:F3} penalty {band.GeometryPenalty:F3} hypothesis {band.HypothesisScore:F3}");
                for (int i = 0; i < band.Lines.Count; i++)
                    Console.WriteLine($"   raw  {band.LineText(i)}");
                MrzFormat? format = MrzFormat.Detect(band.AllText());
                Console.WriteLine($"   format: {format?.Type.ToString() ?? "none"}");
                if (format is not null)
                {
                    ChecksumArbitrator.Arbitrate(band, format);
                    AdaptiveRefiner.Refine(band, format);
                    ChecksumArbitrator.Arbitrate(band, format);
                    for (int i = 0; i < band.Lines.Count; i++)
                        Console.WriteLine($"   arb  {band.LineText(i)}");

                    // Bitmap dump of the first cells of the last line (name line
                    // on TD1) to inspect alignment and shape fidelity.
                    List<CellRead> nameLine = band.Lines[0];
                    string expected = Environment.GetEnvironmentVariable("MRZ_PROBE_EXPECT") ?? "";
                    if (nameLine.Count.ToString() == Environment.GetEnvironmentVariable("MRZ_PROBE_COUNT"))
                    {
                        for (int c2 = 0; c2 < Math.Min(expected.Length, nameLine.Count); c2++)
                        {
                            CellRead cell = nameLine[c2];
                            Console.WriteLine($"   cell {c2}: expect {expected[c2]}={cell.ScoreAgainst(expected[c2]):F2} tci={BandRecognizer.TopCenterInk(cell.Bitmap):F3}  top " +
                                string.Join(" ", cell.Chars.Take(4).Zip(cell.Scores.Take(4), (ch, s) => $"{ch}={s:F2}")));
                            if (c2 is 1 or 2 or 5)
                                DumpBitmap(cell.Bitmap);
                        }
                    }
                }
            }
        }

        Console.WriteLine("-- full pipeline result");
        MrzResult result = new MrzScanner().Read(data);
        Console.WriteLine($"found {result.MrzFound}  valid {result.IsValid}  confidence {result.Confidence:F3}");
        if (result.Raw is not null)
        {
            foreach (string line in result.Raw.Lines)
                Console.WriteLine($"   out  {line}");
        }
        foreach (MrzIssue issue in result.Issues)
            Console.WriteLine($"   issue {issue}");
    }

    private static void DumpBitmap(float[] bitmap)
    {
        // The bitmap is zero mean unit norm; show positive values as ink.
        for (int y = 0; y < OcrTemplates.Height; y++)
        {
            var row = new char[OcrTemplates.Width];
            for (int x = 0; x < OcrTemplates.Width; x++)
            {
                float v = bitmap[y * OcrTemplates.Width + x];
                row[x] = v > 0.04f ? '#' : v > 0.015f ? '+' : v > 0 ? '.' : ' ';
            }
            Console.WriteLine("      " + new string(row));
        }
    }

    private static GrayImage Crop(GrayImage source, int left, int top, int width, int height)
    {
        left = Math.Max(0, left);
        top = Math.Max(0, top);
        width = Math.Min(width, source.Width - left);
        height = Math.Min(height, source.Height - top);
        var result = new GrayImage(width, height);
        for (int y = 0; y < height; y++)
            Array.Copy(source.Pixels, (top + y) * source.Width + left, result.Pixels, y * width, width);
        return result;
    }
}
