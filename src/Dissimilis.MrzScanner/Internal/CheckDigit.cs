namespace Dissimilis.MrzScanner.Internal;

/// <summary>ICAO 9303 check digit arithmetic (7-3-1 repeating weights, modulo 10).</summary>
internal static class CheckDigit
{
    /// <summary>Numeric value of an MRZ character, or -1 for characters outside the MRZ alphabet.</summary>
    public static int CharValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'Z' => c - 'A' + 10,
        '<' => 0,
        _ => -1,
    };

    private static int Weight(int index) => (index % 3) switch
    {
        0 => 7,
        1 => 3,
        _ => 1,
    };

    /// <summary>
    /// Computes the check digit over the given character sequence.
    /// Returns -1 when the sequence contains a character outside the MRZ alphabet.
    /// </summary>
    public static int Compute(string s)
    {
        int sum = 0;
        for (int i = 0; i < s.Length; i++)
        {
            int v = CharValue(s[i]);
            if (v < 0)
                return -1;
            sum += v * Weight(i);
        }
        return sum % 10;
    }

    /// <summary>
    /// Computes the check digit over several concatenated segments,
    /// weighting positions continuously across segment boundaries.
    /// </summary>
    public static int Compute(IReadOnlyList<string> segments)
    {
        int sum = 0;
        int index = 0;
        for (int s = 0; s < segments.Count; s++)
        {
            string segment = segments[s];
            for (int i = 0; i < segment.Length; i++)
            {
                int v = CharValue(segment[i]);
                if (v < 0)
                    return -1;
                sum += v * Weight(index);
                index++;
            }
        }
        return sum % 10;
    }
}
