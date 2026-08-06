using System.Collections.Generic;

namespace Dissimilis.MrzScanner;

/// <summary>Parses MRZ text that has already been extracted from a document.</summary>
public interface IMrzParser
{
    /// <summary>Parses MRZ text with lines separated by newlines.</summary>
    /// <param name="mrzText">The MRZ lines. Must not be null.</param>
    MrzResult Parse(string mrzText);

    /// <summary>Parses MRZ lines.</summary>
    /// <param name="lines">The MRZ lines. Must not be null.</param>
    MrzResult Parse(IReadOnlyList<string> lines);
}
