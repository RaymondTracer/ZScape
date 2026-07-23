using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using ZScape.Models;
using ZScape.Services;

namespace ZScape.Views;

/// <summary>
/// Dialog for launching Zandronum in offline (single-player) or host-server mode.
/// Settings are remembered across sessions and can be saved/loaded as named configs.
/// Passwords are never persisted.
/// </summary>
public partial class LaunchGameDialog : Window
{
    private static readonly LaunchGameConfig DefaultConfig = new();
    private readonly WadManager _wadManager = WadManager.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;
    private bool _isLoadingConfig;

    /// <summary>The selected IWAD full path.</summary>
    public string? SelectedIwadPath { get; private set; }

    /// <summary>The selected PWAD full paths.</summary>
    public IReadOnlyList<string> SelectedPwadPaths { get; private set; } = [];

    /// <summary>The selected Zandronum executable path, or null for stable.</summary>
    public string? SelectedExePath { get; private set; }

    /// <summary>True if host-server mode, false if offline mode.</summary>
    public bool IsHostMode { get; private set; }

    /// <summary>True if dedicated server (host only), false if listen server.</summary>
    public bool IsDedicated { get; private set; }

    /// <summary>Whether the dialog was confirmed.</summary>
    public bool Confirmed { get; private set; }

    private readonly ObservableCollection<string> _pwads = [];

    public LaunchGameDialog()
    {
        InitializeComponent();

        KeyDown += OnDialogKeyDown;
        Loaded += OnLoaded;
        IwadComboBox.SelectionChanged += IwadComboBox_SelectionChanged;
        PwadsListBox.ItemsSource = _pwads;
        PwadsListBox.SelectionChanged += PwadsListBox_SelectionChanged;
        PwadsListBox.KeyDown += PwadsListBox_KeyDown;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        PopulateVersions();
        PopulateIwads();
        PopulateConfigPresets();
        ApplyLastConfig();
        UpdateLaunchButton();
    }

