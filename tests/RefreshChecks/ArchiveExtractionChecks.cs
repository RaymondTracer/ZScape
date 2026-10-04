using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using ZScape.Services;

internal static class ArchiveExtractionChecks
{
    public static void Run()
    {
        using var fixture = new TestDirectory();
        CheckContainment(fixture);
        foreach (var format in new[] { ArchiveExtractionFormat.Zip, ArchiveExtractionFormat.SharpCompress })
        {
            CheckNestedExtraction(fixture, format);
            CheckRejectedEntries(fixture, format);
            CheckCancellation(fixture, format);
        }
        CheckSharpLinkPolicy(fixture);
        CheckNestedExtraction(fixture, ArchiveExtractionFormat.SharpCompress, gzipCompressed: true);
    }

    private static void CheckContainment(TestDirectory fixture)
    {
        var root = fixture.GetPath("CaseRoot");
        var caseChangedPath = "../caseroot/file.bin";
        Expect<InvalidDataException>(() => ArchiveExtractionService.GetSafeDestinationPath(
            root, caseChangedPath, StringComparer.Ordinal));
        RegressionAssert.That(ArchiveExtractionService.GetSafeDestinationPath(
            root, caseChangedPath, StringComparer.OrdinalIgnoreCase).EndsWith(
                Path.Combine("caseroot", "file.bin"), StringComparison.Ordinal),
            "archive containment uses the supplied OS case policy");
        Expect<InvalidDataException>(() => ArchiveExtractionService.GetSafeDestinationPath(
            root, "../CaseRoot-other/file.bin", StringComparer.OrdinalIgnoreCase));
        RegressionAssert.That(ArchiveExtractionService.GetSafeDestinationPath(
            root, "nested\\inner/file.bin") == Path.Combine(root, "nested", "inner", "file.bin"),
            "archive containment normalizes both separator conventions");
    }

