using ZScape.Models;
using ZScape.Utilities;

namespace ZScape.UI;

/// <summary>
/// Advanced server filter configuration dialog.
/// </summary>
public class ServerFilterDialog : Form
{
    private readonly ServerFilter _filter;
    private readonly List<ServerFilter> _presets;

    // Controls
    private TabControl tabControl = null!;
    
    // Basic tab
    private CheckBox showEmptyCheckBox = null!;
    private CheckBox hideBotOnlyCheckBox = null!;
    private CheckBox showFullCheckBox = null!;
    private ComboBox passwordedComboBox = null!;
    private CheckBox showUnresponsiveCheckBox = null!;
    private CheckBox populatedFirstCheckBox = null!;
    
    // Text filters tab
    private TextBox serverNameTextBox = null!;
    private CheckBox serverNameRegexCheckBox = null!;
    private TextBox mapTextBox = null!;
    private CheckBox mapRegexCheckBox = null!;
    private TextBox iwadTextBox = null!;
    private TextBox versionTextBox = null!;
    
    // Game modes tab
    private CheckedListBox includeModesListBox = null!;
    private CheckedListBox excludeModesListBox = null!;
    
    // WADs tab
    private TextBox requireWadsTextBox = null!;
    private TextBox includeAnyWadsTextBox = null!;
    private TextBox excludeWadsTextBox = null!;
    
    // Numeric tab
    private NumericUpDown minPlayersNumeric = null!;
    private NumericUpDown maxPlayersNumeric = null!;
    private NumericUpDown minHumansNumeric = null!;
    private NumericUpDown minPingNumeric = null!;
    private NumericUpDown maxPingNumeric = null!;
    
    // Country tab
    private CheckedListBox includeCountriesListBox = null!;
    private CheckedListBox excludeCountriesListBox = null!;
    private TextBox includeCountrySearchBox = null!;
    private TextBox excludeCountrySearchBox = null!;
    private readonly HashSet<string> _checkedIncludeCountries = [];
    private readonly HashSet<string> _checkedExcludeCountries = [];
    private bool _isLoadingCountries;
    
    // Presets
    private ComboBox presetComboBox = null!;
    private Button savePresetButton = null!;
    private Button deletePresetButton = null!;

    // Dialog buttons
    private Button okButton = null!;
    private Button cancelButton = null!;
    private Button clearButton = null!;

    public ServerFilter Filter => _filter;
    public List<ServerFilter> Presets => _presets;

    public ServerFilterDialog(ServerFilter currentFilter, List<ServerFilter> presets)
    {
        _filter = currentFilter.Clone();
        _presets = presets.Select(p => p.Clone()).ToList();
        
        InitializeComponent();
        ApplyDarkTheme();
        DarkModeHelper.ApplyDarkTitleBar(this);
        LoadFilterToControls();
        PopulatePresets();
    }

    private void InitializeComponent()
    {
        Text = "Advanced Server Filter";
        Size = new Size(550, 520);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        // Preset panel at top
        var presetPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(10, 5, 10, 5)
        };

        var presetLabel = new Label { Text = "Preset:", Location = new Point(10, 12), AutoSize = true };
        presetComboBox = new ComboBox
        {
            Location = new Point(60, 8),
            Size = new Size(200, 25),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        presetComboBox.SelectedIndexChanged += PresetComboBox_SelectedIndexChanged;

        savePresetButton = new Button { Text = "Save", Location = new Point(270, 7), Size = new Size(60, 25) };
        savePresetButton.Click += SavePresetButton_Click;

        deletePresetButton = new Button { Text = "Delete", Location = new Point(335, 7), Size = new Size(60, 25) };
        deletePresetButton.Click += DeletePresetButton_Click;

        presetPanel.Controls.AddRange([presetLabel, presetComboBox, savePresetButton, deletePresetButton]);

        // Tab control
        tabControl = new TabControl
        {
            Dock = DockStyle.Fill
        };

        tabControl.TabPages.Add(CreateBasicTab());
        tabControl.TabPages.Add(CreateTextFiltersTab());
        tabControl.TabPages.Add(CreateGameModesTab());
        tabControl.TabPages.Add(CreateWadsTab());
        tabControl.TabPages.Add(CreateNumericTab());
        tabControl.TabPages.Add(CreateCountryTab());

        // Button panel at bottom
        var buttonPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 45,
            Padding = new Padding(10)
        };

        okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(80, 28),
            Location = new Point(260, 8)
        };
        okButton.Click += OkButton_Click;

        cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Size = new Size(80, 28),
            Location = new Point(345, 8)
        };

        clearButton = new Button
        {
            Text = "Clear All",
            Size = new Size(80, 28),
            Location = new Point(10, 8)
        };
        clearButton.Click += ClearButton_Click;

        buttonPanel.Controls.AddRange([clearButton, okButton, cancelButton]);

        Controls.Add(tabControl);
        Controls.Add(presetPanel);
        Controls.Add(buttonPanel);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private TabPage CreateBasicTab()
    {
        var tab = new TabPage("Basic");
        tab.Padding = new Padding(15);

        var y = 15;
        const int spacing = 30;

        showEmptyCheckBox = new CheckBox { Text = "Show empty servers", Location = new Point(15, y), AutoSize = true };
        y += spacing;

        hideBotOnlyCheckBox = new CheckBox { Text = "Hide bot-only servers (treat as empty)", Location = new Point(15, y), AutoSize = true };
        y += spacing;

        showFullCheckBox = new CheckBox { Text = "Show full servers", Location = new Point(15, y), AutoSize = true };
        y += spacing;

        var passwordedLabel = new Label { Text = "Passworded servers:", Location = new Point(15, y + 3), AutoSize = true };
        passwordedComboBox = new ComboBox
        {
            Location = new Point(150, y),
            Size = new Size(150, 25),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        passwordedComboBox.Items.AddRange(["Show all", "Show only", "Hide"]);
        y += spacing;

        showUnresponsiveCheckBox = new CheckBox { Text = "Show unresponsive servers", Location = new Point(15, y), AutoSize = true };
        y += spacing + 10;

        var separator = new Label { Text = "Sorting", Location = new Point(15, y), AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        y += 25;

        populatedFirstCheckBox = new CheckBox { Text = "Show populated servers first", Location = new Point(15, y), AutoSize = true };

        tab.Controls.AddRange([
            showEmptyCheckBox, hideBotOnlyCheckBox, showFullCheckBox, passwordedLabel, passwordedComboBox,
            showUnresponsiveCheckBox, separator, populatedFirstCheckBox
        ]);

        return tab;
    }

    private TabPage CreateTextFiltersTab()
    {
        var tab = new TabPage("Text Filters");
        tab.Padding = new Padding(15);

        var y = 15;
        const int spacing = 55;

        // Server name
        var serverNameLabel = new Label { Text = "Server name contains:", Location = new Point(15, y), AutoSize = true };
        y += 22;
        serverNameTextBox = new TextBox { Location = new Point(15, y), Size = new Size(350, 25) };
        serverNameRegexCheckBox = new CheckBox { Text = "Regex", Location = new Point(375, y), AutoSize = true };
        y += spacing;

        // Map
        var mapLabel = new Label { Text = "Map name contains:", Location = new Point(15, y), AutoSize = true };
        y += 22;
        mapTextBox = new TextBox { Location = new Point(15, y), Size = new Size(350, 25) };
        mapRegexCheckBox = new CheckBox { Text = "Regex", Location = new Point(375, y), AutoSize = true };
        y += spacing;

        // IWAD
        var iwadLabel = new Label { Text = "IWAD contains:", Location = new Point(15, y), AutoSize = true };
        y += 22;
        iwadTextBox = new TextBox { Location = new Point(15, y), Size = new Size(200, 25) };
        y += spacing;

        // Version
        var versionLabel = new Label { Text = "Version contains:", Location = new Point(15, y), AutoSize = true };
        y += 22;
        versionTextBox = new TextBox { Location = new Point(15, y), Size = new Size(200, 25) };

        var hintLabel = new Label
        {
            Text = "Tip: Use * for any characters, ? for single character.\nEnable Regex for advanced pattern matching.",
            Location = new Point(15, 320),
            Size = new Size(450, 40),
            ForeColor = Color.Gray
        };

        tab.Controls.AddRange([
            serverNameLabel, serverNameTextBox, serverNameRegexCheckBox,
            mapLabel, mapTextBox, mapRegexCheckBox,
            iwadLabel, iwadTextBox,
            versionLabel, versionTextBox,
            hintLabel
        ]);

        return tab;
    }

    private TabPage CreateGameModesTab()
    {
        var tab = new TabPage("Game Modes");
        tab.Padding = new Padding(15);

        var includeLabel = new Label { Text = "Include only these modes (empty = all):", Location = new Point(15, 15), AutoSize = true };
        includeModesListBox = new CheckedListBox
        {
            Location = new Point(15, 40),
            Size = new Size(200, 280),
            CheckOnClick = true
        };

        var excludeLabel = new Label { Text = "Exclude these modes:", Location = new Point(240, 15), AutoSize = true };
        excludeModesListBox = new CheckedListBox
        {
            Location = new Point(240, 40),
            Size = new Size(200, 280),
            CheckOnClick = true
        };

        // Populate game modes
        foreach (GameModeType mode in Enum.GetValues<GameModeType>())
        {
            if (mode != GameModeType.Unknown)
            {
                var gm = GameMode.FromType(mode);
                includeModesListBox.Items.Add(new GameModeItem(mode, gm.Name));
                excludeModesListBox.Items.Add(new GameModeItem(mode, gm.Name));
            }
        }

        tab.Controls.AddRange([includeLabel, includeModesListBox, excludeLabel, excludeModesListBox]);

        return tab;
    }

    private TabPage CreateWadsTab()
    {
        var tab = new TabPage("WADs");
        tab.Padding = new Padding(15);

        var y = 15;

        var requireLabel = new Label { Text = "Require ALL of these WADs (one per line):", Location = new Point(15, y), AutoSize = true };
        y += 22;
        requireWadsTextBox = new TextBox
        {
            Location = new Point(15, y),
            Size = new Size(220, 80),
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical
        };
        y += 95;

        var includeAnyLabel = new Label { Text = "Require ANY of these WADs:", Location = new Point(15, y), AutoSize = true };
        y += 22;
        includeAnyWadsTextBox = new TextBox
        {
            Location = new Point(15, y),
            Size = new Size(220, 80),
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical
        };
        y += 95;

        var excludeLabel = new Label { Text = "Exclude servers with these WADs:", Location = new Point(15, y), AutoSize = true };
        y += 22;
        excludeWadsTextBox = new TextBox
        {
            Location = new Point(15, y),
            Size = new Size(220, 80),
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical
        };

        var hintLabel = new Label
        {
            Text = "Enter partial WAD names to match.\nE.g., 'brutal' matches 'brutalv21.pk3'",
            Location = new Point(260, 40),
            Size = new Size(200, 60),
            ForeColor = Color.Gray
        };

        tab.Controls.AddRange([
            requireLabel, requireWadsTextBox,
            includeAnyLabel, includeAnyWadsTextBox,
            excludeLabel, excludeWadsTextBox,
            hintLabel
        ]);

        return tab;
    }

    private TabPage CreateNumericTab()
    {
        var tab = new TabPage("Players/Ping");
        tab.Padding = new Padding(15);

        var y = 15;
        const int spacing = 40;

        var minPlayersLabel = new Label { Text = "Minimum players:", Location = new Point(15, y + 3), AutoSize = true };
        minPlayersNumeric = new NumericUpDown
        {
            Location = new Point(180, y),
            Size = new Size(80, 25),
            Minimum = 0,
            Maximum = 64
        };
        y += spacing;

        var maxPlayersLabel = new Label { Text = "Maximum players:", Location = new Point(15, y + 3), AutoSize = true };
        maxPlayersNumeric = new NumericUpDown
        {
            Location = new Point(180, y),
            Size = new Size(80, 25),
            Minimum = 0,
            Maximum = 64
        };
        var maxPlayersHint = new Label { Text = "(0 = no limit)", Location = new Point(270, y + 3), AutoSize = true, ForeColor = Color.Gray };
        y += spacing;

        var minHumansLabel = new Label { Text = "Minimum human players:", Location = new Point(15, y + 3), AutoSize = true };
        minHumansNumeric = new NumericUpDown
        {
            Location = new Point(180, y),
            Size = new Size(80, 25),
            Minimum = 0,
            Maximum = 64
        };
        var minHumansHint = new Label { Text = "(excludes bots)", Location = new Point(270, y + 3), AutoSize = true, ForeColor = Color.Gray };
        y += spacing + 20;

        var minPingLabel = new Label { Text = "Minimum ping (ms):", Location = new Point(15, y + 3), AutoSize = true };
        minPingNumeric = new NumericUpDown
        {
            Location = new Point(180, y),
            Size = new Size(80, 25),
            Minimum = 0,
            Maximum = 9999
        };
        var minPingHint = new Label { Text = "(0 = no limit)", Location = new Point(270, y + 3), AutoSize = true, ForeColor = Color.Gray };
        y += spacing;

        var pingLabel = new Label { Text = "Maximum ping (ms):", Location = new Point(15, y + 3), AutoSize = true };
        maxPingNumeric = new NumericUpDown
        {
            Location = new Point(180, y),
            Size = new Size(80, 25),
            Minimum = 0,
            Maximum = 9999
        };
        var pingHint = new Label { Text = "(0 = no limit)", Location = new Point(270, y + 3), AutoSize = true, ForeColor = Color.Gray };

        tab.Controls.AddRange([
            minPlayersLabel, minPlayersNumeric,
            maxPlayersLabel, maxPlayersNumeric, maxPlayersHint,
            minHumansLabel, minHumansNumeric, minHumansHint,
            minPingLabel, minPingNumeric, minPingHint,
            pingLabel, maxPingNumeric, pingHint
        ]);

        return tab;
    }

    private TabPage CreateCountryTab()
    {
        var tab = new TabPage("Country");
        tab.Padding = new Padding(10);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(5)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var includeLabel = new Label
        {
            Text = "Include Countries:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };

        var excludeLabel = new Label
        {
            Text = "Exclude Countries:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        };

        includeCountrySearchBox = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "Search countries..."
        };
        includeCountrySearchBox.TextChanged += (s, e) => FilterCountryList(includeCountriesListBox, includeCountrySearchBox.Text);

        excludeCountrySearchBox = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "Search countries..."
        };
        excludeCountrySearchBox.TextChanged += (s, e) => FilterCountryList(excludeCountriesListBox, excludeCountrySearchBox.Text);

        includeCountriesListBox = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false
        };
        includeCountriesListBox.ItemCheck += IncludeCountriesListBox_ItemCheck;

        excludeCountriesListBox = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false
        };
        excludeCountriesListBox.ItemCheck += ExcludeCountriesListBox_ItemCheck;

        // Populate with countries
        foreach (var country in CountryData.Countries)
        {
            includeCountriesListBox.Items.Add(country);
            excludeCountriesListBox.Items.Add(country);
        }

        panel.Controls.Add(includeLabel, 0, 0);
        panel.Controls.Add(excludeLabel, 1, 0);
        panel.Controls.Add(includeCountrySearchBox, 0, 1);
        panel.Controls.Add(excludeCountrySearchBox, 1, 1);
        panel.Controls.Add(includeCountriesListBox, 0, 2);
        panel.Controls.Add(excludeCountriesListBox, 1, 2);

        var hintLabel = new Label
        {
            Text = "Include = only show these countries. Exclude = hide these. (Mutually exclusive)",
            Dock = DockStyle.Bottom,
            Height = 25,
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleCenter
        };

        tab.Controls.Add(panel);
        tab.Controls.Add(hintLabel);

        return tab;
    }

    private void FilterCountryList(CheckedListBox listBox, string searchText)
    {
        // Determine which tracking set to use
        var checkedCodes = listBox == includeCountriesListBox 
            ? _checkedIncludeCountries 
            : _checkedExcludeCountries;

        _isLoadingCountries = true;
        listBox.BeginUpdate();
        listBox.Items.Clear();

        foreach (var country in CountryData.Countries)
        {
            if (string.IsNullOrEmpty(searchText) ||
                country.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                country.Code.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            {
                int index = listBox.Items.Add(country);
                if (checkedCodes.Contains(country.Code))
                {
                    listBox.SetItemChecked(index, true);
                }
            }
        }

        listBox.EndUpdate();
        _isLoadingCountries = false;
    }

    private void IncludeCountriesListBox_ItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_isLoadingCountries) return;
        var item = (CountryItem)includeCountriesListBox.Items[e.Index];
        if (e.NewValue == CheckState.Checked)
        {
            _checkedIncludeCountries.Add(item.Code);
            // Mutual exclusion: uncheck from Exclude
            _checkedExcludeCountries.Remove(item.Code);
            UncheckCountryInList(excludeCountriesListBox, item.Code);
        }
        else
        {
            _checkedIncludeCountries.Remove(item.Code);
        }
    }

    private void ExcludeCountriesListBox_ItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_isLoadingCountries) return;
        var item = (CountryItem)excludeCountriesListBox.Items[e.Index];
        if (e.NewValue == CheckState.Checked)
        {
            _checkedExcludeCountries.Add(item.Code);
            // Mutual exclusion: uncheck from Include
            _checkedIncludeCountries.Remove(item.Code);
            UncheckCountryInList(includeCountriesListBox, item.Code);
        }
        else
        {
            _checkedExcludeCountries.Remove(item.Code);
        }
    }

    private void UncheckCountryInList(CheckedListBox listBox, string code)
    {
        for (int i = 0; i < listBox.Items.Count; i++)
        {
            if (listBox.Items[i] is CountryItem item && item.Code == code)
            {
                listBox.SetItemChecked(i, false);
                break;
            }
        }
    }

    private void LoadFilterToControls()
    {
        // Basic
        showEmptyCheckBox.Checked = _filter.ShowEmpty;
        hideBotOnlyCheckBox.Checked = _filter.TreatBotOnlyAsEmpty;
        showFullCheckBox.Checked = _filter.ShowFull;
        passwordedComboBox.SelectedIndex = (int)_filter.PasswordedServers;
        showUnresponsiveCheckBox.Checked = _filter.ShowUnresponsive;
        populatedFirstCheckBox.Checked = _filter.PopulatedServersFirst;

        // Text filters
        serverNameTextBox.Text = _filter.ServerNameFilter;
        serverNameRegexCheckBox.Checked = _filter.ServerNameIsRegex;
        mapTextBox.Text = _filter.MapFilter;
        mapRegexCheckBox.Checked = _filter.MapIsRegex;
        iwadTextBox.Text = _filter.RequireIWAD;
        versionTextBox.Text = _filter.RequireVersion;

        // Game modes
        for (int i = 0; i < includeModesListBox.Items.Count; i++)
        {
            var item = (GameModeItem)includeModesListBox.Items[i];
            includeModesListBox.SetItemChecked(i, _filter.IncludeGameModes.Contains(item.Mode));
        }
        for (int i = 0; i < excludeModesListBox.Items.Count; i++)
        {
            var item = (GameModeItem)excludeModesListBox.Items[i];
            excludeModesListBox.SetItemChecked(i, _filter.ExcludeGameModes.Contains(item.Mode));
        }

        // WADs
        requireWadsTextBox.Text = string.Join(Environment.NewLine, _filter.RequireWads);
        includeAnyWadsTextBox.Text = string.Join(Environment.NewLine, _filter.IncludeAnyWads);
        excludeWadsTextBox.Text = string.Join(Environment.NewLine, _filter.ExcludeWads);

        // Numeric
        minPlayersNumeric.Value = _filter.MinPlayers;
        maxPlayersNumeric.Value = _filter.MaxPlayers;
        minHumansNumeric.Value = _filter.MinHumanPlayers;
        minPingNumeric.Value = _filter.MinPing;
        maxPingNumeric.Value = _filter.MaxPing;

        // Country - initialize tracking sets and check items
        _checkedIncludeCountries.Clear();
        _checkedExcludeCountries.Clear();
        foreach (var code in _filter.IncludeCountries)
        {
            _checkedIncludeCountries.Add(code);
        }
        foreach (var code in _filter.ExcludeCountries)
        {
            _checkedExcludeCountries.Add(code);
        }
        
        _isLoadingCountries = true;
        for (int i = 0; i < includeCountriesListBox.Items.Count; i++)
        {
            var item = (CountryItem)includeCountriesListBox.Items[i];
            includeCountriesListBox.SetItemChecked(i, _checkedIncludeCountries.Contains(item.Code));
        }
        for (int i = 0; i < excludeCountriesListBox.Items.Count; i++)
        {
            var item = (CountryItem)excludeCountriesListBox.Items[i];
            excludeCountriesListBox.SetItemChecked(i, _checkedExcludeCountries.Contains(item.Code));
        }
        _isLoadingCountries = false;
    }

    private void SaveControlsToFilter()
    {
        // Basic
        _filter.ShowEmpty = showEmptyCheckBox.Checked;
        _filter.TreatBotOnlyAsEmpty = hideBotOnlyCheckBox.Checked;
        _filter.ShowFull = showFullCheckBox.Checked;
        _filter.PasswordedServers = (FilterMode)passwordedComboBox.SelectedIndex;
        _filter.ShowUnresponsive = showUnresponsiveCheckBox.Checked;
        _filter.PopulatedServersFirst = populatedFirstCheckBox.Checked;

        // Text filters
        _filter.ServerNameFilter = serverNameTextBox.Text.Trim();
        _filter.ServerNameIsRegex = serverNameRegexCheckBox.Checked;
        _filter.MapFilter = mapTextBox.Text.Trim();
        _filter.MapIsRegex = mapRegexCheckBox.Checked;
        _filter.RequireIWAD = iwadTextBox.Text.Trim();
        _filter.RequireVersion = versionTextBox.Text.Trim();

        // Game modes
        _filter.IncludeGameModes.Clear();
        for (int i = 0; i < includeModesListBox.Items.Count; i++)
        {
            if (includeModesListBox.GetItemChecked(i))
            {
                var item = (GameModeItem)includeModesListBox.Items[i];
                _filter.IncludeGameModes.Add(item.Mode);
            }
        }
        _filter.ExcludeGameModes.Clear();
        for (int i = 0; i < excludeModesListBox.Items.Count; i++)
        {
            if (excludeModesListBox.GetItemChecked(i))
            {
                var item = (GameModeItem)excludeModesListBox.Items[i];
                _filter.ExcludeGameModes.Add(item.Mode);
            }
        }

        // WADs
        _filter.RequireWads = ParseWadList(requireWadsTextBox.Text);
        _filter.IncludeAnyWads = ParseWadList(includeAnyWadsTextBox.Text);
        _filter.ExcludeWads = ParseWadList(excludeWadsTextBox.Text);

        // Numeric
        _filter.MinPlayers = (int)minPlayersNumeric.Value;
        _filter.MaxPlayers = (int)maxPlayersNumeric.Value;
        _filter.MinHumanPlayers = (int)minHumansNumeric.Value;
        _filter.MinPing = (int)minPingNumeric.Value;
        _filter.MaxPing = (int)maxPingNumeric.Value;

        // Country - use tracking sets (includes items that may be filtered out of view)
        _filter.IncludeCountries = [.. _checkedIncludeCountries];
        _filter.ExcludeCountries = [.. _checkedExcludeCountries];
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
        presetComboBox.Items.Clear();
        presetComboBox.Items.Add("(Current filter)");
        foreach (var preset in _presets)
        {
            presetComboBox.Items.Add(preset.Name);
        }
        presetComboBox.SelectedIndex = 0;
    }

    private void ApplyDarkTheme()
    {
        BackColor = DarkTheme.PrimaryBackground;
        ForeColor = DarkTheme.TextPrimary;

        DarkTheme.Apply(this);
        DarkTheme.ApplyToButton(okButton);
        DarkTheme.ApplyToButton(cancelButton);
        DarkTheme.ApplyToButton(clearButton);
        DarkTheme.ApplyToButton(savePresetButton);
        DarkTheme.ApplyToButton(deletePresetButton);
    }

    private void PresetComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (presetComboBox.SelectedIndex > 0)
        {
            var preset = _presets[presetComboBox.SelectedIndex - 1];
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
            LoadFilterToControls();
        }

        deletePresetButton.Enabled = presetComboBox.SelectedIndex > 0;
    }

    private void SavePresetButton_Click(object? sender, EventArgs e)
    {
        SaveControlsToFilter();

        using var inputDialog = new Form
        {
            Text = "Save Preset",
            Size = new Size(300, 130),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = DarkTheme.PrimaryBackground,
            ForeColor = DarkTheme.TextPrimary
        };

        var label = new Label { Text = "Preset name:", Location = new Point(15, 15), AutoSize = true };
        var textBox = new TextBox { Text = _filter.Name, Location = new Point(15, 35), Size = new Size(250, 25) };
        var okBtn = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(110, 65), Size = new Size(75, 25) };
        var cancelBtn = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(190, 65), Size = new Size(75, 25) };

        DarkTheme.ApplyToButton(okBtn);
        DarkTheme.ApplyToButton(cancelBtn);
        textBox.BackColor = DarkTheme.SecondaryBackground;
        textBox.ForeColor = DarkTheme.TextPrimary;

        inputDialog.Controls.AddRange([label, textBox, okBtn, cancelBtn]);
        inputDialog.AcceptButton = okBtn;
        inputDialog.CancelButton = cancelBtn;

        if (inputDialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            var name = textBox.Text.Trim();
            _filter.Name = name;

            // Check if preset already exists
            var existingIndex = _presets.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                _presets[existingIndex] = _filter.Clone();
            }
            else
            {
                _presets.Add(_filter.Clone());
            }

            PopulatePresets();
            presetComboBox.SelectedIndex = _presets.FindIndex(p => p.Name == name) + 1;
        }
    }

    private void DeletePresetButton_Click(object? sender, EventArgs e)
    {
        if (presetComboBox.SelectedIndex > 0)
        {
            var presetIndex = presetComboBox.SelectedIndex - 1;
            var presetName = _presets[presetIndex].Name;

            if (MessageBox.Show($"Delete preset '{presetName}'?", "Confirm Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _presets.RemoveAt(presetIndex);
                PopulatePresets();
            }
        }
    }

    private void ClearButton_Click(object? sender, EventArgs e)
    {
        // Reset to defaults
        showEmptyCheckBox.Checked = true;
        hideBotOnlyCheckBox.Checked = false;
        showFullCheckBox.Checked = true;
        passwordedComboBox.SelectedIndex = 0;
        showUnresponsiveCheckBox.Checked = false;
        populatedFirstCheckBox.Checked = true;

        serverNameTextBox.Clear();
        serverNameRegexCheckBox.Checked = false;
        mapTextBox.Clear();
        mapRegexCheckBox.Checked = false;
        iwadTextBox.Clear();
        versionTextBox.Clear();

        for (int i = 0; i < includeModesListBox.Items.Count; i++)
            includeModesListBox.SetItemChecked(i, false);
        for (int i = 0; i < excludeModesListBox.Items.Count; i++)
            excludeModesListBox.SetItemChecked(i, false);

        requireWadsTextBox.Clear();
        includeAnyWadsTextBox.Clear();
        excludeWadsTextBox.Clear();

        minPlayersNumeric.Value = 0;
        maxPlayersNumeric.Value = 0;
        minHumansNumeric.Value = 0;
        minPingNumeric.Value = 0;
        maxPingNumeric.Value = 0;

        _checkedIncludeCountries.Clear();
        _checkedExcludeCountries.Clear();
        for (int i = 0; i < includeCountriesListBox.Items.Count; i++)
            includeCountriesListBox.SetItemChecked(i, false);
        for (int i = 0; i < excludeCountriesListBox.Items.Count; i++)
            excludeCountriesListBox.SetItemChecked(i, false);
        includeCountrySearchBox.Clear();
        excludeCountrySearchBox.Clear();

        presetComboBox.SelectedIndex = 0;
    }

    private void OkButton_Click(object? sender, EventArgs e)
    {
        SaveControlsToFilter();
    }

    private class GameModeItem(GameModeType mode, string name)
    {
        public GameModeType Mode { get; } = mode;
        public string Name { get; } = name;
        public override string ToString() => Name;
    }
}

