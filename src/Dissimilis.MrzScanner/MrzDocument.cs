namespace Dissimilis.MrzScanner;

/// <summary>Parsed and typed fields of a machine readable document.</summary>
public sealed class MrzDocument
{
    internal MrzDocument(
        DocumentType type,
        string documentCode,
        string issuingCountry,
        string nationality,
        string documentNumber,
        string primaryIdentifier,
        string secondaryIdentifier,
        Sex sex,
        MrzDate birthDate,
        MrzDate expiryDate,
        string optionalData1,
        string optionalData2)
    {
        Type = type;
        DocumentCode = documentCode;
        IssuingCountry = issuingCountry;
        Nationality = nationality;
        DocumentNumber = documentNumber;
        PrimaryIdentifier = primaryIdentifier;
        SecondaryIdentifier = secondaryIdentifier;
        Sex = sex;
        BirthDate = birthDate;
        ExpiryDate = expiryDate;
        OptionalData1 = optionalData1;
        OptionalData2 = optionalData2;
    }

    /// <summary>The detected MRZ format.</summary>
    public DocumentType Type { get; }

    /// <summary>Document code with fillers removed, for example "P", "ID", "V".</summary>
    public string DocumentCode { get; }

    /// <summary>Issuing state or organization, ICAO three letter code as printed.</summary>
    public string IssuingCountry { get; }

    /// <summary>Nationality of the holder, ICAO three letter code as printed.</summary>
    public string Nationality { get; }

    /// <summary>Document number with fillers removed.</summary>
    public string DocumentNumber { get; }

    /// <summary>Primary identifier, usually the surname. Word breaks become spaces.</summary>
    public string PrimaryIdentifier { get; }

    /// <summary>Secondary identifier, usually the given names. Word breaks become spaces.</summary>
    public string SecondaryIdentifier { get; }

    /// <summary>Sex of the holder.</summary>
    public Sex Sex { get; }

    /// <summary>Date of birth.</summary>
    public MrzDate BirthDate { get; }

    /// <summary>Date of expiry.</summary>
    public MrzDate ExpiryDate { get; }

    /// <summary>First optional data field with fillers removed (TD1 line 1, TD2/TD3/visa line 2).</summary>
    public string OptionalData1 { get; }

    /// <summary>Second optional data field with fillers removed (TD1 line 2 only).</summary>
    public string OptionalData2 { get; }

    /// <summary>Alias for <see cref="OptionalData1" /> on TD3 passports, where it holds the personal number.</summary>
    public string PersonalNumber => OptionalData1;
}
