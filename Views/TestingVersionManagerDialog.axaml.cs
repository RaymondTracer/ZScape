using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ZScape.Controls;
using ZScape.Services;
using ZScape.Utilities;

namespace ZScape.Views;

/// <summary>
/// Dialog for managing installed testing versions of Zandronum.
/// </summary>
public partial class TestingVersionManagerDialog : Window
{
    private readonly LoggingService _logger = LoggingService.Instance;

    public ObservableCollection<TestingVersionInfo> Versions { get; } = [];

    public TestingVersionManagerDialog()
    {
        InitializeComponent();
        DataContext = this;

        KeyDown += OnDialogKeyDown;

        SetupVersionListView();

        Loaded += async (_, _) =>
        {
            await ScanVersionsAsync();
        };
    }

    private void SetupVersionListView()
    {
        VersionListView.SelectionMode = ListViewSelectionMode.Multi;
        VersionListView.SelectionChanged += (_, _) => UpdateButtonStates();

        VersionListView.AddColumn(new ListViewColumn
        {
            Header = "Version",
            BindingPath = "VersionName",
            Width = 200,
            MinWidth = 80,
            TextTrimming = TextTrimming.CharacterEllipsis,
            CellContentFactory = () =>
            {
                var tb = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Padding = new Avalonia.Thickness(4, 0)
                };
                tb.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("VersionName"));
                tb.Bind(TextBlock.ForegroundProperty, new Avalonia.Data.Binding("RowForeground"));
                return tb;
            }
        });

        VersionListView.AddColumn(new ListViewColumn
        {
            Header = "Size",
            BindingPath = "SizeDisplay",
            Width = 100,
            MinWidth = 50,
            CellContentFactory = () =>
            {
                var tb = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Avalonia.Thickness(4, 0)
                };
                tb.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("SizeDisplay"));
                tb.Bind(TextBlock.ForegroundProperty, new Avalonia.Data.Binding("RowForeground"));
                return tb;
            }
        });

        VersionListView.AddColumn(new ListViewColumn
        {
            Header = "Files",
            BindingPath = "FileCount",
            Width = 80,
            MinWidth = 40,
            CellContentFactory = () =>
            {
                var tb = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Avalonia.Thickness(4, 0)
                };
                tb.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("FileCount"));
                tb.Bind(TextBlock.ForegroundProperty, new Avalonia.Data.Binding("RowForeground"));
                return tb;
            }
        });

        VersionListView.AddColumn(new ListViewColumn
        {
            Header = "Screenshots",
            BindingPath = "ScreenshotDisplay",
            Width = 80,
            MinWidth = 40,
            CellContentFactory = () =>
            {
                var tb = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Avalonia.Thickness(4, 0)
                };
                tb.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("ScreenshotDisplay"));
                tb.Bind(TextBlock.ForegroundProperty, new Avalonia.Data.Binding("RowForeground"));
                return tb;
            }
        });

        VersionListView.AddColumn(new ListViewColumn
        {
            Header = "Path",
            Width = 200,
            IsStar = true,
            MinWidth = 80,
            TextTrimming = TextTrimming.CharacterEllipsis,
            CellContentFactory = () =>
            {
                var tb = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Padding = new Avalonia.Thickness(4, 0)
                };
                tb.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding("Path"));
                tb.Bind(TextBlock.ForegroundProperty, new Avalonia.Data.Binding("RowForeground"));
                return tb;
            }
        });

        VersionListView.RowDoubleTapped += VersionListView_RowDoubleTapped;

        VersionListView.Build(ListViewOverflowMode.AutoScroll);
        VersionListView.ItemsSource = Versions;
    }

    private void VersionListView_RowDoubleTapped(object? sender, ListViewRowEventArgs e)
    {
        if (e.DataContext is TestingVersionInfo info)
        {
            OpenFolder(info.Path);
        }
    }

    private void UpdateButtonStates()
    {
        var selectedCount = VersionListView.SelectedItems.Count;
        DeleteButton.IsEnabled = selectedCount > 0;
        OpenFolderButton.IsEnabled = selectedCount == 1;
    }
    
    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private async void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        await ScanVersionsAsync();
    }

    private async Task ScanVersionsAsync()
    {
        VersionListView.ClearSelection();
        Versions.Clear();
        UpdateButtonStates();

        var testingRoot = GetTestingRootPath();
        if (string.IsNullOrEmpty(testingRoot) || !Directory.Exists(testingRoot))
        {
            StatusLabel.Text = "Testing versions path not configured";
            TotalSizeLabel.Text = "Total: --";
            return;
        }

        StatusLabel.Text = "Scanning...";
        TotalSizeLabel.Text = "Calculating...";

        try
        {
            long totalSize = 0;

            await Task.Run(() =>
            {
                foreach (var dir in Directory.GetDirectories(testingRoot))
                {
                    var versionName = System.IO.Path.GetFileName(dir);
                    var exeNames = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) 
                        ? new[] { "zandronum.exe" } 
                        : new[] { "zandronum", "zandronum.x86_64" };
                    var hasExe = exeNames.Any(name => File.Exists(System.IO.Path.Combine(dir, name)));

                    var (size, fileCount) = GetDirectoryInfo(dir);

                    int screenshotCount = 0;
                    try
                    {
                        screenshotCount = Directory.GetFiles(dir, "Screenshot_*.png", SearchOption.TopDirectoryOnly).Length;
                    }
                    catch { }

                    var info = new TestingVersionInfo
                    {
                        VersionName = versionName,
                        Path = dir,
                        Size = size,
                        FileCount = fileCount,
                        ScreenshotCount = screenshotCount,
                        HasExecutable = hasExe,
                        RowForeground = hasExe ? Brushes.White : new SolidColorBrush(Color.FromRgb(255, 165, 0))
                    };

                    totalSize += size;

                    Avalonia.Threading.Dispatcher.UIThread.Post(() => Versions.Add(info));
                }
            });

            StatusLabel.Text = $"Found {Versions.Count} testing version{(Versions.Count != 1 ? "s" : "")}";
            TotalSizeLabel.Text = $"Total: {FormatFileSize(totalSize)}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Error: {ex.Message}";
            _logger.Error($"Failed to scan testing versions: {ex.Message}");
        }
    }

    private static (long size, int fileCount) GetDirectoryInfo(string path)
    {
        long size = 0;
        int count = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    size += info.Length;
                    count++;
                }
                catch { }
            }
        }
        catch { }

        return (size, count);
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private void OpenFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        var selected = VersionListView.SelectedItems.OfType<TestingVersionInfo>().FirstOrDefault();
        if (selected != null)
        {
            OpenFolder(selected.Path);
        }
    }

    private static void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("explorer.exe", path);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", path);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", path);
            }
        }
    }

    private async void DeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        var selectedItems = VersionListView.SelectedItems.OfType<TestingVersionInfo>().ToList();
        if (selectedItems.Count == 0) return;

        var totalSize = selectedItems.Sum(v => v.Size);
        var message = selectedItems.Count == 1
            ? $"Delete testing version '{selectedItems[0].VersionName}'?\n\nSize: {FormatFileSize(selectedItems[0].Size)}\n\nThis action cannot be undone."
            : $"Delete {selectedItems.Count} testing versions?\n\nTotal size: {FormatFileSize(totalSize)}\n\nThis action cannot be undone.";

        var result = await ShowConfirmDialog(message);
        if (!result) return;

        int deleted = 0;
        int failed = 0;

        foreach (var version in selectedItems)
        {
            try
            {
                if (Directory.Exists(version.Path))
                {
                    Directory.Delete(version.Path, true);
                    Versions.Remove(version);
                    deleted++;
                    _logger.Info($"Deleted testing version: {version.VersionName}");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to delete {version.VersionName}: {ex.Message}");
                failed++;
            }
        }

        await ScanVersionsAsync();

        StatusLabel.Text = failed > 0
            ? $"Deleted {deleted}, {failed} failed"
            : $"Deleted {deleted} version{(deleted != 1 ? "s" : "")}";
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string? GetTestingRootPath() => PathResolver.GetTestingVersionsPath();

    private async Task<bool> ShowConfirmDialog(string message)
    {
        var dialog = new Window
        {
            Title = "Confirm Delete",
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var result = false;
        var grid = new Grid { Margin = new Avalonia.Thickness(15) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(new GridLength(40)));

        var text = new TextBlock
        {
            Text = message,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(text, 0);
        grid.Children.Add(text);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        Grid.SetRow(buttonPanel, 1);

        var yesButton = new Button { Content = "Yes", Width = 70 };
        yesButton.Click += (_, _) => { result = true; dialog.Close(); };
        var noButton = new Button { Content = "No", Width = 70 };
        noButton.Click += (_, _) => { dialog.Close(); };

        buttonPanel.Children.Add(yesButton);
        buttonPanel.Children.Add(noButton);
        grid.Children.Add(buttonPanel);

        dialog.Content = grid;
        await dialog.ShowDialog(this);

        return result;
    }
}

/// <summary>
/// View model for testing version entries.
/// </summary>
public class TestingVersionInfo : INotifyPropertyChanged
{
    private IBrush _rowForeground = Brushes.White;

    public string VersionName { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public int FileCount { get; set; }
    public int ScreenshotCount { get; set; }
    public bool HasExecutable { get; set; }

    public IBrush RowForeground
    {
        get => _rowForeground;
        set { _rowForeground = value; OnPropertyChanged(); }
    }

    public string SizeDisplay => FormatFileSize(Size);
    public string ScreenshotDisplay => ScreenshotCount > 0 ? ScreenshotCount.ToString() : "-";

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
