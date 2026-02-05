namespace ZScape.Models;

/// <summary>
/// Represents information about a team on a server.
/// </summary>
public class TeamInfo
{
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Color stored as hex string (e.g., "#0000FF" for blue).
    /// </summary>
    public string ColorHex { get; set; } = "#FFFFFF";
    
    public int Score { get; set; }

    public static TeamInfo[] DefaultTeams =>
    [
        new TeamInfo { Name = "Blue", ColorHex = "#0000FF", Score = 0 },
        new TeamInfo { Name = "Red", ColorHex = "#FF0000", Score = 0 },
        new TeamInfo { Name = "Green", ColorHex = "#00FF00", Score = 0 },
        new TeamInfo { Name = "Gold", ColorHex = "#FFD700", Score = 0 }
    ];

    public override string ToString() => $"{Name}: {Score}";
}
