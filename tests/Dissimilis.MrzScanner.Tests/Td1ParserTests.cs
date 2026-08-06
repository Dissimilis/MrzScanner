using Dissimilis.MrzScanner.Internal;
using Xunit;

namespace Dissimilis.MrzScanner.Tests;

public class Td1ParserTests
{
    // Specimen ID card of Utopia from ICAO Doc 9303 part 5.
    private const string Specimen =
        "I<UTOD231458907<<<<<<<<<<<<<<<\n" +
        "7408122F1204159UTO<<<<<<<<<<<6\n" +
        "ERIKSSON<<ANNA<MARIA<<<<<<<<<<";

    private static readonly MrzParser Parser = new(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Parses_the_icao_specimen_id_card()
    {
        MrzResult result = Parser.Parse(Specimen);

        Assert.True(result.MrzFound);
        Assert.True(result.IsValid, string.Join("; ", result.Issues));

        MrzDocument doc = result.Document!;
        Assert.Equal(DocumentType.Td1, doc.Type);
        Assert.Equal("I", doc.DocumentCode);
        Assert.Equal("UTO", doc.IssuingCountry);
        Assert.Equal("UTO", doc.Nationality);
        Assert.Equal("D23145890", doc.DocumentNumber);
        Assert.Equal("ERIKSSON", doc.PrimaryIdentifier);
        Assert.Equal("ANNA MARIA", doc.SecondaryIdentifier);
        Assert.Equal(Sex.Female, doc.Sex);
        Assert.Equal(new DateTime(1974, 8, 12), doc.BirthDate.ToDateTime());
        Assert.Equal(new DateTime(2012, 4, 15), doc.ExpiryDate.ToDateTime());
        Assert.Equal(string.Empty, doc.OptionalData1);
        Assert.Equal(string.Empty, doc.OptionalData2);

        Assert.Equal(CheckDigitStatus.Valid, result.Checks.DocumentNumber);
        Assert.Equal(CheckDigitStatus.Valid, result.Checks.BirthDate);
        Assert.Equal(CheckDigitStatus.Valid, result.Checks.ExpiryDate);
        Assert.Equal(CheckDigitStatus.Valid, result.Checks.Composite);
        Assert.Equal(CheckDigitStatus.NotPresent, result.Checks.OptionalData);
    }

    [Fact]
    public void Parses_an_extended_document_number_continued_in_optional_data()
    {
        // A document number longer than nine characters puts a filler in the check
        // position and continues in optional data, followed by its check digit.
        const string fullNumber = "D23145890123";
        int numberCheck = CheckDigit.Compute(fullNumber);
        string line1 = "I<UTO" + fullNumber.Substring(0, 9) + "<" +
                       (fullNumber.Substring(9) + numberCheck).PadRight(15, '<');
        string line2Body = "7408122F1204159UTO<<<<<<<<<<<";
        int composite = CheckDigit.Compute(new[]
        {
            line1.Substring(5, 25),
            line2Body.Substring(0, 7),
            line2Body.Substring(8, 7),
            line2Body.Substring(18, 11),
        });
        string text = line1 + "\n" + line2Body + composite + "\nERIKSSON<<ANNA<MARIA<<<<<<<<<<";

        MrzResult result = Parser.Parse(text);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal(fullNumber, result.Document!.DocumentNumber);
        Assert.Equal(CheckDigitStatus.Valid, result.Checks.DocumentNumber);
        Assert.Equal(string.Empty, result.Document.OptionalData1);
    }
}
