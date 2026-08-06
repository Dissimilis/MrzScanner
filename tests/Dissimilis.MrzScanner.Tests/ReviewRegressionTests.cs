using Dissimilis.MrzScanner.Internal;
using Xunit;

namespace Dissimilis.MrzScanner.Tests;

// Regressions for defects surfaced by external code review.
public class ReviewRegressionTests
{
    private static readonly MrzParser Parser = new(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void All_filler_lines_with_zero_check_digits_are_not_valid()
    {
        // Structurally correct TD3 dimensions, zero content, check digits that
        // arithmetically match empty fields. Must not be reported as valid.
        string line1 = "P<" + new string('<', 42);
        string line2 = "<<<<<<<<<0<<<<<<<<<0<<<<<<<0<<<<<<<<<<<<<<00";

        MrzResult result = Parser.Parse(line1 + "\n" + line2);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Field == "DocumentNumber" && i.Kind == MrzIssueKind.InvalidValue);
        Assert.Contains(result.Issues, i => i.Field == "Name" && i.Kind == MrzIssueKind.InvalidValue);
        Assert.Contains(result.Issues, i => i.Field == "ExpiryDate" && i.Kind == MrzIssueKind.InvalidValue);
    }

    [Fact]
    public void Wrong_document_code_for_format_is_reported()
    {
        // TD3 dimensions but a document code that TD3 does not allow.
        string text =
            "I<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\n" +
            "L898902C36UTO7408122F1204159ZE184226B<<<<<10";

        MrzResult result = Parser.Parse(text);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Field == "DocumentCode" && i.Kind == MrzIssueKind.InvalidValue);
    }

    [Fact]
    public void Invalid_value_makes_the_result_invalid_even_with_good_check_digits()
    {
        // Birth month 13 with a matching check digit.
        string birth = "741315";
        string line2Start = "L898902C36UTO" + birth + CheckDigit.Compute(birth) + "F" +
                            "1204159" + "<<<<<<<<<<<<<<<";
        int composite = CheckDigit.Compute(new[]
        {
            line2Start.Substring(0, 10), line2Start.Substring(13, 7), line2Start.Substring(21, 22),
        });
        string text = "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\n" + line2Start + composite;

        MrzResult result = Parser.Parse(text);

        Assert.Equal(CheckDigitStatus.Valid, result.Checks.BirthDate);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Calendar_invalid_dates_are_reported()
    {
        // 31 February 1974 passes digit range checks but is not a real date.
        string birth = "740231";
        string line2Start = "L898902C36UTO" + birth + CheckDigit.Compute(birth) + "F" +
                            "1204159" + "<<<<<<<<<<<<<<<";
        int composite = CheckDigit.Compute(new[]
        {
            line2Start.Substring(0, 10), line2Start.Substring(13, 7), line2Start.Substring(21, 22),
        });
        string text = "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\n" + line2Start + composite;

        MrzResult result = Parser.Parse(text);

        Assert.Contains(result.Issues, i => i.Field == "BirthDate" && i.Kind == MrzIssueKind.InvalidValue);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void X_in_the_sex_position_maps_to_unspecified_with_an_issue()
    {
        string text =
            "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\n" +
            "L898902C36UTO7408122X1204159ZE184226B<<<<<10";

        MrzResult result = Parser.Parse(text);

        Assert.Equal(Sex.Unspecified, result.Document!.Sex);
        Assert.Contains(result.Issues, i => i.Field == "Sex" && i.Kind == MrzIssueKind.InvalidValue);
    }

    [Fact]
    public void Birth_date_later_this_year_resolves_to_the_previous_century()
    {
        // Reference date is 2026-08-03; 26 December 31 would be in the future,
        // so the birth year must resolve to 1926.
        string birth = "261231";
        string line2Start = "L898902C36UTO" + birth + CheckDigit.Compute(birth) + "F" +
                            "3012319" + "<<<<<<<<<<<<<<<";
        int composite = CheckDigit.Compute(new[]
        {
            line2Start.Substring(0, 10), line2Start.Substring(13, 7), line2Start.Substring(21, 22),
        });
        string text = "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\n" + line2Start + composite;

        MrzResult result = Parser.Parse(text);

        Assert.Equal(1926, result.Document!.BirthDate.Year);
    }

    [Fact]
    public void Reads_a_document_photographed_sideways()
    {
        string[] lines =
        {
            "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<",
            "L898902C36UTO7408122F1204159ZE184226B<<<<<10",
        };
        (byte[] pixels, int width, int height) = SyntheticMrz.Render(lines);

        // Rotate the photo 90 degrees clockwise so the MRZ runs vertically.
        var rotated = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
                rotated[x * height + (height - 1 - y)] = pixels[y * width + x];
        }

        MrzResult result = MrzScanner.Default.Read(MrzImage.FromGrayscale8(rotated, height, width));

        Assert.True(result.MrzFound, string.Join("; ", result.Issues));
        Assert.Equal("L898902C3", result.Document!.DocumentNumber);
    }

    [Fact]
    public void Transparent_pixels_read_as_white_not_black()
    {
        // A fully transparent RGBA image with black hidden RGB must convert to
        // white, producing no MRZ instead of a solid black false positive.
        int width = 400;
        int height = 300;
        var rgba = new byte[width * height * 4];

        MrzResult result = MrzScanner.Default.Read(MrzImage.FromRgba32(rgba, width, height));

        Assert.False(result.MrzFound);
    }

    [Fact]
    public void Too_small_max_dimension_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MrzScanner(new MrzScannerOptions { MaxImageDimension = 50 }));
    }
}
