namespace Dissimilis.MrzScanner;

/// <summary>The ICAO 9303 machine readable zone format of a document.</summary>
public enum DocumentType
{
    /// <summary>Unknown or undetected format.</summary>
    Unknown = 0,

    /// <summary>TD1: credit card size document, three lines of 30 characters (most ID cards).</summary>
    Td1,

    /// <summary>TD2: two lines of 36 characters (older ID documents).</summary>
    Td2,

    /// <summary>TD3: two lines of 44 characters (passports).</summary>
    Td3,

    /// <summary>MRV-A: full size visa, two lines of 44 characters.</summary>
    MrvA,

    /// <summary>MRV-B: small size visa, two lines of 36 characters.</summary>
    MrvB,
}
