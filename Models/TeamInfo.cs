namespace ZScape.Models;

/// <summary>
/// Represents information about a team on a server.
/// </summary>
public class TeamInfo
{
    public string Name { get; set; } = string.Empty;
    public Color Color { get; set; } = Color.White;
    public int Score { get; set; }

    public static TeamInfo[] DefaultTeams =>
    [
        new TeamInfo { Name = "Blue", Color = Color.Blue, Score = 0 },
        new TeamInfo { Name = "Red", Color = Color.Red, Score = 0 },
        new TeamInfo { Name = "Green", Color = Color.Green, Score = 0 },
        new TeamInfo { Name = "Gold", Color = Color.Gold, Score = 0 }
    ];

    public override string ToString() => $"{Name}: {Score}";
}
