namespace Dissimilis.MrzScanner.Internal;

/// <summary>Character class allowed at an MRZ position, used for validation and OCR arbitration.</summary>
internal enum CharClass
{
    /// <summary>A-Z, 0-9, or filler.</summary>
    Any = 0,

    /// <summary>A-Z or filler.</summary>
    Alpha,

    /// <summary>0-9 only.</summary>
    Digit,

    /// <summary>0-9 or filler.</summary>
    DigitOrFiller,

    /// <summary>M, F, or filler.</summary>
    SexChar,
}

/// <summary>Identifies a field within an MRZ format.</summary>
internal enum FieldId
{
    DocumentCode,
    IssuingCountry,
    Name,
    DocumentNumber,
    DocumentNumberCheck,
    Nationality,
    BirthDate,
    BirthDateCheck,
    Sex,
    ExpiryDate,
    ExpiryDateCheck,
    OptionalData1,
    OptionalData2,
    OptionalDataCheck,
    CompositeCheck,
}

/// <summary>Location and character class of one field.</summary>
internal sealed class FieldDef
{
    public FieldDef(FieldId id, int line, int start, int length, CharClass charClass)
    {
        Id = id;
        Line = line;
        Start = start;
        Length = length;
        Class = charClass;
    }

    public FieldId Id { get; }
    public int Line { get; }
    public int Start { get; }
    public int Length { get; }
    public CharClass Class { get; }
}

/// <summary>One check digit and the character ranges it protects.</summary>
internal sealed class CheckRelation
{
    public CheckRelation(FieldId checkField, (int Line, int Start, int Length)[] protects, bool fillerAllowed)
    {
        CheckField = checkField;
        Protects = protects;
        FillerAllowed = fillerAllowed;
    }

    public FieldId CheckField { get; }
    public (int Line, int Start, int Length)[] Protects { get; }

    /// <summary>True when the check position may be a filler because the protected field can be unused.</summary>
    public bool FillerAllowed { get; }
}

/// <summary>
/// Structure of one ICAO 9303 MRZ format. A single source of truth shared by the
/// text parser (field extraction, validation) and the recognizer (per position
/// character classes for checksum arbitration).
/// </summary>
internal sealed class MrzFormat
{
    private MrzFormat(DocumentType type, int lineCount, int lineLength, FieldDef[] fields, CheckRelation[] checks)
    {
        Type = type;
        LineCount = lineCount;
        LineLength = lineLength;
        Fields = fields;
        Checks = checks;
    }

    public DocumentType Type { get; }
    public int LineCount { get; }
    public int LineLength { get; }
    public FieldDef[] Fields { get; }
    public CheckRelation[] Checks { get; }

    /// <summary>
    /// First characters ICAO 9303 allows in the document code of this format.
    /// No check digit protects the code, so recognition uses this prior directly.
    /// </summary>
    public char[] AllowedCodeFirstChars => Type switch
    {
        DocumentType.Td3 => new[] { 'P' },
        DocumentType.MrvA or DocumentType.MrvB => new[] { 'V' },
        _ => new[] { 'I', 'A', 'C' },
    };

    public FieldDef? Field(FieldId id)
    {
        for (int i = 0; i < Fields.Length; i++)
        {
            if (Fields[i].Id == id)
                return Fields[i];
        }
        return null;
    }

    /// <summary>Allowed character class at a position, for OCR candidate arbitration.</summary>
    public CharClass ClassAt(int line, int position)
    {
        for (int i = 0; i < Fields.Length; i++)
        {
            FieldDef f = Fields[i];
            if (f.Line == line && position >= f.Start && position < f.Start + f.Length)
            {
                if (f.Id is FieldId.DocumentNumberCheck or FieldId.BirthDateCheck
                    or FieldId.ExpiryDateCheck or FieldId.OptionalDataCheck or FieldId.CompositeCheck)
                {
                    return CharClass.DigitOrFiller;
                }
                return f.Class;
            }
        }
        return CharClass.Any;
    }

