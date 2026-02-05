using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using ZScape.Models;
using ZScape.Utilities;

namespace ZScape.Views;

/// <summary>
/// Advanced server filter configuration dialog.
/// </summary>
public partial class ServerFilterDialog : Window
{
    private readonly ServerFilter _filter;
    private readonly List<ServerFilter> _presets;

    public ObservableCollection<GameModeItem> IncludeModes { get; } = [];
    public ObservableCollection<GameModeItem> ExcludeModes { get; } = [];
    public ObservableCollection<CountryFilterItem> IncludeCountries { get; } = [];
    public ObservableCollection<CountryFilterItem> ExcludeCountries { get; } = [];
    
    // Master lists for modes and countries (unified for mutual exclusivity)
    private readonly List<GameModeItem> _allModes = [];
    private readonly List<CountryFilterItem> _allCountries = [];

    public ServerFilter Filter => _filter;
    public List<ServerFilter> Presets => _presets;
    public bool Confirmed { get; private set; }

    // Required for XAML runtime loader
    public ServerFilterDialog() : this(new ServerFilter(), []) { }

    public ServerFilterDialog(ServerFilter currentFilter, List<ServerFilter> presets)
    {
        _filter = currentFilter.Clone();
        _presets = presets.Select(p => p.Clone()).ToList();

        InitializeComponent();
        DataContext = this;
        
        // Handle Escape/Enter keys
        KeyDown += OnDialogKeyDown;
        
        Loaded += (_, _) =>
        {
            PopulateGameModes();
            PopulateCountries();
            LoadFilterToControls();
            PopulatePresets();

            IncludeModeSearchBox.TextChanged += (_, _) => FilterModeList(true, IncludeModeSearchBox.Text ?? "");
            ExcludeModeSearchBox.TextChanged += (_, _) => FilterModeList(false, ExcludeModeSearchBox.Text ?? "");
            IncludeCountrySearchBox.TextChanged += (_, _) => FilterCountryList(true, IncludeCountrySearchBox.Text ?? "");
            ExcludeCountrySearchBox.TextChanged += (_, _) => FilterCountryList(false, ExcludeCountrySearchBox.Text ?? "");
        };
    }
    
