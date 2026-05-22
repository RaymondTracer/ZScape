using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using SharpCompress.Archives;
using SharpCompress.Common;
using ZScape.Models;
using ZScape.Utilities;

namespace ZScape.Services;

/// <summary>
/// Handles launching Zandronum to connect to servers, including automatic
/// downloading of testing versions.
/// </summary>
public class GameLauncher
{
    private static readonly Lazy<GameLauncher> _instance = new(() => new GameLauncher());
    private static readonly Uri TestingArchiveBaseUri = new("https://zandronum.com/", UriKind.Absolute);
    public static GameLauncher Instance => _instance.Value;

    private readonly DomainThreadConfig _domainConfig = DomainThreadConfig.Instance;
    private readonly HttpClient _httpClient = new();

    public event EventHandler<string>? LaunchError;
    public event EventHandler<string>? LaunchSuccess;
    public event EventHandler<TestingBuildDownloadProgress>? DownloadProgress;

    private GameLauncher() 
    {
        _httpClient.Timeout = TimeSpan.FromMinutes(AppConstants.Timeouts.HttpLongOperationTimeoutMinutes);
    }

    /// <summary>
    /// Launches Zandronum to connect to the specified server.
    /// </summary>
    /// <param name="server">The server to connect to.</param>
    /// <param name="connectPassword">Optional connect password.</param>
    /// <param name="joinPassword">Optional join/in-game password.</param>
    /// <param name="excludedOptionalPwads">Optional PWAD names to exclude from the launch command line.</param>
    /// <returns>True if launch succeeded, false otherwise.</returns>
    public bool LaunchGame(
        ServerInfo server,
        string? connectPassword = null,
        string? joinPassword = null,
        ISet<string>? excludedOptionalPwads = null)
    {
        var settings = SettingsService.Instance.Settings;
        
        // Select correct executable based on testing status
        var exePath = GetExecutablePath(server);
            
        if (string.IsNullOrEmpty(exePath))
        {
            var error = server.IsTestingServer 
                ? "Zandronum Testing executable path not configured. Please set it in Settings."
                : "Zandronum executable path not configured. Please set it in Settings.";
            LaunchError?.Invoke(this, error);
            LoggingService.Instance.Warning(error);
            return false;
        }
        
        if (!File.Exists(exePath))
        {
            var error = $"Zandronum executable not found: {exePath}";
            LaunchError?.Invoke(this, error);
            LoggingService.Instance.Warning(error);
            return false;
        }

        var args = BuildCommandLine(server, connectPassword, joinPassword, excludedOptionalPwads);
        
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath)
            };
            
            LoggingService.Instance.Verbose($"Launching: {exePath} {args}");
            
            Process.Start(startInfo);
            
            // Record connection in history
            SettingsService.Instance.RecordConnection(
                server.Address, 
                server.Port, 
                server.Name,
                server.IWAD,
                server.GameMode?.Name);
            
            var msg = $"Launched Zandronum to connect to {server.Name}";
            LaunchSuccess?.Invoke(this, msg);
            LoggingService.Instance.Info(msg);
            return true;
        }
        catch (Exception ex)
        {
            var error = $"Failed to launch Zandronum: {ex.Message}";
            LaunchError?.Invoke(this, error);
            LoggingService.Instance.Error(error);
            return false;
        }
    }

    /// <summary>
    /// Validates if all required WADs are available for the server.
    /// Checks exe folder first, then WAD manager paths.
    /// Optional PWADs do not block joins and are skipped here.
    /// </summary>
    /// <param name="server">The server to check.</param>
    /// <returns>A tuple with (allFound, missingWads) where each missing WAD includes name and expected hash.</returns>
    public (bool AllFound, List<(string Name, string? Hash)> MissingWads) CheckRequiredWads(ServerInfo server)
    {
        return CheckPwads(server, pwad => !pwad.IsOptional);
    }

    /// <summary>
    /// Finds optional PWADs that are not currently available.
    /// These do not block joins, but can be offered for download.
    /// </summary>
    public (bool AllFound, List<(string Name, string? Hash)> MissingWads) CheckOptionalWads(ServerInfo server)
    {
        return CheckPwads(server, pwad => pwad.IsOptional);
    }

    private (bool AllFound, List<(string Name, string? Hash)> MissingWads) CheckPwads(
        ServerInfo server,
        Func<PWadInfo, bool> predicate)
    {
        var missing = new List<(string Name, string? Hash)>();
        var exeFolder = GetExecutableFolder(server);
        
        // Check IWAD (IWADs don't have server-provided hashes)
        if (!string.IsNullOrEmpty(server.IWAD))
        {
            var iwadPath = FindWadWithExeFolder(server.IWAD, exeFolder);
            if (string.IsNullOrEmpty(iwadPath))
            {
                missing.Add((server.IWAD, null));
            }
        }
        
        foreach (var pwad in server.PWADs.Where(predicate))
        {
            var pwadPath = FindWadWithExeFolder(pwad.Name, exeFolder);
            if (string.IsNullOrEmpty(pwadPath))
            {
                missing.Add((pwad.Name, pwad.Hash));
            }
        }
        
        return (missing.Count == 0, missing);
    }

    /// <summary>
    /// Resolves missing WADs by activating locally archived copies that match the server hash.
    /// Returns only the WADs that still need to be downloaded.
    /// </summary>
    public (List<WadInfo> NeedsDownload, bool CacheChanged) ResolveMissingWadsByHash(List<(string Name, string? Hash)> missingWads)
    {
        var needsDownload = new List<WadInfo>();
        var wadManager = WadManager.Instance;
        var cacheChanged = false;

        foreach (var (name, hash) in missingWads)
        {
            if (string.IsNullOrEmpty(hash))
            {
                needsDownload.Add(new WadInfo(name, hash));
                continue;
            }

            var baseName = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            var matchingPath = wadManager.FindWadByHash(hash, baseName, extension);
            if (string.IsNullOrEmpty(matchingPath))
            {
                needsDownload.Add(new WadInfo(name, hash));
                continue;
            }

            var activatedPath = wadManager.ActivateArchivedWad(matchingPath, name);
            if (activatedPath != null)
            {
                cacheChanged = true;
                LoggingService.Instance.Info(
                    $"Activated local WAD for {name}: {Path.GetFileName(matchingPath)} -> {Path.GetFileName(activatedPath)}");
                continue;
            }

            LoggingService.Instance.Warning($"Found matching local WAD for {name} but failed to activate it: {matchingPath}");
            needsDownload.Add(new WadInfo(name, hash));
        }

        return (needsDownload, cacheChanged);
    }

    /// <summary>
    /// Represents a WAD that has a hash mismatch.
    /// </summary>
    public class WadHashMismatch
    {
        public string WadName { get; init; } = string.Empty;
        public bool IsOptional { get; init; }
        public string LocalPath { get; init; } = string.Empty;
        public string LocalHash { get; init; } = string.Empty;
        public string ExpectedHash { get; init; } = string.Empty;
        public string? MatchingVersionPath { get; set; }
        public bool NeedsDownload => string.IsNullOrEmpty(MatchingVersionPath);
    }

    /// <summary>
    /// Progress information for hash verification.
    /// </summary>
    public class HashVerificationProgress
    {
        public string CurrentFile { get; init; } = string.Empty;
        public int CurrentIndex { get; init; }
        public int TotalFiles { get; init; }
        public string Status { get; init; } = string.Empty;
        public long FileSize { get; init; }
        public long BytesProcessed { get; init; }
        public int FilePercentComplete => FileSize > 0 ? (int)((BytesProcessed * 100) / FileSize) : 0;
        public int OverallPercentComplete => TotalFiles > 0 ? (CurrentIndex * 100) / TotalFiles : 0;
        
        /// <summary>
        /// Per-file progress info for concurrent verification.
        /// Key = filename, Value = (bytesProcessed, totalBytes)
        /// </summary>
        public Dictionary<string, (long BytesProcessed, long TotalBytes)>? FileProgress { get; init; }
    }

    /// <summary>
    /// Progress information for testing build downloads.
    /// </summary>
    public class TestingBuildDownloadProgress
    {
        public string Status { get; init; } = string.Empty;
        public int ProgressPercent { get; init; }
        public long DownloadedBytes { get; init; }
        public long TotalBytes { get; init; }
        public double BytesPerSecond { get; init; }
        public TimeSpan? EstimatedTimeRemaining { get; init; }
        public int ThreadCount { get; init; } = 1;
    }

    /// <summary>
    /// Verifies that all local PWAD files with advertised hashes match the server's expected hashes.
    /// </summary>
    /// <param name="server">The server to verify WADs for.</param>
    /// <param name="progress">Progress callback with per-file byte-level progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of WADs with hash mismatches.</returns>
    public async Task<List<WadHashMismatch>> VerifyWadHashesAsync(
        ServerInfo server, 
        IProgress<HashVerificationProgress>? progress,
        CancellationToken cancellationToken)
    {
        return await VerifyWadHashesCoreAsync(server, progress, cancellationToken, _ => true);
    }

    /// <summary>
    /// Verifies local optional PWADs that advertise hashes.
    /// Unresolved mismatches can be excluded from launch or offered for download.
    /// </summary>
    public async Task<List<WadHashMismatch>> VerifyOptionalWadHashesAsync(
        ServerInfo server,
        IProgress<HashVerificationProgress>? progress,
        CancellationToken cancellationToken)
    {
        return await VerifyWadHashesCoreAsync(server, progress, cancellationToken, pwad => pwad.IsOptional);
    }

    private async Task<List<WadHashMismatch>> VerifyWadHashesCoreAsync(
        ServerInfo server,
        IProgress<HashVerificationProgress>? progress,
        CancellationToken cancellationToken,
        Func<PWadInfo, bool> predicate)
    {
        var mismatches = new System.Collections.Concurrent.ConcurrentBag<WadHashMismatch>();
        var exeFolder = GetExecutableFolder(server);
        var wadManager = WadManager.Instance;
        var settings = SettingsService.Instance.Settings;
        
        // Verify only the PWADs requested by the caller that advertise hashes.
        var pwadsWithHashes = server.PWADs
            .Where(p => predicate(p) && !string.IsNullOrEmpty(p.Hash))
            .ToList();
        var totalFiles = pwadsWithHashes.Count;
        
        if (totalFiles == 0)
        {
            progress?.Report(new HashVerificationProgress
            {
                CurrentFile = string.Empty,
                CurrentIndex = 0,
                TotalFiles = 0,
                Status = "No hashes to verify.",
                FileSize = 0
            });
            return [];
        }
        
        // Track per-file progress for concurrent display
        var fileProgress = new System.Collections.Concurrent.ConcurrentDictionary<string, (long BytesProcessed, long TotalBytes)>();
        var completedCount = 0;
        
        // Prepare verification tasks
        var verificationItems = new List<(PWadInfo Pwad, string LocalPath, long FileSize)>();
        foreach (var pwad in pwadsWithHashes)
        {
            var localPath = FindWadWithExeFolder(pwad.Name, exeFolder);
            if (string.IsNullOrEmpty(localPath))
                continue;
            
            var fileSize = 0L;
            try { fileSize = new FileInfo(localPath).Length; } catch { }
            
            verificationItems.Add((pwad, localPath, fileSize));
            fileProgress[pwad.Name] = (0, fileSize);
        }

        if (verificationItems.Count == 0)
        {
            progress?.Report(new HashVerificationProgress
            {
                CurrentFile = string.Empty,
                CurrentIndex = 0,
                TotalFiles = 0,
                Status = "No local WADs with server hashes are available to verify.",
                FileSize = 0
            });
            return [];
        }
        
        // Report initial state
        progress?.Report(new HashVerificationProgress
        {
            CurrentFile = string.Empty,
            CurrentIndex = 0,
            TotalFiles = verificationItems.Count,
            Status = $"Verifying {verificationItems.Count} file(s)...",
            FileSize = 0,
            FileProgress = new Dictionary<string, (long, long)>(fileProgress)
        });
        
        // Determine concurrency level
        var maxConcurrency = settings.HashVerificationConcurrency;
        if (maxConcurrency <= 0)
            maxConcurrency = verificationItems.Count; // Unlimited = all at once
        
        var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        
        // Throttle progress updates to prevent UI thread overload
        var lastProgressUpdate = DateTime.MinValue;
        var progressLock = new object();
        
        void ThrottledProgress(HashVerificationProgress p)
        {
            lock (progressLock)
            {
                var now = DateTime.UtcNow;
                if ((now - lastProgressUpdate).TotalMilliseconds < AppConstants.UiIntervals.ProgressReportThrottleMs)
                    return;
                lastProgressUpdate = now;
            }
            progress?.Report(p);
        }
        
        // Create verification tasks - run entirely on thread pool
        var tasks = verificationItems.Select(item => Task.Run(async () =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // Progress callback for this file - uses throttled updates
                Action<long> fileProgressCallback = bytesProcessed =>
                {
                    fileProgress[item.Pwad.Name] = (bytesProcessed, item.FileSize);
                    
                    // Report aggregate progress (throttled)
                    ThrottledProgress(new HashVerificationProgress
                    {
                        CurrentFile = item.Pwad.Name,
                        CurrentIndex = completedCount,
                        TotalFiles = verificationItems.Count,
                        Status = $"Hashing {item.Pwad.Name}... ({FormatUtils.FormatBytes(bytesProcessed)} / {FormatUtils.FormatBytes(item.FileSize)})",
                        FileSize = item.FileSize,
                        BytesProcessed = bytesProcessed,
                        FileProgress = new Dictionary<string, (long, long)>(fileProgress)
                    });
                };
                
                // Compute hash with progress
                var localHash = await ComputeFileHashWithProgressAsync(item.LocalPath, fileProgressCallback, cancellationToken);
                if (string.IsNullOrEmpty(localHash))
                    return;
                
                // Mark file as complete in progress tracking
                fileProgress[item.Pwad.Name] = (item.FileSize, item.FileSize);
                
                // Compare hashes
                if (!string.Equals(localHash, item.Pwad.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    var baseName = Path.GetFileNameWithoutExtension(item.Pwad.Name);
                    var extension = Path.GetExtension(item.Pwad.Name);
                    var matchingPath = wadManager.FindWadByHash(item.Pwad.Hash!, baseName, extension);
                    var expectedHash = item.Pwad.Hash!;
                    
                    mismatches.Add(new WadHashMismatch
                    {
                        WadName = item.Pwad.Name,
                        IsOptional = item.Pwad.IsOptional,
                        LocalPath = item.LocalPath,
                        LocalHash = localHash,
                        ExpectedHash = expectedHash,
                        MatchingVersionPath = matchingPath
                    });
                    
                    var localTrunc = localHash[..Math.Min(12, localHash.Length)];
                    var expectedTrunc = expectedHash[..Math.Min(12, expectedHash.Length)];
                    LoggingService.Instance.Warning(
                        $"Hash mismatch for {item.Pwad.Name}: local={localTrunc}..., expected={expectedTrunc}..." +
                        (matchingPath != null ? " (found matching version)" : " (needs download)"));
                }
                
                Interlocked.Increment(ref completedCount);
                
                // Report file completed
                progress?.Report(new HashVerificationProgress
                {
                    CurrentFile = item.Pwad.Name,
                    CurrentIndex = completedCount,
                    TotalFiles = verificationItems.Count,
                    Status = $"Verified: {item.Pwad.Name}",
                    FileSize = item.FileSize,
                    BytesProcessed = item.FileSize,
                    FileProgress = new Dictionary<string, (long, long)>(fileProgress)
                });
            }
            finally
            {
                semaphore.Release();
            }
        }, cancellationToken)).ToList();
        
        // Wait for all tasks
        await Task.WhenAll(tasks);
        
        var result = mismatches.ToList();
        
        // Final progress report
        progress?.Report(new HashVerificationProgress
        {
            CurrentFile = string.Empty,
            CurrentIndex = verificationItems.Count,
            TotalFiles = verificationItems.Count,
            Status = result.Count > 0 
                ? $"Verification complete. {result.Count} mismatch(es) found." 
                : "All WAD hashes verified successfully.",
            FileSize = 0,
            FileProgress = new Dictionary<string, (long, long)>(fileProgress)
        });
        
        return result;
    }

    /// <summary>
    /// Computes MD5 hash of a file with byte-level progress reporting.
    /// </summary>
    private static async Task<string?> ComputeFileHashWithProgressAsync(
        string filePath, 
        Action<long> progress, 
        CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 
                bufferSize: 1024 * 1024, useAsync: true); // 1MB buffer for large files
            using var md5 = System.Security.Cryptography.MD5.Create();
            
            var buffer = new byte[1024 * 1024]; // 1MB read chunks
            long totalBytesRead = 0;
            int bytesRead;
            
            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                md5.TransformBlock(buffer, 0, bytesRead, null, 0);
                totalBytesRead += bytesRead;
                progress(totalBytesRead);
            }
            
            md5.TransformFinalBlock([], 0, 0);
            return BitConverter.ToString(md5.Hash!).Replace("-", "").ToLowerInvariant();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Failed to compute hash for {filePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Resolves hash mismatches by swapping WAD versions or preparing for download.
    /// </summary>
    /// <param name="mismatches">List of WAD hash mismatches to resolve.</param>
    /// <returns>List of WADs that need to be downloaded (no local version with matching hash).</returns>
    public List<WadInfo> ResolveHashMismatches(List<WadHashMismatch> mismatches)
    {
        var needsDownload = new List<WadInfo>();
        var wadManager = WadManager.Instance;
        
        foreach (var mismatch in mismatches)
        {
            if (!string.IsNullOrEmpty(mismatch.MatchingVersionPath))
            {
                // We have a local version with matching hash - swap them
                LoggingService.Instance.Info($"Swapping WAD versions for {mismatch.WadName}");
                
                // Archive the current (mismatched) file
                var archived = wadManager.ArchiveWadWithHash(mismatch.LocalPath);
                if (archived == null)
                {
                    LoggingService.Instance.Error($"Failed to archive {mismatch.WadName}");
                    needsDownload.Add(new WadInfo(mismatch.WadName, mismatch.ExpectedHash));
                    continue;
                }
                
                // If matching version is not already in the expected location, copy/move it
                var targetDir = Path.GetDirectoryName(mismatch.LocalPath);
                var matchingDir = Path.GetDirectoryName(mismatch.MatchingVersionPath);
                
                if (!string.Equals(targetDir, matchingDir, StringComparison.OrdinalIgnoreCase))
                {
                    // Copy to target directory with standard name
                    var targetPath = Path.Combine(targetDir!, mismatch.WadName);
                    try
                    {
                        File.Copy(mismatch.MatchingVersionPath, targetPath, overwrite: true);
                        LoggingService.Instance.Info($"Copied matching version to {targetPath}");
                    }
                    catch (Exception ex)
                    {
                        LoggingService.Instance.Error($"Failed to copy matching version: {ex.Message}");
                        needsDownload.Add(new WadInfo(mismatch.WadName, mismatch.ExpectedHash));
                    }
                }
                else
                {
                    // Activate the archived version (rename to standard name)
                    var activated = wadManager.ActivateArchivedWad(mismatch.MatchingVersionPath, mismatch.WadName);
                    if (activated == null)
                    {
                        LoggingService.Instance.Error($"Failed to activate archived version of {mismatch.WadName}");
                        needsDownload.Add(new WadInfo(mismatch.WadName, mismatch.ExpectedHash));
                    }
                }
            }
            else
            {
                // No local version with matching hash - need to download
                // First, archive the current file
                var archived = wadManager.ArchiveWadWithHash(mismatch.LocalPath);
                if (archived != null)
                {
                    LoggingService.Instance.Info($"Archived mismatched {mismatch.WadName} as {Path.GetFileName(archived)}");
                }
                else
                {
                    LoggingService.Instance.Warning($"Failed to archive mismatched {mismatch.WadName} before download.");
                }

                needsDownload.Add(new WadInfo(mismatch.WadName, mismatch.ExpectedHash));
            }
        }
        
        return needsDownload;
    }

    /// <summary>
    /// Gets the folder containing the Zandronum executable.
    /// </summary>
    private string? GetExecutableFolder(ServerInfo server)
    {
        var exePath = GetExecutablePath(server);
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            return Path.GetDirectoryName(exePath);
        }
        return null;
    }

    /// <summary>
    /// Finds a WAD file using WadManager.
    /// WadManager now includes executable folders as highest priority search locations.
    /// </summary>
    private string? FindWadWithExeFolder(string wadName, string? exeFolder)
    {
        // WadManager now handles exe folder priority automatically
        return WadManager.Instance.FindWad(wadName);
    }

    /// <summary>
    /// Finds a file in a directory with case-insensitive name matching.
    /// </summary>
    private static string? FindFileIgnoreCase(string directory, string fileName)
    {
        if (!Directory.Exists(directory))
            return null;
        
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (Path.GetFileName(file).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }
        catch { /* Ignore access errors */ }
        
        return null;
    }

    /// <summary>
    /// Checks if the appropriate Zandronum executable is configured for the server.
    /// For testing servers, checks if the version is installed.
    /// </summary>
    /// <param name="server">The server to check.</param>
    /// <returns>True if executable is configured and exists.</returns>
    public bool IsExecutableConfigured(ServerInfo server)
    {
        var settings = SettingsService.Instance.Settings;
        
        if (server.IsTestingServer)
        {
            // For testing, check if testing folder is available (explicit or default)
            var testingRoot = GetTestingRootPath();
            return !string.IsNullOrEmpty(testingRoot);
        }
        
        return !string.IsNullOrEmpty(settings.ZandronumPath) && File.Exists(settings.ZandronumPath);
    }

    /// <summary>
    /// Checks if a specific testing version is installed.
    /// </summary>
    public bool IsTestingVersionInstalled(ServerInfo server)
    {
        var exePath = GetTestingExePath(server);
        return !string.IsNullOrEmpty(exePath) && File.Exists(exePath);
    }

    /// <summary>
    /// Gets the folder path for a specific testing version.
    /// </summary>
    public string? GetTestingVersionFolder(ServerInfo server)
    {
        var testingRoot = GetTestingRootPath();
        if (string.IsNullOrEmpty(testingRoot) || string.IsNullOrEmpty(server.GameVersion))
        {
            return null;
        }
        
        // Sanitize version string for folder name
        var safeVersion = SanitizeFolderName(server.GameVersion);
        return Path.Combine(testingRoot, safeVersion);
    }

    /// <summary>
    /// Gets the root path for testing versions.
    /// Falls back to {ZandronumPath}/TestingVersions/ if not explicitly configured.
    /// </summary>
    public string? GetTestingRootPath() => PathResolver.GetTestingVersionsPath();

    /// <summary>
    /// Gets the exe path for a specific testing version.
    /// </summary>
    private string? GetTestingExePath(ServerInfo server)
    {
        var folder = GetTestingVersionFolder(server);
        if (string.IsNullOrEmpty(folder))
        {
            return null;
        }
        return Path.Combine(folder, "zandronum.exe");
    }

    /// <summary>
    /// Extracts the core version string from a full game version.
    /// Strips extraneous data after the version number (e.g., OS info, mod names).
    /// Example: "3.3-alpha-r260112-1855 (TSPGv32) on Linux 6.8.0-58-generic" -> "3.3-alpha-r260112-1855"
    /// </summary>
    public static string ExtractCoreVersion(string fullVersion)
    {
        if (string.IsNullOrEmpty(fullVersion))
        {
            return fullVersion;
        }
        
        // Strip everything from the first space character (matches Doomseeker behavior)
        var spaceIndex = fullVersion.IndexOf(' ');
        if (spaceIndex > 0)
        {
            return fullVersion.Substring(0, spaceIndex);
        }
        
        return fullVersion;
    }

    /// <summary>
    /// Sanitizes a string to be safe for use as a folder name.
    /// </summary>
    private static string SanitizeFolderName(string name)
    {
        // First extract the core version, then sanitize
        var coreVersion = ExtractCoreVersion(name);
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", coreVersion.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Gets the path to the Zandronum executable for the given server.
    /// For testing servers, returns the versioned path.
    /// </summary>
    public string GetExecutablePath(ServerInfo server)
    {
        var settings = SettingsService.Instance.Settings;
        
        if (server.IsTestingServer)
        {
            return GetTestingExePath(server) ?? string.Empty;
        }
        
        return settings.ZandronumPath;
    }

    /// <summary>
    /// Downloads and installs a testing version for the specified server.
    /// </summary>
    /// <param name="server">The server requiring the testing version.</param>
    /// <returns>True if download and installation succeeded.</returns>
    public async Task<bool> DownloadTestingBuildAsync(ServerInfo server)
    {
        if (string.IsNullOrEmpty(server.TestingArchive))
        {
            LaunchError?.Invoke(this, "No testing archive URL available from server.");
            return false;
        }

        var archiveUri = ResolveTestingArchiveUri(server.TestingArchive);
        if (archiveUri == null)
        {
            LaunchError?.Invoke(this, $"Invalid testing archive URL: {server.TestingArchive}");
            return false;
        }

        var versionFolder = GetTestingVersionFolder(server);
        if (string.IsNullOrEmpty(versionFolder))
        {
            LaunchError?.Invoke(this, "Testing versions folder not configured.");
            return false;
        }

        LoggingService.Instance.Info($"Downloading testing version from: {archiveUri}");
        DownloadProgress?.Invoke(this, new TestingBuildDownloadProgress
        {
            Status = "Starting download...",
            ProgressPercent = 0
        });

        try
        {
            using var response = await _httpClient.GetAsync(archiveUri, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var tempFile = Path.GetTempFileName();

            try
            {
                await DownloadTestingArchiveAsync(archiveUri, tempFile, totalBytes, response);
            }
            catch
            {
                try { File.Delete(tempFile); } catch { }
                throw;
            }

            DownloadProgress?.Invoke(this, new TestingBuildDownloadProgress
            {
                Status = "Extracting...",
                ProgressPercent = 90,
                DownloadedBytes = totalBytes > 0 ? totalBytes : 0,
                TotalBytes = totalBytes > 0 ? totalBytes : 0
            });

            // Create version folder
            Directory.CreateDirectory(versionFolder);

            // Extract archive
            await Task.Run(() => ExtractArchive(tempFile, versionFolder, archiveUri.AbsoluteUri));

            // Clean up temp file
            try { File.Delete(tempFile); } catch { }

            // Copy configuration files from base Zandronum directory (matches Doomseeker behavior)
            DownloadProgress?.Invoke(this, new TestingBuildDownloadProgress
            {
                Status = "Copying configuration files...",
                ProgressPercent = 95,
                DownloadedBytes = totalBytes > 0 ? totalBytes : 0,
                TotalBytes = totalBytes > 0 ? totalBytes : 0
            });
            await Task.Run(() => CopyConfigFilesToTestingVersion(versionFolder));

            DownloadProgress?.Invoke(this, new TestingBuildDownloadProgress
            {
                Status = "Complete!",
                ProgressPercent = 100,
                DownloadedBytes = totalBytes > 0 ? totalBytes : 0,
                TotalBytes = totalBytes > 0 ? totalBytes : 0
            });
            LoggingService.Instance.Info($"Testing version {ExtractCoreVersion(server.GameVersion)} installed to: {versionFolder}");

            return IsTestingVersionInstalled(server);
        }
        catch (Exception ex)
        {
            var error = $"Failed to download testing build: {ex.Message}";
            LaunchError?.Invoke(this, error);
            LoggingService.Instance.Error(error);
            return false;
        }
    }

    private async Task DownloadTestingArchiveAsync(Uri archiveUri, string outputPath, long totalBytes, HttpResponseMessage initialResponse)
    {
        var domain = archiveUri.Host;
        var (domainMaxThreads, minSegmentSizeKb, _, _) = _domainConfig.GetEffectiveThreadSettings(domain);
        var threadCount = CalculateOptimalThreads(totalBytes, domainMaxThreads, minSegmentSizeKb);
        var minSegmentSizeBytes = minSegmentSizeKb * 1024L;
        var supportsRange = totalBytes > 0 && threadCount > 1 && await TestRangeRequestAsync(archiveUri);

        if (supportsRange && totalBytes > minSegmentSizeBytes * 2)
        {
            LoggingService.Instance.Verbose($"Using {threadCount} threads for testing build download from {domain}.");
            initialResponse.Dispose();
            await MultiThreadedDownloadTestingArchiveAsync(archiveUri, outputPath, totalBytes, threadCount);
            _domainConfig.UpdateThreadCount(domain, threadCount);
            return;
        }

        await SingleThreadDownloadTestingArchiveAsync(archiveUri, outputPath, totalBytes, initialResponse);
    }

    private async Task SingleThreadDownloadTestingArchiveAsync(Uri archiveUri, string outputPath, long totalBytes, HttpResponseMessage? response)
    {
        using var ownedResponse = response is null
            ? await _httpClient.GetAsync(archiveUri, HttpCompletionOption.ResponseHeadersRead)
            : null;
        using var activeResponse = response ?? ownedResponse!;

        activeResponse.EnsureSuccessStatusCode();

        var resolvedTotalBytes = totalBytes > 0 ? totalBytes : activeResponse.Content.Headers.ContentLength ?? -1;
        await using var contentStream = await activeResponse.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, AppConstants.BufferSizes.FileStreamBuffer, true);

        var buffer = new byte[AppConstants.BufferSizes.NetworkBuffer];
        var totalRead = 0L;
        var stopwatch = Stopwatch.StartNew();
        var lastUpdate = Stopwatch.StartNew();
        var lastBytes = 0L;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            totalRead += bytesRead;

            if (lastUpdate.ElapsedMilliseconds >= AppConstants.UiIntervals.UiUpdateThrottleMs ||
                (resolvedTotalBytes > 0 && totalRead >= resolvedTotalBytes))
            {
                var elapsedSeconds = Math.Max(lastUpdate.Elapsed.TotalSeconds, 0.001);
                var speed = (totalRead - lastBytes) / elapsedSeconds;
                ReportTestingDownloadProgress(totalRead, resolvedTotalBytes, speed, threadCount: 1);
                lastBytes = totalRead;
                lastUpdate.Restart();
            }
        }

        var finalElapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        var finalSpeed = totalRead / finalElapsedSeconds;
        ReportTestingDownloadProgress(totalRead, resolvedTotalBytes, finalSpeed, threadCount: 1);
    }

    private async Task MultiThreadedDownloadTestingArchiveAsync(Uri archiveUri, string outputPath, long totalBytes, int threadCount)
    {
        var segmentSize = (long)Math.Ceiling((double)totalBytes / threadCount);
        var segments = new List<(long Start, long End)>();

        for (long start = 0; start < totalBytes; start += segmentSize)
        {
            var end = Math.Min(start + segmentSize - 1, totalBytes - 1);
            segments.Add((start, end));
        }

        var downloadedBytes = new long[segments.Count];
        var failedSegments = new ConcurrentBag<(int SegmentIndex, bool IsConnectionLimit)>();
        using var outputHandle = File.OpenHandle(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        RandomAccess.SetLength(outputHandle, totalBytes);

        var lastUpdate = Stopwatch.StartNew();
        var lastBytes = 0L;
        using var progressTimer = new System.Timers.Timer(AppConstants.UiIntervals.UiUpdateThrottleMs);
        progressTimer.Elapsed += (_, _) =>
        {
            var currentBytes = downloadedBytes.Sum();
            var elapsedSeconds = Math.Max(lastUpdate.Elapsed.TotalSeconds, 0.001);
            var speed = (currentBytes - lastBytes) / elapsedSeconds;
            ReportTestingDownloadProgress(currentBytes, totalBytes, speed, threadCount);
            lastBytes = currentBytes;
            lastUpdate.Restart();
        };

        progressTimer.Start();

        try
        {
            var downloadTasks = segments.Select((segment, index) =>
                DownloadTestingArchiveSegmentAsync(archiveUri, segment.Start, segment.End, outputHandle, index, downloadedBytes, failedSegments));

            await Task.WhenAll(downloadTasks);

            var connectionLimitFailures = failedSegments.Count(f => f.IsConnectionLimit);
            if (connectionLimitFailures > segments.Count * 0.3)
            {
                _domainConfig.ReduceThreadCount(archiveUri.Host, threadCount);
                throw new Exception($"Testing build download hit connection limits with {threadCount} threads.");
            }

            if (!failedSegments.IsEmpty)
            {
                throw new Exception($"{failedSegments.Count} download segments failed.");
            }
        }
        finally
        {
            progressTimer.Stop();
        }

        var finalBytes = downloadedBytes.Sum();
        ReportTestingDownloadProgress(finalBytes, totalBytes, 0, threadCount);
    }

    private async Task DownloadTestingArchiveSegmentAsync(
        Uri archiveUri,
        long start,
        long end,
        Microsoft.Win32.SafeHandles.SafeFileHandle outputHandle,
        int segmentIndex,
        long[] downloadedBytes,
        ConcurrentBag<(int SegmentIndex, bool IsConnectionLimit)> failedSegments)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, archiveUri);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            long position = start;
            var buffer = new byte[AppConstants.BufferSizes.NetworkBuffer];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
            {
                await RandomAccess.WriteAsync(outputHandle, buffer.AsMemory(0, bytesRead), position);
                position += bytesRead;
                downloadedBytes[segmentIndex] = position - start;
            }
        }
        catch (HttpRequestException ex)
        {
            var isConnectionLimit = ex.StatusCode == HttpStatusCode.TooManyRequests ||
                                    ex.StatusCode == HttpStatusCode.ServiceUnavailable ||
                                    ex.InnerException is System.Net.Sockets.SocketException;
            failedSegments.Add((segmentIndex, isConnectionLimit));
        }
        catch
        {
            failedSegments.Add((segmentIndex, false));
        }
    }

    private async Task<bool> TestRangeRequestAsync(Uri archiveUri)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, archiveUri);
            using var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode && response.Headers.AcceptRanges.Contains("bytes"))
            {
                return true;
            }
        }
        catch
        {
            // Fall back to GET probe below.
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, archiveUri);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode && response.Headers.AcceptRanges.Contains("bytes");
        }
        catch
        {
            return false;
        }
    }

    private static int CalculateOptimalThreads(long totalBytes, int domainMaxThreads, int minSegmentSizeKb)
    {
        if (totalBytes <= 0)
        {
            return 1;
        }

        var minSegmentSize = minSegmentSizeKb * 1024L;
        var maxBySegmentSize = (int)Math.Max(1, totalBytes / minSegmentSize);
        var maxByFileSize = totalBytes switch
        {
            < 1_000_000 => 4,
            < 5_000_000 => 16,
            < 20_000_000 => 32,
            < 50_000_000 => 64,
            < 100_000_000 => 128,
            _ => domainMaxThreads
        };

        return Math.Max(1, Math.Min(Math.Min(maxByFileSize, domainMaxThreads), maxBySegmentSize));
    }

    private void ReportTestingDownloadProgress(long downloadedBytes, long totalBytes, double bytesPerSecond, int threadCount)
    {
        TimeSpan? estimatedTimeRemaining = null;
        if (totalBytes > 0 && bytesPerSecond > 0 && downloadedBytes < totalBytes)
        {
            estimatedTimeRemaining = TimeSpan.FromSeconds((totalBytes - downloadedBytes) / bytesPerSecond);
        }

        DownloadProgress?.Invoke(this, new TestingBuildDownloadProgress
        {
            Status = "Downloading...",
            ProgressPercent = totalBytes > 0 ? (int)((downloadedBytes * 100) / totalBytes) : 0,
            DownloadedBytes = downloadedBytes,
            TotalBytes = totalBytes > 0 ? totalBytes : 0,
            BytesPerSecond = bytesPerSecond,
            EstimatedTimeRemaining = estimatedTimeRemaining,
            ThreadCount = threadCount
        });
    }

    private static Uri? ResolveTestingArchiveUri(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return null;
        }

        if (Uri.TryCreate(archivePath, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        return Uri.TryCreate(TestingArchiveBaseUri, archivePath, out var relativeUri)
            ? relativeUri
            : null;
    }

    /// <summary>
    /// Extracts an archive (zip, 7z, tar.gz, etc.) to the destination folder using SharpCompress.
    /// </summary>
    private void ExtractArchive(string archivePath, string destinationFolder, string originalUrl)
    {
        var extension = Path.GetExtension(originalUrl).ToLowerInvariant();
        
        // For simple zip files, use built-in ZipFile for efficiency
        if (extension == ".zip" || extension == ".pk3")
        {
            ZipFile.ExtractToDirectory(archivePath, destinationFolder, overwriteFiles: true);
            return;
        }
        
        // Use SharpCompress for all other archive formats (7z, rar, tar, etc.)
        using var archive = ArchiveFactory.Open(archivePath);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            entry.WriteToDirectory(destinationFolder, new ExtractionOptions
            {
                ExtractFullPath = true,
                Overwrite = true
            });
        }
    }

    /// <summary>
    /// Copies configuration files (.ini) from the base Zandronum directory to the testing version folder.
    /// This matches Doomseeker's behavior of copying config files when installing testing versions.
    /// </summary>
    private void CopyConfigFilesToTestingVersion(string testingVersionFolder)
    {
        var settings = SettingsService.Instance.Settings;
        
        // Get the base Zandronum directory (where the stable exe is located)
        if (string.IsNullOrEmpty(settings.ZandronumPath) || !File.Exists(settings.ZandronumPath))
        {
            LoggingService.Instance.Verbose("No base Zandronum path configured, skipping config file copy.");
            return;
        }

        var baseDir = Path.GetDirectoryName(settings.ZandronumPath);
        if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir))
        {
            LoggingService.Instance.Verbose("Base Zandronum directory not found, skipping config file copy.");
            return;
        }

        // Copy all .ini files from the base directory
        var iniFiles = Directory.GetFiles(baseDir, "*.ini", SearchOption.TopDirectoryOnly);
        var copiedCount = 0;

        foreach (var iniFile in iniFiles)
        {
            var fileName = Path.GetFileName(iniFile);
            var targetPath = Path.Combine(testingVersionFolder, fileName);
            
            try
            {
                // Only copy if target doesn't exist (don't overwrite user's testing config)
                if (!File.Exists(targetPath))
                {
                    File.Copy(iniFile, targetPath);
                    copiedCount++;
                    LoggingService.Instance.Verbose($"Copied config file: {fileName}");
                }
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Warning($"Failed to copy {fileName}: {ex.Message}");
            }
        }

        if (copiedCount > 0)
        {
            LoggingService.Instance.Info($"Copied {copiedCount} configuration file(s) to testing version folder.");
        }
    }

    /// <summary>
    /// Gets a list of installed testing versions.
    /// </summary>
    public List<string> GetInstalledTestingVersions()
    {
        var testingRoot = GetTestingRootPath();
        if (string.IsNullOrEmpty(testingRoot) || 
            !Directory.Exists(testingRoot))
        {
            return [];
        }

        var versions = new List<string>();
        foreach (var dir in Directory.GetDirectories(testingRoot))
        {
            var exePath = Path.Combine(dir, "zandronum.exe");
            if (File.Exists(exePath))
            {
                versions.Add(Path.GetFileName(dir));
            }
        }
        return versions;
    }

    private string BuildCommandLine(
        ServerInfo server,
        string? connectPassword,
        string? joinPassword,
        ISet<string>? excludedOptionalPwads = null)
    {
        var args = new List<string>();
        var exeFolder = GetExecutableFolder(server);
        
        // Connection
        args.Add($"-connect {server.Address}:{server.Port}");
        
        // IWAD
        if (!string.IsNullOrEmpty(server.IWAD))
        {
            var iwadPath = FindWadWithExeFolder(server.IWAD, exeFolder);
            if (!string.IsNullOrEmpty(iwadPath))
            {
                args.Add($"-iwad \"{iwadPath}\"");
            }
        }
        
        // PWADs
        var pwadPaths = new List<string>();
        foreach (var pwad in server.PWADs)
        {
            if (pwad.IsOptional && excludedOptionalPwads?.Contains(pwad.Name) == true)
            {
                continue;
            }

            var path = FindWadWithExeFolder(pwad.Name, exeFolder);
            if (!string.IsNullOrEmpty(path))
            {
                pwadPaths.Add($"\"{path}\"");
            }
        }
        if (pwadPaths.Count > 0)
        {
            args.Add($"-file {string.Join(" ", pwadPaths)}");
        }
        
        // Passwords
        if (!string.IsNullOrEmpty(connectPassword))
        {
            args.Add($"+cl_password \"{connectPassword}\"");
        }
        if (!string.IsNullOrEmpty(joinPassword))
        {
            args.Add($"+cl_joinpassword \"{joinPassword}\"");
        }
        
        return string.Join(" ", args);
    }
    
    /// <summary>
    /// Gets the full connect command for a server including the executable path.
    /// Uses full paths for all files.
    /// </summary>
    /// <param name="server">The server to connect to.</param>
    /// <returns>The full command string, or null if executable not found.</returns>
    public string? GetFullConnectCommand(ServerInfo server)
    {
        var exePath = GetExecutablePath(server);
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return null;
        
        var args = BuildCommandLine(server, null, null);
        return $"\"{exePath}\" {args}";
    }

    /// <summary>
    /// Launches Zandronum in offline (single-player) mode with the given IWAD and PWADs.
    /// </summary>
    /// <param name="exePath">Full path to the Zandronum executable, or null for the stable configured path.</param>
    public bool LaunchOffline(string? exePath, string iwadPath, IReadOnlyList<string> pwadPaths, int skill, string? map)
    {
        exePath ??= SettingsService.Instance.Settings.ZandronumPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            LaunchError?.Invoke(this, "Zandronum executable not configured. Please set it in Settings.");
            return false;
        }

        var args = new List<string>
        {
            $"-iwad \"{iwadPath}\""
        };

        if (pwadPaths.Count > 0)
        {
            args.Add($"-file {string.Join(" ", pwadPaths.Select(p => $"\"{p}\""))}");
        }

        if (skill >= 0 && skill <= 4)
        {
            args.Add($"-skill {skill}");
        }

        if (!string.IsNullOrWhiteSpace(map))
        {
            args.Add($"+map \"{map}\"");
        }

        var argString = string.Join(" ", args);
        LoggingService.Instance.Verbose($"Launching offline: {exePath} {argString}");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = argString,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath)
            };
            Process.Start(startInfo);

            var msg = "Launched Zandronum in offline mode";
            LaunchSuccess?.Invoke(this, msg);
            LoggingService.Instance.Info(msg);
            return true;
        }
        catch (Exception ex)
        {
            var error = $"Failed to launch Zandronum offline: {ex.Message}";
            LaunchError?.Invoke(this, error);
            LoggingService.Instance.Error(error);
            return false;
        }
    }

    /// <summary>
    /// Launches Zandronum as a host server (listen or dedicated).
    /// For dedicated servers, tries zandronum-server.exe first, falling back to zandronum.exe -host.
    /// </summary>
    /// <param name="exePath">Full path to the Zandronum executable, or null for the stable configured path.</param>
    public bool LaunchHost(
        string? exePath,
        string iwadPath,
        IReadOnlyList<string> pwadPaths,
        int skill,
        int maxPlayers,
        int maxClients,
        bool isDedicated,
        string? map,
        string? serverName,
        string? password,
        string? joinPassword)
    {
        exePath ??= SettingsService.Instance.Settings.ZandronumPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            LaunchError?.Invoke(this, "Zandronum executable not configured. Please set it in Settings.");
            return false;
        }

        string resolvedExePath;
        var args = new List<string>();

        if (isDedicated)
        {
            var exeDir = Path.GetDirectoryName(exePath) ?? "";
            var dedicatedExe = Path.Combine(exeDir, "zandronum-server.exe");
            if (File.Exists(dedicatedExe))
            {
                resolvedExePath = dedicatedExe;
            }
            else
            {
                resolvedExePath = exePath;
                args.Add("-host");
            }
        }
        else
        {
            resolvedExePath = exePath;
            args.Add("-host");
        }

        args.Add($"-iwad \"{iwadPath}\"");

        if (pwadPaths.Count > 0)
        {
            args.Add($"-file {string.Join(" ", pwadPaths.Select(p => $"\"{p}\""))}");
        }

        if (skill >= 0 && skill <= 4)
        {
            args.Add($"-skill {skill}");
        }

        args.Add($"+sv_maxplayers {maxPlayers}");
        args.Add($"+sv_maxclients {maxClients}");

        if (!string.IsNullOrWhiteSpace(map))
        {
            args.Add($"+map \"{map}\"");
        }

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            args.Add($"+sv_hostname \"{serverName}\"");
        }

        if (!string.IsNullOrWhiteSpace(password))
        {
            args.Add($"+sv_password \"{password}\"");
        }

        if (!string.IsNullOrWhiteSpace(joinPassword))
        {
            args.Add($"+sv_joinpassword \"{joinPassword}\"");
        }

        var argString = string.Join(" ", args);
        LoggingService.Instance.Verbose($"Launching host: {resolvedExePath} {argString}");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = resolvedExePath,
                Arguments = argString,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(resolvedExePath)
            };
            Process.Start(startInfo);

            var mode = isDedicated ? "dedicated server" : "listen server";
            var msg = $"Launched Zandronum as {mode}";
            LaunchSuccess?.Invoke(this, msg);
            LoggingService.Instance.Info(msg);
            return true;
        }
        catch (Exception ex)
        {
            var error = $"Failed to launch Zandronum host: {ex.Message}";
            LaunchError?.Invoke(this, error);
            LoggingService.Instance.Error(error);
            return false;
        }
    }
}