/// <summary>
/// Represents a country with its ISO code and name.
/// </summary>
public class CountryItem(string code, string name)
{
    public string Code { get; } = code;
    public string Name { get; } = name;
    public override string ToString() => $"{Name} ({Code})";
}

/// <summary>
/// Static data containing ISO 3166-1 alpha-2 country codes.
/// </summary>
public static class CountryData
{
    public static readonly CountryItem[] Countries =
    [
        // Special codes - shown at top for easy filtering
        new("??", "[Unknown/Unresolved]"),
        new("A1", "[Anonymous Proxy]"),
        new("A2", "[Satellite Provider]"),
        new("AP", "[Asia/Pacific Region]"),
        new("EU", "[Europe Region]"),
        // Standard ISO 3166-1 alpha-2 codes
        new("AF", "Afghanistan"),
        new("AL", "Albania"),
        new("DZ", "Algeria"),
        new("AD", "Andorra"),
        new("AO", "Angola"),
        new("AR", "Argentina"),
        new("AM", "Armenia"),
        new("AU", "Australia"),
        new("AT", "Austria"),
        new("AZ", "Azerbaijan"),
        new("BS", "Bahamas"),
        new("BH", "Bahrain"),
        new("BD", "Bangladesh"),
        new("BY", "Belarus"),
        new("BE", "Belgium"),
        new("BZ", "Belize"),
        new("BO", "Bolivia"),
        new("BA", "Bosnia and Herzegovina"),
        new("BR", "Brazil"),
        new("BN", "Brunei"),
        new("BG", "Bulgaria"),
        new("KH", "Cambodia"),
        new("CM", "Cameroon"),
        new("CA", "Canada"),
        new("CL", "Chile"),
        new("CN", "China"),
        new("CO", "Colombia"),
        new("CR", "Costa Rica"),
        new("HR", "Croatia"),
        new("CU", "Cuba"),
        new("CY", "Cyprus"),
        new("CZ", "Czechia"),
        new("DK", "Denmark"),
        new("DO", "Dominican Republic"),
        new("EC", "Ecuador"),
        new("EG", "Egypt"),
        new("SV", "El Salvador"),
        new("EE", "Estonia"),
        new("ET", "Ethiopia"),
        new("FI", "Finland"),
        new("FR", "France"),
        new("GE", "Georgia"),
        new("DE", "Germany"),
        new("GH", "Ghana"),
        new("GR", "Greece"),
        new("GT", "Guatemala"),
        new("HN", "Honduras"),
        new("HK", "Hong Kong"),
        new("HU", "Hungary"),
        new("IS", "Iceland"),
        new("IN", "India"),
        new("ID", "Indonesia"),
        new("IR", "Iran"),
        new("IQ", "Iraq"),
        new("IE", "Ireland"),
        new("IL", "Israel"),
        new("IT", "Italy"),
        new("JM", "Jamaica"),
        new("JP", "Japan"),
        new("JO", "Jordan"),
        new("KZ", "Kazakhstan"),
        new("KE", "Kenya"),
        new("KR", "Korea, South"),
        new("KW", "Kuwait"),
        new("KG", "Kyrgyzstan"),
        new("LA", "Laos"),
        new("LV", "Latvia"),
        new("LB", "Lebanon"),
        new("LY", "Libya"),
        new("LI", "Liechtenstein"),
        new("LT", "Lithuania"),
        new("LU", "Luxembourg"),
        new("MO", "Macau"),
        new("MY", "Malaysia"),
        new("MT", "Malta"),
        new("MX", "Mexico"),
        new("MD", "Moldova"),
        new("MC", "Monaco"),
        new("MN", "Mongolia"),
        new("ME", "Montenegro"),
        new("MA", "Morocco"),
        new("MM", "Myanmar"),
        new("NP", "Nepal"),
        new("NL", "Netherlands"),
        new("NZ", "New Zealand"),
        new("NI", "Nicaragua"),
        new("NG", "Nigeria"),
        new("MK", "North Macedonia"),
        new("NO", "Norway"),
        new("OM", "Oman"),
        new("PK", "Pakistan"),
        new("PA", "Panama"),
        new("PY", "Paraguay"),
        new("PE", "Peru"),
        new("PH", "Philippines"),
        new("PL", "Poland"),
        new("PT", "Portugal"),
        new("PR", "Puerto Rico"),
        new("QA", "Qatar"),
        new("RO", "Romania"),
        new("RU", "Russia"),
        new("SA", "Saudi Arabia"),
        new("RS", "Serbia"),
        new("SG", "Singapore"),
        new("SK", "Slovakia"),
        new("SI", "Slovenia"),
        new("ZA", "South Africa"),
        new("ES", "Spain"),
        new("LK", "Sri Lanka"),
        new("SE", "Sweden"),
        new("CH", "Switzerland"),
        new("SY", "Syria"),
        new("TW", "Taiwan"),
        new("TH", "Thailand"),
        new("TN", "Tunisia"),
        new("TR", "Turkey"),
        new("UA", "Ukraine"),
        new("AE", "United Arab Emirates"),
        new("GB", "United Kingdom"),
        new("US", "United States"),
        new("UY", "Uruguay"),
        new("UZ", "Uzbekistan"),
        new("VE", "Venezuela"),
        new("VN", "Vietnam"),
        new("YE", "Yemen"),
        new("ZW", "Zimbabwe")
    ];

