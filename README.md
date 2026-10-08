# Dissimilis.MrzScanner

A C# library that reads the machine readable zone of passports, ID cards and visas from photos. Everything is managed code, including the OCR: no native binaries, no ML runtime, one small dependency for image decoding. Give it a JPEG, get back parsed fields with every ICAO 9303 check digit verified.

[![NuGet](https://img.shields.io/nuget/v/Dissimilis.MrzScanner.svg)](https://www.nuget.org/packages/Dissimilis.MrzScanner)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Dissimilis.MrzScanner.svg)](https://www.nuget.org/packages/Dissimilis.MrzScanner)
[![CI](https://github.com/Dissimilis/MrzScanner/actions/workflows/ci.yml/badge.svg)](https://github.com/Dissimilis/MrzScanner/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-donate-yellow.svg)](https://buymeacoffee.com/dissimilis)

![Detected MRZ on a specimen document](https://raw.githubusercontent.com/Dissimilis/MrzScanner/main/docs/mrz-detection.png)

The image above is the library's own detection output on a synthetic specimen (the Anna Maria Eriksson example from the ICAO 9303 spec). No real documents appear in this repository.

## Install

```
dotnet add package Dissimilis.MrzScanner
```

Targets netstandard2.0 and net8.0.

## Reading a photo

```csharp
using Dissimilis.MrzScanner;

MrzResult result = await MrzScanner.Default.ReadAsync("passport.jpg");

if (result.IsValid)
{
    var doc = result.Document!;
    Console.WriteLine($"{doc.DocumentNumber} {doc.PrimaryIdentifier}, born {doc.BirthDate}");
}
else
{
    foreach (MrzIssue issue in result.Issues)
        Console.WriteLine(issue);
}
```

`Read`/`ReadAsync` accept a file path, byte array, stream, or raw pixels via `MrzImage` if you decode images yourself. Bad input never throws; you get a result explaining what went wrong.

The result carries more than the fields:

- `IsValid` is true only when every present check digit verified.
- `Confidence` estimates the fraction of characters read correctly, calibrated on labeled documents. `FieldConfidence` breaks that down per field, so you can trust the checksum-backed document number while re-checking a shaky name.
- `Region` tells you where the MRZ sits in the image and how the image needs to be rotated to bring it upright. Sideways and upside-down photos are handled.
- `Raw` has the exact recognized characters for auditing.
- `CharacterScores` exposes per-character visual matching scores for highlighting uncertain text. They are scores, not calibrated probabilities; fused video reads report vote support.
- `CorrectionCount` counts distinct positions that retain a checksum substitution in the final read. Repeated search passes count once. It excludes grammar corrections.

## Scanning with a camera

Single frames from a phone camera are often half readable: motion blur in one, glare in the next. `MrzVideoSession` folds frames together, voting per character, so the combined read can be valid even when no single frame was.

Camera previews arrive as YUV, and for MRZ reading only the luma plane matters, so you can hand the buffer over as is. No JPEG round trip, no color conversion:

```csharp
var session = new MrzVideoSession();

void OnPreviewFrame(byte[] nv21, int width, int height)
{
    session.Feed(MrzImage.FromNv21(nv21, width, height));
    if (session.IsStable)
        Show(session.Best!.Document!);
    else
        ShowGuidance(session.LastFrameHints);
}
```

`FromNv12` and `FromI420` cover the other common camera layouts, and `FromGrayscale8`/`FromBgra32` and friends take decoded buffers. Camera buffers usually arrive in sensor orientation; pass the rotation the camera reports and the reader handles it: `MrzImage.FromNv21(buffer, width, height, 0, rotationDegrees)`. `IsStable` turns true once the result is fully valid and corroborated by more than one frame. Call `Reset()` between documents. A different identity is kept separate and replaces the current document after two valid sightings (one when `stableFrames` is 1). Until then, `Best` retains the prior result and `IsStable` is false on a conflicting frame. Read current capture guidance from `LastFrameHints`, including when the fused result is valid.

Fusion retains close character alternatives within a rolling window of at least 16 frames, applies bounded checksum correction, and supplies field confidence and character scores. Its overall confidence describes agreement between frames; it is not calibrated like still-image confidence.

`LastFrameHints` says what to fix when a frame did not read: `TooSmall` means move closer, `CutOff` means the band touches the frame edge, plus `Blurry`, `Glare`, `LowContrast` and `NoMrzDetected`. The same hints appear on `MrzResult.CaptureHints` for still photos. A frame with a clean MRZ typically reads in under 100 ms; a frame without one returns quickly so the next frame gets its turn.

If you want a viewfinder overlay before committing to a full read, `LocateMrz` finds MRZ-shaped bands in a few milliseconds without recognizing characters:

```csharp
foreach (MrzRegion region in reader.LocateMrz(frame))
    DrawOverlay(region.Left, region.Top, region.Width, region.Height);
```

## Options

```csharp
// Stills: exhaustive search (default). Video: bounded per-frame cost.
var reader = new MrzScanner(new MrzScannerOptions
{
    MaxImageDimension = 2000,
    SearchEffort = MrzSearchEffort.SingleFrame,
});

// Text you already have, no image involved.
MrzResult parsed = MrzParser.ParseText("P<UTOERIKSSON<<ANNA<MARIA...");
```

For archived scans, `new MrzParser(referenceUtcDate)` fixes the reference date used for birth-year century resolution. A two-digit year cannot distinguish a centenarian from a younger person with the same year suffix. Expiry dates retain the 1990 to 2089 window.

A clearly recognized `X` in the sex field is preserved and reported as `InvalidValue`; supported values are `M`, `F`, and filler. Such a read is not valid and cannot stabilize a video session.

## Local evaluation

`dotnet run --project tools/SamplesHarness -c Release -- --evaluate samples/non-sensitive - samples/baseline.json` records aggregate metrics from the local `expected.json` manifest. Empty expected line arrays label images without an MRZ; `?` excludes an uncertain character from comparison. Pass the baseline path instead of `-` to fail the command on accuracy, exact-read, wrong-valid, or false-detection regressions. Missing inputs also fail. No document text or filenames are printed by this command.

`--video-evaluate samples/non-sensitive` compares hard votes and alternative character evidence using the same current session implementation on 20 locally perturbed images. It is an evidence comparison, not a comparison with the previous library version. It reports aggregate results, including incorrect stable results during a sequence; it does not substitute for testing real camera sequences.

## How it works

The locator finds bands of dense, monospaced text by their edge profile. Candidate bands are segmented into character cells on the OCR-B pitch and matched against glyph templates; competing grid placements are judged by how well the templates actually fit. Uncertain characters are then resolved by the MRZ's own structure: check digits, character classes per position, and date plausibility arbitrate between close candidates. All formats from ICAO 9303 are supported: TD1, TD2, TD3, MRV-A and MRV-B.

## Limits

The recognizer only knows the 37-character MRZ alphabet in OCR-B; it is not a general OCR. Glyphs below roughly 6 px of pitch are at the edge of what template matching can do. Tilt up to about 8 degrees is detected and corrected automatically, on top of the 90/180/270 degree orientations; extreme perspective is not. Chip reading and the visual inspection zone are out of scope.

## License

MIT