    public static MrzFormat Td1 { get; } = new(
        DocumentType.Td1, 3, 30,
        new[]
        {
            new FieldDef(FieldId.DocumentCode, 0, 0, 2, CharClass.Alpha),
            new FieldDef(FieldId.IssuingCountry, 0, 2, 3, CharClass.Alpha),
            new FieldDef(FieldId.DocumentNumber, 0, 5, 9, CharClass.Any),
            new FieldDef(FieldId.DocumentNumberCheck, 0, 14, 1, CharClass.DigitOrFiller),
            new FieldDef(FieldId.OptionalData1, 0, 15, 15, CharClass.Any),
            new FieldDef(FieldId.BirthDate, 1, 0, 6, CharClass.DigitOrFiller),
            new FieldDef(FieldId.BirthDateCheck, 1, 6, 1, CharClass.Digit),
            new FieldDef(FieldId.Sex, 1, 7, 1, CharClass.SexChar),
            new FieldDef(FieldId.ExpiryDate, 1, 8, 6, CharClass.DigitOrFiller),
            new FieldDef(FieldId.ExpiryDateCheck, 1, 14, 1, CharClass.Digit),
            new FieldDef(FieldId.Nationality, 1, 15, 3, CharClass.Alpha),
            new FieldDef(FieldId.OptionalData2, 1, 18, 11, CharClass.Any),
            new FieldDef(FieldId.CompositeCheck, 1, 29, 1, CharClass.Digit),
            new FieldDef(FieldId.Name, 2, 0, 30, CharClass.Alpha),
        },
        new[]
        {
            new CheckRelation(FieldId.DocumentNumberCheck, new[] { (0, 5, 9) }, fillerAllowed: true),
            new CheckRelation(FieldId.BirthDateCheck, new[] { (1, 0, 6) }, fillerAllowed: false),
            new CheckRelation(FieldId.ExpiryDateCheck, new[] { (1, 8, 6) }, fillerAllowed: false),
            new CheckRelation(FieldId.CompositeCheck, new[] { (0, 5, 25), (1, 0, 7), (1, 8, 7), (1, 18, 11) }, fillerAllowed: false),
        });

    public static MrzFormat Td2 { get; } = new(
        DocumentType.Td2, 2, 36,
        new[]
        {
            new FieldDef(FieldId.DocumentCode, 0, 0, 2, CharClass.Alpha),
            new FieldDef(FieldId.IssuingCountry, 0, 2, 3, CharClass.Alpha),
            new FieldDef(FieldId.Name, 0, 5, 31, CharClass.Alpha),
            new FieldDef(FieldId.DocumentNumber, 1, 0, 9, CharClass.Any),
            new FieldDef(FieldId.DocumentNumberCheck, 1, 9, 1, CharClass.DigitOrFiller),
            new FieldDef(FieldId.Nationality, 1, 10, 3, CharClass.Alpha),
            new FieldDef(FieldId.BirthDate, 1, 13, 6, CharClass.DigitOrFiller),
            new FieldDef(FieldId.BirthDateCheck, 1, 19, 1, CharClass.Digit),
            new FieldDef(FieldId.Sex, 1, 20, 1, CharClass.SexChar),
            new FieldDef(FieldId.ExpiryDate, 1, 21, 6, CharClass.DigitOrFiller),
            new FieldDef(FieldId.ExpiryDateCheck, 1, 27, 1, CharClass.Digit),
            new FieldDef(FieldId.OptionalData1, 1, 28, 7, CharClass.Any),
            new FieldDef(FieldId.CompositeCheck, 1, 35, 1, CharClass.Digit),
        },
        new[]
        {
            new CheckRelation(FieldId.DocumentNumberCheck, new[] { (1, 0, 9) }, fillerAllowed: true),
            new CheckRelation(FieldId.BirthDateCheck, new[] { (1, 13, 6) }, fillerAllowed: false),
            new CheckRelation(FieldId.ExpiryDateCheck, new[] { (1, 21, 6) }, fillerAllowed: false),
            new CheckRelation(FieldId.CompositeCheck, new[] { (1, 0, 10), (1, 13, 7), (1, 21, 14) }, fillerAllowed: false),
        });

    public static MrzFormat Td3 { get; } = new(
        DocumentType.Td3, 2, 44,
        new[]
        {
            new FieldDef(FieldId.DocumentCode, 0, 0, 2, CharClass.Alpha),
            new FieldDef(FieldId.IssuingCountry, 0, 2, 3, CharClass.Alpha),
            new FieldDef(FieldId.Name, 0, 5, 39, CharClass.Alpha),
            new FieldDef(FieldId.DocumentNumber, 1, 0, 9, CharClass.Any),
            new FieldDef(FieldId.DocumentNumberCheck, 1, 9, 1, CharClass.Digit),
            new FieldDef(FieldId.Nationality, 1, 10, 3, CharClass.Alpha),
            new FieldDef(FieldId.BirthDate, 1, 13, 6, CharClass.DigitOrFiller),
            new FieldDef(FieldId.BirthDateCheck, 1, 19, 1, CharClass.Digit),
            new FieldDef(FieldId.Sex, 1, 20, 1, CharClass.SexChar),
            new FieldDef(FieldId.ExpiryDate, 1, 21, 6, CharClass.DigitOrFiller),
            new FieldDef(FieldId.ExpiryDateCheck, 1, 27, 1, CharClass.Digit),
            new FieldDef(FieldId.OptionalData1, 1, 28, 14, CharClass.Any),
            new FieldDef(FieldId.OptionalDataCheck, 1, 42, 1, CharClass.DigitOrFiller),
            new FieldDef(FieldId.CompositeCheck, 1, 43, 1, CharClass.Digit),
        },
        new[]
        {
            new CheckRelation(FieldId.DocumentNumberCheck, new[] { (1, 0, 9) }, fillerAllowed: false),
            new CheckRelation(FieldId.BirthDateCheck, new[] { (1, 13, 6) }, fillerAllowed: false),
            new CheckRelation(FieldId.ExpiryDateCheck, new[] { (1, 21, 6) }, fillerAllowed: false),
            new CheckRelation(FieldId.OptionalDataCheck, new[] { (1, 28, 14) }, fillerAllowed: true),
            new CheckRelation(FieldId.CompositeCheck, new[] { (1, 0, 10), (1, 13, 7), (1, 21, 22) }, fillerAllowed: false),
        });

