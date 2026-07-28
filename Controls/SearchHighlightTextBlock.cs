using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using ZScape.Utilities;

namespace ZScape.Controls;

/// <summary>
/// TextBlock that preserves the original text while bolding ranges matched by
/// ZScape's forgiving server search.
/// </summary>
public sealed class SearchHighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceTextProperty =
        AvaloniaProperty.Register<SearchHighlightTextBlock, string?>(
            nameof(SourceText));

    public static readonly StyledProperty<string?> SearchTextProperty =
        AvaloniaProperty.Register<SearchHighlightTextBlock, string?>(
            nameof(SearchText));

    static SearchHighlightTextBlock()
    {
        SourceTextProperty.Changed.AddClassHandler<SearchHighlightTextBlock>(
            (control, _) => control.RebuildInlines());
        SearchTextProperty.Changed.AddClassHandler<SearchHighlightTextBlock>(
            (control, _) => control.RebuildInlines());
    }

    public string? SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public string? SearchText
    {
        get => GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    private void RebuildInlines()
    {
        var text = SourceText ?? string.Empty;
        var ranges = TextMatchUtility.FindLooseMatchRanges(text, SearchText);

        Inlines?.Clear();
        if (ranges.Count == 0)
        {
            Inlines?.Add(new Run(text));
            return;
        }

        var position = 0;
        foreach (var range in ranges)
        {
            if (range.Start > position)
                Inlines?.Add(new Run(text[position..range.Start]));

            var end = Math.Min(text.Length, range.Start + range.Length);
            if (end > range.Start)
            {
                Inlines?.Add(new Run(text[range.Start..end])
                {
                    FontWeight = FontWeight.Bold
                });
            }
            position = end;
        }

        if (position < text.Length)
            Inlines?.Add(new Run(text[position..]));
    }
}
