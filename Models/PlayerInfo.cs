namespace ZScape.Models;

/// <summary>
/// Represents information about a player on a server.
/// </summary>
public class PlayerInfo
{
    public string Name { get; set; } = string.Empty;
    public int Score { get; set; }
    public int Ping { get; set; }
    public int Team { get; set; }
    public bool IsSpectator { get; set; }
    public bool IsBot { get; set; }

    public string TeamName => Team switch
    {
        0 => "Blue",
        1 => "Red",
        2 => "Green",
        3 => "Gold",
        255 => "None",
        _ => $"Team {Team}"
    };

    public override string ToString()
    {
        var suffix = IsBot ? " (Bot)" : IsSpectator ? " (Spec)" : "";
        return $"{Name}{suffix}";
    }
}
