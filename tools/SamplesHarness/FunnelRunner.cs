using Dissimilis.MrzScanner;
using Dissimilis.MrzScanner.Internal;

namespace MrzHarness;

/// <summary>
/// Aggregate funnel diagnostics over a sample directory: counts where images
/// drop out of the pipeline (no locator candidates, no line segmentation, low
/// recognition score, no format) without printing any document content.
/// Console output is aggregate statistics only, per the samples privacy
/// contract.
/// </summary>
internal static class FunnelRunner
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

        int decodeFailed = 0;
        int noCandidates = 0;
        int noLineSpans = 0;
        int badSpanCount = 0;
        int lowScore = 0;
        int noFormat = 0;
        int recognized = 0;
        var spanCounts = new Dictionary<int, int>();

        foreach (string file in files)
        {
            (GrayImage? image, _) = ImageDecoder.Decode(File.ReadAllBytes(file));
            if (image is null)
            {
                decodeFailed++;
                continue;
            }
            var options = new MrzScannerOptions();
            GrayImage working = image.DownscaleTo(options.MaxImageDimension);
            double scaleX = image.Width / (double)working.Width;
            double scaleY = image.Height / (double)working.Height;

            // Track the best stage reached across candidates of both whole
            // image orientations, mirroring the pipeline's search space.
            int stage = 0; // 1 candidates, 2 spans, 3 span count ok, 4 score ok, 5 format ok
            foreach (GrayImage orientedImage in new[] { working, working.Rotate90() })
            {
                List<BandCandidate> candidates = MrzLocator.Locate(orientedImage);
                if (candidates.Count == 0)
                    continue;
                stage = Math.Max(stage, 1);
                GrayImage source = ReferenceEquals(orientedImage, working) ? image : image.Rotate90();
                double sx = ReferenceEquals(orientedImage, working) ? scaleX : scaleY;
                double sy = ReferenceEquals(orientedImage, working) ? scaleY : scaleX;
                foreach (BandCandidate candidate in candidates)
                {
                    GrayImage crop = MrzPipeline.PrepareCrop(source, candidate, sx, sy);
                    for (int orientation = 0; orientation < 2; orientation++)
                    {
                        GrayImage oriented = orientation == 0 ? crop : crop.Rotate180();
                        var prepared = BandRecognizer.PrepareBand(oriented);
                        if (prepared is null)
                            continue;
                        int spans = prepared.Value.Spans.Count;
                        if (spans == 0)
                            continue;
                        stage = Math.Max(stage, 2);
                        spanCounts[spans] = spanCounts.TryGetValue(spans, out int n) ? n + 1 : 1;
                        if (spans is not (2 or 3))
                            continue;
                        stage = Math.Max(stage, 3);
                        foreach (BandRead band in BandRecognizer.Recognize(oriented, CancellationToken.None))
                        {
                            if (band.MeanScore < 0.30)
                                continue;
                            stage = Math.Max(stage, 4);
                            if (MrzFormat.Detect(band.AllText()) is not null)
                                stage = Math.Max(stage, 5);
                        }
                    }
                }
            }

            switch (stage)
            {
                case 0: noCandidates++; break;
                case 1: noLineSpans++; break;
                case 2: badSpanCount++; break;
                case 3: lowScore++; break;
                case 4: noFormat++; break;
                default: recognized++; break;
            }
        }

        Console.WriteLine($"images:                     {files.Count}");
        Console.WriteLine($"decode failed:              {decodeFailed}");
        Console.WriteLine($"no locator candidates:      {noCandidates}");
        Console.WriteLine($"candidates, no line spans:  {noLineSpans}");
        Console.WriteLine($"spans, never 2 or 3:        {badSpanCount}");
        Console.WriteLine($"lines, low match score:     {lowScore}");
        Console.WriteLine($"score ok, no format:        {noFormat}");
        Console.WriteLine($"format detected:            {recognized}");
        foreach (var pair in spanCounts.OrderBy(p => p.Key))
            Console.WriteLine($"crops with {pair.Key} spans:          {pair.Value}");
        return 0;
    }
}
