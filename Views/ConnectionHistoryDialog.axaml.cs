using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using ZScape.Controls;
using ZScape.Models;
using ZScape.Services;
using ZScape.Utilities;

namespace ZScape.Views;

/// <summary>
/// Dialog for viewing and managing server connection history.
/// </summary>
public partial class ConnectionHistoryDialog : Window
{
    /// <summary>
    /// Event raised when user requests to reconnect to a history entry.
    /// </summary>
    public event EventHandler<ConnectionHistoryEntry>? ReconnectRequested;

    public ObservableCollection<HistoryEntryViewModel> HistoryEntries { get; } = [];
    private readonly List<HistoryEntryViewModel> _allHistoryEntries = [];
    private readonly List<ListViewSortDescriptor> _sortDescriptors =
    [
        new(8, "last-played", false),
        new(1, "name", true)
    ];

    private ServerBrowserService? _serverBrowserService;

    public ConnectionHistoryDialog()
    {
        InitializeComponent();
        DataContext = this;
        
        // Configure the list as a compact version of the main server browser.
        HistoryListView.AlternatingRowColors = true;
        HistoryListView.RowBaseBackgroundPath = "RowBackground";
        HistoryListView.AddColumn(new ListViewColumn
        {
            Key = "status", Header = "Status", Width = 80, MinWidth = 10,
            BindingPath = "StatusDisplay",
            Foreground = Brushes.Gray,
            CanSort = true
        });
        HistoryListView.AddColumn(new ListViewColumn
        {
            Key = "name", Header = "Server Name", Width = 250, IsStar = true, MinWidth = 10,
            BindingPath = "DisplayServerName",
            TextTrimming = TextTrimming.CharacterEllipsis,
            CellPadding = new Thickness(8, 0),
            CanUserHide = false,
            CanSort = true
        });
        HistoryListView.AddColumn(new ListViewColumn
        {
            Key = "players", Header = "Players", Width = 75, MinWidth = 10,
            HeaderToolTip = "p = playing count/limit; c = connected-client capacity. A current/max c appears when spectators affect the count.",
            BindingPath = "PlayersDisplay",
            CanSort = true,
            DefaultSortDescending = true
        });
        HistoryListView.AddColumn(new ListViewColumn
        {
            Key = "ping", Header = "Ping", Width = 60, MinWidth = 10,
            BindingPath = "PingDisplay",
            CanSort = true
        });
        HistoryListView.AddColumn(new ListViewColumn
        {
            Key = "map", Header = "Map", Width = 95, MinWidth = 10,
            BindingPath = "Map",
            TextTrimming = TextTrimming.CharacterEllipsis,
            CanSort = true
        });
        HistoryListView.AddColumn(new ListViewColumn
        {
            Key = "mode", Header = "Mode", Width = 90, MinWidth = 10,
            BindingPath = "DisplayGameMode",
            TextTrimming = TextTrimming.CharacterEllipsis,
            CanSort = true
        });
        HistoryListView.AddColumn(new ListViewColumn
        {
            Key = "iwad", Header = "IWAD", Width = 100, MinWidth = 10,
            BindingPath = "IWAD",
            TextTrimming = TextTrimming.CharacterEllipsis,
            CanSort = true
        });
        HistoryListView.AddColumn(new ListViewColumn
        {
            Key = "address", Header = "Address", Width = 150, MinWidth = 10,
            BindingPath = "DisplayAddress",
            TextTrimming = TextTrimming.CharacterEllipsis,
            CanSort = true
        });
        HistoryListView.AddColumn(new ListViewColumn
        {
            Key = "last-played", Header = "Last Played", Width = 105, MinWidth = 10,
            BindingPath = "LastPlayedDisplay",
            CanSort = true,
            DefaultSortDescending = true
        });
        HistoryListView.AddColumn(new ListViewColumn
        {
            Key = "count", Header = "Visits", Width = 60, MinWidth = 10,
            BindingPath = "ConnectionCount",
            CanSort = true,
            DefaultSortDescending = true
        });
        HistoryListView.Build(ListViewOverflowMode.AutoScroll);
        HistoryListView.SetSortDescriptors(_sortDescriptors);

        // Wire up row events
        HistoryListView.RowDoubleTapped += OnHistoryRowDoubleTapped;
        HistoryListView.SelectionChanged += (_, _) => UpdateActionState();
        HistoryListView.SortRequested += HistoryListView_SortRequested;

        // Handle Escape key
        KeyDown += OnDialogKeyDown;
        
        Loaded += (_, _) =>
        {
            LoadHistory();
            MaxEntriesNumeric.Value = SettingsService.Instance.Settings.MaxHistoryEntries;
            MaxEntriesNumeric.ValueChanged += MaxEntriesNumeric_ValueChanged;
            
            // Set tracking mode combo box
            TrackingModeComboBox.SelectedIndex = (int)SettingsService.Instance.Settings.HistoryTrackingMode;
            UpdateTrackingModeHelp();
            if (_serverBrowserService != null)
                UpdateAllEntriesFromServerList();
            else
                ApplyHistoryView();
        };
        
        Closed += OnDialogClosed;
    }
    
