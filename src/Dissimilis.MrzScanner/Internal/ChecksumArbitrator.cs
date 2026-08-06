namespace Dissimilis.MrzScanner.Internal;

/// <summary>
/// Improves recognized text using the MRZ structure: forces characters into the
/// class their position allows, then resolves failed check digits by trying
/// alternate candidates for the least confident positions.
/// </summary>
internal static class ChecksumArbitrator
{
    private const int MaxSearchPositions = 8;
    private const int MaxCandidatesPerPosition = 4;
    private const float CandidateScoreMargin = 0.35f;

    public static void Arbitrate(BandRead band, MrzFormat format, CancellationToken ct = default)
    {
        ApplyGrammar(band, format);

        var passing = new HashSet<FieldId>();
        foreach (CheckRelation relation in format.Checks)
        {
            ct.ThrowIfCancellationRequested();
            if (relation.CheckField == FieldId.CompositeCheck)
                continue;
            if (TrySatisfy(band, format, relation, mustKeepPassing: null, excludePositionsOf: null))
                passing.Add(relation.CheckField);
        }

        foreach (CheckRelation relation in format.Checks)
        {
            ct.ThrowIfCancellationRequested();
            if (relation.CheckField != FieldId.CompositeCheck)
                continue;

            // Composite: first touch only positions no passing field check
            // protects. If that fails, a field fix was probably a compensating
            // error; retry wider but keep the passing checks passing.
            if (!TrySatisfy(band, format, relation, mustKeepPassing: passing, excludePositionsOf: passing))
                TrySatisfy(band, format, relation, mustKeepPassing: passing, excludePositionsOf: null);
        }
    }

    private static void ApplyGrammar(BandRead band, MrzFormat format)
    {
        ApplyDocumentCodePrior(band, format);
        ApplyCountryPrior(band, format, FieldId.IssuingCountry);
        ApplyCountryPrior(band, format, FieldId.Nationality);
        for (int line = 0; line < band.Lines.Count; line++)
        {
            List<CellRead> cells = band.Lines[line];
            for (int x = 0; x < cells.Count; x++)
            {
                CellRead cell = cells[x];
                CharClass allowed = format.ClassAt(line, x);
                if (IsAllowed(cell.Chosen, allowed))
                    continue;
                bool found = false;
                for (int k = 0; k < cell.Chars.Length; k++)
                {
                    if (IsAllowed(cell.Chars[k], allowed))
                    {
                        cell.Chosen = cell.Chars[k];
                        cell.ChosenScore = cell.Scores[k];
                        found = true;
                        break;
                    }
                }
                if (!found)
                    ClassifyWithinClass(cell, allowed);
            }
            ApplyDigitPreference(cells, format, line);
            ApplyTrailingFillerPrior(cells, format, line);
        }
    }

    /// <summary>
    /// An isolated non-filler inside a trailing filler run is almost always a
    /// misread chevron (real initials carry at most one filler on the left).
    /// Flips only close races, so a decisive read survives.
    /// </summary>
    private static void ApplyTrailingFillerPrior(List<CellRead> cells, MrzFormat format, int line)
    {
        for (int x = cells.Count - 1; x >= 2; x--)
        {
            CellRead cell = cells[x];
            if (cell.Chosen == '<')
                continue;
            if (!IsAllowed('<', format.ClassAt(line, x)))
                break;
            if (cells[x - 1].Chosen != '<' || cells[x - 2].Chosen != '<')
                break;
            // On a name line the final cell is unconditional: no valid name
            // ends with a single letter after a filler run (names truncate to
            // a full final letter only when no fillers precede it), so
            // whatever the window sampled there, edge junk included, it is a
            // chevron. Optional data lines carry no such guarantee: a lone
            // digit after fillers is perfectly legal there.
            bool unconditional = x == cells.Count - 1 && format.ClassAt(line, x) == CharClass.Alpha;
            float fillerScore = cell.ScoreAgainst('<');
            if (!unconditional && fillerScore < cell.ChosenScore - 0.25f)
                break;
            cell.Chosen = '<';
            cell.ChosenScore = Math.Max(0.25f, fillerScore);
        }
    }

