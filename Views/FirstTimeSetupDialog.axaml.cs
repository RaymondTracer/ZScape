using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ZScape.Controls;
using ZScape.Services;
using ZScape.Utilities;

namespace ZScape.Views;

public partial class FirstTimeSetupDialog : Window
{
    private enum StableDownloadMode
    {
        Cancel,
        Automatic,
        Website
    }

    private sealed record StableReleaseDiscoveryResult(
        ZandronumStableReleaseService.ReleaseManifest? Release,
        string? ErrorMessage,
        bool Cancelled);

    private readonly SettingsService _settingsService;
    private readonly ZandronumStableReleaseService _stableReleaseService;
    private AppSettings Settings => _settingsService.Settings;
    public UpdateServerState? PrefetchedServerState { get; private set; }
    private readonly ObservableCollection<string> _wadPaths = new();
    private const string DownloadFolderPrefix = "[Download Folder] ";
    private const int PageCount = 4;
    private bool _applyingPreset;
    private int _currentPageIndex;
    private ZandronumStableReleaseService.ReleaseManifest? _observedLatestStableRelease;
    private int _observedStableServerCount;

    public FirstTimeSetupDialog()
    {
        InitializeComponent();
        _settingsService = SettingsService.Instance;
        _stableReleaseService = ZandronumStableReleaseService.Instance;
        
        WadPathsListBox.ItemsSource = _wadPaths;
        WadPathsListBox.SelectionChanged += WadPathsListBox_SelectionChanged;
        WadDownloadPathTextBox.TextChanged += WadDownloadPathTextBox_TextChanged;
        UpdateIntervalSpinner.ValueChanged += IntervalSpinner_Changed;
        UpdateIntervalUnitComboBox.SelectionChanged += IntervalUnit_Changed;
        UpdatePresetsComboBox.SelectionChanged += UpdatePresets_SelectionChanged;
        
        // Handle Escape key to show exit confirmation
        KeyDown += OnDialogKeyDown;
        
        LoadDefaults();
        ConfigureStableDownloadUi();
        UpdateWizardUi();
    }
    
    private void OnDialogKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            if (_currentPageIndex == 0)
            {
                ExitButton_Click(sender, e);
            }
            else
            {
                BackButton_Click(sender, e);
            }

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

    private void ConfigureStableDownloadUi()
    {
        if (_observedLatestStableRelease != null)
        {
            LatestStableTextBlock.Text =
                $"Latest stable observed from {_observedStableServerCount} live stable server(s): {_observedLatestStableRelease.Version} ({_observedLatestStableRelease.PlatformLabel}). ZScape can download that archive automatically or open the official download in your browser.";
            DownloadStableButton.IsEnabled = true;
            return;
        }

        if (_stableReleaseService.IsStableReleasePlatformSupported(out var errorMessage))
        {
            LatestStableTextBlock.Text =
                "ZScape can refresh the master server to discover the latest stable version currently used by live servers, then either download it automatically or open the official archive in your browser.";
            DownloadStableButton.IsEnabled = true;
            return;
        }

        LatestStableTextBlock.Text = errorMessage ?? "Automatic stable installs are not available on this platform yet.";
        DownloadStableButton.IsEnabled = false;
    }

    private void UpdateWizardUi()
    {
        ZandronumPagePanel.IsVisible = _currentPageIndex == 0;
        WadPagePanel.IsVisible = _currentPageIndex == 1;
        UpdatesPagePanel.IsVisible = _currentPageIndex == 2;

        StepIndicatorTextBlock.Text = $"Step {_currentPageIndex + 1} of {PageCount}";
        ExitButton.IsVisible = _currentPageIndex == 0;
        BackButton.IsVisible = _currentPageIndex > 0;
        NextButton.Content = _currentPageIndex == PageCount - 1 ? "Finish Setup" : "Next";

        switch (_currentPageIndex)
        {
            case 0:
                PageTitleTextBlock.Text = "Zandronum Setup";
                PageDescriptionTextBlock.Text = "Point ZScape at an existing install or download the latest stable release into a folder you choose.";
                break;
            case 1:
                PageTitleTextBlock.Text = "WAD Settings";
                PageDescriptionTextBlock.Text = "Set the download folder and any extra WAD locations ZScape should search before launch.";
                break;
            case 2:
                PageTitleTextBlock.Text = "Theme & Interface";
                PageDescriptionTextBlock.Text = "Choose your preferred theme and how much of ZScape's features you want to see.";
                break;
            default:
                PageTitleTextBlock.Text = "Updates & Finish";
                PageDescriptionTextBlock.Text = "Choose how ZScape should check for its own updates, then finish the initial setup.";
                break;
        }
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
        {
            if (_wadPaths.Count > 0 && _wadPaths[0].StartsWith(DownloadFolderPrefix, StringComparison.Ordinal))
            {
                _wadPaths.RemoveAt(0);
            }

            return;
        }

        var displayText = DownloadFolderPrefix + downloadPath;

        if (_wadPaths.Count == 0)
        {
            _wadPaths.Add(displayText);
        }
        else if (_wadPaths[0].StartsWith(DownloadFolderPrefix, StringComparison.Ordinal))
        {
            _wadPaths[0] = displayText;
        }
        else
        {
            _wadPaths.Insert(0, displayText);
        }
    }

