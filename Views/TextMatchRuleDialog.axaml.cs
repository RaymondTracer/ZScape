using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ZScape.Models;
using ZScape.Utilities;

namespace ZScape.Views;

/// <summary>
/// Dialog for creating or editing a reusable text matching rule.
/// </summary>
public partial class TextMatchRuleDialog : Window
{
    public TextMatchRule Rule { get; private set; } = new();

    public bool Confirmed { get; private set; }

    // Required for Avalonia runtime XAML loading.
    public TextMatchRuleDialog() : this("Text Match Rule") { }

    public TextMatchRuleDialog(string title, TextMatchRule? existingRule = null)
    {
        InitializeComponent();

        Title = title;
        PopulateMatchModes();

        Rule = existingRule?.Clone() ?? new TextMatchRule();
        PatternTextBox.Text = Rule.Pattern;
        MatchModeComboBox.SelectedIndex = AppConstants.TextMatchModeLabels.GetIndex(Rule.Mode);

        KeyDown += OnDialogKeyDown;
        Loaded += (_, _) =>
        {
            PatternTextBox.TextChanged += (_, _) => UpdateState();
            MatchModeComboBox.SelectionChanged += (_, _) => UpdateState();
            UpdateState();
            PatternTextBox.Focus();
        };
    }

    private void PopulateMatchModes()
    {
        MatchModeComboBox.Items.Clear();
        foreach (var option in AppConstants.TextMatchModeLabels.Options)
        {
            MatchModeComboBox.Items.Add(new ComboBoxItem { Content = option.Label });
        }
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelButton_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && OkButton.IsEnabled)
        {
            OkButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void UpdateState()
    {
        var pattern = PatternTextBox.Text?.Trim() ?? string.Empty;
        var hasPattern = !string.IsNullOrWhiteSpace(pattern);
        OkButton.IsEnabled = hasPattern;
        StatusLabel.Text = hasPattern ? "Rule is ready to save" : "Enter a pattern";
        StatusLabel.Foreground = hasPattern
            ? new SolidColorBrush(Color.FromRgb(100, 200, 100))
            : Brushes.Gray;
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        Rule = new TextMatchRule
        {
            Pattern = PatternTextBox.Text?.Trim() ?? string.Empty,
            Mode = AppConstants.TextMatchModeLabels.GetValue(MatchModeComboBox.SelectedIndex)
        };

        Confirmed = true;
        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close(false);
    }
}