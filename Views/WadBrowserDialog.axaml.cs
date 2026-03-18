using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ZScape.Controls;
using ZScape.Services;

namespace ZScape.Views;

/// <summary>
/// View-model for a single WAD file row in the browser list.
/// </summary>
public class WadFileEntry
{
    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }

    public string NameWithExtension => Name + Extension;

    public string SizeDisplay => FormatSize(Size);
    public string ModifiedDisplay => Modified.ToString("yyyy-MM-dd HH:mm");

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:N1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):N1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):N2} GB";
    }
}

public partial class WadBrowserDialog : Window
{
    private readonly SettingsService _settings;
    private readonly List<WadFileEntry> _allWads = new();
    private ObservableCollection<WadFileEntry> _filteredWads = new();
    private bool _isScanning;

    // Sorting state
    private int _sortColumn = -1;
    private bool _sortAscending = true;

    // For shift-click range selection
    // (Handled by built-in multi-select in ResizableListView)

    public WadBrowserDialog()
    {
        InitializeComponent();
        _settings = SettingsService.Instance;

        // Configure the list view columns
        WadListView.SelectionMode = ListViewSelectionMode.Multi;
        WadListView.AddColumn(new ListViewColumn
        {
            Header = "Name", Width = 250, MinWidth = 80,
            BindingPath = "NameWithExtension",
            TextTrimming = TextTrimming.CharacterEllipsis,
            CellPadding = new Thickness(6, 0),
            SortClick = SortByName_Click
        });
        WadListView.AddColumn(new ListViewColumn
        {
            Header = "Size", Width = 80, MinWidth = 50,
            BindingPath = "SizeDisplay",
            SortClick = SortBySize_Click
        });
        WadListView.AddColumn(new ListViewColumn
        {
            Header = "Modified", Width = 130, MinWidth = 80,
            BindingPath = "ModifiedDisplay",
            SortClick = SortByModified_Click
        });
        WadListView.AddColumn(new ListViewColumn
        {
            Header = "Path", IsStar = true, MinWidth = 80,
            BindingPath = "FullPath",
            Foreground = Brushes.Gray,
            TextTrimming = TextTrimming.CharacterEllipsis,
            SortClick = SortByPath_Click
        });
        WadListView.Build(ListViewOverflowMode.AutoScroll);
        WadListView.ItemsSource = _filteredWads;

        // Wire up row events
        WadListView.RowDoubleTapped += OnWadRowDoubleTapped;

        // Handle Escape key
        KeyDown += OnDialogKeyDown;

        Loaded += async (_, _) => await ScanWadsAsync();
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    #region Scanning

    private async Task ScanWadsAsync()
    {
        if (_isScanning) return;
        _isScanning = true;

        StatusLabel.Text = "Scanning WAD folders...";
        _allWads.Clear();
        _filteredWads.Clear();
        WadListView.ClearSelection();

        await Task.Run(() =>
        {
            var extensions = new[] { ".wad", ".pk3", ".pk7", ".pke", ".ipk3", ".ipk7", ".deh", ".bex" };
            var wadPaths = _settings.Settings.WadSearchPaths.Where(Directory.Exists).ToList();

            foreach (var basePath in wadPaths)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(basePath, "*.*", SearchOption.AllDirectories))
                    {
                        var ext = Path.GetExtension(file).ToLowerInvariant();
                        if (!extensions.Contains(ext)) continue;

                        try
                        {
                            var fi = new FileInfo(file);
                            var entry = new WadFileEntry
                            {
                                Name = Path.GetFileNameWithoutExtension(file),
                                Extension = ext,
                                FullPath = file,
                                Size = fi.Length,
                                Modified = fi.LastWriteTime
                            };

                            Avalonia.Threading.Dispatcher.UIThread.Post(() => _allWads.Add(entry));
                        }
                        catch { }
                    }
                }
                catch { }
            }
        });

        ApplyFilterAndSort();
        UpdateStats();
        _isScanning = false;
        StatusLabel.Text = "Ready";
    }

    #endregion

    #region Filtering and Sorting

    private void ApplyFilterAndSort()
    {
        var searchText = SearchTextBox?.Text?.ToLowerInvariant() ?? "";

        IEnumerable<WadFileEntry> results = _allWads;

        // Text search
        if (!string.IsNullOrEmpty(searchText))
        {
            results = results.Where(w =>
                w.Name.ToLowerInvariant().Contains(searchText) ||
                w.FullPath.ToLowerInvariant().Contains(searchText));
        }

        // Sorting
        if (_sortColumn >= 0)
        {
            results = (_sortColumn, _sortAscending) switch
            {
                (0, true) => results.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase),
                (0, false) => results.OrderByDescending(w => w.Name, StringComparer.OrdinalIgnoreCase),
                (1, true) => results.OrderBy(w => w.Size),
                (1, false) => results.OrderByDescending(w => w.Size),
                (2, true) => results.OrderBy(w => w.Modified),
                (2, false) => results.OrderByDescending(w => w.Modified),
                (3, true) => results.OrderBy(w => w.FullPath, StringComparer.OrdinalIgnoreCase),
                (3, false) => results.OrderByDescending(w => w.FullPath, StringComparer.OrdinalIgnoreCase),
                _ => results
            };
        }

        _filteredWads.Clear();
        foreach (var wad in results)
        {
            _filteredWads.Add(wad);
        }
    }

    private void SortByColumn(int column)
    {
        if (_sortColumn == column)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }

        ApplyFilterAndSort();
    }

    #endregion

    #region Sort Click Handlers

    private void SortByName_Click(object? sender, RoutedEventArgs e) => SortByColumn(0);
    private void SortBySize_Click(object? sender, RoutedEventArgs e) => SortByColumn(1);
    private void SortByModified_Click(object? sender, RoutedEventArgs e) => SortByColumn(2);
    private void SortByPath_Click(object? sender, RoutedEventArgs e) => SortByColumn(3);

    #endregion

    #region Row Interaction

    private void OnWadRowDoubleTapped(object? sender, ListViewRowEventArgs e)
    {
        if (e.DataContext is WadFileEntry wad)
        {
            var folder = Path.GetDirectoryName(wad.FullPath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
        }
    }

    #endregion

    #region Stats

    private void UpdateStats()
    {
        var count = _filteredWads.Count;
        var totalSize = _filteredWads.Sum(w => w.Size);

        CountLabel.Text = $"{count} files";
        TotalSizeLabel.Text = FormatSize(totalSize);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:N1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):N1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):N2} GB";
    }

    #endregion

    #region Toolbar Handlers

    private async void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        await ScanWadsAsync();
    }

    private void OpenFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        var selected = WadListView.SelectedItem as WadFileEntry;
        if (selected == null) return;

        var folder = Path.GetDirectoryName(selected.FullPath);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
    }

    private void SearchTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilterAndSort();
        UpdateStats();
    }

    private void ClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        SearchTextBox.Text = "";
    }

    #endregion

    #region Delete / Copy / Close

    private async void DeleteSelected_Click(object? sender, RoutedEventArgs e)
    {
        var selected = WadListView.SelectedItems.OfType<WadFileEntry>().ToList();
        if (selected.Count == 0) return;

        var msgBox = new Window
        {
            Title = "Confirm Delete",
            Width = 400,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 15,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Delete {selected.Count} file(s)? This cannot be undone.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children =
                        {
                            new Button { Content = "Delete", Tag = "yes" },
                            new Button { Content = "Cancel", Tag = "no" }
                        }
                    }
                }
            }
        };

        bool confirmed = false;
        foreach (var btn in ((StackPanel)((StackPanel)msgBox.Content).Children[1]).Children.OfType<Button>())
        {
            btn.Click += (s, _) =>
            {
                confirmed = ((Button)s!).Tag?.ToString() == "yes";
                msgBox.Close();
            };
        }

        await msgBox.ShowDialog(this);

        if (!confirmed) return;

        var deleted = 0;
        foreach (var wad in selected)
        {
            try
            {
                File.Delete(wad.FullPath);
                _allWads.Remove(wad);
                _filteredWads.Remove(wad);
                deleted++;
            }
            catch { }
        }

        WadListView.ClearSelection();

        StatusLabel.Text = $"Deleted {deleted} file(s)";
        UpdateStats();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion
}
