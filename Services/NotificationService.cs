using ZScape.Models;

namespace ZScape.Services;

/// <summary>
/// Handles desktop notifications for server alerts.
/// Cross-platform stub - actual notifications can be implemented per-platform later.
/// </summary>
public class NotificationService : IDisposable
{
    private static readonly Lazy<NotificationService> _instance = new(() => new NotificationService());
    public static NotificationService Instance => _instance.Value;
    
    private bool _disposed;
    
    /// <summary>
    /// Event raised when user clicks on a notification.
    /// </summary>
    public event EventHandler<ServerAlertEventArgs>? AlertClicked;
    
    private NotificationService()
    {
        // Cross-platform notification initialization can be added here
    }
    
    /// <summary>
    /// Shows a notification for a server coming online with players.
    /// </summary>
    /// <param name="server">The server that came online.</param>
    /// <param name="alertType">Whether this is a favorite or manual server alert.</param>
    public void ShowServerAlert(ServerInfo server, ServerAlertType alertType)
    {
        if (_disposed)
            return;
        
        var typeText = alertType == ServerAlertType.Favorite ? "Favorite" : "Manual";
        
        // TODO: Implement cross-platform notifications
        // For now, just log the alert
        LoggingService.Instance.Info($"Alert: {typeText} server '{server.Name}' is online with {server.CurrentPlayers} players");
    }
    
    /// <summary>
    /// Shows a batch notification when multiple servers come online.
    /// </summary>
    public void ShowMultipleServersAlert(IReadOnlyList<(ServerInfo Server, ServerAlertType Type)> servers)
    {
        if (_disposed || servers.Count == 0)
            return;
        
        if (servers.Count == 1)
        {
            ShowServerAlert(servers[0].Server, servers[0].Type);
            return;
        }
        
        // TODO: Implement cross-platform notifications
        LoggingService.Instance.Info($"Alert: {servers.Count} servers came online");
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
        GC.SuppressFinalize(this);
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
/// Event arguments for server alerts.
/// </summary>
public class ServerAlertEventArgs : EventArgs
{
    public ServerInfo Server { get; }
    public ServerAlertType AlertType { get; }
    
    public ServerAlertEventArgs(ServerInfo server, ServerAlertType alertType)
    {
        Server = server;
        AlertType = alertType;
    }
}
