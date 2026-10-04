using System.Text.Json.Serialization;
using ZScape.Utilities;

namespace ZScape.Models;

/// <summary>
/// Defines how to handle a particular server attribute in filtering.
/// </summary>
public enum FilterMode
{
    /// <summary>Don't filter on this attribute.</summary>
    DontCare,
    /// <summary>Show only servers matching the filter.</summary>
    ShowOnly,
    /// <summary>Hide servers matching the filter.</summary>
    Hide
}

/// <summary>
/// Comprehensive server filter configuration.
/// </summary>
public class ServerFilter
{
    /// <summary>Name of this filter preset (for saved presets).</summary>
    public string Name { get; set; } = "Default";

    /// <summary>Whether this filter is currently enabled.</summary>
    public bool Enabled { get; set; } = true;

    // === Basic Visibility ===
    
    /// <summary>Show servers with no players.</summary>
    public bool ShowEmpty { get; set; } = true;

    /// <summary>Treat servers with only bots (no human players) as empty.</summary>
    public bool TreatBotOnlyAsEmpty { get; set; } = false;

    /// <summary>Show servers that are full.</summary>
    public bool ShowFull { get; set; } = true;

    /// <summary>How to handle passworded servers.</summary>
    public FilterMode PasswordedServers { get; set; } = FilterMode.DontCare;

    /// <summary>Show servers that didn't respond to query.</summary>
    public bool ShowUnresponsive { get; set; } = false;

    // === Text Filters ===

    /// <summary>Filter by visible server name.</summary>
    public string ServerNameFilter { get; set; } = string.Empty;

    /// <summary>How the server name filter should be matched.</summary>
    public TextMatchMode ServerNameMatchMode { get; set; } = TextMatchMode.Contains;

    /// <summary>Legacy compatibility flag for old settings files that only stored regex state.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ServerNameIsRegex
    {
        get => ServerNameMatchMode == TextMatchMode.Regex;
        set
        {
            if (value)
            {
                ServerNameMatchMode = TextMatchMode.Regex;
            }
        }
    }

    /// <summary>Filter by map name.</summary>
    public string MapFilter { get; set; } = string.Empty;

    /// <summary>How the map filter should be matched.</summary>
    public TextMatchMode MapMatchMode { get; set; } = TextMatchMode.Contains;

    /// <summary>Legacy compatibility flag for old settings files that only stored regex state.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MapIsRegex
    {
        get => MapMatchMode == TextMatchMode.Regex;
        set
        {
            if (value)
            {
                MapMatchMode = TextMatchMode.Regex;
            }
        }
    }

    // === Game Mode Filters ===

    /// <summary>Game modes to include (empty = all modes).</summary>
    public List<GameModeType> IncludeGameModes { get; set; } = [];

    /// <summary>Game modes to exclude.</summary>
    public List<GameModeType> ExcludeGameModes { get; set; } = [];

    // === WAD Filters ===

    /// <summary>Required WADs (all must be present).</summary>
    public List<string> RequireWads { get; set; } = [];

    /// <summary>Any of these WADs must be present.</summary>
    public List<string> IncludeAnyWads { get; set; } = [];

    /// <summary>Servers with these WADs are hidden.</summary>
    public List<string> ExcludeWads { get; set; } = [];

    /// <summary>Required IWAD (empty = any).</summary>
    public string RequireIWAD { get; set; } = string.Empty;

    // === Numeric Filters ===

    /// <summary>Minimum number of players (0 = no minimum).</summary>
    public int MinPlayers { get; set; }

    /// <summary>Maximum number of players (0 = no maximum).</summary>
    public int MaxPlayers { get; set; }

    /// <summary>Minimum human players (excludes bots).</summary>
    public int MinHumanPlayers { get; set; }

    /// <summary>Maximum allowed ping in ms (0 = no limit).</summary>
    public int MaxPing { get; set; }
    
    /// <summary>Minimum allowed ping in ms (0 = no minimum).</summary>
    public int MinPing { get; set; }
    
    // === Country Filters ===
    
    /// <summary>Countries to include (empty = all countries, use country codes like "US", "DE").</summary>
    public List<string> IncludeCountries { get; set; } = [];
    
    /// <summary>Countries to exclude.</summary>
    public List<string> ExcludeCountries { get; set; } = [];

    // === Sorting Options ===

    /// <summary>Put servers with players at the top.</summary>
    public bool PopulatedServersFirst { get; set; } = true;

    // === Version/Testing ===

    /// <summary>How to handle testing/beta servers.</summary>
    public FilterMode TestingServers { get; set; } = FilterMode.DontCare;

    /// <summary>Required game version (empty = any).</summary>
    public string RequireVersion { get; set; } = string.Empty;

