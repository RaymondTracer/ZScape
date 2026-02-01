namespace ZScape.UI;

/// <summary>
/// Shared UI helper methods for consistent control creation across dialogs.
/// </summary>
public static class UIHelpers
{
    /// <summary>
    /// Creates a hint label with muted text color.
    /// </summary>
    public static Label CreateHintLabel(string text)
    {
        return new Label
        {
            Text = text,
            ForeColor = Color.FromArgb(150, 150, 150),
            AutoSize = true,
            Margin = new Padding(0)
        };
    }
    
    /// <summary>
    /// Creates a section header label with bold font.
    /// </summary>
    public static Label CreateSectionHeader(string text, float fontSize = 10f)
    {
        return new Label
        {
            Text = text,
            Font = new Font("Segoe UI", fontSize, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 3)
        };
    }
    
    /// <summary>
    /// Creates update interval controls with preset dropdown.
    /// </summary>
    /// <param name="initialValue">Initial interval value.</param>
    /// <param name="initialUnit">Initial interval unit (0=Hours, 1=Days, 2=Weeks).</param>
    /// <returns>A tuple containing the container panel and the individual controls.</returns>
    public static (FlowLayoutPanel container, ComboBox presets, NumericUpDown value, ComboBox unit) 
        CreateUpdateIntervalControls(int initialValue, int initialUnit)
    {
        var container = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0)
        };
        
        var applyingPreset = false;
        
        var presets = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 120,
            Margin = new Padding(0, 2, 0, 0)
        };
        presets.Items.AddRange(["Custom:", "Every 6 hours", "Once a day", "Once a week", "Once a month"]);
        
        // Determine initial preset based on current settings
        var presetIndex = 0; // Default to Custom
        if (initialValue == 6 && initialUnit == 0)
            presetIndex = 1; // Every 6 hours
        else if (initialValue == 1 && initialUnit == 1)
            presetIndex = 2; // Once a day
        else if (initialValue == 1 && initialUnit == 2)
            presetIndex = 3; // Once a week
        else if (initialValue == 4 && initialUnit == 2)
            presetIndex = 4; // Once a month
        presets.SelectedIndex = presetIndex;
        
        var valueControl = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 99,
            Value = initialValue,
            Width = 55,
            Margin = new Padding(10, 2, 0, 0)
        };
        
        var unit = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 70,
            Margin = new Padding(5, 2, 0, 0)
        };
        unit.Items.AddRange(["Hours", "Days", "Weeks"]);
        unit.SelectedIndex = initialUnit;
        
        // Preset selection handler
        presets.SelectedIndexChanged += (_, _) =>
        {
            if (presets.SelectedIndex == 0) return; // Custom selected
            applyingPreset = true;
            switch (presets.SelectedIndex)
            {
                case 1: // Every 6 hours
                    valueControl.Value = 6;
                    unit.SelectedIndex = 0;
                    break;
                case 2: // Once a day
                    valueControl.Value = 1;
                    unit.SelectedIndex = 1;
                    break;
                case 3: // Once a week
                    valueControl.Value = 1;
                    unit.SelectedIndex = 2;
                    break;
                case 4: // Once a month (4 weeks)
                    valueControl.Value = 4;
                    unit.SelectedIndex = 2;
                    break;
            }
            applyingPreset = false;
        };
        
        // When user manually changes interval, switch to "Custom:"
        valueControl.ValueChanged += (_, _) => { if (!applyingPreset) presets.SelectedIndex = 0; };
        unit.SelectedIndexChanged += (_, _) => { if (!applyingPreset) presets.SelectedIndex = 0; };
        
        // Add controls in order
        container.Controls.Add(presets);
        container.Controls.Add(valueControl);
        container.Controls.Add(unit);
        
        return (container, presets, valueControl, unit);
    }
    
    /// <summary>
    /// Standard hint text for the testing versions folder.
    /// </summary>
    public const string TestingFolderHint = "Leave blank to use \"TestingVersions\" folder in Zandronum directory";
}
