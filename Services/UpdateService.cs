using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace ZScape.Services;

/// <summary>
/// Manages automatic updates from GitHub releases.
/// </summary>
public class UpdateService
{
    private static readonly Lazy<UpdateService> _instance = new(() => new UpdateService());
    public static UpdateService Instance => _instance.Value;

    private const string GitHubApiUrl = "https://api.github.com/repos/{0}/{1}/releases/latest";
    private const string UserAgent = "ZScape-UpdateChecker";
    
    private readonly HttpClient _httpClient;
    private readonly string _updateDirectory;
    private readonly string _currentVersion;
    
    private GitHubRelease? _latestRelease;
    private string? _downloadedUpdatePath;
    private bool _isChecking;
    private bool _isDownloading;
    
    /// <summary>Callback to check if the application is busy (e.g., refreshing servers).</summary>
    public Func<bool>? IsApplicationBusy { get; set; }
    
    /// <summary>Fired when a new update is available.</summary>
    public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;
    
    /// <summary>Fired when update download progress changes.</summary>
    public event EventHandler<UpdateDownloadProgressEventArgs>? DownloadProgress;
    
    /// <summary>Fired when update is ready to install.</summary>
    public event EventHandler? UpdateReady;
    
    /// <summary>The latest available version, if known.</summary>
    public string? LatestVersion => _latestRelease?.TagName?.TrimStart('v');
    
    /// <summary>The current application version.</summary>
    public string CurrentVersion => _currentVersion;
    
    /// <summary>Whether an update has been downloaded and is ready to install.</summary>
    public bool IsUpdateReady => !string.IsNullOrEmpty(_downloadedUpdatePath) && File.Exists(_downloadedUpdatePath);
    
    /// <summary>Whether a newer version is available.</summary>
    public bool IsUpdateAvailable => _latestRelease != null && IsNewerVersion(_latestRelease.TagName);
    
    /// <summary>Release notes for the latest version.</summary>
    public string? ReleaseNotes => _latestRelease?.Body;
    
    private UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        
        _updateDirectory = Path.Combine(AppContext.BaseDirectory, "updates");
        
