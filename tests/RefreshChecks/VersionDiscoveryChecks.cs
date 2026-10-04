using ZScape.Services;
using ZScape.Models;
using ZScape.Utilities;

internal static class VersionDiscoveryChecks
{
    public static void Run()
    {
        using var fixture = new TestDirectory();

        string AddExecutable(params string[] parts)
        {
            var path = fixture.GetPath(parts);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "discovery fixture, not an executable");
            return path;
        }

        var windows = AddExecutable("platforms", "windows", "zandronum.exe");
        var windowsWrapper = AddExecutable("platforms", "wrapped-windows", "release", "zandronum.exe");
        var linux = AddExecutable("platforms", "linux", "zandronum");
        AddExecutable("platforms", "linux", "zandronum.x86_64");
        var linux64 = AddExecutable("platforms", "linux64", "zandronum.x86_64");
        var bundle = AddExecutable("platforms", "Zandronum.app", "Contents", "MacOS", "zandronum");
        var wrappedBundle = AddExecutable("platforms", "wrapped-mac", "Zandronum.app", "Contents", "MacOS", "zandronum");

        RegressionAssert.That(PathResolver.FindZandronumExecutable(fixture.GetPath("platforms", "windows"),
                ZandronumExecutablePlatform.Windows) == windows
            && PathResolver.FindZandronumExecutable(fixture.GetPath("platforms", "wrapped-windows"),
                ZandronumExecutablePlatform.Windows) == windowsWrapper,
            "Windows discovery supports direct executables and one archive wrapper");
        RegressionAssert.That(PathResolver.FindZandronumExecutable(fixture.GetPath("platforms", "linux"),
                ZandronumExecutablePlatform.Linux) == linux
            && PathResolver.FindZandronumExecutable(fixture.GetPath("platforms", "linux64"),
                ZandronumExecutablePlatform.Linux) == linux64,
            "Linux discovery supports both binary names, preferring the direct default name");
        RegressionAssert.That(PathResolver.FindZandronumExecutable(fixture.GetPath("platforms", "Zandronum.app"),
                ZandronumExecutablePlatform.MacOS) == bundle
            && PathResolver.FindZandronumExecutable(fixture.GetPath("platforms", "wrapped-mac"),
                ZandronumExecutablePlatform.MacOS) == wrappedBundle,
            "macOS discovery supports a bundle root and a bundled archive wrapper");
        RegressionAssert.That(PathResolver.FindZandronumExecutable(fixture.GetPath("platforms", "linux"),
                ZandronumExecutablePlatform.Windows) == null
            && PathResolver.FindZandronumExecutable(fixture.GetPath("platforms", "windows"),
                ZandronumExecutablePlatform.Linux) == null,
            "Managed discovery does not select another platform's executable name");
        RegressionAssert.That(PathResolver.FindZandronumExecutable(fixture.GetPath("platforms", "linux64"),
                ZandronumExecutablePlatform.Linux, executableName: "zandronum") == null,
            "Stable-release discovery can require the manifest's exact executable name");
        RegressionAssert.That(PathResolver.IsZandronumExecutableFileName("ZANDRONUM.EXE", ZandronumExecutablePlatform.Windows)
            && !PathResolver.IsZandronumExecutableFileName("ZANDRONUM", ZandronumExecutablePlatform.Linux)
            && PathResolver.IsZandronumExecutableFileName("zandronum.x86_64", ZandronumExecutablePlatform.Linux),
            "Installation notifications share the platform-specific executable naming rule");

        var testingRoot = fixture.GetPath("managed-testing");
        var direct = AddExecutable("managed-testing", "3.3-direct", "zandronum.exe");
        var wrapped = AddExecutable("managed-testing", "3.3-wrapped", "release", "zandronum.exe");
        AddExecutable("managed-testing", "Backup-3.3", "zandronum.exe");
        AddExecutable("managed-testing", "Backups", "3.3", "zandronum.exe");
        AddExecutable("managed-testing", ".old-version", "zandronum.exe");
        AddExecutable("managed-testing", "artifacts", "zandronum.exe");
        AddExecutable("managed-testing", "3.3-deep", "assets", "nested", "zandronum.exe");
        AddExecutable("managed-testing", "3.3-backup-only", "Backup", "zandronum.exe");
        Directory.CreateDirectory(fixture.GetPath("managed-testing", "3.3-dead"));
        Directory.CreateDirectory(fixture.GetPath("managed-testing", "unrelated"));

