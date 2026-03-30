using System;
using System.Runtime.InteropServices;

namespace ZScape.Utilities;

/// <summary>
/// Applies Windows dark mode to window title bars via DWM API.
/// </summary>
public static class DarkModeHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    /// <summary>
    /// Applies dark mode to the title bar of the given window handle.
    /// Only effective on Windows 10 build 18985+ and Windows 11.
    /// </summary>
    public static void ApplyDarkTitleBar(IntPtr handle, bool enabled = true)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            int value = enabled ? 1 : 0;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch
        {
            // Silently fail on older Windows versions that don't support this attribute
        }
    }
}
