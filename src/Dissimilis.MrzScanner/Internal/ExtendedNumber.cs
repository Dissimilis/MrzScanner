namespace Dissimilis.MrzScanner.Internal;

/// <summary>Shared boundary rule for a continued TD1/TD2 document number.</summary>
internal static class ExtendedNumber
{
    internal static int ContinuationLength(string optionalField)
    {
        int end = optionalField.IndexOf('<');
        return end < 0 ? optionalField.Length : end;
    }
}
