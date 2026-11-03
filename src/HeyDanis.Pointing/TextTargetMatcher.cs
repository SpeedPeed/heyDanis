using System.Drawing;
using System.Globalization;
using System.Text;
using HeyDanis.Capture;

namespace HeyDanis.Pointing;

/// <summary>
/// Finds the on-screen text Claude means (its label, e.g. "Browwser" or "Datei") among the
/// words Windows OCR recognized, and returns that text's exact box. Claude's own coordinates
/// are only used to choose between several equal matches (nearest one wins).
/// </summary>
public static class TextTargetMatcher
{
    /// <summary>Longest phrase (in words) we try to match.</summary>
    private const int MaxPhraseWords = 6;

    /// <summary>Similarity needed for a match (1 = identical). Tolerates small OCR misreads.</summary>
    private const double MinimumSimilarity = 0.84;

    public static Rectangle? FindTextTarget(string label, int roughX, int roughY, IReadOnlyList<RecognizedWord> words)
    {
        var wanted = Normalize(label);
        if (wanted.Length == 0 || words.Count == 0) return null;

        var wantedWordCount = wanted.Split(' ').Length;
        var candidates = new List<(Rectangle Box, double Score)>();

        foreach (var line in words.GroupBy(word => word.LineIndex))
        {
            var lineWords = line.OrderBy(word => word.WordIndexInLine).ToList();
            for (var startIndex = 0; startIndex < lineWords.Count; startIndex++)
            {
                // Only phrases with roughly the same number of words as the label.
                var minLength = Math.Max(1, wantedWordCount - 1);
                var maxLength = Math.Min(MaxPhraseWords, wantedWordCount + 1);
                for (var length = minLength; length <= maxLength && startIndex + length <= lineWords.Count; length++)
                {
                    var phrase = lineWords.GetRange(startIndex, length);
                    var phraseText = Normalize(string.Join(" ", phrase.Select(word => word.Text)));
                    var score = Similarity(wanted, phraseText);
                    if (score < RequiredSimilarityFor(wanted)) continue;

                    var box = phrase.Select(word => word.Bounds).Aggregate(Rectangle.Union);
                    candidates.Add((box, score));
                }
            }
        }

        if (candidates.Count == 0) return null;

        // Best text match first; among (nearly) equally good matches, the one closest to
        // where Claude roughly pointed.
        var bestScore = candidates.Max(candidate => candidate.Score);
        return candidates
            .Where(candidate => candidate.Score >= bestScore - 0.03)
            .OrderBy(candidate => DistanceSquared(candidate.Box, roughX, roughY))
            .First()
            .Box;
    }

    /// <summary>Short words must match exactly ("in" must never match "im").</summary>
    private static double RequiredSimilarityFor(string wanted) => wanted.Length <= 4 ? 1.0 : MinimumSimilarity;

    private static double DistanceSquared(Rectangle box, int x, int y)
    {
        var centerX = box.X + box.Width / 2.0;
        var centerY = box.Y + box.Height / 2.0;
        return (centerX - x) * (centerX - x) + (centerY - y) * (centerY - y);
    }

    /// <summary>Lowercase, without accents' combining marks variance, quotes and edge punctuation.</summary>
    internal static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormC).ToLower(CultureInfo.GetCultureInfo("de-DE")))
        {
            if (char.IsLetterOrDigit(character) || character is '+' or '-' or '_' or '.' or '#')
                builder.Append(character);
            else if (char.IsWhiteSpace(character))
                builder.Append(' ');
            // quotes, commas, brackets … are dropped
        }
        var collapsed = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim('.', '-', '_');
    }

    /// <summary>1 − Levenshtein distance / longer length.</summary>
    internal static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return 1.0 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }
}
