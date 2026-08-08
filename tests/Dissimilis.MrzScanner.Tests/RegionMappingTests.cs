using Xunit;

namespace Dissimilis.MrzScanner.Tests;

/// <summary>
/// The tilt rescue reads a rotated copy of the frame; the region it reports
/// must still land on the band in the coordinates of the supplied image.
/// </summary>
public class RegionMappingTests
{
    private static readonly string[] Td3Lines =
    {
        "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<",
        "L898902C36UTO7408122F1204159ZE184226B<<<<<10",
    };

    [Theory]
    [InlineData(5.0)]
    [InlineData(-5.0)]
    public void Rescued_region_lands_on_the_band(double degrees)
    {
        (byte[] gray, int width, int height) = SyntheticMrz.Render(Td3Lines);

        // Find the band's true row range in the level image: rows containing
        // dark ink. The synthetic card puts the band well inside the photo.
        (int bandTop, int bandBottom) = InkRows(gray, width, height);

        byte[] tilted = Rotate(gray, width, height, degrees);
        MrzResult result = MrzScanner.Default.Read(MrzImage.FromGrayscale8(tilted, width, height));

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        MrzRegion region = result.Region!;

        Assert.True(region.Width > 0 && region.Height > 0,
            $"degenerate region {region.Width}x{region.Height}");

        // The tilted band's ink moves by at most width/2 * tan(5deg) plus the
        // band's own height; a correctly mapped region overlaps that range.
        // A sign error doubles the rotation and displaces the rect far more.
        int slack = (int)(width / 2.0 * Math.Tan(Math.Abs(degrees) * Math.PI / 180)) + (bandBottom - bandTop);
        int regionCenterY = region.Top + region.Height / 2;
        int bandCenterY = (bandTop + bandBottom) / 2;
        Assert.True(Math.Abs(regionCenterY - bandCenterY) <= slack,
            $"region center {regionCenterY} vs band center {bandCenterY}, slack {slack}, " +
            $"region {region.Left},{region.Top} {region.Width}x{region.Height}");
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(-5.0)]
    public void Rescued_region_lands_on_an_off_center_band(double degrees)
    {
        // A horizontally centered band cannot tell a correct mapping from a
        // sign-inverted one (the displacement scales with the band's offset
        // from the image center), so this variant pushes the card far left.
        (byte[] card, int cardWidth, int cardHeight) = SyntheticMrz.Render(Td3Lines);
        int canvasWidth = cardWidth + 900;
        int canvasHeight = cardHeight + 400;
        var canvas = new byte[canvasWidth * canvasHeight];
        for (int i = 0; i < canvas.Length; i++)
            canvas[i] = 140;
        for (int y = 0; y < cardHeight; y++)
            Array.Copy(card, y * cardWidth, canvas, (y + 200) * canvasWidth, cardWidth);

        byte[] tilted = Rotate(canvas, canvasWidth, canvasHeight, degrees);
        (int inkTop, int inkBottom, int inkLeft, int inkRight) = InkBox(tilted, canvasWidth, canvasHeight);

        MrzResult result = MrzScanner.Default.Read(MrzImage.FromGrayscale8(tilted, canvasWidth, canvasHeight));

        Assert.True(result.IsValid, $"tilt {degrees}: " + string.Join("; ", result.Issues));
        MrzRegion region = result.Region!;
        int regionCenterY = region.Top + region.Height / 2;
        int inkCenterY = (inkTop + inkBottom) / 2;
        int regionCenterX = region.Left + region.Width / 2;
        int inkCenterX = (inkLeft + inkRight) / 2;

        // A sign-inverted mapping displaces the rect by about twice the
        // band's center offset times sin(tilt), roughly 75 px here; a correct
        // mapping lands within a few pixels plus AABB growth.
        Assert.True(Math.Abs(regionCenterY - inkCenterY) <= 50,
            $"vertical: region {regionCenterY} ink {inkCenterY}");
        Assert.True(Math.Abs(regionCenterX - inkCenterX) <= 120,
            $"horizontal: region {regionCenterX} ink {inkCenterX}");
    }

    private static (int Top, int Bottom, int Left, int Right) InkBox(byte[] pixels, int width, int height)
    {
        int top = height, bottom = -1, left = width, right = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (pixels[y * width + x] < 100)
                {
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }
        }
        Assert.True(bottom >= 0, "no ink found");
        return (top, bottom, left, right);
    }

    private static (int Top, int Bottom) InkRows(byte[] pixels, int width, int height)
    {
        int top = -1;
        int bottom = -1;
        for (int y = 0; y < height; y++)
        {
            int dark = 0;
            for (int x = 0; x < width; x++)
            {
                if (pixels[y * width + x] < 100)
                    dark++;
            }
            if (dark > width / 20)
            {
                if (top < 0)
                    top = y;
                bottom = y;
            }
        }
        Assert.True(top >= 0, "synthetic render produced no ink rows");
        return (top, bottom);
    }

    private static byte[] Rotate(byte[] pixels, int width, int height, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double cx = (width - 1) / 2.0;
        double cy = (height - 1) / 2.0;
        var result = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double dx = x - cx;
                double dy = y - cy;
                double sx = cx + dx * cos - dy * sin;
                double sy = cy + dx * sin + dy * cos;
                int ix = Math.Max(0, Math.Min(width - 1, (int)Math.Round(sx)));
                int iy = Math.Max(0, Math.Min(height - 1, (int)Math.Round(sy)));
                result[y * width + x] = pixels[iy * width + ix];
            }
        }
        return result;
    }
}