    /// <summary>
    /// Maps ISO 3166-1 alpha-3 codes to alpha-2 codes.
    /// Also includes special codes that should pass through unchanged.
    /// </summary>
    public static readonly Dictionary<string, string> Alpha3ToAlpha2 = new(StringComparer.OrdinalIgnoreCase)
    {
        // Special codes - normalize unknown variants to "??"
        ["XIP"] = "??", ["XUN"] = "??", ["O1"] = "??", ["A1"] = "A1", ["A2"] = "A2", ["AP"] = "AP", ["EU"] = "EU", ["??"] = "??",
        // Standard alpha-3 to alpha-2 mappings
        ["AFG"] = "AF", ["ALB"] = "AL", ["DZA"] = "DZ", ["AND"] = "AD", ["AGO"] = "AO",
        ["ARG"] = "AR", ["ARM"] = "AM", ["AUS"] = "AU", ["AUT"] = "AT", ["AZE"] = "AZ",
        ["BHS"] = "BS", ["BHR"] = "BH", ["BGD"] = "BD", ["BLR"] = "BY", ["BEL"] = "BE",
        ["BLZ"] = "BZ", ["BOL"] = "BO", ["BIH"] = "BA", ["BRA"] = "BR", ["BRN"] = "BN",
        ["BGR"] = "BG", ["KHM"] = "KH", ["CMR"] = "CM", ["CAN"] = "CA", ["CHL"] = "CL",
        ["CHN"] = "CN", ["COL"] = "CO", ["CRI"] = "CR", ["HRV"] = "HR", ["CUB"] = "CU",
        ["CYP"] = "CY", ["CZE"] = "CZ", ["DNK"] = "DK", ["DOM"] = "DO", ["ECU"] = "EC",
        ["EGY"] = "EG", ["SLV"] = "SV", ["EST"] = "EE", ["ETH"] = "ET", ["FIN"] = "FI",
        ["FRA"] = "FR", ["GEO"] = "GE", ["DEU"] = "DE", ["GHA"] = "GH", ["GRC"] = "GR",
        ["GTM"] = "GT", ["HND"] = "HN", ["HKG"] = "HK", ["HUN"] = "HU", ["ISL"] = "IS",
        ["IND"] = "IN", ["IDN"] = "ID", ["IRN"] = "IR", ["IRQ"] = "IQ", ["IRL"] = "IE",
        ["ISR"] = "IL", ["ITA"] = "IT", ["JAM"] = "JM", ["JPN"] = "JP", ["JOR"] = "JO",
        ["KAZ"] = "KZ", ["KEN"] = "KE", ["KOR"] = "KR", ["KWT"] = "KW", ["KGZ"] = "KG",
        ["LAO"] = "LA", ["LVA"] = "LV", ["LBN"] = "LB", ["LBY"] = "LY", ["LIE"] = "LI",
        ["LTU"] = "LT", ["LUX"] = "LU", ["MAC"] = "MO", ["MYS"] = "MY", ["MLT"] = "MT",
        ["MEX"] = "MX", ["MDA"] = "MD", ["MCO"] = "MC", ["MNG"] = "MN", ["MNE"] = "ME",
        ["MAR"] = "MA", ["MMR"] = "MM", ["NPL"] = "NP", ["NLD"] = "NL", ["NZL"] = "NZ",
        ["NIC"] = "NI", ["NGA"] = "NG", ["MKD"] = "MK", ["NOR"] = "NO", ["OMN"] = "OM",
        ["PAK"] = "PK", ["PAN"] = "PA", ["PRY"] = "PY", ["PER"] = "PE", ["PHL"] = "PH",
        ["POL"] = "PL", ["PRT"] = "PT", ["PRI"] = "PR", ["QAT"] = "QA", ["ROU"] = "RO",
        ["RUS"] = "RU", ["SAU"] = "SA", ["SRB"] = "RS", ["SGP"] = "SG", ["SVK"] = "SK",
        ["SVN"] = "SI", ["ZAF"] = "ZA", ["ESP"] = "ES", ["LKA"] = "LK", ["SWE"] = "SE",
        ["CHE"] = "CH", ["SYR"] = "SY", ["TWN"] = "TW", ["THA"] = "TH", ["TUN"] = "TN",
        ["TUR"] = "TR", ["UKR"] = "UA", ["ARE"] = "AE", ["GBR"] = "GB", ["USA"] = "US",
        ["URY"] = "UY", ["UZB"] = "UZ", ["VEN"] = "VE", ["VNM"] = "VN", ["YEM"] = "YE",
        ["ZWE"] = "ZW"
    };

    /// <summary>
    /// Normalizes a country code to alpha-2 format.
    /// Unknown variants (XIP, XUN, O1, empty) are normalized to "??".
    /// </summary>
    public static string NormalizeToAlpha2(string code)
    {
        if (string.IsNullOrEmpty(code)) return "??";
        
        var upper = code.ToUpperInvariant().Trim();
        if (string.IsNullOrEmpty(upper)) return "??";
        
        // Check for special/unknown codes first
        if (upper == "XIP" || upper == "XUN" || upper == "O1")
            return "??";
        
        // Already alpha-2
        if (upper.Length == 2) return upper;
        
        // Try to convert from alpha-3
        if (upper.Length == 3 && Alpha3ToAlpha2.TryGetValue(upper, out var alpha2))
            return alpha2;
        
        return upper;
    }
}