    private void OnDialogKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            CancelButton_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Avalonia.Input.Key.Enter && e.KeyModifiers == Avalonia.Input.KeyModifiers.None)
        {
            // Only trigger OK if not focused on a multi-line textbox
            var focusedElement = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (focusedElement is not TextBox textBox || !textBox.AcceptsReturn)
            {
                OkButton_Click(sender, e);
                e.Handled = true;
            }
        }
    }

    private void PopulateGameModes()
    {
        foreach (GameModeType mode in Enum.GetValues<GameModeType>())
        {
            if (mode != GameModeType.Unknown)
            {
                var gm = GameMode.FromType(mode);
                var item = new GameModeItem(mode, gm.Name);
                _allModes.Add(item);
            }
        }
        
        RefreshModeItemsControls();
    }
    
    private void RefreshModeItemsControls()
    {
        IncludeModes.Clear();
        ExcludeModes.Clear();
        int index = 0;
        foreach (var mode in _allModes)
        {
            mode.Index = index++;
            IncludeModes.Add(mode);
            ExcludeModes.Add(mode);
        }
        IncludeModesItemsControl.ItemsSource = IncludeModes;
        ExcludeModesItemsControl.ItemsSource = ExcludeModes;
    }

    private void PopulateCountries()
    {
        foreach (var country in CountryData.Countries)
        {
            var item = new CountryFilterItem(country.Code, country.Name);
            _allCountries.Add(item);
        }
        
        RefreshCountryItemsControls();
    }
    
    private void RefreshCountryItemsControls()
    {
        IncludeCountries.Clear();
        ExcludeCountries.Clear();
        int index = 0;
        foreach (var country in _allCountries)
        {
            country.Index = index++;
            IncludeCountries.Add(country);
            ExcludeCountries.Add(country);
        }
        IncludeCountriesItemsControl.ItemsSource = IncludeCountries;
        ExcludeCountriesItemsControl.ItemsSource = ExcludeCountries;
    }

    private void FilterModeList(bool isIncludeList, string searchText)
    {
        var filtered = string.IsNullOrEmpty(searchText)
            ? _allModes
            : _allModes.Where(m =>
                m.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        var target = isIncludeList ? IncludeModes : ExcludeModes;
        var control = isIncludeList ? IncludeModesItemsControl : ExcludeModesItemsControl;
        
        target.Clear();
        int index = 0;
        foreach (var m in filtered)
        {
            m.Index = index++;
            target.Add(m);
        }
        control.ItemsSource = target;
    }

    private void FilterCountryList(bool isIncludeList, string searchText)
    {
        var filtered = string.IsNullOrEmpty(searchText)
            ? _allCountries
            : _allCountries.Where(c =>
                c.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                c.Code.Contains(searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        var target = isIncludeList ? IncludeCountries : ExcludeCountries;
        var control = isIncludeList ? IncludeCountriesItemsControl : ExcludeCountriesItemsControl;
        
        target.Clear();
        int index = 0;
        foreach (var c in filtered)
        {
            c.Index = index++;
            target.Add(c);
        }
        control.ItemsSource = target;
    }

    private void LoadFilterToControls()
    {
        // Basic
        ShowEmptyCheckBox.IsChecked = _filter.ShowEmpty;
        HideBotOnlyCheckBox.IsChecked = _filter.TreatBotOnlyAsEmpty;
        ShowFullCheckBox.IsChecked = _filter.ShowFull;
        PasswordedComboBox.SelectedIndex = (int)_filter.PasswordedServers;
        ShowUnresponsiveCheckBox.IsChecked = _filter.ShowUnresponsive;
        PopulatedFirstCheckBox.IsChecked = _filter.PopulatedServersFirst;

        // Text filters
        ServerNameTextBox.Text = _filter.ServerNameFilter;
        ServerNameRegexCheckBox.IsChecked = _filter.ServerNameIsRegex;
        MapTextBox.Text = _filter.MapFilter;
        MapRegexCheckBox.IsChecked = _filter.MapIsRegex;
        VersionTextBox.Text = _filter.RequireVersion;

        // Game modes - set IsIncluded/IsExcluded based on filter
        foreach (var item in _allModes)
        {
            item.IsIncluded = _filter.IncludeGameModes.Contains(item.Mode);
            item.IsExcluded = _filter.ExcludeGameModes.Contains(item.Mode);
        }

        // WADs
        RequireWadsTextBox.Text = string.Join(Environment.NewLine, _filter.RequireWads);
        IncludeAnyWadsTextBox.Text = string.Join(Environment.NewLine, _filter.IncludeAnyWads);
        ExcludeWadsTextBox.Text = string.Join(Environment.NewLine, _filter.ExcludeWads);

        // Numeric
        MinPlayersNumeric.Value = _filter.MinPlayers;
        MaxPlayersNumeric.Value = _filter.MaxPlayers;
        MinHumansNumeric.Value = _filter.MinHumanPlayers;
        MinPingNumeric.Value = _filter.MinPing;
        MaxPingNumeric.Value = _filter.MaxPing;

        // Countries - set IsIncluded/IsExcluded based on filter
        foreach (var item in _allCountries)
        {
            item.IsIncluded = _filter.IncludeCountries.Contains(item.Code);
            item.IsExcluded = _filter.ExcludeCountries.Contains(item.Code);
        }
    }

    private void SaveControlsToFilter()
    {
        // Basic
        _filter.ShowEmpty = ShowEmptyCheckBox.IsChecked ?? true;
        _filter.TreatBotOnlyAsEmpty = HideBotOnlyCheckBox.IsChecked ?? false;
        _filter.ShowFull = ShowFullCheckBox.IsChecked ?? true;
        _filter.PasswordedServers = (FilterMode)PasswordedComboBox.SelectedIndex;
        _filter.ShowUnresponsive = ShowUnresponsiveCheckBox.IsChecked ?? false;
        _filter.PopulatedServersFirst = PopulatedFirstCheckBox.IsChecked ?? true;

        // Text filters
        _filter.ServerNameFilter = ServerNameTextBox.Text?.Trim() ?? "";
        _filter.ServerNameIsRegex = ServerNameRegexCheckBox.IsChecked ?? false;
        _filter.MapFilter = MapTextBox.Text?.Trim() ?? "";
        _filter.MapIsRegex = MapRegexCheckBox.IsChecked ?? false;
        _filter.RequireVersion = VersionTextBox.Text?.Trim() ?? "";

        // Game modes - get from IsIncluded/IsExcluded
        _filter.IncludeGameModes.Clear();
        _filter.ExcludeGameModes.Clear();
        foreach (var item in _allModes)
        {
            if (item.IsIncluded)
                _filter.IncludeGameModes.Add(item.Mode);
            if (item.IsExcluded)
                _filter.ExcludeGameModes.Add(item.Mode);
        }

        // WADs
        _filter.RequireWads = ParseWadList(RequireWadsTextBox.Text ?? "");
        _filter.IncludeAnyWads = ParseWadList(IncludeAnyWadsTextBox.Text ?? "");
        _filter.ExcludeWads = ParseWadList(ExcludeWadsTextBox.Text ?? "");

        // Numeric
        _filter.MinPlayers = MinPlayersNumeric.Value;
        _filter.MaxPlayers = MaxPlayersNumeric.Value;
        _filter.MinHumanPlayers = MinHumansNumeric.Value;
        _filter.MinPing = MinPingNumeric.Value;
        _filter.MaxPing = MaxPingNumeric.Value;

        // Countries - get from IsIncluded/IsExcluded
        _filter.IncludeCountries.Clear();
        _filter.ExcludeCountries.Clear();
        foreach (var item in _allCountries)
        {
            if (item.IsIncluded)
                _filter.IncludeCountries.Add(item.Code);
            if (item.IsExcluded)
                _filter.ExcludeCountries.Add(item.Code);
        }
    }

    private static List<string> ParseWadList(string text)
    {
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    private void PopulatePresets()
    {
        PresetComboBox.Items.Clear();
        PresetComboBox.Items.Add("(Current filter)");
        foreach (var preset in _presets)
        {
            PresetComboBox.Items.Add(preset.Name);
        }
        PresetComboBox.SelectedIndex = 0;
        PresetComboBox.SelectionChanged += PresetComboBox_SelectionChanged;
    }

    private void PresetComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PresetComboBox.SelectedIndex > 0)
        {
            var preset = _presets[PresetComboBox.SelectedIndex - 1];
            CopyPresetToFilter(preset);
            LoadFilterToControls();
        }
        DeletePresetButton.IsEnabled = PresetComboBox.SelectedIndex > 0;
    }

    private void CopyPresetToFilter(ServerFilter preset)
    {
        _filter.Name = preset.Name;
        _filter.Enabled = preset.Enabled;
        _filter.ShowEmpty = preset.ShowEmpty;
        _filter.ShowFull = preset.ShowFull;
        _filter.PasswordedServers = preset.PasswordedServers;
        _filter.ShowUnresponsive = preset.ShowUnresponsive;
        _filter.ServerNameFilter = preset.ServerNameFilter;
        _filter.ServerNameIsRegex = preset.ServerNameIsRegex;
        _filter.MapFilter = preset.MapFilter;
        _filter.MapIsRegex = preset.MapIsRegex;
        _filter.IncludeGameModes = [.. preset.IncludeGameModes];
        _filter.ExcludeGameModes = [.. preset.ExcludeGameModes];
        _filter.RequireWads = [.. preset.RequireWads];
        _filter.IncludeAnyWads = [.. preset.IncludeAnyWads];
        _filter.ExcludeWads = [.. preset.ExcludeWads];
        _filter.RequireIWAD = preset.RequireIWAD;
        _filter.MinPlayers = preset.MinPlayers;
        _filter.MaxPlayers = preset.MaxPlayers;
        _filter.MinHumanPlayers = preset.MinHumanPlayers;
        _filter.MaxPing = preset.MaxPing;
        _filter.PopulatedServersFirst = preset.PopulatedServersFirst;
        _filter.TestingServers = preset.TestingServers;
        _filter.RequireVersion = preset.RequireVersion;
    }

    private async void SavePresetButton_Click(object? sender, RoutedEventArgs e)
    {
        SaveControlsToFilter();

        var dialog = new Window
        {
            Title = "Save Preset",
            Width = 300,
            Height = 130,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };

        string? presetName = null;
        var grid = new Grid { Margin = new Avalonia.Thickness(15) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(new GridLength(10)));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var label = new TextBlock { Text = "Preset name:" };
        Grid.SetRow(label, 0);
        grid.Children.Add(label);

        var textBox = new TextBox { Text = _filter.Name };
        Grid.SetRow(textBox, 2);
        grid.Children.Add(textBox);

        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10
        };
        Grid.SetRow(buttonPanel, 4);

        var saveBtn = new Button { Content = "Save", Width = 75 };
        saveBtn.Click += (_, _) => { presetName = textBox.Text; dialog.Close(); };
        var cancelBtn = new Button { Content = "Cancel", Width = 75 };
        cancelBtn.Click += (_, _) => { dialog.Close(); };

        buttonPanel.Children.Add(saveBtn);
        buttonPanel.Children.Add(cancelBtn);
        grid.Children.Add(buttonPanel);

        dialog.Content = grid;
        await dialog.ShowDialog(this);

        if (!string.IsNullOrWhiteSpace(presetName))
        {
            _filter.Name = presetName.Trim();
            var existingIndex = _presets.FindIndex(p => p.Name.Equals(_filter.Name, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                _presets[existingIndex] = _filter.Clone();
            }
            else
            {
                _presets.Add(_filter.Clone());
            }
            PopulatePresets();
            PresetComboBox.SelectedIndex = _presets.FindIndex(p => p.Name == _filter.Name) + 1;
        }
    }

    private async void DeletePresetButton_Click(object? sender, RoutedEventArgs e)
    {
        if (PresetComboBox.SelectedIndex <= 0) return;

        var presetIndex = PresetComboBox.SelectedIndex - 1;
        var presetName = _presets[presetIndex].Name;

        var result = await ShowConfirmDialog($"Delete preset '{presetName}'?");
        if (result)
        {
            _presets.RemoveAt(presetIndex);
            PopulatePresets();
        }
    }

    private void ClearButton_Click(object? sender, RoutedEventArgs e)
    {
        // Reset to defaults
        ShowEmptyCheckBox.IsChecked = true;
        HideBotOnlyCheckBox.IsChecked = false;
        ShowFullCheckBox.IsChecked = true;
        PasswordedComboBox.SelectedIndex = 0;
        ShowUnresponsiveCheckBox.IsChecked = false;
        PopulatedFirstCheckBox.IsChecked = true;

        ServerNameTextBox.Text = "";
        ServerNameRegexCheckBox.IsChecked = false;
        MapTextBox.Text = "";
        MapRegexCheckBox.IsChecked = false;
        VersionTextBox.Text = "";

        // Clear mode selections
        foreach (var mode in _allModes)
        {
            mode.IsIncluded = false;
            mode.IsExcluded = false;
        }

        RequireWadsTextBox.Text = "";
        IncludeAnyWadsTextBox.Text = "";
        ExcludeWadsTextBox.Text = "";

        MinPlayersNumeric.Value = 0;
        MaxPlayersNumeric.Value = 0;
        MinHumansNumeric.Value = 0;
        MinPingNumeric.Value = 0;
        MaxPingNumeric.Value = 0;

        // Clear country selections
        foreach (var country in _allCountries)
        {
            country.IsIncluded = false;
            country.IsExcluded = false;
        }
        IncludeCountrySearchBox.Text = "";
        ExcludeCountrySearchBox.Text = "";

        PresetComboBox.SelectedIndex = 0;
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        SaveControlsToFilter();
        Confirmed = true;
        Close();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }

    private void IncludeModeRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is GameModeItem item)
        {
            item.IsIncluded = !item.IsIncluded;
            e.Handled = true;
        }
    }

    private void ExcludeModeRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is GameModeItem item)
        {
            item.IsExcluded = !item.IsExcluded;
            e.Handled = true;
        }
    }

    private void IncludeCountryRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is CountryFilterItem item)
        {
            item.IsIncluded = !item.IsIncluded;
            e.Handled = true;
        }
    }

    private void ExcludeCountryRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is CountryFilterItem item)
        {
            item.IsExcluded = !item.IsExcluded;
            e.Handled = true;
        }
    }

    private static readonly IBrush HoverBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));

    private void FilterRow_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Border border)
        {
            border.Tag = border.Background;
            border.Background = HoverBrush;
        }
    }

    private void FilterRow_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border border && border.Tag is IBrush originalBrush)
        {
            border.Background = originalBrush;
        }
    }

    private async Task<bool> ShowConfirmDialog(string message)
    {
        var dialog = new Window
        {
            Title = "Confirm",
            Width = 300,
            Height = 120,
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
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
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
/// Represents a game mode for filter selection with checkbox binding.
/// </summary>
public class GameModeItem : INotifyPropertyChanged
{
    private bool _isIncluded;
    private bool _isExcluded;
    
    public GameModeType Mode { get; }
    public string Name { get; }
    public int Index { get; set; }
    
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (_isIncluded != value)
            {
                _isIncluded = value;
                // Mutual exclusivity: if included, cannot be excluded
                if (value && _isExcluded)
                {
                    _isExcluded = false;
                    OnPropertyChanged(nameof(IsExcluded));
                }
                OnPropertyChanged();
            }
        }
    }
    
    public bool IsExcluded
    {
        get => _isExcluded;
        set
        {
            if (_isExcluded != value)
            {
                _isExcluded = value;
                // Mutual exclusivity: if excluded, cannot be included
                if (value && _isIncluded)
                {
                    _isIncluded = false;
                    OnPropertyChanged(nameof(IsIncluded));
                }
                OnPropertyChanged();
            }
        }
    }

    public GameModeItem(GameModeType mode, string name, int index = 0)
    {
        Mode = mode;
        Name = name;
        Index = index;
    }

    public override string ToString() => Name;
    
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Represents a country for filter selection with checkbox binding.
/// </summary>
public class CountryFilterItem : INotifyPropertyChanged
{
    private bool _isIncluded;
    private bool _isExcluded;
    
    public string Code { get; }
    public string Name { get; }
    public int Index { get; set; }
    
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (_isIncluded != value)
            {
                _isIncluded = value;
                // Mutual exclusivity: if included, cannot be excluded
                if (value && _isExcluded)
                {
                    _isExcluded = false;
                    OnPropertyChanged(nameof(IsExcluded));
                }
                OnPropertyChanged();
            }
        }
    }
    
    public bool IsExcluded
    {
        get => _isExcluded;
        set
        {
            if (_isExcluded != value)
            {
                _isExcluded = value;
                // Mutual exclusivity: if excluded, cannot be included
                if (value && _isIncluded)
                {
                    _isIncluded = false;
                    OnPropertyChanged(nameof(IsIncluded));
                }
                OnPropertyChanged();
            }
        }
    }

    public CountryFilterItem(string code, string name, int index = 0)
    {
        Code = code;
        Name = name;
        Index = index;
    }

    public override string ToString() => Name;
    
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Converter for alternating row background colors.
/// </summary>
public class AlternatingRowConverter : IValueConverter
{
    private static readonly IBrush EvenBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)); // Transparent
    private static readonly IBrush OddBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)); // Darker overlay
    
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int index)
        {
            return index % 2 == 0 ? EvenBrush : OddBrush;
        }
        return EvenBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
