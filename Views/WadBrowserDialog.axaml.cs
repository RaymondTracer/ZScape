using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ZScape.Services;

namespace ZScape.Views;

/// <summary>
/// View-model for a single WAD file row in the browser list.
/// Implements INotifyPropertyChanged so selection updates reflect in the UI.
/// </summary>
public class WadFileEntry : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isHovered;

    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }

    public string NameWithExtension => Name + Extension;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RowBackground));
        }
    }

    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (_isHovered == value) return;
            _isHovered = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RowBackground));
        }
    }

    public IBrush RowBackground => _isSelected
        ? new SolidColorBrush(Color.FromArgb(60, 0, 120, 215))
        : _isHovered
            ? new SolidColorBrush(Color.FromArgb(30, 255, 255, 255))
            : Brushes.Transparent;

    public string SizeDisplay => FormatSize(Size);
    public string ModifiedDisplay => Modified.ToString("yyyy-MM-dd HH:mm");

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:N1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):N1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):N2} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class WadBrowserDialog : Window
{
    private readonly SettingsService _settings;
    private readonly List<WadFileEntry> _allWads = new();
    private ObservableCollection<WadFileEntry> _filteredWads = new();
    private readonly HashSet<string> _selectedWads = new();
    private bool _isScanning;

    // Sorting state
    private int _sortColumn = -1;
    private bool _sortAscending = true;

    // For shift-click range selection
    private int _lastClickedIndex = -1;

    public WadBrowserDialog()
    {
        InitializeComponent();
        _settings = SettingsService.Instance;
        WadItemsControl.ItemsSource = _filteredWads;

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
        _selectedWads.Clear();

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
            // Restore selection state
            wad.IsSelected = _selectedWads.Contains(wad.FullPath);
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

    private void WadRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not WadFileEntry entry) return;

        var point = e.GetCurrentPoint(border);
        if (!point.Properties.IsLeftButtonPressed) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var clickedIndex = _filteredWads.IndexOf(entry);

        if (ctrl)
        {
            // Toggle selection
            entry.IsSelected = !entry.IsSelected;
            if (entry.IsSelected)
                _selectedWads.Add(entry.FullPath);
            else
                _selectedWads.Remove(entry.FullPath);
            _lastClickedIndex = clickedIndex;
        }
        else if (shift && _lastClickedIndex >= 0)
        {
            // Range selection
            var start = Math.Min(_lastClickedIndex, clickedIndex);
            var end = Math.Max(_lastClickedIndex, clickedIndex);

            // Clear current selection
            foreach (var w in _filteredWads) w.IsSelected = false;
            _selectedWads.Clear();

            for (var i = start; i <= end && i < _filteredWads.Count; i++)
            {
                _filteredWads[i].IsSelected = true;
                _selectedWads.Add(_filteredWads[i].FullPath);
            }
        }
        else
        {
            // Single select - clear others
            foreach (var w in _filteredWads) w.IsSelected = false;
            _selectedWads.Clear();

            entry.IsSelected = true;
            _selectedWads.Add(entry.FullPath);
            _lastClickedIndex = clickedIndex;
        }
    }

    private void WadRow_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Border border && border.DataContext is WadFileEntry entry)
            entry.IsHovered = true;
    }

    private void WadRow_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border border && border.DataContext is WadFileEntry entry)
            entry.IsHovered = false;
    }

    private void WadRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border border && border.DataContext is WadFileEntry wad)
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
        var selected = _filteredWads.FirstOrDefault(w => w.IsSelected);
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
        var selected = _filteredWads.Where(w => w.IsSelected).ToList();
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
                _selectedWads.Remove(wad.FullPath);
                deleted++;
            }
            catch { }
        }

        StatusLabel.Text = $"Deleted {deleted} file(s)";
        UpdateStats();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion
}
