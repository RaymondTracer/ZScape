# ZScape Specification

## Project Overview

A modern, dark-themed WinForms application for browsing Zandronum game servers. Built with .NET 10 and C#.

**Author**: Charlie Gadd

This specification has been synchronized with the implementation and documents protocol constants, encoding/decoding behavior, data models, services, UI theme details, settings, and build instructions as implemented in the repository.

---

## Architecture

### Core Components

- **HuffmanCodec**: Zandronum/Skulltag-compatible Huffman encoding/decoding using a prebuilt tree and reverse-bit table.
- **ProtocolConstants**: Centralized protocol constants (challenges, response codes, flags, timeouts).
- **MasterServerClient**: UDP client that communicates with the master server to obtain server endpoints and handles multi-packet responses with retry logic.
- **ServerQueryClient**: Queries individual servers, handles segmented responses and reassembly, and parses server payloads into `ServerInfo`.
- **ServerBrowserService**: Coordinates master refresh and batch server queries with pipelined processing, keeps an in-memory server store, and provides filtering and summary metrics.
- **SettingsService**: Loads/saves `AppSettings` to `settings.json` in the application base directory.
- **LoggingService**: Singleton logging with `VerboseMode` and optional hexdump output used across the protocol and UI layers.
- **WadManager**: Manages WAD file discovery across configured search paths with hash verification and archived version management.
- **WadDownloader**: Multi-threaded WAD downloading with parallel URL discovery, idgames Archive integration, and web search fallback.
- **GameLauncher**: Handles launching Zandronum with proper arguments, WAD hash verification, and testing build downloads.
- **DomainThreadConfig**: Manages domain-specific thread settings for optimized parallel downloads with adaptive learning.
- **NotificationService**: System tray notifications for server alerts (favorite servers coming online).
- **ScreenshotMonitorService**: Consolidates screenshots from testing versions to a central location.
- **Ip2CountryService**: IP-to-country geolocation service using ip-api.com with caching, rate limiting, and batch lookup support.
- **UpdateService**: Automatic updates from GitHub releases with configurable check intervals, background downloading, and optional auto-restart.
- **UI**: WinForms MainForm and supporting dialogs, with `DarkTheme` to consistently style controls and `UIHelpers` for shared component creation.

---

## Protocol Details (implementation-accurate)

### Master Server Communication

- Master host: `ProtocolConstants.MasterServerHost` = `master.zandronum.com`
- Master port: `ProtocolConstants.MasterServerPort` = 15300
- Master protocol version: `ProtocolConstants.MasterProtocolVersion` = 2
- Master challenge: `ProtocolConstants.MasterChallenge` = 5660028

Request (Huffman encoded, little-endian):
```
[4 bytes] int32 MasterChallenge
[2 bytes] int16 MasterProtocolVersion
```

The master server can return multi-packet responses using begin-part markers and packet numbers. The master parser aggregates server endpoints using groups of [IP:4 bytes][count:1 byte][ports:2 bytes each]. Master response codes are defined in `ProtocolConstants` and include codes for Good, Server, End, Banned, Bad, WrongVersion, BeginPart, EndPart and ServerBlock.

### Individual Server Query

- Server challenge: `ProtocolConstants.ServerChallenge` = 199
- Server request format (Huffman encoded):
```
[4 bytes] int32 ServerChallenge (199)
[4 bytes] uint32 QueryFlags
[4 bytes] uint32 Timestamp (milliseconds)
[4 bytes] uint32 ExtendedFlags (SQF2)
[1 byte]  Segmented preference (0=don't care, 1=prefer single, 2=prefer segmented)
```

- Responses include: `ServerGoodSingle` (5660023), `ServerWait` (5660024), `ServerBanned` (5660025), and `ServerGoodSegmented` (5660032).
- `ServerQueryClient` supports `QueryServerAsync` (single server) and `QueryServersAsync` (concurrent queries, default max 50).

**Payload parsing (implemented order):**
1. Timestamp
2. Null-terminated GameVersion
3. Flags (4 bytes), then conditional fields based on flags:
   - Name, website, email
   - Map name
   - Max clients/players
   - PWADs list
   - Game type (mode + instagib/buckshot)
   - IWAD
   - Password flags
   - Skill/botskill
   - Game limits (frag/time/duel/point/win/timeleft)
   - Team damage
   - Num players
   - Player data (name, score, ping, spectator/bot, and optionally team)
   - Team info (number, names, colors, scores)
   - Testing server info
   - Optional WADs
   - DEH
   - Extended info (PWAD hashes and country)

Segmented responses include detailed segment headers and are reassembled by `ServerQueryClient`:
- `segmentNo`: read as a byte and masked with `0x7F` (high bit reserved),
- `totalSegments`: byte,
- `offset`: ushort,
- `segmentSize`: ushort,
- `totalSize`: ushort (used to size the final buffer).

`ServerQueryClient` collects segments and copies each segment's payload into the final buffer at the specified `offset` until the `totalSize` is satisfied (or logs a warning if not all segments arrive).

---

### Huffman Encoding

- Implemented by `HuffmanCodec` as a singleton (`HuffmanCodec.Instance`).
- Uses `CompatibleHuffmanTree` (byte array) and builds a binary Huffman tree on startup.
- `Encode(byte[])` returns `byte[]?` (null on failure). Encoding prepends a padding byte (0-7) and writes bit-packed codes. `_allowExpansion` is `false` by default: if encoding would expand, the encode returns null.
- `Decode(byte[])` returns `byte[]?` and recognizes the unencoded prefix (`0xFF`) to return raw payload when present.
- Bit reversal uses `ReverseMap` for compatibility with the original implementations.

---

## UI Design

