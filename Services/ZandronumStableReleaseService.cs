using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharpCompress.Archives;
using SharpCompress.Common;
using ZScape.Models;

namespace ZScape.Services;

public sealed class ZandronumStableReleaseService
{
    private const string StableVersionsFolderName = "StableVersions";
    private static readonly Regex StableVersionRegex = new(@"(?<!\d)(\d+)\.(\d+)(?:\.(\d+))?(?:\.\d+)?(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public sealed record ReleaseManifest(
        string Version,
        string PlatformLabel,
        string DownloadUrl,
        string ArchiveFileName,
        string ExecutableName);

    public sealed record InstallProgress(
        string Status,
        int ProgressPercent,
        long DownloadedBytes = 0,
        long TotalBytes = 0,
        double BytesPerSecond = 0,
        TimeSpan? EstimatedTimeRemaining = null);

    public sealed record InstallResult(
        string Version,
        string InstallDirectory,
        string ExecutablePath);

    private static readonly Lazy<ZandronumStableReleaseService> _instance = new(() => new ZandronumStableReleaseService());
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static ZandronumStableReleaseService Instance => _instance.Value;

    private ZandronumStableReleaseService() { }

    public bool IsStableReleasePlatformSupported(out string? errorMessage)
    {
        if (OperatingSystem.IsWindows())
        {
            errorMessage = null;
            return true;
        }

        if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            errorMessage = null;
            return true;
        }

        if (OperatingSystem.IsMacOS())
        {
            errorMessage = null;
            return true;
        }

