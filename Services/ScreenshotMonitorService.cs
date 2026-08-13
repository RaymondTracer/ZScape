using ZScape.Utilities;

namespace ZScape.Services;

/// <summary>
/// Monitors testing version directories for screenshots and consolidates them
/// into a single folder for easy access.
/// </summary>
public class ScreenshotMonitorService : IDisposable
{
    private const int ScreenshotMoveAttempts = 40;
    private static readonly TimeSpan ScreenshotMoveRetryDelay =
        TimeSpan.FromMilliseconds(250);

    private static ScreenshotMonitorService? _instance;
    public static ScreenshotMonitorService Instance => _instance ??= new ScreenshotMonitorService();
    
    private readonly LoggingService _logger = LoggingService.Instance;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _lock = new();
    private readonly object _pendingMoveLock = new();
    private readonly HashSet<string> _pendingScreenshotMoves =
        new(StringComparer.OrdinalIgnoreCase);
    private int _screenshotsMovedCount;
    private bool _isMonitoring;
    private bool _disposed;
    
    /// <summary>
    /// Event fired when a screenshot is successfully moved.
    /// </summary>
    public event EventHandler<ScreenshotMovedEventArgs>? ScreenshotMoved;
    
    /// <summary>
    /// Gets whether monitoring is currently active.
    /// </summary>
    public bool IsMonitoring => _isMonitoring;
    
    /// <summary>
    /// Gets the current destination path for screenshots.
    /// </summary>
    public string? DestinationPath { get; private set; }
    
    /// <summary>
    /// Gets the number of screenshots moved in this session.
    /// </summary>
    public int ScreenshotsMovedCount => Volatile.Read(ref _screenshotsMovedCount);
    
    private ScreenshotMonitorService() { }
    
    /// <summary>
    /// Starts monitoring Zandronum directories for screenshots.
    /// Monitors both the stable release folder and testing version folders.
    /// </summary>
    public void StartMonitoring()
    {
        var settings = SettingsService.Instance.Settings;
        
        if (!settings.EnableScreenshotMonitoring)
        {
            _logger.Verbose("Screenshot monitoring is disabled");
            return;
        }
        
        DestinationPath = GetDestinationPath();
        if (string.IsNullOrEmpty(DestinationPath))
        {
            _logger.Warning("Cannot start screenshot monitoring: destination path not configured");
            return;
        }
        
        // Ensure destination directory exists
        try
        {
            Directory.CreateDirectory(DestinationPath);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to create screenshot destination folder: {ex.Message}");
            return;
        }
        
        lock (_lock)
        {
            StopMonitoringInternal();
            
            var watchedDirs = new List<string>();
            
            try
            {
                // Watch the stable release folder
                var stableDir = GetStableZandronumDir();
                if (!string.IsNullOrEmpty(stableDir) && Directory.Exists(stableDir) && 
                    !stableDir.Equals(DestinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    CreateWatcherForDirectory(stableDir);
                    watchedDirs.Add("stable");
                }
                
                // Watch testing version directories
                var testingRoot = GetTestingRootPath();
                if (!string.IsNullOrEmpty(testingRoot) && Directory.Exists(testingRoot))
                {
                    foreach (var versionDir in Directory.GetDirectories(testingRoot))
                    {
                        CreateWatcherForDirectory(versionDir);
                    }
                    
                    // Also watch the root testing folder for new version directories
                    var rootWatcher = new FileSystemWatcher(testingRoot)
                    {
                        NotifyFilter = NotifyFilters.DirectoryName,
                        EnableRaisingEvents = true
                    };
                    rootWatcher.Created += OnVersionDirectoryCreated;
                    _watchers.Add(rootWatcher);
                    
                    watchedDirs.Add($"{Directory.GetDirectories(testingRoot).Length} testing versions");
                }
                
                _isMonitoring = true;
                _logger.Info($"Screenshot monitoring started. Watching: {string.Join(", ", watchedDirs)}. Destination: {DestinationPath}");
                
                // Queue existing screenshots through the same lock-aware move
                // path used by live FileSystemWatcher events.
                MoveExistingScreenshots();
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to start screenshot monitoring: {ex.Message}");
                StopMonitoringInternal();
            }
        }
    }
    
    /// <summary>
    /// Stops all screenshot monitoring.
    /// </summary>
    public void StopMonitoring()
    {
        lock (_lock)
        {
            StopMonitoringInternal();
        }
    }
    
    private void StopMonitoringInternal()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        _isMonitoring = false;
    }
    
