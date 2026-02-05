using ZScape.Utilities;

namespace ZScape.Services;

/// <summary>
/// Monitors testing version directories for screenshots and consolidates them
/// into a single folder for easy access.
/// </summary>
public class ScreenshotMonitorService : IDisposable
{
    private static ScreenshotMonitorService? _instance;
    public static ScreenshotMonitorService Instance => _instance ??= new ScreenshotMonitorService();
    
    private readonly LoggingService _logger = LoggingService.Instance;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _lock = new();
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
    public int ScreenshotsMovedCount { get; private set; }
    
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
                
                // Move any existing screenshots on startup
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
        MoveScreenshot(e.FullPath);
    }
    
    private void OnScreenshotRenamed(object sender, RenamedEventArgs e)
    {
        // Handle case where screenshot is renamed to match our pattern
        if (Path.GetFileName(e.FullPath).StartsWith("Screenshot_", StringComparison.OrdinalIgnoreCase) &&
            Path.GetExtension(e.FullPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            MoveScreenshot(e.FullPath);
        }
    }
    
    private void MoveScreenshot(string sourcePath)
    {
        if (string.IsNullOrEmpty(DestinationPath))
            return;
            
        try
        {
            // Wait briefly for file to be fully written
            Thread.Sleep(100);
            
            if (!File.Exists(sourcePath))
                return;
                
            var fileName = Path.GetFileName(sourcePath);
            var destPath = Path.Combine(DestinationPath, fileName);
            
            // Handle name conflicts by appending a number
            if (File.Exists(destPath))
            {
                var baseName = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                var counter = 1;
                while (File.Exists(destPath))
                {
                    destPath = Path.Combine(DestinationPath, $"{baseName}_{counter++}{ext}");
                }
            }
            
            File.Move(sourcePath, destPath);
            ScreenshotsMovedCount++;
            
            var sourceVersion = Path.GetFileName(Path.GetDirectoryName(sourcePath)) ?? "unknown";
            _logger.Info($"Moved screenshot from {sourceVersion}: {fileName}");
            
            ScreenshotMoved?.Invoke(this, new ScreenshotMovedEventArgs(sourcePath, destPath, sourceVersion));
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to move screenshot {sourcePath}: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Moves any existing screenshots from testing version directories and stable folder.
    /// </summary>
    public int MoveExistingScreenshots()
    {
        var destPath = DestinationPath ?? GetDestinationPath();
        if (string.IsNullOrEmpty(destPath))
            return 0;
            
        Directory.CreateDirectory(destPath);
        
        int moved = 0;
        
        // Helper to move screenshots from a directory
        void MoveScreenshotsFromDir(string dir, string source)
        {
            if (!Directory.Exists(dir)) return;
            
            // Don't move from the destination folder itself
            if (Path.GetFullPath(dir).Equals(Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
                return;
                
            var screenshots = Directory.GetFiles(dir, "Screenshot_*.png", SearchOption.TopDirectoryOnly);
            foreach (var screenshot in screenshots)
            {
                try
                {
                    var fileName = Path.GetFileName(screenshot);
                    var dest = Path.Combine(destPath, fileName);
                    
                    // Handle name conflicts
                    if (File.Exists(dest))
                    {
                        var baseName = Path.GetFileNameWithoutExtension(fileName);
                        var ext = Path.GetExtension(fileName);
                        var counter = 1;
                        while (File.Exists(dest))
                        {
                            dest = Path.Combine(destPath, $"{baseName}_{counter++}{ext}");
                        }
                    }
                    
                    File.Move(screenshot, dest);
                    moved++;
                    _logger.Info($"Moved screenshot from {source}: {fileName}");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to move screenshot {screenshot}: {ex.Message}");
                }
            }
        }
        
        try
        {
            // Move from stable Zandronum folder
            var stableDir = GetStableZandronumDir();
            if (!string.IsNullOrEmpty(stableDir))
            {
                MoveScreenshotsFromDir(stableDir, "stable");
            }
            
            // Move from testing version subdirectories
            var testingRoot = GetTestingRootPath();
            if (!string.IsNullOrEmpty(testingRoot) && Directory.Exists(testingRoot))
            {
                foreach (var versionDir in Directory.GetDirectories(testingRoot))
                {
                    var versionName = Path.GetFileName(versionDir);
                    MoveScreenshotsFromDir(versionDir, versionName);
                }
            }
            
            if (moved > 0)
            {
                _logger.Info($"Moved {moved} existing screenshots to {destPath}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error scanning for existing screenshots: {ex.Message}");
        }
        
        return moved;
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
