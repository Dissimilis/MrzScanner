using Xunit;

namespace Dissimilis.MrzScanner.Tests;

public class ParserEdgeCaseTests
{
    private static readonly MrzParser Parser = new(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Garbage_text_reports_not_found()
    {
        MrzResult result = Parser.Parse("hello world\nthis is not an mrz");

        Assert.False(result.MrzFound);
        Assert.False(result.IsValid);
        Assert.Null(result.Document);
        Assert.Contains(result.Issues, i => i.Kind == MrzIssueKind.NotFound);
    }

    [Fact]
    public void Empty_text_reports_not_found()
    {
        Assert.False(Parser.Parse("").MrzFound);
        Assert.False(Parser.Parse("   \n \n").MrzFound);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => Parser.Parse((string)null!));
        Assert.Throws<ArgumentNullException>(() => Parser.Parse((IReadOnlyList<string>)null!));
    }

    [Fact]
    public void Slightly_short_lines_are_padded_with_a_reported_issue()
    {
        // Specimen with two trailing fillers missing from line 1.
        string text =
            "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<\n" +
            "L898902C36UTO7408122F1204159ZE184226B<<<<<10";

        MrzResult result = Parser.Parse(text);

        Assert.True(result.MrzFound);
        Assert.Equal(DocumentType.Td3, result.Document!.Type);
        Assert.Equal("ERIKSSON", result.Document.PrimaryIdentifier);
        Assert.Contains(result.Issues, i => i.Kind == MrzIssueKind.BadFormat);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Lowercase_input_is_accepted()
    {
        string text =
            "p<utoeriksson<<anna<maria<<<<<<<<<<<<<<<<<<<\n" +
            "l898902c36uto7408122f1204159ze184226b<<<<<10";

        MrzResult result = Parser.Parse(text);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal("ERIKSSON", result.Document!.PrimaryIdentifier);
    }

    [Fact]
    public void Windows_line_endings_are_accepted()
    {
        string text =
            "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\r\n" +
            "L898902C36UTO7408122F1204159ZE184226B<<<<<10\r\n";

        MrzResult result = Parser.Parse(text);
        Assert.True(result.IsValid, string.Join("; ", result.Issues));
    }

    [Fact]
    public void Characters_outside_the_alphabet_are_reported_per_field()
    {
        string text =
            "P<UTOERIKS ON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\n" +
            "L898902C36UTO7408122F1204159ZE184226B<<<<<10";

        MrzResult result = Parser.Parse(text);

        // The space is stripped by trimming only at the ends, so an inner space
        // must surface as a bad format issue on the name field.
        Assert.Contains(result.Issues, i => i.Kind == MrzIssueKind.BadFormat && i.Field == "Name");
    }

    [Fact]
    public void Unexpected_sex_character_is_reported()
    {
        string text =
            "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\n" +
            "L898902C36UTO7408122Q1204159ZE184226B<<<<<10";

        MrzResult result = Parser.Parse(text);

        Assert.Equal(Sex.Unspecified, result.Document!.Sex);
        Assert.Contains(result.Issues, i => i.Field == "Sex");
    }

    [Fact]
    public void Composite_check_protects_against_field_swaps()
    {
        // Swap birth and expiry (both individually valid with their check digits):
        // the composite check must catch it. Field checks travel with the values,
        // so only the composite fails.
        string text =
            "P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\n" +
            "L898902C36UTO1204159F7408122ZE184226B<<<<<10";

        MrzResult result = Parser.Parse(text);

        Assert.False(result.IsValid);
        Assert.Equal(CheckDigitStatus.Valid, result.Checks.BirthDate);
        Assert.Equal(CheckDigitStatus.Valid, result.Checks.ExpiryDate);
        Assert.Equal(CheckDigitStatus.Invalid, result.Checks.Composite);
    }

    [Fact]
    public void Name_without_secondary_identifier_parses()
    {
        string text =
            "P<UTOMADONNA<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<\n" +
            "L898902C36UTO7408122F1204159ZE184226B<<<<<10";

        MrzResult result = Parser.Parse(text);

        Assert.Equal("MADONNA", result.Document!.PrimaryIdentifier);
        Assert.Equal(string.Empty, result.Document.SecondaryIdentifier);
    }
}
