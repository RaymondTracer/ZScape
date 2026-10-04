using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using ZScape.Utilities;

namespace ZScape.Services;

/// <summary>
/// Extracts installer archives under one destination policy. Complete entries
/// replace their targets; cancelled or failed entries leave no partial target.
/// </summary>
internal static class ArchiveExtractionService
{
    public static void Extract(
        string archivePath,
        string destinationDirectory,
        ArchiveExtractionFormat format,
        Action<ArchiveExtractionProgress>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (format == ArchiveExtractionFormat.Zip)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            ExtractEntries(archive.Entries.Select(entry => new ArchiveEntry(
                entry.FullName,
                entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'),
                entry.Length,
                entry.Open,
                entry.LastWriteTime.LocalDateTime, null)).ToList(),
                destinationDirectory, reportProgress, cancellationToken);
            return;
        }

        using var compressedArchive = ArchiveFactory.Open(archivePath);
        if (compressedArchive.Type == ArchiveType.Tar)
        {
            ExtractTarArchive(archivePath, gzipCompressed: false, destinationDirectory, reportProgress, cancellationToken);
            return;
        }
        if (compressedArchive.Type == ArchiveType.GZip && IsGZipTarArchive(archivePath))
        {
            ExtractTarArchive(archivePath, gzipCompressed: true, destinationDirectory, reportProgress, cancellationToken);
            return;
        }
        ExtractEntries(compressedArchive.Entries.Select(entry => new ArchiveEntry(
            entry.Key ?? string.Empty, entry.IsDirectory, entry.Size, entry.OpenEntryStream, null, entry.LinkTarget)).ToList(),
            destinationDirectory, reportProgress, cancellationToken);
    }

    private static bool IsGZipTarArchive(string archivePath)
    {
        using var input = OpenTarStream(archivePath, gzipCompressed: true);
        using var reader = new TarReader(input);
        try
        {
            return reader.GetNextEntry() != null;
        }
        catch (InvalidDataException)
        {
            // Ordinary single-file gzip archives retain the SharpCompress reader.
            return false;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    private static Stream OpenTarStream(string archivePath, bool gzipCompressed)
    {
        var input = File.OpenRead(archivePath);
        return gzipCompressed ? new GZipStream(input, CompressionMode.Decompress) : input;
    }

    private static void ExtractTarArchive(
        string archivePath,
        bool gzipCompressed,
        string destinationDirectory,
        Action<ArchiveExtractionProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        // TarReader consumes PAX/GNU metadata itself, returning only payload
        // entries. Scan metadata first so destination validation still precedes
        // all writes, then stream each payload through the common materializer.
        using var payloadInput = OpenTarStream(archivePath, gzipCompressed);
        using var payloadReader = new TarReader(payloadInput);
        var payloadIndex = -1;
        TarEntry? currentPayload = null;
        Stream OpenPayload(int entryIndex)
        {
            while (payloadIndex < entryIndex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                currentPayload = payloadReader.GetNextEntry();
                payloadIndex++;
            }
            return currentPayload?.DataStream ?? Stream.Null;
        }

        var entries = new List<ArchiveEntry>();
        using (var metadataInput = OpenTarStream(archivePath, gzipCompressed))
        using (var metadataReader = new TarReader(metadataInput))
        {
            var buffer = new byte[81920];
            TarEntry? entry;
            while ((entry = metadataReader.GetNextEntry()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryIndex = entries.Count;
                var linkTarget = entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink
                    ? entry.LinkName : null;
                entries.Add(new ArchiveEntry(entry.Name, entry.EntryType == TarEntryType.Directory,
                    entry.Length, () => OpenPayload(entryIndex), null, linkTarget));
                if (entry.DataStream == null)
                    continue;
                if (entry.DataStream.CanSeek)
                {
                    entry.DataStream.Seek(0, SeekOrigin.End);
                    continue;
                }
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.DataStream.Read(buffer, 0, buffer.Length) == 0)
                        break;
                }
            }
        }
        ExtractEntries(entries, destinationDirectory, reportProgress, cancellationToken);
    }

    /// <summary>
    /// Applies the stable installer's containment rule to either archive reader.
    /// The comparer is injectable so case-sensitive policies can be checked on Windows.
    /// </summary>
    internal static string GetSafeDestinationPath(
        string destinationDirectory,
        string entryPath,
        StringComparer? pathComparer = null)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
            throw new InvalidDataException("Archive entry path was empty.");

        // Archives may contain either separator on any host. Reject absolute
        // Windows paths on Unix too, rather than interpreting them as filenames.
        if (entryPath[0] is '/' or '\\' || Path.IsPathRooted(entryPath) ||
            (entryPath.Length >= 2 && char.IsAsciiLetter(entryPath[0]) && entryPath[1] == ':'))
            throw new InvalidDataException($"Archive entry path was absolute: {entryPath}");

        var normalizedEntryPath = entryPath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        var destinationPath = Path.GetFullPath(Path.Combine(rootPath, normalizedEntryPath));
        var expectedPrefix = Path.EndsInDirectorySeparator(rootPath)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        var comparer = pathComparer ?? PathResolver.DirectoryPathComparer;
        if (!comparer.Equals(destinationPath, rootPath) &&
            (destinationPath.Length < expectedPrefix.Length ||
             !comparer.Equals(destinationPath[..expectedPrefix.Length], expectedPrefix)))
            throw new InvalidDataException($"Archive entry would extract outside the destination directory: {entryPath}");

        return destinationPath;
    }

    private static void ExtractEntries(
        IReadOnlyList<ArchiveEntry> entries,
        string destinationDirectory,
        Action<ArchiveExtractionProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        // Validate every path before writing the first entry, including directories.
        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        var destinations = entries.Select(entry =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationPath = GetSafeDestinationPath(destinationDirectory, entry.Path);
            if (!entry.IsDirectory && PathResolver.DirectoryPathComparer.Equals(destinationPath, rootPath))
                throw new InvalidDataException($"Archive file entry resolves to the destination directory: {entry.Path}");
            return destinationPath;
        }).ToArray();
        var totalFiles = entries.Count(entry => !entry.IsDirectory);
        var completedFiles = 0;
        var buffer = new byte[81920];

        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            var destinationPath = destinations[index];
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var entryName = Path.GetFileName(destinationPath);
            reportProgress?.Invoke(new ArchiveExtractionProgress(entryName, completedFiles, totalFiles, 0, entry.Size, false));
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(entry.LinkTarget))
            {
                // Preserve SharpCompress's existing unsupported-link policy. The
                // default callback reports the link but does not write a file.
                new ExtractionOptions().WriteSymbolicLink(destinationPath, entry.LinkTarget);
            }
            else
            {
                var parentDirectory = Path.GetDirectoryName(destinationPath)!;
                Directory.CreateDirectory(parentDirectory);
                var temporaryPath = Path.Combine(parentDirectory, $".zscape-extract-{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var input = entry.OpenStream())
                    using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        long transferred = 0;
                        var lastReport = Stopwatch.StartNew();
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var bytesRead = input.Read(buffer, 0, buffer.Length);
                            if (bytesRead == 0)
                                break;
                            output.Write(buffer, 0, bytesRead);
                            transferred += bytesRead;
                            if (transferred == bytesRead || lastReport.ElapsedMilliseconds >= 100 || transferred >= entry.Size)
                            {
                                reportProgress?.Invoke(new ArchiveExtractionProgress(
                                    entryName, completedFiles, totalFiles, transferred, entry.Size, false));
                                lastReport.Restart();
                            }
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.LastWriteTime.HasValue)
                        File.SetLastWriteTime(temporaryPath, entry.LastWriteTime.Value);
                    File.Move(temporaryPath, destinationPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
            }

            completedFiles++;
            reportProgress?.Invoke(new ArchiveExtractionProgress(
                entryName, completedFiles, totalFiles, entry.Size, entry.Size, true));
        }
    }

    private sealed record ArchiveEntry(
        string Path, bool IsDirectory, long Size, Func<Stream> OpenStream, DateTime? LastWriteTime, string? LinkTarget);
}

internal enum ArchiveExtractionFormat
{
    Zip,
    SharpCompress
}

internal sealed record ArchiveExtractionProgress(
    string EntryName,
    int CompletedFiles,
    int TotalFiles,
    long BytesWritten,
    long EntryBytes,
    bool EntryCompleted)
{
    public int Percent => TotalFiles == 0 ? 100 : Math.Clamp((int)(100d *
        (CompletedFiles + (!EntryCompleted && EntryBytes > 0 ? Math.Min(1d, (double)BytesWritten / EntryBytes) : 0d)) /
        TotalFiles), 0, 100);
}
