using System.Collections.Concurrent;
using System.Net;
using ZScape.Models;
using ZScape.Services;

internal static class ServerRefreshChecks
{
    public static void Run()
    {
        CheckTransitions();
        // Keep asynchronous service checks independent of the headless UI's
        // synchronization context; all query results are deterministic fakes.
        Task.Run(CheckRefreshWorkflowsAsync).GetAwaiter().GetResult();
    }

    private static void CheckTransitions()
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, 10666);
        var key = endpoint.ToString();
        var servers = new ConcurrentDictionary<string, ServerInfo>();
        var existing = OnlineServer(endpoint);
        existing.IsRefreshPending = true;
        existing.ConsecutiveFailures = 1;
        servers[key] = existing;

        var change = Apply(servers, key, FailedServer(endpoint), threshold: 3);
        RegressionAssert.That(ReferenceEquals(change.UpdatedServer, existing) && !change.MembershipChanged &&
              existing.IsOnline && existing.ConsecutiveFailures == 2 && !existing.IsRefreshPending &&
              existing.Name == "Known server" && existing.CurrentPlayers == 4,
            "Failed refresh retains the last complete server snapshot below the offline threshold");

        existing.IsRefreshPending = true;
        Apply(servers, key, result: null, threshold: 3);
        RegressionAssert.That(!existing.IsOnline && existing.ConsecutiveFailures == 3 &&
              existing.ErrorMessage == "Offline after 3 failed queries" && !existing.IsRefreshPending,
            "Failed refresh marks a known server offline exactly at the configured threshold");

        var recovery = OnlineServer(endpoint);
        recovery.ConsecutiveFailures = 8;
        recovery.IsRefreshPending = true;
        change = Apply(servers, key, recovery, threshold: 3);
        RegressionAssert.That(ReferenceEquals(servers[key], recovery) && ReferenceEquals(change.UpdatedServer, recovery) &&
              recovery.IsOnline && recovery.ConsecutiveFailures == 0 && !recovery.IsRefreshPending,
            "A complete successful response replaces the snapshot and clears pending/failure state");

        recovery.ConsecutiveFailures = 2;
        recovery.IsRefreshPending = true;
        change = ServerBrowserService.ApplyRefreshOutcome(servers, key, OnlineServer(endpoint),
            ServerBrowserService.RefreshOutcome.Cancelled, failuresBeforeOffline: 2);
        RegressionAssert.That(ReferenceEquals(servers[key], recovery) && ReferenceEquals(change.UpdatedServer, recovery) &&
              recovery.IsOnline && recovery.ConsecutiveFailures == 2 && !recovery.IsRefreshPending,
            "Cancellation does not replace the old snapshot or count a failure");

        var placeholder = new ServerInfo { EndPoint = endpoint, IsRefreshPending = true };
        servers[key] = placeholder;
        var incomplete = new ServerInfo { EndPoint = endpoint, IsOnline = true, IsQueried = false };
        change = Apply(servers, key, incomplete, threshold: 3);
        RegressionAssert.That(!servers.ContainsKey(key) && change.MembershipChanged && change.UpdatedServer == null,
            "An incomplete response removes its placeholder instead of retaining a blank server row");

        servers[key] = new ServerInfo { EndPoint = endpoint, IsRefreshPending = true };
        change = ServerBrowserService.ApplyRefreshOutcome(servers, key, result: null,
            ServerBrowserService.RefreshOutcome.Cancelled, failuresBeforeOffline: 3);
        RegressionAssert.That(!servers.ContainsKey(key) && change.MembershipChanged,
            "Cancelled placeholder-only refresh removes its temporary row");

        change = Apply(servers, key, FailedServer(endpoint), threshold: 3);
        RegressionAssert.That(servers.IsEmpty && change.UpdatedServer == null && !change.MembershipChanged,
            "An unsuccessful response cannot recreate a removed endpoint as a blank server row");

        existing = OnlineServer(endpoint);
        servers[key] = existing;
        Apply(servers, key, result: null, threshold: 0);
        RegressionAssert.That(!existing.IsOnline && existing.ConsecutiveFailures == 1,
            "An invalid offline threshold is clamped to one consistently");
    }

    private static async Task CheckRefreshWorkflowsAsync()
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, 10666);
        var settings = IsolatedSettings();
        settings.Settings.FavoriteServers.Add(endpoint.ToString());
        bool fail = false;
        using var browser = new ServerBrowserService(settings, (address, _) =>
            Task.FromResult<ServerInfo?>(fail ? FailedServer(address) : OnlineServer(address)));

        await browser.RefreshServerAsync(endpoint);
        fail = true;
        await browser.RefreshServerAsync(endpoint);
        var known = browser.Servers.Single();
        RegressionAssert.That(known.IsOnline && known.ConsecutiveFailures == 1 && !known.IsRefreshPending,
            "Direct refresh uses the shared failure transition below the offline threshold");
        await browser.RefreshFavoritesAsync();
        RegressionAssert.That(ReferenceEquals(browser.Servers.Single(), known) && !known.IsOnline &&
              known.ConsecutiveFailures == 2 && !known.IsRefreshPending,
            "Direct and bulk refreshes share one failure count and offline threshold");
        fail = false;
        await browser.RefreshServerAsync(endpoint);
        RegressionAssert.That(browser.Servers.Single().IsOnline && browser.Servers.Single().ConsecutiveFailures == 0,
            "A successful direct refresh recovers a server marked offline by a bulk refresh");

        var missingSettings = IsolatedSettings();
        missingSettings.Settings.FavoriteServers.Add(endpoint.ToString());
        using var missingBrowser = new ServerBrowserService(missingSettings, (address, _) =>
            Task.FromResult<ServerInfo?>(FailedServer(address)));
        var progress = new List<int>();
        missingBrowser.RefreshProgress += (_, percent) => progress.Add(percent);
        await missingBrowser.RefreshFavoritesAsync();
        RegressionAssert.That(missingBrowser.TotalServers == 0 && progress.SequenceEqual(new[] { 100 }),
            "Failed placeholder removal still counts towards completed bulk refresh progress");

        await CheckLastAttemptCancellationAsync(endpoint);
        await CheckBatchCancellationDrainsQueriesAsync(endpoint);
    }

    private static async Task CheckLastAttemptCancellationAsync(IPEndPoint endpoint)
    {
        var settings = IsolatedSettings();
        var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool delayed = false;
        using var browser = new ServerBrowserService(settings, async (address, _) =>
        {
            if (!delayed)
                return OnlineServer(address);
            queryStarted.SetResult();
            await finishQuery.Task;
            return FailedServer(address);
        });
        await browser.RefreshServerAsync(endpoint);
        var known = browser.Servers.Single();
        delayed = true;
        using var cancellation = new CancellationTokenSource();
        var refresh = browser.RefreshServerAsync(endpoint, cancellation.Token);
        await queryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        finishQuery.SetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        RegressionAssert.That(known.IsOnline && known.ConsecutiveFailures == 0 && !known.IsRefreshPending,
            "Cancellation on the final query attempt is not counted as a failed response");
    }

    private static async Task CheckBatchCancellationDrainsQueriesAsync(IPEndPoint endpoint)
    {
        var settings = IsolatedSettings();
        settings.Settings.MaxConcurrentQueries = 1;
        settings.Settings.QueryIntervalMs = 1000;
        settings.Settings.FavoriteServers.Add(endpoint.ToString());
        settings.Settings.FavoriteServers.Add(new IPEndPoint(IPAddress.Loopback, endpoint.Port + 1).ToString());
        var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int startedCount = 0;
        using var browser = new ServerBrowserService(settings, async (address, _) =>
        {
            Interlocked.Increment(ref startedCount);
            queryStarted.SetResult();
            // Model a query that cannot finish its cleanup until explicitly
            // released, even after its caller's cancellation token is set.
            await finishQuery.Task;
            return FailedServer(address);
        });
        using var cancellation = new CancellationTokenSource();
        var refresh = browser.RefreshFavoritesAsync(cancellation.Token);
        await queryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try
        {
            var first = await Task.WhenAny(refresh, Task.Delay(100));
            RegressionAssert.That(!ReferenceEquals(first, refresh) && browser.IsRefreshing,
                "Cancelling launch pacing waits for active query cleanup before ending the refresh");
        }
        finally
        {
            finishQuery.TrySetResult();
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }
        RegressionAssert.That(startedCount == 1 && !browser.IsRefreshing && browser.TotalServers == 0,
            "Bulk cancellation drains active queries and removes both active and unstarted placeholders");
    }

    private static SettingsService IsolatedSettings()
    {
        // This directory is never created or saved; no portable user settings
        // or network data are read by these checks.
        var settings = new SettingsService(Path.Combine(AppContext.BaseDirectory, "refresh-check-" + Guid.NewGuid()));
        settings.Settings.QueryRetryAttempts = 1;
        settings.Settings.QueryIntervalMs = 1;
        settings.Settings.ConsecutiveFailuresBeforeOffline = 2;
        return settings;
    }

    private static ServerInfo OnlineServer(IPEndPoint endpoint) => new()
    {
        EndPoint = endpoint,
        Name = "Known server",
        IsOnline = true,
        IsQueried = true,
        CurrentPlayers = 4,
        Country = "AU"
    };

    private static ServerInfo FailedServer(IPEndPoint endpoint) => new()
    {
        EndPoint = endpoint,
        IsOnline = false,
        IsQueried = false,
        ErrorMessage = "Test timeout"
    };

    private static ServerBrowserService.RefreshChange Apply(
        ConcurrentDictionary<string, ServerInfo> servers, string key, ServerInfo? result, int threshold) =>
        ServerBrowserService.ApplyRefreshOutcome(servers, key, result,
            ServerBrowserService.RefreshOutcome.Completed, threshold);

}
