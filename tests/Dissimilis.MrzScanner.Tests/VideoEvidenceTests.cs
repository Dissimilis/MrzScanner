using Dissimilis.MrzScanner.Internal;
using Xunit;

namespace Dissimilis.MrzScanner.Tests;

public class VideoEvidenceTests
{
    private static MrzResult Frame(string[] lines, BandRead? evidence = null, double confidence = 0.8)
    {
        MrzResult parsed = MrzParser.ParseText(string.Join("\n", lines));
        return new MrzResult(parsed.MrzFound, parsed.Document, parsed.Raw, parsed.Checks, parsed.Issues,
            confidence, evidence: evidence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    public void Alternative_evidence_recovers_complementary_misreads(int seed)
    {
        var soft = new MrzVideoSession();
        var hard = new MrzVideoSession();
        for (int frame = 0; frame < 2; frame++)
        {
            string[] lines = (string[])RecognitionEvidenceTests.Passport.Clone();
            // Each frame chooses a different wrong glyph. The correct glyph
            // is a close runner-up in both, so hard voting cannot recover it.
            char wrong = frame == 0 ? 'K' : 'J';
            lines[1] = wrong + lines[1].Substring(1);
            BandRead evidence = RecognitionEvidenceTests.Band(lines);
            evidence.Lines[1][0] = new CellRead(new[] { wrong, 'L' },
                new[] { 0.81f + seed * 0.001f, 0.80f }, Array.Empty<float>());
            Assert.False(Frame(lines).IsValid);
            soft.Fold(Frame(lines, evidence));
            hard.Fold(Frame(lines));
        }
        Assert.False(hard.Best!.IsValid);
        Assert.True(soft.IsStable);
        Assert.Equal(RecognitionEvidenceTests.Passport, soft.Best!.Raw!.Lines);
        Assert.NotNull(soft.Best.FieldConfidence);
    }

    [Fact]
    public void A_new_verified_document_starts_fresh_corroboration()
    {
        var session = new MrzVideoSession();
        session.Fold(Frame(RecognitionEvidenceTests.Passport));
        session.Fold(Frame(RecognitionEvidenceTests.Passport));
        Assert.True(session.IsStable);
        string[] other = (string[])RecognitionEvidenceTests.Passport.Clone();
        char[] row = other[1].ToCharArray();
        row[0] = 'M';
        row[9] = (char)('0' + CheckDigit.Compute(new string(row, 0, 9)));
        row[43] = (char)('0' + CheckDigit.Compute(new[]
        {
            new string(row, 0, 10), new string(row, 13, 7),
            new string(row, 21, 7), new string(row, 28, 15),
        }));
        other[1] = new string(row);
        Assert.True(Frame(other).IsValid, string.Join("; ", Frame(other).Issues));
        session.Fold(Frame(other));
        Assert.False(session.IsStable);
        Assert.Equal("L898902C3", session.Best!.Document!.DocumentNumber);
        session.Fold(Frame(other));
        Assert.True(session.IsStable);
        Assert.Equal("M898902C3", session.Best!.Document!.DocumentNumber);
        Assert.Equal(4, session.FramesSeen);
    }

    [Fact]
    public void A_checksum_collision_does_not_erase_prior_sightings()
    {
        var session = new MrzVideoSession();
        string[] other = (string[])RecognitionEvidenceTests.Passport.Clone();
        other[1] = 'V' + other[1].Substring(1);
        session.Fold(Frame(RecognitionEvidenceTests.Passport));
        session.Fold(Frame(other));
        Assert.False(session.IsStable);
        session.Fold(Frame(RecognitionEvidenceTests.Passport));
        Assert.True(session.IsStable);
        Assert.Equal("L898902C3", session.Best!.Document!.DocumentNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fusion_obeys_grammar_and_isolates_the_next_document(bool ambiguousName)
    {
        var session = new MrzVideoSession();
        foreach (char wrong in new[] { 'K', 'J' })
        {
            string[] lines = (string[])RecognitionEvidenceTests.Passport.Clone();
            lines[1] = wrong + lines[1].Substring(1);
            BandRead evidence = RecognitionEvidenceTests.Band(lines);
            evidence.Lines[1][0] = new CellRead(new[] { wrong, 'L' }, new[] { .81f, .80f }, Array.Empty<float>());
            if (ambiguousName)
                evidence.Lines[0][11] = new CellRead(new[] { '0', 'O' }, new[] { .86f, .85f }, Array.Empty<float>());
            session.Fold(Frame(lines, evidence));
        }
        Assert.True(session.IsStable);
        Assert.Equal(RecognitionEvidenceTests.Passport, session.Best!.Raw!.Lines);
        string[] other = (string[])RecognitionEvidenceTests.Passport.Clone();
        other[1] = 'V' + other[1].Substring(1);
        session.Fold(Frame(other));
        Assert.False(session.IsStable);
        Assert.Equal("L898902C3", session.Best.Document!.DocumentNumber);
        session.Fold(Frame(other));
        Assert.True(session.IsStable);
        Assert.Equal("V898902C3", session.Best.Document!.DocumentNumber);
    }

    [Fact]
    public void Fusion_does_not_invent_unobserved_checksum_candidates()
    {
        var session = new MrzVideoSession();
        string[] lines = (string[])RecognitionEvidenceTests.Passport.Clone();
        lines[1] = 'K' + lines[1].Substring(1);
        for (int i = 0; i < 20; i++)
            session.Fold(Frame(lines, RecognitionEvidenceTests.Band(lines)));
        Assert.False(session.IsStable);
        Assert.False(session.Best!.IsValid);
    }

    [Fact]
    public void Fusion_can_corroborate_more_than_sixteen_frames()
    {
        var session = new MrzVideoSession(new MrzScannerOptions(), stableFrames: 18);
        for (int frame = 0; frame < 18; frame++)
        {
            string[] lines = (string[])RecognitionEvidenceTests.Passport.Clone();
            char wrong = frame % 2 == 0 ? 'K' : 'J';
            lines[1] = wrong + lines[1].Substring(1);
            BandRead evidence = RecognitionEvidenceTests.Band(lines);
            evidence.Lines[1][0] = new CellRead(new[] { wrong, 'L' }, new[] { 0.81f, 0.8f }, Array.Empty<float>());
            session.Fold(Frame(lines, evidence));
            if (frame < 17) Assert.False(session.IsStable);
        }
        Assert.True(session.IsStable);
    }

    [Fact]
    public void Partially_valid_different_documents_cannot_create_a_stable_chimera()
    {
        string[] first = (string[])RecognitionEvidenceTests.Passport.Clone();
        string[] second = (string[])RecognitionEvidenceTests.Passport.Clone();
        char[] a = first[1].ToCharArray(), b = second[1].ToCharArray();
        a[13] = '8'; // Incorrect birth digit, original checksum retained.
        b[0] = 'V'; // L and V differ by ten in checksum value: same check digit.
        b[21] = '2'; // Incorrect expiry digit, original checksum retained.
        first[1] = new string(a);
        second[1] = new string(b);
        Assert.Equal(CheckDigitStatus.Valid, Frame(first).Checks.DocumentNumber);
        Assert.Equal(CheckDigitStatus.Valid, Frame(second).Checks.DocumentNumber);
        Assert.False(Frame(first).IsValid);
        Assert.False(Frame(second).IsValid);
        var session = new MrzVideoSession();
        session.Fold(Frame(first));
        session.Fold(Frame(second));
        Assert.False(session.IsStable);
        Assert.False(session.Best!.IsValid);
    }
}
