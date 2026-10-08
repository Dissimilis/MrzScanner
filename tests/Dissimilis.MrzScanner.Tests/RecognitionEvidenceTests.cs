using System.Reflection;
using Dissimilis.MrzScanner.Internal;
using Xunit;

namespace Dissimilis.MrzScanner.Tests;

public class RecognitionEvidenceTests
{
    internal static readonly string[] Passport =
    {
        "P<UTOERIKSSON<<ANNA<MARIA".PadRight(44, '<'),
        "L898902C36UTO7408122F1204159ZE184226B<<<<<10",
    };

    internal static BandRead Band(string[] lines, float score = 0.99f) => new(
        lines.Select(line => line.Select(c => new CellRead(new[] { c }, new[] { score },
            new float[OcrTemplates.Width * OcrTemplates.Height])).ToList()).ToList(), score, 0);

    internal static string[] Extended(MrzFormat format)
    {
        var rows = Enumerable.Range(0, format.LineCount)
            .Select(_ => new string('<', format.LineLength).ToCharArray()).ToArray();
        void Set(FieldId id, string value)
        {
            FieldDef field = format.Field(id)!;
            value.CopyTo(0, rows[field.Line], field.Start, value.Length);
        }
        const string number = "D23145890123";
        Set(FieldId.DocumentCode, "I<");
        Set(FieldId.IssuingCountry, "UTO");
        Set(FieldId.Nationality, "UTO");
        Set(FieldId.Name, "ERIKSSON<<ANNA<MARIA");
        Set(FieldId.DocumentNumber, number.Substring(0, 9));
        Set(FieldId.OptionalData1, number.Substring(9) + CheckDigit.Compute(number));
        Set(FieldId.BirthDate, "740812");
        Set(FieldId.ExpiryDate, "120415");
        Set(FieldId.Sex, "F");
        foreach (CheckRelation relation in format.Checks)
        {
            if (relation.CheckField == FieldId.DocumentNumberCheck) continue;
            int check = CheckDigit.Compute(relation.Protects.Select(p =>
                new string(rows[p.Line], p.Start, p.Length)).ToArray());
            Set(relation.CheckField, check.ToString());
        }
        return rows.Select(row => new string(row)).ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Arbitration_preserves_extended_number_marker(bool td2)
    {
        MrzFormat format = td2 ? MrzFormat.Td2 : MrzFormat.Td1;
        string[] lines = Extended(format);
        Assert.True(MrzParser.ParseText(string.Join("\n", lines)).IsValid);
        BandRead band = Band(lines);
        FieldDef marker = format.Field(FieldId.DocumentNumberCheck)!;
        band.Lines[marker.Line][marker.Start] = new CellRead(
            new[] { '<', (char)('0' + CheckDigit.Compute("D23145890")) },
            new[] { 0.80f, 0.79f }, new float[OcrTemplates.Width * OcrTemplates.Height]);
        ChecksumArbitrator.Arbitrate(band, format);
        MrzResult parsed = MrzParser.ParseText(string.Join("\n", band.AllText()));
        Assert.Equal('<', band.Lines[marker.Line][marker.Start].Chosen);
        Assert.Equal("D23145890123", parsed.Document!.DocumentNumber);
        Assert.True(parsed.IsValid);
    }

    [Fact]
    public void Adaptive_donor_does_not_increase_its_own_score()
    {
        var bitmap = new float[OcrTemplates.Width * OcrTemplates.Height];
        bitmap[0] = 1;
        bitmap[1] = -1;
        OcrTemplates.Normalize(bitmap);
        var cell = new CellRead(new[] { 'A', 'B' }, new[] { 0.80f, 0.60f }, bitmap);
        var band = new BandRead(new List<List<CellRead>> { new() { cell } }, 0.8, 0);
        AdaptiveRefiner.Refine(band, MrzFormat.Td3);
        Assert.Equal(0.80f, cell.ChosenScore, 5);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void Extended_number_can_recover_a_false_terminator(bool td2, int offset)
    {
        MrzFormat format = td2 ? MrzFormat.Td2 : MrzFormat.Td1;
        string[] lines = Extended(format);
        BandRead band = Band(lines);
        FieldDef optional = format.Field(FieldId.OptionalData1)!;
        char correct = lines[optional.Line][optional.Start + offset];
        band.Lines[optional.Line][optional.Start + offset] = new CellRead(
            new[] { '<', correct }, new[] { .80f, .79f }, Array.Empty<float>());
        ChecksumArbitrator.Arbitrate(band, format, applyGrammar: false);
        MrzResult result = MrzParser.ParseText(string.Join("\n", band.AllText()));
        Assert.True(result.IsValid);
        Assert.Equal("D23145890123", result.Document!.DocumentNumber);
    }

    [Fact]
    public void Repeated_checksum_search_counts_a_retained_substitution_once()
    {
        BandRead band = Band(Passport);
        var cell = new CellRead(new[] { 'K', 'L' }, new[] { .81f, .80f }, Array.Empty<float>());
        band.Lines[1][0] = cell;
        ChecksumArbitrator.Arbitrate(band, MrzFormat.Td3, applyGrammar: false);
        Assert.Equal('L', cell.Chosen);
        cell.Chosen = 'K';
        cell.ChosenScore = .81f;
        ChecksumArbitrator.Arbitrate(band, MrzFormat.Td3, applyGrammar: false);
        Assert.Equal(2, band.Coercions);
        Assert.Equal(1, band.CorrectionCount);
        cell.Chosen = 'K';
        Assert.Equal(0, band.CorrectionCount);
    }

    [Fact]
    public void Pixel_quality_does_not_depend_on_checksums()
    {
        static double Quality(BandRead band)
        {
            MethodInfo parse = typeof(MrzPipeline).GetMethod("ParseBand", BindingFlags.NonPublic | BindingFlags.Static)!;
            object?[] args = { band, MrzFormat.Td3, new MrzParser(), null, 0d };
            parse.Invoke(null, args);
            return (double)args[4]!;
        }
        BandRead valid = Band(Passport, 0.8f);
        BandRead invalid = Band(Passport, 0.8f);
        invalid.Lines[1][9].Chosen = '0';
        Assert.Equal(Quality(valid), Quality(invalid), 8);
    }

    [Fact]
    public void Unsupported_sex_is_reported_instead_of_silently_replaced()
    {
        BandRead band = Band(Passport);
        band.Lines[1][20] = new CellRead(new[] { 'X', 'M' }, new[] { 0.99f, 0.50f },
            new float[OcrTemplates.Width * OcrTemplates.Height]);
        ChecksumArbitrator.Arbitrate(band, MrzFormat.Td3);
        Assert.Equal('X', band.Lines[1][20].Chosen);
        Assert.False(MrzParser.ParseText(string.Join("\n", band.AllText())).IsValid);
    }

    [Theory]
    [InlineData(false, MrzSearchEffort.SingleFrame)]
    [InlineData(false, MrzSearchEffort.Exhaustive)]
    [InlineData(true, MrzSearchEffort.SingleFrame)]
    public void Found_gate_requires_checksum_evidence(bool valid, MrzSearchEffort effort)
    {
        BandRead band = Band(Passport, valid ? .80f : .95f);
        if (!valid)
        {
            foreach (int position in new[] { 9, 19, 27, 42, 43 })
            {
                CellRead cell = band.Lines[1][position];
                cell.Chosen = (char)('0' + (cell.Chosen - '0' + 1) % 10);
            }
        }
        MethodInfo parse = typeof(MrzPipeline).GetMethod("ParseBand", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] args = { band, MrzFormat.Td3, new MrzParser(), null, 0d };
        var parsed = (MrzResult)parse.Invoke(null, args)!;
        Assert.Equal(valid, parsed.IsValid);
        if (!valid)
            Assert.All(new[] { parsed.Checks.DocumentNumber, parsed.Checks.BirthDate,
                parsed.Checks.ExpiryDate, parsed.Checks.OptionalData, parsed.Checks.Composite },
                check => Assert.Equal(CheckDigitStatus.Invalid, check));
        Type statsType = typeof(MrzPipeline).GetNestedType("CaptureStats", BindingFlags.NonPublic)!;
        object stats = Activator.CreateInstance(statsType)!;
        Type rankedType = typeof(MrzPipeline).GetNestedType("Ranked", BindingFlags.NonPublic)!;
        object ranked = Activator.CreateInstance(rankedType, parsed, .95, 0, null, args[4], stats)!;
        MethodInfo gate = typeof(MrzPipeline).GetMethod("GateWeakFound", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (MrzResult)gate.Invoke(null, new[] { ranked, (object)effort })!;
        Assert.Equal(valid, result.MrzFound);
        if (valid) Assert.Empty(result.CaptureHints);
    }

    [Fact]
    public void Glare_outside_the_candidate_does_not_contaminate_capture_guidance()
    {
        var image = new GrayImage(300, 100);
        Array.Fill(image.Pixels, (byte)255);
        for (int y = 20; y < 80; y++)
            for (int x = 80; x < 220; x++)
                image.Pixels[y * image.Width + x] = (byte)(x % 2 == 0 ? 60 : 220);
        var candidate = new BandCandidate(20, 80, 80, 220, 2, 1);
        MethodInfo measure = typeof(MrzPipeline).GetMethod("ComputeStats", BindingFlags.NonPublic | BindingFlags.Static)!;
        object stats = measure.Invoke(null, new object[] { candidate, image, 140 })!;
        double glare = (double)stats.GetType().GetField("GlareFraction")!.GetValue(stats)!;
        Assert.Equal(0, glare);
    }

    [Fact]
    public void Character_scores_are_read_only_and_keep_weak_cells_visible()
    {
        BandRead band = Band(Passport);
        band.Lines[0][5].ChosenScore = 0.4f;
        var scores = MrzResult.ScoresFor(band);
        Assert.Equal(0.4, scores[0][5], 6);
        Assert.Equal(0.99, scores[0][6], 6);
        Assert.Throws<NotSupportedException>(() => ((IList<double>)scores[0])[5] = 1);
        band.Lines[0][5].ChosenScore = 1;
        Assert.Equal(0.4, scores[0][5], 6);
    }

    [Fact]
    public void A_public_reference_date_makes_archived_parsing_deterministic()
    {
        string[] lines = (string[])Passport.Clone();
        char[] row = lines[1].ToCharArray();
        "260901".CopyTo(0, row, 13, 6);
        row[19] = (char)('0' + CheckDigit.Compute("260901"));
        lines[1] = new string(row);
        Assert.Equal(1926, new MrzParser(new DateTime(2026, 8, 1)).Parse(lines).Document!.BirthDate.Year);
        Assert.Equal(2026, new MrzParser(new DateTime(2026, 10, 1)).Parse(lines).Document!.BirthDate.Year);
    }

    [Theory]
    [InlineData("740431", false)]
    [InlineData("740430", true)]
    [InlineData("000229", true)]
    [InlineData("990229", false)]
    [InlineData("74<<<<", true)]
    [InlineData("740<30", false)]
    public void Arbitration_uses_calendar_dates_and_preserves_unknown_pairs(string date, bool expected)
    {
        BandRead band = Band(Passport);
        for (int i = 0; i < 6; i++) band.Lines[1][13 + i].Chosen = date[i];
        var check = MrzFormat.Td3.Checks.Single(c => c.CheckField == FieldId.BirthDateCheck);
        MethodInfo plausible = typeof(ChecksumArbitrator).GetMethod("DatesPlausible", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool actual = (bool)plausible.Invoke(null, new object[] { band, MrzFormat.Td3, check })!;
        Assert.Equal(expected, actual);
    }
}