    private void OnDialogKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            CancelButton_Click(sender, e);
            e.Handled = true;
        }
    }

    // ── Version selection ─────────────────────────────────────────────

    private void PopulateVersions()
    {
        VersionComboBox.Items.Clear();

        var stablePath = _settings.Settings.ZandronumPath;
        if (!string.IsNullOrEmpty(stablePath))
        {
            VersionComboBox.Items.Add(new ComboBoxItem
            {
                Content = File.Exists(stablePath) ? "Stable" : "Stable (not found)",
                Tag = null
            });
        }

        var testingRoot = GameLauncher.Instance.GetTestingRootPath();
        if (!string.IsNullOrEmpty(testingRoot) && Directory.Exists(testingRoot))
        {
            foreach (var dir in Directory.GetDirectories(testingRoot).OrderDescending())
            {
                var exePath = Path.Combine(dir, "zandronum.exe");
                if (File.Exists(exePath))
                {
                    var versionName = Path.GetFileName(dir);
                    VersionComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = versionName,
                        Tag = exePath
                    });
                }
            }
        }

        if (VersionComboBox.Items.Count == 0)
        {
            VersionComboBox.Items.Add(new ComboBoxItem
            {
                Content = "(no Zandronum installation found)",
                IsEnabled = false
            });
        }

        VersionComboBox.SelectedIndex = 0;
    }

    private void VersionComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedExePath();
    }

    private void UpdateSelectedExePath()
    {
        if (VersionComboBox.SelectedItem is ComboBoxItem item)
            SelectedExePath = item.Tag as string;
        else
            SelectedExePath = null;
    }

    // ── IWAD population ──────────────────────────────────────────────

    private void PopulateIwads()
    {
        _wadManager.RefreshCache();
        var iwads = _wadManager.EnumerateIwads();

        IwadComboBox.Items.Clear();
        if (iwads.Count == 0)
        {
            IwadComboBox.Items.Add(new ComboBoxItem
            {
                Content = "(no IWADs found in search paths)",
                IsEnabled = false
            });
            return;
        }

        foreach (var (displayName, fullPath) in iwads)
        {
            var item = new ComboBoxItem
            {
                Content = $"{displayName}  ({fullPath})",
                Tag = fullPath
            };
            IwadComboBox.Items.Add(item);
        }

        if (IwadComboBox.Items.Count > 0)
        {
            IwadComboBox.SelectedIndex = 0;
        }
    }

    // ── IWAD selection ───────────────────────────────────────────────

    private void IwadComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateLaunchButton();
        UpdateSelectedIwad();
    }

    private void UpdateSelectedIwad()
    {
        if (IwadComboBox.SelectedItem is ComboBoxItem item && item.Tag is string path)
            SelectedIwadPath = path;
        else
            SelectedIwadPath = null;
    }

    private void UpdateLaunchButton()
    {
        UpdateSelectedIwad();
        LaunchButton.IsEnabled = !string.IsNullOrEmpty(SelectedIwadPath);
    }

    // ── Mode radio ───────────────────────────────────────────────────

    private void LaunchModeRadio_Click(object? sender, RoutedEventArgs e)
    {
        IsHostMode = HostModeRadio.IsChecked == true;
        HostOptionsPanel.IsVisible = IsHostMode;
        MaxPlayersPanel.IsVisible = IsHostMode;
        MaxClientsPanel.IsVisible = IsHostMode;
        HostAdvancedPanel.IsVisible = IsHostMode;
    }

    // ── PWAD picker ──────────────────────────────────────────────────

    private async void AddPwadButton_Click(object? sender, RoutedEventArgs e)
    {
        var storageProvider = StorageProvider;
        if (storageProvider is null) return;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select PWAD Files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("WAD/PK3 Files")
                {
                    Patterns = ["*.wad", "*.pk3", "*.pk7", "*.zip", "*.7z"]
                }
            ]
        });

        foreach (var file in files)
        {
            var path = file.Path.LocalPath;
            if (!_pwads.Contains(path, StringComparer.OrdinalIgnoreCase))
                _pwads.Add(path);
        }

        foreach (var file in files)
        {
            var dir = Path.GetDirectoryName(file.Path.LocalPath);
            if (!string.IsNullOrEmpty(dir))
                _wadManager.AddSearchPath(dir);
        }
    }

    private void PwadsListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var idx = PwadsListBox.SelectedIndex;
        var count = _pwads.Count;
        RemovePwadButton.IsEnabled = idx >= 0;
        MoveUpPwadButton.IsEnabled = idx > 0;
        MoveDownPwadButton.IsEnabled = idx >= 0 && idx < count - 1;

        if (idx >= 0 && idx < _pwads.Count)
            PwadEditTextBox.Text = _pwads[idx];
        else
            PwadEditTextBox.Text = "";
    }

    private void RemovePwadButton_Click(object? sender, RoutedEventArgs e)
    {
        var idx = PwadsListBox.SelectedIndex;
        if (idx < 0 || idx >= _pwads.Count) return;
        _pwads.RemoveAt(idx);
    }

    private void MoveUpPwadButton_Click(object? sender, RoutedEventArgs e)
    {
        var idx = PwadsListBox.SelectedIndex;
        if (idx <= 0 || idx >= _pwads.Count) return;
        _pwads.Move(idx, idx - 1);
        PwadsListBox.SelectedIndex = idx - 1;
    }

    private void MoveDownPwadButton_Click(object? sender, RoutedEventArgs e)
    {
        var idx = PwadsListBox.SelectedIndex;
        if (idx < 0 || idx >= _pwads.Count - 1) return;
        _pwads.Move(idx, idx + 1);
        PwadsListBox.SelectedIndex = idx + 1;
    }

    private void PwadEditTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var idx = PwadsListBox.SelectedIndex;
        if (idx < 0 || idx >= _pwads.Count) return;
        var newPath = PwadEditTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(newPath)) return;
        if (_pwads[idx] == newPath) return;

        // Don't allow duplicate entries
        for (int i = 0; i < _pwads.Count; i++)
        {
            if (i != idx && _pwads[i].Equals(newPath, StringComparison.OrdinalIgnoreCase))
                return;
        }

        _pwads[idx] = newPath;
    }

    private void PwadsListBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Delete)
        {
            RemovePwadButton_Click(sender, e);
            e.Handled = true;
        }
    }

    // ── Config presets ───────────────────────────────────────────────

    private void PopulateConfigPresets()
    {
        _isLoadingConfig = true;
        ConfigPresetComboBox.Items.Clear();
        ConfigPresetComboBox.Items.Add(new ComboBoxItem
        {
            Content = "(last session)",
            Tag = null
        });

        foreach (var named in _settings.Settings.SavedLaunchGameConfigs)
        {
            ConfigPresetComboBox.Items.Add(new ComboBoxItem
            {
                Content = named.Name,
                Tag = named
            });
        }

        ConfigPresetComboBox.SelectedIndex = 0;
        _isLoadingConfig = false;
    }

    private void ConfigPresetComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingConfig) return;

        if (ConfigPresetComboBox.SelectedItem is ComboBoxItem item)
        {
            if (item.Tag is NamedLaunchGameConfig named)
                ApplyConfig(named.Config);
            else
                ApplyConfig(DefaultConfig);
        }
    }

    private void ApplyLastConfig()
    {
        var last = _settings.Settings.LastLaunchGameConfig;
        if (last != null)
        {
            ApplyConfig(last);
        }
    }

    private void ApplyConfig(LaunchGameConfig config)
    {
        _isLoadingConfig = true;

        // Mode
        OfflineModeRadio.IsChecked = !config.IsHostMode;
        HostModeRadio.IsChecked = config.IsHostMode;
        IsHostMode = config.IsHostMode;
        HostOptionsPanel.IsVisible = config.IsHostMode;
        MaxPlayersPanel.IsVisible = config.IsHostMode;
        MaxClientsPanel.IsVisible = config.IsHostMode;
        HostAdvancedPanel.IsVisible = config.IsHostMode;

        // Server type
        ListenServerRadio.IsChecked = !config.IsDedicated;
        DedicatedServerRadio.IsChecked = config.IsDedicated;
        IsDedicated = config.IsDedicated;

        // Version
        if (!string.IsNullOrEmpty(config.ExePath))
        {
            for (int i = 0; i < VersionComboBox.Items.Count; i++)
            {
                if (VersionComboBox.Items[i] is ComboBoxItem verItem &&
                    verItem.Tag is string verPath &&
                    verPath.Equals(config.ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    VersionComboBox.SelectedIndex = i;
                    break;
                }
            }
        }

        // IWAD
        if (!string.IsNullOrEmpty(config.IwadPath))
        {
            for (int i = 0; i < IwadComboBox.Items.Count; i++)
            {
                if (IwadComboBox.Items[i] is ComboBoxItem iwadItem &&
                    iwadItem.Tag is string path &&
                    path.Equals(config.IwadPath, StringComparison.OrdinalIgnoreCase))
                {
                    IwadComboBox.SelectedIndex = i;
                    break;
                }
            }
        }

        // PWADs
        _pwads.Clear();
        foreach (var pwad in config.PwadPaths)
        {
            if (File.Exists(pwad))
                _pwads.Add(pwad);
        }

        // Map
        MapTextBox.Text = config.Map ?? "";

        // Skill (1-5 map to 0-4 index)
        var skillIndex = Math.Clamp(config.Skill - 1, 0, 4);
        SkillComboBox.SelectedIndex = skillIndex;

        // Max players / clients
        MaxPlayersNumeric.Value = Math.Clamp(config.MaxPlayers, 1, 64);
        MaxClientsNumeric.Value = Math.Clamp(config.MaxClients, 2, 64);

        // Server name
        ServerNameTextBox.Text = config.ServerName ?? "";

        _isLoadingConfig = false;
        UpdateLaunchButton();
    }

    private LaunchGameConfig SnapshotConfig()
    {
        return new LaunchGameConfig
        {
            IsHostMode = HostModeRadio.IsChecked == true,
            IsDedicated = DedicatedServerRadio.IsChecked == true,
            ExePath = SelectedExePath,
            IwadPath = SelectedIwadPath,
            PwadPaths = _pwads.ToList(),
            Map = string.IsNullOrWhiteSpace(MapTextBox.Text) ? null : MapTextBox.Text.Trim(),
            Skill = SkillComboBox.SelectedIndex + 1,
            MaxPlayers = MaxPlayersNumeric.Value,
            MaxClients = MaxClientsNumeric.Value,
            ServerName = string.IsNullOrWhiteSpace(ServerNameTextBox.Text) ? null : ServerNameTextBox.Text.Trim()
        };
    }

    private async void SaveConfigButton_Click(object? sender, RoutedEventArgs e)
    {
        var name = await PromptForConfigNameAsync();
        if (string.IsNullOrWhiteSpace(name))
            return;

        var config = SnapshotConfig();
        var saved = _settings.Settings.SavedLaunchGameConfigs;

        var existing = saved.FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Config = config;
        }
        else
        {
            saved.Add(new NamedLaunchGameConfig { Name = name, Config = config });
        }

        _settings.NotifySettingChanged();
        PopulateConfigPresets();

        // Select the saved config
        for (int i = 0; i < ConfigPresetComboBox.Items.Count; i++)
        {
            if (ConfigPresetComboBox.Items[i] is ComboBoxItem item &&
                item.Content is string content &&
                content.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                ConfigPresetComboBox.SelectedIndex = i;
                break;
            }
        }

        StatusLabel.Text = $"Saved '{name}'";
    }

    private void ClearConfigButton_Click(object? sender, RoutedEventArgs e)
    {
        ApplyConfig(DefaultConfig);
        ConfigPresetComboBox.SelectedIndex = 0;
        StatusLabel.Text = "Reset to defaults";
    }

    private void DeleteConfigButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ConfigPresetComboBox.SelectedItem is ComboBoxItem item &&
            item.Tag is NamedLaunchGameConfig named)
        {
            _settings.Settings.SavedLaunchGameConfigs.Remove(named);
            _settings.NotifySettingChanged();
            PopulateConfigPresets();
            ApplyConfig(_settings.Settings.LastLaunchGameConfig ?? DefaultConfig);
            StatusLabel.Text = $"Deleted '{named.Name}'";
        }
    }

    private async Task<string?> PromptForConfigNameAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        var dialog = new Window
        {
            Title = "Save Configuration",
            Width = 350,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        var textBox = new TextBox
        {
            Watermark = "e.g. DM with Brutal Doom",
            Width = 300,
            Margin = new Thickness(15, 10, 15, 0)
        };

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(15, 8, 15, 0),
            Children =
            {
                new Button { Content = "Save", Width = 70 },
                new Button { Content = "Cancel", Width = 70 }
            }
        };

        if (buttons.Children[0] is Button saveBtn)
            saveBtn.Click += (_, _) => { tcs.TrySetResult(textBox.Text?.Trim()); dialog.Close(); };
        if (buttons.Children[1] is Button cancelBtn)
            cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Children = { textBox, buttons }
        };

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                tcs.TrySetResult(textBox.Text?.Trim());
                dialog.Close();
                e.Handled = true;
            }
            else if (e.Key == Avalonia.Input.Key.Escape)
            {
                tcs.TrySetResult(null);
                dialog.Close();
                e.Handled = true;
            }
        };

        await dialog.ShowDialog(this);
        return await tcs.Task;
    }

    // ── Launch / Cancel ──────────────────────────────────────────────

    private void LaunchButton_Click(object? sender, RoutedEventArgs e)
    {
        UpdateSelectedIwad();
        UpdateSelectedExePath();
        if (string.IsNullOrEmpty(SelectedIwadPath))
            return;

        IsHostMode = HostModeRadio.IsChecked == true;
        IsDedicated = IsHostMode && DedicatedServerRadio.IsChecked == true;
        SelectedPwadPaths = _pwads.ToList();
        Confirmed = true;

        // Persist last config
        _settings.Settings.LastLaunchGameConfig = SnapshotConfig();
        _settings.NotifySettingChanged();

        Close();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    // ── Public accessors ─────────────────────────────────────────────

    public string? GetSelectedIwadName()
    {
        if (IwadComboBox.SelectedItem is ComboBoxItem item && item.Content is string content)
        {
            var parenIndex = content.IndexOf("  (");
            if (parenIndex > 0)
                return content.Substring(0, parenIndex);
        }
        return null;
    }

    public int GetSkill() => SkillComboBox.SelectedIndex + 1;

    public string? GetMap()
    {
        var map = MapTextBox.Text?.Trim();
        return string.IsNullOrWhiteSpace(map) ? null : map;
    }

    public int GetMaxPlayers() => MaxPlayersNumeric.Value;
    public int GetMaxClients() => MaxClientsNumeric.Value;

    public string? GetServerName()
    {
        var name = ServerNameTextBox.Text?.Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public string? GetServerPassword()
    {
        var pw = ServerPasswordTextBox.Text;
        return string.IsNullOrWhiteSpace(pw) ? null : pw;
    }

    public string? GetJoinPassword()
    {
        var pw = JoinPasswordTextBox.Text;
        return string.IsNullOrWhiteSpace(pw) ? null : pw;
    }
}
