using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ZScape.Models;

namespace ZScape.Utilities;

/// <summary>
/// Shared text matching helpers used across filters and server rules.
/// </summary>
public static class TextMatchUtility
{
    /// <summary>
    /// Performs forgiving search matching. Case, accents, apostrophes, and most
    /// punctuation are ignored; every query token must occur in the candidate.
    /// For example, "ghouls" matches "Ghoul's".
    /// </summary>
    public static bool IsLooseSearchMatch(string? text, string? query)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(query))
            return false;

        var normalizedText = NormalizeForSearch(text);
        var normalizedQuery = NormalizeForSearch(query);
        if (normalizedQuery.Length == 0)
            return false;

        var compactText = normalizedText.Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(token =>
                normalizedText.Contains(token, StringComparison.Ordinal)
                || compactText.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>
    /// Normalizes user-facing text for forgiving search comparisons.
    /// </summary>
    public static string NormalizeForSearch(string? text)
    {
        return NormalizeWithMap(text).Text;
    }

    /// <summary>
    /// Finds original-text ranges corresponding to a forgiving query. These
    /// ranges can be emphasized without replacing the user's punctuation.
    /// </summary>
    public static IReadOnlyList<TextMatchRange> FindLooseMatchRanges(
        string? text,
        string? query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query))
            return [];

        var normalizedText = NormalizeWithMap(text);
        var normalizedQuery = NormalizeForSearch(query);
        if (normalizedQuery.Length == 0 || normalizedText.Map.Count == 0)
            return [];

        var ranges = new List<TextMatchRange>();
        var compactText = Compact(normalizedText);
        foreach (var token in normalizedQuery.Split(
                     ' ',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var foundDirectMatch = AddTokenRanges(normalizedText, token, ranges);
            if (!foundDirectMatch && compactText.Text.Length != normalizedText.Text.Length)
            {
                AddTokenRanges(compactText, token, ranges);
            }
        }

        if (ranges.Count <= 1)
            return ranges;

        var merged = new List<TextMatchRange>();
        foreach (var range in ranges.OrderBy(item => item.Start))
        {
            if (merged.Count == 0)
            {
                merged.Add(range);
                continue;
            }

            var previous = merged[^1];
            var previousEnd = previous.Start + previous.Length;
            if (range.Start <= previousEnd)
            {
                var mergedEnd = Math.Max(
                    previousEnd,
                    range.Start + range.Length);
                merged[^1] = new TextMatchRange(
                    previous.Start,
                    mergedEnd - previous.Start);
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }

    private static bool AddTokenRanges(
        NormalizedText normalizedText,
        string token,
        List<TextMatchRange> ranges)
    {
        var found = false;
        var searchFrom = 0;
        while (searchFrom < normalizedText.Text.Length)
        {
            var matchIndex = normalizedText.Text.IndexOf(
                token,
                searchFrom,
                StringComparison.Ordinal);
            if (matchIndex < 0)
                break;

            var endIndex = matchIndex + token.Length - 1;
            if (endIndex < normalizedText.Map.Count)
            {
                var originalStart = normalizedText.Map[matchIndex];
                var originalEnd = normalizedText.Map[endIndex] + 1;
                if (originalEnd > originalStart)
                {
                    ranges.Add(new TextMatchRange(
                        originalStart,
                        originalEnd - originalStart));
                    found = true;
                }
            }

            searchFrom = matchIndex + Math.Max(1, token.Length);
        }

        return found;
    }

    public static bool IsMatch(string? text, TextMatchRule? rule)
    {
        if (rule == null)
        {
            return false;
        }

        return IsMatch(text, rule.Pattern, rule.Mode);
    }

    public static bool IsMatch(string? text, string? pattern, TextMatchMode mode)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        return mode switch
        {
            TextMatchMode.Exact => text.Equals(pattern, StringComparison.OrdinalIgnoreCase),
            TextMatchMode.StartsWith => text.StartsWith(pattern, StringComparison.OrdinalIgnoreCase),
            TextMatchMode.EndsWith => text.EndsWith(pattern, StringComparison.OrdinalIgnoreCase),
            TextMatchMode.Wildcard => MatchesRegex(text, BuildWildcardPattern(pattern)),
            TextMatchMode.Regex => MatchesRegex(text, pattern),
            _ => text.Contains(pattern, StringComparison.OrdinalIgnoreCase)
        };
    }

    public static bool MatchesAny(string? text, IEnumerable<TextMatchRule>? rules)
    {
        if (string.IsNullOrEmpty(text) || rules == null)
        {
            return false;
        }

        foreach (var rule in rules)
        {
            if (rule != null && IsMatch(text, rule))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildWildcardPattern(string pattern)
    {
        return "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
    }

    private static bool MatchesRegex(string text, string pattern)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static NormalizedText NormalizeWithMap(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new NormalizedText(string.Empty, []);

        var normalized = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        var pendingSeparator = false;

        for (var originalIndex = 0; originalIndex < text.Length; originalIndex++)
        {
            var character = text[originalIndex];
            if (character is '\'' or '\u2018' or '\u2019' or '`')
                continue;

            var decomposed = character
                .ToString()
                .Normalize(NormalizationForm.FormD);
            var appendedLetterOrDigit = false;

            foreach (var decomposedCharacter in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(decomposedCharacter)
                    == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (!char.IsLetterOrDigit(decomposedCharacter))
                    continue;

                if (pendingSeparator && normalized.Length > 0)
                {
                    normalized.Append(' ');
                    map.Add(originalIndex);
                }

                normalized.Append(char.ToLowerInvariant(decomposedCharacter));
                map.Add(originalIndex);
                pendingSeparator = false;
                appendedLetterOrDigit = true;
            }

            if (!appendedLetterOrDigit
                && (char.IsWhiteSpace(character)
                    || char.IsPunctuation(character)
                    || char.IsSeparator(character)
                    || char.IsSymbol(character)))
            {
                pendingSeparator = normalized.Length > 0;
            }
        }

        return new NormalizedText(normalized.ToString(), map);
    }

    private static NormalizedText Compact(NormalizedText text)
    {
        if (!text.Text.Contains(' '))
            return text;

        var compactText = new StringBuilder(text.Text.Length);
        var compactMap = new List<int>(text.Map.Count);
        for (var index = 0; index < text.Text.Length; index++)
        {
            if (text.Text[index] == ' ')
                continue;

            compactText.Append(text.Text[index]);
            compactMap.Add(text.Map[index]);
        }

        return new NormalizedText(compactText.ToString(), compactMap);
    }

    private sealed record NormalizedText(string Text, List<int> Map);
}

/// <summary>An emphasized range in the original display text.</summary>
public readonly record struct TextMatchRange(int Start, int Length);
