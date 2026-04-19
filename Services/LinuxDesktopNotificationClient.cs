using Tmds.DBus.Protocol;
using ZScape.Models;

namespace ZScape.Services;

internal sealed class LinuxDesktopNotificationClient : IDisposable
{
    private const string NotificationServiceName = "org.freedesktop.Notifications";
    private const string NotificationPath = "/org/freedesktop/Notifications";
    private const string NotificationInterface = "org.freedesktop.Notifications";
    private const string NotifySignature = "susssasa{sv}i";
    private const string DefaultActionKey = "default";
    private const int TimeoutMilliseconds = 10000;

    private readonly Connection _connection = new(Address.Session);
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<uint, LinuxNotificationContext> _activeNotifications = [];
    private readonly Action<ServerAlertEventArgs> _alertInvoked;

    private IDisposable? _actionSubscription;
    private IDisposable? _closedSubscription;
    private bool _isInitialized;
    private bool _isUnavailable;
    private bool _disposed;

    public LinuxDesktopNotificationClient(Action<ServerAlertEventArgs> alertInvoked)
    {
        _alertInvoked = alertInvoked;
    }

    public async Task<bool> TryShowAsync(NativeNotificationRequest request)
    {
        if (_disposed || _isUnavailable)
        {
            return false;
        }

        try
        {
            await EnsureInitializedAsync();
        }
        catch (Exception ex)
        {
            _isUnavailable = true;
            LoggingService.Instance.Warning($"Linux notification service unavailable: {ex.Message}");
            return false;
        }

        await _sendLock.WaitAsync();
        try
        {
            var notificationId = await SendNotificationAsync(request);
            if (notificationId == 0)
            {
                return false;
            }

            _activeNotifications[notificationId] = LinuxNotificationContext.FromRequest(notificationId, request);
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warning($"Failed to send Linux desktop notification: {ex.Message}");
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _actionSubscription?.Dispose();
        _closedSubscription?.Dispose();
        _initializationLock.Dispose();
        _sendLock.Dispose();
        _activeNotifications.Clear();
    }

    private async Task EnsureInitializedAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        await _initializationLock.WaitAsync();
        try
        {
            if (_isInitialized)
            {
                return;
            }

            await _connection.ConnectAsync();

            _actionSubscription = await _connection.AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Interface = NotificationInterface,
                    Member = "ActionInvoked"
                },
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    return new LinuxActionInvokedSignal(reader.ReadUInt32(), reader.ReadString());
                },
                static (exception, signal, _, handlerState) =>
                {
                    var client = (LinuxDesktopNotificationClient)handlerState;
                    client.HandleActionInvoked(exception, signal);
                },
                readerState: null,
                handlerState: this,
                emitOnCapturedContext: false);

            _closedSubscription = await _connection.AddMatchAsync(
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Interface = NotificationInterface,
                    Member = "NotificationClosed"
                },
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    return new LinuxNotificationClosedSignal(reader.ReadUInt32(), reader.ReadUInt32());
                },
                static (exception, signal, _, handlerState) =>
                {
                    var client = (LinuxDesktopNotificationClient)handlerState;
                    client.HandleNotificationClosed(exception, signal);
                },
                readerState: null,
                handlerState: this,
                emitOnCapturedContext: false);

            _isInitialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<uint> SendNotificationAsync(NativeNotificationRequest request)
    {
        var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            NotificationServiceName,
            NotificationPath,
            NotificationInterface,
            "Notify",
            NotifySignature,
            MessageFlags.None);
        writer.WriteString("ZScape");
        writer.WriteUInt32(0);
        writer.WriteString(string.Empty);
        writer.WriteString(request.Title);
        writer.WriteString(BuildBody(request));
        writer.WriteArray(BuildActions(request));
        writer.WriteDictionary(new Dictionary<string, VariantValue>());
        writer.WriteInt32(TimeoutMilliseconds);

        return await _connection.CallMethodAsync(
            writer.CreateMessage(),
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return reader.ReadUInt32();
            });
    }

    private void HandleActionInvoked(Exception? exception, LinuxActionInvokedSignal signal)
    {
        if (exception != null)
        {
            LoggingService.Instance.Warning($"Linux notification action handler failed: {exception.Message}");
            return;
        }

        if (!_activeNotifications.TryGetValue(signal.NotificationId, out var context))
        {
            return;
        }

        var action = context.ResolveAction(signal.ActionKey);
        _alertInvoked(new ServerAlertEventArgs(
            context.Server,
            context.AlertType,
            action,
            context.ServerAddress,
            context.IsTestAlert));
    }

    private void HandleNotificationClosed(Exception? exception, LinuxNotificationClosedSignal signal)
    {
        if (exception != null)
        {
            LoggingService.Instance.Warning($"Linux notification close handler failed: {exception.Message}");
            return;
        }

        _activeNotifications.Remove(signal.NotificationId);
    }

    private static string BuildBody(NativeNotificationRequest request)
    {
        return string.IsNullOrWhiteSpace(request.Detail)
            ? request.Message
            : $"{request.Message}\n{request.Detail}";
    }

    private static string[] BuildActions(NativeNotificationRequest request)
    {
        if (request.Actions.Count == 0)
        {
            return [];
        }

        var actions = new List<string> { DefaultActionKey, "Open ZScape" };
        foreach (var action in request.Actions)
        {
            actions.Add(GetActionKey(action.Action));
            actions.Add(action.Label);
        }

        return [.. actions];
    }

    private static string GetActionKey(ServerAlertAction action)
    {
        return action switch
        {
            ServerAlertAction.Connect => "connect",
            ServerAlertAction.ShowServer => "show-server",
            ServerAlertAction.FocusWindow => "focus-window",
            _ => action.ToString().ToLowerInvariant()
        };
    }

    private sealed record LinuxActionInvokedSignal(uint NotificationId, string ActionKey);

    private sealed record LinuxNotificationClosedSignal(uint NotificationId, uint Reason);

    private sealed record LinuxNotificationContext(
        uint NotificationId,
        ServerInfo? Server,
        ServerAlertType? AlertType,
        string? ServerAddress,
        ServerAlertAction DefaultAction,
        Dictionary<string, ServerAlertAction> Actions,
        bool IsTestAlert)
    {
        public static LinuxNotificationContext FromRequest(uint notificationId, NativeNotificationRequest request)
        {
            var actions = request.Actions.ToDictionary(
                action => GetActionKey(action.Action),
                action => action.Action,
                StringComparer.OrdinalIgnoreCase);

            return new LinuxNotificationContext(
                notificationId,
                request.Server,
                request.AlertType,
                request.ServerAddress,
                request.DefaultAction,
                actions,
                request.IsTestAlert);
        }

        public ServerAlertAction ResolveAction(string actionKey)
        {
            if (string.IsNullOrWhiteSpace(actionKey) || actionKey.Equals(DefaultActionKey, StringComparison.OrdinalIgnoreCase))
            {
                return DefaultAction;
            }

            return Actions.TryGetValue(actionKey, out var action) ? action : DefaultAction;
        }
    }
}