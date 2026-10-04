using ZScape.Services;
using ZScape.Utilities;

internal static class WadPathChecks
{
    public static void Run()
    {
        using var fixture = new TestDirectory();
        var first = fixture.GetPath("first");
        var second = fixture.GetPath("second");
        var offline = fixture.GetPath("offline");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(System.IO.Path.Combine(first, "MixedCase.PK3"), "first");
        File.WriteAllText(System.IO.Path.Combine(second, "MixedCase.PK3"), "second");
        var nested = System.IO.Path.Combine(first, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(System.IO.Path.Combine(nested, "nested.pk3"), "nested");

        var manager = new WadManager();
        manager.SetSearchPaths([offline, first, first + System.IO.Path.DirectorySeparatorChar, second]);
        RegressionAssert.That(manager.SearchPaths.SequenceEqual(new[] { offline, first, second }),
            "Configured WAD roots retain unavailable paths and explicit order, without slash duplicates");
        manager.RefreshCache();
        RegressionAssert.That(manager.GetSearchRootsInPriorityOrder().SequenceEqual(new[] { first, second }),
            "Availability affects effective discovery roots, not configured roots");
        RegressionAssert.That(manager.FindWad("mixedcase.pk3") == System.IO.Path.Combine(first, "MixedCase.PK3"),
            "First configured root wins with case-insensitive WAD filenames");
        RegressionAssert.That(manager.FindWad("nested.pk3") is null,
            "Join filename discovery remains top-level only");

        manager.SetSearchPaths([second, offline, first]);
        manager.RefreshCache();
        RegressionAssert.That(manager.FindWad("MIXEDCASE.PK3") == System.IO.Path.Combine(second, "MixedCase.PK3"),
            "Reordering configured roots changes duplicate filename precedence");
        Directory.CreateDirectory(offline);
        File.WriteAllText(System.IO.Path.Combine(offline, "reconnected.wad"), "available");
        RegressionAssert.That(manager.FindWad("reconnected.wad") == System.IO.Path.Combine(offline, "reconnected.wad"),
            "A configured root becomes usable again without re-adding it");
        manager.RemoveSearchPath(first + System.IO.Path.DirectorySeparatorChar);
        RegressionAssert.That(manager.SearchPaths.SequenceEqual(new[] { second, offline }),
            "Removing a slash variant removes the same configured directory");

        var root = System.IO.Path.GetPathRoot(first)!;
        RegressionAssert.That(PathResolver.NormalizeDirectoryPath(root) == root,
            "Directory normalization preserves filesystem roots");
        RegressionAssert.That(PathResolver.DirectoryPathsEqual(first, first.ToUpperInvariant()) == OperatingSystem.IsWindows(),
            "Directory identity follows platform casing while filenames remain case-insensitive");

        var settings = new SettingsService(fixture.Path);
        settings.Settings.WadSearchPaths = [second, fixture.GetPath("disconnected"), first, first + System.IO.Path.DirectorySeparatorChar];
        settings.Save();
        var reloaded = new SettingsService(fixture.Path);
        RegressionAssert.That(reloaded.Settings.WadSearchPaths.SequenceEqual(
                new[] { second, fixture.GetPath("disconnected"), first }),
            "Settings round-trip retains root order and unavailable paths, normalizing duplicates");

        settings.Settings.WadSearchPaths.Clear();
        settings.Settings.WadDownloadPath = first;
        settings.Save();
        RegressionAssert.That(new SettingsService(fixture.Path).Settings.WadSearchPaths.Count == 0,
            "An intentionally empty search-path list stays empty even with a configured download folder");
        manager.SetSearchPaths([]);
        manager.RefreshCache();
        RegressionAssert.That(manager.SearchPaths.Count == 0,
            "Refreshing effective WAD discovery cannot repopulate an empty configured list");
    }
}
