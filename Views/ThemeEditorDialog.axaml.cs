using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ZScape.Services;

namespace ZScape.Views;

/// <summary>
/// Presents a ThemeService palette draft with live preview and hex color editors.
/// Loading, saving, and restoring the accepted appearance belong to ThemeService.
/// </summary>
public partial class ThemeEditorDialog : Window
{
    private readonly ThemeService _themes;
    private readonly Dictionary<string, ColorEditorRow> _colorRows = new();
    private readonly Dictionary<string, Color> _appliedColors = new();
    private string _currentThemeName = "Untitled";
    private ThemeService.ThemePreview? _preview;

    public ThemeEditorDialog() : this(ThemeService.Instance) { }

    internal ThemeEditorDialog(ThemeService themes)
    {
        _themes = themes;
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        ThemeNameBox.Text = _currentThemeName;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_preview != null) return;
        _preview = _themes.BeginPreview();
        var name = _themes.ActiveThemeId;
        if (ThemeService.GetBuiltInThemeNames().Contains(name))
            name += " Custom";
        PopulateColors(_preview.Colors, name);
    }

    private void UpdateSaveButtonState()
    {
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(ThemeNameBox.Text);
    }

    private void ThemeNameBox_TextChanged(object? sender, TextChangedEventArgs e) => UpdateSaveButtonState();

    /// <summary>
    /// Presents the palette owned by the current theme preview.
    /// </summary>
    private void PopulateColors(IReadOnlyDictionary<string, Color> colors, string themeName)
    {
        ColorEditorPanel.Children.Clear();
        _colorRows.Clear();
        _appliedColors.Clear();

        foreach (var group in colors.GroupBy(pair => InferCategory(pair.Key)).OrderBy(group => group.Key))
        {
            var header = new TextBlock
            {
                Text = group.Key,
                FontWeight = FontWeight.Bold,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 4),
                Foreground = new SolidColorBrush(Color.Parse("#888888"))
            };
            ColorEditorPanel.Children.Add(header);

            foreach (var (key, currentColor) in group.OrderBy(pair => pair.Key))
            {
                _appliedColors[key] = currentColor;
                var row = new ColorEditorRow(key, currentColor, OnColorChanged);
                _colorRows[key] = row;
                ColorEditorPanel.Children.Add(row.Build());
            }
        }

        _currentThemeName = themeName;
        ThemeNameBox.Text = themeName;
        UpdateSaveButtonState();
    }

    private void OnColorChanged(string key, Color newColor)
    {
        _appliedColors[key] = newColor;
        _preview?.Update(_appliedColors);
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var name = ThemeNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name) || _preview == null) return;

        try
        {
            _preview.Save(name);
            _currentThemeName = name;
            ShowStatus($"Saved and applied '{name}'.", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowStatus(ex.Message, true);
        }
    }

    private async void LoadButton_Click(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Theme File",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Avalonia Theme") { Patterns = new[] { "*.axaml" } } }
        });

        if (files.Count == 0 || _preview == null) return;
        try
        {
            var path = files[0].Path.LocalPath;
            _preview.Load(path);
            PopulateColors(_preview.Colors, Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or FormatException)
        {
            ShowStatus(ex.Message, true);
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _preview?.Dispose();
        _preview = null;
    }

    private static string InferCategory(string key)
    {
        if (key.Contains("Background") || key.Contains("Primary") || key.Contains("Secondary") || key.Contains("Tertiary"))
            return "Background Colors";
        if (key.Contains("Text"))
            return "Text Colors";
        if (key.Contains("Accent"))
            return "Accent Colors";
        if (key.Contains("Success") || key.Contains("Warning") || key.Contains("Error"))
            return "Status Colors";
        if (key.Contains("Border"))
            return "Border Colors";
        if (key.Contains("Row"))
            return "Row Colors";
        if (key.Contains("Log"))
            return "Log Colors";
        if (key.Contains("Password") || key.Contains("Online") || key.Contains("Offline"))
            return "Icons";
        return "Other";
    }

    private async void ShowStatus(string msg, bool isError)
    {
        var label = new TextBlock
        {
            Text = msg,
            Foreground = isError
                ? new SolidColorBrush(Color.Parse("#FF6347"))
                : new SolidColorBrush(Color.Parse("#32CD32")),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0)
        };
        ColorEditorPanel.Children.Insert(0, label);
        await Task.Delay(4000);
        ColorEditorPanel.Children.Remove(label);
    }

    /// <summary>
    /// A single editable color row: label + hex textbox + color preview swatch.
    /// </summary>
    private sealed class ColorEditorRow
    {
        private readonly string _key;
        private readonly Action<string, Color> _onChanged;
        private Color _current;
        private Border? _swatch;
        private TextBox? _hexInput;

        public ColorEditorRow(string key, Color color, Action<string, Color> onChanged)
        {
            _key = key;
            _current = color;
            _onChanged = onChanged;
        }

        public Control Build()
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(0, 1) };

            row.Children.Add(new TextBlock
            {
                Text = _key, Width = 160, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            _swatch = new Border
            {
                Width = 24, Height = 22, CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(_current),
                BorderBrush = new SolidColorBrush(Color.Parse("#666666")),
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0)
            };
            row.Children.Add(_swatch);

            _hexInput = new TextBox
            {
                Text = $"#{_current.R:X2}{_current.G:X2}{_current.B:X2}",
                Width = 72, FontSize = 11, FontFamily = new FontFamily("Consolas, monospace"),
                Padding = new Thickness(4, 2), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            _hexInput.TextChanged += (_, _) =>
            {
                try
                {
                    var t = (_hexInput.Text ?? "").Trim();
                    if (t.StartsWith("#")) t = t.Substring(1);
                    if (t.Length == 6) t = "FF" + t;
                    if (t.Length < 8) return;
                    _current = Color.Parse("#" + t);
                    if (_swatch != null) _swatch.Background = new SolidColorBrush(_current);
                    _onChanged(_key, _current);
                }
                catch { }
            };
            row.Children.Add(_hexInput);

            return row;
        }
    }
}
