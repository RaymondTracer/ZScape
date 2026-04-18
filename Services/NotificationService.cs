using Avalonia.Controls;
using Avalonia.Threading;
#if WINDOWS
using Microsoft.Toolkit.Uwp.Notifications;
#endif
using ZScape.Models;
using ZScape.Utilities;
using ZScape.Views;

namespace ZScape.Services;

/// <summary>
/// Handles server alert notifications, preferring native OS notifications when available.
/// </summary>
public class NotificationService : IDisposable
{
    private static readonly Lazy<NotificationService> _instance = new(() => new NotificationService());
    public static NotificationService Instance => _instance.Value;

    private readonly Dictionary<string, (ServerInfo Server, ServerAlertType Type)> _knownServers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ServerAlertNotificationWindow> _customWindows = [];
    private WeakReference<Window>? _ownerWindow;
    private bool _nativeActivationSubscribed;
    private bool _disposed;

    /// <summary>
    /// Event raised when user activates an alert action.
    /// </summary>
    public event EventHandler<ServerAlertEventArgs>? AlertClicked;

    private NotificationService()
    {
        TrySubscribeNativeActivation();
    }

    public void AttachWindow(Window owner)
    {
        _ownerWindow = new WeakReference<Window>(owner);
    }

    /// <summary>
    /// Shows a notification for a server coming online with players.
    /// </summary>
    /// <param name="server">The server that came online.</param>
    /// <param name="alertType">Whether this is a favorite or manual server alert.</param>
    /// <param name="displayModeOverride">Optional one-shot display mode override that does not alter saved settings.</param>
    /// <param name="isTestAlert">True when this alert is a debug/test notification and should not trigger game launch behavior.</param>
    public void ShowServerAlert(
        ServerInfo server,
        ServerAlertType alertType,
        NotificationDisplayMode? displayModeOverride = null,
        bool isTestAlert = false)
    {
        if (_disposed)
            return;

        RegisterKnownServer(server, alertType);

        var effectiveMode = GetEffectiveDisplayMode(displayModeOverride);

        if (!TryShowNativeServerAlert(server, alertType, effectiveMode, isTestAlert))
        {
            ShowCustomServerAlert(server, alertType, isTestAlert);
        }
    }

    /// <summary>
    /// Shows a batch notification when multiple servers come online.
    /// </summary>
    public void ShowMultipleServersAlert(
        IReadOnlyList<(ServerInfo Server, ServerAlertType Type)> servers,
        NotificationDisplayMode? displayModeOverride = null,
        bool isTestAlert = false)
    {
        if (_disposed || servers.Count == 0)
            return;

        if (servers.Count == 1)
        {
            ShowServerAlert(servers[0].Server, servers[0].Type, displayModeOverride, isTestAlert);
            return;
        }

        foreach (var (server, type) in servers)
        {
            RegisterKnownServer(server, type);
        }

        var effectiveMode = GetEffectiveDisplayMode(displayModeOverride);

        if (!TryShowNativeMultipleServersAlert(servers, effectiveMode, isTestAlert))
        {
            ShowCustomMultipleServersAlert(servers, isTestAlert);
        }
    }

    private static NotificationDisplayMode GetEffectiveDisplayMode(NotificationDisplayMode? displayModeOverride)
    {
        return displayModeOverride ?? SettingsService.Instance.Settings.AlertNotificationMode;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            if (_nativeActivationSubscribed)
            {
                _nativeActivationSubscribed = false;
            }

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var window in _customWindows.ToList())
                {
                    window.Close();
                }
                _customWindows.Clear();
            });
        }
        GC.SuppressFinalize(this);
    }

    private void TrySubscribeNativeActivation()
    {
#if WINDOWS
        if (_nativeActivationSubscribed || !OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            ToastNotificationManagerCompat.OnActivated += toastArgs => HandleNativeActivation(toastArgs.Argument);
            _nativeActivationSubscribed = true;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warning($"Native notification activation is unavailable: {ex.Message}");
        }
#endif
    }

