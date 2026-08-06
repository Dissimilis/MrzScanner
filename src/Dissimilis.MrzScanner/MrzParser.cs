using System.Collections.Generic;
using Dissimilis.MrzScanner.Internal;

namespace Dissimilis.MrzScanner;

/// <summary>
/// Parses MRZ text into a typed, validated <see cref="MrzResult" />.
/// Instances are stateless and thread safe. Bad content is reported through
/// the result, never thrown.
/// </summary>
public sealed class MrzParser : IMrzParser
{
    private readonly DateTime? _referenceUtcDate;

    /// <summary>Creates a parser.</summary>
    public MrzParser()
    {
    }

    /// <summary>Creates a parser with a fixed reference date for two digit year resolution. For tests.</summary>
    internal MrzParser(DateTime referenceUtcDate)
    {
        _referenceUtcDate = referenceUtcDate;
    }

    /// <summary>Parses MRZ text with lines separated by newlines, without creating a parser instance.</summary>
    /// <param name="mrzText">The MRZ lines. Must not be null.</param>
    public static MrzResult ParseText(string mrzText) => new MrzParser().Parse(mrzText);

    /// <inheritdoc />
    public MrzResult Parse(string mrzText)
    {
        if (mrzText is null)
            throw new ArgumentNullException(nameof(mrzText));
        var lines = new List<string>();
        foreach (string piece in mrzText.Split('\n'))
        {
            string line = piece.Trim().Trim('\r');
            if (line.Length > 0)
                lines.Add(line);
        }
        return Parse(lines);
    }

    /// <inheritdoc />
    public MrzResult Parse(IReadOnlyList<string> lines)
    {
        if (lines is null)
            throw new ArgumentNullException(nameof(lines));

        var issues = new List<MrzIssue>();
        List<string>? normalized = Normalize(lines, issues);
        if (normalized is null)
            return MrzResult.NotFound("The text does not match any ICAO 9303 MRZ format.");

        MrzFormat? format = MrzFormat.Detect(normalized);
        if (format is null)
            return MrzResult.NotFound("The text does not match any ICAO 9303 MRZ format.");

        return ParseNormalized(format, normalized, issues, confidence: 1.0);
    }

