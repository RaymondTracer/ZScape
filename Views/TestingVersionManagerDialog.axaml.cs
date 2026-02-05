using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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

        // Handle Escape/Enter keys
        KeyDown += OnDialogKeyDown;

        Loaded += async (_, _) =>
        {
            VersionDataGrid.ItemsSource = Versions;
            VersionDataGrid.SelectionChanged += VersionDataGrid_SelectionChanged;
            await ScanVersionsAsync();
        };
    }
    
    private void OnDialogKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
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
        Versions.Clear();

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

                    // Calculate directory size and file count
                    var (size, fileCount) = GetDirectoryInfo(dir);

                    // Count screenshots
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
                        RowForeground = hasExe ? Brushes.White : new SolidColorBrush(Color.FromRgb(255, 165, 0)) // Orange for incomplete
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

    private void VersionDataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var count = VersionDataGrid.SelectedItems?.Count ?? 0;
        DeleteButton.IsEnabled = count > 0;
        OpenFolderButton.IsEnabled = count == 1;
    }
    
    private void VersionDataGrid_LoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is TestingVersionInfo info)
        {
            e.Row.Foreground = info.RowForeground;
        }
    }

    private void OpenFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (VersionDataGrid.SelectedItem is TestingVersionInfo info)
        {
            OpenFolder(info.Path);
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
        var selectedItems = VersionDataGrid.SelectedItems?.Cast<TestingVersionInfo>().ToList();
        if (selectedItems == null || selectedItems.Count == 0) return;

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
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(text, 0);
        grid.Children.Add(text);

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
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
public class TestingVersionInfo
{
    public string VersionName { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public int FileCount { get; set; }
    public int ScreenshotCount { get; set; }
    public bool HasExecutable { get; set; }
    public IBrush RowForeground { get; set; } = Brushes.White;

    public string SizeDisplay => FormatFileSize(Size);
    public string ScreenshotDisplay => ScreenshotCount > 0 ? ScreenshotCount.ToString() : "-";

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
