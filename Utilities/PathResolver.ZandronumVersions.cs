namespace ZScape.Utilities;

public enum ZandronumExecutablePlatform
{
    Windows,
    Linux,
    MacOS,
    Other
}

public sealed record ManagedZandronumVersion(string DirectoryPath, string? ExecutablePath)
{
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(DirectoryPath));
    public bool IsInstalled => ExecutablePath != null;
    public string? ExecutableDirectory => ExecutablePath == null ? null : Path.GetDirectoryName(ExecutablePath);
}

public static partial class PathResolver
{
    public static ZandronumExecutablePlatform CurrentExecutablePlatform =>
        OperatingSystem.IsWindows() ? ZandronumExecutablePlatform.Windows
        : OperatingSystem.IsMacOS() ? ZandronumExecutablePlatform.MacOS
        : OperatingSystem.IsLinux() ? ZandronumExecutablePlatform.Linux
        : ZandronumExecutablePlatform.Other;

    public static bool IsZandronumExecutableFileName(string name, ZandronumExecutablePlatform? platform = null) =>
        GetExecutableNames(platform ?? CurrentExecutablePlatform).Contains(name,
            (platform ?? CurrentExecutablePlatform) == ZandronumExecutablePlatform.Windows
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static string[] GetExecutableNames(ZandronumExecutablePlatform platform) =>
        platform == ZandronumExecutablePlatform.Windows ? ["zandronum.exe"] : ["zandronum", "zandronum.x86_64"];

    /// <summary>
    /// Lists only immediate installation directories below an explicitly managed
    /// root. The manager may also display incomplete version folders for repair
    /// or removal; launch/configuration consumers require an executable.
    /// </summary>
    public static IReadOnlyList<ManagedZandronumVersion> GetManagedZandronumVersions(
        string? root,
        bool includeIncomplete = false,
        ZandronumExecutablePlatform? platform = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var versions = new List<ManagedZandronumVersion>();
        foreach (var directory in GetInstallationSubdirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var executable = FindZandronumExecutable(directory, platform, cancellationToken: cancellationToken);
            if (executable != null || (includeIncomplete && LooksLikeVersionFolder(directory)))
                versions.Add(new ManagedZandronumVersion(directory, executable));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return versions.OrderBy(version => version.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Resolves the executable in one installation, using direct files, one
    /// archive wrapper directory, or predictable macOS bundle paths. Backups and
    /// arbitrary deeper directory trees are not executable candidates.
    /// </summary>
    public static string? FindZandronumExecutable(
        string? installationDirectory,
        ZandronumExecutablePlatform? platform = null,
        string? executableName = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(installationDirectory) || !Directory.Exists(installationDirectory)
            || IsNonInstallationDirectory(installationDirectory))
            return null;

        var targetPlatform = platform ?? CurrentExecutablePlatform;
        var names = executableName != null ? new[] { executableName } : GetExecutableNames(targetPlatform);

        string? FindInDirectory(string directory)
        {
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, name);
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        string? FindAtInstallationLevel(string directory) => FindInDirectory(directory)
            ?? (targetPlatform == ZandronumExecutablePlatform.MacOS
                ? FindInDirectory(Path.Combine(directory, "Contents", "MacOS")) : null);

        var direct = FindAtInstallationLevel(installationDirectory);
        if (direct != null)
            return direct;
        foreach (var wrapper in GetInstallationSubdirectories(installationDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var executable = FindAtInstallationLevel(wrapper);
            if (executable != null)
                return executable;
        }
        return null;
    }

    private static IReadOnlyList<string> GetInstallationSubdirectories(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return [];
        try
        {
            return Directory.GetDirectories(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                })
                .Where(directory => !IsNonInstallationDirectory(directory))
                .OrderBy(directory => directory, DirectoryPathComparer)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    private static bool IsNonInstallationDirectory(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.StartsWith('.')
            || name.Equals("Backup", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Backups", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Backup-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Backup_", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Screenshots", StringComparison.OrdinalIgnoreCase)
            || name.Equals("artifacts", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeVersionFolder(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (name.StartsWith("Zandronum", StringComparison.OrdinalIgnoreCase))
            name = name["Zandronum".Length..].TrimStart(' ', '-');
        return name.Length > 2 && char.IsDigit(name[0]) && name.Contains('.');
    }
}
