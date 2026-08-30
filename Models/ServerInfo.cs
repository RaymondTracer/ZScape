using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;

namespace ZScape.Models;

/// <summary>
/// Represents complete information about a Zandronum server.
/// Implements INotifyPropertyChanged for data binding support.
/// </summary>
public class ServerInfo : INotifyPropertyChanged
{
    private int _currentPlayers;
    private int _maxPlayers;
    private int _ping = -1;
    private GameMode _gameMode = GameMode.FromType(GameModeType.Unknown);
    private bool _isOnline = true;
    
    public event PropertyChangedEventHandler? PropertyChanged;
    
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
    
    public IPEndPoint EndPoint { get; set; } = new(IPAddress.Any, 0);
    public string Address => EndPoint.Address.ToString();
    public int Port => EndPoint.Port;
    
    private string _name = string.Empty;
    public string Name 
    { 
        get => _name; 
        set => SetField(ref _name, value); 
    }
    
    private string _map = string.Empty;
    public string Map 
    { 
        get => _map; 
        set => SetField(ref _map, value); 
    }
    
    public int CurrentPlayers 
    { 
        get => _currentPlayers; 
        set
        {
            if (SetField(ref _currentPlayers, value))
            {
                OnPropertyChanged(nameof(PlayerCountDisplay));
                OnPropertyChanged(nameof(IsFull));
            }
        }
    }
    
    public int MaxPlayers 
    { 
        get => _maxPlayers; 
        set
        {
            if (SetField(ref _maxPlayers, value))
            {
                OnPropertyChanged(nameof(PlayerCountDisplay));
                OnPropertyChanged(nameof(HasSeparateClientLimit));
            }
        }
    }
    
    public int Ping 
    { 
        get => _ping; 
        set => SetField(ref _ping, value); 
    }
    
    public GameMode GameMode 
    { 
        get => _gameMode; 
        set => SetField(ref _gameMode, value); 
    }
    
    public bool IsOnline 
    { 
        get => _isOnline; 
        set => SetField(ref _isOnline, value); 
    }
    
