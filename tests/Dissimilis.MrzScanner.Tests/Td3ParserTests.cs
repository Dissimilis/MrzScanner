using Xunit;

namespace Dissimilis.MrzScanner.Tests;

public class Td3ParserTests
{
    // Specimen passport of Utopia from ICAO Doc 9303 part 4.
    private const string Specimen =
        "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\n" +
        "L898902C36UTO7408122F1204159ZE184226B<<<<<10";

    private static readonly MrzParser Parser = new(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Parses_the_icao_specimen_passport()
    {
        MrzResult result = Parser.Parse(Specimen);

        Assert.True(result.MrzFound);
        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.NotNull(result.Document);
        Assert.Equal(1.0, result.Confidence);

        MrzDocument doc = result.Document!;
        Assert.Equal(DocumentType.Td3, doc.Type);
        Assert.Equal("P", doc.DocumentCode);
        Assert.Equal("UTO", doc.IssuingCountry);
        Assert.Equal("UTO", doc.Nationality);
        Assert.Equal("L898902C3", doc.DocumentNumber);
        Assert.Equal("ERIKSSON", doc.PrimaryIdentifier);
        Assert.Equal("ANNA MARIA", doc.SecondaryIdentifier);
        Assert.Equal(Sex.Female, doc.Sex);
        Assert.Equal(new DateTime(1974, 8, 12), doc.BirthDate.ToDateTime());
        Assert.Equal(new DateTime(2012, 4, 15), doc.ExpiryDate.ToDateTime());
        Assert.Equal("ZE184226B", doc.PersonalNumber);
        Assert.Equal(doc.OptionalData1, doc.PersonalNumber);
    }

    [Fact]
    public void All_check_digits_of_the_specimen_are_valid()
    {
        MrzResult result = Parser.Parse(Specimen);

        Assert.Equal(CheckDigitStatus.Valid, result.Checks.DocumentNumber);
        Assert.Equal(CheckDigitStatus.Valid, result.Checks.BirthDate);
        Assert.Equal(CheckDigitStatus.Valid, result.Checks.ExpiryDate);
        Assert.Equal(CheckDigitStatus.Valid, result.Checks.OptionalData);
        Assert.Equal(CheckDigitStatus.Valid, result.Checks.Composite);
    }

    [Fact]
    public void Raw_data_preserves_the_exact_characters()
    {
        MrzResult result = Parser.Parse(Specimen);

        Assert.NotNull(result.Raw);
        Assert.Equal("P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<", result.Raw!.Line1);
        Assert.Equal("L898902C3", result.Raw.DocumentNumber);
        Assert.Equal("740812", result.Raw.BirthDate);
        Assert.Equal("120415", result.Raw.ExpiryDate);
        Assert.Equal("F", result.Raw.Sex);
        Assert.Equal("ZE184226B<<<<<", result.Raw.OptionalData1);
        Assert.Equal("ERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<", result.Raw.Name);
    }

    [Fact]
    public void A_corrupted_check_digit_is_reported_not_thrown()
    {
        string corrupted = Specimen.Replace("L898902C36", "L898902C30");

        MrzResult result = Parser.Parse(corrupted);

        Assert.True(result.MrzFound);
        Assert.False(result.IsValid);
        Assert.Equal(CheckDigitStatus.Invalid, result.Checks.DocumentNumber);
        Assert.Contains(result.Issues, i => i.Kind == MrzIssueKind.CheckDigitFailed && i.Field == "DocumentNumber");
        Assert.NotNull(result.Document);
        Assert.Equal("L898902C3", result.Document!.DocumentNumber);
    }

    [Fact]
    public void Empty_personal_number_with_filler_check_digit_is_not_present()
    {
        // Optional data all fillers and its check digit as filler is legal.
        string line2 = "L898902C36UTO7408122F1204159<<<<<<<<<<<<<<<";
        int composite = Internal.CheckDigit.Compute(new[]
        {
            line2.Substring(0, 10), line2.Substring(13, 7), line2.Substring(21, 22),
        });
        string text = "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\n" + line2 + composite;

        MrzResult result = Parser.Parse(text);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal(CheckDigitStatus.NotPresent, result.Checks.OptionalData);
        Assert.Equal(string.Empty, result.Document!.PersonalNumber);
    }
}
