using System.Diagnostics;

namespace ZScape.Services;

internal sealed class MacOsNotificationClient
{
    private const string OsaScriptPath = "/usr/bin/osascript";

    public bool TryShow(NativeNotificationRequest request)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists(OsaScriptPath))
        {
            return false;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = OsaScriptPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                }
            };

            process.StartInfo.ArgumentList.Add("-e");
            process.StartInfo.ArgumentList.Add(BuildAppleScript(request));

            if (!process.Start())
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warning($"Failed to send macOS notification: {ex.Message}");
            return false;
        }
    }

    private static string BuildAppleScript(NativeNotificationRequest request)
    {
        var body = EscapeAppleScriptText(request.Detail);
        var title = EscapeAppleScriptText(request.Title);
        var subtitle = EscapeAppleScriptText(request.Message);
        return $"display notification \"{body}\" with title \"{title}\" subtitle \"{subtitle}\"";
    }

    private static string EscapeAppleScriptText(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}