    /// <summary>
    /// Parses lines that are already exact format length. Shared by the public text
    /// entry points and the image recognizer.
    /// </summary>
    internal MrzResult ParseNormalized(MrzFormat format, IReadOnlyList<string> lines, List<MrzIssue> issues, double confidence)
    {
        var raw = new MrzRawData(lines);
        string Extract(FieldId id)
        {
            FieldDef? f = format.Field(id);
            return f is null ? string.Empty : lines[f.Line].Substring(f.Start, f.Length);
        }

        raw.DocumentCode = Extract(FieldId.DocumentCode);
        raw.IssuingCountry = Extract(FieldId.IssuingCountry);
        raw.Name = Extract(FieldId.Name);
        raw.DocumentNumber = Extract(FieldId.DocumentNumber);
        raw.Nationality = Extract(FieldId.Nationality);
        WarnUnknownCountry(raw.IssuingCountry, "IssuingCountry", issues);
        WarnUnknownCountry(raw.Nationality, "Nationality", issues);
        raw.BirthDate = Extract(FieldId.BirthDate);
        raw.Sex = Extract(FieldId.Sex);
        raw.ExpiryDate = Extract(FieldId.ExpiryDate);
        raw.OptionalData1 = Extract(FieldId.OptionalData1);
        raw.OptionalData2 = Extract(FieldId.OptionalData2);

        ValidateCharClasses(format, lines, issues);

        // Long document numbers (TD1/TD2 extension): a filler in the check position
        // means the number continues in optional data, followed by its check digit.
        string documentNumber = TrimFillers(raw.DocumentNumber);
        string optionalData1 = TrimFillers(raw.OptionalData1);
        CheckDigitStatus documentNumberCheck;
        string checkChar = Extract(FieldId.DocumentNumberCheck);
        if (checkChar == "<" && format.Type is DocumentType.Td1 or DocumentType.Td2 && raw.OptionalData1.Length > 0)
        {
            (documentNumber, optionalData1, documentNumberCheck) =
                ParseExtendedDocumentNumber(raw.DocumentNumber, raw.OptionalData1, issues);
        }
        else
        {
            documentNumberCheck = VerifyCheck(format, FieldId.DocumentNumberCheck, lines, issues, "DocumentNumber");
        }

        CheckDigitStatus birthCheck = VerifyCheck(format, FieldId.BirthDateCheck, lines, issues, "BirthDate");
        CheckDigitStatus expiryCheck = VerifyCheck(format, FieldId.ExpiryDateCheck, lines, issues, "ExpiryDate");
        CheckDigitStatus optionalCheck = VerifyCheck(format, FieldId.OptionalDataCheck, lines, issues, "OptionalData");
        CheckDigitStatus compositeCheck = VerifyCheck(format, FieldId.CompositeCheck, lines, issues, "Composite");

        var checks = new MrzChecks(documentNumberCheck, birthCheck, expiryCheck, optionalCheck, compositeCheck);

        DateTime reference = _referenceUtcDate ?? DateTime.UtcNow;
        MrzDate birthDate = ParseDate(raw.BirthDate, isExpiry: false, reference, "BirthDate", issues);
        MrzDate expiryDate = ParseDate(raw.ExpiryDate, isExpiry: true, reference, "ExpiryDate", issues);

        (string primary, string secondary) = SplitName(raw.Name);

        var document = new MrzDocument(
            format.Type,
            TrimFillers(raw.DocumentCode),
            TrimFillers(raw.IssuingCountry),
            TrimFillers(raw.Nationality),
            documentNumber,
            primary,
            secondary,
            ParseSex(raw.Sex, issues),
            birthDate,
            expiryDate,
            optionalData1,
            TrimFillers(raw.OptionalData2));

        ValidateMandatoryContent(document, issues);

        return new MrzResult(
            mrzFound: true,
            document: document,
            raw: raw,
            checks: checks,
            issues: issues,
            confidence: confidence);
    }

    private static List<string>? Normalize(IReadOnlyList<string> input, List<MrzIssue> issues)
    {
        var lines = new List<string>();
        foreach (string? rawLine in input)
        {
            if (rawLine is null)
                continue;
            string line = rawLine.Trim().ToUpperInvariant();
            if (line.Length > 0)
                lines.Add(line);
        }

        if (lines.Count is not (2 or 3))
            return null;

        int expected = ExpectedLength(lines);
        if (expected == 0)
            return null;

        for (int i = 0; i < lines.Count; i++)
        {
            int diff = expected - lines[i].Length;
            if (diff is > 0 and <= 2)
            {
                issues.Add(new MrzIssue(string.Empty, MrzIssueKind.BadFormat,
                    $"Line {i + 1} is {lines[i].Length} characters, padded to the expected {expected}."));
                lines[i] = lines[i].PadRight(expected, '<');
            }
            else if (diff is < 0 and >= -2)
            {
                issues.Add(new MrzIssue(string.Empty, MrzIssueKind.BadFormat,
                    $"Line {i + 1} is {lines[i].Length} characters, truncated to the expected {expected}."));
                lines[i] = lines[i].Substring(0, expected);
            }
            else if (diff != 0)
            {
                return null;
            }
        }

        // TD1 must have three lines; two line formats must have two.
        if (expected == 30 && lines.Count != 3)
            return null;
        if (expected != 30 && lines.Count != 2)
            return null;
        return lines;
    }