    /// <summary>
    /// First char of the document code has a hard ICAO prior and no check
    /// digit; snap it to the best allowed candidate.
    /// </summary>
    private static void ApplyDocumentCodePrior(BandRead band, MrzFormat format)
    {
        CellRead cell = band.Lines[0][0];
        char[] allowed = format.AllowedCodeFirstChars;
        if (Array.IndexOf(allowed, cell.Chosen) >= 0)
            return;
        char bestChar = allowed[0];
        float bestScore = float.MinValue;
        foreach (char c in allowed)
        {
            float score = cell.ScoreAgainst(c);
            if (score > bestScore)
            {
                bestScore = score;
                bestChar = c;
            }
        }
        cell.Chosen = bestChar;
        cell.ChosenScore = Math.Max(0f, bestScore);
    }

    /// <summary>
    /// O/0 and I/1 default to the digit in mixed fields when scores are
    /// close; digits dominate there and a check digit still gets a say.
    /// </summary>
    private static void ApplyDigitPreference(List<CellRead> cells, MrzFormat format, int line)
    {
        for (int x = 0; x < cells.Count; x++)
        {
            if (format.ClassAt(line, x) != CharClass.Any)
                continue;
            CellRead cell = cells[x];
            char digit = cell.Chosen switch
            {
                'O' => '0',
                'I' => '1',
                _ => '\0',
            };
            if (digit == '\0')
                continue;
            float digitScore = cell.ScoreAgainst(digit);
            if (digitScore >= cell.ChosenScore - 0.08f)
            {
                cell.Chosen = digit;
                cell.ChosenScore = digitScore;
            }
        }
    }

    /// <summary>
    /// Country fields have no check digit but a closed value set. Snap an
    /// invalid read to the best matching real code, unless even that fits
    /// far worse than the raw cells.
    /// </summary>
    private static void ApplyCountryPrior(BandRead band, MrzFormat format, FieldId fieldId)
    {
        FieldDef? field = format.Field(fieldId);
        if (field is null || field.Length != 3)
            return;
        CellRead[] cells =
        {
            band.Lines[field.Line][field.Start],
            band.Lines[field.Line][field.Start + 1],
            band.Lines[field.Line][field.Start + 2],
        };
        string current = new(new[] { cells[0].Chosen, cells[1].Chosen, cells[2].Chosen });
        if (CountryCodes.IsValid(current))
            return;

        float currentScore = cells[0].ChosenScore + cells[1].ChosenScore + cells[2].ChosenScore;
        string? bestCode = null;
        float bestScore = float.MinValue;
        foreach (string code in CountryCodes.All)
        {
            float score = cells[0].ScoreAgainst(code[0]) +
                          cells[1].ScoreAgainst(code[1]) +
                          cells[2].ScoreAgainst(code[2]);
            if (score > bestScore)
            {
                bestScore = score;
                bestCode = code;
            }
        }

        if (bestCode is null || bestScore < currentScore - 0.45f)
            return;
        for (int i = 0; i < 3; i++)
        {
            cells[i].Chosen = bestCode[i];
            cells[i].ChosenScore = cells[i].ScoreAgainst(bestCode[i]);
        }
    }

    /// <summary>Classify directly over the allowed subset when no stored candidate fits the class.</summary>
    private static void ClassifyWithinClass(CellRead cell, CharClass allowed)
    {
        string alphabet = allowed switch
        {
            CharClass.Alpha => "ABCDEFGHIJKLMNOPQRSTUVWXYZ<",
            CharClass.Digit => "0123456789",
            CharClass.DigitOrFiller => "0123456789<",
            CharClass.SexChar => "MF<",
            _ => "",
        };
        if (alphabet.Length == 0)
            return;
        char bestChar = alphabet[0];
        float bestScore = float.MinValue;
        foreach (char c in alphabet)
        {
            float score = cell.ScoreAgainst(c);
            if (score > bestScore)
            {
                bestScore = score;
                bestChar = c;
            }
        }
        cell.Chosen = bestChar;
        cell.ChosenScore = bestScore;
    }