### Theme
- **Primary Background**: #1E1E1E (30, 30, 30 - Dark gray)
- **Secondary Background**: #252526 (37, 37, 38 - Slightly lighter)
- **Tertiary Background**: #2D2D30 (45, 45, 48 - Control backgrounds)
- **Border Color**: #3E3E42 (62, 62, 66)
- **Accent Color**: #007ACC (0, 122, 204 - Blue)
- **Accent Hover**: #1C97EA (28, 151, 234)
- **Selection Color**: #333334 (51, 51, 52)
- **Highlight Color**: #3E3E40 (62, 62, 64)
- **Text Primary**: #CCCCCC (204, 204, 204 - Light gray)
- **Text Secondary**: #999999 (153, 153, 153)
- **Text Disabled**: #5C5C5C (92, 92, 92)
- **Success Color**: #4EC9B0 (78, 201, 176 - Teal)
- **Warning Color**: #DCDCAA (220, 220, 170 - Yellow)
- **Error Color**: #F14C4C (241, 76, 76 - Red)
- **Info Color**: #569CD6 (86, 156, 214 - Blue)

Server List Row Colors:
- **Full Server Row**: #3C2828 (60, 40, 40 - Reddish tint)
- **Empty Server Row**: #282828 (40, 40, 40 - Slightly darker)
- **Passworded Server Row**: #3C3228 (60, 50, 40 - Orange tint)

### Features
1. **Server List**
   - Sortable columns with persistent sort settings
   - Row highlighting for passworded/full servers
   - Double-click to connect (launches Zandronum)
   - Right-click context menu with copy, join, refresh options
   - Favorite servers (star column, favorites-only mode)
   - Manual server addition (servers not from master list)

2. **Filtering**
   - Comprehensive `ServerFilter` with presets
   - By game mode (include/exclude lists)
   - By player count (hide empty/full, min/max players, human-only count)
   - By map name (wildcards and regex support)
   - By IWAD
   - By WAD requirements (require/include any/exclude)
   - By country (include/exclude with CheckedListBox UI, search functionality, mutual exclusion)
   - Special country codes at list top: [Unknown/Unresolved], [Anonymous Proxy], [Satellite Provider], [Asia/Pacific Region], [Europe Region]
   - IP-to-Country geolocation for servers returning XIP or empty country codes
   - ISO 3166-1 alpha-3 to alpha-2 country code normalization
   - By ping (min/max)
   - By testing/passworded status
   - Quick search (searches name, map, address)
   - Saved filter presets

3. **Verbose Mode**
   - Toggle via View menu
   - Shows detailed network operations
   - Displays packet timing information
   - Shows raw protocol data (hexdump option)

4. **Settings**
   - Unified settings dialog
   - Auto-refresh interval
   - Query concurrency and retry settings
   - WAD search paths configuration
   - Download concurrency and thread settings
   - Zandronum executable paths (stable and testing)
   - Favorites and alerts configuration
   - Screenshot consolidation settings

5. **WAD Management**
   - WAD browser dialog for exploring local WADs
   - Fetch/download missing WADs with multi-threaded downloader
   - Hash verification before joining servers
   - Automatic WAD archiving with hash suffixes
   - idgames Archive integration
   - Web search fallback (DuckDuckGo)

6. **Connection Features**
   - Connection history with recent servers
   - Server alerts when favorites come online
   - Testing version auto-download and management

### UI Dialogs
- **MainForm**: Primary server browser interface
- **UnifiedSettingsDialog**: Comprehensive settings configuration
- **FirstTimeSetupDialog**: Initial setup wizard shown when settings.json doesn't exist
- **UpdateProgressDialog**: Update download progress display with cancel option
- **ServerFilterDialog**: Advanced server filtering options
- **AddServerDialog**: Manually add servers by IP:Port
- **ConnectionHistoryDialog**: View and reconnect to recent servers
- **FetchWadsDialog**: Download missing WADs interface
- **WadBrowserDialog**: Explore and manage local WAD files
- **WadDownloadDialog**: Multi-threaded download progress display
- **TestingVersionManagerDialog**: Manage installed testing builds

## Data Models

### ServerInfo
Implements `INotifyPropertyChanged` for data binding support.
```csharp
public class ServerInfo : INotifyPropertyChanged
{
    // Core identity
    public IPEndPoint EndPoint { get; set; }
    public string Address => EndPoint.Address.ToString();
    public int Port => EndPoint.Port;
    
    // Observable properties (notify on change)
    public string Name { get; set; }
    public string Map { get; set; }
    public int CurrentPlayers { get; set; }
    public int MaxPlayers { get; set; }
    public int Ping { get; set; }
    public GameMode GameMode { get; set; }
    public bool IsOnline { get; set; }
    
    // Server details
    public int MaxClients { get; set; }
    public string IWAD { get; set; }
    public List<PWadInfo> PWADs { get; set; }
    public string GameVersion { get; set; }
    public bool IsPassworded { get; set; }
    public bool RequiresJoinPassword { get; set; }
    public bool IsTestingServer { get; set; }
    public string TestingArchive { get; set; }
    public int Skill { get; set; }
    public int BotSkill { get; set; }
    public List<PlayerInfo> Players { get; set; }
    public TeamInfo[] Teams { get; set; }
    public int NumTeams { get; set; }
    
    // Game limits
    public int FragLimit { get; set; }
    public int TimeLimit { get; set; }
    public int TimeLeft { get; set; }
    public int PointLimit { get; set; }
    public int DuelLimit { get; set; }
    public int WinLimit { get; set; }
    public float TeamDamage { get; set; }
    
    // Extended info
    public string Country { get; set; }
    public string Website { get; set; }
    public string Email { get; set; }
    public bool IsSecure { get; set; }
    public bool Instagib { get; set; }
    public bool Buckshot { get; set; }
    
    // Query tracking
    public DateTime LastQueryTime { get; set; }
    public DateTime QuerySentTime { get; set; }
    public bool IsQueried { get; set; }
    public string ErrorMessage { get; set; }
    public bool IsRefreshPending { get; set; }   // Indicates pending refresh query
    public int ConsecutiveFailures { get; set; }  // Used for offline detection
    
    // Computed properties
    public string PlayerCountDisplay => $"{CurrentPlayers}/{MaxPlayers}";
    public string PingDisplay => Ping >= 0 ? $"{Ping} ms" : "N/A";
    public bool IsFull => CurrentPlayers >= MaxPlayers;
    public bool IsEmpty => CurrentPlayers == 0;
    public bool HasBots => Players.Any(p => p.IsBot);
    public bool IsTesting => IsTestingServer;
    public int HumanPlayerCount => Players.Count(p => !p.IsBot && !p.IsSpectator);
    public int SpectatorCount => Players.Count(p => p.IsSpectator);
    public int BotCount => Players.Count(p => p.IsBot);
}
```

