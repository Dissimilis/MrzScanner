namespace Dissimilis.MrzScanner;

/// <summary>Outcome of one ICAO 9303 check digit verification.</summary>
public enum CheckDigitStatus
{
    /// <summary>The format has no such check digit, or the document marks it as not used.</summary>
    NotPresent = 0,

    /// <summary>The check digit matches the protected characters.</summary>
    Valid,

    /// <summary>The check digit does not match the protected characters.</summary>
    Invalid,
}
