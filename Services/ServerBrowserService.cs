using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using ZScape.Models;
using ZScape.Protocol;

namespace ZScape.Services;

/// <summary>
/// Main service for browsing and querying Zandronum servers.
/// </summary>
public class ServerBrowserService : IDisposable
{
    private readonly LoggingService _logger = LoggingService.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly MasterServerClient _masterClient = new();
    private readonly ConcurrentDictionary<string, ServerInfo> _servers = new();
    private readonly Ip2CountryService _ip2CountryService;
    private CancellationTokenSource? _refreshCts;
    private bool _disposed;

    public ServerBrowserService()
    {
        _ip2CountryService = new Ip2CountryService(_logger);
    }

    public event EventHandler<ServerInfo>? ServerUpdated;
    public event EventHandler? RefreshStarted;
    public event EventHandler<int>? RefreshProgress;
    public event EventHandler<RefreshCompletedEventArgs>? RefreshCompleted;

    public IReadOnlyCollection<ServerInfo> Servers => _servers.Values.ToList();
    public bool IsRefreshing { get; private set; }

    public int TotalServers => _servers.Count;
    public int OnlineServers => _servers.Values.Count(s => s.IsOnline && s.IsQueried);
    public int TotalPlayers => _servers.Values.Where(s => s.IsOnline).Sum(s => s.CurrentPlayers);
    public int TotalHumanPlayers => _servers.Values.Where(s => s.IsOnline).Sum(s => s.HumanPlayerCount);
    public int TotalBots => _servers.Values.Where(s => s.IsOnline).Sum(s => s.BotCount);

    /// <summary>
    /// Refreshes the server list from the master server and queries all servers.
    /// Manual servers are always included. Favorites and manual servers are queried first.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsRefreshing)
        {
            _logger.Warning("Refresh already in progress");
            return;
        }

        IsRefreshing = true;
        RefreshStarted?.Invoke(this, EventArgs.Empty);
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            _logger.Info("Starting server refresh...");

            // Get server list from master
            var masterEndpoints = await _masterClient.GetServerListAsync(_refreshCts.Token);
            _logger.Info($"Received {masterEndpoints.Count} servers from master");

            // Get manual servers (always probe these regardless of master list)
            var manualEndpoints = GetManualServerEndpoints();
            _logger.Info($"Including {manualEndpoints.Count} manual servers");

            // Combine all endpoints (manual + master, deduplicated)
            var allEndpoints = new HashSet<string>();
            var endpoints = new List<IPEndPoint>();
            
            // Add manual servers first
            foreach (var ep in manualEndpoints)
            {
                var key = ep.ToString();
                if (allEndpoints.Add(key))
                    endpoints.Add(ep);
            }
            
            // Add master servers
            foreach (var ep in masterEndpoints)
            {
                var key = ep.ToString();
                if (allEndpoints.Add(key))
                    endpoints.Add(ep);
            }

            // Create set of manual server addresses for checking
            var manualAddresses = new HashSet<string>(manualEndpoints.Select(e => e.ToString()));
            
            // Mark old servers that are no longer in master list as potentially stale
            // But don't remove them - they may just be temporarily missing
            // They'll be removed after consecutive failures during queries
            var existingKeys = _servers.Keys.ToHashSet();
            foreach (var key in existingKeys)
            {
                if (!allEndpoints.Contains(key))
                {
                    // Server not in master list - keep it but it won't be queried
                    // unless it's a manual server
                    if (!manualAddresses.Contains(key))
                    {
                        _servers.TryRemove(key, out _);
                    }
                }
            }

            // Initialize server entries and mark existing servers as pending refresh
            foreach (var endpoint in endpoints)
            {
                var key = endpoint.ToString();
                if (_servers.TryGetValue(key, out var existingServer))
                {
                    // Mark existing server as pending refresh - data may be stale
                    existingServer.IsRefreshPending = true;
                }
                else
                {
                    _servers[key] = new ServerInfo { EndPoint = endpoint, IsRefreshPending = true };
                }
            }

