using System;
using Avalonia.Controls;
using Avalonia.Layout;
using ZScape.Services;
using ZScape.Utilities;

namespace ZScape.Controls;

/// <summary>
/// Presents the shared aggregate progress snapshot for multi-file operations.
/// </summary>
public sealed class OverallProgressControl : UserControl
{
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _percentageLabel;
    private readonly TextBlock _etaLabel;

    public OverallProgressControl()
    {
        _progressBar = new ProgressBar
        {
            Name = "OverallBar",
            Minimum = 0,
            Maximum = 100,
            Height = 20
        };
        _percentageLabel = new TextBlock
        {
            Name = "OverallPercent",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        _etaLabel = new TextBlock
        {
            Name = "OverallEta",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(_etaLabel,
            "ETA is estimated from recent per-file progress across all files. Searches, retries and stalls may extend it.");

        var details = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Avalonia.Thickness(0, 4, 0, 0)
        };
        details.Children.Add(_percentageLabel);
        Grid.SetColumn(_etaLabel, 1);
        details.Children.Add(_etaLabel);

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto")
        };
        layout.Children.Add(_progressBar);
        Grid.SetRow(details, 1);
        layout.Children.Add(details);
        Content = layout;

        UpdateProgress(OverallOperationProgress.Initial);
    }

    public void UpdateProgress(OverallOperationProgress progress)
    {
        var percentage = Math.Clamp(progress.PercentComplete, 0, 100);
        _progressBar.Value = percentage;
        _percentageLabel.Text = $"{percentage:F1}%";
        _etaLabel.Text = progress.IsStopped
            ? "ETA: —"
            : progress.EstimatedTimeRemaining is { } eta
                ? $"ETA: {FormatUtils.FormatEta(eta)}"
                : progress.IsComplete ? "ETA: —" : "ETA: Estimating...";
        _etaLabel.IsEnabled = !progress.IsStopped;
    }
}
