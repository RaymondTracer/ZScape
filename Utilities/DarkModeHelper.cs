using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ZScape.Utilities;

/// <summary>
/// Utility class for applying Windows dark mode to window title bars.
/// </summary>
public static class DarkModeHelper
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>
    /// Detects if Windows is currently using dark mode.
    /// </summary>
    public static bool IsWindowsDarkModeEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int intValue)
            {
                // 0 = Dark mode, 1 = Light mode
                return intValue == 0;
            }
        }
        catch
        {
            // If we can't read the registry, default to light mode
        }

        return false;
    }

    /// <summary>
    /// Applies dark mode to a window's title bar if Windows dark mode is enabled.
    /// </summary>
    /// <param name="form">The form to apply dark mode to.</param>
    public static void ApplyDarkTitleBar(Form form)
    {
        if (form.Handle == IntPtr.Zero)
        {
            return;
        }

        bool isDarkMode = IsWindowsDarkModeEnabled();
        ApplyDarkTitleBar(form.Handle, isDarkMode);
    }

    /// <summary>
    /// Applies dark mode to a window's title bar.
    /// </summary>
    /// <param name="handle">The window handle.</param>
    /// <param name="enabled">True to enable dark mode, false for light mode.</param>
    public static void ApplyDarkTitleBar(IntPtr handle, bool enabled)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (Environment.OSVersion.Version.Major >= 10)
        {
            int useImmersiveDarkMode = enabled ? 1 : 0;

            // Try Windows 10 20H1+ attribute first
            if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int)) != 0)
            {
                // Fallback to older Windows 10 versions
                DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useImmersiveDarkMode, sizeof(int));
            }
        }
    }
}
