using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZScape.Utilities;

/// <summary>
/// Opens the user's file manager with a file selected whenever the current
/// platform supports it. The Linux implementation falls back to the file's
/// containing folder when the desktop file-manager service is unavailable.
/// </summary>
public static class FileManagerService
{
    /// <summary>
    /// Reveals an existing file in the system file manager without blocking the
    /// caller's UI thread.
    /// </summary>
    public static Task<FileManagerOperationResult> LocateFileAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Task.FromResult(FileManagerOperationResult.Failure(
                "No file was selected."));
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileManagerOperationResult.Failure(
                $"The file path is invalid: {ex.Message}"));
        }

        if (!File.Exists(fullPath))
        {
            return Task.FromResult(FileManagerOperationResult.Failure(
                "The selected file no longer exists."));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Explorer's /select switch reveals the containing folder and
            // selects the file rather than merely opening an arbitrary folder.
            return Task.FromResult(TryStart(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{fullPath}\"",
                UseShellExecute = true
            }));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "open",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-R");
            startInfo.ArgumentList.Add(fullPath);
            return Task.FromResult(TryStart(startInfo));
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return LocateFileOnLinuxAsync(fullPath);

        return Task.FromResult(OpenContainingFolder(fullPath));
    }

    private static async Task<FileManagerOperationResult> LocateFileOnLinuxAsync(string fullPath)
    {
        // FileManager1 lets compatible desktop environments reveal a specific
        // file. If there is no compatible session service, xdg-open still gives
        // the user the useful fallback of its containing folder.
        var fileUri = new Uri(fullPath).AbsoluteUri;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dbus-send",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("--session");
        startInfo.ArgumentList.Add("--dest=org.freedesktop.FileManager1");
        startInfo.ArgumentList.Add("--type=method_call");
        startInfo.ArgumentList.Add("--reply-timeout=1500");
        startInfo.ArgumentList.Add("/org/freedesktop/FileManager1");
        startInfo.ArgumentList.Add("org.freedesktop.FileManager1.ShowItems");
        startInfo.ArgumentList.Add($"array:string:{fileUri}");
        startInfo.ArgumentList.Add("string:");

        try
        {
            using var process = Process.Start(startInfo);
            if (process != null)
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                if (process.ExitCode == 0)
                    return FileManagerOperationResult.Success();
            }
        }
        catch
        {
            // Falling back to xdg-open is intentional: not every desktop
            // exposes FileManager1 or ships dbus-send.
        }

        return OpenContainingFolder(fullPath);
    }

    private static FileManagerOperationResult OpenContainingFolder(string fullPath)
    {
        var folder = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return FileManagerOperationResult.Failure(
                "The selected file's containing folder no longer exists.");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return TryStart(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }

        var launcher = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open";
        var startInfo = new ProcessStartInfo
        {
            FileName = launcher,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(folder);
        return TryStart(startInfo);
    }

    private static FileManagerOperationResult TryStart(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
            return process == null
                ? FileManagerOperationResult.Failure("The system file manager could not be started.")
                : FileManagerOperationResult.Success();
        }
        catch (Exception ex)
        {
            return FileManagerOperationResult.Failure(
                $"The system file manager could not be started: {ex.Message}");
        }
    }
}

/// <summary>Outcome of a best-effort system file-manager operation.</summary>
public sealed record FileManagerOperationResult(bool Succeeded, string? ErrorMessage)
{
    public static FileManagerOperationResult Success() => new(true, null);

    public static FileManagerOperationResult Failure(string errorMessage) => new(false, errorMessage);
}
