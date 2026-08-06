# Dissimilis.MrzScanner - Specification

C# library that reads the MRZ (machine readable zone) of identity documents from photos and scans. Pure managed implementation, published as a NuGet package. Includes a minimal WinForms demo application.

## Goals

- Read MRZ from a full document photo: locate the zone, recognize the characters, parse and validate the fields.
- Support all ICAO 9303 formats: TD1 (3x30), TD2 (2x36), TD3 (2x44), MRV-A (2x44), MRV-B (2x36).
- Pure C# end to end, including the OCR. No native binaries, no ML runtimes.
- Convenient, hard-to-misuse API. Never throws on bad input content.
- Cleanly report "no MRZ found" for images without an MRZ (for example the front side of an ID card).

## Non-goals

- General purpose OCR. The recognizer only knows the 37 character MRZ alphabet (A-Z, 0-9, `<`) in the OCR-B font.
- Reading RFID chips, barcodes, or the visual inspection zone.
- Perspective correction of extreme camera angles. Mild rotation and skew are in scope.

## Packages and projects

| Project | Target | Purpose |
| --- | --- | --- |
| `src/Dissimilis.MrzScanner` | netstandard2.0; net8.0 | The library, published to NuGet |
| `tests/Dissimilis.MrzScanner.Tests` | net8.0 | Unit tests (synthetic data) and local-only integration tests |
| `demo/MrzScanner.Demo` | net8.0-windows | WinForms demo: open an image, show the parsed result |

Single external dependency: `StbImageSharp` (public domain, pure managed) for JPEG/PNG/BMP decoding. It is an implementation detail and does not appear in the public API.

## Public API

Root namespace `Dissimilis.MrzScanner`.

### Image entry point

```csharp
public interface IMrzScanner
{
    Task<MrzResult> ReadAsync(Stream image, CancellationToken ct = default);
    Task<MrzResult> ReadAsync(byte[] image, CancellationToken ct = default);
    Task<MrzResult> ReadAsync(string filePath, CancellationToken ct = default);
    MrzResult Read(Stream image);
    MrzResult Read(byte[] image);
    MrzResult Read(string filePath);
}

public sealed class MrzScanner : IMrzScanner
{
    public MrzScanner();
    public MrzScanner(MrzScannerOptions options);
    public static MrzScanner Default { get; }   // shared thread safe instance
}
```

Instances are stateless and thread safe. `MrzScannerOptions` starts minimal (for example `MaxImageDimension` for downscale control) and only grows when a real need appears.

### Raw pixel input

Callers that already have decoded pixels (camera feed, their own decoder) are not forced through the built-in decoder:

```csharp
public readonly struct MrzImage
{
    public static MrzImage FromGrayscale8(byte[] pixels, int width, int height, int stride = 0);
    public static MrzImage FromRgb24(byte[] pixels, int width, int height, int stride = 0);
    public static MrzImage FromBgr24(byte[] pixels, int width, int height, int stride = 0);
    public static MrzImage FromRgba32(byte[] pixels, int width, int height, int stride = 0);
    public static MrzImage FromBgra32(byte[] pixels, int width, int height, int stride = 0);
}

// on IMrzScanner / MrzScanner
Task<MrzResult> ReadAsync(MrzImage image, CancellationToken ct = default);
MrzResult Read(MrzImage image);
```

`stride = 0` means tightly packed rows. The factory methods validate buffer size against width, height, and stride, and throw `ArgumentException` for mismatches (programmer error, not content error). Internally everything converts to the same grayscale buffer the file overloads produce.

### Text entry point

```csharp
public interface IMrzParser
{
    MrzResult Parse(string mrzText);            // lines separated by newlines
    MrzResult Parse(IReadOnlyList<string> lines);
}

public sealed class MrzParser : IMrzParser
{
    public static MrzResult ParseText(string mrzText);  // static convenience
}
```

The reader uses the parser internally. The parser is fully usable on its own for callers that already have MRZ text.

### Result model

