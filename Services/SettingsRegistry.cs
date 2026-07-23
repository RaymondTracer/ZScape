using System;
using System.Collections.Generic;
using System.Linq;

namespace ZScape.Services;

/// <summary>
/// What kind of control a setting field should render as.
/// </summary>
public enum SettingFieldType
{
    Toggle,
    Numeric,
    Text,
    FilePath,
    Dropdown,
    Spacer
}

/// <summary>
/// Declares one setting field — what it is, not how it looks.
/// Both the standard dialog and the Big UI consume these definitions.
/// </summary>
public sealed class SettingFieldDef
{
    public SettingFieldType Type { get; }
    public string Key { get; }                 // Unique within category. Used as a stable identifier.
    public string Label { get; }
    public string? Help { get; init; }

    // Numeric
    public int MinValue { get; init; } = 0;
    public int MaxValue { get; init; } = 100;

    // Dropdown
    public IReadOnlyList<SettingDropdownOption>? DropdownOptions { get; init; }

    public SettingFieldDef(SettingFieldType type, string key, string label, string? help = null)
    {
        Type = type;
        Key = key;
        Label = label;
        Help = help;
    }
}

/// <summary>
/// One option in a dropdown setting field.
/// </summary>
public sealed class SettingDropdownOption
{
    public string Label { get; }
    public int Value { get; }

    public SettingDropdownOption(string label, int value)
    {
        Label = label;
        Value = value;
    }
}

/// <summary>
/// A named group of setting fields within a category (e.g. "Display Options" inside "General").
/// </summary>
public sealed class SettingSectionDef
{
    public string Title { get; }
    public List<SettingFieldDef> Fields { get; } = [];

    public SettingSectionDef(string title) => Title = title;
}

/// <summary>
/// A top-level grouping of settings (e.g. "General", "Favorites", "WAD Paths").
/// </summary>
public sealed class SettingCategoryDef
{
    public string Title { get; }
    public string Icon { get; }          // Single-character icon for sidebar
    public List<SettingSectionDef> Sections { get; } = [];

    public SettingCategoryDef(string title, string icon = "")
    {
        Title = title;
        Icon = icon;
    }
}

/// <summary>
/// Central registry of all application settings, organised by category and section.
/// Used to auto-render settings in different UI modes.
/// </summary>
public static class SettingsRegistry
{
    public static IReadOnlyList<SettingCategoryDef> Categories { get; }