    /// <summary>
    /// Sets the server browser service for live updates.
    /// </summary>
    public void SetServerBrowserService(ServerBrowserService service)
    {
        _serverBrowserService = service;
        _serverBrowserService.ServerUpdated += OnServerUpdated;
        _serverBrowserService.RefreshStarted += OnRefreshStarted;
        _serverBrowserService.RefreshCompleted += OnRefreshCompleted;
        
        // Initial update with current server state
        UpdateAllEntriesFromServerList();
    }
    
    private void OnDialogClosed(object? sender, EventArgs e)
    {
        if (_serverBrowserService != null)
        {
            _serverBrowserService.ServerUpdated -= OnServerUpdated;
            _serverBrowserService.RefreshStarted -= OnRefreshStarted;
            _serverBrowserService.RefreshCompleted -= OnRefreshCompleted;
        }
    }
    
    private void OnRefreshStarted(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var entry in _allHistoryEntries)
            {
                entry.SetRefreshing();
            }
            ApplyHistoryView();
        });
    }
    
    private void OnRefreshCompleted(object? sender, RefreshCompletedEventArgs e)
    {
        Dispatcher.UIThread.Post(UpdateAllEntriesFromServerList);
    }
    
    private void OnServerUpdated(object? sender, ServerInfo server)
    {
        Dispatcher.UIThread.Post(() => UpdateEntryFromServer(server));
    }
    
    private void UpdateEntryFromServer(ServerInfo server)
    {
        var trackingMode = SettingsService.Instance.Settings.HistoryTrackingMode;
        var serverAddress = $"{server.Address}:{server.Port}";
        
        var updated = false;
        foreach (var entry in _allHistoryEntries)
        {
            bool matches = trackingMode switch
            {
                HistoryTrackingMode.ByAddress => entry.Entry.FullAddress.Equals(serverAddress, StringComparison.OrdinalIgnoreCase),
                HistoryTrackingMode.ByServerName => entry.Entry.ServerName.Equals(server.Name, StringComparison.OrdinalIgnoreCase),
                HistoryTrackingMode.Both => entry.Entry.FullAddress.Equals(serverAddress, StringComparison.OrdinalIgnoreCase) &&
                                            entry.Entry.ServerName.Equals(server.Name, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            
            if (matches)
            {
                entry.UpdateFromServer(server, trackingMode);
                updated = true;
            }
        }

        if (updated)
            ApplyHistoryView();
    }
    
    private void UpdateAllEntriesFromServerList()
    {
        if (_serverBrowserService == null) return;
        
        var trackingMode = SettingsService.Instance.Settings.HistoryTrackingMode;
        var servers = _serverBrowserService.Servers;
        var isRefreshing = _serverBrowserService.IsRefreshing;
        var hasEverRefreshed = _serverBrowserService.HasEverRefreshed;
        
        foreach (var entry in _allHistoryEntries)
        {
            ServerInfo? matchingServer = trackingMode switch
            {
                HistoryTrackingMode.ByAddress => servers.FirstOrDefault(server =>
                    $"{server.Address}:{server.Port}".Equals(
                        entry.Entry.FullAddress,
                        StringComparison.OrdinalIgnoreCase)),
                HistoryTrackingMode.ByServerName => servers.FirstOrDefault(server =>
                    server.Name?.Equals(
                        entry.Entry.ServerName,
                        StringComparison.OrdinalIgnoreCase) == true),
                HistoryTrackingMode.Both => servers.FirstOrDefault(server =>
                    $"{server.Address}:{server.Port}".Equals(
                        entry.Entry.FullAddress,
                        StringComparison.OrdinalIgnoreCase)
                    && server.Name?.Equals(
                        entry.Entry.ServerName,
                        StringComparison.OrdinalIgnoreCase) == true),
                _ => null
            };
            
            if (matchingServer != null)
            {
                entry.UpdateFromServer(matchingServer, trackingMode);
            }
            else if (isRefreshing)
            {
                entry.SetRefreshing();
            }
            else if (!hasEverRefreshed)
            {
                entry.SetUnknown();
            }
            else
            {
                entry.SetOffline();
            }
        }
        ApplyHistoryView();
    }
    
    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void LoadHistory()
    {
        _allHistoryEntries.Clear();
        var history = SettingsService.Instance.ConnectionHistory;
        int index = 0;
        foreach (var entry in history)
        {
            _allHistoryEntries.Add(new HistoryEntryViewModel(entry, index++));
        }
    }

    private void HistorySearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyHistoryView();
    }

    private void HistoryListView_SortRequested(
        object? sender,
        ListViewSortEventArgs e)
    {
        _sortDescriptors.Clear();
        _sortDescriptors.AddRange(e.SortDescriptors);
        ApplyHistoryView();
    }

    private void ApplyHistoryView()
    {
        var selected = GetSelectedEntry();
        var query = HistorySearchBox?.Text?.Trim() ?? string.Empty;
        IEnumerable<HistoryEntryViewModel> view = _allHistoryEntries;

        if (!string.IsNullOrWhiteSpace(query))
        {
            view = view.Where(entry => entry.MatchesSearch(query));
        }

        IOrderedEnumerable<HistoryEntryViewModel>? ordered = null;
        foreach (var descriptor in _sortDescriptors)
        {
            ordered = descriptor.ColumnKey switch
            {
                "status" => ApplyHistorySort(
                    view,
                    ordered,
                    entry => entry.StatusSortOrder,
                    descriptor.Ascending),
                "name" => ApplyHistorySort(
                    view,
                    ordered,
                    entry => entry.DisplayServerName,
                    descriptor.Ascending,
                    StringComparer.OrdinalIgnoreCase),
                "players" => ApplyHistorySort(
                    view,
                    ordered,
                    entry => entry.PlayerSortValue,
                    descriptor.Ascending),
                "ping" => ApplyHistorySort(
                    view,
                    ordered,
                    entry => entry.PingSortValue,
                    descriptor.Ascending),
                "map" => ApplyHistorySort(
                    view,
                    ordered,
                    entry => entry.Map,
                    descriptor.Ascending,
                    StringComparer.OrdinalIgnoreCase),
                "mode" => ApplyHistorySort(
                    view,
                    ordered,
                    entry => entry.DisplayGameMode,
                    descriptor.Ascending,
                    StringComparer.OrdinalIgnoreCase),
                "iwad" => ApplyHistorySort(
                    view,
                    ordered,
                    entry => entry.IWAD,
                    descriptor.Ascending,
                    StringComparer.OrdinalIgnoreCase),
                "address" => ApplyHistorySort(
                    view,
                    ordered,
                    entry => entry.DisplayAddress,
                    descriptor.Ascending,
                    StringComparer.OrdinalIgnoreCase),
                "last-played" => ApplyHistorySort(
                    view,
                    ordered,
                    entry => entry.Entry.LastConnected,
                    descriptor.Ascending),
                "count" => ApplyHistorySort(
                    view,
                    ordered,
                    entry => entry.ConnectionCount,
                    descriptor.Ascending),
                _ => ordered
            };
        }

        var materialized = (ordered ?? view.OrderBy(_ => 0)).ToList();
        HistoryEntries.Clear();
        foreach (var entry in materialized)
            HistoryEntries.Add(entry);
        HistoryListView.ItemsSource = HistoryEntries;

        if (selected != null && HistoryEntries.Contains(selected))
            HistoryListView.SelectItem(selected);
        else
            HistoryListView.ClearSelection();

        var onlineCount = HistoryEntries.Count(entry => entry.IsOnline);
        InfoLabel.Text = string.IsNullOrWhiteSpace(query)
            ? $"{HistoryEntries.Count} entries · {onlineCount} online · Double-click to reconnect"
            : $"{HistoryEntries.Count} matches · {onlineCount} online";
        UpdateActionState();
    }

    private static IOrderedEnumerable<HistoryEntryViewModel> ApplyHistorySort<TKey>(
        IEnumerable<HistoryEntryViewModel> source,
        IOrderedEnumerable<HistoryEntryViewModel>? ordered,
        Func<HistoryEntryViewModel, TKey> selector,
        bool ascending,
        IComparer<TKey>? comparer = null)
    {
        if (ordered == null)
        {
            return ascending
                ? source.OrderBy(selector, comparer)
                : source.OrderByDescending(selector, comparer);
        }

        return ascending
            ? ordered.ThenBy(selector, comparer)
            : ordered.ThenByDescending(selector, comparer);
    }

    private void UpdateActionState()
    {
        var hasSelection = GetSelectedEntry() != null;
        ReconnectButton.IsEnabled = hasSelection;
        CopyAddressButton.IsEnabled = hasSelection;
        RemoveButton.IsEnabled = hasSelection;
        ClearButton.IsEnabled = _allHistoryEntries.Count > 0;
    }
    
    private HistoryEntryViewModel? GetSelectedEntry() => HistoryListView.SelectedItem as HistoryEntryViewModel;
    
    private void OnHistoryRowDoubleTapped(object? sender, ListViewRowEventArgs e)
    {
        if (e.DataContext is HistoryEntryViewModel vm)
        {
            ReconnectRequested?.Invoke(this, vm.Entry);
        }
    }

    private void ReconnectButton_Click(object? sender, RoutedEventArgs e)
    {
        var entry = GetSelectedEntry();
        if (entry != null)
        {
            ReconnectRequested?.Invoke(this, entry.Entry);
        }
    }

    private async void CopyAddressButton_Click(object? sender, RoutedEventArgs e)
    {
        var entry = GetSelectedEntry();
        if (entry != null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(entry.FullAddress);
        }
    }

    private void RemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        var entry = GetSelectedEntry();
        if (entry == null) return;

        var history = SettingsService.Instance.ConnectionHistory;
        var historyIndex = history.IndexOf(entry.Entry);
        if (historyIndex >= 0)
        {
            history.RemoveAt(historyIndex);
            _allHistoryEntries.Remove(entry);
            SettingsService.Instance.SaveHistory();
            for (var i = 0; i < _allHistoryEntries.Count; i++)
                _allHistoryEntries[i].Index = i;
            ApplyHistoryView();
        }
    }

    private async void ClearButton_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Confirm Clear",
            Width = 300,
            Height = 120,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };
        
        var result = false;
        var grid = new Grid { Margin = new Avalonia.Thickness(15) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        grid.RowDefinitions.Add(new RowDefinition(new GridLength(40)));
        
        var text = new TextBlock 
        { 
            Text = "Clear all connection history?",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetRow(text, 0);
        grid.Children.Add(text);
        
        var buttonPanel = new StackPanel 
        { 
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10
        };
        Grid.SetRow(buttonPanel, 1);
        
        var yesButton = new Button { Content = "Yes", Width = 70 };
        yesButton.Click += (_, _) => { result = true; dialog.Close(); };
        var noButton = new Button { Content = "No", Width = 70 };
        noButton.Click += (_, _) => { dialog.Close(); };
        
        buttonPanel.Children.Add(yesButton);
        buttonPanel.Children.Add(noButton);
        grid.Children.Add(buttonPanel);
        
        dialog.Content = grid;
        await dialog.ShowDialog(this);
        
        if (result)
        {
            SettingsService.Instance.ClearConnectionHistory();
            _allHistoryEntries.Clear();
            ApplyHistoryView();
        }
    }

    private void MaxEntriesNumeric_ValueChanged(object? sender, int e)
    {
        SettingsService.Instance.Settings.MaxHistoryEntries = e;
        SettingsService.Instance.Save();
    }
    
    private void TrackingModeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TrackingModeComboBox.SelectedIndex >= 0)
        {
            SettingsService.Instance.Settings.HistoryTrackingMode = (HistoryTrackingMode)TrackingModeComboBox.SelectedIndex;
            SettingsService.Instance.Save();
            UpdateTrackingModeHelp();
            UpdateAllEntriesFromServerList();
        }
    }

    private void UpdateTrackingModeHelp()
    {
        TrackingModeHelpText.Text =
            SettingsService.Instance.Settings.HistoryTrackingMode switch
            {
                HistoryTrackingMode.ByAddress =>
                    "Address keeps one entry per IP:port and refreshes the displayed server name. Best when an endpoint is stable.",
                HistoryTrackingMode.ByServerName =>
                    "Server name merges matching names even when the address changes. Best for communities that move servers.",
                HistoryTrackingMode.Both =>
                    "Address + name keeps each exact pair separate. Neither a matching name nor a matching address alone will merge entries.",
                _ => string.Empty
            };
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

/// <summary>
/// View model for connection history entries.
/// </summary>
public class HistoryEntryViewModel : INotifyPropertyChanged
{
    private int _index;
    private string? _displayServerName;
    private string? _displayAddress;
    private ServerInfo? _liveServer;
    private HistoryLiveStatus _liveStatus = HistoryLiveStatus.Unknown;
    
    public ConnectionHistoryEntry Entry { get; }
    
    public event PropertyChangedEventHandler? PropertyChanged;

    public HistoryEntryViewModel(ConnectionHistoryEntry entry, int index)
    {
        Entry = entry;
        _index = index;
        _displayServerName = entry.ServerName;
        _displayAddress = entry.FullAddress;
    }
    
    public void SetUnknown()
    {
        ResetIdentity();
        _liveServer = null;
        SetLiveStatus(HistoryLiveStatus.Unknown);
        NotifyLiveColumns();
    }
    
    public int Index
    {
        get => _index;
        set => _index = value;
    }
    
    /// <summary>
    /// Display server name - may show live data or status indicators.
    /// </summary>
    public string DisplayServerName
    {
        get => _displayServerName ?? Entry.ServerName;
        private set
        {
            if (_displayServerName != value)
            {
                _displayServerName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayServerName)));
            }
        }
    }
    
    /// <summary>
    /// Display address - may show live data or status indicators.
    /// </summary>
    public string DisplayAddress
    {
        get => _displayAddress ?? Entry.FullAddress;
        private set
        {
            if (_displayAddress != value)
            {
                _displayAddress = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayAddress)));
            }
        }
    }
    
    public string ServerName => Entry.ServerName;
    public string FullAddress => Entry.FullAddress;
    public int ConnectionCount => Entry.ConnectionCount;
    public string StatusDisplay => _liveStatus switch
    {
        HistoryLiveStatus.Online => "Online",
        HistoryLiveStatus.Refreshing => "Refreshing",
        HistoryLiveStatus.Offline => "Offline",
        _ => "Unknown"
    };
    public int StatusSortOrder => _liveStatus switch
    {
        HistoryLiveStatus.Online => 0,
        HistoryLiveStatus.Refreshing => 1,
        HistoryLiveStatus.Unknown => 2,
        _ => 3
    };
    public bool IsOnline => _liveStatus == HistoryLiveStatus.Online;
    public int PlayerSortValue => _liveServer?.HumanPlayerCount ?? -1;
    public int PingSortValue => _liveServer?.Ping ?? int.MaxValue;
    public string PlayersDisplay
    {
        get
        {
            if (_liveServer == null || !IsOnline)
                return "—";

            return _liveServer.PlayerCountDisplay;
        }
    }
    public string PingDisplay => _liveServer != null && IsOnline && _liveServer.Ping >= 0
        ? _liveServer.Ping.ToString()
        : "—";
    public string Map => _liveServer != null && IsOnline
        ? _liveServer.Map
        : "—";
    public string DisplayGameMode => _liveServer != null && IsOnline
        ? _liveServer.GameMode.ShortName
        : Entry.GameMode ?? "—";
    public string IWAD => _liveServer != null && IsOnline
        ? _liveServer.IWAD
        : "—";
    public IBrush RowBackground => _liveStatus switch
    {
        HistoryLiveStatus.Online => ThemeService.GetBrush("HistoryOnlineRowBrush", "#183A2A"),
        HistoryLiveStatus.Refreshing => ThemeService.GetBrush("RowPasswordedBrush", "#3C3728"),
        HistoryLiveStatus.Offline => ThemeService.GetBrush("RowEmptyBrush", "#2D2D32"),
        _ => Brushes.Transparent
    };
    
    /// <summary>
    /// Updates display values from a matching server.
    /// </summary>
    public void UpdateFromServer(ServerInfo server, HistoryTrackingMode trackingMode)
    {
        var serverAddress = $"{server.Address}:{server.Port}";
        _liveServer = server;
        
        switch (trackingMode)
        {
            case HistoryTrackingMode.ByAddress:
                DisplayAddress = Entry.FullAddress;
                DisplayServerName = !string.IsNullOrWhiteSpace(server.Name)
                    ? server.Name
                    : Entry.ServerName;
                break;
                
            case HistoryTrackingMode.ByServerName:
                DisplayServerName = Entry.ServerName;
                DisplayAddress = serverAddress;
                break;
                
            case HistoryTrackingMode.Both:
                DisplayServerName = Entry.ServerName;
                DisplayAddress = Entry.FullAddress;
                break;
        }

        SetLiveStatus(!server.IsQueried
            ? HistoryLiveStatus.Refreshing
            : server.IsOnline
                ? HistoryLiveStatus.Online
                : HistoryLiveStatus.Offline);
        NotifyLiveColumns();
    }
    
    /// <summary>
    /// Sets the entry to refreshing state.
    /// </summary>
    public void SetRefreshing()
    {
        ResetIdentity();
        SetLiveStatus(HistoryLiveStatus.Refreshing);
        NotifyLiveColumns();
    }
    
    /// <summary>
    /// Sets the entry to offline state.
    /// </summary>
    public void SetOffline()
    {
        ResetIdentity();
        _liveServer = null;
        SetLiveStatus(HistoryLiveStatus.Offline);
        NotifyLiveColumns();
    }

    public bool MatchesSearch(string query)
    {
        return new[]
        {
            DisplayServerName,
            DisplayAddress,
            Map,
            DisplayGameMode,
            IWAD,
            StatusDisplay
        }.Any(value => TextMatchUtility.IsLooseSearchMatch(value, query));
    }

    private void ResetIdentity()
    {
        DisplayServerName = Entry.ServerName;
        DisplayAddress = Entry.FullAddress;
    }

    private void SetLiveStatus(HistoryLiveStatus status)
    {
        if (_liveStatus == status)
            return;
        _liveStatus = status;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusSortOrder)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOnline)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowBackground)));
    }

    private void NotifyLiveColumns()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayersDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayerSortValue)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PingDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PingSortValue)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Map)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayGameMode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IWAD)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowBackground)));
    }

    public string LastPlayedDisplay
    {
        get
        {
            var elapsed = DateTime.UtcNow - Entry.LastConnected;

            if (elapsed.TotalMinutes < 1)
                return "just now";
            if (elapsed.TotalMinutes < 60)
                return $"{(int)elapsed.TotalMinutes}m ago";
            if (elapsed.TotalHours < 24)
                return $"{(int)elapsed.TotalHours}h ago";
            if (elapsed.TotalDays < 7)
                return $"{(int)elapsed.TotalDays}d ago";
            if (elapsed.TotalDays < 30)
                return $"{(int)(elapsed.TotalDays / 7)}w ago";

            return Entry.LastConnected.ToLocalTime().ToString("MMM d, yyyy");
        }
    }

    private enum HistoryLiveStatus
    {
        Unknown,
        Refreshing,
        Online,
        Offline
    }
}