        errorMessage = "Automatic stable installs are not available on this platform yet.";
        return false;
    }

    public bool TryCreateReleaseManifestForVersion(string version, out ReleaseManifest release, out string? errorMessage)
    {
        if (!TryExtractStableVersion(version, out var normalizedVersion))
        {
            release = null!;
            errorMessage = "The requested Zandronum stable version could not be recognized.";
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            release = new ReleaseManifest(
                Version: normalizedVersion,
                PlatformLabel: "Windows 64-bit",
                DownloadUrl: $"https://zandronum.com/downloads/zandronum{normalizedVersion}-win64-base.zip",
                ArchiveFileName: $"zandronum{normalizedVersion}-win64-base.zip",
                ExecutableName: "zandronum.exe");
            errorMessage = null;
            return true;
        }

        if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            release = new ReleaseManifest(
                Version: normalizedVersion,
                PlatformLabel: "Linux 64-bit",
                DownloadUrl: $"https://zandronum.com/downloads/zandronum{normalizedVersion}-linux-x86_64.tar.bz2",
                ArchiveFileName: $"zandronum{normalizedVersion}-linux-x86_64.tar.bz2",
                ExecutableName: "zandronum");
            errorMessage = null;
            return true;
        }

        if (OperatingSystem.IsMacOS())
        {
            release = new ReleaseManifest(
                Version: normalizedVersion,
                PlatformLabel: "macOS",
                DownloadUrl: $"https://zandronum.com/downloads/zandronum{normalizedVersion}-macosx.dmg",
                ArchiveFileName: $"zandronum{normalizedVersion}-macosx.dmg",
                ExecutableName: "zandronum");
            errorMessage = null;
            return true;
        }

        release = null!;
        errorMessage = "Automatic stable installs are not available on this platform yet.";
        return false;
    }

    public bool TryGetLatestObservedRelease(
        IEnumerable<ServerInfo> servers,
        out ReleaseManifest release,
        out int matchingServerCount,
        out string? errorMessage)
    {
        matchingServerCount = 0;
        release = null!;

        string? latestVersion = null;
        foreach (var server in servers)
        {
            if (server.IsTestingServer || !TryExtractStableVersion(server.GameVersion, out var observedVersion))
            {
                continue;
            }

            matchingServerCount++;
            if (latestVersion == null || CompareStableVersions(observedVersion, latestVersion) > 0)
            {
                latestVersion = observedVersion;
            }
        }

        if (string.IsNullOrEmpty(latestVersion))
        {
            errorMessage = "No stable versions were reported by the current server list.";
            return false;
        }

        return TryCreateReleaseManifestForVersion(latestVersion, out release, out errorMessage);
    }

    public bool TryExtractStableVersion(string? versionText, out string version)
    {
        version = string.Empty;

        if (string.IsNullOrWhiteSpace(versionText))
        {
            return false;
        }

        if (versionText.Contains("alpha", StringComparison.OrdinalIgnoreCase) ||
            versionText.Contains("beta", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = StableVersionRegex.Match(versionText);
        if (!match.Success)
        {
            return false;
        }

        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        version = $"{major}.{minor}.{patch}";
        return true;
    }

    public int CompareStableVersions(string leftVersion, string rightVersion)
    {
        var left = ParseStableVersion(leftVersion);
        var right = ParseStableVersion(rightVersion);

        var majorCompare = left.Major.CompareTo(right.Major);
        if (majorCompare != 0)
        {
            return majorCompare;
        }

        var minorCompare = left.Minor.CompareTo(right.Minor);
        if (minorCompare != 0)
        {
            return minorCompare;
        }

        return left.Patch.CompareTo(right.Patch);
    }

    public bool TryGetInstalledStableVersion(string executablePath, out string version, out string? errorMessage)
    {
        version = string.Empty;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            errorMessage = "The configured Zandronum executable could not be found.";
            return false;
        }

        foreach (var candidate in GetVersionCandidates(executablePath))
        {
            if (TryExtractStableVersion(candidate, out version))
            {
                return true;
            }
        }

        errorMessage = "ZScape could not determine the installed Zandronum version from the executable metadata or folder name.";
        return false;
    }

    public string? GetStableVersionsRootPath(string configuredExecutablePath)
    {
        var installPath = GetPrimaryInstallPath(configuredExecutablePath);
        if (string.IsNullOrEmpty(installPath))
        {
            return null;
        }

        if (installPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            var bundleParent = Path.GetDirectoryName(installPath);
            return string.IsNullOrEmpty(bundleParent)
                ? null
                : Path.Combine(bundleParent, StableVersionsFolderName);
        }

        return Path.Combine(installPath, StableVersionsFolderName);
    }

    public string? GetArchivedVersionInstallPath(string configuredExecutablePath, string version)
    {
        if (!TryCreateReleaseManifestForVersion(version, out var release, out _))
        {
            return null;
        }

        var stableVersionsRoot = GetStableVersionsRootPath(configuredExecutablePath);
        return string.IsNullOrEmpty(stableVersionsRoot)
            ? null
            : Path.Combine(stableVersionsRoot, GetSuggestedInstallFolderName(release));
    }

    public string? GetInstalledArchivedExecutablePath(string configuredExecutablePath, string version)
    {
        if (!TryCreateReleaseManifestForVersion(version, out var release, out _))
        {
            return null;
        }

        var installPath = GetArchivedVersionInstallPath(configuredExecutablePath, version);
        return string.IsNullOrEmpty(installPath)
            ? null
            : TryFindExecutablePath(installPath, release.ExecutableName);
    }

    public string GetSuggestedInstallFolderName(ReleaseManifest release) =>
        release.ArchiveFileName.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)
            ? $"Zandronum {release.Version}.app"
            : $"Zandronum {release.Version}";

    public string[] GetArchivePickerPatterns(ReleaseManifest release) =>
        release.ArchiveFileName.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)
            ? ["*.dmg"]
            : release.ArchiveFileName.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase)
            ? ["*.tar.bz2", "*.tbz2", "*.bz2"]
            : ["*.zip"];

    public async Task<InstallResult> DownloadAndInstallAsync(
        ReleaseManifest release,
        string installDirectory,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tempDownloadDirectory = Path.Combine(Path.GetTempPath(), "ZScape", "stable-downloads");
        Directory.CreateDirectory(tempDownloadDirectory);

        var tempArchivePath = Path.Combine(tempDownloadDirectory, $"{Guid.NewGuid():N}-{release.ArchiveFileName}");

        try
        {
            await DownloadArchiveAsync(release, tempArchivePath, progress, cancellationToken);
            var extractionProgress = CreateMappedProgress(progress, 72, 100);
            return await InstallFromArchiveAsync(release, tempArchivePath, installDirectory, extractionProgress, cancellationToken);
        }
        finally
        {
            try
            {
                if (File.Exists(tempArchivePath))
                {
                    File.Delete(tempArchivePath);
                }
            }
            catch
            {
            }
        }
    }

    public async Task<InstallResult> InstallFromArchiveAsync(
        ReleaseManifest release,
        string archivePath,
        string installDirectory,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            throw new FileNotFoundException("The downloaded Zandronum archive could not be found.", archivePath);
        }

        Directory.CreateDirectory(installDirectory);
        progress?.Report(new InstallProgress($"Preparing Zandronum {release.Version}...", 5));

        var extractedInstallPath = await ExtractArchiveAsync(release, archivePath, installDirectory, progress, cancellationToken);

        var executablePath = FindExecutablePath(extractedInstallPath, release.ExecutableName);
        EnsureExecutablePermissions(executablePath);

        progress?.Report(new InstallProgress("Complete!", 100));
        return new InstallResult(release.Version, extractedInstallPath, executablePath);
    }

    private async Task DownloadArchiveAsync(
        ReleaseManifest release,
        string archivePath,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        var totalRead = 0L;
        var lastBytes = 0L;
        var started = Stopwatch.StartNew();
        var lastUpdate = Stopwatch.StartNew();
        var inspectedFirstChunk = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            if (!inspectedFirstChunk)
            {
                EnsureArchiveLikeResponse(response, buffer.AsSpan(0, bytesRead));
                inspectedFirstChunk = true;
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;

            if (lastUpdate.ElapsedMilliseconds >= 100 || (totalBytes > 0 && totalRead >= totalBytes))
            {
                var speed = (totalRead - lastBytes) / Math.Max(lastUpdate.Elapsed.TotalSeconds, 0.001);
                progress?.Report(CreateDownloadProgress(release.Version, totalRead, totalBytes, speed, 5, 70));
                lastBytes = totalRead;
                lastUpdate.Restart();
            }
        }

        if (!inspectedFirstChunk)
        {
            throw new InvalidDataException("The stable download returned an empty response.");
        }

        var finalSpeed = totalRead / Math.Max(started.Elapsed.TotalSeconds, 0.001);
        progress?.Report(CreateDownloadProgress(release.Version, totalRead, totalBytes, finalSpeed, 5, 70));
    }

    private async Task<string> ExtractArchiveAsync(
        ReleaseManifest release,
        string archivePath,
        string installDirectory,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new InstallProgress("Preparing archive extraction...", 10));

        if (release.ArchiveFileName.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase))
        {
            return await Task.Run(
                () => ExtractMacDiskImage(archivePath, installDirectory, progress, release.Version, cancellationToken),
                cancellationToken);
        }

        if (release.ArchiveFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Run(() => ExtractZipArchiveSafely(archivePath, installDirectory, progress, release.Version, cancellationToken), cancellationToken);
            return installDirectory;
        }

        await Task.Run(() => ExtractSharpCompressArchiveSafely(archivePath, installDirectory, progress, release.Version, cancellationToken), cancellationToken);
        return installDirectory;
    }

    private static string ExtractMacDiskImage(
        string archivePath,
        string installDirectory,
        IProgress<InstallProgress>? progress,
        string version,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("DMG extraction is only available on macOS.");
        }

        progress?.Report(new InstallProgress($"Mounting Zandronum {version} disk image...", 20));
        var mountPoint = MountDiskImage(archivePath);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var appBundlePath = Directory.EnumerateDirectories(mountPoint, "*.app", SearchOption.TopDirectoryOnly).FirstOrDefault()
                ?? Directory.EnumerateDirectories(mountPoint, "*.app", SearchOption.AllDirectories).FirstOrDefault();

            if (string.IsNullOrEmpty(appBundlePath))
            {
                throw new FileNotFoundException("The Zandronum application bundle was not found in the mounted DMG.");
            }

            var targetAppPath = installDirectory.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
                ? installDirectory
                : Path.Combine(installDirectory, Path.GetFileName(appBundlePath));

            var targetParentDirectory = Path.GetDirectoryName(targetAppPath);
            if (string.IsNullOrEmpty(targetParentDirectory))
            {
                throw new InvalidOperationException("The destination path for the Zandronum application bundle was invalid.");
            }

            Directory.CreateDirectory(targetParentDirectory);
            if (Directory.Exists(targetAppPath))
            {
                Directory.Delete(targetAppPath, recursive: true);
            }

            progress?.Report(new InstallProgress($"Copying Zandronum {version}...", 80));
            RunProcessAndCaptureOutput("ditto", $"{QuoteArgument(appBundlePath)} {QuoteArgument(targetAppPath)}");

            progress?.Report(new InstallProgress("Finalizing macOS install...", 95));
            return targetAppPath;
        }
        finally
        {
            TryUnmountDiskImage(mountPoint);
        }
    }

    private static void ExtractZipArchiveSafely(
        string archivePath,
        string destinationDirectory,
        IProgress<InstallProgress>? progress,
        string version,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var fileEntries = archive.Entries.Where(entry => !IsZipDirectory(entry)).ToList();

        for (var index = 0; index < fileEntries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = fileEntries[index];
            var destinationPath = GetSafeDestinationPath(destinationDirectory, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);

            progress?.Report(CreateExtractionProgress(version, index + 1, fileEntries.Count, 10, 100, entry.FullName));
        }
    }

    private static void ExtractSharpCompressArchiveSafely(
        string archivePath,
        string destinationDirectory,
        IProgress<InstallProgress>? progress,
        string version,
        CancellationToken cancellationToken)
    {
        using var archive = ArchiveFactory.Open(archivePath);
        var fileEntries = archive.Entries.Where(entry => !entry.IsDirectory).ToList();

        for (var index = 0; index < fileEntries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = fileEntries[index];
            var entryKey = entry.Key ?? string.Empty;
            var destinationPath = GetSafeDestinationPath(destinationDirectory, entryKey);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.WriteToFile(destinationPath, new ExtractionOptions { Overwrite = true });

            progress?.Report(CreateExtractionProgress(version, index + 1, fileEntries.Count, 10, 100, entryKey));
        }
    }

    private static InstallProgress CreateExtractionProgress(
        string version,
        int current,
        int total,
        int startPercent,
        int endPercent,
        string currentEntry)
    {
        var progressPercent = total > 0
            ? startPercent + (current * (endPercent - startPercent)) / total
            : endPercent;

        var entryName = Path.GetFileName(currentEntry.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        return new InstallProgress(
            Status: string.IsNullOrEmpty(entryName)
                ? $"Extracting Zandronum {version}..."
                : $"Extracting {entryName}...",
            ProgressPercent: Math.Min(progressPercent, endPercent));
    }

    private static InstallProgress CreateDownloadProgress(
        string version,
        long downloadedBytes,
        long totalBytes,
        double bytesPerSecond,
        int startPercent,
        int endPercent)
    {
        var progressPercent = totalBytes > 0
            ? startPercent + (int)((downloadedBytes * (long)(endPercent - startPercent)) / Math.Max(totalBytes, 1))
            : startPercent;

        TimeSpan? eta = null;
        if (totalBytes > 0 && bytesPerSecond > 0 && downloadedBytes < totalBytes)
        {
            eta = TimeSpan.FromSeconds((totalBytes - downloadedBytes) / bytesPerSecond);
        }

        return new InstallProgress(
            Status: $"Downloading Zandronum {version}...",
            ProgressPercent: Math.Clamp(progressPercent, startPercent, endPercent),
            DownloadedBytes: downloadedBytes,
            TotalBytes: Math.Max(totalBytes, 0),
            BytesPerSecond: Math.Max(bytesPerSecond, 0),
            EstimatedTimeRemaining: eta);
    }

    private static void EnsureArchiveLikeResponse(HttpResponseMessage response, ReadOnlySpan<byte> initialBytes)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(mediaType) &&
            (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
             mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Automatic stable download returned HTML instead of an archive. Try the Website option instead.");
        }

        var previewLength = Math.Min(initialBytes.Length, 256);
        if (previewLength == 0)
        {
            return;
        }

        var preview = Encoding.UTF8.GetString(initialBytes[..previewLength]).TrimStart('\uFEFF', '\0', ' ', '\t', '\r', '\n');
        if (preview.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
            preview.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Automatic stable download returned an HTML page instead of an archive. Try the Website option instead.");
        }
    }

    private static IProgress<InstallProgress>? CreateMappedProgress(IProgress<InstallProgress>? progress, int startPercent, int endPercent)
    {
        if (progress == null)
        {
            return null;
        }

        return new Progress<InstallProgress>(update =>
        {
            var mappedPercent = startPercent + (int)Math.Round((endPercent - startPercent) * (update.ProgressPercent / 100d));
            progress.Report(update with { ProgressPercent = Math.Clamp(mappedPercent, startPercent, endPercent) });
        });
    }

    private static bool IsZipDirectory(ZipArchiveEntry entry) =>
        string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/", StringComparison.Ordinal);

    private static string GetSafeDestinationPath(string destinationDirectory, string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            throw new InvalidDataException("Archive entry path was empty.");
        }

        var normalizedEntryPath = entryPath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(normalizedEntryPath))
        {
            throw new InvalidDataException("Archive entry path was invalid.");
        }

        var rootPath = Path.GetFullPath(destinationDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var destinationPath = Path.GetFullPath(Path.Combine(rootPath, normalizedEntryPath));
        var expectedPrefix = rootPath + Path.DirectorySeparatorChar;

        if (!destinationPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(destinationPath, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Archive entry would extract outside the destination directory: {entryPath}");
        }

        return destinationPath;
    }

    private static string FindExecutablePath(string installDirectory, string executableName)
    {
        var executablePath = TryFindExecutablePath(installDirectory, executableName);

        if (string.IsNullOrEmpty(executablePath))
        {
            throw new FileNotFoundException($"The Zandronum executable was not found after extraction ({executableName}).");
        }

        return executablePath;
    }

    private static string? TryFindExecutablePath(string installDirectory, string executableName)
    {
        if (!Directory.Exists(installDirectory))
        {
            return null;
        }

        var executablePath = Directory
            .EnumerateFiles(installDirectory, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(path), executableName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path.Count(character => character is '\\' or '/'))
            .FirstOrDefault();

        return executablePath;
    }

    private static (int Major, int Minor, int Patch) ParseStableVersion(string versionText)
    {
        if (!Instance.TryExtractStableVersion(versionText, out var normalizedVersion))
        {
            throw new FormatException($"The Zandronum stable version '{versionText}' could not be parsed.");
        }

        var parts = normalizedVersion.Split('.');
        return (int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
    }

    private static IEnumerable<string> GetVersionCandidates(string executablePath)
    {
        FileVersionInfo? versionInfo = null;
        try
        {
            versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(versionInfo?.ProductVersion))
        {
            yield return versionInfo.ProductVersion;
        }

        if (!string.IsNullOrWhiteSpace(versionInfo?.FileVersion))
        {
            yield return versionInfo.FileVersion;
        }

        var appBundlePath = TryGetAppBundlePath(executablePath);
        if (!string.IsNullOrEmpty(appBundlePath))
        {
            var macVersion = TryReadMacBundleVersion(appBundlePath);
            if (!string.IsNullOrWhiteSpace(macVersion))
            {
                yield return macVersion;
            }

            yield return Path.GetFileNameWithoutExtension(appBundlePath);
        }

        var executableDirectory = Path.GetDirectoryName(executablePath);
        if (!string.IsNullOrEmpty(executableDirectory))
        {
            yield return Path.GetFileName(executableDirectory);

            var parentDirectory = Path.GetDirectoryName(executableDirectory);
            if (!string.IsNullOrEmpty(parentDirectory))
            {
                yield return Path.GetFileName(parentDirectory);
            }
        }
    }

    private static string? GetPrimaryInstallPath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        return TryGetAppBundlePath(executablePath) ?? Path.GetDirectoryName(executablePath);
    }

    private static string? TryGetAppBundlePath(string path)
    {
        var currentDirectory = File.Exists(path)
            ? Path.GetDirectoryName(path)
            : Directory.Exists(path)
                ? path
                : null;

        while (!string.IsNullOrEmpty(currentDirectory))
        {
            if (currentDirectory.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return currentDirectory;
            }

            currentDirectory = Path.GetDirectoryName(currentDirectory);
        }

        return null;
    }

    private static string? TryReadMacBundleVersion(string appBundlePath)
    {
        var infoPlistPath = Path.Combine(appBundlePath, "Contents", "Info.plist");
        if (!File.Exists(infoPlistPath))
        {
            return null;
        }

        try
        {
            var document = XDocument.Load(infoPlistPath);
            var dictElement = document.Root?.Element("dict");
            if (dictElement == null)
            {
                return null;
            }

            var elements = dictElement.Elements().ToList();
            for (var index = 0; index < elements.Count - 1; index++)
            {
                if (elements[index].Name != "key")
                {
                    continue;
                }

                var keyName = elements[index].Value;
                if (!string.Equals(keyName, "CFBundleShortVersionString", StringComparison.Ordinal) &&
                    !string.Equals(keyName, "CFBundleVersion", StringComparison.Ordinal))
                {
                    continue;
                }

                var valueElement = elements[index + 1];
                if (valueElement.Name == "string" && !string.IsNullOrWhiteSpace(valueElement.Value))
                {
                    return valueElement.Value.Trim();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string MountDiskImage(string archivePath)
    {
        var output = RunProcessAndCaptureOutput(
            "hdiutil",
            $"attach -nobrowse -readonly -plist {QuoteArgument(archivePath)}");

        var document = XDocument.Parse(output);
        var entitiesArray = document.Descendants("key")
            .FirstOrDefault(element => string.Equals(element.Value, "system-entities", StringComparison.Ordinal))?
            .ElementsAfterSelf("array")
            .FirstOrDefault();

        if (entitiesArray == null)
        {
            throw new InvalidOperationException("Unable to locate mounted DMG metadata.");
        }

        foreach (var dict in entitiesArray.Elements("dict"))
        {
            var elements = dict.Elements().ToList();
            for (var index = 0; index < elements.Count - 1; index++)
            {
                if (elements[index].Name == "key" &&
                    string.Equals(elements[index].Value, "mount-point", StringComparison.Ordinal) &&
                    elements[index + 1].Name == "string")
                {
                    var mountPoint = elements[index + 1].Value.Trim();
                    if (!string.IsNullOrEmpty(mountPoint))
                    {
                        return mountPoint;
                    }
                }
            }
        }

        throw new InvalidOperationException("The mounted Zandronum DMG did not report a mount point.");
    }

    private static void TryUnmountDiskImage(string mountPoint)
    {
        try
        {
            RunProcessAndCaptureOutput("hdiutil", $"detach {QuoteArgument(mountPoint)} -force");
        }
        catch
        {
        }
    }

    private static string RunProcessAndCaptureOutput(string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {message.Trim()}");
        }

        return output;
    }

    private static string QuoteArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private static void EnsureExecutablePermissions(string executablePath)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        File.SetUnixFileMode(
            executablePath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute);
    }
}