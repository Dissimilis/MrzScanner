namespace Dissimilis.MrzScanner;

/// <summary>
/// Per field confidence estimates between 0 and 1: how likely each field was
/// read correctly. Derived from the recognition scores of the field's
/// characters and the state of the check digit protecting it, so a field
/// whose check digit verified scores much higher than an unprotected one
/// read from the same pixels. Only available for image reads.
/// </summary>
public sealed class MrzFieldConfidence
{
    internal MrzFieldConfidence(
        double documentNumber,
        double name,
        double birthDate,
        double expiryDate,
        double sex,
        double nationality,
        double issuingCountry,
        double optionalData)
    {
        DocumentNumber = documentNumber;
        Name = name;
        BirthDate = birthDate;
        ExpiryDate = expiryDate;
        Sex = sex;
        Nationality = nationality;
        IssuingCountry = issuingCountry;
        OptionalData = optionalData;
    }

    /// <summary>Confidence for the document number.</summary>
    public double DocumentNumber { get; }

    /// <summary>Confidence for the name line (primary and secondary identifiers).</summary>
    public double Name { get; }

    /// <summary>Confidence for the birth date.</summary>
    public double BirthDate { get; }

    /// <summary>Confidence for the expiry date.</summary>
    public double ExpiryDate { get; }

    /// <summary>Confidence for the sex character.</summary>
    public double Sex { get; }

    /// <summary>Confidence for the nationality code.</summary>
    public double Nationality { get; }

    /// <summary>Confidence for the issuing country code.</summary>
    public double IssuingCountry { get; }

    /// <summary>Confidence for the optional data, 0 when the field is absent or empty.</summary>
    public double OptionalData { get; }
}