    // Non-notifying properties (less frequently updated)
    private int _maxClients;
    public int MaxClients
    {
        get => _maxClients;
        set
        {
            if (_maxClients != value)
            {
                _maxClients = value;
                OnPropertyChanged(nameof(PlayerCountDisplay));
                OnPropertyChanged(nameof(HasSeparateClientLimit));
                OnPropertyChanged(nameof(IsFull));
            }
        }
    }
    public string IWAD { get; set; } = string.Empty;
    public List<PWadInfo> PWADs { get; set; } = [];
    public string GameVersion { get; set; } = string.Empty;
    public bool IsPassworded { get; set; }
    public bool RequiresJoinPassword { get; set; }
    public bool IsTestingServer { get; set; }
    public string TestingArchive { get; set; } = string.Empty;
    public int Skill { get; set; }
    public int BotSkill { get; set; }
    public List<PlayerInfo> Players { get; set; } = [];
    public List<TeamInfo> Teams { get; set; } = new(TeamInfo.DefaultTeams);
    public int NumTeams { get; set; } = 2;
    public int FragLimit { get; set; }
    public int TimeLimit { get; set; }
    public int TimeLeft { get; set; }
    public int PointLimit { get; set; }
    public int DuelLimit { get; set; }
    public int WinLimit { get; set; }
    public float TeamDamage { get; set; }
    public string Country { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsSecure { get; set; }
    public bool Instagib { get; set; }
    public bool Buckshot { get; set; }

    // Timestamp for tracking query time
    public DateTime LastQueryTime { get; set; }
    public DateTime QuerySentTime { get; set; }

    // Status flags
    public bool IsQueried { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    
    /// <summary>
    /// Indicates the server is pending a refresh query. Data may be stale.
    /// Set to true when refresh starts, cleared when query completes.
    /// </summary>
    public bool IsRefreshPending { get; set; }
    
    /// <summary>
    /// Number of consecutive query failures. Reset to 0 on successful response.
    /// Server is marked offline when this exceeds ConsecutiveFailuresBeforeOffline setting.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// True when the server has a different limit for actively playing and
    /// merely being connected (for example, as a spectator).
    /// </summary>
    public bool HasSeparateClientLimit => MaxPlayers != MaxClients;

    /// <summary>
    /// Detailed player/client breakdown used in server details and diagnostic
    /// text. Server-list rows use <see cref="GetPlayerColumnDisplay"/> so
    /// their default capacity remains concise.
    /// </summary>
    public string PlayerCountDisplay
    {
        get
        {
            var activeHumans = HumanPlayerCount;
            var activeBots = Players.Count(player => player.IsBot && !player.IsSpectator);
            var spectators = Players.Count(player => player.IsSpectator);
            var playingBreakdown = activeBots > 0
                ? $"{activeHumans}+{activeBots}b"
                : activeHumans.ToString();

            if (HasSeparateClientLimit)
            {
                var connectedDisplay = CurrentPlayers == PlayingCount
                    ? $"{MaxClients}c"
                    : $"{CurrentPlayers}/{MaxClients}c";
                return $"{playingBreakdown}/{MaxPlayers}p ({connectedDisplay})";
            }

            if (spectators > 0)
                return $"{playingBreakdown}+{spectators}s/{MaxPlayers}";

            return $"{playingBreakdown}/{MaxPlayers}";
        }
    }

    /// <summary>
    /// Formats the player column for a server or connection-history row.
    /// With every detail disabled, the display is occupied client slots /
    /// maximum client slots. Each enabled detail exposes only its existing
    /// part of the original display; the remaining values stay folded into
    /// the unlabelled count on the same side of the fraction.
    /// </summary>
    public string GetPlayerColumnDisplay(PlayerColumnDetails? details)
    {
        details ??= new PlayerColumnDetails();

        if (!details.ShowClientCapacityMarker &&
            !details.ShowPlayingCapacity &&
            !details.ShowBots &&
            !details.ShowSpectators)
        {
            return $"{CurrentPlayers}/{MaxClients}";
        }

        // This is the exact legacy row format. Keep it as the all-details
        // result so existing users can recover precisely what they had before.
        if (details.ShowClientCapacityMarker &&
            details.ShowPlayingCapacity &&
            details.ShowBots &&
            details.ShowSpectators)
        {
            return PlayerCountDisplay;
        }

        var activeBots = Players.Count(player => player.IsBot && !player.IsSpectator);
        var spectators = Players.Count(player => player.IsSpectator);

        if (HasSeparateClientLimit && details.ShowPlayingCapacity)
        {
            var playingDisplay = FormatCountWithOptionalBreakdown(
                PlayingCount,
                activeBots,
                spectators: 0,
                details.ShowBots,
                showSpectators: false);
            var display = $"{playingDisplay}/{MaxPlayers}p";

            if (details.ShowClientCapacityMarker)
            {
                var connectedDisplay = CurrentPlayers == PlayingCount
                    ? $"{MaxClients}c"
                    : $"{CurrentPlayers}/{MaxClients}c";
                display = $"{display} ({connectedDisplay})";
            }

            return display;
        }

        var clientDisplay = FormatCountWithOptionalBreakdown(
            CurrentPlayers,
            activeBots,
            spectators,
            details.ShowBots,
            details.ShowSpectators);

        return details.ShowClientCapacityMarker && HasSeparateClientLimit
            ? $"{clientDisplay}/{MaxClients}c"
            : $"{clientDisplay}/{MaxClients}";
    }

    /// <summary>
    /// Splits a total into optional bot and spectator markers. Every disabled
    /// marker remains part of the ordinary numeric count, preserving the total.
    /// </summary>
    private static string FormatCountWithOptionalBreakdown(
        int total,
        int botCount,
        int spectators,
        bool showBots,
        bool showSpectators)
    {
        var collapsedCount = total;
        var markers = new List<string>();

        if (showBots && botCount > 0)
        {
            collapsedCount -= botCount;
            markers.Add($"{botCount}b");
        }

        if (showSpectators && spectators > 0)
        {
            collapsedCount -= spectators;
            markers.Add($"{spectators}s");
        }

        collapsedCount = Math.Max(0, collapsedCount);
        return markers.Count == 0
            ? collapsedCount.ToString()
            : $"{collapsedCount}+{string.Join("+", markers)}";
    }
    
    public string PingDisplay => Ping >= 0 ? $"{Ping} ms" : "N/A";

    public string ModifiersDisplay
    {
        get
        {
            var mods = new List<string>();
            if (Instagib) mods.Add("Instagib");
            if (Buckshot) mods.Add("Buckshot");
            return mods.Count > 0 ? string.Join(", ", mods) : string.Empty;
        }
    }

    public string PWADsDisplay => PWADs.Count > 0 
        ? string.Join(", ", PWADs.Select(p => p.Name)) 
        : "None";

    public bool IsFull => CurrentPlayers >= MaxClients;
    public bool IsEmpty => CurrentPlayers == 0;
    public bool HasBots => Players.Any(p => p.IsBot);
    public bool IsTesting => IsTestingServer;
    public int PlayingCount => Players.Count(p => !p.IsSpectator);
    public int HumanPlayerCount => Players.Count(p => !p.IsBot && !p.IsSpectator);
    public int SpectatorCount => Players.Count(p => p.IsSpectator && !p.IsBot);
    public int BotCount => Players.Count(p => p.IsBot);

    public string GetConnectCommand()
    {
        return $"zandronum.exe +connect {Address}:{Port}";
    }

    public override string ToString()
    {
        return $"{Name} [{PlayerCountDisplay}] - {Map} ({GameMode.Name})";
    }
}