    static SettingsRegistry()
    {
        var cats = new List<SettingCategoryDef>();

        // ===== General =====
        var general = new SettingCategoryDef("General");
        var zandronum = new SettingSectionDef("Zandronum Configuration");
        zandronum.Fields.Add(new(SettingFieldType.FilePath, "ZandronumPath",
            "Zandronum Executable (Stable)", "Location of zandronum.exe for stable servers."));
        zandronum.Fields.Add(new(SettingFieldType.FilePath, "ZandronumTestingPath",
            "Testing Versions Folder", "Folder for auto-downloaded testing builds. Leave empty for default."));
        zandronum.Fields.Add(new(SettingFieldType.Numeric, "HashConcurrency",
            "Hash Verification Threads")
        {
            MinValue = 0, MaxValue = 32,
            Help = "0 = unlimited, 1 = sequential, N = max N concurrent hash checks."
        });
        general.Sections.Add(zandronum);

        var display = new SettingSectionDef("Display Options");
        display.Fields.Add(new(SettingFieldType.Toggle, "ColorizePlayerNames", "Colorize player names"));
        display.Fields.Add(new(SettingFieldType.Numeric, "ServerListRowHeight",
            "Server List Row Height (px)")
        {
            MinValue = 18, MaxValue = 60,
            Help = "Default: 26."
        });
        general.Sections.Add(display);

        var screenshot = new SettingSectionDef("Screenshot Consolidation");
        screenshot.Fields.Add(new(SettingFieldType.Toggle, "EnableScreenshotMonitoring",
            "Consolidate screenshots to a single folder"));
        screenshot.Fields.Add(new(SettingFieldType.FilePath, "ScreenshotConsolidationPath",
            "Screenshots Folder", "Leave empty for default location."));
        general.Sections.Add(screenshot);

        var theme = new SettingSectionDef("Appearance");
        theme.Fields.Add(new(SettingFieldType.Dropdown, "Theme",
            "Application Theme")
        {
            DropdownOptions = [new("Dark", 0), new("Light", 1)]
        });
        theme.Fields.Add(new(SettingFieldType.Dropdown, "Accent",
            "Accent Color")
        {
            DropdownOptions = [
                new("Blue", 0), new("Green", 1), new("Orange", 2),
                new("Purple", 3), new("Red", 4), new("Teal", 5)
            ]
        });
        general.Sections.Add(theme);

        cats.Add(general);

        // ===== Favorites =====
        var fav = new SettingCategoryDef("Favorites");
        var favAddrs = new SettingSectionDef("Favorite Addresses");
        favAddrs.Fields.Add(new(SettingFieldType.Spacer, "FavoriteAddressesList",
            "Favorite Addresses", "Explicit IP:Port favorites. Managed via the server list star button."));
        fav.Sections.Add(favAddrs);

        var favRules = new SettingSectionDef("Favorite Name Rules");
        favRules.Fields.Add(new(SettingFieldType.Spacer, "FavoriteNameRulesList",
            "Favorite Name Rules", "Servers matching these rules count as favorites without pinning a specific address."));
        fav.Sections.Add(favRules);

        var hiddenRules = new SettingSectionDef("Hidden Server Rules");
        hiddenRules.Fields.Add(new(SettingFieldType.Spacer, "HiddenServerRulesList",
            "Hidden Server Rules", "Matching servers are removed from the main list before other filters."));
        fav.Sections.Add(hiddenRules);

        var manualServers = new SettingSectionDef("Manually Added Servers");
        manualServers.Fields.Add(new(SettingFieldType.Spacer, "ManualServersList",
            "Manual Servers", "Manually added servers are queried first and never removed."));
        fav.Sections.Add(manualServers);

        var starBehavior = new SettingSectionDef("Star Button Behavior");
        starBehavior.Fields.Add(new(SettingFieldType.Dropdown, "FavoriteStarClickBehavior",
            "Preferred Action")
        {
            DropdownOptions = [
                new("Always Ask", 0),
                new("Add as Address Favorite", 1),
                new("Add as Exact Server-Name Favorite", 2)
            ]
        });
        fav.Sections.Add(starBehavior);

        var alerts = new SettingSectionDef("Server Alerts");
        alerts.Fields.Add(new(SettingFieldType.Toggle, "EnableFavoriteServerAlerts",
            "Alert when favorite servers come online with players"));
        alerts.Fields.Add(new(SettingFieldType.Toggle, "EnableManualServerAlerts",
            "Alert when manually added servers come online with players"));
        alerts.Fields.Add(new(SettingFieldType.Dropdown, "AlertNotificationMode",
            "Notification Style")
        {
            DropdownOptions = [new("Native", 0), new("Custom Popup", 1)]
        });
        alerts.Fields.Add(new(SettingFieldType.Dropdown, "CustomNotificationCorner",
            "Custom Popup Corner")
        {
            DropdownOptions = [
                new("Top Left", 0), new("Top Right", 1),
                new("Bottom Left", 2), new("Bottom Right", 3)
            ]
        });
        alerts.Fields.Add(new(SettingFieldType.Numeric, "CustomNotificationDurationSeconds",
            "Custom Popup Duration (seconds)")
        {
            MinValue = 0, MaxValue = 600,
            Help = "0 = until dismissed."
        });
        alerts.Fields.Add(new(SettingFieldType.Toggle, "ShowFavoritesColumn",
            "Show favorites column in server list"));
        alerts.Fields.Add(new(SettingFieldType.Numeric, "AlertMinPlayers",
            "Minimum Players to Alert")
        {
            MinValue = 0, MaxValue = 64,
            Help = "Only alert when at least this many players are online."
        });
        alerts.Fields.Add(new(SettingFieldType.Numeric, "AlertCheckIntervalSeconds",
            "Alert Check Interval (seconds)")
        {
            MinValue = 30, MaxValue = 600,
            Help = "How often to check for alert conditions."
        });
        fav.Sections.Add(alerts);

        cats.Add(fav);

        // ===== WAD Paths =====
        var wadPaths = new SettingCategoryDef("WAD Paths");
        var paths = new SettingSectionDef("Search Paths");
        paths.Fields.Add(new(SettingFieldType.Spacer, "WadSearchPathsList",
            "WAD Search Paths", "Folders to search for WAD files. Order matters."));
        wadPaths.Sections.Add(paths);

        var dlPath = new SettingSectionDef("Download Path");
        dlPath.Fields.Add(new(SettingFieldType.FilePath, "WadDownloadPath",
            "Default Download Folder", "Where downloaded WADs are saved."));
        wadPaths.Sections.Add(dlPath);

        cats.Add(wadPaths);

        // ===== Download Sites =====
        var sites = new SettingCategoryDef("Download Sites");
        var siteList = new SettingSectionDef("WAD Download Sites");
        siteList.Fields.Add(new(SettingFieldType.Spacer, "DownloadSitesList",
            "Download Sites", "Sites searched in order for missing WADs."));
        sites.Sections.Add(siteList);
        cats.Add(sites);

        // ===== Downloads =====
        var dls = new SettingCategoryDef("Downloads");
        var dlBehavior = new SettingSectionDef("Download Behavior");
        dlBehavior.Fields.Add(new(SettingFieldType.Dropdown, "DownloadDialogBehavior",
            "When Joining Server")
        {
            DropdownOptions = [
                new("Close on Success", 0),
                new("Stay Open", 1),
                new("Auto-Download Required Files", 2)
            ]
        });
        dlBehavior.Fields.Add(new(SettingFieldType.Dropdown, "OptionalPwadDownloadMode",
            "Optional PWAD Policy")
        {
            DropdownOptions = [
                new("Ask Each Time", 0),
                new("Auto-Download", 1),
                new("Skip All", 2)
            ]
        });
        dls.Sections.Add(dlBehavior);

        var dlConcurrency = new SettingSectionDef("Download Concurrency");
        dlConcurrency.Fields.Add(new(SettingFieldType.Numeric, "MaxConcurrentDownloads",
            "Max Downloads")
        {
            MinValue = 0, MaxValue = 100,
            Help = "Total simultaneous file downloads. 0 = unlimited."
        });
        dlConcurrency.Fields.Add(new(SettingFieldType.Numeric, "MaxConcurrentDomains",
            "Max Domains")
        {
            MinValue = 0, MaxValue = 50,
            Help = "How many servers to download from at once. 0 = unlimited."
        });
        dlConcurrency.Fields.Add(new(SettingFieldType.Numeric, "MaxThreadsPerFile",
            "Max Threads per File")
        {
            MinValue = 0, MaxValue = 1024,
            Help = "Hard limit on concurrent connections per download. 0 = no global limit."
        });
        dlConcurrency.Fields.Add(new(SettingFieldType.Numeric, "DefaultMinSegmentSizeKb",
            "Min Segment Size (KB)")
        {
            MinValue = 64, MaxValue = 4096,
            Help = "Minimum segment size in KB for new download domains."
        });
        dls.Sections.Add(dlConcurrency);

        var skipped = new SettingSectionDef("Skipped Optional PWADs");
        skipped.Fields.Add(new(SettingFieldType.Spacer, "SkippedOptionalPwadsList",
            "Skipped PWAD Names", "Optional PWADs defaulting to unchecked when prompted."));
        dls.Sections.Add(skipped);

        cats.Add(dls);

        // ===== Domain Threads =====
        var domains = new SettingCategoryDef("Domain Threads");
        var domainSection = new SettingSectionDef("Per-Domain Settings");
        domainSection.Fields.Add(new(SettingFieldType.Spacer, "DomainThreadsTable",
            "Domain Thread Settings", "Per-domain download thread limits. Managed via the table editor."));
        domains.Sections.Add(domainSection);
        cats.Add(domains);

        // ===== Server Queries =====
        var queries = new SettingCategoryDef("Server Queries");
        var querySection = new SettingSectionDef("Query Settings");
        querySection.Fields.Add(new(SettingFieldType.Numeric, "QueryIntervalMs",
            "Query Interval (ms)")
        {
            MinValue = 1, MaxValue = 1000,
            Help = "Lower = faster but more aggressive."
        });
        querySection.Fields.Add(new(SettingFieldType.Numeric, "MaxConcurrentQueries",
            "Max Concurrent Queries")
        {
            MinValue = 0, MaxValue = 500,
            Help = "0 = unlimited."
        });
        querySection.Fields.Add(new(SettingFieldType.Numeric, "QueryRetryAttempts",
            "Retry Attempts")
        {
            MinValue = 1, MaxValue = 10,
            Help = "How many times to retry a failed server query."
        });
        querySection.Fields.Add(new(SettingFieldType.Numeric, "QueryRetryDelayMs",
            "Retry Delay (ms)")
        {
            MinValue = 100, MaxValue = 10000,
            Help = "Time between retry attempts."
        });
        querySection.Fields.Add(new(SettingFieldType.Numeric, "MasterServerRetryCount",
            "Master Server Retries")
        {
            MinValue = 1, MaxValue = 10,
            Help = "How many times to retry the master server."
        });
        querySection.Fields.Add(new(SettingFieldType.Numeric, "ConsecutiveFailuresBeforeOffline",
            "Failures Before Offline")
        {
            MinValue = 1, MaxValue = 10,
            Help = "Consecutive failures before marking a server as offline."
        });
        querySection.Fields.Add(new(SettingFieldType.Numeric, "AutoRefreshIntervalMinutes",
            "Auto Refresh (minutes)")
        {
            MinValue = 1, MaxValue = 60,
            Help = "How often to auto-refresh the server list."
        });
        querySection.Fields.Add(new(SettingFieldType.Numeric, "AutoRefreshFavoritesIntervalMinutes",
            "Favorites Refresh (minutes)")
        {
            MinValue = 1, MaxValue = 60,
            Help = "How often to refresh favorites. Separate from full refresh."
        });
        queries.Sections.Add(querySection);
        cats.Add(queries);

        // ===== Updates =====
        var updates = new SettingCategoryDef("Updates");
        var updateSection = new SettingSectionDef("Automatic Updates");
        updateSection.Fields.Add(new(SettingFieldType.Dropdown, "UpdateBehavior",
            "Update Behavior")
        {
            DropdownOptions = [
                new("Disabled", 0),
                new("Notify Only", 1),
                new("Check and Download", 2)
            ]
        });
        updateSection.Fields.Add(new(SettingFieldType.Toggle, "AutoRestartForUpdates",
            "Auto-restart to install updates (when idle)"));
        updateSection.Fields.Add(new(SettingFieldType.Numeric, "UpdateCheckIntervalValue",
            "Check Interval Value")
        {
            MinValue = 1, MaxValue = 99
        });
        updateSection.Fields.Add(new(SettingFieldType.Dropdown, "UpdateCheckIntervalUnit",
            "Check Interval Unit")
        {
            DropdownOptions = [new("Hours", 0), new("Days", 1), new("Weeks", 2)]
        });
        updates.Sections.Add(updateSection);
        cats.Add(updates);

        Categories = cats;
    }
}

