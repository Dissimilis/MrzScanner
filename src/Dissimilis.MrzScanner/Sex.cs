namespace Dissimilis.MrzScanner;

/// <summary>Sex as recorded in the MRZ.</summary>
public enum Sex
{
    /// <summary>The MRZ contains a filler or an unrecognized character in the sex position.</summary>
    Unspecified = 0,

    /// <summary>M in the MRZ.</summary>
    Male,

    /// <summary>F in the MRZ.</summary>
    Female,
}
