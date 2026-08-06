using Dissimilis.MrzScanner.Internal;
using Xunit;

namespace Dissimilis.MrzScanner.Tests;

public class CheckDigitTests
{
    [Theory]
    [InlineData("L898902C3", 6)]
    [InlineData("740812", 2)]
    [InlineData("120415", 9)]
    [InlineData("ZE184226B<<<<<", 1)]
    [InlineData("D23145890", 7)]
    [InlineData("<<<<<<<<<<<<<<", 0)]
    public void Computes_known_icao_examples(string input, int expected)
    {
        Assert.Equal(expected, CheckDigit.Compute(input));
    }

    [Theory]
    [InlineData('0', 0)]
    [InlineData('9', 9)]
    [InlineData('A', 10)]
    [InlineData('Z', 35)]
    [InlineData('<', 0)]
    public void Character_values_follow_the_spec(char c, int expected)
    {
        Assert.Equal(expected, CheckDigit.CharValue(c));
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('a')]
    [InlineData('-')]
    public void Characters_outside_the_alphabet_are_rejected(char c)
    {
        Assert.Equal(-1, CheckDigit.CharValue(c));
        Assert.Equal(-1, CheckDigit.Compute($"AB{c}"));
    }

    [Fact]
    public void Segmented_computation_weights_continuously()
    {
        int whole = CheckDigit.Compute("L898902C36740812");
        int segmented = CheckDigit.Compute(new[] { "L898902C36", "740812" });
        Assert.Equal(whole, segmented);
    }
}