/// <summary>
/// Helpers to read/write individual setting values from/to AppSettings by key.
/// </summary>
public static class SettingsFieldAccessor
{
    public static object? GetValue(AppSettings s, string key) => key switch
    {
        // General
        "ZandronumPath" => s.ZandronumPath,
        "ZandronumTestingPath" => s.ZandronumTestingPath,
        "HashConcurrency" => s.HashVerificationConcurrency,
        "ColorizePlayerNames" => s.ColorizePlayerNames,
        "ServerListRowHeight" => s.ServerListRowHeight,
        "EnableScreenshotMonitoring" => s.EnableScreenshotMonitoring,
        "ScreenshotConsolidationPath" => s.ScreenshotConsolidationPath,
        "Theme" => (int)s.Theme,
        "Accent" => AccentIndex(s.Accent),

        // Favorites
        "FavoriteStarClickBehavior" => (int)s.FavoriteStarClickBehavior,
        "EnableFavoriteServerAlerts" => s.EnableFavoriteServerAlerts,
        "EnableManualServerAlerts" => s.EnableManualServerAlerts,
        "AlertNotificationMode" => (int)s.AlertNotificationMode,
        "CustomNotificationCorner" => (int)s.CustomNotificationCorner,
        "CustomNotificationDurationSeconds" => s.CustomNotificationDurationSeconds,
        "ShowFavoritesColumn" => s.ShowFavoritesColumn,
        "AlertMinPlayers" => s.AlertMinPlayers,
        "AlertCheckIntervalSeconds" => s.AlertCheckIntervalSeconds,