            // Sort endpoints: favorites first, then manual servers, then rest
            var favorites = _settings.Settings.FavoriteServers;
            
            endpoints = endpoints
                .OrderByDescending(e => favorites.Contains(e.ToString()))  // Favorites first
                .ThenByDescending(e => manualAddresses.Contains(e.ToString()))  // Then manual servers
                .ToList();
            
            _logger.Info($"Querying {endpoints.Count} servers (favorites and manual prioritized)");

            // Query servers in batches
            await QueryAllServersAsync(endpoints, _refreshCts.Token);

            // Resolve countries for servers with XIP or empty country codes
            await ResolveUnknownCountriesAsync(_refreshCts.Token);

            var onlineCount = OnlineServers;
            var totalCount = TotalServers;
            _logger.Success($"Refresh complete. {onlineCount}/{totalCount} servers online, {TotalPlayers} players.");
            RefreshCompleted?.Invoke(this, new RefreshCompletedEventArgs(totalCount, onlineCount, null));
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("Refresh cancelled");
            RefreshCompleted?.Invoke(this, new RefreshCompletedEventArgs(0, 0, "Cancelled"));
        }
        catch (Exception ex)
        {
            _logger.Error($"Refresh failed: {ex.Message}");
            RefreshCompleted?.Invoke(this, new RefreshCompletedEventArgs(0, 0, ex.Message));
        }
        finally
        {
            // Clear all pending flags - refresh is complete (successful or not)
            foreach (var server in _servers.Values)
            {
                server.IsRefreshPending = false;
            }
            IsRefreshing = false;
        }
    }

    /// <summary>
    /// Refreshes only favorite servers without querying the master server.
    /// </summary>
    public async Task RefreshFavoritesAsync(CancellationToken cancellationToken = default)
    {
        if (IsRefreshing)
        {
            _logger.Warning("Refresh already in progress");
            return;
        }

        var favorites = _settings.Settings.FavoriteServers;
        if (favorites.Count == 0)
        {
            _logger.Info("No favorite servers to refresh");
            return;
        }

        IsRefreshing = true;
        RefreshStarted?.Invoke(this, EventArgs.Empty);
        _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            _logger.Info($"Refreshing {favorites.Count} favorite servers...");

            var endpoints = new List<IPEndPoint>();
            foreach (var favKey in favorites)
            {
                if (IPEndPoint.TryParse(favKey, out var endpoint))
                {
                    endpoints.Add(endpoint);
                    // Mark as pending refresh
                    if (_servers.TryGetValue(favKey, out var existingServer))
                    {
                        existingServer.IsRefreshPending = true;
                    }
                }
            }

            await QueryAllServersAsync(endpoints, _refreshCts.Token);

            // Resolve countries for servers with XIP or empty country codes
            await ResolveUnknownCountriesAsync(_refreshCts.Token);

            _logger.Success($"Favorites refresh complete. {endpoints.Count} servers queried.");
            RefreshCompleted?.Invoke(this, new RefreshCompletedEventArgs(TotalServers, OnlineServers, null));
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("Favorites refresh cancelled");
            RefreshCompleted?.Invoke(this, new RefreshCompletedEventArgs(0, 0, "Cancelled"));
        }
        catch (Exception ex)
        {
            _logger.Error($"Favorites refresh failed: {ex.Message}");
            RefreshCompleted?.Invoke(this, new RefreshCompletedEventArgs(0, 0, ex.Message));
        }
        finally
        {
            // Clear pending flags for favorites
            foreach (var favKey in favorites)
            {
                if (_servers.TryGetValue(favKey, out var server))
                {
                    server.IsRefreshPending = false;
                }
            }
            IsRefreshing = false;
        }
    }

    /// <summary>
    /// Gets endpoints for all manually added servers.
    /// </summary>
    private List<IPEndPoint> GetManualServerEndpoints()
    {
        var endpoints = new List<IPEndPoint>();
        
        foreach (var manual in _settings.Settings.ManualServers)
        {
            try
            {
                if (IPAddress.TryParse(manual.Address, out var ip))
                {
                    endpoints.Add(new IPEndPoint(ip, manual.Port));
                }
                else
                {
                    // Try DNS resolution for hostnames
                    var resolved = Dns.GetHostAddresses(manual.Address);
                    if (resolved.Length > 0)
                    {
                        endpoints.Add(new IPEndPoint(resolved[0], manual.Port));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to resolve manual server {manual.FullAddress}: {ex.Message}");
            }
        }
        
        return endpoints;
    }

    /// <summary>
    /// Queries a single server for updated information with retry logic.
    /// </summary>
    public async Task RefreshServerAsync(ServerInfo server, CancellationToken cancellationToken = default)
    {
        var settings = _settings.Settings;
        int retryAttempts = Math.Max(1, settings.QueryRetryAttempts);
        int retryDelayMs = Math.Max(100, settings.QueryRetryDelayMs);
        
        ServerInfo? result = null;
        
        for (int attempt = 1; attempt <= retryAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            
            using var queryClient = new ServerQueryClient();
            result = await queryClient.QueryServerAsync(server.EndPoint, cancellationToken);
            
            if (result != null && result.IsOnline && result.IsQueried)
                break; // Success
            
            if (attempt < retryAttempts)
            {
                _logger.Verbose($"Retry {attempt}/{retryAttempts} for {server.EndPoint} in {retryDelayMs}ms");
                await Task.Delay(retryDelayMs, cancellationToken);
            }
        }
        
        if (result != null && result.IsOnline && result.IsQueried)
        {
            result.ConsecutiveFailures = 0;
            var key = server.EndPoint.ToString();
            _servers[key] = result;
            ServerUpdated?.Invoke(this, result);
        }
        else
        {
            // Query failed - update failure count on existing entry
            var key = server.EndPoint.ToString();
            if (_servers.TryGetValue(key, out var existingServer))
            {
                existingServer.ConsecutiveFailures++;
                ServerUpdated?.Invoke(this, existingServer);
            }
        }
    }
    
    /// <summary>
    /// Queries a server by endpoint address (for manual server adding) with retry logic.
    /// </summary>
    public async Task RefreshServerAsync(IPEndPoint endpoint, CancellationToken cancellationToken = default)
    {
        var settings = _settings.Settings;
        int retryAttempts = Math.Max(1, settings.QueryRetryAttempts);
        int retryDelayMs = Math.Max(100, settings.QueryRetryDelayMs);
        
        ServerInfo? result = null;
        
        for (int attempt = 1; attempt <= retryAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            
            using var queryClient = new ServerQueryClient();
            result = await queryClient.QueryServerAsync(endpoint, cancellationToken);
            
            if (result != null && result.IsOnline && result.IsQueried)
                break; // Success
            
            if (attempt < retryAttempts)
            {
                _logger.Verbose($"Retry {attempt}/{retryAttempts} for {endpoint} in {retryDelayMs}ms");
                await Task.Delay(retryDelayMs, cancellationToken);
            }
        }
        
        if (result != null && result.IsOnline && result.IsQueried)
        {
            result.ConsecutiveFailures = 0;
            var key = endpoint.ToString();
            _servers[key] = result;
            ServerUpdated?.Invoke(this, result);
        }
        else
        {
            // Query failed - update failure count on existing entry
            var key = endpoint.ToString();
            if (_servers.TryGetValue(key, out var existingServer))
            {
                existingServer.ConsecutiveFailures++;
                ServerUpdated?.Invoke(this, existingServer);
            }
        }
    }

    /// <summary>
    /// Cancels any ongoing refresh operation.
    /// </summary>
    public void CancelRefresh()
    {
        _refreshCts?.Cancel();
    }

    private async Task QueryAllServersAsync(List<IPEndPoint> endpoints, CancellationToken cancellationToken)
    {
        var settings = _settings.Settings;
        int maxConcurrent = settings.MaxConcurrentQueries > 0 ? settings.MaxConcurrentQueries : int.MaxValue;
        int intervalMs = Math.Max(1, settings.QueryIntervalMs);
        int retryAttempts = Math.Max(1, settings.QueryRetryAttempts);
        int retryDelayMs = Math.Max(100, settings.QueryRetryDelayMs);
        int failuresBeforeOffline = Math.Max(1, settings.ConsecutiveFailuresBeforeOffline);
        
        int total = endpoints.Count;
        int completed = 0;
        
        // Use semaphore to limit concurrent in-flight queries
        using var concurrencySemaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        
        // Channel for collecting completed queries - enables true pipelining
        var completionChannel = Channel.CreateUnbounded<(IPEndPoint Endpoint, ServerInfo? Result)>();
        
        // Task to process completed results as they stream in
        var processingTask = Task.Run(async () =>
        {
            await foreach (var (endpoint, result) in completionChannel.Reader.ReadAllAsync(cancellationToken))
            {
                var key = endpoint.ToString();
                
                if (result != null && result.IsOnline && result.IsQueried)
                {
                    // Success - reset failure count and update server
                    result.ConsecutiveFailures = 0;
                    result.IsRefreshPending = false;
                    _servers[key] = result;
                    ServerUpdated?.Invoke(this, result);
                }
                else
                {
                    // Failed - log why for debugging
                    if (result != null)
                    {
                        _logger.Verbose($"Server {key} query incomplete: IsOnline={result.IsOnline}, IsQueried={result.IsQueried}, Error={result.ErrorMessage}");
                    }
                    
                    // Failed - increment failure count on existing entry
                    if (_servers.TryGetValue(key, out var existingServer))
                    {
                        existingServer.ConsecutiveFailures++;
                        existingServer.IsRefreshPending = false;
                        
                        // Mark offline after N consecutive failures
                        if (existingServer.ConsecutiveFailures >= failuresBeforeOffline)
                        {
                            existingServer.IsOnline = false;
                            existingServer.ErrorMessage = $"Offline after {existingServer.ConsecutiveFailures} failed queries";
                            _logger.Verbose($"Server {key} marked offline after {existingServer.ConsecutiveFailures} failures");
                        }
                        
                        ServerUpdated?.Invoke(this, existingServer);
                    }
                    else if (result != null)
                    {
                        // New server that failed on first query
                        result.ConsecutiveFailures = 1;
                        result.IsOnline = false;
                        result.IsRefreshPending = false;
                        _servers[key] = result;
                        ServerUpdated?.Invoke(this, result);
                    }
                }
                
                var current = Interlocked.Increment(ref completed);
                RefreshProgress?.Invoke(this, (int)((double)current / total * 100));
            }
        }, cancellationToken);
        
        // Fire off all queries with pipelining - don't wait for batches
        var queryTasks = new List<Task>();
        
        foreach (var endpoint in endpoints)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            
            // Wait for a slot to become available (limits concurrency)
            await concurrencySemaphore.WaitAsync(cancellationToken);
            
            // Fire query without waiting for it to complete (pipelining)
            queryTasks.Add(QueryServerWithRetryAsync(
                endpoint, retryAttempts, retryDelayMs, 
                concurrencySemaphore, completionChannel.Writer, 
                cancellationToken));
            
            // Small interval between sending queries
            if (intervalMs > 0)
                await Task.Delay(intervalMs, cancellationToken);
        }
        
        // Wait for all queries to complete
        await Task.WhenAll(queryTasks);
        
        // Signal that no more items will be written
        completionChannel.Writer.Complete();
        
        // Wait for processing to finish
        await processingTask;
    }

    private async Task QueryServerWithRetryAsync(
        IPEndPoint endpoint, 
        int retryAttempts, 
        int retryDelayMs,
        SemaphoreSlim semaphore,
        ChannelWriter<(IPEndPoint, ServerInfo?)> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            ServerInfo? result = null;
            
            for (int attempt = 1; attempt <= retryAttempts; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                
                using var queryClient = new ServerQueryClient();
                result = await queryClient.QueryServerAsync(endpoint, cancellationToken);
                
                if (result != null && result.IsOnline && result.IsQueried)
                    break; // Success, no need to retry
                
                if (attempt < retryAttempts)
                {
                    _logger.Verbose($"Retry {attempt}/{retryAttempts} for {endpoint} in {retryDelayMs}ms");
                    await Task.Delay(retryDelayMs, cancellationToken);
                }
            }
            
            // Write result to channel for processing
            await writer.WriteAsync((endpoint, result), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancelled - don't write anything
        }
        catch (Exception ex)
        {
            _logger.Verbose($"Query failed for {endpoint}: {ex.Message}");
            // Write null result to update progress
            await writer.WriteAsync((endpoint, null), cancellationToken);
        }
        finally
        {
            // Release semaphore slot for next query
            semaphore.Release();
        }
    }

    /// <summary>
    /// Gets servers filtered by various criteria.
    /// </summary>
    public IEnumerable<ServerInfo> GetFilteredServers(
        bool hideEmpty = false,
        bool hideFull = false,
        bool hidePassworded = false,
        GameModeType? gameMode = null,
        string? searchText = null,
        string? iwadFilter = null)
    {
        var query = _servers.Values.Where(s => s.IsOnline && s.IsQueried);

        if (hideEmpty)
            query = query.Where(s => !s.IsEmpty);

        if (hideFull)
            query = query.Where(s => !s.IsFull);

        if (hidePassworded)
            query = query.Where(s => !s.IsPassworded);

        if (gameMode.HasValue && gameMode.Value != GameModeType.Unknown)
            query = query.Where(s => s.GameMode.Type == gameMode.Value);

        if (!string.IsNullOrWhiteSpace(iwadFilter))
            query = query.Where(s => s.IWAD.Contains(iwadFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            query = query.Where(s => 
                s.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                s.Map.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                s.Address.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        }

        return query;
    }

    /// <summary>
    /// Resolves country codes for servers with XIP, XUN, or empty country values using IP geolocation.
    /// </summary>
    private async Task ResolveUnknownCountriesAsync(CancellationToken cancellationToken)
    {
        // Find servers that need IP2C lookup (XIP or empty country)
        var serversNeedingLookup = _servers.Values
            .Where(s => s.IsOnline && NeedsIp2CLookup(s.Country))
            .ToList();

        if (serversNeedingLookup.Count == 0)
            return;

        _logger.Info($"Resolving countries for {serversNeedingLookup.Count} servers via IP2C...");

        // Extract IP addresses (without port)
        var ipAddresses = serversNeedingLookup
            .Select(s => s.EndPoint?.Address.ToString())
            .Where(ip => !string.IsNullOrEmpty(ip))
            .Distinct()
            .ToList();

        // Batch lookup
        var results = await _ip2CountryService.LookupCountriesAsync(ipAddresses!);

        // Update servers with resolved countries
        int resolved = 0;
        foreach (var server in serversNeedingLookup)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var ip = server.EndPoint?.Address.ToString();
            if (ip != null && results.TryGetValue(ip, out var countryCode))
            {
                server.Country = countryCode;
                resolved++;
            }
            else
            {
                // Mark as unknown so we don't retry on next refresh
                server.Country = "??";
            }
            ServerUpdated?.Invoke(this, server);
        }

        _logger.Info($"IP2C resolved {resolved}/{serversNeedingLookup.Count} server countries");
    }

    /// <summary>
    /// Determines if a country code requires IP2C lookup.
    /// Only XIP (force lookup) and empty/null trigger lookups.
    /// "??" means already unknown/unresolved - don't retry.
    /// </summary>
    private static bool NeedsIp2CLookup(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
            return true;

        var upper = country.ToUpperInvariant();
        
        // XIP = force IP2C lookup (per Doomseeker protocol)
        // Empty/null also needs lookup
        // "??" and "XUN" mean already unknown - don't retry
        return upper == "XIP";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _masterClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

public class RefreshCompletedEventArgs : EventArgs
{
    public int TotalServers { get; }
    public int OnlineServers { get; }
    public string? Error { get; }
    public bool Success => Error == null;

    public RefreshCompletedEventArgs(int totalServers, int onlineServers, string? error)
    {
        TotalServers = totalServers;
        OnlineServers = onlineServers;
        Error = error;
    }
}
