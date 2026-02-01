using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZScape.Utilities;

/// <summary>
/// Centralized JSON serialization options to ensure consistency across the codebase.
/// </summary>
public static class JsonUtils
{
    /// <summary>
    /// Default JSON serializer options for application settings and configuration files.
    /// Uses camelCase property naming, indented formatting, and enum string conversion.
    /// </summary>
    public static JsonSerializerOptions DefaultOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    
    /// <summary>
    /// JSON serializer options for internal configuration (like domain thread config).
    /// Uses camelCase property naming, indented formatting, and ignores null values.
    /// </summary>
    public static JsonSerializerOptions ConfigOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    
    /// <summary>
    /// JSON serializer options for compact output (no indentation).
    /// Useful for logging or when file size matters.
    /// </summary>
    public static JsonSerializerOptions CompactOptions { get; } = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
