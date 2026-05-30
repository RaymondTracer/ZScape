using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Xml.Linq;
using ZScape.Services;

namespace ZScape.Views;

/// <summary>
/// Theme editor dialog for creating and editing custom theme .axaml files.
/// Parses Color definitions from built-in themes, provides a visual editor
/// with live preview, and saves to Themes/User/ directory.
/// If Avalonia.Controls.ColorPicker is available, uses it; otherwise falls
/// back to hex textbox input.
/// </summary>
public partial class ThemeEditorDialog : Window
{
    private const string DefaultBaseTheme = "Dark";
    private readonly Dictionary<string, ColorEditorRow> _colorRows = new();
    private readonly Dictionary<string, Color> _appliedColors = new();
    private string _currentThemeName = "Untitled";

    public ThemeEditorDialog()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        ThemeNameBox.Text = _currentThemeName;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        LoadThemeColors(DefaultBaseTheme);
    }

    private void UpdateSaveButtonState()
    {
        SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(ThemeNameBox.Text);
    }

    private void ThemeNameBox_TextChanged(object? sender, TextChangedEventArgs e) => UpdateSaveButtonState();

    /// <summary>
    /// Parses a built-in or user theme file and populates the color editor.
    /// </summary>
    private void LoadThemeColors(string themeName)
    {
        ColorEditorPanel.Children.Clear();
        _colorRows.Clear();
        _appliedColors.Clear();

        var doc = TryLoadThemeDoc(themeName);
        if (doc?.Root == null)
        {
            ShowStatus("Could not load theme.", true);
            return;
        }

        var colorElements = doc.Descendants()
            .Where(e => e.Name.LocalName == "Color")
            .ToList();

        if (colorElements.Count == 0)
        {
            ShowStatus("No Color definitions found.", true);
            return;
        }

        var categories = new Dictionary<string, List<XElement>>();
        foreach (var el in colorElements)
        {
            var key = GetXamlKey(el);
            if (string.IsNullOrEmpty(key)) continue;
            var cat = InferCategory(key);
            if (!categories.ContainsKey(cat)) categories[cat] = new();
            categories[cat].Add(el);
        }

        foreach (var kvp in categories.OrderBy(c => c.Key))
        {
            var header = new TextBlock
            {
                Text = kvp.Key,
                FontWeight = FontWeight.Bold,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 4),
                Foreground = new SolidColorBrush(Color.Parse("#888888"))
            };
            ColorEditorPanel.Children.Add(header);

            foreach (var el in kvp.Value.OrderBy(e => GetXamlKey(e)))
            {
                var key = GetXamlKey(el);
                var hex = el.Attribute("Value")?.Value ?? el.Value?.Trim() ?? "#FFFFFFFF";
                var currentApp = Application.Current;
                var currentColor = currentApp?.TryGetResource(key, currentApp.ActualThemeVariant, out var r) == true
                    && r is Color c ? c : ParseColor(hex);

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
        ApplyLivePreview();
    }

    private void ApplyLivePreview()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current == null) return;
            foreach (var (key, color) in _appliedColors)
            {
                Application.Current.Resources[key] = color;
                Application.Current.Resources[key + "Brush"] = new SolidColorBrush(color);
            }
        });
    }

    private void RollbackPreview()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Reload the currently active theme to restore original colors
            var activeTheme = ThemeService.Instance.CurrentTheme;
            var themeId = activeTheme == ThemeVariant.Dark ? "Dark" : "Light";
            ThemeService.Instance.ApplyTheme(themeId);
        });
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var name = ThemeNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        var dir = ThemeService.UserThemesDirectory;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{name}.axaml");
        File.WriteAllText(path, BuildThemeXml(name));
        _currentThemeName = name;
        ThemeService.Instance.ApplyTheme(name);
        ShowStatus($"Saved and applied '{name}'.", false);
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

        if (files.Count == 0) return;
        var themeName = Path.GetFileNameWithoutExtension(files[0].Path.LocalPath);
        LoadThemeColors(themeName);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        RollbackPreview();
    }

    private string BuildThemeXml(string themeName)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\"");
        sb.AppendLine("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">");
        sb.AppendLine();
        sb.AppendLine($"    <!-- {themeName} Theme -->");

        foreach (var (key, color) in _appliedColors.OrderBy(k => k.Key))
        {
            var hex = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            sb.AppendLine($"    <Color x:Key=\"{key}\">{hex}</Color>");
        }

        sb.AppendLine();
        sb.AppendLine("    <!-- Brushes -->");
        foreach (var key in _appliedColors.Keys.OrderBy(k => k))
            sb.AppendLine($"    <SolidColorBrush x:Key=\"{key}Brush\" Color=\"{{StaticResource {key}}}\" />");

        sb.AppendLine("</ResourceDictionary>");
        return sb.ToString();
    }

    private static XDocument? TryLoadThemeDoc(string themeName)
    {
        try
        {
            var userPath = Path.Combine(ThemeService.UserThemesDirectory, $"{themeName}.axaml");
            if (File.Exists(userPath)) return XDocument.Load(userPath);

            var builtInName = themeName.EndsWith("Theme", StringComparison.OrdinalIgnoreCase)
                ? themeName : $"{themeName}Theme";
            var resourcePath = Path.Combine(AppContext.BaseDirectory, "Themes", $"{builtInName}.axaml");
            if (File.Exists(resourcePath)) return XDocument.Load(resourcePath);

            return null;
        }
        catch { return null; }
    }

    private static string GetXamlKey(XElement el) =>
        el.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value ?? "";

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

    private static Color ParseColor(string hex)
    {
        try
        {
            hex = hex.Trim();
            if (hex.StartsWith("#")) hex = hex.Substring(1);
            if (hex.Length == 6) hex = "FF" + hex;
            return Color.Parse("#" + hex);
        }
        catch { return Colors.White; }
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