        // Get current version from assembly
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        _currentVersion = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
    }
    
    /// <summary>
    /// Check for updates from GitHub.
    /// </summary>
    /// <returns>True if a newer version is available.</returns>
    public async Task<bool> CheckForUpdatesAsync()
    {
        if (_isChecking) return false;
        
        var settings = SettingsService.Instance.Settings;
        if (string.IsNullOrEmpty(settings.GitHubOwner) || string.IsNullOrEmpty(settings.GitHubRepo))
        {
            LoggingService.Instance.Warning("GitHub repository not configured for updates");
            return false;
        }
        
        _isChecking = true;
        try
        {
            var url = string.Format(GitHubApiUrl, settings.GitHubOwner, settings.GitHubRepo);
            var response = await _httpClient.GetAsync(url);
            
            if (!response.IsSuccessStatusCode)
            {
                LoggingService.Instance.Warning($"Failed to check for updates: {response.StatusCode}");
                return false;
            }
            
            _latestRelease = await response.Content.ReadFromJsonAsync<GitHubRelease>();
            
            if (_latestRelease == null)
            {
                LoggingService.Instance.Warning("Failed to parse GitHub release response");
                return false;
            }
            
            var isNewer = IsNewerVersion(_latestRelease.TagName);
            
            if (isNewer)
            {
                LoggingService.Instance.Info($"Update available: {_latestRelease.TagName} (current: v{_currentVersion})");
                UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs(
                    _latestRelease.TagName?.TrimStart('v') ?? "unknown",
                    _currentVersion,
                    _latestRelease.Body ?? "",
                    _latestRelease.HtmlUrl ?? ""
                ));
            }
            else
            {
                LoggingService.Instance.Info($"No updates available (current: v{_currentVersion}, latest: {_latestRelease.TagName})");
            }
            
            // Update last check time
            settings.LastUpdateCheck = DateTime.UtcNow;
            SettingsService.Instance.Save();
            
            return isNewer;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Error checking for updates: {ex.Message}");
            return false;
        }
        finally
        {
            _isChecking = false;
        }
    }
    
    /// <summary>
    /// Download the latest update.
    /// </summary>
    /// <returns>True if download succeeded.</returns>
    public async Task<bool> DownloadUpdateAsync()
    {
        if (_isDownloading || _latestRelease == null) return false;
        
        // Find the Windows x64 asset
        var asset = _latestRelease.Assets?.FirstOrDefault(a => 
            a.Name?.Contains("win-x64", StringComparison.OrdinalIgnoreCase) == true &&
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        
        if (asset == null || string.IsNullOrEmpty(asset.BrowserDownloadUrl))
        {
            LoggingService.Instance.Warning("No compatible update asset found");
            return false;
        }
        
        _isDownloading = true;
        try
        {
            // Ensure update directory exists
            Directory.CreateDirectory(_updateDirectory);
            
            var downloadPath = Path.Combine(_updateDirectory, asset.Name!);
            
            LoggingService.Instance.Info($"Downloading update: {asset.Name}");
            
            using var response = await _httpClient.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            
            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var downloadedBytes = 0L;
            
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
            
            var buffer = new byte[8192];
            int bytesRead;
            
            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                downloadedBytes += bytesRead;
                
                if (totalBytes > 0)
                {
                    var progress = (int)((downloadedBytes * 100) / totalBytes);
                    DownloadProgress?.Invoke(this, new UpdateDownloadProgressEventArgs(progress, downloadedBytes, totalBytes));
                }
            }
            
            _downloadedUpdatePath = downloadPath;
            LoggingService.Instance.Info($"Update downloaded: {downloadPath}");
            UpdateReady?.Invoke(this, EventArgs.Empty);
            
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Error downloading update: {ex.Message}");
            return false;
        }
        finally
        {
            _isDownloading = false;
        }
    }
    
    /// <summary>
    /// Request confirmation to install update.
    /// </summary>
    public event EventHandler<UpdateInstallEventArgs>? InstallRequested;
    
    /// <summary>
    /// Install the downloaded update and restart the application.
    /// Note: Platform-specific implementation needed for actual installation.
    /// </summary>
    public void InstallUpdate()
    {
        if (!IsUpdateReady || string.IsNullOrEmpty(_downloadedUpdatePath))
        {
            LoggingService.Instance.Warning("No update ready to install");
            return;
        }
        
        // For cross-platform, we fire an event and let the UI handle confirmation
        // The actual installation logic can be triggered by calling PerformInstallation
        InstallRequested?.Invoke(this, new UpdateInstallEventArgs { Version = LatestVersion ?? "unknown" });
    }
    
    /// <summary>
    /// Actually perform the installation after user confirms.
    /// </summary>
    public void PerformInstallation()
    {
        if (!IsUpdateReady || string.IsNullOrEmpty(_downloadedUpdatePath))
        {
            LoggingService.Instance.Warning("No update ready to install");
            return;
        }
        
        try
        {
            var appDirectory = AppContext.BaseDirectory;
            var extractPath = Path.Combine(_updateDirectory, "extract");
            var updateScript = OperatingSystem.IsWindows() 
                ? Path.Combine(_updateDirectory, "update.bat")
                : Path.Combine(_updateDirectory, "update.sh");
            
            // Clean extract directory
            if (Directory.Exists(extractPath))
                Directory.Delete(extractPath, true);
            Directory.CreateDirectory(extractPath);
            
            // Extract the update
            using (var archive = ArchiveFactory.Open(_downloadedUpdatePath))
            {
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    entry.WriteToDirectory(extractPath, new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
                }
            }
            
            if (OperatingSystem.IsWindows())
            {
                // Windows batch script
                var scriptContent = $@"@echo off
echo Waiting for ZScape to close...
:waitloop
tasklist /FI ""IMAGENAME eq ZScape.exe"" 2>NUL | find /I /N ""ZScape.exe"">NUL
if ""%ERRORLEVEL%""==""0"" (
    timeout /t 1 /nobreak >nul
    goto waitloop
)

echo Installing update...
xcopy /E /Y /I ""{extractPath}\*"" ""{appDirectory}""

echo Cleaning up...
rd /s /q ""{_updateDirectory}""

echo Starting ZScape...
start """" ""{Path.Combine(appDirectory, "ZScape.exe")}""
exit
";
                File.WriteAllText(updateScript, scriptContent);
                
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{updateScript}\"",
                    UseShellExecute = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            else
            {
                // Linux/macOS shell script
                var appName = OperatingSystem.IsMacOS() ? "ZScape" : "ZScape";
                var scriptContent = $@"#!/bin/bash
sleep 2
cp -rf ""{extractPath}""/* ""{appDirectory}""
rm -rf ""{_updateDirectory}""
chmod +x ""{Path.Combine(appDirectory, appName)}""
""{Path.Combine(appDirectory, appName)}"" &
";
                File.WriteAllText(updateScript, scriptContent);
                
                // Make script executable
                Process.Start("chmod", $"+x \"{updateScript}\"")?.WaitForExit();
                
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"\"{updateScript}\"",
                    UseShellExecute = true
                });
            }
            
            LoggingService.Instance.Info("Update installation started, exiting application...");
            
            // Signal the application to exit
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Error installing update: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Open the release page in the default browser.
    /// </summary>
    public void OpenReleasePage()
    {
        if (!string.IsNullOrEmpty(_latestRelease?.HtmlUrl))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _latestRelease.HtmlUrl,
                UseShellExecute = true
            });
        }
    }
    
    /// <summary>
    /// Check if auto-update check should run based on settings and timing.
    /// </summary>
    public bool ShouldAutoCheck()
    {
        var settings = SettingsService.Instance.Settings;
        
        if (settings.UpdateBehavior == UpdateBehavior.Disabled)
            return false;
        
        var hoursSinceLastCheck = (DateTime.UtcNow - settings.LastUpdateCheck).TotalHours;
        return hoursSinceLastCheck >= settings.UpdateCheckIntervalHours;
    }
    
    /// <summary>
    /// Perform auto-check and auto-download if enabled.
    /// </summary>
    public async Task PerformAutoUpdateCheckAsync()
    {
        if (!ShouldAutoCheck())
            return;
        
        var settings = SettingsService.Instance.Settings;
        var hasUpdate = await CheckForUpdatesAsync();
        
        if (hasUpdate && settings.UpdateBehavior == UpdateBehavior.CheckAndDownload)
        {
            await DownloadUpdateAsync();
            
            // Auto-restart if enabled and safe to do so
            if (settings.AutoRestartForUpdates && IsUpdateReady)
            {
                await TryAutoRestartAsync();
            }
        }
    }
    
    /// <summary>
    /// Attempt to auto-restart for update installation, waiting for safe conditions.
    /// </summary>
    private async Task TryAutoRestartAsync()
    {
        // Wait up to 30 seconds for the application to become idle
        const int maxWaitSeconds = 30;
        const int checkIntervalMs = 1000;
        
        for (int i = 0; i < maxWaitSeconds; i++)
        {
            if (IsApplicationBusy?.Invoke() != true)
            {
                LoggingService.Instance.Info("Auto-restart: Application is idle, installing update...");
                InstallUpdate();
                return;
            }
            
            await Task.Delay(checkIntervalMs);
        }
        
        // If still busy after waiting, don't auto-restart - let user do it manually
        LoggingService.Instance.Info("Auto-restart: Application is busy, deferring update installation");
    }
    
    /// <summary>
    /// Check if it's safe to restart the application.
    /// </summary>
    public bool IsSafeToRestart()
    {
        return IsApplicationBusy?.Invoke() != true;
    }
    
    /// <summary>
    /// Clean up old update files.
    /// </summary>
    public void CleanupOldUpdates()
    {
        try
        {
            if (Directory.Exists(_updateDirectory))
            {
                Directory.Delete(_updateDirectory, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
    
    private bool IsNewerVersion(string? tagName)
    {
        if (string.IsNullOrEmpty(tagName))
            return false;
        
        var latestVersionStr = tagName.TrimStart('v');
        
        if (Version.TryParse(latestVersionStr, out var latestVersion) && 
            Version.TryParse(_currentVersion, out var currentVersion))
        {
            return latestVersion > currentVersion;
        }
        
        // Fallback to string comparison
        return string.Compare(latestVersionStr, _currentVersion, StringComparison.OrdinalIgnoreCase) > 0;
    }
    
    #region Server State Persistence
    
    private static string ServerStateFilePath => Path.Combine(AppContext.BaseDirectory, "update_server_state.json");
    
    /// <summary>
    /// Callback to get the current server state for persistence.
    /// </summary>
    public Func<UpdateServerState?>? GetServerState { get; set; }
    
    /// <summary>
    /// Callback for saving state with progress dialog.
    /// </summary>
    public Func<IProgress<SaveStateProgress>, CancellationToken, Task<bool>>? SaveStateWithProgress { get; set; }
    
    /// <summary>
    /// Save the current server state before restarting for an update.
    /// Uses async save with progress reporting for large server lists.
    /// </summary>
    public async Task<bool> SaveServerStateAsync(IProgress<SaveStateProgress> progress, CancellationToken cancellationToken)
    {
        try
        {
            var state = GetServerState?.Invoke();
            if (state == null || state.Servers.Count == 0)
            {
                LoggingService.Instance.Verbose("No server state to save");
                return true;
            }
            
            var total = state.Servers.Count;
            var current = 0;
            
            progress.Report(new SaveStateProgress 
            { 
                Current = 0, 
                Total = total, 
                Status = "Preparing server data..." 
            });
            
            // Create snapshots in batches to allow cancellation checks
            const int batchSize = 50;
            for (int i = 0; i < total; i += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                current = Math.Min(i + batchSize, total);
                progress.Report(new SaveStateProgress
                {
                    Current = current,
                    Total = total,
                    Status = "Processing servers..."
                });
                
                // Small delay to allow UI updates
                await Task.Delay(1, cancellationToken);
            }
            
            progress.Report(new SaveStateProgress
            {
                Current = total,
                Total = total,
                Status = "Writing to disk..."
            });
            
            // Serialize with indentation for debugging
            var options = new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true 
            };
            var json = System.Text.Json.JsonSerializer.Serialize(state, options);
            
            await File.WriteAllTextAsync(ServerStateFilePath, json, cancellationToken);
            
            LoggingService.Instance.Info($"Saved server state: {state.Servers.Count} servers ({state.QueriedServerData.Count} with full data)");
            return true;
        }
        catch (OperationCanceledException)
        {
            LoggingService.Instance.Info("Server state save cancelled");
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warning($"Failed to save server state: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Load saved server state from a previous update restart.
    /// </summary>
    /// <returns>The saved state, or null if none exists.</returns>
    public UpdateServerState? LoadServerState()
    {
        try
        {
            if (!File.Exists(ServerStateFilePath))
                return null;
            
            var json = File.ReadAllText(ServerStateFilePath);
            var state = System.Text.Json.JsonSerializer.Deserialize<UpdateServerState>(json);
            
            // Delete the state file after loading (one-time use)
            File.Delete(ServerStateFilePath);
            
            if (state != null)
            {
                LoggingService.Instance.Info($"Restored server state: {state.Servers.Count} servers ({state.QueriedServerData.Count} with full data)");
            }
            
            return state;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warning($"Failed to load server state: {ex.Message}");
            return null;
        }
    }
    
    #endregion
}

/// <summary>
/// Represents the server list state saved before an update restart.
/// </summary>
public class UpdateServerState
{
    /// <summary>All server endpoints (address:port format).</summary>
    public List<string> Servers { get; set; } = [];
    
    /// <summary>Full data for servers that have been queried.</summary>
    public List<ServerSnapshot> QueriedServerData { get; set; } = [];
    
    /// <summary>Timestamp when the state was saved.</summary>
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Serializable snapshot of server information for update persistence.
/// </summary>
public class ServerSnapshot
{
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Map { get; set; } = string.Empty;
    public int CurrentPlayers { get; set; }
    public int MaxPlayers { get; set; }
    public int MaxClients { get; set; }
    public int Ping { get; set; } = -1;
    public int GameModeCode { get; set; }
    public bool IsOnline { get; set; } = true;
    public string IWAD { get; set; } = string.Empty;
    public List<PWadSnapshot> PWADs { get; set; } = [];
    public string GameVersion { get; set; } = string.Empty;
    public bool IsPassworded { get; set; }
    public bool RequiresJoinPassword { get; set; }
    public bool IsTestingServer { get; set; }
    public string TestingArchive { get; set; } = string.Empty;
    public int Skill { get; set; }
    public int BotSkill { get; set; }
    public List<PlayerSnapshot> Players { get; set; } = [];
    public int NumTeams { get; set; }
    public int FragLimit { get; set; }
    public int TimeLimit { get; set; }
    public int TimeLeft { get; set; }
    public int PointLimit { get; set; }
    public int DuelLimit { get; set; }
    public int WinLimit { get; set; }
    public float TeamDamage { get; set; }
    public string Country { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsSecure { get; set; }
    public bool Instagib { get; set; }
    public bool Buckshot { get; set; }
    
    public string EndPointKey => $"{Address}:{Port}";
}

public class PWadSnapshot
{
    public string Name { get; set; } = string.Empty;
    public bool IsOptional { get; set; }
    public string? Hash { get; set; }
}

public class PlayerSnapshot
{
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
    public int Ping { get; set; }
    public int Team { get; set; }
    public bool IsSpectator { get; set; }
    public bool IsBot { get; set; }
}

#region Event Args

public class UpdateAvailableEventArgs : EventArgs
{
    public string NewVersion { get; }
    public string CurrentVersion { get; }
    public string ReleaseNotes { get; }
    public string ReleaseUrl { get; }
    
    public UpdateAvailableEventArgs(string newVersion, string currentVersion, string releaseNotes, string releaseUrl)
    {
        NewVersion = newVersion;
        CurrentVersion = currentVersion;
        ReleaseNotes = releaseNotes;
        ReleaseUrl = releaseUrl;
    }
}

public class UpdateDownloadProgressEventArgs : EventArgs
{
    public int ProgressPercent { get; }
    public long DownloadedBytes { get; }
    public long TotalBytes { get; }
    
    public UpdateDownloadProgressEventArgs(int progressPercent, long downloadedBytes, long totalBytes)
    {
        ProgressPercent = progressPercent;
        DownloadedBytes = downloadedBytes;
        TotalBytes = totalBytes;
    }
}

#endregion

#region GitHub API Models

public class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }
    
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("body")]
    public string? Body { get; set; }
    
    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
    
    [JsonPropertyName("published_at")]
    public DateTime? PublishedAt { get; set; }
    
    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

public class GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    
    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }
    
    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>
/// Event arguments for update installation request.
/// </summary>
public class UpdateInstallEventArgs : EventArgs
{
    public string Version { get; init; } = "";
}

#endregion