### PlayerInfo
```csharp
public class PlayerInfo
{
    public string Name { get; set; }
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
}
```

### TeamInfo
```csharp
public class TeamInfo
{
    public string Name { get; set; }
    public Color Color { get; set; }
    public int Score { get; set; }

    public static TeamInfo[] DefaultTeams =>
    [
        new TeamInfo { Name = "Blue", Color = Color.Blue, Score = 0 },
        new TeamInfo { Name = "Red", Color = Color.Red, Score = 0 },
        new TeamInfo { Name = "Green", Color = Color.Green, Score = 0 },
        new TeamInfo { Name = "Gold", Color = Color.Gold, Score = 0 }
    ];
}
```

### PWadInfo
```csharp
public class PWadInfo
{
    public string Name { get; set; }
    public bool IsOptional { get; set; }
    public string? Hash { get; set; }
}
```

### WadInfo
```csharp
public class WadInfo
{
    public string Name { get; set; }
    public string FileName { get; set; }
    public string? LocalPath { get; set; }
    public bool IsFound => !string.IsNullOrEmpty(LocalPath);
    public long FileSize { get; set; }
    public bool IsOptional { get; set; }
    public string? ExpectedHash { get; set; }
    public string? ServerUrl { get; set; }
    public string? DownloadUrl { get; set; }
}
```

### GameMode
```csharp
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

public class GameMode
{
    public GameModeType Type { get; }
    public string Name { get; }
    public string ShortName { get; }
    public bool IsTeamGame { get; }
    
    public static GameMode FromCode(int code);
    public static GameMode FromType(GameModeType type);
    public static IEnumerable<GameMode> AllModes { get; }
}
```

### ServerFilter
Comprehensive filtering configuration with preset support.
```csharp
public enum FilterMode { DontCare, ShowOnly, Hide }

public class ServerFilter
{
    public string Name { get; set; }           // Preset name
    public bool Enabled { get; set; }
    
    // Visibility
    public bool ShowEmpty { get; set; }
    public bool TreatBotOnlyAsEmpty { get; set; }  // Treat bot-only servers as empty
    public bool ShowFull { get; set; }
    public FilterMode PasswordedServers { get; set; }
    public bool ShowUnresponsive { get; set; }
    
    // Text filters (wildcards and regex)
    public string ServerNameFilter { get; set; }
    public bool ServerNameIsRegex { get; set; }
    public string MapFilter { get; set; }
    public bool MapIsRegex { get; set; }
    
    // Game mode filters
    public List<GameModeType> IncludeGameModes { get; set; }
    public List<GameModeType> ExcludeGameModes { get; set; }
    
    // WAD filters
    public List<string> RequireWads { get; set; }
    public List<string> IncludeAnyWads { get; set; }
    public List<string> ExcludeWads { get; set; }
    public string RequireIWAD { get; set; }
    
    // Numeric filters
    public int MinPlayers { get; set; }
    public int MaxPlayers { get; set; }
    public int MinHumanPlayers { get; set; }
    public int MinPing { get; set; }
    public int MaxPing { get; set; }
    
    // Country filters
    public List<string> IncludeCountries { get; set; }
    public List<string> ExcludeCountries { get; set; }
    
    // Other
    public bool PopulatedServersFirst { get; set; }
    public FilterMode TestingServers { get; set; }
    public string RequireVersion { get; set; }
    
    public bool Matches(ServerInfo server);
    public ServerFilter Clone();
    public string GetActiveFiltersDescription();
}
```

### WadDownloadTask
```csharp
public enum WadDownloadStatus
{
    Pending, Searching, Queued, Downloading, 
    Completed, Failed, Cancelled, AlreadyExists
}

public class WadDownloadTask
{
    public WadInfo Wad { get; set; }
    public WadDownloadStatus Status { get; set; }
    public string StatusMessage { get; set; }
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public double BytesPerSecond { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SourceUrl { get; set; }
    public int ThreadCount { get; set; }
    public string? DownloadedFileName { get; set; }
    public List<(string Url, long Size)> AlternateUrls { get; set; }
    public int RetryCount { get; set; }
    public const int MaxRetriesPerSource = 2;
    public int SitesSearched { get; set; }
    public int TotalSitesToSearch { get; set; }
    public double ProgressPercent { get; }
    public string ProgressText { get; }    // e.g., "1.5 MB / 10.0 MB"
    public string SpeedText { get; }       // e.g., "2.5 MB/s"
    
    // Thread-safe increment of SitesSearched counter
    public int IncrementSitesSearched();
}
```

---

## Implementation Details