    private void LoadDefaults()
    {
        Dispatcher.UIThread.Post(() =>
        {
            ApplyInitialUpdateSelections();
        }, DispatcherPriority.Loaded);
        
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
        LoadExistingWadPaths();
    }

    private void ApplyInitialUpdateSelections()
    {
        UpdateBehaviorComboBox.SelectedIndex = Math.Clamp((int)Settings.UpdateBehavior, 0, 2);

        IntervalValue = Math.Max(1, Settings.UpdateCheckIntervalValue);
        UpdateIntervalUnitComboBox.SelectedIndex = Settings.UpdateCheckIntervalUnit switch
        {
            UpdateIntervalUnit.Hours => 0,
            UpdateIntervalUnit.Days => 1,
            UpdateIntervalUnit.Weeks => 2,
            _ => 1
        };

        _applyingPreset = true;
        UpdatePresetsComboBox.SelectedIndex = (IntervalValue, UpdateIntervalUnitComboBox.SelectedIndex) switch
        {
            (6, 0) => 1,
            (1, 1) => 2,
            (1, 2) => 3,
            (4, 2) => 4,
            _ => 0
        };
        _applyingPreset = false;

        AutoRestartCheckBox.IsChecked = Settings.AutoRestartForUpdates;
    }

    private void LoadExistingWadPaths()
    {
        var downloadPath = WadDownloadPathTextBox.Text?.Trim() ?? string.Empty;
        foreach (var path in Settings.WadSearchPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(downloadPath) && path.Equals(downloadPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_wadPaths.Any(existing => NormalizeDisplayedWadPath(existing).Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _wadPaths.Add(path);
        }
    }

    private static string NormalizeDisplayedWadPath(string path) =>
        path.StartsWith(DownloadFolderPrefix, StringComparison.Ordinal)
            ? path[DownloadFolderPrefix.Length..]
            : path;

    private void PopulateRelatedFoldersFromZandronumPath(string executablePath)
    {
        var zandronumDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(zandronumDirectory))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(WadDownloadPathTextBox.Text))
        {
            WadDownloadPathTextBox.Text = zandronumDirectory;
        }

        if (string.IsNullOrWhiteSpace(TestingFolderTextBox.Text))
        {
            TestingFolderTextBox.Text = Path.Combine(zandronumDirectory, "TestingVersions");
        }
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
            PopulateRelatedFoldersFromZandronumPath(files[0].Path.LocalPath);
        }
    }

    private async void DownloadStableButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_stableReleaseService.IsStableReleasePlatformSupported(out var errorMessage))
        {
            await ShowMessageAsync("Stable Download Unavailable", errorMessage ?? "Automatic stable installs are not available on this platform yet.");
            return;
        }

        var mode = await PromptStableDownloadModeAsync();
        if (mode == StableDownloadMode.Cancel)
        {
            return;
        }

        var release = await ResolveLatestStableReleaseAsync();
        if (release == null)
        {
            return;
        }

        if (mode == StableDownloadMode.Automatic)
        {
            await DownloadStableAutomaticallyAsync(release);
            return;
        }

        await DownloadStableFromWebsiteAsync(release);
    }

    private async Task DownloadStableAutomaticallyAsync(ZandronumStableReleaseService.ReleaseManifest release)
    {
        var installFolders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Choose the install folder for Zandronum {release.Version}",
            AllowMultiple = false
        });

        if (installFolders.Count == 0)
        {
            return;
        }

        var installDirectory = installFolders[0].Path.LocalPath;
        if (!await EnsureInstallDirectoryReadyAsync(installDirectory, release.Version))
        {
            return;
        }

        var installResult = await ShowStableInstallProgressAsync(release, installDirectory);
        if (installResult == null)
        {
            return;
        }

        ZandronumPathTextBox.Text = installResult.ExecutablePath;
        PopulateRelatedFoldersFromZandronumPath(installResult.ExecutablePath);

        await ShowMessageAsync(
            "Zandronum Installed",
            $"Zandronum {installResult.Version} was installed successfully.\n\nExecutable:\n{installResult.ExecutablePath}");
    }

    private async Task DownloadStableFromWebsiteAsync(ZandronumStableReleaseService.ReleaseManifest release)
    {

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = release.DownloadUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Browser Launch Failed", $"Could not open the official Zandronum download in your browser:\n{ex.Message}");
            return;
        }

        var readyToExtract = await ShowConfirmationAsync(
            "Choose Downloaded Archive",
            $"Your browser should now be downloading the official Zandronum {release.Version} archive. Save it wherever you want.\n\nClick Yes when the download finishes and you are ready to let ZScape extract it.");

        if (!readyToExtract)
        {
            return;
        }

        var archiveFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Select the downloaded Zandronum {release.Version} archive",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Zandronum Archive") { Patterns = _stableReleaseService.GetArchivePickerPatterns(release) },
                new FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });

        if (archiveFiles.Count == 0)
        {
            return;
        }

        var archivePath = archiveFiles[0].Path.LocalPath;
        var archiveDirectory = Path.GetDirectoryName(archivePath);
        if (string.IsNullOrEmpty(archiveDirectory))
        {
            await ShowMessageAsync("Invalid Archive Location", "ZScape could not determine where the downloaded archive was saved.");
            return;
        }

        var installDirectory = Path.Combine(archiveDirectory, _stableReleaseService.GetSuggestedInstallFolderName(release));

        if (!await EnsureInstallDirectoryReadyAsync(installDirectory, release.Version))
        {
            return;
        }

        var installResult = await ShowStableInstallProgressAsync(release, installDirectory, archivePath);
        if (installResult == null)
        {
            return;
        }

        ZandronumPathTextBox.Text = installResult.ExecutablePath;
        PopulateRelatedFoldersFromZandronumPath(installResult.ExecutablePath);

        await ShowMessageAsync(
            "Zandronum Installed",
            $"Zandronum {installResult.Version} was installed successfully.\n\nExecutable:\n{installResult.ExecutablePath}");
    }

    private async Task<bool> EnsureInstallDirectoryReadyAsync(string installDirectory, string version)
    {
        if (!Directory.Exists(installDirectory) || !Directory.EnumerateFileSystemEntries(installDirectory).Any())
        {
            return true;
        }

        var confirmed = await ShowConfirmationAsync(
            "Overwrite Existing Folder",
            $"{installDirectory}\n\nalready exists and is not empty. Replace it with a fresh Zandronum {version} install?");

        if (!confirmed)
        {
            return false;
        }

        try
        {
            Directory.Delete(installDirectory, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Unable To Replace Folder", $"Could not clear the existing install folder:\n{ex.Message}");
            return false;
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

    private void BackButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentPageIndex == 0)
        {
            return;
        }

        _currentPageIndex--;
        UpdateWizardUi();
    }

    private async void NextButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!await ValidateCurrentPageAsync())
        {
            return;
        }

        if (_currentPageIndex < PageCount - 1)
        {
            _currentPageIndex++;
            UpdateWizardUi();
            return;
        }

        if (!await ValidateZandronumSelectionAsync())
        {
            _currentPageIndex = 0;
            UpdateWizardUi();
            return;
        }

        SaveSettings();
        Close(true);
    }

    private async Task<bool> ValidateCurrentPageAsync()
    {
        return _currentPageIndex switch
        {
            0 => await ValidateZandronumSelectionAsync(),
            _ => true
        };
    }

    private async Task<bool> ValidateZandronumSelectionAsync()
    {
        var zandPath = ZandronumPathTextBox.Text?.Trim() ?? "";
        
        if (string.IsNullOrWhiteSpace(zandPath))
        {
            await ShowMessageAsync("Zandronum Path Required", 
                "Please specify a path to the Zandronum executable.");
            return false;
        }

        if (!File.Exists(zandPath))
        {
            await ShowMessageAsync("File Not Found", 
                $"The specified Zandronum executable was not found:\n{zandPath}");
            return false;
        }

        return true;
    }

    private void SaveSettings()
    {
        var zandPath = ZandronumPathTextBox.Text?.Trim() ?? string.Empty;

        // Save settings
        Settings.ZandronumPath = zandPath;
        Settings.ZandronumTestingPath = TestingFolderTextBox.Text?.Trim() ?? string.Empty;

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
        Settings.WadDownloadPath = WadDownloadPathTextBox.Text?.Trim() ?? string.Empty;

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
        Settings.Theme = ThemeDarkRadio.IsChecked == true ? AppTheme.Dark : AppTheme.Light;
        Settings.ThemeId = ThemeDarkRadio.IsChecked == true ? "Dark" : "Light";
        _settingsService.Save();
    }

    private async Task<ZandronumStableReleaseService.InstallResult?> ShowStableInstallProgressAsync(
        ZandronumStableReleaseService.ReleaseManifest release,
        string installDirectory,
        string? archivePath = null)
    {
        var progressWindow = new Window
        {
            Title = $"Installing Zandronum {release.Version}",
            Width = 480,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var statusLabel = new TextBlock
        {
            Text = archivePath == null ? "Preparing download..." : "Preparing extraction...",
            Margin = new Thickness(20, 20, 20, 4),
            FontSize = 14,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var progressDetailLabel = new TextBlock
        {
            Text = string.Empty,
            Margin = new Thickness(20, 0, 20, 2),
            Foreground = Avalonia.Media.Brushes.LightGray
        };
        var speedLabel = new TextBlock
        {
            Text = string.Empty,
            Margin = new Thickness(20, 0, 20, 2),
            Foreground = Avalonia.Media.Brushes.LightGray
        };
        var etaLabel = new TextBlock
        {
            Text = string.Empty,
            Margin = new Thickness(20, 0, 20, 6),
            Foreground = Avalonia.Media.Brushes.LightGray
        };
        var progressBar = new ProgressBar
        {
            Margin = new Thickness(20, 4, 20, 5),
            Height = 20,
            Minimum = 0,
            Maximum = 100
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 100,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };

        progressWindow.Content = new StackPanel
        {
            Children = { statusLabel, progressDetailLabel, speedLabel, etaLabel, progressBar, cancelButton }
        };

        using var cts = new CancellationTokenSource();
        var cancelled = false;
        Exception? failure = null;
        ZandronumStableReleaseService.InstallResult? result = null;

        cancelButton.Click += (_, _) =>
        {
            cancelled = true;
            cancelButton.IsEnabled = false;
            statusLabel.Text = "Cancelling...";
            cts.Cancel();
        };

        var progress = new Progress<ZandronumStableReleaseService.InstallProgress>(update =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                statusLabel.Text = update.Status;
                progressBar.Value = update.ProgressPercent;

                progressDetailLabel.Text = update.TotalBytes > 0
                    ? $"Transferred: {FormatUtils.FormatBytes(update.DownloadedBytes)} / {FormatUtils.FormatBytes(update.TotalBytes)}"
                    : update.DownloadedBytes > 0
                        ? $"Transferred: {FormatUtils.FormatBytes(update.DownloadedBytes)}"
                        : string.Empty;

                speedLabel.Text = update.BytesPerSecond > 0
                    ? $"Speed: {FormatUtils.FormatSpeed(update.BytesPerSecond)}"
                    : string.Empty;

                etaLabel.Text = update.EstimatedTimeRemaining.HasValue
                    ? $"ETA: {FormatDownloadEta(update.EstimatedTimeRemaining.Value)}"
                    : string.Empty;
            });
        });

        progressWindow.Opened += async (_, _) =>
        {
            try
            {
                result = archivePath == null
                    ? await _stableReleaseService.DownloadAndInstallAsync(release, installDirectory, progress, cts.Token)
                    : await _stableReleaseService.InstallFromArchiveAsync(release, archivePath, installDirectory, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (progressWindow.IsVisible)
                {
                    progressWindow.Close();
                }
            }
        };

        await progressWindow.ShowDialog(this);

        if (failure != null)
        {
            await ShowMessageAsync("Download Failed", failure.Message);
            return null;
        }

        return cancelled ? null : result;
    }

    private async Task<ZandronumStableReleaseService.ReleaseManifest?> ResolveLatestStableReleaseAsync()
    {
        if (_observedLatestStableRelease != null)
        {
            return _observedLatestStableRelease;
        }

        var discovery = await DiscoverLatestStableReleaseAsync();
        if (discovery.Release != null)
        {
            return discovery.Release;
        }

        if (discovery.Cancelled)
        {
            return null;
        }

        await ShowMessageAsync(
            "Latest Stable Version Not Found",
            discovery.ErrorMessage ?? "ZScape could not determine the latest stable version from live servers. Try again later.");
        return null;
    }

    private async Task<StableReleaseDiscoveryResult> DiscoverLatestStableReleaseAsync()
    {
        if (_observedLatestStableRelease != null)
        {
            return new StableReleaseDiscoveryResult(_observedLatestStableRelease, null, false);
        }

        var progressWindow = new Window
        {
            Title = "Discovering Latest Stable Version",
            Width = 500,
            Height = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var statusLabel = new TextBlock
        {
            Text = "Refreshing the master server list...",
            Margin = new Thickness(20, 20, 20, 6),
            FontSize = 14,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var detailsLabel = new TextBlock
        {
            Text = "ZScape will reuse these results for the main server list after setup.",
            Margin = new Thickness(20, 0, 20, 8),
            Foreground = Avalonia.Media.Brushes.LightGray,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var progressBar = new ProgressBar
        {
            Margin = new Thickness(20, 4, 20, 6),
            Height = 20,
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = false
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 100,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };

        progressWindow.Content = new StackPanel
        {
            Children = { statusLabel, detailsLabel, progressBar, cancelButton }
        };

        using var browserService = new ServerBrowserService();
        using var cts = new CancellationTokenSource();
        var cancelled = false;
        string? errorMessage = null;
        ZandronumStableReleaseService.ReleaseManifest? discoveredRelease = null;
        var matchingServerCount = 0;

        browserService.RefreshStarted += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                statusLabel.Text = "Refreshing the master server list...";
                progressBar.Value = 0;
            });
        };

        browserService.RefreshProgress += (_, progress) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                statusLabel.Text = $"Querying live servers... {progress}%";
                progressBar.Value = Math.Min(progress, 100);
            });
        };

        browserService.RefreshCompleted += (_, args) =>
        {
            if (!args.Success)
            {
                errorMessage = args.Error;
            }
        };

        cancelButton.Click += (_, _) =>
        {
            cancelled = true;
            cancelButton.IsEnabled = false;
            statusLabel.Text = "Cancelling...";
            cts.Cancel();
        };

        progressWindow.Opened += async (_, _) =>
        {
            try
            {
                await browserService.RefreshAsync(cts.Token);

                if (!cancelled && string.IsNullOrWhiteSpace(errorMessage))
                {
                    PrefetchedServerState = browserService.GetServerState();

                    if (_stableReleaseService.TryGetLatestObservedRelease(browserService.Servers, out var release, out matchingServerCount, out var releaseError))
                    {
                        discoveredRelease = release;
                    }
                    else
                    {
                        errorMessage = releaseError;
                    }
                }
            }
            finally
            {
                if (progressWindow.IsVisible)
                {
                    progressWindow.Close();
                }
            }
        };

        await progressWindow.ShowDialog(this);

        if (discoveredRelease != null)
        {
            _observedLatestStableRelease = discoveredRelease;
            _observedStableServerCount = matchingServerCount;
            ConfigureStableDownloadUi();
            return new StableReleaseDiscoveryResult(discoveredRelease, null, false);
        }

        return new StableReleaseDiscoveryResult(null, errorMessage, cancelled);
    }

    private async Task<StableDownloadMode> PromptStableDownloadModeAsync()
    {
        var result = StableDownloadMode.Cancel;

        var dialog = new Window
        {
            Title = "Download Latest Stable",
            Width = 560,
            Height = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 15,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Do you want to download the latest stable version from the website, or automatically?\n\nAutomatic first refreshes the master server to find the newest stable version currently used by live servers, keeps those results for the main server list after setup, then downloads the matching archive directly. Website uses the same discovered version but lets your browser handle the download.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children =
                        {
                            new Button { Content = "Cancel", Width = 90 },
                            new Button { Content = "Website", Width = 100 },
                            new Button { Content = "Automatic", Width = 100 }
                        }
                    }
                }
            }
        };

        if (dialog.Content is StackPanel stackPanel && stackPanel.Children.LastOrDefault() is StackPanel buttons)
        {
            if (buttons.Children[0] is Button cancelButton) cancelButton.Click += (_, _) => dialog.Close();
            if (buttons.Children[1] is Button websiteButton) websiteButton.Click += (_, _) => { result = StableDownloadMode.Website; dialog.Close(); };
            if (buttons.Children[2] is Button automaticButton) automaticButton.Click += (_, _) => { result = StableDownloadMode.Automatic; dialog.Close(); };
        }

        await dialog.ShowDialog(this);
        return result;
    }

    private static string FormatDownloadEta(TimeSpan eta)
    {
        if (eta < TimeSpan.Zero)
        {
            eta = TimeSpan.Zero;
        }

        if (eta.TotalHours >= 1)
        {
            return $"{(int)eta.TotalHours}:{eta.Minutes:D2}:{eta.Seconds:D2}";
        }

        if (eta.TotalMinutes >= 1)
        {
            return $"{(int)eta.TotalMinutes}:{eta.Seconds:D2}";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(eta.TotalSeconds))}s";
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