    private static int ExpectedLength(List<string> lines)
    {
        // The most common exact length wins; ties resolve to the first line's length.
        int[] candidates = { 30, 36, 44 };
        int best = 0;
        int bestVotes = -1;
        foreach (int candidate in candidates)
        {
            int votes = 0;
            foreach (string line in lines)
            {
                if (line.Length == candidate)
                    votes++;
            }
            if (votes > bestVotes)
            {
                bestVotes = votes;
                best = candidate;
            }
        }
        if (bestVotes <= 0)
        {
            // No exact match at all; fall back to the nearest candidate within 2 of line 1.
            foreach (int candidate in candidates)
            {
                if (Math.Abs(lines[0].Length - candidate) <= 2)
                    return candidate;
            }
            return 0;
        }
        return best;
    }

    private static void ValidateCharClasses(MrzFormat format, IReadOnlyList<string> lines, List<MrzIssue> issues)
    {
        foreach (FieldDef field in format.Fields)
        {
            string value = lines[field.Line].Substring(field.Start, field.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (CheckDigit.CharValue(c) < 0)
                {
                    issues.Add(new MrzIssue(field.Id.ToString(), MrzIssueKind.BadFormat,
                        $"Character '{c}' at position {field.Start + i + 1} is outside the MRZ alphabet."));
                    continue;
                }
                bool ok = field.Class switch
                {
                    CharClass.Alpha => c is (>= 'A' and <= 'Z') or '<',
                    CharClass.Digit => c is >= '0' and <= '9',
                    CharClass.DigitOrFiller => c is (>= '0' and <= '9') or '<',
                    CharClass.SexChar => c is 'M' or 'F' or 'X' or '<',
                    _ => true,
                };
                if (!ok)
                {
                    issues.Add(new MrzIssue(field.Id.ToString(), MrzIssueKind.BadFormat,
                        $"Character '{c}' is not allowed at position {field.Start + i + 1} ({field.Class})."));
                }
            }
        }
    }

    private static CheckDigitStatus VerifyCheck(
        MrzFormat format, FieldId checkId, IReadOnlyList<string> lines, List<MrzIssue> issues, string fieldName)
    {
        CheckRelation? relation = null;
        foreach (CheckRelation r in format.Checks)
        {
            if (r.CheckField == checkId)
            {
                relation = r;
                break;
            }
        }
        FieldDef? checkField = format.Field(checkId);
        if (relation is null || checkField is null)
            return CheckDigitStatus.NotPresent;

        char checkChar = lines[checkField.Line][checkField.Start];
        var segments = new List<string>(relation.Protects.Length);
        bool allFiller = true;
        foreach ((int line, int start, int length) in relation.Protects)
        {
            string segment = lines[line].Substring(start, length);
            segments.Add(segment);
            for (int i = 0; i < segment.Length; i++)
            {
                if (segment[i] != '<')
                {
                    allFiller = false;
                    break;
                }
            }
        }

        if (checkChar == '<')
        {
            if (relation.FillerAllowed && allFiller)
                return CheckDigitStatus.NotPresent;
            issues.Add(new MrzIssue(fieldName, MrzIssueKind.CheckDigitFailed,
                "Check digit position contains a filler but the protected field is not empty."));
            return CheckDigitStatus.Invalid;
        }

        if (checkChar is < '0' or > '9')
        {
            issues.Add(new MrzIssue(fieldName, MrzIssueKind.CheckDigitFailed,
                $"Check digit position contains '{checkChar}', expected a digit."));
            return CheckDigitStatus.Invalid;
        }

        int expected = CheckDigit.Compute(segments);
        if (expected < 0)
        {
            issues.Add(new MrzIssue(fieldName, MrzIssueKind.CheckDigitFailed,
                "The protected characters contain a character outside the MRZ alphabet."));
            return CheckDigitStatus.Invalid;
        }

        if (expected != checkChar - '0')
        {
            issues.Add(new MrzIssue(fieldName, MrzIssueKind.CheckDigitFailed,
                $"Check digit is {checkChar}, computed {expected}."));
            return CheckDigitStatus.Invalid;
        }
        return CheckDigitStatus.Valid;
    }

