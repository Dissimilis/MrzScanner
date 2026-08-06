using System.Globalization;
using System.Text;

namespace Dissimilis.MrzScanner;

/// <summary>
/// A date as recorded in an MRZ. MRZ dates are YYMMDD and individual components
/// may be unknown (filler characters), so every component is optional.
/// </summary>
public readonly struct MrzDate : IEquatable<MrzDate>
{
    internal MrzDate(int? year, int? month, int? day)
    {
        Year = year;
        Month = month;
        Day = day;
    }

    /// <summary>Full four digit year after century resolution, if known.</summary>
    public int? Year { get; }

    /// <summary>Month 1 to 12, if known.</summary>
    public int? Month { get; }

    /// <summary>Day 1 to 31, if known.</summary>
    public int? Day { get; }

    /// <summary>True when year, month, and day are all known.</summary>
    public bool IsComplete => Year.HasValue && Month.HasValue && Day.HasValue;

    /// <summary>True when no component is known at all.</summary>
    public bool IsEmpty => !Year.HasValue && !Month.HasValue && !Day.HasValue;

    /// <summary>The date as a <see cref="DateTime" /> (midnight, unspecified kind), or null when incomplete or invalid.</summary>
    public DateTime? ToDateTime()
    {
        if (!IsComplete)
            return null;
        try
        {
            return new DateTime(Year!.Value, Month!.Value, Day!.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Formats as yyyy-MM-dd with ? in place of unknown parts.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder(10);
        sb.Append(Year.HasValue ? Year.Value.ToString("D4", CultureInfo.InvariantCulture) : "????");
        sb.Append('-');
        sb.Append(Month.HasValue ? Month.Value.ToString("D2", CultureInfo.InvariantCulture) : "??");
        sb.Append('-');
        sb.Append(Day.HasValue ? Day.Value.ToString("D2", CultureInfo.InvariantCulture) : "??");
        return sb.ToString();
    }

    /// <inheritdoc />
    public bool Equals(MrzDate other) => Year == other.Year && Month == other.Month && Day == other.Day;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MrzDate other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = Year.GetValueOrDefault(-1);
            hash = hash * 397 ^ Month.GetValueOrDefault(-1);
            hash = hash * 397 ^ Day.GetValueOrDefault(-1);
            return hash;
        }
    }

    /// <summary>Equality.</summary>
    public static bool operator ==(MrzDate left, MrzDate right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    public static bool operator !=(MrzDate left, MrzDate right) => !left.Equals(right);
}
