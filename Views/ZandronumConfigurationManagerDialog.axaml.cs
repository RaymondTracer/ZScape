using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ZScape.Controls;
using ZScape.Models;
using ZScape.Services;

namespace ZScape.Views;

/// <summary>
/// Lets the user compare the per-user Zandronum INI files for the versions
/// ZScape manages, choose a section-level sync scope, and opt into safe
/// post-exit synchronization for versions ZScape launches.
/// </summary>
public partial class ZandronumConfigurationManagerDialog : Window
{
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly ZandronumConfigSyncService _syncService = ZandronumConfigSyncService.Instance;
    private readonly ObservableCollection<ConfigurationVersionRow> _versions = [];
    private readonly Dictionary<string, CheckBox> _sectionCheckBoxes =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _updatingControls;
    private bool _isBusy;

    public ZandronumConfigurationManagerDialog()
    {
        InitializeComponent();
        DataContext = this;

        DescriptionText.Text =
            $"Compare and synchronize {ZandronumConfigSyncService.UserConfigurationFileName} between the stable, archived stable, testing, and saved-launch versions ZScape knows about.";

        SetupVersionList();
        KeyDown += OnDialogKeyDown;
        Loaded += async (_, _) => await ReloadVersionsAsync(preserveSelectedSource: false);
    }

    private void SetupVersionList()
    {
        VersionListView.SelectionMode = ListViewSelectionMode.Single;
        VersionListView.AlternatingRowColors = true;
        VersionListView.RowHeight = 26;
        VersionListView.SelectionChanged += VersionListView_SelectionChanged;

        VersionListView.AddColumn(new ListViewColumn
        {
            Key = "version",
            Header = "Version",
            BindingPath = nameof(ConfigurationVersionRow.DisplayName),
            Width = 175,
            MinWidth = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            AutoSizeTextPath = nameof(ConfigurationVersionRow.DisplayName)
        });
        VersionListView.AddColumn(new ListViewColumn
        {
            Key = "status",
            Header = "Status",
            BindingPath = nameof(ConfigurationVersionRow.StatusDisplay),
            Width = 78,
            MinWidth = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            AutoSizeTextPath = nameof(ConfigurationVersionRow.StatusDisplay)
        });
        VersionListView.AddColumn(new ListViewColumn
        {
            Key = "modified",
            Header = "Modified",
            BindingPath = nameof(ConfigurationVersionRow.LastModifiedDisplay),
            Width = 125,
            MinWidth = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            AutoSizeTextPath = nameof(ConfigurationVersionRow.LastModifiedDisplay)
        });
        VersionListView.AddColumn(new ListViewColumn
        {
            Key = "changes",
            Header = "Changes",
            BindingPath = nameof(ConfigurationVersionRow.DifferenceDisplay),
            Width = 96,
            MinWidth = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            AutoSizeTextPath = nameof(ConfigurationVersionRow.DifferenceDisplay)
        });
        VersionListView.AddColumn(new ListViewColumn
        {
            Key = "path",
            Header = "Configuration file",
            BindingPath = nameof(ConfigurationVersionRow.ConfigurationPath),
            Width = 220,
            IsStar = true,
            MinWidth = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            AutoSizeTextPath = nameof(ConfigurationVersionRow.ConfigurationPath)
        });

        VersionListView.Build(ListViewOverflowMode.Fill);
        VersionListView.ItemsSource = _versions;
    }