### Protocol Constants & Defaults
- Master host: `ProtocolConstants.MasterServerHost` = `master.zandronum.com`, port `ProtocolConstants.MasterServerPort` = 15300.
- Master challenge: `ProtocolConstants.MasterChallenge` = 5660028; Protocol version constant = 2.
- Server challenge: `ProtocolConstants.ServerChallenge` = 199.
- Default timeouts: application defaults are `AppConstants.Timeouts.DefaultTimeoutMs` = 5000 ms and `AppConstants.Timeouts.ServerQueryTimeoutMs` = 3000 ms. For protocol-specific use, `ProtocolConstants.DefaultTimeout` = 5000 and `ProtocolConstants.ServerQueryTimeout` = 3000 (kept aligned by the implementation).
- Network buffer sizes: `AppConstants.BufferSizes.NetworkBuffer` = 8192 bytes; protocol-level `ProtocolConstants.MaxPacketSize` = 8192 bytes.
- Huffman settings: `ProtocolConstants.HuffmanBufferMultiplier` = 3 (used to size buffers for Huffman compression).

### Query Flags (SQF)
```csharp
[Flags]
public enum QueryFlags : uint
{
    None = 0,
    Name = 0x00000001, Url = 0x00000002, Email = 0x00000004,
    MapName = 0x00000008, MaxClients = 0x00000010, MaxPlayers = 0x00000020,
    PWads = 0x00000040, GameType = 0x00000080, GameName = 0x00000100,
    Iwad = 0x00000200, ForcePassword = 0x00000400, ForceJoinPassword = 0x00000800,
    GameSkill = 0x00001000, BotSkill = 0x00002000, DmFlags = 0x00004000,
    Limits = 0x00010000, TeamDamage = 0x00020000, TeamScores = 0x00040000,
    NumPlayers = 0x00080000, PlayerData = 0x00100000,
    TeamInfoNumber = 0x00200000, TeamInfoName = 0x00400000,
    TeamInfoColor = 0x00800000, TeamInfoScore = 0x01000000,
    TestingServer = 0x02000000, DataMd5Sum = 0x04000000,
    AllDmFlags = 0x08000000, SecuritySettings = 0x10000000,
    OptionalWads = 0x20000000, Deh = 0x40000000, ExtendedInfo = 0x80000000
}
```

### Extended Query Flags (SQF2)
```csharp
[Flags]
public enum ExtendedQueryFlags : uint
{
    None = 0,
    PwadHashes = 0x00000001,
    Country = 0x00000002,
    GameModeName = 0x00000004,
    GameModeShortName = 0x00000008,
    VoiceChat = 0x00000010,
    
    StandardQuery = PwadHashes | Country | GameModeName
}
```

### MasterServerClient (summary)
- `GetServerListAsync(CancellationToken)` sends a Huffman-encoded challenge and collects one or more responses, handling packet numbers and end-of-list markers.
- Implements retry logic with configurable `MasterServerRetryCount` (default 3) and `QueryRetryDelayMs` between attempts.
- Emits events: `ServerFound` (`ServerEndpointEventArgs`), `RefreshCompleted` (`MasterServerEventArgs`), and `RefreshFailed` (`MasterServerEventArgs`). The client logs hexdumps when verbose mode is enabled.

### ServerQueryClient (summary)
- `QueryServerAsync(IPEndPoint, CancellationToken)` queries a single server and returns a populated `ServerInfo` (or an error state on failure).
- Supports segmented responses (reassembly), single-packet responses, and provides `QueryServersAsync(IEnumerable<IPEndPoint>, int maxConcurrent = 50)` for concurrent querying.
- Parses flags, players, teams, PWADs, extended info (PWAD hashes, country, game mode name) and populates the `ServerInfo` model accordingly.
- Raises `ServerQueried` (`ServerQueryEventArgs`) when a query completes (success or failure).

Note: `CreateServerChallenge()` currently writes `ExtendedQueryFlags.StandardQuery` into the request unconditionally (the implementation writes the extended flags field regardless of whether `QueryFlags.ExtendedInfo` is set). The code's comment suggests conditional writing, but the current behavior always includes the extended flags field.

### HuffmanCodec
- Singleton implementation that builds a Huffman tree from `CompatibleHuffmanTree` and uses `ReverseMap` for bit reversal.
- `Encode` will return `null` if encoding would expand data (by default) or if nodes are missing; `Decode` handles `0xFF`-prefixed unencoded payloads and normal Huffman-decoded payloads.

### Logging & Verbose Mode
- `LoggingService.Instance` controls `VerboseMode` and `ShowHexDumps`.
- `LogHexDump` will return early (do nothing) if `VerboseMode` is false, `ShowHexDumps` is false, or if the provided `data` is null or empty. When enabled, protocol clients call `LogHexDump` to output formatted hex + ASCII blocks for troubleshooting.

### Settings & Persistence
- `SettingsService` persists runtime options to `settings.json` in the application base directory (portable / next to executable).
- Provides `RecordConnection` and `ClearConnectionHistory` methods for connection history management.

