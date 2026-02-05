using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZScape.Services;
using ZScape.Utilities;

namespace ZScape.Views;

/// <summary>
/// Dialog for fetching WAD files by name with configurable download sources.
/// </summary>
public partial class FetchWadsDialog : Window
{
    /// <summary>
    /// List of WAD file names to fetch (result after dialog closes).
    /// </summary>
    public List<string> WadNames { get; private set; } = [];

    /// <summary>
    /// Whether to search WAD hosting sites.
    /// </summary>
    public bool UseWadHostingSites => WadHostingSitesCheckBox.IsChecked ?? false;

    /// <summary>
    /// Whether to search /idgames Archive.
    /// </summary>
    public bool UseIdgames => IdgamesCheckBox.IsChecked ?? false;

    /// <summary>
    /// Whether to use web search (DuckDuckGo) as fallback.
    /// </summary>
    public bool UseWebSearch => WebSearchCheckBox.IsChecked ?? false;

    /// <summary>
    /// Whether the dialog was confirmed.
    /// </summary>
    public bool Confirmed { get; private set; }

    public FetchWadsDialog()
    {
        InitializeComponent();
        
        // Handle Escape/Enter keys
        KeyDown += OnDialogKeyDown;

        Loaded += (_, _) =>
        {
            // Update hosting sites checkbox text based on configured sites
            var siteCount = SettingsService.Instance.Settings.DownloadSites.Count;
            WadHostingSitesCheckBox.Content = siteCount > 0
                ? $"WAD Hosting Sites ({siteCount} configured in Settings)"
                : "WAD Hosting Sites (none configured - add in Settings)";
            WadHostingSitesCheckBox.IsChecked = siteCount > 0;
            WadHostingSitesCheckBox.IsEnabled = siteCount > 0;

            WadNamesTextBox.TextChanged += WadNamesTextBox_TextChanged;
        };
    }
    
    private void OnDialogKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            CancelButton_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Avalonia.Input.Key.Enter && FetchButton.IsEnabled)
        {
            // Only trigger if not in the multi-line textbox
            var focusedElement = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focusedElement is not TextBox textBox || !textBox.AcceptsReturn)
            {
                FetchButton_Click(sender, e);
                e.Handled = true;
            }
        }
    }

    private void WadNamesTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var lines = ParseWadNames();
        var count = lines.Count;

        var hasSource = (WadHostingSitesCheckBox.IsChecked ?? false) ||
                        (IdgamesCheckBox.IsChecked ?? false) ||
                        (WebSearchCheckBox.IsChecked ?? false);

        FetchButton.IsEnabled = count > 0 && hasSource;
        StatusLabel.Text = count == 0 ? "Enter at least one WAD name" : $"{count} WAD(s) to fetch";
    }

    private List<string> ParseWadNames()
    {
        var names = new List<string>();
        var text = WadNamesTextBox.Text ?? "";
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Keep files without extension - the downloader will try all supported extensions
            // Only validate that if there IS an extension, it's a supported one
            var ext = Path.GetExtension(trimmed);
            if (!string.IsNullOrEmpty(ext) && !WadExtensions.IsSupportedExtension(ext))
            {
                // Has an unsupported extension - skip
                continue;
            }

            if (!names.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(trimmed);
            }
        }

        return names;
    }

    private void FetchButton_Click(object? sender, RoutedEventArgs e)
    {
        WadNames = ParseWadNames();

        if (WadNames.Count == 0)
        {
            StatusLabel.Text = "Please enter at least one WAD file name";
            return;
        }

        var hasSource = (WadHostingSitesCheckBox.IsChecked ?? false) ||
                        (IdgamesCheckBox.IsChecked ?? false) ||
                        (WebSearchCheckBox.IsChecked ?? false);

        if (!hasSource)
        {
            StatusLabel.Text = "Please enable at least one download source";
            return;
        }

        Confirmed = true;
        Close();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }
}
