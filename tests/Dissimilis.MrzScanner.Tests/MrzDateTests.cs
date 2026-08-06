using Xunit;

namespace Dissimilis.MrzScanner.Tests;

public class MrzDateTests
{
    private static readonly MrzParser Parser = new(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc));

    private static MrzResult ParseTd3WithDates(string birth, string expiry)
    {
        string line2Start = "L898902C36UTO" + birth + Internal.CheckDigit.Compute(birth) + "F" +
                            expiry + Internal.CheckDigit.Compute(expiry) + "<<<<<<<<<<<<<<<";
        int composite = Internal.CheckDigit.Compute(new[]
        {
            line2Start.Substring(0, 10), line2Start.Substring(13, 7), line2Start.Substring(21, 22),
        });
        return Parser.Parse("P<UTOERIKSSON<<ANNA<MARIA<<<<<<<<<<<<<<<<<<<\n" + line2Start + composite);
    }

    [Theory]
    [InlineData("740812", 1974)]
    [InlineData("260101", 2026)]
    [InlineData("270101", 1927)]
    [InlineData("000229", 2000)]
    public void Birth_year_resolves_to_the_latest_year_not_in_the_future(string birth, int expectedYear)
    {
        MrzResult result = ParseTd3WithDates(birth, "301231");
        Assert.Equal(expectedYear, result.Document!.BirthDate.Year);
    }

    [Theory]
    [InlineData("961210", 1996)]
    [InlineData("891231", 2089)]
    [InlineData("900101", 1990)]
    [InlineData("301231", 2030)]
    public void Expiry_year_resolves_into_the_1990_to_2089_window(string expiry, int expectedYear)
    {
        MrzResult result = ParseTd3WithDates("740812", expiry);
        Assert.Equal(expectedYear, result.Document!.ExpiryDate.Year);
    }

    [Fact]
    public void Partial_dates_keep_the_known_components()
    {
        MrzResult result = ParseTd3WithDates("74<<<<", "301231");

        MrzDate birth = result.Document!.BirthDate;
        Assert.Equal(1974, birth.Year);
        Assert.Null(birth.Month);
        Assert.Null(birth.Day);
        Assert.False(birth.IsComplete);
        Assert.False(birth.IsEmpty);
        Assert.Null(birth.ToDateTime());
        Assert.Equal("1974-??-??", birth.ToString());
    }

    [Fact]
    public void Fully_unknown_date_is_empty()
    {
        MrzResult result = ParseTd3WithDates("<<<<<<", "301231");

        Assert.True(result.Document!.BirthDate.IsEmpty);
        Assert.Equal("????-??-??", result.Document.BirthDate.ToString());
    }

    [Fact]
    public void Out_of_range_components_are_nulled_and_reported()
    {
        MrzResult result = ParseTd3WithDates("741315", "301231");

        Assert.Null(result.Document!.BirthDate.Month);
        Assert.Equal(15, result.Document.BirthDate.Day);
        Assert.Contains(result.Issues, i => i.Kind == MrzIssueKind.InvalidValue && i.Field == "BirthDate");
    }

    [Fact]
    public void Complete_date_formats_as_iso()
    {
        MrzResult result = ParseTd3WithDates("740812", "301231");
        Assert.Equal("1974-08-12", result.Document!.BirthDate.ToString());
        Assert.True(result.Document.BirthDate.IsComplete);
    }
}