    private static (string Number, string Optional, CheckDigitStatus Status) ParseExtendedDocumentNumber(
        string numberField, string optionalField, List<MrzIssue> issues)
    {
        int end = optionalField.IndexOf('<');
        if (end < 0)
            end = optionalField.Length;
        string continuation = optionalField.Substring(0, end);
        string remainder = end < optionalField.Length ? optionalField.Substring(end + 1) : string.Empty;

        if (continuation.Length < 2)
        {
            issues.Add(new MrzIssue("DocumentNumber", MrzIssueKind.CheckDigitFailed,
                "Extended document number marker found but no continuation and check digit in optional data."));
            return (TrimFillers(numberField), TrimFillers(optionalField), CheckDigitStatus.Invalid);
        }

        char checkChar = continuation[continuation.Length - 1];
        string numberPart = continuation.Substring(0, continuation.Length - 1);
        string fullNumber = numberField + numberPart;

        if (checkChar is < '0' or > '9')
        {
            issues.Add(new MrzIssue("DocumentNumber", MrzIssueKind.CheckDigitFailed,
                $"Extended document number check digit is '{checkChar}', expected a digit."));
            return (TrimFillers(fullNumber), TrimFillers(remainder), CheckDigitStatus.Invalid);
        }

        int expected = CheckDigit.Compute(fullNumber);
        if (expected != checkChar - '0')
        {
            issues.Add(new MrzIssue("DocumentNumber", MrzIssueKind.CheckDigitFailed,
                $"Extended document number check digit is {checkChar}, computed {expected}."));
            return (TrimFillers(fullNumber), TrimFillers(remainder), CheckDigitStatus.Invalid);
        }
        return (TrimFillers(fullNumber), TrimFillers(remainder), CheckDigitStatus.Valid);
    }

    private static void ValidateMandatoryContent(MrzDocument document, List<MrzIssue> issues)
    {
        // ICAO 9303 mandates the document code family per format.
        char code = document.DocumentCode.Length > 0 ? document.DocumentCode[0] : '<';
        bool codeOk = document.Type switch
        {
            DocumentType.Td3 => code == 'P',
            DocumentType.MrvA or DocumentType.MrvB => code == 'V',
            DocumentType.Td1 or DocumentType.Td2 => code is 'I' or 'A' or 'C',
            _ => true,
        };
        if (!codeOk)
        {
            issues.Add(new MrzIssue("DocumentCode", MrzIssueKind.InvalidValue,
                $"Document code '{document.DocumentCode}' is not valid for {document.Type}."));
        }

        if (document.DocumentNumber.Length == 0)
            issues.Add(new MrzIssue("DocumentNumber", MrzIssueKind.InvalidValue, "Document number is empty."));
        if (document.PrimaryIdentifier.Length == 0)
            issues.Add(new MrzIssue("Name", MrzIssueKind.InvalidValue, "Name is empty."));

        // Birth date components may legally be unknown; the expiry date may not.
        if (document.ExpiryDate.IsEmpty)
            issues.Add(new MrzIssue("ExpiryDate", MrzIssueKind.InvalidValue, "Expiry date is missing."));
    }

