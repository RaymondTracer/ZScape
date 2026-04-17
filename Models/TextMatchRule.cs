namespace ZScape.Models;

/// <summary>
/// Matching modes for reusable text rules.
/// </summary>
public enum TextMatchMode
{
    Contains = 0,
    Exact = 1,
    StartsWith = 2,
    EndsWith = 3,
    Wildcard = 4,
    Regex = 5
}

/// <summary>
/// Reusable text matching rule used by favorites, hidden servers, and filters.
/// </summary>
public class TextMatchRule
{
    public string Pattern { get; set; } = string.Empty;

    public TextMatchMode Mode { get; set; } = TextMatchMode.Contains;

    public TextMatchRule Clone()
    {
        return new TextMatchRule
        {
            Pattern = Pattern,
            Mode = Mode
        };
    }

    public override string ToString()
    {
        return $"{Mode switch
        {
            TextMatchMode.Contains => "Contains",
            TextMatchMode.Exact => "Exact",
            TextMatchMode.StartsWith => "Starts with",
            TextMatchMode.EndsWith => "Ends with",
            TextMatchMode.Wildcard => "Wildcard",
            TextMatchMode.Regex => "Regex",
            _ => "Contains"
        }}: {Pattern}";
    }
}