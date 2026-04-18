using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System.Net;
using ZScape.Models;
using ZScape.Services;
using ZScape.Utilities;

namespace ZScape.Views;

/// <summary>
/// Debug-only helper window for manually sending notification test payloads.
/// </summary>
public partial class NotificationDebugWindow : Window
{
    private readonly NotificationService _notificationService = NotificationService.Instance;
    private readonly Func<ServerInfo?> _primaryServerProvider;
    private readonly Func<IReadOnlyList<ServerInfo>> _serverSetProvider;

    private readonly (NotificationDisplayMode? ModeOverride, string Label)[] _dispatchModes =
    [
        (null, "Use current setting"),
        (NotificationDisplayMode.Native, "Force native notifications"),
        (NotificationDisplayMode.Custom, "Force custom popup notifications")
    ];

    public NotificationDebugWindow()
        : this(() => null, () => [])
    {
    }

    public NotificationDebugWindow(
        Func<ServerInfo?> primaryServerProvider,
        Func<IReadOnlyList<ServerInfo>> serverSetProvider)
    {
        _primaryServerProvider = primaryServerProvider;
        _serverSetProvider = serverSetProvider;

        InitializeComponent();

        PopulateDispatchModeComboBox();
        RefreshContextSummary();

        _notificationService.AlertClicked += NotificationService_AlertClicked;

        Activated += (_, _) => RefreshContextSummary();
        Closed += NotificationDebugWindow_Closed;
    }

    private void NotificationDebugWindow_Closed(object? sender, EventArgs e)
    {
        _notificationService.AlertClicked -= NotificationService_AlertClicked;
    }

    private void PopulateDispatchModeComboBox()
    {
        DispatchModeComboBox.Items.Clear();
        foreach (var dispatchMode in _dispatchModes)
        {
            DispatchModeComboBox.Items.Add(new ComboBoxItem { Content = dispatchMode.Label });
        }

        DispatchModeComboBox.SelectedIndex = 0;
    }