    private async Task ReloadVersionsAsync(bool preserveSelectedSource)
    {
        if (_isBusy)
            return;

        var previousSourcePath = preserveSelectedSource
            ? GetSelectedSource()?.ConfigurationPath
            : null;

        SetBusy(true);
        StatusText.Text = "Finding the Zandronum versions configured in ZScape...";

        try
        {
            var configurations = await Task.Run(_syncService.DiscoverConfigurations);
            var sections = await Task.Run(() => _syncService.GetAvailableSections(configurations));

            _updatingControls = true;
            try
            {
                _versions.Clear();
                foreach (var configuration in configurations)
                    _versions.Add(new ConfigurationVersionRow(configuration));

                PopulateSectionChoices(sections);

                var selected = _versions.FirstOrDefault(row =>
                    !string.IsNullOrWhiteSpace(previousSourcePath) &&
                    PathsEqual(row.ConfigurationPath, previousSourcePath));
                selected ??= _versions.FirstOrDefault(row =>
                    row.ConfigurationExists &&
                    row.DisplayName.Equals("Configured stable", StringComparison.OrdinalIgnoreCase));
                selected ??= _versions.FirstOrDefault(row => row.ConfigurationExists);

                if (selected != null)
                    VersionListView.SelectItem(selected);
                else
                    VersionListView.ClearSelection();

                ApplySavedOptionsToControls();
            }
            finally
            {
                _updatingControls = false;
            }

            VersionListHint.Text = configurations.Count == 0
                ? "No Zandronum executable is configured yet. Set the stable or testing path in Preferences first."
                : "Select one ready version as the source. The Changes column is relative to that source and the selected sync scope.";

            await RefreshComparisonAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not inspect Zandronum configurations: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PopulateSectionChoices(IEnumerable<string> sections)
    {
        SectionsPanel.Children.Clear();
        _sectionCheckBoxes.Clear();

        var selectedSections = new HashSet<string>(
            _settings.Settings.ZandronumConfigSync.SelectedSections ?? [],
            StringComparer.OrdinalIgnoreCase);

        var materializedSections = sections.ToList();
        if (materializedSections.Count == 0)
        {
            SectionsPanel.Children.Add(new TextBlock
            {
                Text = "No readable INI sections were found yet.",
                Foreground = ThemeService.GetBrush("TextSecondaryBrush", "#A0A0A0"),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var section in materializedSections)
        {
            var checkBox = new CheckBox
            {
                Content = section,
                IsChecked = selectedSections.Contains(section),
                Margin = new Thickness(0, 1)
            };
            ToolTip.SetTip(checkBox, $"Synchronize the [{section}] INI section");
            checkBox.Tag = section;
            checkBox.IsCheckedChanged += SectionCheckBox_IsCheckedChanged;
            SectionsPanel.Children.Add(checkBox);
            _sectionCheckBoxes.Add(section, checkBox);
        }
    }

    private void ApplySavedOptionsToControls()
    {
        var options = _settings.Settings.ZandronumConfigSync;
        AutoSyncCheckBox.IsChecked = options.AutoSyncEnabled;
        SyncWholeFileCheckBox.IsChecked = options.SyncWholeFile;

        foreach (var pair in _sectionCheckBoxes)
            pair.Value.IsChecked = options.SelectedSections.Contains(pair.Key, StringComparer.OrdinalIgnoreCase);

        UpdateControlStates();
    }

    private async void VersionListView_SelectionChanged(object? sender, EventArgs e)
    {
        if (!_updatingControls)
            await RefreshComparisonAsync();
    }

    private async Task RefreshComparisonAsync()
    {
        if (_updatingControls)
            return;

        var source = GetSelectedSource();
        var options = CreateOptionsSnapshot();

        foreach (var row in _versions)
            row.DifferenceDisplay = "—";

        UpdateControlStates();

        if (source == null)
        {
            StatusText.Text = "Select a configuration file to use as the source.";
            return;
        }

        if (!source.ConfigurationExists)
        {
            StatusText.Text = "The selected version has not created its per-user INI file yet.";
            return;
        }

        if (!ZandronumConfigSyncService.HasSyncScope(options))
        {
            source.DifferenceDisplay = "Source";
            StatusText.Text = "Select one or more INI sections, or choose entire-file sync, before comparing or syncing.";
            return;
        }

        SetBusy(true);
        try
        {
            var comparisons = await Task.Run(() => _syncService.Compare(
                source.ConfigurationPath,
                _versions.Select(row => row.ConfigurationPath).ToList(),
                options));
            var comparisonByPath = comparisons.ToDictionary(
                comparison => comparison.TargetConfigurationPath,
                comparison => comparison,
                GetPathComparer());

            var differentTargets = 0;
            foreach (var row in _versions)
            {
                if (PathsEqual(row.ConfigurationPath, source.ConfigurationPath))
                {
                    row.DifferenceDisplay = "Source";
                    continue;
                }

                if (!row.ConfigurationExists)
                {
                    row.DifferenceDisplay = "INI missing";
                    continue;
                }

                if (!comparisonByPath.TryGetValue(row.ConfigurationPath, out var comparison))
                {
                    row.DifferenceDisplay = "—";
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(comparison.ErrorMessage))
                {
                    row.DifferenceDisplay = "Unavailable";
                    continue;
                }

                if (comparison.DifferentSectionCount == 0)
                {
                    row.DifferenceDisplay = "Up to date";
                }
                else
                {
                    differentTargets++;
                    row.DifferenceDisplay = options.SyncWholeFile
                        ? $"{comparison.DifferentSectionCount} difference{(comparison.DifferentSectionCount == 1 ? string.Empty : "s")}" 
                        : $"{comparison.DifferentSectionCount} section{(comparison.DifferentSectionCount == 1 ? string.Empty : "s")}";
                }
            }

            StatusText.Text = differentTargets == 0
                ? "Every existing target configuration already matches the current sync scope."
                : $"{differentTargets} version{(differentTargets == 1 ? string.Empty : "s")} would be updated from {source.DisplayName}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not compare configurations: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateControlStates()
    {
        var options = _settings.Settings.ZandronumConfigSync;
        var hasScope = ZandronumConfigSyncService.HasSyncScope(options);
        var source = GetSelectedSource();
        var canUseSource = source?.ConfigurationExists == true;
        var hasTarget = _versions.Any(row => row.ConfigurationExists &&
            (source == null || !PathsEqual(row.ConfigurationPath, source.ConfigurationPath)));

        SectionsScrollViewer.IsEnabled = !options.SyncWholeFile && !_isBusy;
        SelectCommonButton.IsEnabled = !options.SyncWholeFile && !_isBusy && _sectionCheckBoxes.Count > 0;
        ClearSectionsButton.IsEnabled = !options.SyncWholeFile && !_isBusy && _sectionCheckBoxes.Count > 0;
        AutoSyncCheckBox.IsEnabled = !_isBusy && hasScope;
        ToolTip.SetTip(AutoSyncCheckBox, hasScope
            ? "Synchronize the selected scope after a Zandronum process launched by ZScape closes."
            : "Select INI sections or choose entire-file sync before enabling automatic synchronization.");
        RefreshButton.IsEnabled = !_isBusy;
        CompareButton.IsEnabled = !_isBusy && canUseSource && hasScope;
        SyncButton.IsEnabled = !_isBusy && canUseSource && hasScope && hasTarget;

        if (options.SyncWholeFile)
        {
            ScopeSummaryText.Text = "Entire-file sync is selected. The selected source INI will replace each differing target INI.";
        }
        else
        {
            var selectedCount = options.SelectedSections.Count;
            ScopeSummaryText.Text = selectedCount == 0
                ? "No sections selected. Nothing can be synchronized until you select a scope."
                : $"{selectedCount} INI section{(selectedCount == 1 ? string.Empty : "s")} selected. Non-selected sections remain untouched in every target.";
        }
    }

    private void SaveOptions()
    {
        _settings.Save();
    }

    private void EnsureAutoSyncHasScope()
    {
        var options = _settings.Settings.ZandronumConfigSync;
        if (ZandronumConfigSyncService.HasSyncScope(options) || !options.AutoSyncEnabled)
            return;

        options.AutoSyncEnabled = false;
        _updatingControls = true;
        try
        {
            AutoSyncCheckBox.IsChecked = false;
        }
        finally
        {
            _updatingControls = false;
        }
    }

    private void AutoSyncCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingControls)
            return;

        var options = _settings.Settings.ZandronumConfigSync;
        if (AutoSyncCheckBox.IsChecked == true && !ZandronumConfigSyncService.HasSyncScope(options))
        {
            StatusText.Text = "Choose a synchronization scope before enabling automatic synchronization.";
            _updatingControls = true;
            try
            {
                AutoSyncCheckBox.IsChecked = false;
            }
            finally
            {
                _updatingControls = false;
            }
            return;
        }

        options.AutoSyncEnabled = AutoSyncCheckBox.IsChecked == true;
        SaveOptions();
        StatusText.Text = options.AutoSyncEnabled
            ? "Automatic synchronization is enabled for future ZScape-launched Zandronum sessions."
            : "Automatic synchronization is disabled. Manual compare and sync remain available.";
    }

    private async void SyncWholeFileCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingControls)
            return;

        _settings.Settings.ZandronumConfigSync.SyncWholeFile = SyncWholeFileCheckBox.IsChecked == true;
        SaveOptions();
        UpdateControlStates();
        await RefreshComparisonAsync();
    }

    private async void SectionCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingControls || sender is not CheckBox checkBox || checkBox.Tag is not string sectionName)
            return;

        var options = _settings.Settings.ZandronumConfigSync;
        var selected = new HashSet<string>(options.SelectedSections ?? [], StringComparer.OrdinalIgnoreCase);
        if (checkBox.IsChecked == true)
            selected.Add(sectionName);
        else
            selected.Remove(sectionName);

        options.SelectedSections = selected.OrderBy(section => section, StringComparer.OrdinalIgnoreCase).ToList();
        EnsureAutoSyncHasScope();
        SaveOptions();
        UpdateControlStates();
        await RefreshComparisonAsync();
    }

