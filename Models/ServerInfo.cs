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
    private string _name = string.Empty;
    private string _map = string.Empty;
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
    
    public string Name 
    { 
        get => _name; 
        set => SetField(ref _name, value); 
    }
    
    public string Map 
    { 
        get => _map; 
        set => SetField(ref _map, value); 
    }
    
    public int CurrentPlayers 
    { 
        get => _currentPlayers; 
        set => SetField(ref _currentPlayers, value); 
    }
    
    public int MaxPlayers 
    { 
        get => _maxPlayers; 
        set => SetField(ref _maxPlayers, value); 
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
    public int MaxClients { get; set; }
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
    public TeamInfo[] Teams { get; set; } = TeamInfo.DefaultTeams;
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

    public string PlayerCountDisplay => $"{CurrentPlayers}/{MaxPlayers}";
    
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

    public bool IsFull => CurrentPlayers >= MaxPlayers;
    public bool IsEmpty => CurrentPlayers == 0;
    public bool HasBots => Players.Any(p => p.IsBot);
    public bool IsTesting => IsTestingServer;
    public int HumanPlayerCount => Players.Count(p => !p.IsBot && !p.IsSpectator);
    public int SpectatorCount => Players.Count(p => p.IsSpectator);
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
