using ZScape.Services;

namespace ZScape.Utilities;

/// <summary>
/// Centralized path resolution for application directories.
/// Single source of truth for default folder logic.
/// </summary>
public static class PathResolver
{
    /// <summary>
    /// Folder under the current user's Documents directory that ZScape owns
    /// for non-runtime files such as build archives and safe backups.
    /// </summary>
    public const string ZScapeDocumentsFolderName = "ZScape";

    /// <summary>
    /// Subfolder under <see cref="ZScapeDocumentsFolderName"/> containing
    /// timestamped copies made before ZScape changes a user file.
    /// </summary>
    public const string BackupsFolderName = "Backups";

    /// <summary>
    /// Subfolder under <see cref="ZScapeDocumentsFolderName"/> containing
    /// manually archived application build outputs.
    /// </summary>
    public const string BuildsFolderName = "Builds";

    /// <summary>
    /// Gets the root directory in Documents that is reserved for ZScape-owned
    /// files. The directory is not created until a caller needs to write to it.
    /// </summary>
    public static string GetZScapeDocumentsPath()
    {
        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documentsPath)
            ? Path.Combine(AppContext.BaseDirectory, ZScapeDocumentsFolderName)
            : Path.Combine(documentsPath, ZScapeDocumentsFolderName);
    }

    /// <summary>
    /// Gets the central location for safe copies made before ZScape updates a
    /// user-managed file.
    /// </summary>
    public static string GetBackupsPath() =>
        Path.Combine(GetZScapeDocumentsPath(), BackupsFolderName);

    /// <summary>
    /// Gets the central location for timestamped ZScape executable archives.
    /// </summary>
    public static string GetBuildsPath() =>
        Path.Combine(GetZScapeDocumentsPath(), BuildsFolderName);

    /// <summary>
    /// Gets the central location for snapshots made before configuration
    /// synchronization changes a Zandronum INI file.
    /// </summary>
    public static string GetConfigurationSyncBackupsPath() =>
        Path.Combine(GetBackupsPath(), "Configuration Sync");

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
