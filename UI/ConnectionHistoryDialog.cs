using ZScape.Services;

namespace ZScape.UI;

/// <summary>
/// Dialog for viewing and managing server connection history.
/// </summary>
public class ConnectionHistoryDialog : Form
{
    private ListView _historyListView = null!;
    private Button _reconnectButton = null!;
    private Button _removeButton = null!;
    private Button _clearButton = null!;
    private Button _copyAddressButton = null!;
    private Button _closeButton = null!;
    private NumericUpDown _maxEntriesNumeric = null!;
    private Label _infoLabel = null!;
    
    /// <summary>
    /// Event raised when user requests to reconnect to a history entry.
    /// </summary>
    public event EventHandler<ConnectionHistoryEntry>? ReconnectRequested;
    
    public ConnectionHistoryDialog()
    {
        InitializeComponent();
        ApplyDarkTheme();
        LoadHistory();
    }
    
    private void InitializeComponent()
    {
        Text = "Connection History";
        Size = new Size(750, 450);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(550, 350);
        
        // Main layout
        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(10)
        };
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));   // Header
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // List
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));   // Max entries setting
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));   // Bottom bar
        
        // Header
        var headerLabel = new Label
        {
            Text = "Recently Connected Servers",
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        mainPanel.Controls.Add(headerLabel, 0, 0);
        mainPanel.SetColumnSpan(headerLabel, 2);
        
        // ListView
        _historyListView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false
        };
        
        _historyListView.Columns.AddRange([
            new ColumnHeader { Text = "Server Name", Width = 220 },
            new ColumnHeader { Text = "Address", Width = 160 },
            new ColumnHeader { Text = "Last Played", Width = 120 },
            new ColumnHeader { Text = "Connections", Width = 85, TextAlign = HorizontalAlignment.Center },
            new ColumnHeader { Text = "Game Mode", Width = 100 }
        ]);
        
        _historyListView.DoubleClick += HistoryListView_DoubleClick;
        mainPanel.Controls.Add(_historyListView, 0, 1);
        
        // Button stack
        var buttonStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(5, 0, 0, 0)
        };
        
        _reconnectButton = new Button { Text = "Reconnect", Width = 105, Height = 28 };
        _reconnectButton.Click += ReconnectButton_Click;
        
        _copyAddressButton = new Button { Text = "Copy Address", Width = 105, Height = 28, Margin = new Padding(0, 5, 0, 0) };
        _copyAddressButton.Click += CopyAddressButton_Click;
        
        _removeButton = new Button { Text = "Remove", Width = 105, Height = 28, Margin = new Padding(0, 5, 0, 0) };
        _removeButton.Click += RemoveButton_Click;
        
        _clearButton = new Button { Text = "Clear All", Width = 105, Height = 28, Margin = new Padding(0, 5, 0, 0) };
        _clearButton.Click += ClearButton_Click;
        
        buttonStack.Controls.AddRange([_reconnectButton, _copyAddressButton, _removeButton, _clearButton]);
        mainPanel.Controls.Add(buttonStack, 1, 1);
        
        // Max history entries setting
        var maxEntriesPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        
        var maxEntriesLabel = new Label
        {
            Text = "Max history entries:",
            AutoSize = true,
            Margin = new Padding(0, 5, 5, 0)
        };
        
        _maxEntriesNumeric = new NumericUpDown
        {
            Minimum = 10,
            Maximum = 500,
            Value = SettingsService.Instance.Settings.MaxHistoryEntries,
            Width = 70
        };
        _maxEntriesNumeric.ValueChanged += MaxEntriesNumeric_ValueChanged;
        
        maxEntriesPanel.Controls.Add(maxEntriesLabel);
        maxEntriesPanel.Controls.Add(_maxEntriesNumeric);
        mainPanel.Controls.Add(maxEntriesPanel, 0, 2);
        mainPanel.SetColumnSpan(maxEntriesPanel, 2);
        
        // Bottom bar
        var bottomPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 5, 0, 0)
        };
        
        _closeButton = new Button
        {
            Text = "Close",
            Width = 80,
            Height = 28,
            DialogResult = DialogResult.Cancel
        };
        
        _infoLabel = new Label
        {
            Text = "Double-click an entry to reconnect",
            AutoSize = true,
            ForeColor = Color.Gray,
            Margin = new Padding(0, 8, 20, 0),
            Anchor = AnchorStyles.Left
        };
        
        bottomPanel.Controls.Add(_closeButton);
        bottomPanel.Controls.Add(_infoLabel);
        mainPanel.Controls.Add(bottomPanel, 0, 3);
        mainPanel.SetColumnSpan(bottomPanel, 2);
        
        Controls.Add(mainPanel);
        
        CancelButton = _closeButton;
    }
    
    private void LoadHistory()
    {
        _historyListView.Items.Clear();
        
        var settings = SettingsService.Instance.Settings;
        foreach (var entry in settings.ConnectionHistory)
        {
            var timeAgo = FormatTimeAgo(entry.LastConnected);
            var item = new ListViewItem(entry.ServerName);
            item.SubItems.Add(entry.FullAddress);
            item.SubItems.Add(timeAgo);
            item.SubItems.Add(entry.ConnectionCount.ToString());
            item.SubItems.Add(entry.GameMode ?? "");
            item.Tag = entry;
            _historyListView.Items.Add(item);
        }
    }
    
    private static string FormatTimeAgo(DateTime utcTime)
    {
        var elapsed = DateTime.UtcNow - utcTime;
        
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
        
        return utcTime.ToLocalTime().ToString("MMM d, yyyy");
    }
    
    private ConnectionHistoryEntry? GetSelectedEntry()
    {
        if (_historyListView.SelectedItems.Count == 0)
            return null;
        
        return _historyListView.SelectedItems[0].Tag as ConnectionHistoryEntry;
    }
    
    private void HistoryListView_DoubleClick(object? sender, EventArgs e)
    {
        var entry = GetSelectedEntry();
        if (entry != null)
        {
            ReconnectRequested?.Invoke(this, entry);
        }
    }
    
    private void ReconnectButton_Click(object? sender, EventArgs e)
    {
        var entry = GetSelectedEntry();
        if (entry != null)
        {
            ReconnectRequested?.Invoke(this, entry);
        }
    }
    
    private void CopyAddressButton_Click(object? sender, EventArgs e)
    {
        var entry = GetSelectedEntry();
        if (entry != null)
        {
            Clipboard.SetText(entry.FullAddress);
        }
    }
    
    private void RemoveButton_Click(object? sender, EventArgs e)
    {
        if (_historyListView.SelectedItems.Count == 0)
            return;
        
        var index = _historyListView.SelectedIndices[0];
        var settings = SettingsService.Instance.Settings;
        
        if (index < settings.ConnectionHistory.Count)
        {
            settings.ConnectionHistory.RemoveAt(index);
            _historyListView.Items.RemoveAt(index);
        }
    }
    
    private void ClearButton_Click(object? sender, EventArgs e)
    {
        if (MessageBox.Show(this, "Clear all connection history?", "Confirm Clear",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            SettingsService.Instance.ClearConnectionHistory();
            _historyListView.Items.Clear();
        }
    }
    
    private void MaxEntriesNumeric_ValueChanged(object? sender, EventArgs e)
    {
        SettingsService.Instance.Settings.MaxHistoryEntries = (int)_maxEntriesNumeric.Value;
    }
    
    private void ApplyDarkTheme()
    {
        BackColor = DarkTheme.PrimaryBackground;
        ForeColor = DarkTheme.TextPrimary;
        
        DarkTheme.Apply(this);
        DarkTheme.ApplyToButton(_reconnectButton);
        DarkTheme.ApplyToButton(_removeButton);
        DarkTheme.ApplyToButton(_clearButton);
        DarkTheme.ApplyToButton(_copyAddressButton);
        DarkTheme.ApplyToButton(_closeButton);
        DarkTheme.ApplyToListView(_historyListView);
        
        Utilities.DarkModeHelper.ApplyDarkTitleBar(this);
    }
}
