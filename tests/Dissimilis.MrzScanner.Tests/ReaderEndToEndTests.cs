using Xunit;

namespace Dissimilis.MrzScanner.Tests;

public class ReaderEndToEndTests
{
    private static readonly string[] Td3Lines =
    {
        "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<",
        "L898902C36UTO7408122F1204159ZE184226B<<<<<10",
    };

    private static readonly string[] Td1Lines =
    {
        "I<UTOD231458907<<<<<<<<<<<<<<<",
        "7408122F1204159UTO<<<<<<<<<<<6",
        "ERIKSSON<<ANNA<MARIA<<<<<<<<<<",
    };

    private static MrzResult ReadSynthetic(string[] lines, SyntheticMrz.RenderOptions? options = null)
    {
        (byte[] pixels, int width, int height) = SyntheticMrz.Render(lines, options);
        return MrzScanner.Default.Read(MrzImage.FromGrayscale8(pixels, width, height));
    }

    [Fact]
    public void Reads_a_clean_synthetic_td3_passport()
    {
        MrzResult result = ReadSynthetic(Td3Lines);

        Assert.True(result.MrzFound, string.Join("; ", result.Issues));
        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal(DocumentType.Td3, result.Document!.Type);
        Assert.Equal("L898902C3", result.Document.DocumentNumber);
        Assert.Equal("ERIKSSON", result.Document.PrimaryIdentifier);
        Assert.Equal("ANNA MARIA", result.Document.SecondaryIdentifier);
        Assert.True(result.Confidence > 0.6, $"Confidence {result.Confidence}");
    }

    [Fact]
    public void Reads_a_clean_synthetic_td1_id_card()
    {
        MrzResult result = ReadSynthetic(Td1Lines);

        Assert.True(result.MrzFound, string.Join("; ", result.Issues));
        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal(DocumentType.Td1, result.Document!.Type);
        Assert.Equal("D23145890", result.Document.DocumentNumber);
    }

    [Fact]
    public void Reads_an_upside_down_document()
    {
        MrzResult result = ReadSynthetic(Td3Lines, new SyntheticMrz.RenderOptions { Rotate180 = true });

        Assert.True(result.MrzFound, string.Join("; ", result.Issues));
        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal("L898902C3", result.Document!.DocumentNumber);
    }

    [Fact]
    public void Reads_a_noisy_blurred_document()
    {
        MrzResult result = ReadSynthetic(Td3Lines, new SyntheticMrz.RenderOptions
        {
            NoiseAmplitude = 18,
            Blur = true,
        });

        Assert.True(result.MrzFound, string.Join("; ", result.Issues));
        Assert.Equal("L898902C3", result.Document!.DocumentNumber);
        Assert.Equal(CheckDigitStatus.Valid, result.Checks.DocumentNumber);
    }

    [Fact]
    public void Reads_from_encoded_bmp_bytes()
    {
        (byte[] pixels, int width, int height) = SyntheticMrz.Render(Td3Lines);
        byte[] bmp = SyntheticMrz.ToBmp(pixels, width, height);

        MrzResult result = MrzScanner.Default.Read(bmp);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal("L898902C3", result.Document!.DocumentNumber);
    }

    [Fact]
    public async Task Async_entry_point_reads_from_a_stream()
    {
        (byte[] pixels, int width, int height) = SyntheticMrz.Render(Td3Lines);
        byte[] bmp = SyntheticMrz.ToBmp(pixels, width, height);
        using var stream = new MemoryStream(bmp);

        MrzResult result = await MrzScanner.Default.ReadAsync(stream);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
    }

    [Fact]
    public void An_image_without_mrz_reports_not_found()
    {
        // A plain card with no printed band.
        int width = 800;
        int height = 500;
        var pixels = new byte[width * height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = 200;

        MrzResult result = MrzScanner.Default.Read(MrzImage.FromGrayscale8(pixels, width, height));

        Assert.False(result.MrzFound);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Kind == MrzIssueKind.NotFound);
    }

    [Fact]
    public void Undecodable_bytes_report_not_found_instead_of_throwing()
    {
        MrzResult result = MrzScanner.Default.Read(new byte[] { 1, 2, 3, 4, 5 });
        Assert.False(result.MrzFound);
    }

    [Fact]
    public void Invalid_raw_image_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => MrzImage.FromGrayscale8(null!, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => MrzImage.FromGrayscale8(new byte[100], 0, 10));
        Assert.Throws<ArgumentException>(() => MrzImage.FromGrayscale8(new byte[50], 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => MrzImage.FromRgb24(new byte[300], 10, 10, stride: 5));
        Assert.Throws<ArgumentException>(() => MrzScanner.Default.Read(default(MrzImage)));
    }

    [Fact]
    public void Raw_rgb_input_is_equivalent_to_grayscale()
    {
        (byte[] pixels, int width, int height) = SyntheticMrz.Render(Td3Lines);
        var rgb = new byte[pixels.Length * 3];
        for (int i = 0; i < pixels.Length; i++)
        {
            rgb[i * 3] = pixels[i];
            rgb[i * 3 + 1] = pixels[i];
            rgb[i * 3 + 2] = pixels[i];
        }

        MrzResult result = MrzScanner.Default.Read(MrzImage.FromRgb24(rgb, width, height));

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal("L898902C3", result.Document!.DocumentNumber);
    }
}