    private static void CheckNestedExtraction(TestDirectory fixture, ArchiveExtractionFormat format, bool gzipCompressed = false)
    {
        var description = gzipCompressed ? "GZip TAR" : format.ToString();
        var destination = fixture.GetPath(description + "-nested");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "replace.txt"), "old");
        var archive = CreateArchive(fixture, format,
            ("nested/inner/file.txt", Encoding.UTF8.GetBytes("nested content")),
            ("mixed\\path/another.txt", Encoding.UTF8.GetBytes("mixed content")),
            ("replace.txt", Encoding.UTF8.GetBytes("replacement")),
            ("empty.bin", Array.Empty<byte>()));
        if (gzipCompressed)
        {
            var compressedPath = archive + ".gz";
            using (var input = File.OpenRead(archive))
            using (var output = File.Create(compressedPath))
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
                input.CopyTo(gzip);
            archive = compressedPath;
        }
        var reports = new List<ArchiveExtractionProgress>();
        ArchiveExtractionService.Extract(archive, destination, format, reports.Add);
        RegressionAssert.That(
            File.ReadAllText(Path.Combine(destination, "nested", "inner", "file.txt")) == "nested content" &&
            File.ReadAllText(Path.Combine(destination, "mixed", "path", "another.txt")) == "mixed content" &&
            File.ReadAllText(Path.Combine(destination, "replace.txt")) == "replacement" &&
            new FileInfo(Path.Combine(destination, "empty.bin")).Length == 0,
            $"{description} extraction preserves nested paths, overwrite and empty files");
        RegressionAssert.That(
            reports.Count(report => report.EntryCompleted) == 4 && reports[^1].Percent == 100 &&
            reports.All(report => report.TotalFiles == 4 && report.Percent is >= 0 and <= 100) &&
            reports.Zip(reports.Skip(1)).All(pair => pair.First.Percent <= pair.Second.Percent),
            $"{description} extraction reports bounded, monotonic file progress");
        RegressionAssert.That(Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Count() == 4,
            $"{description} extracts payload files without materializing archive metadata");
        AssertNoTemporaryFiles(destination, description + " successful extraction");
    }

    private static void CheckRejectedEntries(TestDirectory fixture, ArchiveExtractionFormat format)
    {
        var rejectedPaths = new[]
        {
            "../escape.bin", "safe\\..\\..\\escape.bin", "/absolute.bin",
            "\\absolute.bin", "C:\\absolute.bin", "C:drive-relative.bin", "."
        };
        foreach (var rejectedPath in rejectedPaths)
        {
            var destination = fixture.GetPath(format + "-invalid-" + Guid.NewGuid().ToString("N"));
            var archive = CreateArchive(fixture, format,
                ("valid.txt", new byte[] { 1 }), (rejectedPath, new byte[] { 2 }));
            Expect<InvalidDataException>(() => ArchiveExtractionService.Extract(archive, destination, format));
            RegressionAssert.That(!Directory.Exists(destination),
                $"{format} validates all paths before writing: {rejectedPath}");
        }
        RegressionAssert.That(!File.Exists(fixture.GetPath("escape.bin")),
            $"{format} traversal fixtures leave no file outside the destination");
    }

    private static void CheckCancellation(TestDirectory fixture, ArchiveExtractionFormat format)
    {
        var contents = new byte[256 * 1024];
        Array.Fill(contents, (byte)37);
        var archive = CreateArchive(fixture, format,
            ("large.bin", contents), ("later.bin", new byte[] { 9 }));
        var beforeDestination = fixture.GetPath(format + "-cancel-before");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            Expect<OperationCanceledException>(() => ArchiveExtractionService.Extract(
                archive, beforeDestination, format, cancellationToken: cancellation.Token));
        }
        RegressionAssert.That(!Directory.Exists(beforeDestination),
            $"{format} pre-cancelled extraction creates no destination");

        var duringDestination = fixture.GetPath(format + "-cancel-during");
        Directory.CreateDirectory(duringDestination);
        var oldTarget = Path.Combine(duringDestination, "large.bin");
        File.WriteAllText(oldTarget, "existing target");
        using (var cancellation = new CancellationTokenSource())
        {
            Expect<OperationCanceledException>(() => ArchiveExtractionService.Extract(
                archive, duringDestination, format, report =>
                {
                    if (report.BytesWritten > 0)
                        cancellation.Cancel();
                }, cancellation.Token));
        }
        RegressionAssert.That(File.ReadAllText(oldTarget) == "existing target" &&
            !File.Exists(Path.Combine(duringDestination, "later.bin")),
            $"{format} mid-entry cancellation preserves old targets and skips later entries");
        AssertNoTemporaryFiles(duringDestination, format + " cancelled extraction");

        var completedDestination = fixture.GetPath(format + "-cancel-completed");
        using (var cancellation = new CancellationTokenSource())
        {
            Expect<OperationCanceledException>(() => ArchiveExtractionService.Extract(
                archive, completedDestination, format, report =>
                {
                    if (report.EntryCompleted)
                        cancellation.Cancel();
                }, cancellation.Token));
        }
        RegressionAssert.That(File.ReadAllBytes(Path.Combine(completedDestination, "large.bin"))
            .SequenceEqual(contents) && !File.Exists(Path.Combine(completedDestination, "later.bin")),
            $"{format} cancellation keeps fully extracted entries intact");
        AssertNoTemporaryFiles(completedDestination, format + " between-entry cancellation");

        var failedDestination = fixture.GetPath(format + "-failure");
        Directory.CreateDirectory(failedDestination);
        File.WriteAllText(Path.Combine(failedDestination, "large.bin"), "old target");
        Expect<IOException>(() => ArchiveExtractionService.Extract(archive, failedDestination, format, report =>
        {
            if (report.BytesWritten > 0)
                throw new IOException("Fixture progress callback failure");
        }));
        RegressionAssert.That(File.ReadAllText(Path.Combine(failedDestination, "large.bin")) == "old target",
            $"{format} interrupted writes preserve the previous target");
        AssertNoTemporaryFiles(failedDestination, format + " failed extraction");
    }

    private static void CheckSharpLinkPolicy(TestDirectory fixture)
    {
        var archivePath = fixture.GetPath("links.tar");
        using (var stream = File.Create(archivePath))
        using (var writer = new TarWriter(stream))
        using (var contents = new MemoryStream(new byte[] { 42 }))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "target.bin") { DataStream = contents });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link.bin") { LinkName = "target.bin" });
        }
        var destination = fixture.GetPath("sharp-links");
        ArchiveExtractionService.Extract(archivePath, destination, ArchiveExtractionFormat.SharpCompress);
        RegressionAssert.That(File.Exists(Path.Combine(destination, "target.bin")) &&
            !File.Exists(Path.Combine(destination, "link.bin")),
            "SharpCompress preserves its existing unsupported-symbolic-link policy");
    }

    private static string CreateArchive(TestDirectory fixture, ArchiveExtractionFormat format,
        params (string Name, byte[] Contents)[] entries)
    {
        var archivePath = fixture.GetPath(Guid.NewGuid().ToString("N") +
            (format == ArchiveExtractionFormat.Zip ? ".zip" : ".tar"));
        using var stream = File.Create(archivePath);
        if (format == ArchiveExtractionFormat.Zip)
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var entry in entries)
            {
                using var target = archive.CreateEntry(entry.Name).Open();
                target.Write(entry.Contents);
            }
        }
        else
        {
            using var writer = new TarWriter(stream);
            foreach (var entry in entries)
            {
                using var contents = new MemoryStream(entry.Contents, writable: false);
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entry.Name)
                {
                    DataStream = contents
                });
            }
        }
        return archivePath;
    }

    private static void AssertNoTemporaryFiles(string destination, string operation) =>
        RegressionAssert.That(!Directory.EnumerateFiles(destination, ".zscape-extract-*.tmp",
            SearchOption.AllDirectories).Any(), operation + " leaves no staged files");

    private static void Expect<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(TException).Name);
    }
}
