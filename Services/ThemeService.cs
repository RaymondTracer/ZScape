using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Xml.Linq;

namespace ZScape.Services;

/// <summary>Owns the active theme, its palette, and reversible editor previews.</summary>
public class ThemeService
{
    private static readonly Lazy<ThemeService> _instance = new(() => new ThemeService());
    public static ThemeService Instance => _instance.Value;

    private const string DarkUri = "avares://ZScape/Themes/DarkTheme.axaml";
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Dictionary<string, string> BuiltInThemes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dark"] = "ZScape.ThemePalettes.Dark",
        ["Light"] = "ZScape.ThemePalettes.Light",
    };

    private readonly SettingsService _settings;
    private readonly string _userThemesDirectory;
    private IResourceDictionary? _paletteResources;
    private ThemePalette? _palette;
    private ThemePreview? _preview;
    private bool _isInitialized;

    public event EventHandler? ThemeChanged;
    public ThemeVariant CurrentTheme { get; private set; } = ThemeVariant.Dark;
    public string CurrentAccent { get; private set; } = "Blue";
    public string ActiveThemeId { get; private set; } = "Dark";
    public bool IsDark => CurrentTheme == ThemeVariant.Dark;
    public bool IsLight => CurrentTheme == ThemeVariant.Light;

    public static IReadOnlyList<string> GetBuiltInThemeNames() => BuiltInThemes.Keys.ToArray();
    public static string UserThemesDirectory => Path.Combine(AppContext.BaseDirectory, "Themes", "User");

    private ThemeService() : this(SettingsService.Instance, UserThemesDirectory) { }

    internal ThemeService(SettingsService settings, string userThemesDirectory)
    {
        _settings = settings;
        _userThemesDirectory = userThemesDirectory;
    }

    /// <summary>Loads saved appearance without rewriting settings.</summary>
    public void Initialize()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_isInitialized) return;
        var app = Application.Current ?? throw new InvalidOperationException("Avalonia must be initialized before themes.");
        var styleInclude = new StyleInclude(new Uri("avares://ZScape/")) { Source = new Uri(DarkUri) };
        var styles = (Styles)styleInclude.Loaded;
        // Keep one stylesheet and its brush instances. Light and custom files
        // supply colors; existing controls retain the same live brush objects.
        _paletteResources = styles.OfType<Style>().First(style => style.Resources.ContainsKey("PrimaryBackground")).Resources;
        app.Styles.Add(styleInclude);
        _isInitialized = true;

        var settings = _settings.Settings;
        CurrentAccent = settings.Accent;
        var themeId = settings.ThemeId ?? (settings.Theme == AppTheme.Light ? "Light" : "Dark");
        try
        {
            ApplyPalette(ReadPalette(themeId, settings.Theme));
            ActiveThemeId = CanonicalThemeId(themeId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or FormatException)
        {
            LoggingService.Instance.Warning($"Could not load theme '{themeId}': {ex.Message}");
            ApplyPalette(ReadPalette("Dark", AppTheme.Dark));
            ActiveThemeId = "Dark";
        }
    }

    /// <summary>Applies a named theme and persists only the accepted appearance.</summary>
    public void ApplyTheme(string themeId, string? accent = null)
    {
        Initialize();
        var palette = ReadPalette(themeId, _palette!.BaseTheme);
        var newAccent = accent ?? CurrentAccent;
        _preview?.Dispose();
        ApplyPalette(palette);
        ActiveThemeId = CanonicalThemeId(themeId);
        CurrentAccent = newAccent;
        PersistAppearance();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    [Obsolete("Use ApplyTheme(string themeId) instead.")]
    public void ApplyTheme(AppTheme theme, string? accent = null) => ApplyTheme(theme.ToString(), accent);

    public void ApplyFromSettings()
    {
        Initialize();
        var settings = _settings.Settings;
        var themeId = settings.ThemeId ?? settings.Theme.ToString();
        var palette = ReadPalette(themeId, settings.Theme);
        _preview?.Dispose();
        ApplyPalette(palette);
        ActiveThemeId = CanonicalThemeId(themeId);
        CurrentAccent = settings.Accent;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public ThemePreview BeginPreview()
    {
        Initialize();
        if (_preview != null) throw new InvalidOperationException("A theme preview is already open.");
        return _preview = new ThemePreview(this, _palette!.Copy());
    }

    private void PersistAppearance()
    {
        var settings = _settings.Settings;
        settings.Theme = _palette!.BaseTheme;
        settings.ThemeId = ActiveThemeId;
        settings.Accent = CurrentAccent;
        _settings.Save();
    }

    private void ApplyPalette(ThemePalette palette)
    {
        var resources = _paletteResources!;
        if (_palette != null)
        {
            foreach (var key in _palette.Colors.Keys.Except(palette.Colors.Keys)) resources.Remove(key);
            foreach (var key in _palette.BrushColors.Keys.Except(palette.BrushColors.Keys)) resources.Remove(key);
        }
        foreach (var (key, color) in palette.Colors) resources[key] = color;
        foreach (var (key, colorKey) in palette.BrushColors)
        {
            if (!palette.Colors.TryGetValue(colorKey, out var color)) continue;
            if (resources.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
                brush.Color = color;
            else
                resources[key] = new SolidColorBrush(color);
        }
        _palette = palette.Copy();
        CurrentTheme = palette.BaseTheme == AppTheme.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        Application.Current!.RequestedThemeVariant = CurrentTheme;
    }

    private ThemePalette ReadPalette(string themeId, AppTheme fallbackBaseTheme)
    {
        if (BuiltInThemes.TryGetValue(themeId, out var resourceName))
        {
            using var stream = typeof(ThemeService).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new IOException($"Built-in palette '{themeId}' was not embedded.");
            return ParsePalette(XDocument.Load(stream), themeId.Equals("Light", StringComparison.OrdinalIgnoreCase) ? AppTheme.Light : AppTheme.Dark);
        }
        return ReadPaletteFile(Path.Combine(_userThemesDirectory, $"{themeId}.axaml"), fallbackBaseTheme);
    }

    private ThemePalette ReadPaletteFile(string path, AppTheme fallbackBaseTheme)
    {
        var document = XDocument.Load(path);
        var baseValue = document.Descendants().FirstOrDefault(element =>
            element.Attribute(XamlNamespace + "Key")?.Value == "ZScapeBaseTheme")?.Value;
        var baseTheme = Enum.TryParse<AppTheme>(baseValue, out var parsedBase) && Enum.IsDefined(parsedBase)
            ? parsedBase : fallbackBaseTheme;
        var custom = ParsePalette(document, baseTheme);
        if (custom.Colors.Count == 0) throw new FormatException("The theme does not contain any colors.");
        var palette = ReadPalette(baseTheme.ToString(), baseTheme);
        foreach (var (key, color) in custom.Colors) palette.Colors[key] = color;
        foreach (var (key, colorKey) in custom.BrushColors) palette.BrushColors[key] = colorKey;
        if (palette.BrushColors.Values.Any(key => !palette.Colors.ContainsKey(key)))
            throw new FormatException("A theme brush refers to an undefined color.");
        return palette;
    }

    private static ThemePalette ParsePalette(XDocument document, AppTheme baseTheme)
    {
        var colors = new Dictionary<string, Color>();
        var brushes = new Dictionary<string, string>();
        foreach (var element in document.Descendants())
        {
            var key = element.Attribute(XamlNamespace + "Key")?.Value;
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (element.Name.LocalName == "Color")
                colors[key] = Color.Parse((element.Attribute("Value")?.Value ?? element.Value).Trim());
            else if (element.Name.LocalName == "SolidColorBrush")
            {
                var reference = element.Attribute("Color")?.Value;
                const string prefix = "{StaticResource ";
                if (reference?.StartsWith(prefix, StringComparison.Ordinal) == true && reference.EndsWith('}'))
                    brushes[key] = reference[prefix.Length..^1].Trim();
            }
        }
        return new ThemePalette(baseTheme, colors, brushes);
    }

    private static string CanonicalThemeId(string themeId) => BuiltInThemes.Keys.FirstOrDefault(
        name => name.Equals(themeId, StringComparison.OrdinalIgnoreCase)) ?? themeId;

    public static IBrush GetBrush(string key, string fallbackHex)
    {
        var app = Application.Current;
        return app?.TryGetResource(key, app.ActualThemeVariant, out var resource) == true && resource is IBrush brush
            ? brush : new SolidColorBrush(Color.Parse(fallbackHex));
    }

    public static Color GetColor(string key, string fallbackHex)
    {
        var app = Application.Current;
        return app?.TryGetResource(key, app.ActualThemeVariant, out var resource) == true && resource is Color color
            ? color : Color.Parse(fallbackHex);
    }

    internal sealed record ThemePalette(AppTheme BaseTheme, Dictionary<string, Color> Colors, Dictionary<string, string> BrushColors)
    {
        public ThemePalette Copy() => new(BaseTheme, new(Colors), new(BrushColors));
    }

    /// <summary>An editor draft. Disposing restores the last accepted palette without saving settings.</summary>
    public sealed class ThemePreview : IDisposable
    {
        private readonly ThemeService _owner;
        private ThemePalette _accepted;
        private bool _disposed;
        internal ThemePreview(ThemeService owner, ThemePalette accepted) { _owner = owner; _accepted = accepted; }

        public IReadOnlyDictionary<string, Color> Colors
        {
            get
            {
                VerifyActive();
                return new Dictionary<string, Color>(_owner._palette!.Colors);
            }
        }

        public void Update(IReadOnlyDictionary<string, Color> colors)
        {
            VerifyActive();
            var palette = _owner._palette!.Copy();
            foreach (var (key, color) in colors) palette.Colors[key] = color;
            _owner.ApplyPalette(palette);
            _owner.ThemeChanged?.Invoke(_owner, EventArgs.Empty);
        }

        public void Load(string path)
        {
            VerifyActive();
            var palette = _owner.ReadPaletteFile(path, _owner._palette!.BaseTheme);
            _owner.ApplyPalette(palette);
            _owner.ThemeChanged?.Invoke(_owner, EventArgs.Empty);
        }

        public void Save(string name)
        {
            VerifyActive();
            name = name.Trim();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || BuiltInThemes.ContainsKey(name))
                throw new ArgumentException("Choose a valid custom theme name other than Dark or Light.", nameof(name));

            var palette = _owner._palette!;
            XNamespace avaloniaNamespace = "https://github.com/avaloniaui";
            var root = new XElement(avaloniaNamespace + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", XamlNamespace));
            root.Add(new XElement(XamlNamespace + "String", new XAttribute(XamlNamespace + "Key", "ZScapeBaseTheme"), palette.BaseTheme));
            foreach (var (key, color) in palette.Colors.OrderBy(pair => pair.Key))
                root.Add(new XElement(avaloniaNamespace + "Color", new XAttribute(XamlNamespace + "Key", key), color.ToString()));
            foreach (var (key, colorKey) in palette.BrushColors.OrderBy(pair => pair.Key))
                root.Add(new XElement(avaloniaNamespace + "SolidColorBrush", new XAttribute(XamlNamespace + "Key", key),
                    new XAttribute("Color", $"{{StaticResource {colorKey}}}")));
            Directory.CreateDirectory(_owner._userThemesDirectory);
            new XDocument(root).Save(Path.Combine(_owner._userThemesDirectory, $"{name}.axaml"));
            _owner.ActiveThemeId = name;
            _owner.PersistAppearance();
            _accepted = palette.Copy();
            _owner.ThemeChanged?.Invoke(_owner, EventArgs.Empty);
        }

        private void VerifyActive()
        {
            Dispatcher.UIThread.VerifyAccess();
            if (_disposed || !ReferenceEquals(_owner._preview, this)) throw new ObjectDisposedException(nameof(ThemePreview));
        }

        public void Dispose()
        {
            Dispatcher.UIThread.VerifyAccess();
            if (_disposed) return;
            _disposed = true;
            if (!ReferenceEquals(_owner._preview, this)) return;
            _owner._preview = null;
            _owner.ApplyPalette(_accepted);
            _owner.ThemeChanged?.Invoke(_owner, EventArgs.Empty);
        }
    }
}

public enum AppTheme
{
    Dark = 0,
    Light = 1
}

public enum UIMode
{
    Standard = 0,
    BigUI = 1
}
