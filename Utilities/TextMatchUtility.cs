using System.Collections.Generic;
using System.Text.RegularExpressions;
using ZScape.Models;

namespace ZScape.Utilities;

/// <summary>
/// Shared text matching helpers used across filters and server rules.
/// </summary>
public static class TextMatchUtility
{
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
}