        // WAD Paths
        "WadDownloadPath" => s.WadDownloadPath,

        // Downloads
        "DownloadDialogBehavior" => (int)s.DownloadDialogBehavior,
        "OptionalPwadDownloadMode" => (int)s.OptionalPwadDownloadMode,
        "MaxConcurrentDownloads" => s.MaxConcurrentDownloads,
        "MaxConcurrentDomains" => s.MaxConcurrentDomains,
        "MaxThreadsPerFile" => s.MaxThreadsPerFile,
        "DefaultMinSegmentSizeKb" => s.DefaultMinSegmentSizeKb,

        // Server Queries
        "QueryIntervalMs" => s.QueryIntervalMs,
        "MaxConcurrentQueries" => s.MaxConcurrentQueries,
        "QueryRetryAttempts" => s.QueryRetryAttempts,
        "QueryRetryDelayMs" => s.QueryRetryDelayMs,
        "MasterServerRetryCount" => s.MasterServerRetryCount,
        "ConsecutiveFailuresBeforeOffline" => s.ConsecutiveFailuresBeforeOffline,
        "AutoRefreshIntervalMinutes" => s.AutoRefreshIntervalMinutes,
        "AutoRefreshFavoritesIntervalMinutes" => s.AutoRefreshFavoritesIntervalMinutes,

