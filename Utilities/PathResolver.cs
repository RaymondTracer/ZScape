using ZScape.Services;

namespace ZScape.Utilities;

/// <summary>
/// Centralized path resolution for application directories.
/// Single source of truth for default folder logic.
/// </summary>
public static class PathResolver
{
    /// <summary>
    /// Default subfolder name for testing versions.
    /// </summary>
    public const string TestingVersionsFolderName = "TestingVersions";
    
    /// <summary>
    /// Default subfolder name for consolidated screenshots.
    /// </summary>
    public const string ScreenshotsFolderName = "Screenshots";
    
    /// <summary>
    /// Gets the configured or default path for testing versions.
    /// Returns null if no path can be determined.
    /// </summary>
    public static string? GetTestingVersionsPath()
    {
        var settings = SettingsService.Instance.Settings;
        return GetTestingVersionsPath(settings);
    }
    
    /// <summary>
    /// Gets the configured or default path for testing versions.
    /// Returns null if no path can be determined.
    /// </summary>
    public static string? GetTestingVersionsPath(AppSettings settings)
    {
        // Use configured path if specified
        if (!string.IsNullOrEmpty(settings.ZandronumTestingPath))
        {
            return settings.ZandronumTestingPath;
        }
        
        // Fall back to TestingVersions subfolder next to stable exe
        var zandDir = GetZandronumDirectory(settings);
        if (!string.IsNullOrEmpty(zandDir))
        {
            return Path.Combine(zandDir, TestingVersionsFolderName);
        }
        
        return null;
    }
    
    /// <summary>
    /// Gets the configured or default path for consolidated screenshots.
    /// Returns null if no path can be determined.
    /// </summary>
    public static string? GetScreenshotsPath()
    {
        var settings = SettingsService.Instance.Settings;
        return GetScreenshotsPath(settings);
    }
    
    /// <summary>
    /// Gets the configured or default path for consolidated screenshots.
    /// Returns null if no path can be determined.
    /// </summary>
    public static string? GetScreenshotsPath(AppSettings settings)
    {
        // Use configured path if specified
        if (!string.IsNullOrEmpty(settings.ScreenshotConsolidationPath))
        {
            return settings.ScreenshotConsolidationPath;
        }
        
        // Fall back to Screenshots subfolder next to stable exe
        var zandDir = GetZandronumDirectory(settings);
        if (!string.IsNullOrEmpty(zandDir))
        {
            return Path.Combine(zandDir, ScreenshotsFolderName);
        }
        
        return null;
    }
    
    /// <summary>
    /// Gets the default testing versions path for display purposes.
    /// Returns the path that would be used if the setting is empty.
    /// Returns null if no default can be determined.
    /// </summary>
    public static string? GetDefaultTestingVersionsPath()
    {
        var settings = SettingsService.Instance.Settings;
        return GetDefaultTestingVersionsPath(settings);
    }
    
    /// <summary>
    /// Gets the default testing versions path for display purposes.
    /// Returns the path that would be used if the setting is empty.
    /// Returns null if no default can be determined.
    /// </summary>
    public static string? GetDefaultTestingVersionsPath(AppSettings settings)
    {
        var zandDir = GetZandronumDirectory(settings);
        if (!string.IsNullOrEmpty(zandDir))
        {
            return Path.Combine(zandDir, TestingVersionsFolderName);
        }
        return null;
    }
    
    /// <summary>
    /// Gets the default screenshots path for display purposes.
    /// Returns the path that would be used if the setting is empty.
    /// Returns null if no default can be determined.
    /// </summary>
    public static string? GetDefaultScreenshotsPath()
    {
        var settings = SettingsService.Instance.Settings;
        return GetDefaultScreenshotsPath(settings);
    }
    
    /// <summary>
    /// Gets the default screenshots path for display purposes.
    /// Returns the path that would be used if the setting is empty.
    /// Returns null if no default can be determined.
    /// </summary>
    public static string? GetDefaultScreenshotsPath(AppSettings settings)
    {
        var zandDir = GetZandronumDirectory(settings);
        if (!string.IsNullOrEmpty(zandDir))
        {
            return Path.Combine(zandDir, ScreenshotsFolderName);
        }
        return null;
    }
    
    /// <summary>
    /// Gets the directory containing the Zandronum executable.
    /// Returns null if not configured or executable doesn't exist.
    /// </summary>
    public static string? GetZandronumDirectory()
    {
        var settings = SettingsService.Instance.Settings;
        return GetZandronumDirectory(settings);
    }
    
    /// <summary>
    /// Gets the directory containing the Zandronum executable.
    /// Returns null if not configured or executable doesn't exist.
    /// </summary>
    public static string? GetZandronumDirectory(AppSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.ZandronumPath) && File.Exists(settings.ZandronumPath))
        {
            return Path.GetDirectoryName(settings.ZandronumPath);
        }
        return null;
    }
}
