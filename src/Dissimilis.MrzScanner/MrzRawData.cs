using System.Collections.Generic;

namespace Dissimilis.MrzScanner;

/// <summary>
/// The MRZ characters before any field interpretation, after input
/// normalization (uppercasing, trimming, and length padding). Useful for
/// auditing, debugging, and conversion edge cases.
/// </summary>
public sealed class MrzRawData
{
    internal MrzRawData(IReadOnlyList<string> lines)
    {
        Lines = lines is string[] || lines is System.Collections.ObjectModel.ReadOnlyCollection<string>
            ? lines
            : new System.Collections.ObjectModel.ReadOnlyCollection<string>(new List<string>(lines));
        DocumentCode = string.Empty;
        IssuingCountry = string.Empty;
        Name = string.Empty;
        DocumentNumber = string.Empty;
        Nationality = string.Empty;
        BirthDate = string.Empty;
        Sex = string.Empty;
        ExpiryDate = string.Empty;
        OptionalData1 = string.Empty;
        OptionalData2 = string.Empty;
    }

    /// <summary>All MRZ lines exactly as recognized or supplied.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>First line, or an empty string.</summary>
    public string Line1 => Lines.Count > 0 ? Lines[0] : string.Empty;

    /// <summary>Second line, or an empty string.</summary>
    public string Line2 => Lines.Count > 1 ? Lines[1] : string.Empty;

    /// <summary>Third line (TD1 only), or an empty string.</summary>
    public string Line3 => Lines.Count > 2 ? Lines[2] : string.Empty;

    /// <summary>Document code characters, for example "P&lt;" or "ID".</summary>
    public string DocumentCode { get; internal set; }

    /// <summary>Issuing state field as printed, fillers included.</summary>
    public string IssuingCountry { get; internal set; }

    /// <summary>The complete name field as printed, fillers included.</summary>
    public string Name { get; internal set; }

    /// <summary>Document number field as printed, fillers included.</summary>
    public string DocumentNumber { get; internal set; }

    /// <summary>Nationality field as printed, fillers included.</summary>
    public string Nationality { get; internal set; }

    /// <summary>Birth date field as printed (YYMMDD, may contain fillers).</summary>
    public string BirthDate { get; internal set; }

    /// <summary>Sex character as printed.</summary>
    public string Sex { get; internal set; }

    /// <summary>Expiry date field as printed (YYMMDD, may contain fillers).</summary>
    public string ExpiryDate { get; internal set; }

    /// <summary>First optional data field as printed, fillers included.</summary>
    public string OptionalData1 { get; internal set; }

    /// <summary>Second optional data field as printed (TD1 only), fillers included.</summary>
    public string OptionalData2 { get; internal set; }
}
