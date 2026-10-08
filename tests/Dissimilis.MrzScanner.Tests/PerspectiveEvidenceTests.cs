using Xunit;

namespace Dissimilis.MrzScanner.Tests;

public class PerspectiveEvidenceTests
{
    [Theory]
    [InlineData(-0.06)]
    [InlineData(0.06)]
    public void Existing_per_line_grids_handle_mild_trapezoidal_scale(double taper)
    {
        var (pixels, width, height) = SyntheticMrz.Render(RecognitionEvidenceTests.Passport);
        var warped = new byte[pixels.Length];
        double center = (width - 1) / 2.0;
        for (int y = 0; y < height; y++)
        {
            double scale = 1 + taper * (2.0 * y / (height - 1) - 1);
            for (int x = 0; x < width; x++)
            {
                double sx = center + (x - center) / scale;
                int left = (int)Math.Floor(sx);
                if (left < 0 || left + 1 >= width)
                    warped[y * width + x] = 140;
                else
                {
                    double fraction = sx - left;
                    warped[y * width + x] = (byte)(pixels[y * width + left] * (1 - fraction) +
                        pixels[y * width + left + 1] * fraction);
                }
            }
        }
        MrzResult result = MrzScanner.Default.Read(MrzImage.FromGrayscale8(warped, width, height));
        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal(RecognitionEvidenceTests.Passport, result.Raw!.Lines);
    }
}