    private void CreateWatcherForDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return;
            
        var watcher = new FileSystemWatcher(directory)
        {
            Filter = "Screenshot_*.png",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        
        watcher.Created += OnScreenshotCreated;
        watcher.Renamed += OnScreenshotRenamed;
        
        _watchers.Add(watcher);
        _logger.Verbose($"Watching for screenshots in: {directory}");
    }
    
    private void OnVersionDirectoryCreated(object sender, FileSystemEventArgs e)
    {
        // New testing version directory created, start watching it
        if (Directory.Exists(e.FullPath))
        {
            lock (_lock)
            {
                CreateWatcherForDirectory(e.FullPath);
            }
            _logger.Verbose($"Started watching new version directory: {e.FullPath}");
        }
    }
    
    private void OnScreenshotCreated(object sender, FileSystemEventArgs e)
    {
        QueueScreenshotMove(e.FullPath);
    }
    
    private void OnScreenshotRenamed(object sender, RenamedEventArgs e)
    {
        // Handle case where screenshot is renamed to match our pattern
        if (Path.GetFileName(e.FullPath).StartsWith("Screenshot_", StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(e.FullPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            QueueScreenshotMove(e.FullPath);
        }
    }

    /// <summary>
    /// Starts one lock-aware move per source path. FileSystemWatcher commonly
    /// raises several events while Zandronum is still writing the PNG, so
    /// duplicate events deliberately share a single retry loop.
    /// </summary>
    private void QueueScreenshotMove(string sourcePath)
    {
        if (string.IsNullOrEmpty(DestinationPath))
            return;

        lock (_pendingMoveLock)
        {
            if (!_pendingScreenshotMoves.Add(sourcePath))
            {
                return;
            }
        }

        _ = MoveScreenshotWhenReadyAsync(sourcePath);
    }

    /// <summary>
    /// Waits for the screenshot producer to release its file handle, then
    /// moves the completed file. Individual lock failures are deliberately
    /// silent; only a file that remains unavailable for the full retry window
    /// is reported.
    /// </summary>
    private async Task MoveScreenshotWhenReadyAsync(string sourcePath)
    {
        try
        {
            for (var attempt = 1; attempt <= ScreenshotMoveAttempts; attempt++)
            {
                if (string.IsNullOrEmpty(DestinationPath))
                {
                    return;
                }

                if (File.Exists(sourcePath)
                    && IsFileReadyToMove(sourcePath)
                    && TryMoveScreenshot(sourcePath, out var destinationPath))
                {
                    var fileName = Path.GetFileName(sourcePath);
                    var sourceVersion =
                        Path.GetFileName(Path.GetDirectoryName(sourcePath))
                        ?? "unknown";
                    Interlocked.Increment(ref _screenshotsMovedCount);
                    _logger.Info(
                        $"Moved screenshot from {sourceVersion}: {fileName}");
                    ScreenshotMoved?.Invoke(
                        this,
                        new ScreenshotMovedEventArgs(
                            sourcePath,
                            destinationPath!,
                            sourceVersion));
                    return;
                }

                if (attempt < ScreenshotMoveAttempts)
                {
                    await Task.Delay(ScreenshotMoveRetryDelay)
                        .ConfigureAwait(false);
                }
            }

            if (File.Exists(sourcePath))
            {
                _logger.Warning(
                    $"Screenshot remained in use for "
                    + $"{ScreenshotMoveAttempts * ScreenshotMoveRetryDelay.TotalMilliseconds / 1000:F0} seconds "
                    + $"and was not moved: {sourcePath}");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to move screenshot {sourcePath}: {ex.Message}");
        }
        finally
        {
            lock (_pendingMoveLock)
            {
                _pendingScreenshotMoves.Remove(sourcePath);
            }
        }
    }

    /// <summary>
    /// Verifies that the writer has released the screenshot before attempting
    /// the move. The exclusive handle is immediately disposed; File.Move is
    /// still retried if another process races in after this check.
    /// </summary>
    private static bool IsFileReadyToMove(string sourcePath)
    {
        try
        {
            using var file = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            _ = file.Length;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryMoveScreenshot(string sourcePath, out string? destinationPath)
    {
        destinationPath = null;
        if (string.IsNullOrEmpty(DestinationPath))
        {
            return false;
        }

        var fileName = Path.GetFileName(sourcePath);
        var targetPath = Path.Combine(DestinationPath, fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 1;
        while (File.Exists(targetPath))
        {
            targetPath = Path.Combine(
                DestinationPath,
                $"{baseName}_{counter++}{extension}");
        }

        try
        {
            File.Move(sourcePath, targetPath);
            destinationPath = targetPath;
            return true;
        }
        catch (IOException)
        {
            // Source is still locked or a competing event won the move. Both
            // are transient from the monitor's point of view.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Queues screenshots already present in the stable and testing folders.
    /// Returns the number of files queued for lock-aware consolidation.
    /// </summary>
    public int MoveExistingScreenshots()
    {
        var destPath = DestinationPath ?? GetDestinationPath();
        if (string.IsNullOrEmpty(destPath))
            return 0;
            
        Directory.CreateDirectory(destPath);

        var queued = 0;

        void QueueScreenshotsFromDirectory(string directory)
        {
            if (!Directory.Exists(directory)) return;

            // Don't move from the destination folder itself
            if (Path.GetFullPath(directory).Equals(
                    Path.GetFullPath(destPath),
                    StringComparison.OrdinalIgnoreCase))
                return;

            var screenshots = Directory.GetFiles(
                directory,
                "Screenshot_*.png",
                SearchOption.TopDirectoryOnly);
            foreach (var screenshot in screenshots)
            {
                QueueScreenshotMove(screenshot);
                queued++;
            }
        }

        try
        {
            // Move from stable Zandronum folder
            var stableDir = GetStableZandronumDir();
            if (!string.IsNullOrEmpty(stableDir))
            {
                QueueScreenshotsFromDirectory(stableDir);
            }

            // Move from testing version subdirectories
            var testingRoot = GetTestingRootPath();
            if (!string.IsNullOrEmpty(testingRoot) && Directory.Exists(testingRoot))
            {
                foreach (var versionDir in Directory.GetDirectories(testingRoot))
                {
                    QueueScreenshotsFromDirectory(versionDir);
                }
            }

            if (queued > 0)
            {
                _logger.Verbose(
                    $"Queued {queued} existing screenshot(s) for consolidation");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error scanning for existing screenshots: {ex.Message}");
        }

        return queued;
    }
    
    /// <summary>
    /// Gets the configured destination path for screenshots.
    /// </summary>
    public string? GetDestinationPath() => PathResolver.GetScreenshotsPath();
    
    private static string? GetTestingRootPath() => PathResolver.GetTestingVersionsPath();
    
    /// <summary>
    /// Gets the stable Zandronum directory (where the main exe lives).
    /// </summary>
    private static string? GetStableZandronumDir() => PathResolver.GetZandronumDirectory();
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        StopMonitoring();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Event arguments for screenshot moved events.
/// </summary>
public class ScreenshotMovedEventArgs(string sourcePath, string destinationPath, string sourceVersion) : EventArgs
{
    public string SourcePath { get; } = sourcePath;
    public string DestinationPath { get; } = destinationPath;
    public string SourceVersion { get; } = sourceVersion;
}
