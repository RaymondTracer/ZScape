namespace ZScape.Utilities;

/// <summary>
/// Centralized WAD file extension definitions to ensure consistency across the codebase.
/// </summary>
public static class WadExtensions
{
    /// <summary>
    /// WAD/mod file extensions (files that can be loaded directly by Zandronum).
    /// </summary>
    public static readonly string[] WadFileExtensions = { ".wad", ".pk3", ".pk7", ".ipk3", ".ipk7", ".pke" };
    
    /// <summary>
    /// Archive file extensions that may contain WAD files.
    /// </summary>
    public static readonly string[] ArchiveExtensions = { ".zip", ".7z", ".rar" };
    
    /// <summary>
    /// All supported extensions for downloading (WAD files + archives).
    /// </summary>
    public static readonly string[] AllSupportedExtensions = 
    { 
        ".wad", ".pk3", ".pk7", ".ipk3", ".ipk7", ".pke", 
        ".zip", ".7z", ".rar" 
    };
    
    /// <summary>
    /// Checks if the extension is a WAD/mod file extension.
    /// </summary>
    public static bool IsWadExtension(string extension)
    {
        return WadFileExtensions.Contains(extension.ToLowerInvariant());
    }
    
    /// <summary>
    /// Checks if the extension is an archive extension.
    /// </summary>
    public static bool IsArchiveExtension(string extension)
    {
        return ArchiveExtensions.Contains(extension.ToLowerInvariant());
    }
    
    /// <summary>
    /// Checks if the extension is supported (either WAD or archive).
    /// </summary>
    public static bool IsSupportedExtension(string extension)
    {
        return AllSupportedExtensions.Contains(extension.ToLowerInvariant());
    }
    
    /// <summary>
    /// Gets the extension from a file path/name in lowercase.
    /// </summary>
    public static string GetLowerExtension(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant();
    }
}