#if WINDOWS
    private void HandleNativeActivation(string argument)
    {
        try
        {
            var args = ToastArguments.Parse(argument);
            if (!TryGetArgument(args, "action", out var actionValue) || !Enum.TryParse(actionValue, true, out ServerAlertAction action))
            {
                action = ServerAlertAction.FocusWindow;
            }

            TryGetArgument(args, "serverAddress", out var serverAddress);
            ServerInfo? server = null;
            ServerAlertType? alertType = null;
            var isTestAlert = TryGetArgument(args, "test", out var testValue) && IsTruthyArgument(testValue);

            if (!string.IsNullOrEmpty(serverAddress) && _knownServers.TryGetValue(serverAddress, out var knownServer))
            {
                server = knownServer.Server;
                alertType = knownServer.Type;
            }

            if (!alertType.HasValue && TryGetArgument(args, "alertType", out var alertTypeValue) && Enum.TryParse<ServerAlertType>(alertTypeValue, true, out var parsedAlertType))
            {
                alertType = parsedAlertType;
            }

            AlertClicked?.Invoke(this, new ServerAlertEventArgs(server, alertType, action, serverAddress, isTestAlert));
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Exception("Failed to handle native notification activation", ex);
        }
    }

    private static bool TryGetArgument(ToastArguments args, string key, out string value)
    {
        try
        {
            value = args[key];
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            value = string.Empty;
            return false;
        }
    }

    private static bool IsTruthyArgument(string value)
    {
        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
#endif

    private bool TryShowNativeServerAlert(
        ServerInfo server,
        ServerAlertType alertType,
        NotificationDisplayMode effectiveMode,
        bool isTestAlert)
    {
#if WINDOWS
        if (_disposed || !OperatingSystem.IsWindows() || effectiveMode != NotificationDisplayMode.Native)
        {
            return false;
        }

        try
        {
            var address = ServerRuleUtility.GetServerAddress(server);
            var title = alertType == ServerAlertType.Favorite ? "Favorite server online" : "Manual server online";
            var testValue = isTestAlert ? "true" : "false";

            new ToastContentBuilder()
                .AddArgument("serverAddress", address)
                .AddArgument("alertType", alertType.ToString())
                .AddArgument("action", ServerAlertAction.ShowServer.ToString())
                .AddArgument("test", testValue)
                .AddText(title)
                .AddText(server.Name)
                .AddText($"{server.CurrentPlayers}/{server.MaxClients} players on {server.Map} ({server.GameMode.Name})")
                .AddButton(new ToastButton()
                    .SetContent("Connect")
                    .AddArgument("action", ServerAlertAction.Connect.ToString())
                    .AddArgument("serverAddress", address)
                    .AddArgument("alertType", alertType.ToString())
                    .AddArgument("test", testValue))
                .AddButton(new ToastButton()
                    .SetContent("Show Server")
                    .AddArgument("action", ServerAlertAction.ShowServer.ToString())
                    .AddArgument("serverAddress", address)
                    .AddArgument("alertType", alertType.ToString())
                    .AddArgument("test", testValue))
                .Show(toast =>
                {
                    toast.Tag = address;
                    toast.Group = "server-alerts";
                    toast.ExpirationTime = DateTime.Now.AddMinutes(10);
                });

            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warning($"Native single-server notification failed, using custom fallback: {ex.Message}");
            return false;
        }
#else
        return false;
#endif
    }

    private bool TryShowNativeMultipleServersAlert(
        IReadOnlyList<(ServerInfo Server, ServerAlertType Type)> servers,
        NotificationDisplayMode effectiveMode,
        bool isTestAlert)
    {
#if WINDOWS
        if (_disposed || !OperatingSystem.IsWindows() || effectiveMode != NotificationDisplayMode.Native)
        {
            return false;
        }

        try
        {
            new ToastContentBuilder()
                .AddArgument("action", ServerAlertAction.FocusWindow.ToString())
                .AddArgument("test", isTestAlert ? "true" : "false")
                .AddText("Servers online")
                .AddText($"{servers.Count} favorite or manual servers are online")
                .AddText(BuildServerSummary(servers.Select(entry => entry.Server)))
                .AddButton(new ToastButton()
                    .SetContent("Show Window")
                    .AddArgument("action", ServerAlertAction.FocusWindow.ToString())
                    .AddArgument("test", isTestAlert ? "true" : "false"))
                .Show(toast =>
                {
                    toast.Group = "server-alerts";
                    toast.ExpirationTime = DateTime.Now.AddMinutes(10);
                });

            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warning($"Native multi-server notification failed, using custom fallback: {ex.Message}");
            return false;
        }
#else
        return false;
#endif
    }

    private void ShowCustomServerAlert(ServerInfo server, ServerAlertType alertType, bool isTestAlert)
    {
        var title = alertType == ServerAlertType.Favorite ? "Favorite server online" : "Manual server online";
        var detail = $"{server.CurrentPlayers}/{server.MaxClients} players on {server.Map} ({server.GameMode.Name})";

        ShowCustomNotification(
            title,
            server.Name,
            detail,
            [
                new AlertActionDefinition("Connect", ServerAlertAction.Connect, IsPrimary: true),
                new AlertActionDefinition("Show Server", ServerAlertAction.ShowServer, IsPrimary: false)
            ],
            server,
            alertType,
            isTestAlert);
    }

    private void ShowCustomMultipleServersAlert(IReadOnlyList<(ServerInfo Server, ServerAlertType Type)> servers, bool isTestAlert)
    {
        ShowCustomNotification(
            "Servers online",
            $"{servers.Count} favorite or manual servers are online",
            BuildServerSummary(servers.Select(entry => entry.Server)),
            [
                new AlertActionDefinition("Show Window", ServerAlertAction.FocusWindow, IsPrimary: true)
            ],
            server: null,
            alertType: null,
            isTestAlert);
    }

    private void ShowCustomNotification(
        string title,
        string message,
        string detail,
        IReadOnlyList<AlertActionDefinition> actions,
        ServerInfo? server,
        ServerAlertType? alertType,
        bool isTestAlert)
    {
        if (_disposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            var notificationWindow = new ServerAlertNotificationWindow(title, message, detail, actions);
            notificationWindow.ActionInvoked += (_, action) =>
            {
                AlertClicked?.Invoke(this, new ServerAlertEventArgs(
                    server,
                    alertType,
                    action,
                    server is null ? null : ServerRuleUtility.GetServerAddress(server),
                    isTestAlert));
            };
            notificationWindow.Closed += (_, _) =>
            {
                _customWindows.Remove(notificationWindow);
                PositionCustomWindows();
            };

            _customWindows.Add(notificationWindow);

            if (TryGetOwnerWindow(out var ownerWindow) && ownerWindow != null)
            {
                notificationWindow.Show(ownerWindow);
            }
            else
            {
                notificationWindow.Show();
            }

            PositionCustomWindows();
        });
    }

    private void PositionCustomWindows()
    {
        if (_customWindows.Count == 0)
        {
            return;
        }

        Avalonia.PixelRect? workArea = null;
        if (TryGetOwnerWindow(out var ownerWindow) && ownerWindow != null)
        {
            workArea = ownerWindow.Screens.ScreenFromVisual(ownerWindow)?.WorkingArea;
            if (workArea == null)
            {
                workArea = ownerWindow.Screens.Primary?.WorkingArea;
            }
        }

        var bounds = workArea ?? new Avalonia.PixelRect(0, 0, 1920, 1080);
        var right = bounds.X + bounds.Width - 16;
        var top = bounds.Y + 16;

        for (var index = 0; index < _customWindows.Count; index++)
        {
            var window = _customWindows[index];
            var width = (int)(window.Width > 0 ? window.Width : Math.Max(window.Bounds.Width, 420));
            var height = (int)(window.Height > 0 ? window.Height : Math.Max(window.Bounds.Height, 170));
            window.Position = new Avalonia.PixelPoint(right - width, top + (index * (height + 12)));
        }
    }

    private bool TryGetOwnerWindow(out Window? ownerWindow)
    {
        if (_ownerWindow != null && _ownerWindow.TryGetTarget(out ownerWindow))
        {
            return true;
        }

        ownerWindow = null;
        return false;
    }

    private void RegisterKnownServer(ServerInfo server, ServerAlertType alertType)
    {
        _knownServers[ServerRuleUtility.GetServerAddress(server)] = (server, alertType);
    }

    private static string BuildServerSummary(IEnumerable<ServerInfo> servers)
    {
        var names = servers
            .Select(server => server.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        if (names.Count == 0)
        {
            return "Open ZScape to review the servers.";
        }

        var summary = string.Join(", ", names);
        var remaining = servers
            .Select(server => server.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() - names.Count;

        return remaining > 0 ? $"{summary}, and {remaining} more" : summary;
    }
}

/// <summary>
/// Type of server alert.
/// </summary>
public enum ServerAlertType
{
    Favorite,
    Manual
}

/// <summary>
/// Preferred presentation mode for server alerts.
/// </summary>
public enum NotificationDisplayMode
{
    Native,
    Custom
}

/// <summary>
/// Action invoked from a server alert notification.
/// </summary>
public enum ServerAlertAction
{
    Connect,
    ShowServer,
    FocusWindow
}

/// <summary>
/// Event arguments for server alerts.
/// </summary>
public class ServerAlertEventArgs : EventArgs
{
    public ServerInfo? Server { get; }
    public ServerAlertType? AlertType { get; }
    public ServerAlertAction Action { get; }
    public string? ServerAddress { get; }
    public bool IsTestAlert { get; }

    public ServerAlertEventArgs(
        ServerInfo? server,
        ServerAlertType? alertType,
        ServerAlertAction action,
        string? serverAddress,
        bool isTestAlert = false)
    {
        Server = server;
        AlertType = alertType;
        Action = action;
        ServerAddress = serverAddress;
        IsTestAlert = isTestAlert;
    }
}

internal sealed record AlertActionDefinition(string Label, ServerAlertAction Action, bool IsPrimary);