    internal static bool IsAllowed(char c, CharClass allowed) => allowed switch
    {
        CharClass.Alpha => c is (>= 'A' and <= 'Z') or '<',
        CharClass.Digit => c is >= '0' and <= '9',
        CharClass.DigitOrFiller => c is (>= '0' and <= '9') or '<',
        CharClass.SexChar => c is 'M' or 'F' or '<',
        _ => true,
    };

    private static bool TrySatisfy(
        BandRead band,
        MrzFormat format,
        CheckRelation relation,
        HashSet<FieldId>? mustKeepPassing,
        HashSet<FieldId>? excludePositionsOf)
    {
        FieldDef? checkField = format.Field(relation.CheckField);
        if (checkField is null)
            return false;

        // Gather the protected cells plus the check cell itself.
        var positions = new List<(int Line, int Position)>();
        foreach ((int line, int start, int length) in relation.Protects)
        {
            for (int i = 0; i < length; i++)
                positions.Add((line, start + i));
        }
        var checkPosition = (checkField.Line, checkField.Start);

        if (IsSatisfied(band, positions, checkPosition, relation) && DatesPlausible(band, format, relation))
            return true;

        // Search alternates for the least confident positions.
        var searchable = new List<(int Line, int Position)>(positions) { checkPosition };
        if (excludePositionsOf is not null)
        {
            var frozen = new HashSet<(int Line, int Position)>();
            foreach (CheckRelation other in format.Checks)
            {
                if (!excludePositionsOf.Contains(other.CheckField))
                    continue;
                FieldDef? otherCheck = format.Field(other.CheckField);
                if (otherCheck is not null)
                    frozen.Add((otherCheck.Line, otherCheck.Start));
                foreach ((int line, int start, int length) in other.Protects)
                {
                    for (int i = 0; i < length; i++)
                        frozen.Add((line, start + i));
                }
            }
            searchable.RemoveAll(frozen.Contains);
        }
        searchable.Sort((a, b) =>
            band.Lines[a.Line][a.Position].ChosenScore.CompareTo(band.Lines[b.Line][b.Position].ChosenScore));
        if (searchable.Count > MaxSearchPositions)
            searchable.RemoveRange(MaxSearchPositions, searchable.Count - MaxSearchPositions);

        var options = new List<(char Char, float Score)>[searchable.Count];
        for (int i = 0; i < searchable.Count; i++)
        {
            (int line, int x) = searchable[i];
            CellRead cell = band.Lines[line][x];
            CharClass allowed = format.ClassAt(line, x);
            var list = new List<(char, float)>();
            for (int k = 0; k < cell.Chars.Length && list.Count < MaxCandidatesPerPosition; k++)
            {
                // Only candidates scoring close to the cell's best are eligible.
                // A genuine near-miss has runners-up within this margin; garbage
                // cells have scattered scores, and without the gate the mod 10
                // search can manufacture "valid" check digits on misreads of the
                // wrong format.
                if (IsAllowed(cell.Chars[k], allowed) && cell.Scores[k] >= cell.Scores[0] - CandidateScoreMargin)
                    list.Add((cell.Chars[k], cell.Scores[k]));
            }
            if (list.Count == 0)
                list.Add((cell.Chosen, cell.ChosenScore));
            options[i] = list;
        }

        // On blurry crops the correct digit can rank below every gated
        // candidate, and date fields are where check failures concentrate.
        // Date positions are structurally constrained (month tens is 0 or 1,
        // day tens 0 to 3), so their search runs over every structurally legal
        // digit scored against the cell, making it complete over the dates;
        // the joint score still prefers assignments the pixels support.
        for (int i = 0; i < searchable.Count; i++)
        {
            (int line, int x) = searchable[i];
            string? structural = StructuralDateDigits(format, line, x);
            if (structural is null)
                continue;
            CellRead cell = band.Lines[line][x];
            var digits = new List<(char Char, float Score)>(structural.Length);
            foreach (char d in structural)
                digits.Add((d, cell.ScoreAgainst(d)));
            digits.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (digits.Count > 6)
                digits.RemoveRange(6, digits.Count - 6);
            options[i] = digits;
        }

        // The check equation is linear modulo 10, so the weighted sum threads
        // through the recursion incrementally and every leaf test is constant
        // time; recomputing the full sum per leaf dominated the whole reader.
        // Precompute each searchable position's weight, the fixed sum of the
        // non searched protected cells, and how many of them are non fillers.
        var weightOf = new int[searchable.Count];
        int checkIndex = -1;
        for (int i = 0; i < searchable.Count; i++)
        {
            if (searchable[i] == checkPosition)
            {
                checkIndex = i;
                continue;
            }
            weightOf[i] = WeightAt(positions.IndexOf(searchable[i]));
        }
        int fixedSum = 0;
        int fixedNonFillers = 0;
        var searchSet = new HashSet<(int, int)>(searchable);
        for (int i = 0; i < positions.Count; i++)
        {
            (int line, int x) = positions[i];
            if (searchSet.Contains((line, x)))
                continue;
            int value = CheckDigit.CharValue(band.Lines[line][x].Chosen);
            if (value < 0)
                return false;
            fixedSum += value * WeightAt(i);
            if (band.Lines[line][x].Chosen != '<')
                fixedNonFillers++;
        }
        char fixedCheckChar = searchSet.Contains(checkPosition)
            ? '\0'
            : band.Lines[checkPosition.Line][checkPosition.Start].Chosen;

        var current = new char[searchable.Count];
        var bestAssignment = new char[searchable.Count];
        double bestScore = double.MinValue;
        bool found = false;

        void Search(int depth, double score, int sum, int nonFillers, char checkChar)
        {
            if (depth == searchable.Count)
            {
                bool satisfied;
                if (checkChar == '<')
                    satisfied = relation.FillerAllowed && nonFillers == 0;
                else if (checkChar is >= '0' and <= '9')
                    satisfied = sum % 10 == checkChar - '0';
                else
                    satisfied = false;
                if (!satisfied || score <= bestScore)
                    return;

                for (int i = 0; i < searchable.Count; i++)
                {
                    (int line, int x) = searchable[i];
                    band.Lines[line][x].Chosen = current[i];
                }
                if (DatesPlausible(band, format, relation) &&
                    (mustKeepPassing is null || RelationsStillPass(band, format, mustKeepPassing)))
                {
                    bestScore = score;
                    Array.Copy(current, bestAssignment, current.Length);
                    found = true;
                }
                return;
            }
            if (depth == checkIndex)
            {
                foreach ((char c, float s) in options[depth])
                {
                    current[depth] = c;
                    Search(depth + 1, score + s, sum, nonFillers, c);
                }
                return;
            }
            int weight = weightOf[depth];
            foreach ((char c, float s) in options[depth])
            {
                int value = CheckDigit.CharValue(c);
                if (value < 0)
                    continue;
                current[depth] = c;
                Search(depth + 1, score + s, sum + value * weight, nonFillers + (c == '<' ? 0 : 1), checkChar);
            }
        }

        // Remember the starting assignment so a fruitless search leaves everything unchanged.
        var original = new (char Chosen, float Score)[searchable.Count];
        for (int i = 0; i < searchable.Count; i++)
        {
            (int line, int x) = searchable[i];
            original[i] = (band.Lines[line][x].Chosen, band.Lines[line][x].ChosenScore);
        }

        Search(0, 0, fixedSum, fixedNonFillers, fixedCheckChar);

        for (int i = 0; i < searchable.Count; i++)
        {
            (int line, int x) = searchable[i];
            CellRead cell = band.Lines[line][x];
            if (found)
            {
                if (bestAssignment[i] != original[i].Chosen)
                    band.Coercions++;
                cell.Chosen = bestAssignment[i];
                cell.ChosenScore = cell.ScoreFor(bestAssignment[i]);
            }
            else
            {
                cell.Chosen = original[i].Chosen;
                cell.ChosenScore = original[i].Score;
            }
        }
        return found;
    }