### AppSettings (comprehensive)
```csharp
public class AppSettings
{
    // Window state
    public int WindowX { get; set; } = 100;
    public int WindowY { get; set; } = 100;
    public int WindowWidth { get; set; } = 1200;
    public int WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }
    public Dictionary<string, int> ColumnWidths { get; set; }
    public int SortColumnIndex { get; set; } = 3;  // Default: Players (column 3)
    public bool SortAscending { get; set; } = false;
    public int MainSplitterDistance { get; set; } = 400;
    public int DetailsSplitterDistance { get; set; } = 400;
    public int LogSplitterDistance { get; set; } = 150;
    
    // Filters
    public bool HideEmpty { get; set; }
    public bool TreatBotOnlyAsEmpty { get; set; }  // Treat bot-only servers as empty
    public bool HideFull { get; set; }
    public bool HidePassworded { get; set; }
    public int GameModeFilterIndex { get; set; }
    public string SearchText { get; set; }
    public ServerFilter CurrentFilter { get; set; }
    public List<ServerFilter> FilterPresets { get; set; }
    
    // View options
    public bool VerboseMode { get; set; } = false;
    public bool ShowHexDumps { get; set; } = false;
    public bool ShowLogPanel { get; set; } = true;
    
    // Behavior options
    public bool RefreshOnLaunch { get; set; } = true;
    public bool AutoRefresh { get; set; }
    public int AutoRefreshIntervalMinutes { get; set; } = 5;
    public bool AutoRefreshFavoritesOnly { get; set; }  // Only refresh favorites during auto-refresh
    
    // Server query settings
    public int QueryIntervalMs { get; set; } = 5;
    public int MaxConcurrentQueries { get; set; } = 50;
    public int QueryRetryAttempts { get; set; } = 2;
    public int QueryRetryDelayMs { get; set; } = 2000;
    public int MasterServerRetryCount { get; set; } = 3;
    public int ConsecutiveFailuresBeforeOffline { get; set; } = 3;
    
    // WAD settings
    public List<string> WadSearchPaths { get; set; }
    public string WadDownloadPath { get; set; }
    public List<string> DownloadSites { get; set; }
    public int HashVerificationConcurrency { get; set; } = 0;  // 0 = unlimited
    
    // Download concurrency settings
    public int MaxConcurrentDownloads { get; set; } = 0;  // 0 = unlimited
    public int MaxConcurrentDomains { get; set; } = 8;
    public int MaxThreadsPerFile { get; set; } = 0;  // 0 = no global limit
    public int DefaultMaxThreads { get; set; } = 32;
    public int DefaultInitialThreads { get; set; } = 2;
    public int DefaultMinSegmentSizeKb { get; set; } = 256;
    public Dictionary<string, DomainSettings> DomainThreadSettings { get; set; }
    
    // Zandronum paths
    public string ZandronumPath { get; set; }
    public string ZandronumTestingPath { get; set; }
    
    // Favorites
    public HashSet<string> FavoriteServers { get; set; }
    public List<ManualServerEntry> ManualServers { get; set; }
    public bool ShowFavoritesColumn { get; set; } = true;
    public bool ShowFavoritesOnly { get; set; }
    
    // Server Alerts
    public bool EnableFavoriteServerAlerts { get; set; }
    public bool EnableManualServerAlerts { get; set; }
    public int AlertMinPlayers { get; set; } = 1;
    public int AlertCheckIntervalSeconds { get; set; } = 60;
    
    // Connection History
    public List<ConnectionHistoryEntry> ConnectionHistory { get; set; }
    public int MaxHistoryEntries { get; set; } = 50;
    
    // Screenshot consolidation
    public bool EnableScreenshotMonitoring { get; set; }
    public string ScreenshotConsolidationPath { get; set; }
}

public class ManualServerEntry
{
    public string Address { get; set; }
    public int Port { get; set; } = 10666;
    public string? CustomName { get; set; }
    public bool IsFavorite { get; set; } = true;
    public string FullAddress => $"{Address}:{Port}";
}

public class ConnectionHistoryEntry
{
    public string Address { get; set; }
    public int Port { get; set; } = 10666;
    public string ServerName { get; set; }
    public DateTime LastConnected { get; set; }
    public int ConnectionCount { get; set; } = 1;
    public string? IWAD { get; set; }
    public string? GameMode { get; set; }
    public string FullAddress => $"{Address}:{Port}";
}
```

### ServerBrowserService
- Orchestrates refresh: fetch endpoints from master, add/cleanup `ServerInfo` entries, and query servers using pipelined processing with configurable concurrency.
- Supports manual servers and favorites (queried with priority).
- Uses `Channel<T>` for streaming query results to the UI as they arrive.
- Implements retry logic per server with `ConsecutiveFailures` tracking.
- `RefreshAsync()`: Full refresh from master server.
- `RefreshFavoritesAsync()`: Refresh only favorite servers without master query.
- Integrates `Ip2CountryService` to resolve countries for servers with XIP or empty country codes after querying.
- Exposes server lists, summary counts (TotalServers, OnlineServers, TotalPlayers, TotalHumanPlayers, TotalBots) and filtering helper `GetFilteredServers(...)`.
- Raises events: `ServerUpdated`, `RefreshStarted`, `RefreshProgress`, `RefreshCompleted`.

### GameLauncher
- Singleton (`GameLauncher.Instance`) handling Zandronum launches.
- `LaunchGame(ServerInfo, connectPassword?, joinPassword?)`: Launches Zandronum with constructed arguments.
- `CheckRequiredWads(ServerInfo)`: Returns missing WADs with expected hashes.
- `VerifyWadHashesAsync(ServerInfo, progress, cancellationToken)`: Concurrent hash verification with byte-level progress.
- `ResolveHashMismatches(List<WadHashMismatch>)`: Swaps WAD versions or prepares download list.
- `DownloadTestingBuildAsync(ServerInfo)`: Downloads and extracts testing versions, copies config files.
- `GetFullConnectCommand(ServerInfo)`: Returns full command line for clipboard.
- Emits `LaunchError`, `LaunchSuccess`, and `DownloadProgress` events.