    private void DispatchModeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshContextSummary();
    }

    private void OptionsChanged(object? sender, RoutedEventArgs e)
    {
        RefreshContextSummary();
    }

    private void RefreshContextButton_Click(object? sender, RoutedEventArgs e)
    {
        RefreshContextSummary();
    }

    private void FavoriteAlertButton_Click(object? sender, RoutedEventArgs e)
    {
        SendSingleAlert(ServerAlertType.Favorite);
    }

    private void ManualAlertButton_Click(object? sender, RoutedEventArgs e)
    {
        SendSingleAlert(ServerAlertType.Manual);
    }

    private void MultiAlertButton_Click(object? sender, RoutedEventArgs e)
    {
        var alerts = BuildMultiServerAlerts();
        var modeOverride = GetSelectedModeOverride();

        _notificationService.ShowMultipleServersAlert(alerts, modeOverride, isTestAlert: true);
        SetDispatchFeedback($"Sent {alerts.Count} test alerts using {GetModeDescription(modeOverride)}.");
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SendSingleAlert(ServerAlertType alertType)
    {
        var server = ResolveSingleServer();
        var modeOverride = GetSelectedModeOverride();

        _notificationService.ShowServerAlert(server, alertType, modeOverride, isTestAlert: true);
        SetDispatchFeedback($"Sent a {alertType.ToString().ToLowerInvariant()} test alert using {GetModeDescription(modeOverride)} for {server.Name}.");
    }

    private NotificationDisplayMode? GetSelectedModeOverride()
    {
        var index = DispatchModeComboBox.SelectedIndex;
        return index >= 0 && index < _dispatchModes.Length
            ? _dispatchModes[index].ModeOverride
            : null;
    }

    private void RefreshContextSummary()
    {
        var savedMode = SettingsService.Instance.Settings.AlertNotificationMode;
        var modeOverride = GetSelectedModeOverride();
        var effectiveModeText = GetModeDescription(modeOverride);
        var currentServer = UseSelectedServerCheckBox.IsChecked == true ? _primaryServerProvider() : null;
        var serverText = currentServer != null
            ? $"Real server: {currentServer.Name} [{currentServer.PlayerCountDisplay}] at {currentServer.Address}:{currentServer.Port}."
            : "No real server selected. Sample debug server data will be used.";

        var nativeNote = modeOverride == NotificationDisplayMode.Native && !OperatingSystem.IsWindows()
            ? "Forced native mode will fall back to the custom popup on non-Windows builds."
            : "Test alert clicks never launch Zandronum; they only validate delivery and activation routing.";

        ContextSummaryTextBlock.Text =
            $"Saved mode: {AppConstants.NotificationDisplayModeLabels.GetLabel(savedMode)}\n" +
            $"Dispatch mode: {effectiveModeText}\n" +
            $"{serverText}\n" +
            nativeNote;
    }

    private string GetModeDescription(NotificationDisplayMode? modeOverride)
    {
        if (modeOverride.HasValue)
        {
            return AppConstants.NotificationDisplayModeLabels.GetLabel(modeOverride.Value);
        }

        return $"current setting ({AppConstants.NotificationDisplayModeLabels.GetLabel(SettingsService.Instance.Settings.AlertNotificationMode)})";
    }

    private ServerInfo ResolveSingleServer()
    {
        if (UseSelectedServerCheckBox.IsChecked == true)
        {
            var currentServer = _primaryServerProvider();
            if (currentServer != null)
            {
                return currentServer;
            }
        }

        return BuildSampleServer(0);
    }

    private List<(ServerInfo Server, ServerAlertType Type)> BuildMultiServerAlerts()
    {
        var alerts = new List<(ServerInfo Server, ServerAlertType Type)>();
        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (UseSelectedServerCheckBox.IsChecked == true)
        {
            foreach (var server in _serverSetProvider())
            {
                var address = ServerRuleUtility.GetServerAddress(server);
                if (!seenAddresses.Add(address))
                {
                    continue;
                }

                var alertType = alerts.Count % 2 == 0 ? ServerAlertType.Favorite : ServerAlertType.Manual;
                alerts.Add((server, alertType));

                if (alerts.Count == 3)
                {
                    break;
                }
            }
        }

        var sampleIndex = 0;
        while (alerts.Count < 3)
        {
            var sampleServer = BuildSampleServer(sampleIndex + 1);
            alerts.Add((sampleServer, alerts.Count % 2 == 0 ? ServerAlertType.Favorite : ServerAlertType.Manual));
            sampleIndex++;
        }

        return alerts;
    }

    private static ServerInfo BuildSampleServer(int index)
    {
        var names = new[]
        {
            "Debug Signal",
            "Packet Garden",
            "Toast Harness",
            "Popup Fallback"
        };
        var maps = new[] { "MAP01", "MAP07", "MAP20", "E1M1" };
        var modes = new[]
        {
            GameMode.FromType(GameModeType.Cooperative),
            GameMode.FromType(GameModeType.Deathmatch),
            GameMode.FromType(GameModeType.CaptureTheFlag),
            GameMode.FromType(GameModeType.Duel)
        };

        var slot = Math.Abs(index) % names.Length;
        var port = 10666 + index;

        return new ServerInfo
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, port),
            Name = names[slot],
            Map = maps[slot],
            CurrentPlayers = 3 + slot,
            MaxClients = 16,
            MaxPlayers = 16,
            GameMode = modes[slot],
            IsOnline = true,
            Country = "US"
        };
    }

    private void NotificationService_AlertClicked(object? sender, ServerAlertEventArgs e)
    {
        if (!e.IsTestAlert)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var target = e.Server?.Name ?? e.ServerAddress ?? "window focus";
            LastActivationTextBlock.Text = $"Last activation: {e.Action} for {target} at {DateTime.Now:HH:mm:ss}.";
            LastActivationTextBlock.Foreground = Brushes.White;
        });
    }

    private void SetDispatchFeedback(string message)
    {
        LastDispatchTextBlock.Text = $"{message} ({DateTime.Now:HH:mm:ss})";
        LastDispatchTextBlock.Foreground = Brushes.White;
        RefreshContextSummary();
    }
}