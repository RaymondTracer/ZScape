using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ZScape.Models;
using ZScape.Utilities;

namespace ZScape.Services;

/// <summary>
/// Discovers the Zandronum installations ZScape knows about and safely copies
/// either selected INI sections or a complete per-user configuration file
/// between them. Automatic synchronization is intentionally limited to
/// processes started through <see cref="GameLauncher"/>.
/// </summary>
public sealed class ZandronumConfigSyncService
{
    private static readonly Lazy<ZandronumConfigSyncService> InstanceFactory =
        new(() => new ZandronumConfigSyncService());

    private static readonly Regex SectionHeaderRegex = new(
        @"^\s*\[(?<name>[^\]\r\n]+)\]\s*(?:[;#].*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly LoggingService _logger = LoggingService.Instance;
    private readonly SemaphoreSlim _synchronizationGate = new(1, 1);
    private readonly object _trackedProcessLock = new();
    private readonly HashSet<int> _trackedProcessIds = [];

    public static ZandronumConfigSyncService Instance => InstanceFactory.Value;

    private ZandronumConfigSyncService()
    {
    }

    /// <summary>
    /// The per-user INI filename Zandronum creates beside its executable.
    /// </summary>
    public static string UserConfigurationFileName => $"zandronum-{Environment.UserName}.ini";

    /// <summary>
    /// Gets the per-user INI path belonging to an executable.
    /// </summary>
    public static string? GetConfigurationPathForExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        var directory = Path.GetDirectoryName(executablePath);
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : Path.Combine(directory, UserConfigurationFileName);
    }

    /// <summary>
    /// Returns the stable, archived stable, testing, and explicitly saved
    /// launch executables that ZScape can safely regard as known versions.
    /// It never searches arbitrary folders on the computer.
    /// </summary>
    public IReadOnlyList<ZandronumConfigurationVersion> DiscoverConfigurations()
    {
        var settings = SettingsService.Instance.Settings;
        var candidates = new Dictionary<string, string>(GetPathComparer());

        void AddCandidate(string? executablePath, string displayName)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                return;

            var normalizedPath = NormalizePath(executablePath);
            if (normalizedPath == null || candidates.ContainsKey(normalizedPath))
                return;

            candidates.Add(normalizedPath, displayName);
        }

        AddCandidate(settings.ZandronumPath, "Configured stable");

        foreach (var savedConfig in settings.SavedLaunchGameConfigs)
            AddCandidate(savedConfig.Config?.ExePath, "Saved launch configuration");

        AddCandidate(settings.LastLaunchGameConfig?.ExePath, "Last launch configuration");

        var stableVersionsRoot = string.IsNullOrWhiteSpace(settings.ZandronumPath)
            ? null
            : ZandronumStableReleaseService.Instance.GetStableVersionsRootPath(settings.ZandronumPath);
        foreach (var executablePath in EnumerateManagedZandronumExecutables(stableVersionsRoot))
        {
            AddCandidate(
                executablePath,
                $"Stable · {GetVersionFolderLabel(executablePath, stableVersionsRoot)}");
        }

        var testingRoot = PathResolver.GetTestingVersionsPath(settings);
        foreach (var executablePath in EnumerateManagedZandronumExecutables(testingRoot))
        {
            AddCandidate(
                executablePath,
                $"Testing · {GetVersionFolderLabel(executablePath, testingRoot)}");
        }

        return candidates
            .Select(pair => CreateConfigurationVersion(pair.Value, pair.Key))
            .OrderBy(version => version.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(version => version.ExecutablePath, GetPathComparer())
            .ToList();
    }

    /// <summary>
    /// Reads the section names that are actually present in the discovered
    /// configurations, so the UI never offers a guessed or hard-coded setting.
    /// </summary>
    public IReadOnlyList<string> GetAvailableSections(
        IEnumerable<ZandronumConfigurationVersion> configurations)
    {
        var sectionNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var configuration in configurations.Where(configuration => configuration.ConfigurationExists))
        {
            try
            {
                var document = ParseIni(ReadIniText(configuration.ConfigurationPath).Content);
                foreach (var section in document)
                    sectionNames.Add(section.Name);
            }
            catch (Exception ex)
            {
                _logger.Verbose(
                    $"Could not inspect Zandronum configuration '{configuration.ConfigurationPath}': {ex.Message}");
            }
        }