    public static MrzFormat MrvA { get; } = new(
        DocumentType.MrvA, 2, 44,
        new[]
        {
            new FieldDef(FieldId.DocumentCode, 0, 0, 2, CharClass.Alpha),
            new FieldDef(FieldId.IssuingCountry, 0, 2, 3, CharClass.Alpha),
            new FieldDef(FieldId.Name, 0, 5, 39, CharClass.Alpha),
            new FieldDef(FieldId.DocumentNumber, 1, 0, 9, CharClass.Any),
            new FieldDef(FieldId.DocumentNumberCheck, 1, 9, 1, CharClass.Digit),
            new FieldDef(FieldId.Nationality, 1, 10, 3, CharClass.Alpha),
            new FieldDef(FieldId.BirthDate, 1, 13, 6, CharClass.DigitOrFiller),
            new FieldDef(FieldId.BirthDateCheck, 1, 19, 1, CharClass.Digit),
            new FieldDef(FieldId.Sex, 1, 20, 1, CharClass.SexChar),
            new FieldDef(FieldId.ExpiryDate, 1, 21, 6, CharClass.DigitOrFiller),
            new FieldDef(FieldId.ExpiryDateCheck, 1, 27, 1, CharClass.Digit),
            new FieldDef(FieldId.OptionalData1, 1, 28, 16, CharClass.Any),
        },
        new[]
        {
            new CheckRelation(FieldId.DocumentNumberCheck, new[] { (1, 0, 9) }, fillerAllowed: false),
            new CheckRelation(FieldId.BirthDateCheck, new[] { (1, 13, 6) }, fillerAllowed: false),
            new CheckRelation(FieldId.ExpiryDateCheck, new[] { (1, 21, 6) }, fillerAllowed: false),
        });

    public static MrzFormat MrvB { get; } = new(
        DocumentType.MrvB, 2, 36,
        new[]
        {
            new FieldDef(FieldId.DocumentCode, 0, 0, 2, CharClass.Alpha),
            new FieldDef(FieldId.IssuingCountry, 0, 2, 3, CharClass.Alpha),
            new FieldDef(FieldId.Name, 0, 5, 31, CharClass.Alpha),
            new FieldDef(FieldId.DocumentNumber, 1, 0, 9, CharClass.Any),
            new FieldDef(FieldId.DocumentNumberCheck, 1, 9, 1, CharClass.Digit),
            new FieldDef(FieldId.Nationality, 1, 10, 3, CharClass.Alpha),
            new FieldDef(FieldId.BirthDate, 1, 13, 6, CharClass.DigitOrFiller),
            new FieldDef(FieldId.BirthDateCheck, 1, 19, 1, CharClass.Digit),
            new FieldDef(FieldId.Sex, 1, 20, 1, CharClass.SexChar),
            new FieldDef(FieldId.ExpiryDate, 1, 21, 6, CharClass.DigitOrFiller),
            new FieldDef(FieldId.ExpiryDateCheck, 1, 27, 1, CharClass.Digit),
            new FieldDef(FieldId.OptionalData1, 1, 28, 8, CharClass.Any),
        },
        new[]
        {
            new CheckRelation(FieldId.DocumentNumberCheck, new[] { (1, 0, 9) }, fillerAllowed: false),
            new CheckRelation(FieldId.BirthDateCheck, new[] { (1, 13, 6) }, fillerAllowed: false),
            new CheckRelation(FieldId.ExpiryDateCheck, new[] { (1, 21, 6) }, fillerAllowed: false),
        });

    /// <summary>Picks the format for normalized MRZ lines, or null when nothing matches.</summary>
    public static MrzFormat? Detect(IReadOnlyList<string> lines)
    {
        if (lines.Count == 3 && lines[0].Length == 30)
            return Td1;
        if (lines.Count == 2 && lines[0].Length == 44)
            return lines[0][0] == 'V' ? MrvA : Td3;
        if (lines.Count == 2 && lines[0].Length == 36)
            return lines[0][0] == 'V' ? MrvB : Td2;
        return null;
    }
}
