using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ZScape.Services;

namespace ZScape.Views;

public partial class WadBrowserDialog : Window
{
    private readonly SettingsService _settings;
    private ObservableCollection<WadFileInfo> _allWads = new();
    private ObservableCollection<WadFileInfo> _filteredWads = new();
    private HashSet<string> _duplicateHashes = new();
    private bool _isScanning;

    public WadBrowserDialog()
    {
        InitializeComponent();
        _settings = SettingsService.Instance;
        WadListView.ItemsSource = _filteredWads;
        
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

    private async Task ScanWadsAsync()
    {
        if (_isScanning) return;
        _isScanning = true;
        
        StatusLabel.Text = "Scanning WAD folders...";
        _allWads.Clear();
        _filteredWads.Clear();
        _duplicateHashes.Clear();

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
                            var wadInfo = new WadFileInfo
                            {
                                Name = Path.GetFileNameWithoutExtension(file),
                                Extension = ext,
                                FullPath = file,
                                Size = fi.Length,
                                Modified = fi.LastWriteTime
                            };
                            
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => _allWads.Add(wadInfo));
                        }
                        catch { }
                    }
                }
                catch { }
            }
        });

        ApplyFilter();
        UpdateStats();
        _isScanning = false;
        StatusLabel.Text = "Ready";
    }

    private void ApplyFilter()
    {
        var searchText = SearchTextBox.Text?.ToLowerInvariant() ?? "";
        var filterIndex = FilterComboBox.SelectedIndex;

        _filteredWads.Clear();
        
        foreach (var wad in _allWads)
        {
            // Search filter
            if (!string.IsNullOrEmpty(searchText))
            {
                if (!wad.Name.ToLowerInvariant().Contains(searchText) &&
                    !wad.FullPath.ToLowerInvariant().Contains(searchText))
                    continue;
            }

            // Type filter
            var include = filterIndex switch
            {
                1 => wad.Extension == ".wad",
                2 => wad.Extension == ".pk3" || wad.Extension == ".pk7",
                3 => wad.Extension == ".pke",
                4 => wad.Extension == ".ipk3" || wad.Extension == ".ipk7",
                5 => !string.IsNullOrEmpty(wad.Md5Hash) && _duplicateHashes.Contains(wad.Md5Hash),
                _ => true
            };

            if (include)
            {
                _filteredWads.Add(wad);
            }
        }
    }

    private void UpdateStats()
    {
        var count = _filteredWads.Count;
        var totalSize = _filteredWads.Sum(w => w.Size);
        var duplicates = _filteredWads.Count(w => !string.IsNullOrEmpty(w.Md5Hash) && _duplicateHashes.Contains(w.Md5Hash));

        CountLabel.Text = $"{count} files";
        TotalSizeLabel.Text = FormatSize(totalSize);
        DuplicateLabel.Text = duplicates > 0 ? $"{duplicates} duplicates" : "No duplicates";
        DuplicateLabel.Foreground = duplicates > 0 
            ? Avalonia.Media.Brushes.Orange 
            : Avalonia.Media.Brushes.Gray;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:N1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):N1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):N2} GB";
    }

    private async void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        await ScanWadsAsync();
    }

    private void OpenFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (WadListView.SelectedItem is WadFileInfo wad)
        {
            var folder = Path.GetDirectoryName(wad.FullPath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
        }
    }

    private void SearchTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilter();
        UpdateStats();
    }

    private void ClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        SearchTextBox.Text = "";
    }

    private void FilterComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SearchTextBox != null)
        {
            ApplyFilter();
            UpdateStats();
        }
    }

    private async void ComputeMd5_Click(object? sender, RoutedEventArgs e)
    {
        var selected = WadListView.SelectedItems?.Cast<WadFileInfo>().ToList();
        if (selected == null || selected.Count == 0)
        {
            // Compute all
            selected = _filteredWads.Where(w => string.IsNullOrEmpty(w.Md5Hash)).ToList();
        }

        if (selected.Count == 0) return;

        StatusLabel.Text = $"Computing MD5 for {selected.Count} files...";
        var count = 0;
        
        foreach (var wad in selected)
        {
            count++;
            StatusLabel.Text = $"Computing MD5... {count}/{selected.Count}";
            
            await Task.Run(() =>
            {
                try
                {
                    using var md5 = MD5.Create();
                    using var stream = File.OpenRead(wad.FullPath);
                    var hash = md5.ComputeHash(stream);
                    wad.Md5Hash = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
                catch
                {
                    wad.Md5Hash = "Error";
                }
            });
        }

        StatusLabel.Text = "MD5 computation complete";
        WadListView.ItemsSource = null;
        WadListView.ItemsSource = _filteredWads;
    }

    private void FindDuplicates_Click(object? sender, RoutedEventArgs e)
    {
        _duplicateHashes.Clear();
        
        var hashCounts = new Dictionary<string, int>();
        foreach (var wad in _allWads.Where(w => !string.IsNullOrEmpty(w.Md5Hash) && w.Md5Hash != "Error"))
        {
            if (hashCounts.ContainsKey(wad.Md5Hash!))
                hashCounts[wad.Md5Hash!]++;
            else
                hashCounts[wad.Md5Hash!] = 1;
        }

        foreach (var kvp in hashCounts.Where(k => k.Value > 1))
        {
            _duplicateHashes.Add(kvp.Key);
        }

        if (_duplicateHashes.Count > 0)
        {
            FilterComboBox.SelectedIndex = 5; // Duplicates filter
        }
        
        ApplyFilter();
        UpdateStats();
        StatusLabel.Text = $"Found {_duplicateHashes.Count} duplicate groups";
    }

    private async void DeleteSelected_Click(object? sender, RoutedEventArgs e)
    {
        var selected = WadListView.SelectedItems?.Cast<WadFileInfo>().ToList();
        if (selected == null || selected.Count == 0) return;

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
                    new TextBlock { Text = $"Delete {selected.Count} file(s)? This cannot be undone.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
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

        StatusLabel.Text = $"Deleted {deleted} file(s)";
        UpdateStats();
    }

    private async void CopyName_Click(object? sender, RoutedEventArgs e)
    {
        if (WadListView.SelectedItem is WadFileInfo wad)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(wad.Name + wad.Extension);
            }
        }
    }

    private void WadListView_DoubleTapped(object? sender, TappedEventArgs e)
    {
        OpenFolderButton_Click(sender, e);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private class WadFileInfo
    {
        public string Name { get; set; } = "";
        public string Extension { get; set; } = "";
        public string FullPath { get; set; } = "";
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public string? Md5Hash { get; set; }
        public string SizeDisplay => FormatSize(Size);
        public string ModifiedDisplay => Modified.ToString("yyyy-MM-dd HH:mm");
    }
}
