namespace Dissimilis.MrzScanner.Internal;

/// <summary>
/// Second recognition pass adapted to the specific document: characters read with
/// high confidence become document specific templates (capturing the exact print,
/// blur, and contrast), and weak cells are re-scored against them.
/// </summary>
internal static class AdaptiveRefiner
{
    private const float ConfidentScore = 0.72f;
    private const float WeakScore = 0.90f;
    private const int MinSamples = 1;

    public static void Refine(BandRead band, MrzFormat format)
    {
        int templateLength = OcrTemplates.Width * OcrTemplates.Height;
        var sums = new float[OcrTemplates.Alphabet.Length][];
        var counts = new int[OcrTemplates.Alphabet.Length];

        for (int lineIndex = 0; lineIndex < band.Lines.Count; lineIndex++)
        {
            List<CellRead> line = band.Lines[lineIndex];
            for (int x = 0; x < line.Count; x++)
            {
                CellRead cell = line[x];
                if (cell.ChosenScore < ConfidentScore)
                    continue;

                // Only unambiguous cells may donate their bitmap: a misread
                // whose wrong character narrowly beats the right one would
                // otherwise poison the document template of the wrong
                // character with the right character's shape. Only characters
                // the position's class allows count as rivals; a digit cannot
                // steal a name cell, so it is no threat there.
                CharClass allowed = format.ClassAt(lineIndex, x);
                float runnerUp = float.MinValue;
                for (int k = 0; k < cell.Chars.Length; k++)
                {
                    if (cell.Chars[k] != cell.Chosen &&
                        ChecksumArbitrator.IsAllowed(cell.Chars[k], allowed) &&
                        cell.Scores[k] > runnerUp)
                    {
                        runnerUp = cell.Scores[k];
                    }
                }
                if (cell.ChosenScore - runnerUp < 0.12f)
                    continue;

                int index = OcrTemplates.IndexOf(cell.Chosen);
                if (index < 0)
                    continue;
                sums[index] ??= new float[templateLength];
                float[] sum = sums[index];
                for (int i = 0; i < templateLength; i++)
                    sum[i] += cell.Bitmap[i];
                counts[index]++;
            }
        }

        var documentTemplates = new float[OcrTemplates.Alphabet.Length][];
        bool any = false;
        for (int t = 0; t < sums.Length; t++)
        {
            if (counts[t] < MinSamples)
                continue;
            var template = (float[])sums[t]!.Clone();
            OcrTemplates.Normalize(template);
            documentTemplates[t] = template;
            any = true;
        }
        if (!any)
            return;

        foreach (List<CellRead> line in band.Lines)
        {
            foreach (CellRead cell in line)
            {
                if (cell.ChosenScore >= WeakScore)
                    continue;

                // Blend document evidence into the candidate scores: for each candidate
                // character with a document template, its score becomes the better of
                // the base score and the document correlation.
                for (int k = 0; k < cell.Chars.Length; k++)
                {
                    int index = OcrTemplates.IndexOf(cell.Chars[k]);
                    if (index < 0 || documentTemplates[index] is null)
                        continue;
                    float[] template = documentTemplates[index]!;
                    float dot = MathKernels.Dot(cell.Bitmap, template);
                    if (dot > cell.Scores[k])
                        cell.Scores[k] = dot;
                }

                // Re-rank candidates after the score updates.
                for (int a = 0; a < cell.Chars.Length - 1; a++)
                {
                    for (int b = a + 1; b < cell.Chars.Length; b++)
                    {
                        if (cell.Scores[b] > cell.Scores[a])
                        {
                            (cell.Scores[a], cell.Scores[b]) = (cell.Scores[b], cell.Scores[a]);
                            (cell.Chars[a], cell.Chars[b]) = (cell.Chars[b], cell.Chars[a]);
                        }
                    }
                }
                cell.Chosen = cell.Chars[0];
                cell.ChosenScore = cell.Scores[0];
            }
        }
    }
}
