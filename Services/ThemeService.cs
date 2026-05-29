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

    private ThemeVariant _currentTheme = ThemeVariant.Dark;
    private string _currentAccent = "Blue"; // default accent

    public ThemeVariant CurrentTheme => _currentTheme;
    public string CurrentAccent => _currentAccent;
    public bool IsDark => _currentTheme == ThemeVariant.Dark;

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

        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = newVariant;
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
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
    /// Resolves an IBrush from a resource key. Falls back to the specified default hex color
    /// if the resource is not found (e.g., code-behind that runs before theme loads).
    /// </summary>
    public static IBrush GetBrush(string key, string fallbackHex)
    {
        if (Application.Current?.TryGetResource(key, ThemeVariant.Dark, out var resource) == true
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
        if (Application.Current?.TryGetResource(key, ThemeVariant.Dark, out var resource) == true
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