### WadManager
- Singleton (`WadManager.Instance`) for WAD file discovery.
- Searches in priority order: 1) Executable folders, 2) Download path, 3) Configured search paths.
- `ForbiddenWads`: Static set of commercial IWADs that should never be downloaded.
- `FindWad(string wadName)`: Locates WAD by name.
- `FindWadByHash(expectedHash, baseName, extension)`: Finds archived versions by hash.
- `ArchiveWadWithHash(filePath)`: Renames WAD with hash suffix for archival.
- `ActivateArchivedWad(archivedPath, standardName)`: Swaps active WAD version.
- `GetAllCachedWads()`: Returns dictionary of all cached WAD filenames and paths.
- `RefreshCache()`: Rebuilds the WAD cache by scanning all search paths.
- `SetExecutableFolders(IEnumerable<string>)`: Sets Zandronum executable folders for priority search.
- `ComputeFileHash(filePath)`: Static method - returns MD5 hash as lowercase hex.

### WadDownloader
- Multi-threaded WAD downloader with parallel URL discovery.
- Searches sources in order: 1) Server URLs, 2) Download sites, 3) idgames Archive, 4) Web search (DuckDuckGo).
- Supports range requests for parallel segment downloads.
- Domain-specific thread optimization with adaptive learning (`DomainThreadConfig`).
- Archives downloaded from idgames are automatically extracted.
- Hash verification after download if expected hash is provided.
- Events: `ProgressUpdated`, `DownloadCompleted`, `LogMessage`.

Default download sites:
```
action.fapnow.xyz, allfearthesentinel.com, euroboros.net,
audrealms.org, pizza-doom.it, wads.firestick.games, doomshack.org
```

### DomainThreadConfig
- Singleton for managing per-domain download thread settings.
- Persists learned optimal thread counts to settings.
- Supports adaptive learning to find maximum supported threads.
- `GetSettings(domain)`: Returns domain-specific settings.
- `UpdateThreadCount(domain, threads, wasSuccessful)`: Updates after download.
- `ReduceThreadCount(domain, currentThreads)`: Reduces on connection errors.

### Ip2CountryService
IP geolocation service for resolving server countries from IP addresses.
- Uses ip-api.com (free tier, no API key required).
- Rate limiting: 45 requests/minute with 1.5s minimum interval between requests.
- Response caching to avoid duplicate lookups.
- `LookupCountryAsync(ipAddress)`: Single IP lookup, returns ISO alpha-2 code.
- `LookupCountriesAsync(ipAddresses)`: Batch lookup (up to 100 IPs per request).
- `ClearCache()`: Clears the IP-to-country cache.

Called by `ServerBrowserService.ResolveUnknownCountriesAsync()` after server queries complete. Servers with "XIP" or empty country codes trigger IP2C lookup. Failed lookups are marked as "??" to prevent retry on next refresh.

### LoggingService Types
```csharp
public enum LogLevel
{
    Verbose,
    Info,
    Warning,
    Error,
    Success
}

public class LogEntry : EventArgs
{
    public string Message { get; }
    public LogLevel Level { get; }
    public DateTime Timestamp { get; }
}
```

### NotificationService Types
```csharp
public enum ServerAlertType
{
    Favorite,
    Manual
}

public class ServerAlertEventArgs : EventArgs
{
    public ServerInfo Server { get; }
    public ServerAlertType AlertType { get; }
}
```

### GameLauncher Helper Classes
```csharp
public class WadHashMismatch
{
    public string WadName { get; init; }
    public string LocalPath { get; init; }
    public string LocalHash { get; init; }
    public string ExpectedHash { get; init; }
    public string? MatchingVersionPath { get; set; }
    public bool NeedsDownload => string.IsNullOrEmpty(MatchingVersionPath);
}

public class HashVerificationProgress
{
    public string CurrentFile { get; init; }
    public int CurrentIndex { get; init; }
    public int TotalFiles { get; init; }
    public string Status { get; init; }
    public long FileSize { get; init; }
    public long BytesProcessed { get; init; }
    public int FilePercentComplete { get; }
    public int OverallPercentComplete { get; }
    public Dictionary<string, (long BytesProcessed, long TotalBytes)>? FileProgress { get; init; }
}
```

### DomainSettings
```csharp
public class DomainSettings
{
    public int MaxThreads { get; set; }
    public int InitialThreads { get; set; }
    public int MinSegmentSizeKb { get; set; }
    public bool AdaptiveLearning { get; set; } = true;
    public DateTime LastUpdated { get; set; }
}
```

### CountryData
Static country data and normalization utilities in `ServerFilterDialog.cs`.
```csharp
public class CountryItem(string code, string name)
{
    public string Code { get; }
    public string Name { get; }
    public override string ToString() => $"{Name} ({Code})";
}

public static class CountryData
{
    // Countries array - special codes first, then ISO 3166-1 alpha-2
    public static readonly CountryItem[] Countries =
    [
        // Special codes at top for easy filtering
        new("??", "[Unknown/Unresolved]"),
        new("A1", "[Anonymous Proxy]"),
        new("A2", "[Satellite Provider]"),
        new("AP", "[Asia/Pacific Region]"),
        new("EU", "[Europe Region]"),
        // Standard countries (AF, AL, DZ, ... ZW)
        // 130+ countries with full names
    ];
    
    // Maps ISO 3166-1 alpha-3 to alpha-2
    // Also normalizes XIP, XUN, O1 to "??"
    public static readonly Dictionary<string, string> Alpha3ToAlpha2;
    
    // Normalizes country codes:
    // - Empty/null -> "??"
    // - XIP, XUN, O1 -> "??"
    // - 3-letter codes -> 2-letter (USA -> US)
    // - 2-letter codes -> uppercase
    public static string NormalizeToAlpha2(string code);
}
```

Used by `ServerFilter.Matches()` to normalize server country codes before comparison with filter values.

