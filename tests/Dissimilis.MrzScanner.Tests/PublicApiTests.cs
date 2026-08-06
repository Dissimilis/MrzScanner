using Xunit;

namespace Dissimilis.MrzScanner.Tests;

/// <summary>
/// Tests for the detection and video session APIs: region reporting, the
/// locator fast path, and multi frame fusion.
/// </summary>
public class PublicApiTests
{
    private static readonly string[] Td3Lines =
    {
        "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<",
        "L898902C36UTO7408122F1204159ZE184226B<<<<<10",
    };

    private static MrzImage Synthetic(string[] lines, SyntheticMrz.RenderOptions? options = null)
    {
        (byte[] pixels, int width, int height) = SyntheticMrz.Render(lines, options);
        return MrzImage.FromGrayscale8(pixels, width, height);
    }

    [Fact]
    public void Read_reports_the_mrz_region()
    {
        (byte[] pixels, int width, int height) = SyntheticMrz.Render(Td3Lines);
        MrzResult result = MrzScanner.Default.Read(MrzImage.FromGrayscale8(pixels, width, height));

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.NotNull(result.Region);
        MrzRegion region = result.Region!;
        Assert.Equal(0, region.RotationDegrees);
        Assert.Equal(2, region.LineCount);

        // The band sits in the lower part of the card and inside the image.
        Assert.InRange(region.Left, 0, width);
        Assert.InRange(region.Top, height / 4, height);
        Assert.True(region.Width > width / 2, $"Width {region.Width} of {width}");
        Assert.True(region.Top + region.Height <= height);
    }

    [Fact]
    public void Read_resolves_rotation_for_an_upside_down_document()
    {
        MrzResult result = MrzScanner.Default.Read(
            Synthetic(Td3Lines, new SyntheticMrz.RenderOptions { Rotate180 = true }));

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.NotNull(result.Region);
        Assert.Equal(180, result.Region!.RotationDegrees);
    }

    [Fact]
    public void LocateMrz_finds_the_band_without_reading_it()
    {
        (byte[] pixels, int width, int height) = SyntheticMrz.Render(Td3Lines);
        IReadOnlyList<MrzRegion> regions = MrzScanner.Default.LocateMrz(
            MrzImage.FromGrayscale8(pixels, width, height));

        Assert.NotEmpty(regions);
        MrzRegion best = regions[0];
        Assert.Equal(0, best.RotationDegrees);
        Assert.InRange(best.Top, height / 4, height);
        Assert.True(best.Width > width / 2);
    }

    [Fact]
    public void LocateMrz_returns_nothing_for_a_blank_image()
    {
        var pixels = new byte[400 * 300];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = 200;
        IReadOnlyList<MrzRegion> regions = MrzScanner.Default.LocateMrz(
            MrzImage.FromGrayscale8(pixels, 400, 300));

        Assert.Empty(regions);
    }

    [Fact]
    public void Read_resolves_rotation_and_region_for_a_sideways_document()
    {
        (byte[] pixels, int width, int height) = SyntheticMrz.Render(Td3Lines);

        // Rotate the photo 90 degrees counter clockwise, so bringing the MRZ
        // upright requires a 90 degree clockwise turn.
        var rotated = new byte[pixels.Length];
        int rw = height;
        int rh = width;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
                rotated[(rh - 1 - x) * rw + y] = pixels[y * width + x];
        }

        MrzResult result = MrzScanner.Default.Read(MrzImage.FromGrayscale8(rotated, rw, rh));

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.NotNull(result.Region);
        MrzRegion region = result.Region!;
        Assert.Equal(90, region.RotationDegrees);

        // In the rotated photo the band runs vertically along the left side.
        Assert.True(region.Height > region.Width, $"{region.Width}x{region.Height}");
        Assert.InRange(region.Left, 0, rw / 2);
        Assert.True(region.Left + region.Width <= rw);
        Assert.True(region.Top + region.Height <= rh);
    }

    [Fact]
    public void Result_collections_cannot_be_mutated_by_the_caller()
    {
        MrzResult result = MrzScanner.Default.Read(Synthetic(Td3Lines));

        Assert.True(result.MrzFound);
        Assert.False(result.Issues is List<MrzIssue>, "Issues must not be a mutable List");
        Assert.False(result.Raw!.Lines is List<string>, "Raw lines must not be a mutable List");
    }

    [Fact]
    public void Undefined_search_effort_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MrzScanner(new MrzScannerOptions { SearchEffort = (MrzSearchEffort)99 }));
    }

    [Fact]
    public void Unknown_country_code_is_flagged_but_not_invalid()
    {
        // ZZZ is not an ICAO code; nationality carries no check digit, so
        // the substitution leaves every check digit intact.
        MrzResult result = MrzParser.ParseText(
            "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\n" +
            "L898902C36ZZZ7408122F1204159ZE184226B<<<<<10");

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Contains(result.Issues, i => i.Kind == MrzIssueKind.UnknownValue);
    }

    [Fact]
    public void VideoSession_fuses_noisy_frames_into_a_stable_result()
    {
        var session = new MrzVideoSession();
        MrzResult last = MrzResult.NotFound("no frames");
        for (int frame = 0; frame < 6 && !session.IsStable; frame++)
        {
            MrzImage image = Synthetic(Td3Lines, new SyntheticMrz.RenderOptions
            {
                NoiseAmplitude = 24,
                Seed = 1000 + frame,
            });
            last = session.Feed(image);
        }

        Assert.True(session.IsStable, string.Join("; ", last.Issues));
        Assert.True(session.Best!.IsValid);
        Assert.Equal("L898902C3", session.Best.Document!.DocumentNumber);
        Assert.Equal("ERIKSSON", session.Best.Document.PrimaryIdentifier);
    }

    [Fact]
    public void VideoSession_stays_unstable_on_garbage_frames()
    {
        var session = new MrzVideoSession();
        var pixels = new byte[400 * 300];
        var random = new Random(7);
        for (int frame = 0; frame < 3; frame++)
        {
            random.NextBytes(pixels);
            session.Feed(MrzImage.FromGrayscale8(pixels, 400, 300));
        }

        Assert.False(session.IsStable);
        Assert.Equal(3, session.FramesSeen);
    }

    [Fact]
    public void VideoSession_reset_forgets_the_document()
    {
        var session = new MrzVideoSession();
        session.Feed(Synthetic(Td3Lines));
        session.Feed(Synthetic(Td3Lines));
        Assert.True(session.IsStable);

        session.Reset();
        Assert.False(session.IsStable);
        Assert.Null(session.Best);
        Assert.Equal(0, session.FramesSeen);
    }
}
