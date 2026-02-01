namespace ZScape.Models;

/// <summary>
/// Represents information about a WAD file.
/// </summary>
public class PWadInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsOptional { get; set; }
    public string? Hash { get; set; }

    public override string ToString()
    {
        return IsOptional ? $"{Name} (optional)" : Name;
    }
}
