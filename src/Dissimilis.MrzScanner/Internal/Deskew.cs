namespace Dissimilis.MrzScanner.Internal;

/// <summary>
/// Small angle skew estimation for a located band crop. Handheld photos are
/// commonly tilted a few degrees, which smears the character rows across the
/// horizontal projection the recognizer's grid search depends on. The
/// estimator shears row projections at candidate angles and picks the angle
/// whose profile is sharpest; the pipeline then re-reads a rotated crop.
/// </summary>
internal static class Deskew
{
    public const double MaxAngleDegrees = 8.0;

    /// <summary>Angles closer to level than this are left alone.</summary>
    public const double MinActionableDegrees = 1.0;

    /// <summary>
    /// Estimates the rotation in degrees that levels the band, ready to pass
    /// to <see cref="GrayImage.RotateBy" />. Returns 0 when the crop is
    /// effectively level or too small to judge.
    /// </summary>
    public static double EstimateCorrectionDegrees(GrayImage crop)
    {
        GrayImage working = crop.DownscaleTo(480);
        int width = working.Width;
        int height = working.Height;
        if (width < 40 || height < 8)
            return 0;

        // Vertical gradient magnitude is the alignment signal: text rows and
        // card edges produce strong vertical transitions while flat background
        // contributes nothing, so the profile is not drowned by whatever
        // brightness dominates the frame. Small gradients are sensor noise.
        byte[] pixels = working.Pixels;
        var gradient = new short[pixels.Length];
        for (int y = 0; y < height - 1; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int g = Math.Abs(pixels[row + width + x] - pixels[row + x]);
                gradient[row + x] = g >= 8 ? (short)g : (short)0;
            }
        }

        double bestAngle = 0;
        double bestScore = double.MinValue;
        double levelScore = 0;
        int bins = height + 2 * (int)(width * Math.Tan(MaxAngleDegrees * Math.PI / 180)) + 4;
        var profile = new double[bins];
        double centerX = width / 2.0;

        double binOffset = width * Math.Tan(MaxAngleDegrees * Math.PI / 180);
        for (double angle = -MaxAngleDegrees; angle <= MaxAngleDegrees + 1e-9; angle += 0.5)
        {
            double slope = Math.Tan(angle * Math.PI / 180);
            Array.Clear(profile, 0, profile.Length);
            for (int y = 0; y < height - 1; y++)
            {
                int row = y * width;
                double baseBin = y + binOffset - centerX * slope + 0.5;
                for (int x = 0; x < width; x++)
                {
                    short g = gradient[row + x];
                    if (g == 0)
                        continue;
                    int bin = (int)(baseBin + x * slope);
                    if (bin >= 0 && bin < bins)
                        profile[bin] += g;
                }
            }

            // Sharp profiles concentrate ink into few bins: score by the sum
            // of squares, which rewards peaks over smear at equal total ink.
            double score = 0;
            for (int i = 0; i < bins; i++)
                score += profile[i] * profile[i];
            if (Math.Abs(angle) < 0.25)
                levelScore = score;
            if (score > bestScore)
            {
                bestScore = score;
                bestAngle = angle;
            }
        }

        // Featureless or already level frames score every angle about the
        // same; only a clear win over the level profile justifies a retry.
        // The comparison must not pass on ties: a blank frame scores zero
        // everywhere and the first angle tested would win otherwise.
        if (bestScore <= 0 || bestScore <= levelScore * 1.03)
            return 0;

        // The shear that sharpens the profile cancels the text row slope; the
        // rotation that levels the image is its negation.
        return Math.Abs(bestAngle) < MinActionableDegrees ? 0 : -bestAngle;
    }
}
