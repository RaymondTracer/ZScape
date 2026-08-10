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
using ZScape.Utilities;

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

    private readonly List<ListViewSortDescriptor> _sortDescriptors =
    [
        new(0, "name", true)
    ];

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
            Key = "name", Header = "Name", Width = 250, MinWidth = 10,
            BindingPath = "NameWithExtension",
            TextTrimming = TextTrimming.CharacterEllipsis,
            CellPadding = new Thickness(6, 0),
            CanSort = true,
            CanUserHide = false
        });
        WadListView.AddColumn(new ListViewColumn
        {
            Key = "size", Header = "Size", Width = 80, MinWidth = 10,
            BindingPath = "SizeDisplay",
            CanSort = true
        });
        WadListView.AddColumn(new ListViewColumn
        {
            Key = "modified", Header = "Modified", Width = 130, MinWidth = 10,
            BindingPath = "ModifiedDisplay",
            CanSort = true
        });
        WadListView.AddColumn(new ListViewColumn
        {
            Key = "path", Header = "Path", Width = 240, IsStar = true, MinWidth = 10,
            BindingPath = "FullPath",
            Foreground = Brushes.Gray,
            TextTrimming = TextTrimming.CharacterEllipsis,
            CanSort = true
        });
        WadListView.Build(ListViewOverflowMode.AutoScroll);
        WadListView.SetSortDescriptors(_sortDescriptors);
        WadListView.SortRequested += WadListView_SortRequested;
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
        var searchText = SearchTextBox?.Text?.Trim() ?? "";

        IEnumerable<WadFileEntry> results = _allWads;

        // Text search
        if (!string.IsNullOrEmpty(searchText))
        {
            results = results.Where(w =>
                TextMatchUtility.IsLooseSearchMatch(w.NameWithExtension, searchText) ||
                TextMatchUtility.IsLooseSearchMatch(w.FullPath, searchText));
        }

        IOrderedEnumerable<WadFileEntry>? ordered = null;
        foreach (var descriptor in _sortDescriptors)
        {
            ordered = descriptor.ColumnKey switch
            {
                "name" => ApplyWadSort(results, ordered, wad => wad.Name,
                    descriptor.Ascending, StringComparer.OrdinalIgnoreCase),
                "size" => ApplyWadSort(results, ordered, wad => wad.Size,
                    descriptor.Ascending),
                "modified" => ApplyWadSort(results, ordered, wad => wad.Modified,
                    descriptor.Ascending),
                "path" => ApplyWadSort(results, ordered, wad => wad.FullPath,
                    descriptor.Ascending, StringComparer.OrdinalIgnoreCase),
                _ => ordered
            };
        }
        if (ordered != null)
            results = ordered;

        _filteredWads.Clear();
        foreach (var wad in results)
        {
            _filteredWads.Add(wad);
        }
    }

    private static IOrderedEnumerable<WadFileEntry> ApplyWadSort<TKey>(
        IEnumerable<WadFileEntry> source,
        IOrderedEnumerable<WadFileEntry>? ordered,
        Func<WadFileEntry, TKey> selector,
        bool ascending,
        IComparer<TKey>? comparer = null)
    {
        if (ordered == null)
        {
            return ascending
                ? source.OrderBy(selector, comparer)
                : source.OrderByDescending(selector, comparer);
        }

        return ascending
            ? ordered.ThenBy(selector, comparer)
            : ordered.ThenByDescending(selector, comparer);
    }

    #endregion

    private void WadListView_SortRequested(object? sender, ListViewSortEventArgs e)
    {
        _sortDescriptors.Clear();
        _sortDescriptors.AddRange(e.SortDescriptors);
        ApplyFilterAndSort();
    }

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
