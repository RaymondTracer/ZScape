namespace ZScape.Utilities;

/// <summary>
/// Centralized formatting utilities to ensure consistency across the codebase.
/// </summary>
public static class FormatUtils
{
    private static readonly string[] ByteSizeUnits = ["B", "KB", "MB", "GB", "TB"];
    
    /// <summary>
    /// Formats a byte count into a human-readable string (e.g., "1.50 MB").
    /// </summary>
    /// <param name="bytes">The number of bytes to format.</param>
    /// <returns>A formatted string with appropriate units.</returns>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
            return "0 B";
            
        int order = 0;
        double size = bytes;
        
        while (size >= 1024 && order < ByteSizeUnits.Length - 1)
        {
            order++;
            size /= 1024;
        }
        
        return $"{size:F2} {ByteSizeUnits[order]}";
    }
    
    /// <summary>
    /// Formats a byte count into a human-readable string with optional decimal places.
    /// </summary>
    /// <param name="bytes">The number of bytes to format.</param>
    /// <param name="decimalPlaces">Number of decimal places to show.</param>
    /// <returns>A formatted string with appropriate units.</returns>
    public static string FormatBytes(long bytes, int decimalPlaces)
    {
        if (bytes < 0)
            return "0 B";
            
        int order = 0;
        double size = bytes;
        
        while (size >= 1024 && order < ByteSizeUnits.Length - 1)
        {
            order++;
            size /= 1024;
        }
        
        return $"{size.ToString($"F{decimalPlaces}")} {ByteSizeUnits[order]}";
    }
    
    /// <summary>
    /// Formats a speed value (bytes per second) into a human-readable string (e.g., "1.50 MB/s").
    /// </summary>
    /// <param name="bytesPerSecond">The speed in bytes per second.</param>
    /// <returns>A formatted string with appropriate units and "/s" suffix.</returns>
    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
            return string.Empty;
            
        return $"{FormatBytes((long)bytesPerSecond)}/s";
    }
}
