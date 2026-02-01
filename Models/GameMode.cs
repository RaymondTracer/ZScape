namespace ZScape.Models;

/// <summary>
/// Represents a Zandronum game mode.
/// </summary>
public enum GameModeType
{
    Cooperative = 0,
    Survival = 1,
    Invasion = 2,
    Deathmatch = 3,
    TeamDeathmatch = 4,
    Duel = 5,
    Terminator = 6,
    LastManStanding = 7,
    TeamLMS = 8,
    Possession = 9,
    TeamPossession = 10,
    TeamGame = 11,
    CaptureTheFlag = 12,
    OneFlag = 13,
    Skulltag = 14,
    Domination = 15,
    Unknown = -1
}

/// <summary>
/// Provides information about a game mode.
/// </summary>
public class GameMode
{
    private static readonly Dictionary<GameModeType, GameMode> _gameModes = new()
    {
        { GameModeType.Cooperative, new GameMode(GameModeType.Cooperative, "Cooperative", "Coop", false) },
        { GameModeType.Survival, new GameMode(GameModeType.Survival, "Survival", "Surv", false) },
        { GameModeType.Invasion, new GameMode(GameModeType.Invasion, "Invasion", "Inv", false) },
        { GameModeType.Deathmatch, new GameMode(GameModeType.Deathmatch, "Deathmatch", "DM", false) },
        { GameModeType.TeamDeathmatch, new GameMode(GameModeType.TeamDeathmatch, "Team Deathmatch", "TDM", true) },
        { GameModeType.Duel, new GameMode(GameModeType.Duel, "Duel", "Duel", false) },
        { GameModeType.Terminator, new GameMode(GameModeType.Terminator, "Terminator", "Term", false) },
        { GameModeType.LastManStanding, new GameMode(GameModeType.LastManStanding, "Last Man Standing", "LMS", false) },
        { GameModeType.TeamLMS, new GameMode(GameModeType.TeamLMS, "Team LMS", "TLMS", true) },
        { GameModeType.Possession, new GameMode(GameModeType.Possession, "Possession", "Poss", false) },
        { GameModeType.TeamPossession, new GameMode(GameModeType.TeamPossession, "Team Possession", "TPoss", true) },
        { GameModeType.TeamGame, new GameMode(GameModeType.TeamGame, "Team Game", "Team", true) },
        { GameModeType.CaptureTheFlag, new GameMode(GameModeType.CaptureTheFlag, "Capture The Flag", "CTF", true) },
        { GameModeType.OneFlag, new GameMode(GameModeType.OneFlag, "One Flag CTF", "1Flag", true) },
        { GameModeType.Skulltag, new GameMode(GameModeType.Skulltag, "Skulltag", "ST", true) },
        { GameModeType.Domination, new GameMode(GameModeType.Domination, "Domination", "Dom", true) },
        { GameModeType.Unknown, new GameMode(GameModeType.Unknown, "Unknown", "???", false) }
    };

    public GameModeType Type { get; }
    public string Name { get; }
    public string ShortName { get; }
    public bool IsTeamGame { get; }

    private GameMode(GameModeType type, string name, string shortName, bool isTeamGame)
    {
        Type = type;
        Name = name;
        ShortName = shortName;
        IsTeamGame = isTeamGame;
    }

    public static GameMode FromCode(int code)
    {
        if (Enum.IsDefined(typeof(GameModeType), code))
        {
            var type = (GameModeType)code;
            if (_gameModes.TryGetValue(type, out var mode))
                return mode;
        }
        return _gameModes[GameModeType.Unknown];
    }

    public static GameMode FromType(GameModeType type)
    {
        return _gameModes.TryGetValue(type, out var mode) ? mode : _gameModes[GameModeType.Unknown];
    }

    public static IEnumerable<GameMode> AllModes => _gameModes.Values;

    public override string ToString() => Name;
}