    /// <summary>
    /// The structurally legal digits for a date field position, or null when
    /// the position is not inside a date field.
    /// </summary>
    private static string? StructuralDateDigits(MrzFormat format, int line, int x)
    {
        foreach (FieldId id in new[] { FieldId.BirthDate, FieldId.ExpiryDate })
        {
            FieldDef? field = format.Field(id);
            if (field is null || field.Line != line || x < field.Start || x >= field.Start + field.Length)
                continue;
            return (x - field.Start) switch
            {
                2 => "01",
                4 => "0123",
                _ => "0123456789",
            };
        }
        return null;
    }

    /// <summary>
    /// Validates that the date fields covered by a relation form plausible MRZ
    /// dates. Check digits alone cannot see that a coerced digit produced
    /// month 15; solving both constraints jointly steers the search to the
    /// true characters.
    /// </summary>
    private static bool DatesPlausible(BandRead band, MrzFormat format, CheckRelation relation)
    {
        foreach (FieldId id in new[] { FieldId.BirthDate, FieldId.ExpiryDate })
        {
            FieldDef? field = format.Field(id);
            if (field is null)
                continue;
            bool covered = false;
            foreach ((int line, int start, int length) in relation.Protects)
            {
                if (line == field.Line && field.Start >= start && field.Start + field.Length <= start + length)
                {
                    covered = true;
                    break;
                }
            }
            if (!covered)
                continue;

            var chars = new char[6];
            for (int i = 0; i < 6; i++)
                chars[i] = band.Lines[field.Line][field.Start + i].Chosen;
            if (!PlausiblePair(chars[0], chars[1], 0, 99) ||
                !PlausiblePair(chars[2], chars[3], 1, 12) ||
                !PlausiblePair(chars[4], chars[5], 1, 31))
            {
                return false;
            }
        }
        return true;
    }