    private static MrzDate ParseDate(
        string field, bool isExpiry, DateTime reference, string fieldName, List<MrzIssue> issues)
    {
        if (field.Length != 6)
            return default;

        int? twoDigitYear = ParsePair(field, 0);
        int? month = ParsePair(field, 2);
        int? day = ParsePair(field, 4);

        if ((twoDigitYear is null && !IsFillerPair(field, 0)) ||
            (month is null && !IsFillerPair(field, 2)) ||
            (day is null && !IsFillerPair(field, 4)))
        {
            issues.Add(new MrzIssue(fieldName, MrzIssueKind.InvalidValue,
                $"Date field '{field}' mixes digits and fillers within a component."));
        }

        if (month is < 1 or > 12)
        {
            issues.Add(new MrzIssue(fieldName, MrzIssueKind.InvalidValue, $"Month {month} is out of range."));
            month = null;
        }
        if (day is < 1 or > 31)
        {
            issues.Add(new MrzIssue(fieldName, MrzIssueKind.InvalidValue, $"Day {day} is out of range."));
            day = null;
        }

        int? year = null;
        if (twoDigitYear.HasValue)
        {
            if (isExpiry)
            {
                // Expiry dates resolve into the 1990 to 2089 window.
                year = twoDigitYear.Value >= 90 ? 1900 + twoDigitYear.Value : 2000 + twoDigitYear.Value;
            }
            else
            {
                // Birth dates resolve to the latest date that is not in the future,
                // considering month and day when the year alone cannot decide.
                int candidate = 2000 + twoDigitYear.Value;
                bool future = candidate > reference.Year;
                if (candidate == reference.Year && month.HasValue)
                {
                    future = month.Value > reference.Month ||
                        (month.Value == reference.Month && day.HasValue && day.Value > reference.Day);
                }
                year = future ? 1900 + twoDigitYear.Value : candidate;
            }
        }

        if (year.HasValue && month.HasValue && day.HasValue &&
            day.Value > DateTime.DaysInMonth(year.Value, month.Value))
        {
            issues.Add(new MrzIssue(fieldName, MrzIssueKind.InvalidValue,
                $"Day {day} does not exist in {year}-{month:D2}."));
        }

        return new MrzDate(year, month, day);
    }

    private static int? ParsePair(string s, int index)
    {
        char a = s[index];
        char b = s[index + 1];
        if (a is >= '0' and <= '9' && b is >= '0' and <= '9')
            return (a - '0') * 10 + (b - '0');
        return null;
    }

    private static bool IsFillerPair(string s, int index) => s[index] == '<' && s[index + 1] == '<';

    private static Sex ParseSex(string field, List<MrzIssue> issues)
    {
        if (field.Length == 0)
            return Sex.Unspecified;
        char c = field[0];
        switch (c)
        {
            case 'M':
                return Sex.Male;
            case 'F':
                return Sex.Female;
            case '<':
                return Sex.Unspecified;
            default:
                // ICAO 9303 allows only M, F, or filler in the MRZ; X and anything
                // else is reported but still mapped to Unspecified.
                issues.Add(new MrzIssue("Sex", MrzIssueKind.InvalidValue, $"Unexpected sex character '{c}'."));
                return Sex.Unspecified;
        }
    }

    private static (string Primary, string Secondary) SplitName(string field)
    {
        string primaryRaw;
        string secondaryRaw;
        int separator = field.IndexOf("<<", StringComparison.Ordinal);
        if (separator < 0)
        {
            primaryRaw = field;
            secondaryRaw = string.Empty;
        }
        else
        {
            primaryRaw = field.Substring(0, separator);
            secondaryRaw = field.Substring(separator + 2);
        }
        return (FillersToSpaces(primaryRaw), FillersToSpaces(secondaryRaw));
    }

    private static string FillersToSpaces(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        bool pendingSpace = false;
        foreach (char c in value)
        {
            if (c == '<')
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Flags issuer and nationality codes that are well formed but not on the
    /// ICAO country list. Informational only: the tables can lag newly issued
    /// codes, so an unknown code must not invalidate an otherwise clean read.
    /// </summary>
    private static void WarnUnknownCountry(string code, string field, List<MrzIssue> issues)
    {
        if (string.IsNullOrEmpty(code) || code.Trim('<').Length == 0)
            return;
        if (Internal.CountryCodes.IsValid(code))
            return;
        issues.Add(new MrzIssue(field, MrzIssueKind.UnknownValue,
            $"Code '{code}' is not a known ICAO issuing state or nationality code."));
    }

    private static string TrimFillers(string value)
    {
        int start = 0;
        int end = value.Length;
        while (start < end && value[start] == '<')
            start++;
        while (end > start && value[end - 1] == '<')
            end--;
        return value.Substring(start, end - start);
    }
}
