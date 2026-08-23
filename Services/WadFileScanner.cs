using System.Diagnostics;
using System.Security;
using ZScape.Utilities;

namespace ZScape.Services;

/// <summary>
/// Safely enumerates supported WAD files beneath configured roots. Traversal is
/// explicit rather than using a single recursive filesystem call so it can
/// report progress, honour cancellation between directories, and avoid
/// reparse-point loops.
/// </summary>
public static class WadFileScanner
{
    private static readonly EnumerationOptions TopLevelEnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        // Reparse points are deliberately returned so the traversal can count
        // and report each one it rejects. Following them would let a configured
        // WAD root wander into a linked folder or loop back into itself.
        AttributesToSkip = 0
    };

    private const int ProgressReportIntervalMilliseconds = 150;

    /// <summary>
    /// Scans the supplied roots in order. The root itself is always allowed,
    /// even if it is a user-configured link; reparse-point directories found
    /// beneath a root are skipped to prevent recursion loops and unexpected
    /// excursions outside that root.
    /// </summary>
    public static WadFileScanResult Scan(
        IReadOnlyList<string> roots,
        Action<string>? supportedFileFound,
        IProgress<WadFileScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var issues = new List<WadFileScanIssue>();
        var directoriesVisited = 0;
        var filesExamined = 0;
        var skippedDirectories = 0;
        var skippedReparsePoints = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastProgressReport = TimeSpan.MinValue;
        var currentRoot = string.Empty;
        var currentDirectory = string.Empty;

        void ReportProgress(int rootIndex, bool force = false)
        {
            if (progress == null)
                return;

            var elapsed = stopwatch.Elapsed;
            if (!force && elapsed - lastProgressReport < TimeSpan.FromMilliseconds(ProgressReportIntervalMilliseconds))
                return;

            lastProgressReport = elapsed;
            progress.Report(new WadFileScanProgress(
                RootIndex: rootIndex,
                RootCount: roots.Count,
                CurrentRoot: currentRoot,
                CurrentDirectory: currentDirectory,
                DirectoriesVisited: directoriesVisited,
                FilesExamined: filesExamined,
                SupportedFilesFound: files.Count,
                SkippedDirectories: skippedDirectories,
                SkippedReparsePoints: skippedReparsePoints));
        }

        for (var rootIndex = 0; rootIndex < roots.Count; rootIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            currentRoot = roots[rootIndex];
            currentDirectory = currentRoot;
            if (string.IsNullOrWhiteSpace(currentRoot) || !Directory.Exists(currentRoot))
            {
                skippedDirectories++;
                issues.Add(new WadFileScanIssue(currentRoot, "The configured folder no longer exists."));
                ReportProgress(rootIndex, force: true);
                continue;
            }

            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(currentRoot);
            ReportProgress(rootIndex, force: true);

            while (pendingDirectories.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                currentDirectory = pendingDirectories.Pop();
                directoriesVisited++;
                ReportProgress(rootIndex);

                try
                {
                    foreach (var filePath in Directory.EnumerateFiles(
                        currentDirectory,
                        "*",
                        TopLevelEnumerationOptions))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        filesExamined++;

                        if (WadExtensions.IsSupportedExtension(Path.GetExtension(filePath)))
                        {
                            files.Add(filePath);
                            supportedFileFound?.Invoke(filePath);
                        }

                        ReportProgress(rootIndex);
                    }
                }
                catch (Exception ex) when (IsTraversalException(ex))
                {
                    skippedDirectories++;
                    issues.Add(new WadFileScanIssue(currentDirectory, ex.Message));
                    ReportProgress(rootIndex, force: true);
                    continue;
                }

                try
                {
                    foreach (var directoryPath in Directory.EnumerateDirectories(
                        currentDirectory,
                        "*",
                        TopLevelEnumerationOptions))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            if (File.GetAttributes(directoryPath).HasFlag(FileAttributes.ReparsePoint))
                            {
                                skippedReparsePoints++;
                                continue;
                            }

                            pendingDirectories.Push(directoryPath);
                        }
                        catch (Exception ex) when (IsTraversalException(ex))
                        {
                            skippedDirectories++;
                            issues.Add(new WadFileScanIssue(directoryPath, ex.Message));
                        }
                    }
                }
                catch (Exception ex) when (IsTraversalException(ex))
                {
                    skippedDirectories++;
                    issues.Add(new WadFileScanIssue(currentDirectory, ex.Message));
                }

                ReportProgress(rootIndex);
            }

            ReportProgress(rootIndex, force: true);
        }

        stopwatch.Stop();
        return new WadFileScanResult(
            files,
            directoriesVisited,
            filesExamined,
            skippedDirectories,
            skippedReparsePoints,
            issues,
            stopwatch.Elapsed);
    }

    private static bool IsTraversalException(Exception exception) =>
        exception is UnauthorizedAccessException
            or IOException
            or SecurityException
            or NotSupportedException
            or ArgumentException;
}

/// <summary>Live, indeterminate-progress state for a WAD filesystem scan.</summary>
public sealed record WadFileScanProgress(
    int RootIndex,
    int RootCount,
    string CurrentRoot,
    string CurrentDirectory,
    int DirectoriesVisited,
    int FilesExamined,
    int SupportedFilesFound,
    int SkippedDirectories,
    int SkippedReparsePoints);

/// <summary>One inaccessible path encountered during an otherwise valid scan.</summary>
public sealed record WadFileScanIssue(string Path, string Message);

/// <summary>Completed filesystem scan result and nonfatal traversal diagnostics.</summary>
public sealed record WadFileScanResult(
    IReadOnlyList<string> Files,
    int DirectoriesVisited,
    int FilesExamined,
    int SkippedDirectories,
    int SkippedReparsePoints,
    IReadOnlyList<WadFileScanIssue> Issues,
    TimeSpan Elapsed);
