using Dissimilis.MrzScanner.Internal;
using Xunit;

namespace Dissimilis.MrzScanner.Tests;

public class Td2AndVisaParserTests
{
    // Specimen TD2 document of Utopia from ICAO Doc 9303 part 6.
    private const string Td2Specimen =
        "I<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<\n" +
        "D231458907UTO7408122F1204159<<<<<<<6";

    private static readonly MrzParser Parser = new(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Parses_the_icao_specimen_td2_document()
    {
        MrzResult result = Parser.Parse(Td2Specimen);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));

        MrzDocument doc = result.Document!;
        Assert.Equal(DocumentType.Td2, doc.Type);
        Assert.Equal("D23145890", doc.DocumentNumber);
        Assert.Equal("ERIKSSON", doc.PrimaryIdentifier);
        Assert.Equal("ANNA MARIA", doc.SecondaryIdentifier);
        Assert.Equal(CheckDigitStatus.Valid, result.Checks.Composite);
    }

    [Fact]
    public void Parses_a_synthetic_mrv_a_visa()
    {
        // Visas have no composite check digit; optional data has no check either.
        string line1 = "V<UTOTRAVELER<<HAPPY<<<<<<<<<<<<<<<<<<<<<<<<";
        string number = "17103813<";
        string birth = "800215";
        string expiry = "281231";
        string line2 = number + CheckDigit.Compute(number) + "UTO" +
                       birth + CheckDigit.Compute(birth) + "M" +
                       expiry + CheckDigit.Compute(expiry) + "6007668UT<<<<<<<";

        MrzResult result = Parser.Parse(line1 + "\n" + line2);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        MrzDocument doc = result.Document!;
        Assert.Equal(DocumentType.MrvA, doc.Type);
        Assert.Equal("V", doc.DocumentCode);
        Assert.Equal("17103813", doc.DocumentNumber);
        Assert.Equal("TRAVELER", doc.PrimaryIdentifier);
        Assert.Equal("HAPPY", doc.SecondaryIdentifier);
        Assert.Equal(Sex.Male, doc.Sex);
        Assert.Equal("6007668UT", doc.OptionalData1);
        Assert.Equal(CheckDigitStatus.NotPresent, result.Checks.Composite);
        Assert.Equal(CheckDigitStatus.NotPresent, result.Checks.OptionalData);
    }

    [Fact]
    public void Parses_a_synthetic_mrv_b_visa()
    {
        string line1 = "V<UTOTRAVELER<<HAPPY<<<<<<<<<<<<<<<<";
        string number = "17103813<";
        string birth = "800215";
        string expiry = "281231";
        string line2 = number + CheckDigit.Compute(number) + "UTO" +
                       birth + CheckDigit.Compute(birth) + "M" +
                       expiry + CheckDigit.Compute(expiry) + "<<<<<<<<";

        MrzResult result = Parser.Parse(line1 + "\n" + line2);

        Assert.True(result.IsValid, string.Join("; ", result.Issues));
        Assert.Equal(DocumentType.MrvB, result.Document!.Type);
    }

    [Fact]
    public void Static_convenience_entry_point_works()
    {
        MrzResult result = MrzParser.ParseText(Td2Specimen);
        Assert.True(result.MrzFound);
        Assert.Equal(DocumentType.Td2, result.Document!.Type);
    }
}
