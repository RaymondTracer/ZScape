namespace ZScape.Models;

/// <summary>
/// Serializable state of the Launch Game dialog, used for persisting last-used
/// settings and saving/loading named configurations. Passwords are never persisted.
/// </summary>
public class LaunchGameConfig
{
    public bool IsHostMode { get; set; }
    public bool IsDedicated { get; set; }
    /// <summary>Full path to the Zandronum executable, or null for stable.</summary>
    public string? ExePath { get; set; }
    public string? IwadPath { get; set; }
    public List<string> PwadPaths { get; set; } = [];
    public string? Map { get; set; }
    public int Skill { get; set; } = 3;
    public int MaxPlayers { get; set; } = 8;
    public int MaxClients { get; set; } = 32;
    public string? ServerName { get; set; }
}

/// <summary>
/// A named, saved launch configuration.
/// </summary>
public class NamedLaunchGameConfig
{
    public string Name { get; set; } = string.Empty;
    public LaunchGameConfig Config { get; set; } = new();
}
