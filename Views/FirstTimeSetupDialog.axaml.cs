using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ZScape.Controls;
using ZScape.Services;

namespace ZScape.Views;

public partial class FirstTimeSetupDialog : Window
{
    private readonly SettingsService _settingsService;
    private AppSettings Settings => _settingsService.Settings;
    private readonly ObservableCollection<string> _wadPaths = new();
    private const string DownloadFolderPrefix = "[Download Folder] ";
    private bool _applyingPreset;

    public FirstTimeSetupDialog()
    {
        InitializeComponent();
        _settingsService = SettingsService.Instance;
        
        WadPathsListBox.ItemsSource = _wadPaths;
        WadPathsListBox.SelectionChanged += WadPathsListBox_SelectionChanged;
        WadDownloadPathTextBox.TextChanged += WadDownloadPathTextBox_TextChanged;
        UpdateIntervalSpinner.ValueChanged += IntervalSpinner_Changed;
        UpdateIntervalUnitComboBox.SelectionChanged += IntervalUnit_Changed;
        UpdatePresetsComboBox.SelectionChanged += UpdatePresets_SelectionChanged;
        
        // Handle Escape key to show exit confirmation
        KeyDown += OnDialogKeyDown;
        
        LoadDefaults();
    }
    
    private void OnDialogKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            ExitButton_Click(sender, e);
            e.Handled = true;
        }
    }
    
    private int IntervalValue
    {
        get => UpdateIntervalSpinner.Value;
        set => UpdateIntervalSpinner.Value = value;
    }

    private void WadPathsListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Can't remove the download folder (index 0)
        RemoveWadPathButton.IsEnabled = WadPathsListBox.SelectedIndex > 0;
    }

    private void WadDownloadPathTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateDownloadFolderDisplay();
    }

    private void IntervalSpinner_Changed(object? sender, int value)
    {
        if (!_applyingPreset && UpdatePresetsComboBox != null)
            UpdatePresetsComboBox.SelectedIndex = 0;
    }

    private void IntervalUnit_Changed(object? sender, SelectionChangedEventArgs e)
    {
        // Ignore invalid selections or restoration events
        if (UpdateIntervalUnitComboBox.SelectedIndex < 0 || UpdateIntervalUnitComboBox.IsRestoringSelection)
            return;
            
        if (!_applyingPreset && UpdatePresetsComboBox != null)
            UpdatePresetsComboBox.SelectedIndex = 0;
    }

    private void UpdatePresets_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Ignore invalid selections or restoration events
        if (UpdatePresetsComboBox.SelectedIndex <= 0 || UpdatePresetsComboBox.IsRestoringSelection) 
            return;
        
        _applyingPreset = true;
        switch (UpdatePresetsComboBox.SelectedIndex)
        {
            case 1: // Every 6 hours
                IntervalValue = 6;
                UpdateIntervalUnitComboBox.SelectedIndex = 0;
                break;
            case 2: // Once a day
                IntervalValue = 1;
                UpdateIntervalUnitComboBox.SelectedIndex = 1;
                break;
            case 3: // Once a week
                IntervalValue = 1;
                UpdateIntervalUnitComboBox.SelectedIndex = 2;
                break;
            case 4: // Once a month
                IntervalValue = 4;
                UpdateIntervalUnitComboBox.SelectedIndex = 2;
                break;
        }
        _applyingPreset = false;
    }

    private void UpdateDownloadFolderDisplay()
    {
        var downloadPath = WadDownloadPathTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(downloadPath))
            return;

        var displayText = DownloadFolderPrefix + downloadPath;

        if (_wadPaths.Count == 0)
        {
            _wadPaths.Add(displayText);
        }
        else
        {
            _wadPaths[0] = displayText;
        }
    }

    private void LoadDefaults()
    {
        // Defer ComboBox selections to ensure they render correctly
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Set default ComboBox selections (AXAML IsSelected doesn't work reliably)
            UpdateBehaviorComboBox.SelectedIndex = 2;    // Auto Download
            UpdatePresetsComboBox.SelectedIndex = 2;     // Once a day
            UpdateIntervalUnitComboBox.SelectedIndex = 1; // Days
        }, Avalonia.Threading.DispatcherPriority.Loaded);
        
        // Try to auto-detect Zandronum
        var detectedPath = AutoDetectZandronumPath();
        if (!string.IsNullOrEmpty(detectedPath))
        {
            ZandronumPathTextBox.Text = detectedPath;
            
            var zandDir = Path.GetDirectoryName(detectedPath);
            if (!string.IsNullOrEmpty(zandDir))
            {
                WadDownloadPathTextBox.Text = zandDir;
            }
        }

        // Load any existing settings
        if (!string.IsNullOrEmpty(Settings.ZandronumPath))
            ZandronumPathTextBox.Text = Settings.ZandronumPath;
        if (!string.IsNullOrEmpty(Settings.ZandronumTestingPath))
            TestingFolderTextBox.Text = Settings.ZandronumTestingPath;
        if (!string.IsNullOrEmpty(Settings.WadDownloadPath))
            WadDownloadPathTextBox.Text = Settings.WadDownloadPath;

        UpdateDownloadFolderDisplay();
    }

    private static string? AutoDetectZandronumPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var windowsPaths = new[]
            {
                @"C:\Zandronum\zandronum.exe",
                @"C:\Games\Zandronum\zandronum.exe",
                @"C:\Program Files\Zandronum\zandronum.exe",
                @"C:\Program Files (x86)\Zandronum\zandronum.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zandronum", "zandronum.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zandronum", "zandronum.exe"),
                Path.Combine(AppContext.BaseDirectory, "zandronum.exe"),
            };

            foreach (var path in windowsPaths)
            {
                try
                {
                    if (File.Exists(path)) return path;
                }
                catch { }
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var linuxPaths = new[]
            {
                "/usr/bin/zandronum",
                "/usr/local/bin/zandronum",
                "/usr/games/zandronum",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zandronum/zandronum"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "zandronum/zandronum"),
                Path.Combine(AppContext.BaseDirectory, "zandronum"),
            };

            foreach (var path in linuxPaths)
            {
                try
                {
                    if (File.Exists(path)) return path;
                }
                catch { }
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var macPaths = new[]
            {
                "/Applications/Zandronum.app/Contents/MacOS/zandronum",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/Zandronum.app/Contents/MacOS/zandronum"),
                "/usr/local/bin/zandronum",
                Path.Combine(AppContext.BaseDirectory, "zandronum"),
            };

            foreach (var path in macPaths)
            {
                try
                {
                    if (File.Exists(path)) return path;
                }
                catch { }
            }
        }

        return null;
    }

    private async void BrowseZandronum_Click(object? sender, RoutedEventArgs e)
    {
        var patterns = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "*.exe" }
            : new[] { "zandronum*", "*" };

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Zandronum Executable",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Executable") { Patterns = patterns },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
            }
        });

        if (files.Count > 0)
        {
            ZandronumPathTextBox.Text = files[0].Path.LocalPath;
            
            // Auto-set download folder if empty
            if (string.IsNullOrEmpty(WadDownloadPathTextBox.Text))
            {
                var zandDir = Path.GetDirectoryName(files[0].Path.LocalPath);
                if (!string.IsNullOrEmpty(zandDir))
                {
                    WadDownloadPathTextBox.Text = zandDir;
                }
            }
        }
    }

    private async void BrowseTestingFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Testing Versions Folder",
            AllowMultiple = false
        });

        if (folder.Count > 0)
        {
            TestingFolderTextBox.Text = folder[0].Path.LocalPath;
        }
    }

    private async void AddWadPath_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select WAD Folder",
            AllowMultiple = false
        });

        if (folder.Count > 0)
        {
            var path = folder[0].Path.LocalPath;
            var existingPaths = _wadPaths
                .Select(p => p.StartsWith(DownloadFolderPrefix) ? p[DownloadFolderPrefix.Length..] : p);

            if (!existingPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _wadPaths.Add(path);
            }
        }
    }

    private void RemoveWadPath_Click(object? sender, RoutedEventArgs e)
    {
        if (WadPathsListBox.SelectedIndex > 0)
        {
            _wadPaths.RemoveAt(WadPathsListBox.SelectedIndex);
        }
    }

    private async void BrowseDownloadFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Download Folder",
            AllowMultiple = false
        });

        if (folder.Count > 0)
        {
            WadDownloadPathTextBox.Text = folder[0].Path.LocalPath;
        }
    }

    private async void ExitButton_Click(object? sender, RoutedEventArgs e)
    {
        var confirmed = await ShowConfirmationAsync("Exit Setup", 
            "Are you sure you want to exit? ZScape cannot run without completing the initial setup.");
        
        if (confirmed)
        {
            Close(false);
        }
    }

    private async void FinishButton_Click(object? sender, RoutedEventArgs e)
    {
        var zandPath = ZandronumPathTextBox.Text?.Trim() ?? "";
        
        if (string.IsNullOrWhiteSpace(zandPath))
        {
            await ShowMessageAsync("Zandronum Path Required", 
                "Please specify a path to the Zandronum executable.");
            return;
        }

        if (!File.Exists(zandPath))
        {
            await ShowMessageAsync("File Not Found", 
                $"The specified Zandronum executable was not found:\n{zandPath}");
            return;
        }

        // Save settings
        Settings.ZandronumPath = zandPath;

        if (!string.IsNullOrWhiteSpace(TestingFolderTextBox.Text))
            Settings.ZandronumTestingPath = TestingFolderTextBox.Text.Trim();

        // Save WAD paths
        var wadPaths = new List<string>();
        foreach (var item in _wadPaths)
        {
            var path = item;
            if (path.StartsWith(DownloadFolderPrefix))
                path = path[DownloadFolderPrefix.Length..];
            if (!string.IsNullOrWhiteSpace(path) && !wadPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                wadPaths.Add(path);
        }
        Settings.WadSearchPaths = wadPaths;

        if (!string.IsNullOrWhiteSpace(WadDownloadPathTextBox.Text))
            Settings.WadDownloadPath = WadDownloadPathTextBox.Text.Trim();

        // Save update settings
        Settings.UpdateBehavior = (UpdateBehavior)UpdateBehaviorComboBox.SelectedIndex;
        
        Settings.UpdateCheckIntervalValue = IntervalValue;
        Settings.UpdateCheckIntervalUnit = UpdateIntervalUnitComboBox.SelectedIndex switch
        {
            0 => UpdateIntervalUnit.Hours,
            1 => UpdateIntervalUnit.Days,
            2 => UpdateIntervalUnit.Weeks,
            _ => UpdateIntervalUnit.Days
        };
        
        Settings.AutoRestartForUpdates = AutoRestartCheckBox.IsChecked ?? false;

        _settingsService.Save();

        Close(true);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var msgBox = new Window
        {
            Title = title,
            Width = 400,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 15,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button
                    {
                        Content = "OK",
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Width = 80
                    }
                }
            }
        };

        var okButton = ((StackPanel)msgBox.Content).Children.OfType<Button>().First();
        okButton.Click += (_, _) => msgBox.Close();

        await msgBox.ShowDialog(this);
    }

    private async Task<bool> ShowConfirmationAsync(string title, string message)
    {
        var result = false;
        
        var msgBox = new Window
        {
            Title = title,
            Width = 420,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 15,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children =
                        {
                            new Button { Content = "No", Width = 80 },
                            new Button { Content = "Yes", Width = 80 }
                        }
                    }
                }
            }
        };

        var buttonPanel = ((StackPanel)msgBox.Content).Children.OfType<StackPanel>().First();
        var noButton = buttonPanel.Children.OfType<Button>().First();
        var yesButton = buttonPanel.Children.OfType<Button>().Last();
        
        noButton.Click += (_, _) => { result = false; msgBox.Close(); };
        yesButton.Click += (_, _) => { result = true; msgBox.Close(); };

        await msgBox.ShowDialog(this);
        return result;
    }
}