        // Updates
        "UpdateBehavior" => (int)s.UpdateBehavior,
        "AutoRestartForUpdates" => s.AutoRestartForUpdates,
        "UpdateCheckIntervalValue" => s.UpdateCheckIntervalValue,
        "UpdateCheckIntervalUnit" => (int)s.UpdateCheckIntervalUnit,

        _ => null
    };

    public static void SetValue(AppSettings s, string key, object? value)
    {
        switch (key)
        {
            // General
            case "ZandronumPath": s.ZandronumPath = (string?)value ?? ""; break;
            case "ZandronumTestingPath": s.ZandronumTestingPath = (string?)value ?? ""; break;
            case "HashConcurrency": s.HashVerificationConcurrency = (int)(value ?? 0); break;
            case "ColorizePlayerNames": s.ColorizePlayerNames = (bool)(value ?? true); break;
            case "ServerListRowHeight": s.ServerListRowHeight = (int)(value ?? 26); break;
            case "EnableScreenshotMonitoring": s.EnableScreenshotMonitoring = (bool)(value ?? false); break;
            case "ScreenshotConsolidationPath": s.ScreenshotConsolidationPath = (string?)value ?? ""; break;
            case "Theme": s.Theme = (AppTheme)(int)(value ?? 0); break;
            case "Accent": s.Accent = AccentName((int)(value ?? 0)); break;

            // Favorites
            case "FavoriteStarClickBehavior": s.FavoriteStarClickBehavior = (FavoriteStarClickBehavior)(int)(value ?? 0); break;
            case "EnableFavoriteServerAlerts": s.EnableFavoriteServerAlerts = (bool)(value ?? false); break;
            case "EnableManualServerAlerts": s.EnableManualServerAlerts = (bool)(value ?? false); break;
            case "AlertNotificationMode": s.AlertNotificationMode = (NotificationDisplayMode)(int)(value ?? 0); break;
            case "CustomNotificationCorner": s.CustomNotificationCorner = (CustomNotificationCorner)(int)(value ?? 0); break;
            case "CustomNotificationDurationSeconds": s.CustomNotificationDurationSeconds = (int)(value ?? 15); break;
            case "ShowFavoritesColumn": s.ShowFavoritesColumn = (bool)(value ?? true); break;
            case "AlertMinPlayers": s.AlertMinPlayers = (int)(value ?? 1); break;
            case "AlertCheckIntervalSeconds": s.AlertCheckIntervalSeconds = (int)(value ?? 60); break;

            // WAD Paths
            case "WadDownloadPath": s.WadDownloadPath = (string?)value ?? ""; break;

            // Downloads
            case "DownloadDialogBehavior": s.DownloadDialogBehavior = (DownloadDialogBehavior)(int)(value ?? 0); break;
            case "OptionalPwadDownloadMode": s.OptionalPwadDownloadMode = (OptionalPwadDownloadMode)(int)(value ?? 0); break;
            case "MaxConcurrentDownloads": s.MaxConcurrentDownloads = (int)(value ?? 0); break;
            case "MaxConcurrentDomains": s.MaxConcurrentDomains = (int)(value ?? 8); break;
            case "MaxThreadsPerFile": s.MaxThreadsPerFile = (int)(value ?? 0); break;
            case "DefaultMinSegmentSizeKb": s.DefaultMinSegmentSizeKb = (int)(value ?? 256); break;

            // Server Queries
            case "QueryIntervalMs": s.QueryIntervalMs = (int)(value ?? 5); break;
            case "MaxConcurrentQueries": s.MaxConcurrentQueries = (int)(value ?? 50); break;
            case "QueryRetryAttempts": s.QueryRetryAttempts = (int)(value ?? 2); break;
            case "QueryRetryDelayMs": s.QueryRetryDelayMs = (int)(value ?? 2000); break;
            case "MasterServerRetryCount": s.MasterServerRetryCount = (int)(value ?? 3); break;
            case "ConsecutiveFailuresBeforeOffline": s.ConsecutiveFailuresBeforeOffline = (int)(value ?? 3); break;
            case "AutoRefreshIntervalMinutes": s.AutoRefreshIntervalMinutes = (int)(value ?? 5); break;
            case "AutoRefreshFavoritesIntervalMinutes": s.AutoRefreshFavoritesIntervalMinutes = (int)(value ?? 5); break;

            // Updates
            case "UpdateBehavior": s.UpdateBehavior = (UpdateBehavior)(int)(value ?? 2); break;
            case "AutoRestartForUpdates": s.AutoRestartForUpdates = (bool)(value ?? false); break;
            case "UpdateCheckIntervalValue": s.UpdateCheckIntervalValue = (int)(value ?? 1); break;
            case "UpdateCheckIntervalUnit": s.UpdateCheckIntervalUnit = (UpdateIntervalUnit)(int)(value ?? 1); break;
        }
    }

    private static int AccentIndex(string accent) => accent switch
    {
        "Blue" => 0, "Green" => 1, "Orange" => 2,
        "Purple" => 3, "Red" => 4, "Teal" => 5,
        _ => 0
    };

    private static string AccentName(int index) => index switch
    {
        0 => "Blue", 1 => "Green", 2 => "Orange",
        3 => "Purple", 4 => "Red", 5 => "Teal",
        _ => "Blue"
    };
}