### UI Notes
- `DarkTheme` defines colors and provides `Apply`/`ApplyToControl` methods for consistent styling.
- Custom renderers: `DarkMenuRenderer`, `DarkToolStripRenderer`, `DarkColorTable`.
- ListView owner-draw support for proper dark theme rendering.
- DataGridView styling with alternating row colors and header customization.

---

## Utilities

### AppConstants
Centralized application constants for consistency across the codebase.
```csharp
public static class AppConstants
{
    public static class BufferSizes
    {
        public const int FileStreamBuffer = 8192;  // 8 KB
        public const int NetworkBuffer = 8192;     // 8 KB
    }
    
    public static class Timeouts
    {
        public const int DefaultTimeoutMs = 5000;
        public const int ServerQueryTimeoutMs = 3000;
        public const int ConnectionTestTimeoutMs = 5000;
        public const int HttpConnectTimeoutSeconds = 30;
        public const int HttpLongOperationTimeoutMinutes = 30;
        public const int WebRequestTimeoutSeconds = 30;
        public const int PageCrawlTimeoutSeconds = 15;
    }
    
    public static class HttpPooling
    {
        public const int DownloadPooledConnectionLifetimeMinutes = 10;
        public const int PooledConnectionIdleTimeoutMinutes = 5;
        public const int WebPooledConnectionLifetimeMinutes = 5;
    }
    
    public static class AppInfo
    {
        public const string UserAgent = "ZScape/1.0";
        public const string WadDownloaderUserAgent = "..." ;  // Full browser-like UA
    }
    
    public static class UiIntervals
    {
        public const int ProgressReportThrottleMs = 50;
    }
}
```

### FormatUtils
Centralized formatting utilities for human-readable output.
```csharp
public static class FormatUtils
{
    // Formats bytes to human-readable string (e.g., "1.50 MB")
    public static string FormatBytes(long bytes);
    public static string FormatBytes(long bytes, int decimalPlaces);
    
    // Formats speed (e.g., "1.50 MB/s")
    public static string FormatSpeed(double bytesPerSecond);
}
```

### JsonUtils
Centralized JSON serialization options.
```csharp
public static class JsonUtils
{
    // Settings persistence (indented, camelCase, enum strings)
    public static JsonSerializerOptions DefaultOptions { get; }
    
    // Internal config (indented, camelCase, ignore nulls)
    public static JsonSerializerOptions ConfigOptions { get; }
    
    // Compact output (no indentation)
    public static JsonSerializerOptions CompactOptions { get; }
}
```

### WadExtensions
Centralized WAD file extension definitions.
```csharp
public static class WadExtensions
{
    // WAD/mod file extensions
    public static readonly string[] WadFileExtensions = 
        { ".wad", ".pk3", ".pk7", ".ipk3", ".ipk7", ".pke" };
    
    // Archive file extensions
    public static readonly string[] ArchiveExtensions = 
        { ".zip", ".7z", ".rar" };
    
    // All supported extensions for downloading
    public static readonly string[] AllSupportedExtensions;
    
    public static bool IsWadExtension(string extension);
    public static bool IsArchiveExtension(string extension);
    public static bool IsSupportedExtension(string extension);
    public static string GetLowerExtension(string path);
}
```

### DoomColorCodes
Utility for handling Doom/Zandronum color codes in strings.
```csharp
public static class DoomColorCodes
{
    // Escape character for color codes (0x1C)
    public const char EscapeColorChar = '\x1c';
    
    // Strips all color codes, returning plain text
    public static string StripColorCodes(string? text);
    
    // Checks if string contains color codes
    public static bool ContainsColorCodes(string? text);
}
```

### DarkModeHelper
Utility for applying Windows dark mode to window title bars.
```csharp
public static class DarkModeHelper
{
    // Detects if Windows is using dark mode
    public static bool IsWindowsDarkModeEnabled();
    
    // Applies dark mode to a form's title bar
    public static void ApplyDarkTitleBar(Form form);
    public static void ApplyDarkTitleBar(IntPtr handle, bool enabled);
}
```

## Project Structure
```
ZScape/
├── SPECIFICATION.md
├── README.md
├── PROPOSED_CHANGES.md
├── ZScape.sln
├── ZScape.csproj
├── Program.cs
├── MainForm.cs
├── MainForm.Designer.cs
├── Protocol/
│   ├── HuffmanCodec.cs
│   ├── ProtocolConstants.cs
│   ├── MasterServerClient.cs
│   └── ServerQueryClient.cs
├── Models/
│   ├── ServerInfo.cs
│   ├── PlayerInfo.cs
│   ├── TeamInfo.cs
│   ├── PWadInfo.cs
│   ├── WadInfo.cs
│   ├── GameMode.cs
│   └── ServerFilter.cs
├── Services/
│   ├── ServerBrowserService.cs
│   ├── SettingsService.cs
│   ├── LoggingService.cs
│   ├── WadDownloader.cs
│   ├── WadManager.cs
│   ├── GameLauncher.cs
│   ├── DomainThreadConfig.cs
│   ├── NotificationService.cs
│   ├── ScreenshotMonitorService.cs
│   ├── Ip2CountryService.cs
│   └── UpdateService.cs
├── UI/
│   ├── DarkTheme.cs
│   ├── UIHelpers.cs
│   ├── AddServerDialog.cs
│   ├── ConnectionHistoryDialog.cs
│   ├── FetchWadsDialog.cs
│   ├── FirstTimeSetupDialog.cs
│   ├── ServerFilterDialog.cs
│   ├── TestingVersionManagerDialog.cs
│   ├── UnifiedSettingsDialog.cs
│   ├── UpdateProgressDialog.cs
│   ├── WadBrowserDialog.cs
│   └── WadDownloadDialog.cs
├── Utilities/
│   ├── AppConstants.cs
│   ├── DarkModeHelper.cs
│   ├── DoomColorCodes.cs
│   ├── FormatUtils.cs
│   ├── JsonUtils.cs
│   └── WadExtensions.cs
└── References/
    └── (reference implementations)
```

