using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using ZScape.Models;
using ZScape.Services;

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
    
    private HistoryEntryViewModel? _selectedEntry;
    private ServerBrowserService? _serverBrowserService;

    public ConnectionHistoryDialog()
    {
        InitializeComponent();
        DataContext = this;
        
        // Handle Escape key
        KeyDown += OnDialogKeyDown;
        
        Loaded += (_, _) =>
        {
            LoadHistory();
            HistoryItemsControl.ItemsSource = HistoryEntries;
            MaxEntriesNumeric.Value = SettingsService.Instance.Settings.MaxHistoryEntries;
            MaxEntriesNumeric.ValueChanged += MaxEntriesNumeric_ValueChanged;
            
            // Set tracking mode combo box
            TrackingModeComboBox.SelectedIndex = (int)SettingsService.Instance.Settings.HistoryTrackingMode;
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
            var trackingMode = SettingsService.Instance.Settings.HistoryTrackingMode;
            foreach (var entry in HistoryEntries)
            {
                entry.SetRefreshing(trackingMode);
            }
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
        
        foreach (var entry in HistoryEntries)
        {
            bool matches = trackingMode switch
            {
                HistoryTrackingMode.ByAddress => entry.Entry.FullAddress.Equals(serverAddress, StringComparison.OrdinalIgnoreCase),
                HistoryTrackingMode.ByServerName => entry.Entry.ServerName.Equals(server.Name, StringComparison.OrdinalIgnoreCase),
                HistoryTrackingMode.Both => entry.Entry.FullAddress.Equals(serverAddress, StringComparison.OrdinalIgnoreCase) ||
                                            entry.Entry.ServerName.Equals(server.Name, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            
            if (matches)
            {
                entry.UpdateFromServer(server, trackingMode);
            }
        }
    }
    
    private void UpdateAllEntriesFromServerList()
    {
        if (_serverBrowserService == null) return;
        
        var trackingMode = SettingsService.Instance.Settings.HistoryTrackingMode;
        var servers = _serverBrowserService.Servers;
        var isRefreshing = _serverBrowserService.IsRefreshing;
        var hasEverRefreshed = _serverBrowserService.HasEverRefreshed;
        
        foreach (var entry in HistoryEntries)
        {
            ServerInfo? matchingServer = null;
            
            if (trackingMode == HistoryTrackingMode.ByAddress || trackingMode == HistoryTrackingMode.Both)
            {
                matchingServer = servers.FirstOrDefault(s => 
                    $"{s.Address}:{s.Port}".Equals(entry.Entry.FullAddress, StringComparison.OrdinalIgnoreCase));
            }
            
            if (matchingServer == null && (trackingMode == HistoryTrackingMode.ByServerName || trackingMode == HistoryTrackingMode.Both))
            {
                matchingServer = servers.FirstOrDefault(s => 
                    s.Name?.Equals(entry.Entry.ServerName, StringComparison.OrdinalIgnoreCase) == true);
            }
            
            if (matchingServer != null)
            {
                entry.UpdateFromServer(matchingServer, trackingMode);
            }
            else if (isRefreshing)
            {
                entry.SetRefreshing(trackingMode);
            }
            else if (!hasEverRefreshed)
            {
                entry.SetUnknown(trackingMode);
            }
            else
            {
                entry.SetOffline(trackingMode);
            }
        }
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
        HistoryEntries.Clear();
        var history = SettingsService.Instance.ConnectionHistory;
        int index = 0;
        foreach (var entry in history)
        {
            HistoryEntries.Add(new HistoryEntryViewModel(entry, index++));
        }
    }
    
    private void SelectEntry(HistoryEntryViewModel? entry)
    {
        // Deselect previous
        if (_selectedEntry != null)
            _selectedEntry.IsSelected = false;
        
        // Select new
        _selectedEntry = entry;
        if (_selectedEntry != null)
            _selectedEntry.IsSelected = true;
    }

    private HistoryEntryViewModel? GetSelectedEntry() => _selectedEntry;
    
    private void HistoryRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.DataContext is HistoryEntryViewModel vm)
        {
            SelectEntry(vm);
        }
    }
    
    private void HistoryRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border border && border.DataContext is HistoryEntryViewModel vm)
        {
            ReconnectRequested?.Invoke(this, vm.Entry);
        }
    }
    
    private void HistoryRow_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Border border && border.DataContext is HistoryEntryViewModel vm)
        {
            vm.IsHovered = true;
        }
    }
    
    private void HistoryRow_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border border && border.DataContext is HistoryEntryViewModel vm)
        {
            vm.IsHovered = false;
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

        var index = HistoryEntries.IndexOf(entry);
        if (index >= 0)
        {
            var history = SettingsService.Instance.ConnectionHistory;
            if (index < history.Count)
            {
                history.RemoveAt(index);
                SettingsService.Instance.SaveHistory();
                HistoryEntries.RemoveAt(index);
                _selectedEntry = null;
                
                // Update indices for remaining items
                for (int i = 0; i < HistoryEntries.Count; i++)
                {
                    HistoryEntries[i].Index = i;
                }
            }
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
            HistoryEntries.Clear();
            _selectedEntry = null;
        }
    }

    private void MaxEntriesNumeric_ValueChanged(object? sender, int e)
    {
        SettingsService.Instance.Settings.MaxHistoryEntries = e;
    }
    
    private void TrackingModeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TrackingModeComboBox.SelectedIndex >= 0)
        {
            SettingsService.Instance.Settings.HistoryTrackingMode = (HistoryTrackingMode)TrackingModeComboBox.SelectedIndex;
        }
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
    private static readonly IBrush EvenRowBrush = new SolidColorBrush(Color.Parse("#1E1E1E"));
    private static readonly IBrush OddRowBrush = new SolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.Parse("#094771"));
    private static readonly IBrush HoverBrush = new SolidColorBrush(Color.Parse("#2A2D2E"));
    
    private const string OfflineIndicator = "<Offline>";
    private const string RefreshingIndicator = "<Refreshing>";
    private const string UnknownIndicator = "<Unknown>";
    
    private bool _isSelected;
    private bool _isHovered;
    private int _index;
    private string? _displayServerName;
    private string? _displayAddress;
    
    public ConnectionHistoryEntry Entry { get; }
    
    public event PropertyChangedEventHandler? PropertyChanged;

    public HistoryEntryViewModel(ConnectionHistoryEntry entry, int index)
    {
        Entry = entry;
        _index = index;
        // Initialize display values - will be updated when server list is available
        _displayServerName = entry.ServerName;
        _displayAddress = entry.FullAddress;
    }
    
    /// <summary>
    /// Sets the entry to unknown state (before first refresh).
    /// </summary>
    public void SetUnknown(HistoryTrackingMode trackingMode)
    {
        switch (trackingMode)
        {
            case HistoryTrackingMode.ByAddress:
                DisplayAddress = Entry.FullAddress;
                DisplayServerName = UnknownIndicator;
                break;
            case HistoryTrackingMode.ByServerName:
                DisplayServerName = Entry.ServerName;
                DisplayAddress = UnknownIndicator;
                break;
            case HistoryTrackingMode.Both:
                DisplayServerName = Entry.ServerName;
                DisplayAddress = Entry.FullAddress;
                break;
        }
    }
    
    public int Index
    {
        get => _index;
        set
        {
            _index = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowBackground)));
        }
    }
    
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowBackground)));
            }
        }
    }
    
    public bool IsHovered
    {
        get => _isHovered;
        set
        {
            if (_isHovered != value)
            {
                _isHovered = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHovered)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowBackground)));
            }
        }
    }
    
    public IBrush RowBackground
    {
        get
        {
            if (_isSelected) return SelectedBrush;
            if (_isHovered) return HoverBrush;
            return _index % 2 == 0 ? EvenRowBrush : OddRowBrush;
        }
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
    public string? GameMode => Entry.GameMode;
    
    /// <summary>
    /// Updates display values from a matching server.
    /// </summary>
    public void UpdateFromServer(ServerInfo server, HistoryTrackingMode trackingMode)
    {
        var serverAddress = $"{server.Address}:{server.Port}";
        
        switch (trackingMode)
        {
            case HistoryTrackingMode.ByAddress:
                // Address is static, update name from server
                DisplayAddress = Entry.FullAddress;
                if (server.IsOnline && server.IsQueried)
                    DisplayServerName = server.Name ?? Entry.ServerName;
                else if (!server.IsQueried)
                    DisplayServerName = RefreshingIndicator;
                else
                    DisplayServerName = OfflineIndicator;
                break;
                
            case HistoryTrackingMode.ByServerName:
                // Name is static, update address from server
                DisplayServerName = Entry.ServerName;
                if (server.IsOnline && server.IsQueried)
                    DisplayAddress = serverAddress;
                else if (!server.IsQueried)
                    DisplayAddress = RefreshingIndicator;
                else
                    DisplayAddress = OfflineIndicator;
                break;
                
            case HistoryTrackingMode.Both:
                // Both are tracked exactly as recorded
                DisplayServerName = Entry.ServerName;
                DisplayAddress = Entry.FullAddress;
                break;
        }
    }
    
    /// <summary>
    /// Sets the entry to refreshing state.
    /// </summary>
    public void SetRefreshing(HistoryTrackingMode trackingMode)
    {
        switch (trackingMode)
        {
            case HistoryTrackingMode.ByAddress:
                DisplayAddress = Entry.FullAddress;
                DisplayServerName = RefreshingIndicator;
                break;
            case HistoryTrackingMode.ByServerName:
                DisplayServerName = Entry.ServerName;
                DisplayAddress = RefreshingIndicator;
                break;
            case HistoryTrackingMode.Both:
                DisplayServerName = Entry.ServerName;
                DisplayAddress = Entry.FullAddress;
                break;
        }
    }
    
    /// <summary>
    /// Sets the entry to offline state.
    /// </summary>
    public void SetOffline(HistoryTrackingMode trackingMode)
    {
        switch (trackingMode)
        {
            case HistoryTrackingMode.ByAddress:
                DisplayAddress = Entry.FullAddress;
                DisplayServerName = OfflineIndicator;
                break;
            case HistoryTrackingMode.ByServerName:
                DisplayServerName = Entry.ServerName;
                DisplayAddress = OfflineIndicator;
                break;
            case HistoryTrackingMode.Both:
                DisplayServerName = Entry.ServerName;
                DisplayAddress = Entry.FullAddress;
                break;
        }
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
}
