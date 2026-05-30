using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace ZScape.Services;

/// <summary>
/// Manages application themes. Call <see cref="Initialize"/> once in App.axaml.cs
/// before the first window is created. Supports built-in themes (Dark, Light) and
/// user-supplied theme files loaded from disk.
/// 
/// Architecture: ThemeService owns exactly one <see cref="StyleInclude"/> in
/// Application.Current.Styles. Swapping themes removes the old and adds the new.
/// </summary>
public class ThemeService
{
    private static readonly Lazy<ThemeService> _instance = new(() => new ThemeService());
    public static ThemeService Instance => _instance.Value;

    // Built-in themes (embedded avares resources)
    private const string DarkUri = "avares://ZScape/Themes/DarkTheme.axaml";
    private const string LightUri = "avares://ZScape/Themes/LightTheme.axaml";

    private static readonly Dictionary<string, string> BuiltInThemes = new()
    {
        ["Dark"] = DarkUri,
        ["Light"] = LightUri,
    };

    public event EventHandler? ThemeChanged;

    private ThemeVariant _currentTheme = ThemeVariant.Dark;
    private string _currentAccent = "Blue";
    private string _activeThemeId = "Dark"; // key into BuiltInThemes or user theme name
    private StyleInclude? _styleInclude;
    private bool _isInitialized;

    public ThemeVariant CurrentTheme => _currentTheme;
    public string CurrentAccent => _currentAccent;
    public bool IsDark => _currentTheme == ThemeVariant.Dark;
    public bool IsLight => _currentTheme == ThemeVariant.Light;

    /// <summary>
    /// Returns available built-in theme names.
    /// </summary>
    public static IReadOnlyList<string> GetBuiltInThemeNames() => new List<string>(BuiltInThemes.Keys);

    private ThemeService() { }

    /// <summary>
    /// Must be called once after Avalonia is initialized but before the first window.
    /// Loads the saved theme from settings and applies it.
    /// </summary>
    public void Initialize()
    {
        if (_isInitialized) return;
        _isInitialized = true;

        var settings = SettingsService.Instance.Settings;

        // Determine which theme to load
        var themeId = settings.ThemeId ?? "Dark";

        // Try built-in themes first, then user themes
        if (BuiltInThemes.TryGetValue(themeId, out var uri))
        {
            LoadThemeInternal(uri, ThemeVariant.Dark, themeId);
        }
        else
        {
            // User theme: try loading from disk
            var userPath = GetUserThemePath(themeId);
            if (File.Exists(userPath))
            {
                LoadThemeInternal(userPath, ThemeVariant.Dark, themeId);
            }
            else
            {
                // Fallback to Dark
                LoadThemeInternal(DarkUri, ThemeVariant.Dark, "Dark");
            }
        }

        // If saved theme says Light, then layer the light variant/overrides
        if (settings.Theme == AppTheme.Light)
        {
            _currentTheme = ThemeVariant.Light;
            if (Application.Current is { } app)
                app.RequestedThemeVariant = ThemeVariant.Light;
        }
    }

    /// <summary>
    /// Switches to a named theme at runtime. "Dark" and "Light" are built-in.
    /// Any other name is treated as a user theme file in the themes directory.
    /// </summary>
    public void ApplyTheme(string themeId, string? accent = null)
    {
        if (!_isInitialized) Initialize();

        var newAccent = accent ?? _currentAccent;
        var isDark = string.Equals(themeId, "Dark", StringComparison.OrdinalIgnoreCase);
        var newVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        if (themeId == _activeThemeId && newAccent == _currentAccent)
            return;

        _currentAccent = newAccent;
        _activeThemeId = themeId;
        _currentTheme = newVariant;

        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = newVariant;

            if (BuiltInThemes.TryGetValue(themeId, out var uri))
            {
                LoadThemeInternal(uri, newVariant, themeId);
            }
            else
            {
                var userPath = GetUserThemePath(themeId);
                if (File.Exists(userPath))
                    LoadThemeInternal(userPath, newVariant, themeId);
            }
        }

        // Persist
        var settings = SettingsService.Instance.Settings;
        settings.Theme = isDark ? AppTheme.Dark : AppTheme.Light;
        settings.ThemeId = themeId;
        SettingsService.Instance.Save();

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    [Obsolete("Use ApplyTheme(string themeId) instead.")]
    public void ApplyTheme(AppTheme theme, string? accent = null)
    {
        ApplyTheme(theme == AppTheme.Dark ? "Dark" : "Light", accent);
    }

    /// <summary>
    /// Applies theme from persisted settings.
    /// </summary>
    public void ApplyFromSettings()
    {
        var s = SettingsService.Instance.Settings;
        ApplyTheme(s.ThemeId ?? (s.Theme == AppTheme.Dark ? "Dark" : "Light"), s.Accent);
    }

    /// <summary>
    /// Resolves an IBrush from a resource key with a fallback hex color.
    /// </summary>
    public static IBrush GetBrush(string key, string fallbackHex)
    {
        var app = Application.Current;
        if (app == null) return new SolidColorBrush(Color.Parse(fallbackHex));
        if (app.TryGetResource(key, app.ActualThemeVariant, out var r) && r is IBrush brush) return brush;
        return new SolidColorBrush(Color.Parse(fallbackHex));
    }

    /// <summary>
    /// Resolves a Color from a resource key with a fallback hex color.
    /// </summary>
    public static Color GetColor(string key, string fallbackHex)
    {
        var app = Application.Current;
        if (app == null) return Color.Parse(fallbackHex);
        if (app.TryGetResource(key, app.ActualThemeVariant, out var r) && r is Color color) return color;
        return Color.Parse(fallbackHex);
    }

    /// <summary>
    /// Path where user-created theme files are stored.
    /// </summary>
    public static string UserThemesDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Themes", "User");

    private static string GetUserThemePath(string themeId) =>
        Path.Combine(UserThemesDirectory, $"{themeId}.axaml");

    private void LoadThemeInternal(string sourceUri, ThemeVariant variant, string themeId)
    {
        if (Application.Current is not { } app) return;

        // Remove old theme
        if (_styleInclude != null)
        {
            app.Styles.Remove(_styleInclude);
            _styleInclude = null;
        }

        _activeThemeId = themeId;

        var styleInclude = new StyleInclude(new Uri("avares://ZScape/"))
        {
            Source = new Uri(sourceUri, sourceUri.StartsWith("avares://") ? UriKind.Absolute : UriKind.RelativeOrAbsolute)
        };
        app.Styles.Add(styleInclude);
        _styleInclude = styleInclude;
    }
}

public enum AppTheme
{
    Dark = 0,
    Light = 1
}