    private async void SelectCommonButton_Click(object? sender, RoutedEventArgs e)
    {
        var commonSections = _sectionCheckBoxes.Keys
            .Where(ZandronumConfigSyncService.IsCommonSettingsSection)
            .ToList();
        SetSelectedSections(commonSections);
        await RefreshComparisonAsync();
    }

    private async void ClearSectionsButton_Click(object? sender, RoutedEventArgs e)
    {
        SetSelectedSections([]);
        await RefreshComparisonAsync();
    }

    private void SetSelectedSections(IEnumerable<string> sections)
    {
        var options = _settings.Settings.ZandronumConfigSync;
        options.SelectedSections = sections
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .Select(section => section.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(section => section, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _updatingControls = true;
        try
        {
            foreach (var pair in _sectionCheckBoxes)
                pair.Value.IsChecked = options.SelectedSections.Contains(pair.Key, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _updatingControls = false;
        }

        EnsureAutoSyncHasScope();
        SaveOptions();
        UpdateControlStates();
    }

    private async void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        await ReloadVersionsAsync(preserveSelectedSource: true);
    }

    private async void CompareButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshComparisonAsync();
    }

    private async void SyncButton_Click(object? sender, RoutedEventArgs e)
    {
        var source = GetSelectedSource();
        var options = CreateOptionsSnapshot();
        if (source == null || !source.ConfigurationExists || !ZandronumConfigSyncService.HasSyncScope(options))
            return;

        var targetCount = _versions.Count(row => row.ConfigurationExists &&
            !PathsEqual(row.ConfigurationPath, source.ConfigurationPath));
        if (targetCount == 0)
        {
            StatusText.Text = "There are no other existing configuration files to update.";
            return;
        }

        var scopeDescription = options.SyncWholeFile
            ? "the entire INI file"
            : $"{options.SelectedSections.Count} selected INI section{(options.SelectedSections.Count == 1 ? string.Empty : "s")}";
        if (!await ConfirmSyncAsync(source.DisplayName, targetCount, scopeDescription))
            return;

        SetBusy(true);
        StatusText.Text = "Synchronizing configuration files...";
        try
        {
            var result = await _syncService.SynchronizeFromConfigurationAsync(source.ConfigurationPath, options);
            if (result.Errors.Count > 0)
            {
                StatusText.Text = $"Updated {result.UpdatedFileCount}; {result.Errors.Count} failed. {result.Errors[0]}";
            }
            else
            {
                StatusText.Text = result.UpdatedFileCount == 0
                    ? "No configuration files needed changes."
                    : $"Updated {result.UpdatedFileCount} configuration file{(result.UpdatedFileCount == 1 ? string.Empty : "s")}. A .zscape-sync.bak backup was kept beside each changed file.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Configuration synchronization failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }

        await ReloadVersionsAsync(preserveSelectedSource: true);
    }

    private async Task<bool> ConfirmSyncAsync(string sourceName, int targetCount, string scopeDescription)
    {
        var confirmation = new Window
        {
            Title = "Confirm Configuration Sync",
            Width = 510,
            Height = 230,
            MinWidth = 430,
            MinHeight = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var accepted = false;
        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("*,Auto") };
        var message = new TextBlock
        {
            Text = $"Copy {scopeDescription} from \"{sourceName}\" to {targetCount} other Zandronum version{(targetCount == 1 ? string.Empty : "s")} now?\n\nEach changed target is backed up as {ZandronumConfigSyncService.UserConfigurationFileName}.zscape-sync.bak before it is replaced.",
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(message, 0);
        root.Children.Add(message);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        Grid.SetRow(buttons, 1);

        var sync = new Button { Content = "Sync", MinWidth = 76, IsDefault = true };
        sync.Click += (_, _) =>
        {
            accepted = true;
            confirmation.Close();
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 76, IsCancel = true };
        cancel.Click += (_, _) => confirmation.Close();
        buttons.Children.Add(sync);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        confirmation.Content = root;
        await confirmation.ShowDialog(this);
        return accepted;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        Close();
        e.Handled = true;
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        UpdateControlStates();
    }

    private ConfigurationVersionRow? GetSelectedSource() =>
        VersionListView.SelectedItem as ConfigurationVersionRow;

    private ZandronumConfigSyncSettings CreateOptionsSnapshot()
    {
        var options = _settings.Settings.ZandronumConfigSync;
        return new ZandronumConfigSyncSettings
        {
            AutoSyncEnabled = options.AutoSyncEnabled,
            SyncWholeFile = options.SyncWholeFile,
            SelectedSections = options.SelectedSections.ToList()
        };
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class ConfigurationVersionRow : INotifyPropertyChanged
    {
        private string _differenceDisplay = "—";

        public ConfigurationVersionRow(ZandronumConfigurationVersion configuration)
        {
            DisplayName = configuration.DisplayName;
            ConfigurationPath = configuration.ConfigurationPath;
            ConfigurationExists = configuration.ConfigurationExists;
            StatusDisplay = configuration.StatusDisplay;
            LastModifiedDisplay = configuration.LastModifiedDisplay;
        }

        public string DisplayName { get; }
        public string ConfigurationPath { get; }
        public bool ConfigurationExists { get; }
        public string StatusDisplay { get; }
        public string LastModifiedDisplay { get; }

        public string DifferenceDisplay
        {
            get => _differenceDisplay;
            set
            {
                if (string.Equals(_differenceDisplay, value, StringComparison.Ordinal))
                    return;

                _differenceDisplay = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