    /// <summary>
    /// Check if a server matches this filter.
    /// </summary>
    public bool Matches(ServerInfo server)
    {
        if (!Enabled) return true;

        var displayName = ServerRuleUtility.GetComparableServerName(server);

        // Basic visibility
        if (!MatchesBasicVisibility(server, hideEmpty: !ShowEmpty, hideBotOnly: TreatBotOnlyAsEmpty,
                hideFull: !ShowFull, passwordedServers: PasswordedServers,
                botOnlyScope: BotOnlyScope.PlayingHumans)) return false;
        if (!ShowUnresponsive && !server.IsOnline) return false;

        // Testing servers
        if (TestingServers == FilterMode.ShowOnly && !server.IsTesting) return false;
        if (TestingServers == FilterMode.Hide && server.IsTesting) return false;

        // Visible server name filter
        if (!string.IsNullOrWhiteSpace(ServerNameFilter))
        {
            if (!TextMatchUtility.IsMatch(displayName, ServerNameFilter, ServerNameMatchMode))
                return false;
        }

        // Map filter
        if (!string.IsNullOrWhiteSpace(MapFilter))
        {
            if (!TextMatchUtility.IsMatch(server.Map, MapFilter, MapMatchMode))
                return false;
        }

        // Game mode include
        if (IncludeGameModes.Count > 0 && !IncludeGameModes.Contains(server.GameMode?.Type ?? GameModeType.Unknown))
            return false;

        // Game mode exclude
        if (ExcludeGameModes.Contains(server.GameMode?.Type ?? GameModeType.Unknown))
            return false;

        // Required IWAD
        if (!string.IsNullOrWhiteSpace(RequireIWAD))
        {
            if (!server.IWAD.Contains(RequireIWAD, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Build the same WAD projection once for all three WAD filter policies.
        var serverWads = RequireWads.Count > 0 || IncludeAnyWads.Count > 0 || ExcludeWads.Count > 0
            ? server.PWADs.Select(w => w.Name.ToLowerInvariant()).Append(server.IWAD.ToLowerInvariant()).ToArray()
            : [];

        // Required WADs (all must be present)
        if (RequireWads.Count > 0)
        {
            foreach (var required in RequireWads)
            {
                if (!serverWads.Any(w => w.Contains(required, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
        }

        // Include any WADs (at least one must be present)
        if (IncludeAnyWads.Count > 0)
        {
            bool hasAny = false;
            foreach (var include in IncludeAnyWads)
            {
                if (serverWads.Any(w => w.Contains(include, StringComparison.OrdinalIgnoreCase)))
                {
                    hasAny = true;
                    break;
                }
            }
            if (!hasAny) return false;
        }

        // Exclude WADs
        if (ExcludeWads.Count > 0)
        {
            foreach (var exclude in ExcludeWads)
            {
                if (serverWads.Any(w => w.Contains(exclude, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }
        }

        // Player count filters
        if (MinPlayers > 0 && server.CurrentPlayers < MinPlayers) return false;
        if (MaxPlayers > 0 && server.CurrentPlayers > MaxPlayers) return false;
        if (MinHumanPlayers > 0 && server.HumanPlayerCount < MinHumanPlayers) return false;

        // Ping filter
        if (MinPing > 0 && server.Ping < MinPing) return false;
        if (MaxPing > 0 && server.Ping > MaxPing) return false;
        
        // Country filter
        if (IncludeCountries.Count > 0)
        {
            var normalizedCountry = CountryData.NormalizeToAlpha2(server.Country);
            if (!IncludeCountries.Any(c => 
                c.Equals(normalizedCountry, StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        
        if (ExcludeCountries.Count > 0)
        {
            var normalizedCountry = CountryData.NormalizeToAlpha2(server.Country);
            if (ExcludeCountries.Any(c => 
                c.Equals(normalizedCountry, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        // Version filter
        if (!string.IsNullOrWhiteSpace(RequireVersion))
        {
            if (!server.GameVersion.Contains(RequireVersion, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    internal enum BotOnlyScope
    {
        // Toolbar: a connected human spectator means this is not bot-only.
        ConnectedClients,
        // Advanced filter: no humans are actively playing, even if spectating.
        PlayingHumans
    }

    /// <summary>
    /// Shared basic visibility rules for toolbar toggles and advanced presets.
    /// The two existing bot-only scopes remain explicit rather than changing
    /// how either filter treats human spectators.
    /// </summary>
    internal static bool MatchesBasicVisibility(
        ServerInfo server,
        bool hideEmpty,
        bool hideBotOnly,
        bool hideFull,
        FilterMode passwordedServers,
        BotOnlyScope botOnlyScope)
    {
        if (hideEmpty && server.IsEmpty) return false;
        if (hideBotOnly && server.CurrentPlayers > 0)
        {
            var botOnly = botOnlyScope == BotOnlyScope.ConnectedClients
                ? server.CurrentPlayers == server.BotCount
                : server.HumanPlayerCount == 0;
            if (botOnly) return false;
        }
        if (hideFull && server.IsFull) return false;
        if (passwordedServers == FilterMode.ShowOnly && !server.IsPassworded) return false;
        if (passwordedServers == FilterMode.Hide && server.IsPassworded) return false;
        return true;
    }

    /// <summary>
    /// Toolbar search matches a complete query within any one existing server
    /// field or WAD name. Pending rows expose only their known endpoint address.
    /// </summary>
    internal static bool MatchesLooseSearch(ServerInfo server, string? query)
    {
        if (string.IsNullOrEmpty(query)) return true;
        var address = ServerRuleUtility.GetServerAddress(server);
        if (server.IsRefreshPending)
            return TextMatchUtility.IsLooseSearchMatch(address, query);

        var fields = new[]
        {
            ServerRuleUtility.GetComparableServerName(server),
            server.Map,
            address,
            server.IWAD,
            server.GameMode.Name,
            server.GameMode.ShortName,
            server.Country,
            server.GameVersion
        };
        return TextMatchUtility.IsAnyLooseSearchMatch(fields.Concat(server.PWADs.Select(wad => wad.Name)), query);
    }

    /// <summary>
    /// Creates a copy of this filter.
    /// </summary>
    public ServerFilter Clone()
    {
        return new ServerFilter
        {
            Name = Name,
            Enabled = Enabled,
            ShowEmpty = ShowEmpty,
            TreatBotOnlyAsEmpty = TreatBotOnlyAsEmpty,
            ShowFull = ShowFull,
            PasswordedServers = PasswordedServers,
            ShowUnresponsive = ShowUnresponsive,
            ServerNameFilter = ServerNameFilter,
            ServerNameMatchMode = ServerNameMatchMode,
            MapFilter = MapFilter,
            MapMatchMode = MapMatchMode,
            IncludeGameModes = [.. IncludeGameModes],
            ExcludeGameModes = [.. ExcludeGameModes],
            RequireWads = [.. RequireWads],
            IncludeAnyWads = [.. IncludeAnyWads],
            ExcludeWads = [.. ExcludeWads],
            RequireIWAD = RequireIWAD,
            MinPlayers = MinPlayers,
            MaxPlayers = MaxPlayers,
            MinHumanPlayers = MinHumanPlayers,
            MinPing = MinPing,
            MaxPing = MaxPing,
            IncludeCountries = [.. IncludeCountries],
            ExcludeCountries = [.. ExcludeCountries],
            PopulatedServersFirst = PopulatedServersFirst,
            TestingServers = TestingServers,
            RequireVersion = RequireVersion
        };
    }

    /// <summary>
    /// Gets a human-readable description of currently active filters.
    /// </summary>
    public string GetActiveFiltersDescription()
    {
        var parts = new List<string>();

        if (!ShowEmpty) parts.Add("hiding empty");
        if (TreatBotOnlyAsEmpty) parts.Add("hiding bots-only");
        if (!ShowFull) parts.Add("hiding full");
        if (PasswordedServers == FilterMode.Hide) parts.Add("hiding passworded");
        if (PasswordedServers == FilterMode.ShowOnly) parts.Add("passworded only");
        if (!string.IsNullOrWhiteSpace(ServerNameFilter)) parts.Add($"name ({AppConstants.TextMatchModeLabels.GetLabel(ServerNameMatchMode)}): {ServerNameFilter}");
        if (!string.IsNullOrWhiteSpace(MapFilter)) parts.Add($"map ({AppConstants.TextMatchModeLabels.GetLabel(MapMatchMode)}): {MapFilter}");
        if (IncludeGameModes.Count > 0) parts.Add($"modes: {string.Join(", ", IncludeGameModes)}");
        if (MinPlayers > 0) parts.Add($"min {MinPlayers} players");
        if (MinHumanPlayers > 0) parts.Add($"min {MinHumanPlayers} humans");
        if (MinPing > 0) parts.Add($"min {MinPing}ms ping");
        if (MaxPing > 0) parts.Add($"max {MaxPing}ms ping");
        if (IncludeCountries.Count > 0) parts.Add($"countries: {string.Join(", ", IncludeCountries)}");
        if (ExcludeCountries.Count > 0) parts.Add($"exclude countries: {string.Join(", ", ExcludeCountries)}");
        if (RequireWads.Count > 0) parts.Add($"requires: {string.Join(", ", RequireWads)}");
        if (ExcludeWads.Count > 0) parts.Add($"excludes: {string.Join(", ", ExcludeWads)}");

        return parts.Count > 0 ? string.Join("; ", parts) : "No filters active";
    }
}