## Dependencies
- .NET 10.0
- System.Net.Sockets (built-in)
- Windows Forms (built-in)
- **SharpCompress** (0.44.5) - Archive extraction (7z, rar, tar, etc.)

## Build Instructions
```bash
cd ZScape
dotnet build
dotnet run
```

## Version History
- 1.0.0 - Initial release
- 1.0.1 - Spec synchronized to repository (2026-01-30): updated protocol constants, Huffman codec behavior, model fields, services, and UI theming.
- 1.0.2 - Spec synchronized to repository (2026-01-31): comprehensive update including:
  - Added ServerFilter model documentation
  - Added WadDownloadTask model and status enum
  - Updated ServerInfo with INotifyPropertyChanged, ConsecutiveFailures, computed properties
  - Added all extended query flags (VoiceChat, GameModeName, GameModeShortName)
  - Documented complete AppSettings with all settings categories
  - Added ManualServerEntry and ConnectionHistoryEntry models
  - Documented WadDownloader with multi-threaded download, idgames integration, web search
  - Documented WadManager with hash verification and version archiving
  - Documented DomainThreadConfig for adaptive download optimization
  - Added new services: NotificationService, ScreenshotMonitorService
  - Corrected project structure (flat layout, not nested)
  - Added all UI dialogs
  - Added SharpCompress dependency
  - Expanded DarkTheme color documentation
  - Added author information
- 1.0.3 - Spec synchronized to repository (2026-02-01): country filtering and IP geolocation update:
  - Added Ip2CountryService for IP-to-country geolocation via ip-api.com
  - Added CountryData static class with 130+ countries and normalization utilities
  - Country codes now normalized: alpha-3 to alpha-2, XIP/XUN/O1 to "??"
  - Country filter UI upgraded to CheckedListBox with search functionality
  - Mutual exclusion between include/exclude country lists
  - Special country codes ([Unknown/Unresolved], [Anonymous Proxy], etc.) at top of lists
  - Added AutoRefreshFavoritesOnly setting for favorites-only auto-refresh
  - Added RefreshFavoritesAsync() method to ServerBrowserService
  - ServerBrowserService now integrates IP2C lookup after server queries
- 1.0.4 - Spec synchronized to repository (2026-02-01): comprehensive documentation update:
  - Added ServerFilter.TreatBotOnlyAsEmpty property
  - Added AppSettings.TreatBotOnlyAsEmpty setting
  - Added ServerInfo.IsRefreshPending property for tracking pending refresh state
  - Fixed SortColumnIndex default value (3, not 2)
  - Added LoggingService types: LogLevel enum and LogEntry class
  - Added NotificationService types: ServerAlertType enum and ServerAlertEventArgs class
  - Added GameLauncher helper classes: WadHashMismatch, HashVerificationProgress
  - Added DomainSettings class documentation
  - Added WadManager methods: GetAllCachedWads(), RefreshCache(), SetExecutableFolders()
  - Fixed WadManager.ComputeFileHash() documentation (static method)
  - Added WadDownloadTask: ProgressText, SpeedText properties, IncrementSitesSearched() method
  - Added complete Utilities section: AppConstants, FormatUtils, JsonUtils, WadExtensions, DoomColorCodes, DarkModeHelper
- 1.0.5 - Spec synchronized to repository (2026-02-02): automatic updates and first-time setup:
  - Added UpdateService for GitHub releases integration (check, download, install updates)
  - Added FirstTimeSetupDialog for initial configuration wizard
  - Added UpdateProgressDialog for update download progress display
  - Added UIHelpers utility class for shared UI component creation
  - Added UpdateIntervalUnit enum (Hours, Days, Weeks) for flexible update scheduling
  - Added update settings: UpdateCheckIntervalValue, UpdateCheckIntervalUnit, AutoRestartForUpdates
  - Added SettingsService.SettingsFileExists property for first-run detection
  - Removed FirstTimeSetupCompleted setting (settings file existence used instead)
  - DarkTheme.Apply() now automatically applies dark title bar on Windows
  - Consistent UI across dialogs using UIHelpers for hints and interval controls

---

## Notes
- Settings are persisted to `settings.json` in the application's base directory (portable by default).
- Use `LoggingService.Instance.VerboseMode` and `ShowHexDumps` for detailed protocol-level troubleshooting.
- Master server queries respect `MasterServerRetryCount` setting (default 3 attempts).
- Server queries respect `ConsecutiveFailuresBeforeOffline` setting to mark servers offline.
- WAD files are searched in priority order: executable folders > download path > configured search paths.
- Commercial IWADs (DOOM, DOOM2, Heretic, Hexen, etc.) are in `ForbiddenWads` and won't be downloaded.
- Testing builds are stored in `{ZandronumPath}/TestingVersions/{version}/` by default.
- Country codes are normalized during filtering: alpha-3 codes (USA, DEU) convert to alpha-2 (US, DE). Unknown codes (XIP, XUN, O1, empty) normalize to "??".
- Servers with "XIP" or empty country codes trigger automatic IP geolocation lookup via ip-api.com after querying. Failed lookups are marked as "??" to prevent retry.
- Auto-refresh can be configured to only refresh favorite servers, skipping the master server query entirely.
- First-time setup wizard is shown automatically when `settings.json` doesn't exist; settings file is only created after completing setup.
- Automatic updates check GitHub releases on configurable intervals (hours/days/weeks); downloads occur in background with optional auto-restart when idle.
- For any behavioral differences, consult the corresponding class in `Protocol/`, `Services/`, or `UI/` for precise implementation details.