        var installed = PathResolver.GetManagedZandronumVersions(testingRoot,
            platform: ZandronumExecutablePlatform.Windows);
        RegressionAssert.That(installed.Select(version => version.Name).SequenceEqual(new[] { "3.3-direct", "3.3-wrapped" })
            && installed.Select(version => version.ExecutablePath).SequenceEqual(new[] { direct, wrapped }),
            "Managed inventory is immediate, ordered, and retains version identity across wrappers");
        var withIncomplete = PathResolver.GetManagedZandronumVersions(testingRoot, includeIncomplete: true,
            platform: ZandronumExecutablePlatform.Windows);
        RegressionAssert.That(withIncomplete.Select(version => version.Name).SequenceEqual(
                new[] { "3.3-backup-only", "3.3-dead", "3.3-deep", "3.3-direct", "3.3-wrapped" })
            && withIncomplete.Count(version => !version.IsInstalled) == 3,
            "The version manager retains incomplete version folders without listing unrelated folders or backups");
        RegressionAssert.That(PathResolver.FindZandronumExecutable(fixture.GetPath("managed-testing", "3.3-deep"),
                ZandronumExecutablePlatform.Windows) == null
            && PathResolver.FindZandronumExecutable(fixture.GetPath("managed-testing", "3.3-backup-only"),
                ZandronumExecutablePlatform.Windows) == null,
            "Executable resolution never scavenges arbitrary deeper trees or a backup copy");
        RegressionAssert.That(PathResolver.GetManagedZandronumVersions(fixture.GetPath("missing-root")).Count == 0
            && PathResolver.FindZandronumExecutable(fixture.GetPath("missing-root")) == null
            && PathResolver.GetManagedZandronumVersions(null).Count == 0,
            "Unconfigured and unavailable managed roots remain harmless");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledInventory = false;
        var canceledExecutable = false;
        try { PathResolver.GetManagedZandronumVersions(testingRoot, cancellationToken: cancellation.Token); }
        catch (OperationCanceledException) { canceledInventory = true; }
        try { PathResolver.FindZandronumExecutable(testingRoot, cancellationToken: cancellation.Token); }
        catch (OperationCanceledException) { canceledExecutable = true; }
        RegressionAssert.That(canceledInventory && canceledExecutable,
            "Canceled managed discovery does not return stale installation results");

        var stable = AddExecutable("configured-stable", "zandronum.exe");
        var archivedStable = AddExecutable("configured-stable", "StableVersions", "3.2", "zandronum.exe");
        var lastLaunch = AddExecutable("external-launch", "zandronum.exe");
        var settings = new AppSettings
        {
            ZandronumPath = stable,
            ZandronumTestingPath = testingRoot,
            LastLaunchGameConfig = new LaunchGameConfig { ExePath = lastLaunch }
        };
        var screenshotSources = ScreenshotMonitorService.GetScreenshotSourceDirectories(settings,
            ZandronumExecutablePlatform.Windows);
        RegressionAssert.That(screenshotSources.Contains(Path.GetDirectoryName(stable)!, PathResolver.DirectoryPathComparer)
            && screenshotSources.Contains(Path.GetDirectoryName(direct)!, PathResolver.DirectoryPathComparer)
            && screenshotSources.Contains(Path.GetDirectoryName(wrapped)!, PathResolver.DirectoryPathComparer)
            && screenshotSources.Contains(fixture.GetPath("managed-testing", "3.3-wrapped"), PathResolver.DirectoryPathComparer)
            && screenshotSources.Contains(fixture.GetPath("managed-testing", "3.3-dead"), PathResolver.DirectoryPathComparer),
            "Screenshot consumers share managed discovery while retaining existing and executable working directories");
        RegressionAssert.That(!screenshotSources.Contains(Path.GetDirectoryName(archivedStable)!, PathResolver.DirectoryPathComparer)
            && !screenshotSources.Contains(Path.GetDirectoryName(lastLaunch)!, PathResolver.DirectoryPathComparer)
            && !screenshotSources.Contains(fixture.GetPath("managed-testing", "Backup-3.3"), PathResolver.DirectoryPathComparer)
            && !screenshotSources.Contains(fixture.GetPath("managed-testing", "unrelated"), PathResolver.DirectoryPathComparer)
            && screenshotSources.Count == screenshotSources.Distinct(PathResolver.DirectoryPathComparer).Count(),
            "Screenshot monitoring remains limited to configured stable/testing scope without duplicate watchers");
    }
}