        return sectionNames.ToList();
    }

    /// <summary>
    /// Determines which source sections are different in every supplied target.
    /// A missing source section is never counted because selective sync would
    /// deliberately leave the target section intact.
    /// </summary>
    public IReadOnlyList<ZandronumConfigComparison> Compare(
        string sourceConfigurationPath,
        IEnumerable<string> targetConfigurationPaths,
        ZandronumConfigSyncSettings options)
    {
        if (string.IsNullOrWhiteSpace(sourceConfigurationPath) || !File.Exists(sourceConfigurationPath))
        {
            return targetConfigurationPaths
                .Select(path => new ZandronumConfigComparison(path, 0, "The selected source configuration is missing."))
                .ToList();
        }

        if (!HasSyncScope(options))
        {
            return targetConfigurationPaths
                .Select(path => new ZandronumConfigComparison(path, 0, "Select at least one section or choose entire-file sync."))
                .ToList();
        }

        try
        {
            var sourceText = ReadIniText(sourceConfigurationPath).Content;
            var sourceSections = ParseIni(sourceText);

            return targetConfigurationPaths
                .Where(path => !PathsEqual(path, sourceConfigurationPath))
                .Select(path => CompareOne(sourceText, sourceSections, path, options))
                .ToList();
        }
        catch (Exception ex)
        {
            return targetConfigurationPaths
                .Where(path => !PathsEqual(path, sourceConfigurationPath))
                .Select(path => new ZandronumConfigComparison(path, 0, ex.Message))
                .ToList();
        }
    }

    /// <summary>
    /// Copies the configured scope from one known configuration to all of the
    /// other known configurations. Existing target files are backed up under
    /// the current user's Documents\ZScape\Backups folder before a successful
    /// write.
    /// </summary>
    public async Task<ZandronumConfigSyncResult> SynchronizeFromConfigurationAsync(
        string sourceConfigurationPath,
        ZandronumConfigSyncSettings options,
        CancellationToken cancellationToken = default)
    {
        var scope = CreateScopeSnapshot(options);
        if (!scope.HasContent)
        {
            return new ZandronumConfigSyncResult(
                UpdatedFileCount: 0,
                UnchangedFileCount: 0,
                SkippedFileCount: 0,
                Errors: ["Select at least one INI section or choose entire-file sync first."]);
        }

        await _synchronizationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => SynchronizeCore(sourceConfigurationPath, scope, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _synchronizationGate.Release();
        }
    }

    /// <summary>
    /// Begins monitoring a process ZScape just launched. This is intentionally
    /// not a system-wide process watcher: the process handle is supplied only
    /// by <see cref="GameLauncher"/>, and synchronization happens after it exits.
    /// </summary>
    public void TrackLaunchedProcess(Process? process, string executablePath)
    {
        if (process == null)
            return;

        try
        {
            var options = SettingsService.Instance.Settings.ZandronumConfigSync;
            var sourceConfigurationPath = GetConfigurationPathForExecutable(executablePath);

            if (!options.AutoSyncEnabled || !HasSyncScope(options) ||
                string.IsNullOrWhiteSpace(sourceConfigurationPath))
            {
                process.Dispose();
                return;
            }

            var processId = process.Id;
            lock (_trackedProcessLock)
            {
                if (!_trackedProcessIds.Add(processId))
                {
                    process.Dispose();
                    return;
                }
            }

            _logger.Verbose(
                $"Configuration sync is waiting for Zandronum process {processId} to close: {Path.GetFileName(executablePath)}");
            _ = MonitorLaunchedProcessAsync(process, processId, sourceConfigurationPath);
        }
        catch (Exception ex)
        {
            process.Dispose();
            _logger.Warning($"Could not start Zandronum configuration sync monitoring: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns true when a settings object describes a non-empty sync action.
    /// </summary>
    public static bool HasSyncScope(ZandronumConfigSyncSettings? options) =>
        options?.SyncWholeFile == true || options?.SelectedSections?.Any(section => !string.IsNullOrWhiteSpace(section)) == true;

    /// <summary>
    /// Identifies sensible starting points for users who want the usual
    /// controls and gameplay settings without having to select every
    /// mod-specific INI section by hand.
    /// </summary>
    public static bool IsCommonSettingsSection(string sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
            return false;

        return sectionName.Equals("GlobalSettings", StringComparison.OrdinalIgnoreCase) ||
               sectionName.EndsWith(".Player", StringComparison.OrdinalIgnoreCase) ||
               sectionName.EndsWith(".ConsoleVariables", StringComparison.OrdinalIgnoreCase) ||
               sectionName.EndsWith(".ConsoleAliases", StringComparison.OrdinalIgnoreCase) ||
               sectionName.EndsWith(".Bindings", StringComparison.OrdinalIgnoreCase) ||
               sectionName.EndsWith(".DoubleBindings", StringComparison.OrdinalIgnoreCase) ||
               sectionName.EndsWith(".AutomapBindings", StringComparison.OrdinalIgnoreCase);
    }

    private async Task MonitorLaunchedProcessAsync(
        Process process,
        int processId,
        string sourceConfigurationPath)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);

            // Zandronum normally flushes its INI before process exit. A short
            // grace period also covers a slow network/antivirus file-close on
            // Windows without polling or holding the UI thread.
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);

            var options = SettingsService.Instance.Settings.ZandronumConfigSync;
            if (!options.AutoSyncEnabled)
            {
                _logger.Verbose("Configuration sync was disabled before the launched Zandronum process exited.");
                return;
            }

            var result = await SynchronizeFromConfigurationAsync(sourceConfigurationPath, options).ConfigureAwait(false);
            if (result.UpdatedFileCount > 0)
            {
                _logger.Info(
                    $"Synchronized Zandronum configuration to {result.UpdatedFileCount} version{(result.UpdatedFileCount == 1 ? string.Empty : "s")} after process exit.");
            }
            else if (result.Errors.Count > 0)
            {
                _logger.Warning($"Zandronum configuration sync completed with errors: {string.Join("; ", result.Errors)}");
            }
            else
            {
                _logger.Verbose("Zandronum configuration sync found no changes to apply after process exit.");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Zandronum configuration sync monitor failed: {ex.Message}");
        }
        finally
        {
            lock (_trackedProcessLock)
                _trackedProcessIds.Remove(processId);

            process.Dispose();
        }
    }

    private ZandronumConfigSyncResult SynchronizeCore(
        string sourceConfigurationPath,
        ZandronumConfigSyncScope scope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceConfigurationPath) || !File.Exists(sourceConfigurationPath))
        {
            return new ZandronumConfigSyncResult(0, 0, 0, ["The source Zandronum configuration file does not exist."]);
        }

        IniTextFile source;
        IReadOnlyList<IniSectionBlock> sourceSections;
        try
        {
            source = ReadIniText(sourceConfigurationPath);
            sourceSections = ParseIni(source.Content);
        }
        catch (Exception ex)
        {
            return new ZandronumConfigSyncResult(0, 0, 0, [$"Could not read source configuration: {ex.Message}"]);
        }

        var targetConfigurations = DiscoverConfigurations()
            .Where(configuration => configuration.ConfigurationExists)
            .Where(configuration => !PathsEqual(configuration.ConfigurationPath, sourceConfigurationPath))
            .ToList();

        var updated = 0;
        var unchanged = 0;
        var errors = new List<string>();

        foreach (var target in targetConfigurations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var targetText = ReadIniText(target.ConfigurationPath);
                var replacement = scope.SyncWholeFile
                    ? source.Content
                    : MergeSelectedSections(source.Content, sourceSections, targetText.Content, scope.SelectedSections);

                if (string.Equals(replacement, targetText.Content, StringComparison.Ordinal))
                {
                    unchanged++;
                    continue;
                }

                WriteTextWithBackup(target.ConfigurationPath, replacement, targetText.Encoding);
                updated++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetDirectoryName(target.ConfigurationPath) ?? target.ConfigurationPath}: {ex.Message}");
            }
        }

        var skipped = DiscoverConfigurations().Count(configuration => !configuration.ConfigurationExists);
        return new ZandronumConfigSyncResult(updated, unchanged, skipped, errors);
    }

    private static ZandronumConfigComparison CompareOne(
        string sourceText,
        IReadOnlyList<IniSectionBlock> sourceSections,
        string targetConfigurationPath,
        ZandronumConfigSyncSettings options)
    {
        if (string.IsNullOrWhiteSpace(targetConfigurationPath) || !File.Exists(targetConfigurationPath))
        {
            return new ZandronumConfigComparison(targetConfigurationPath, 0, "Configuration file is missing.");
        }

        try
        {
            var targetText = ReadIniText(targetConfigurationPath).Content;
            if (options.SyncWholeFile)
            {
                var differenceCount = CountWholeFileDifferences(sourceText, sourceSections, targetText);
                return new ZandronumConfigComparison(targetConfigurationPath, differenceCount, null);
            }

            var targetSections = ParseIni(targetText);
            var differenceCountSelective = CountSelectiveDifferences(
                sourceSections,
                targetSections,
                options.SelectedSections);
            return new ZandronumConfigComparison(targetConfigurationPath, differenceCountSelective, null);
        }
        catch (Exception ex)
        {
            return new ZandronumConfigComparison(targetConfigurationPath, 0, ex.Message);
        }
    }

    private static int CountWholeFileDifferences(
        string sourceText,
        IReadOnlyList<IniSectionBlock> sourceSections,
        string targetText)
    {
        if (string.Equals(sourceText, targetText, StringComparison.Ordinal))
            return 0;

        var targetSections = ParseIni(targetText);
        var allNames = sourceSections.Select(section => section.Name)
            .Concat(targetSections.Select(section => section.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var differentSectionCount = allNames.Count(name =>
            !SectionContentEquals(FindSection(sourceSections, name), FindSection(targetSections, name)));

        // Comments, preamble, and trailing whitespace can differ even when
        // every named section happens to match. The UI should still show that
        // full-file sync would modify the target.
        return Math.Max(1, differentSectionCount);
    }

    private static int CountSelectiveDifferences(
        IReadOnlyList<IniSectionBlock> sourceSections,
        IReadOnlyList<IniSectionBlock> targetSections,
        IEnumerable<string>? selectedSections)
    {
        var selected = new HashSet<string>(
            (selectedSections ?? []).Where(section => !string.IsNullOrWhiteSpace(section)).Select(section => section.Trim()),
            StringComparer.OrdinalIgnoreCase);

        return sourceSections
            .Where(section => selected.Contains(section.Name))
            .Count(sourceSection => !SectionContentEquals(sourceSection, FindSection(targetSections, sourceSection.Name)));
    }

    private static string MergeSelectedSections(
        string sourceText,
        IReadOnlyList<IniSectionBlock> sourceSections,
        string targetText,
        IEnumerable<string>? selectedSections)
    {
        var selected = new HashSet<string>(
            (selectedSections ?? []).Where(section => !string.IsNullOrWhiteSpace(section)).Select(section => section.Trim()),
            StringComparer.OrdinalIgnoreCase);

        if (selected.Count == 0)
            return targetText;

        var targetLines = SplitLines(targetText);
        var lineEnding = GetLineEnding(targetText);

        foreach (var sourceSection in sourceSections.Where(section => selected.Contains(section.Name)))
        {
            var currentTargetSections = ParseIni(targetLines);
            var targetSection = FindSection(currentTargetSections, sourceSection.Name);
            var sourceLines = sourceSection.GetLines();

            if (targetSection == null)
            {
                if (targetLines.Count > 0 && !string.IsNullOrEmpty(targetLines[^1]))
                    targetLines.Add(string.Empty);

                targetLines.AddRange(sourceLines);
                continue;
            }

            if (sourceLines.SequenceEqual(targetSection.GetLines(), StringComparer.Ordinal))
                continue;

            targetLines.RemoveRange(targetSection.StartLineIndex, targetSection.LineCount);
            targetLines.InsertRange(targetSection.StartLineIndex, sourceLines);
        }

        return string.Join(lineEnding, targetLines);
    }

    private static bool SectionContentEquals(IniSectionBlock? left, IniSectionBlock? right)
    {
        if (left == null || right == null)
            return left == right;

        return left.GetLines().SequenceEqual(right.GetLines(), StringComparer.Ordinal);
    }

    private static IniSectionBlock? FindSection(IEnumerable<IniSectionBlock> sections, string name) =>
        sections.FirstOrDefault(section => section.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<IniSectionBlock> ParseIni(string content) => ParseIni(SplitLines(content));

    private static IReadOnlyList<IniSectionBlock> ParseIni(IReadOnlyList<string> lines)
    {
        var sections = new List<IniSectionBlock>();
        string? currentName = null;
        var currentStart = -1;

        for (var index = 0; index < lines.Count; index++)
        {
            var match = SectionHeaderRegex.Match(lines[index]);
            if (!match.Success)
                continue;

            if (currentName != null)
                sections.Add(new IniSectionBlock(currentName, currentStart, index, lines));

            currentName = match.Groups["name"].Value.Trim();
            currentStart = index;
        }

        if (currentName != null)
            sections.Add(new IniSectionBlock(currentName, currentStart, lines.Count, lines));

        return sections;
    }

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();

    private static string GetLineEnding(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static IniTextFile ReadIniText(string path) =>
        new(File.ReadAllText(path), DetectEncoding(path));

    private static Encoding DetectEncoding(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[4];
        var count = stream.Read(header);

        if (count >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        if (count >= 2 && header[0] == 0xFF && header[1] == 0xFE)
            return Encoding.Unicode;
        if (count >= 2 && header[0] == 0xFE && header[1] == 0xFF)
            return Encoding.BigEndianUnicode;

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static void WriteTextWithBackup(string targetPath, string content, Encoding encoding)
    {
        var temporaryPath = targetPath + ".zscape-sync.tmp";
        var backupPath = GetBackupPath(targetPath);

        try
        {
            File.WriteAllText(temporaryPath, content, encoding);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(targetPath, backupPath, overwrite: false);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // A leftover temporary file is safer than masking the original
                // write error. The next successful sync will overwrite it.
            }
        }
    }

    private static string GetBackupPath(string targetPath)
    {
        var targetDirectory = Path.GetDirectoryName(targetPath);
        var directoryLabel = SanitizePathSegment(Path.GetFileName(targetDirectory));
        var normalizedPath = NormalizePath(targetPath) ?? targetPath;
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))[..12].ToLowerInvariant();
        var backupDirectory = Path.Combine(
            PathResolver.GetConfigurationSyncBackupsPath(),
            $"{directoryLabel}-{pathHash}");
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var fileName = Path.GetFileName(targetPath);

        return Path.Combine(backupDirectory, $"{timestamp}-{fileName}");
    }

    private static string SanitizePathSegment(string? value)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? "Zandronum" : value;
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(candidate.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character).ToArray()).Trim();

        return string.IsNullOrEmpty(sanitized) ? "Zandronum" : sanitized;
    }

    private static ZandronumConfigurationVersion CreateConfigurationVersion(
        string displayName,
        string executablePath)
    {
        var configurationPath = GetConfigurationPathForExecutable(executablePath) ?? string.Empty;
        var exists = !string.IsNullOrEmpty(configurationPath) && File.Exists(configurationPath);
        FileInfo? fileInfo = null;

        if (exists)
        {
            try
            {
                fileInfo = new FileInfo(configurationPath);
            }
            catch
            {
                // The configuration is still presented as existing; its detail
                // columns simply fall back to their neutral values.
            }
        }

        return new ZandronumConfigurationVersion(
            displayName,
            executablePath,
            configurationPath,
            exists,
            fileInfo?.LastWriteTimeUtc,
            fileInfo?.Length ?? 0);
    }

    private static IEnumerable<string> EnumerateManagedZandronumExecutables(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            yield break;

        var executableNames = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "zandronum.exe" }
            : new[] { "zandronum", "zandronum.x86_64" };

        IEnumerable<string>? installationDirectories = null;
        try
        {
            installationDirectories = Directory.EnumerateDirectories(rootPath, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            yield break;
        }

        using var enumerator = installationDirectories.GetEnumerator();
        while (true)
        {
            string? installationDirectory;
            try
            {
                if (!enumerator.MoveNext())
                    break;

                installationDirectory = enumerator.Current;
            }
            catch
            {
                // One inaccessible version directory should not prevent the
                // manager from showing the rest of the configured root.
                break;
            }

            foreach (var executableName in executableNames)
            {
                var executablePath = Path.Combine(installationDirectory, executableName);
                if (File.Exists(executablePath))
                {
                    yield return executablePath;
                    break;
                }

                // macOS app bundles keep the executable at this predictable
                // location while still remaining one managed version folder.
                var appBundleExecutablePath = Path.Combine(
                    installationDirectory,
                    "Contents",
                    "MacOS",
                    executableName);
                if (File.Exists(appBundleExecutablePath))
                {
                    yield return appBundleExecutablePath;
                    break;
                }
            }
        }
    }

    private static string GetVersionFolderLabel(string executablePath, string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return Path.GetFileName(Path.GetDirectoryName(executablePath)) ?? "Unknown";

        try
        {
            var relativeDirectory = Path.GetRelativePath(
                rootPath,
                Path.GetDirectoryName(executablePath) ?? rootPath);
            if (!relativeDirectory.StartsWith("..", StringComparison.Ordinal))
            {
                var firstSegment = relativeDirectory.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstSegment))
                    return firstSegment;
            }
        }
        catch
        {
        }

        return Path.GetFileName(Path.GetDirectoryName(executablePath)) ?? "Unknown";
    }

    private static ZandronumConfigSyncScope CreateScopeSnapshot(ZandronumConfigSyncSettings? options) =>
        new(
            options?.SyncWholeFile == true,
            (options?.SelectedSections ?? [])
                .Where(section => !string.IsNullOrWhiteSpace(section))
                .Select(section => section.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());

    private static string? NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        var normalizedLeft = string.IsNullOrWhiteSpace(left) ? null : NormalizePath(left);
        var normalizedRight = string.IsNullOrWhiteSpace(right) ? null : NormalizePath(right);
        return normalizedLeft != null && normalizedRight != null &&
               string.Equals(normalizedLeft, normalizedRight, GetPathComparison());
    }

    private static StringComparer GetPathComparer() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison GetPathComparison() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record IniTextFile(string Content, Encoding Encoding);

    private sealed record IniSectionBlock(
        string Name,
        int StartLineIndex,
        int EndLineIndex,
        IReadOnlyList<string> AllLines)
    {
        public int LineCount => EndLineIndex - StartLineIndex;

        public IReadOnlyList<string> GetLines() =>
            AllLines.Skip(StartLineIndex).Take(LineCount).ToArray();
    }

    private sealed record ZandronumConfigSyncScope(bool SyncWholeFile, IReadOnlyList<string> SelectedSections)
    {
        public bool HasContent => SyncWholeFile || SelectedSections.Count > 0;
    }
}

/// <summary>
/// A Zandronum executable and the per-user INI stored beside it.
/// </summary>
public sealed record ZandronumConfigurationVersion(
    string DisplayName,
    string ExecutablePath,
    string ConfigurationPath,
    bool ConfigurationExists,
    DateTime? LastWriteTimeUtc,
    long ConfigurationSize)
{
    public string StatusDisplay => ConfigurationExists ? "Ready" : "INI missing";

    public string LastModifiedDisplay => LastWriteTimeUtc?.ToLocalTime().ToString("g") ?? "-";

    public string ConfigurationSizeDisplay => ConfigurationExists
        ? $"{Math.Max(0, ConfigurationSize) / 1024d:F1} KB"
        : "-";
}

/// <summary>
/// Preview information for one potential configuration synchronization target.
/// </summary>
public sealed record ZandronumConfigComparison(
    string TargetConfigurationPath,
    int DifferentSectionCount,
    string? ErrorMessage);

/// <summary>
/// Result of copying one version's configuration scope to all other known
/// Zandronum versions.
/// </summary>
public sealed record ZandronumConfigSyncResult(
    int UpdatedFileCount,
    int UnchangedFileCount,
    int SkippedFileCount,
    IReadOnlyList<string> Errors);
