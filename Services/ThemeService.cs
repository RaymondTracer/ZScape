using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace ZScape.Services;

/// <summary>
/// Manages application theme (Dark/Light) with optional accent overrides.
/// Handles live theme switching at runtime.
/// </summary>
public class ThemeService
{
    private static readonly Lazy<ThemeService> _instance = new(() => new ThemeService());
    public static ThemeService Instance => _instance.Value;

    public event EventHandler? ThemeChanged;

    private const string BuiltInDarkUri = "avares://ZScape/Themes/DarkTheme.axaml";
    private const string BuiltInLightUri = "avares://ZScape/Themes/LightTheme.axaml";

    private readonly Dictionary<AppTheme, string> _builtInThemeUris = new()
    {
        [AppTheme.Dark] = BuiltInDarkUri,
        [AppTheme.Light] = BuiltInLightUri,
    };

    private ThemeVariant _currentTheme = ThemeVariant.Dark;
    private string _currentAccent = "Blue";
    private StyleInclude? _loadedThemeStyle;

    public ThemeVariant CurrentTheme => _currentTheme;
    public string CurrentAccent => _currentAccent;
    public bool IsDark => _currentTheme == ThemeVariant.Dark;
    public bool IsLight => _currentTheme == ThemeVariant.Light;

    private ThemeService() { }

    /// <summary>
    /// Applies the specified theme and accent at runtime.
    /// </summary>
    public void ApplyTheme(AppTheme theme, string? accent = null)
    {
        var newVariant = theme == AppTheme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var newAccent = accent ?? _currentAccent;

        if (newVariant == _currentTheme && newAccent == _currentAccent)
            return;

        _currentTheme = newVariant;
        _currentAccent = newAccent;

        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = newVariant;
            if (theme == AppTheme.Light)
                EnsureLightThemeLoaded(app);
            else
                RemoveLightTheme(app);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// DarkTheme is loaded statically in App.axaml. We only need to add/remove
    /// LightTheme to toggle between them, since LightTheme's keys override Dark's.
    /// </summary>
    private void EnsureLightThemeLoaded(Application app)
    {
        if (_loadedThemeStyle != null) return;
        var styleInclude = new StyleInclude(new Uri("avares://ZScape/"))
        {
            Source = new Uri(BuiltInLightUri)
        };
        app.Styles.Add(styleInclude);
        _loadedThemeStyle = styleInclude;
    }

    private void RemoveLightTheme(Application app)
    {
        if (_loadedThemeStyle == null) return;
        app.Styles.Remove(_loadedThemeStyle);
        _loadedThemeStyle = null;
    }

    /// <summary>
    /// Applies theme from persisted settings.
    /// </summary>
    public void ApplyFromSettings()
    {
        var settings = SettingsService.Instance.Settings;
        ApplyTheme(settings.Theme, settings.Accent);
    }

    /// <summary>
    /// Resolves an IBrush from a resource key with a fallback hex color.
    /// if the resource is not found (e.g., code-behind that runs before theme loads).
    /// </summary>
    public static IBrush GetBrush(string key, string fallbackHex)
    {
        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var resource) == true
            && resource is IBrush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Color.Parse(fallbackHex));
    }

    /// <summary>
    /// Resolves a Color from a resource key. Falls back to the specified default.
    /// </summary>
    public static Color GetColor(string key, string fallbackHex)
    {
        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var resource) == true
            && resource is Color color)
        {
            return color;
        }
        return Color.Parse(fallbackHex);
    }
}

public enum AppTheme
{
    Dark = 0,
    Light = 1
}