    private static bool PlausiblePair(char a, char b, int min, int max)
    {
        if (a == '<' && b == '<')
            return true;
        if (a is < '0' or > '9' || b is < '0' or > '9')
            return false;
        int value = (a - '0') * 10 + (b - '0');
        return value >= min && value <= max;
    }

    private static bool RelationsStillPass(BandRead band, MrzFormat format, HashSet<FieldId> relations)
    {
        foreach (CheckRelation relation in format.Checks)
        {
            if (!relations.Contains(relation.CheckField))
                continue;
            FieldDef? checkField = format.Field(relation.CheckField);
            if (checkField is null)
                continue;
            var positions = new List<(int Line, int Position)>();
            foreach ((int line, int start, int length) in relation.Protects)
            {
                for (int i = 0; i < length; i++)
                    positions.Add((line, start + i));
            }
            if (!IsSatisfied(band, positions, (checkField.Line, checkField.Start), relation))
                return false;
        }
        return true;
    }

    private static bool IsSatisfied(
        BandRead band,
        List<(int Line, int Position)> positions,
        (int Line, int Position) checkPosition,
        CheckRelation relation)
    {
        char checkChar = band.Lines[checkPosition.Line][checkPosition.Position].Chosen;
        int sum = 0;
        int index = 0;
        bool allFiller = true;
        foreach ((int line, int x) in positions)
        {
            int value = CheckDigit.CharValue(band.Lines[line][x].Chosen);
            if (value < 0)
                return false;
            if (band.Lines[line][x].Chosen != '<')
                allFiller = false;
            sum += value * WeightAt(index);
            index++;
        }

        if (checkChar == '<')
            return relation.FillerAllowed && allFiller;
        if (checkChar is < '0' or > '9')
            return false;
        return sum % 10 == checkChar - '0';
    }

    private static int WeightAt(int index) => (index % 3) switch
    {
        0 => 7,
        1 => 3,
        _ => 1,
    };
}