```csharp
public sealed class MrzResult
{
    public bool MrzFound { get; }        // was an MRZ-shaped region detected at all
    public bool IsValid { get; }         // parsed and all present check digits valid
    public MrzDocument? Document { get; }
    public MrzRawData? Raw { get; }      // exact recognized characters, per line and per field
    public MrzChecks Checks { get; }     // each ICAO check digit: Valid / Invalid / NotPresent
    public IReadOnlyList<MrzIssue> Issues { get; }
    public double Confidence { get; }    // 0..1, aggregated from per character scores; 1.0 for text input
}

public sealed class MrzDocument
{
    public DocumentType Type { get; }          // Td1, Td2, Td3, MrvA, MrvB
    public string IssuingCountry { get; }      // ICAO 3 letter code as printed
    public string Nationality { get; }
    public string DocumentNumber { get; }
    public string PrimaryIdentifier { get; }   // surname
    public string SecondaryIdentifier { get; } // given names, space separated
    public Sex Sex { get; }                    // Male, Female, Unspecified
    public MrzDate BirthDate { get; }
    public MrzDate ExpiryDate { get; }
    public string OptionalData1 { get; }
    public string OptionalData2 { get; }
    public string PersonalNumber { get; }      // TD3 optional data alias
}

public readonly struct MrzDate
{
    public int? Year { get; }    // full year after century resolution
    public int? Month { get; }
    public int? Day { get; }
    public bool IsComplete { get; }
    public DateTime? ToDateTime();
    public override string ToString();  // yyyy-MM-dd with ? for unknown parts
}
```

`MrzIssue` carries `Field`, `Kind` (enum: `NotFound`, `BadFormat`, `CheckDigitFailed`, `LowConfidence`, `InvalidValue`), and a message. Errors are reported, never thrown; exceptions are reserved for programmer errors (null arguments) and IO failures from the file path overloads.

MRZ dates have two digit years. Century resolution: birth dates map to the latest year not in the future; expiry dates map to the window 1990 to 2089.

## Recognition pipeline

All stages pure C# on grayscale byte buffers.

1. **Decode and downscale.** StbImageSharp decodes; images larger than `MaxImageDimension` (default 2000 px) are downscaled with area averaging.
2. **Locate the MRZ band.** The MRZ has a distinctive signature: rows of high horizontal gradient density in a monospaced grid, 2 or 3 parallel lines, over 30 percent of document width. Search the whole image, score candidate bands, handle 180 degree rotation (upside down scans) by scoring both orientations, correct small skew by fitting the text baseline.
3. **Binarize and segment.** Adaptive threshold on the band. Line count and character pitch follow from the detected format candidates (30, 36, or 44 cells per line). Cells are cut on the fixed pitch grid with per cell refinement.
4. **Classify glyphs.** Normalized template matching against the 37 glyph OCR-B alphabet. Templates are procedurally generated vector shapes rasterized at match resolution (original work, no font license issues). Each cell keeps a ranked candidate list with scores.
5. **Checksum arbitration.** The ICAO check digits (document number, birth date, expiry, optional data, composite) plus field grammar (letters only vs digits only positions) arbitrate between top candidates. This is what lets a lean matcher reach high accuracy: uncertain glyphs are resolved by choosing the candidate combination that satisfies the checksums.
6. **Adaptive second pass.** Characters confirmed by valid checksums become document specific templates for re-scoring uncertain cells in the same image, adapting to the specific print, contrast, and blur.
7. **Parse.** The winning character grid goes through `MrzParser` for field extraction and validation.

If no region scores above the MRZ band threshold, the result is `MrzFound = false` with a `NotFound` issue. Front sides of cards must take this path, not produce garbage fields.

## Testing

- **Unit tests** (committed): check digit algorithm, all five format parsers, `MrzDate` century and partial date rules, issue reporting. Fixtures are synthetic MRZ strings from the ICAO 9303 specification examples and generated fakes.
- **Recognizer tests** (committed): synthetic images rendered from the embedded templates with noise, blur, rotation, and contrast variation. Full pipeline tested end to end without any real document.
- **Local integration harness** (committed code, local data): a test category that scans `samples/` from disk. It prints aggregate statistics only: images processed, MRZ found count, checksum pass rates, mean confidence. It must never print recognized field values, file names paired with results, or any document content. Expected-value files, if ever needed, live inside `samples/` (gitignored).
- `samples/` contains both MRZ documents and MRZ-less images (card front sides). The harness treats "no MRZ found" as a legitimate outcome and reports it as a count; there is no per file ground truth in the repo.

## Demo application

WinForms, deliberately minimal: one window with an Open button, a picture box showing the chosen image, and a read-only details panel listing the parsed fields, check digit results, and confidence. Runs the reader on a background thread. Not part of the NuGet package.

## Packaging

- Package ID `Dissimilis.MrzScanner`, license MIT, deterministic build, SourceLink, symbols package, XML docs.
- Versioning: semver starting at 0.1.0 until the API settles.

## Hard project rules

See CLAUDE.md: commits authored by dissimilis only, no AI attribution anywhere, no em-dash characters in any file, AI tooling files gitignored, and nothing from `samples/` ever leaves this machine (no commits, no uploads, no content into any LLM or cloud service).
