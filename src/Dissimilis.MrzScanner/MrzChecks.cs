namespace Dissimilis.MrzScanner;

/// <summary>Results of all ICAO 9303 check digit verifications for a document.</summary>
public sealed class MrzChecks
{
    internal MrzChecks(
        CheckDigitStatus documentNumber,
        CheckDigitStatus birthDate,
        CheckDigitStatus expiryDate,
        CheckDigitStatus optionalData,
        CheckDigitStatus composite)
    {
        DocumentNumber = documentNumber;
        BirthDate = birthDate;
        ExpiryDate = expiryDate;
        OptionalData = optionalData;
        Composite = composite;
    }

    internal static MrzChecks Empty { get; } = new(
        CheckDigitStatus.NotPresent,
        CheckDigitStatus.NotPresent,
        CheckDigitStatus.NotPresent,
        CheckDigitStatus.NotPresent,
        CheckDigitStatus.NotPresent);

    /// <summary>Check digit protecting the document number.</summary>
    public CheckDigitStatus DocumentNumber { get; }

    /// <summary>Check digit protecting the date of birth.</summary>
    public CheckDigitStatus BirthDate { get; }

    /// <summary>Check digit protecting the expiry date.</summary>
    public CheckDigitStatus ExpiryDate { get; }

    /// <summary>Check digit protecting the optional data (personal number) on TD3 documents.</summary>
    public CheckDigitStatus OptionalData { get; }

    /// <summary>Composite check digit protecting several fields together (not present on visas).</summary>
    public CheckDigitStatus Composite { get; }

    /// <summary>True when no present check digit failed.</summary>
    public bool AllValid =>
        DocumentNumber != CheckDigitStatus.Invalid &&
        BirthDate != CheckDigitStatus.Invalid &&
        ExpiryDate != CheckDigitStatus.Invalid &&
        OptionalData != CheckDigitStatus.Invalid &&
        Composite != CheckDigitStatus.Invalid;
}
