using System.Media;
using ZScape.Models;

namespace ZScape.Services;

/// <summary>
/// Handles Windows notifications for server alerts.
/// Uses a hidden NotifyIcon for balloon tips.
/// </summary>
public class NotificationService : IDisposable
{
    private static readonly Lazy<NotificationService> _instance = new(() => new NotificationService());
    public static NotificationService Instance => _instance.Value;
    
    private NotifyIcon? _notifyIcon;
    private bool _disposed;
    
    /// <summary>
    /// Event raised when user clicks on a notification.
    /// </summary>
    public event EventHandler<ServerAlertEventArgs>? AlertClicked;
    
    private ServerAlertEventArgs? _pendingAlert;
    
    private NotificationService()
    {
        InitializeNotifyIcon();
    }
    
    private void InitializeNotifyIcon()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Visible = false, // Only visible when showing notification
            Text = "ZScape"
        };
        
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;
    }
    
    private void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        if (_pendingAlert != null)
        {
            AlertClicked?.Invoke(this, _pendingAlert);
            _pendingAlert = null;
        }
    }
    
    /// <summary>
    /// Shows a notification for a server coming online with players.
    /// </summary>
    /// <param name="server">The server that came online.</param>
    /// <param name="alertType">Whether this is a favorite or manual server alert.</param>
    public void ShowServerAlert(ServerInfo server, ServerAlertType alertType)
    {
        if (_notifyIcon == null || _disposed)
            return;
        
        var typeText = alertType == ServerAlertType.Favorite ? "Favorite" : "Manual";
        var title = $"{typeText} Server Online";
        var message = $"{server.Name}\n" +
                      $"{server.CurrentPlayers} player(s) - {server.Map}\n" +
                      $"{server.Address}:{server.Port}";
        
        // Store for click handling
        _pendingAlert = new ServerAlertEventArgs(server, alertType);
        
        // Play notification sound
        SystemSounds.Asterisk.Play();
        
        // Show balloon tip
        _notifyIcon.Visible = true;
        _notifyIcon.ShowBalloonTip(
            timeout: 5000,
            tipTitle: title,
            tipText: message,
            tipIcon: ToolTipIcon.Info);
        
        // Hide icon after balloon closes (approximately)
        Task.Delay(6000).ContinueWith(_ =>
        {
            if (!_disposed && _notifyIcon != null)
            {
                try
                {
                    _notifyIcon.Visible = false;
                }
                catch { /* Ignore if already disposed */ }
            }
        });
        
        LoggingService.Instance.Info($"Alert: {typeText} server '{server.Name}' is online with {server.CurrentPlayers} players");
    }
    
    /// <summary>
    /// Shows a batch notification when multiple servers come online.
    /// </summary>
    public void ShowMultipleServersAlert(IReadOnlyList<(ServerInfo Server, ServerAlertType Type)> servers)
    {
        if (_notifyIcon == null || _disposed || servers.Count == 0)
            return;
        
        if (servers.Count == 1)
        {
            ShowServerAlert(servers[0].Server, servers[0].Type);
            return;
        }
        
        var title = $"{servers.Count} Servers Online";
        var lines = servers
            .Take(3)
            .Select(s => $"{s.Server.Name} ({s.Server.CurrentPlayers}p)")
            .ToList();
        
        if (servers.Count > 3)
            lines.Add($"...and {servers.Count - 3} more");
        
        var message = string.Join("\n", lines);
        
        // Store first server for click handling
        _pendingAlert = new ServerAlertEventArgs(servers[0].Server, servers[0].Type);
        
        SystemSounds.Asterisk.Play();
        
        _notifyIcon.Visible = true;
        _notifyIcon.ShowBalloonTip(
            timeout: 5000,
            tipTitle: title,
            tipText: message,
            tipIcon: ToolTipIcon.Info);
        
        Task.Delay(6000).ContinueWith(_ =>
        {
            if (!_disposed && _notifyIcon != null)
            {
                try { _notifyIcon.Visible = false; }
                catch { }
            }
        });
        
        LoggingService.Instance.Info($"Alert: {servers.Count} servers came online");
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
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
