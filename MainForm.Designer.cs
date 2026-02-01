using ZScape.Utilities;

namespace ZScape;

partial class MainForm
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        
        // Main menu
        menuStrip = new MenuStrip();
        fileMenu = new ToolStripMenuItem();
        refreshMenuItem = new ToolStripMenuItem();
        stopMenuItem = new ToolStripMenuItem();
        toolStripSeparator1 = new ToolStripSeparator();
        fetchWadsMenuItem = new ToolStripMenuItem();
        exitMenuItem = new ToolStripMenuItem();
        viewMenu = new ToolStripMenuItem();
        verboseMenuItem = new ToolStripMenuItem();
        hexDumpMenuItem = new ToolStripMenuItem();
        toolStripSeparator2 = new ToolStripSeparator();
        showLogPanelMenuItem = new ToolStripMenuItem();
        settingsMenu = new ToolStripMenuItem();
        refreshOnLaunchMenuItem = new ToolStripMenuItem();
        autoRefreshMenuItem = new ToolStripMenuItem();
        autoRefreshFavoritesOnlyMenuItem = new ToolStripMenuItem();
        wadBrowserMenuItem = new ToolStripMenuItem();
        testingVersionsMenuItem = new ToolStripMenuItem();
        settingsMenuItem = new ToolStripMenuItem();
        helpMenu = new ToolStripMenuItem();
        aboutMenuItem = new ToolStripMenuItem();

        // Toolbar
        toolStrip = new ToolStrip();
        refreshButton = new ToolStripButton();
        stopButton = new ToolStripButton();
        toolStripSeparator3 = new ToolStripSeparator();
        searchLabel = new ToolStripLabel();
        searchBox = new ToolStripTextBox();
        toolStripSeparator4 = new ToolStripSeparator();
        hideEmptyCheckBox = new ToolStripButton();
        hideBotOnlyCheckBox = new ToolStripButton();
        hideFullCheckBox = new ToolStripButton();
        hidePasswordedCheckBox = new ToolStripButton();
        showFavoritesOnlyCheckBox = new ToolStripButton();
        toolStripSeparator5 = new ToolStripSeparator();
        gameModeLabel = new ToolStripLabel();
        gameModeComboBox = new ToolStripComboBox();
        toolStripSeparator6 = new ToolStripSeparator();
        advancedFilterButton = new ToolStripButton();
        toolStripSeparator7 = new ToolStripSeparator();
        historyButton = new ToolStripButton();

        // Main split container
        mainSplitContainer = new SplitContainer();
        
        // Server list
        serverListView = new DataGridView();
        
        // Details layout panel (3 columns: Server Details, WADs, Players)
        detailsTableLayoutPanel = new TableLayoutPanel();
        
        // Server details panel
        serverDetailsPanel = new Panel();
        serverDetailsLabel = new Label();
        serverInfoTextBox = new RichTextBox();
        
        // WADs panel
        wadsPanel = new Panel();
        wadsLabel = new Label();
        wadsListView = new ListView();
        
        // Player list panel
        playerListPanel = new Panel();
        playerListLabel = new Label();
        playerListView = new ListView();
        playerNameColumn = new ColumnHeader();
        playerScoreColumn = new ColumnHeader();
        playerPingColumn = new ColumnHeader();
        playerTeamColumn = new ColumnHeader();
        
        // Log panel
        logPanel = new Panel();
        logLabel = new Label();
        logTextBox = new RichTextBox();
        
        // Status bar
        statusStrip = new StatusStrip();
        serverCountLabel = new ToolStripStatusLabel();
        playerCountLabel = new ToolStripStatusLabel();
        statusLabel = new ToolStripStatusLabel();
        progressBar = new ToolStripProgressBar();

        // Context menu
        serverContextMenu = new ContextMenuStrip(components);
        connectMenuItem = new ToolStripMenuItem();
        downloadWadsMenuItem = new ToolStripMenuItem();
        copyConnectMenuItem = new ToolStripMenuItem();
        copyAddressMenuItem = new ToolStripMenuItem();
        refreshServerMenuItem = new ToolStripMenuItem();
        toggleFavoriteMenuItem = new ToolStripMenuItem();
        addServerMenuItem = new ToolStripMenuItem();

        // Timers
        autoRefreshTimer = new System.Windows.Forms.Timer(components);

        // Suspend layout
        menuStrip.SuspendLayout();
        toolStrip.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)mainSplitContainer).BeginInit();
        mainSplitContainer.Panel1.SuspendLayout();
        mainSplitContainer.Panel2.SuspendLayout();
        mainSplitContainer.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)serverListView).BeginInit();
        detailsTableLayoutPanel.SuspendLayout();
        serverDetailsPanel.SuspendLayout();
        wadsPanel.SuspendLayout();
        playerListPanel.SuspendLayout();
        logPanel.SuspendLayout();
        statusStrip.SuspendLayout();
        serverContextMenu.SuspendLayout();
        SuspendLayout();

        // === Menu Strip ===
        menuStrip.Items.AddRange(new ToolStripItem[] { fileMenu, viewMenu, settingsMenu, helpMenu });
        menuStrip.Location = new Point(0, 0);
        menuStrip.Name = "menuStrip";
        menuStrip.Size = new Size(1200, 24);

        // File Menu
        fileMenu.DropDownItems.AddRange(new ToolStripItem[] { refreshMenuItem, stopMenuItem, toolStripSeparator1, fetchWadsMenuItem, new ToolStripSeparator(), exitMenuItem });
        fileMenu.Name = "fileMenu";
        fileMenu.Text = "&File";

        refreshMenuItem.Name = "refreshMenuItem";
        refreshMenuItem.ShortcutKeys = Keys.F5;
        refreshMenuItem.Text = "&Refresh";
        refreshMenuItem.Click += RefreshMenuItem_Click;

        stopMenuItem.Name = "stopMenuItem";
        stopMenuItem.Text = "&Stop";
        stopMenuItem.Enabled = false;
        stopMenuItem.Click += StopMenuItem_Click;

        fetchWadsMenuItem.Name = "fetchWadsMenuItem";
        fetchWadsMenuItem.Text = "&Fetch WADs...";
        fetchWadsMenuItem.Click += FetchWadsMenuItem_Click;

        exitMenuItem.Name = "exitMenuItem";
        exitMenuItem.ShortcutKeys = Keys.Alt | Keys.F4;
        exitMenuItem.Text = "E&xit";
        exitMenuItem.Click += ExitMenuItem_Click;

        // View Menu
        viewMenu.DropDownItems.AddRange(new ToolStripItem[] { verboseMenuItem, hexDumpMenuItem, toolStripSeparator2, showLogPanelMenuItem });
        viewMenu.Name = "viewMenu";
        viewMenu.Text = "&View";

        verboseMenuItem.Name = "verboseMenuItem";
        verboseMenuItem.Text = "&Verbose Mode";
        verboseMenuItem.CheckOnClick = true;
        verboseMenuItem.Click += VerboseMenuItem_Click;

        hexDumpMenuItem.Name = "hexDumpMenuItem";
        hexDumpMenuItem.Text = "Show &Hex Dumps";
        hexDumpMenuItem.CheckOnClick = true;
        hexDumpMenuItem.Click += HexDumpMenuItem_Click;

        showLogPanelMenuItem.Name = "showLogPanelMenuItem";
        showLogPanelMenuItem.Text = "Show &Log Panel";
        showLogPanelMenuItem.CheckOnClick = true;
        showLogPanelMenuItem.Checked = true;
        showLogPanelMenuItem.Click += ShowLogPanelMenuItem_Click;

        // Settings Menu
        settingsMenu.DropDownItems.AddRange(new ToolStripItem[] { refreshOnLaunchMenuItem, autoRefreshMenuItem, autoRefreshFavoritesOnlyMenuItem, new ToolStripSeparator(), wadBrowserMenuItem, testingVersionsMenuItem, new ToolStripSeparator(), settingsMenuItem });

        wadBrowserMenuItem.Name = "wadBrowserMenuItem";
        wadBrowserMenuItem.Text = "&WAD Browser...";
        wadBrowserMenuItem.Click += WadBrowserMenuItem_Click;

        testingVersionsMenuItem.Name = "testingVersionsMenuItem";
        testingVersionsMenuItem.Text = "&Testing Version Manager...";
        testingVersionsMenuItem.Click += TestingVersionsMenuItem_Click;
        settingsMenu.Name = "settingsMenu";
        settingsMenu.Text = "&Settings";

        refreshOnLaunchMenuItem.Name = "refreshOnLaunchMenuItem";
        refreshOnLaunchMenuItem.Text = "&Refresh on Launch";
        refreshOnLaunchMenuItem.CheckOnClick = true;
        refreshOnLaunchMenuItem.Checked = true;
        refreshOnLaunchMenuItem.Click += RefreshOnLaunchMenuItem_Click;

        autoRefreshMenuItem.Name = "autoRefreshMenuItem";
        autoRefreshMenuItem.Text = "&Auto Refresh (5 min)";
        autoRefreshMenuItem.CheckOnClick = true;
        autoRefreshMenuItem.Click += AutoRefreshMenuItem_Click;

        autoRefreshFavoritesOnlyMenuItem.Name = "autoRefreshFavoritesOnlyMenuItem";
        autoRefreshFavoritesOnlyMenuItem.Text = "Auto Refresh &Favorites Only";
        autoRefreshFavoritesOnlyMenuItem.CheckOnClick = true;
        autoRefreshFavoritesOnlyMenuItem.Click += AutoRefreshFavoritesOnlyMenuItem_Click;

        // settingsMenuItem
        settingsMenuItem.Name = "settingsMenuItem";
        settingsMenuItem.Text = "&Settings...";
        settingsMenuItem.Click += SettingsMenuItem_Click;

        // Help Menu
        helpMenu.DropDownItems.AddRange(new ToolStripItem[] { aboutMenuItem });
        helpMenu.Name = "helpMenu";
        helpMenu.Text = "&Help";

        aboutMenuItem.Name = "aboutMenuItem";
        aboutMenuItem.Text = "&About";
        aboutMenuItem.Click += AboutMenuItem_Click;

        // === Tool Strip ===
        toolStrip.Items.AddRange(new ToolStripItem[] { 
            refreshButton, stopButton, toolStripSeparator3,
            searchLabel, searchBox, toolStripSeparator4,
            hideEmptyCheckBox, hideBotOnlyCheckBox, hideFullCheckBox, hidePasswordedCheckBox, showFavoritesOnlyCheckBox, toolStripSeparator5,
            gameModeLabel, gameModeComboBox, toolStripSeparator6,
            advancedFilterButton, toolStripSeparator7, historyButton
        });
        toolStrip.Location = new Point(0, 24);
        toolStrip.Name = "toolStrip";
        toolStrip.Size = new Size(1200, 25);
        toolStrip.Padding = new Padding(5, 0, 5, 0);

        refreshButton.Name = "refreshButton";
        refreshButton.Text = "Refresh";
        refreshButton.ToolTipText = "Refresh server list (F5)";
        refreshButton.Click += RefreshMenuItem_Click;

        stopButton.Name = "stopButton";
        stopButton.Text = "Stop";
        stopButton.ToolTipText = "Stop refreshing";
        stopButton.Enabled = false;
        stopButton.Click += StopMenuItem_Click;

        searchLabel.Name = "searchLabel";
        searchLabel.Text = "Search:";

        searchBox.Name = "searchBox";
        searchBox.Size = new Size(150, 25);
        searchBox.ToolTipText = "Search servers by name, map, or address";
        searchBox.TextChanged += SearchBox_TextChanged;

        hideEmptyCheckBox.Name = "hideEmptyCheckBox";
        hideEmptyCheckBox.Text = "Hide Empty";
        hideEmptyCheckBox.CheckOnClick = true;
        hideEmptyCheckBox.Click += FilterChanged;

        hideBotOnlyCheckBox.Name = "hideBotOnlyCheckBox";
        hideBotOnlyCheckBox.Text = "Hide Bot-Only";
        hideBotOnlyCheckBox.CheckOnClick = true;
        hideBotOnlyCheckBox.ToolTipText = "Hide servers with only bots (no human players)";
        hideBotOnlyCheckBox.Click += FilterChanged;

        hideFullCheckBox.Name = "hideFullCheckBox";
        hideFullCheckBox.Text = "Hide Full";
        hideFullCheckBox.CheckOnClick = true;
        hideFullCheckBox.Click += FilterChanged;

        hidePasswordedCheckBox.Name = "hidePasswordedCheckBox";
        hidePasswordedCheckBox.Text = "Hide Passworded";
        hidePasswordedCheckBox.CheckOnClick = true;
        hidePasswordedCheckBox.Click += FilterChanged;

        showFavoritesOnlyCheckBox.Name = "showFavoritesOnlyCheckBox";
        showFavoritesOnlyCheckBox.Text = "Favorites Only";
        showFavoritesOnlyCheckBox.CheckOnClick = true;
        showFavoritesOnlyCheckBox.ToolTipText = "Show only favorite servers";
        showFavoritesOnlyCheckBox.Click += FilterChanged;

        gameModeLabel.Name = "gameModeLabel";
        gameModeLabel.Text = "Mode:";

        gameModeComboBox.Name = "gameModeComboBox";
        gameModeComboBox.Size = new Size(120, 25);
        gameModeComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        gameModeComboBox.SelectedIndexChanged += FilterChanged;

        advancedFilterButton.Name = "advancedFilterButton";
        advancedFilterButton.Text = "Advanced Filter...";
        advancedFilterButton.ToolTipText = "Configure advanced filtering options";
        advancedFilterButton.Click += AdvancedFilterButton_Click;

        historyButton.Name = "historyButton";
        historyButton.Text = "History";
        historyButton.ToolTipText = "View connection history";
        historyButton.Click += HistoryButton_Click;

        // === Main Split Container ===
        mainSplitContainer.Dock = DockStyle.Fill;
        mainSplitContainer.Location = new Point(0, 49);
        mainSplitContainer.Name = "mainSplitContainer";
        mainSplitContainer.Orientation = Orientation.Horizontal;
        mainSplitContainer.Panel1.Controls.Add(serverListView);
        mainSplitContainer.Panel2.Controls.Add(detailsTableLayoutPanel);
        mainSplitContainer.Panel2.Controls.Add(logPanel);
        mainSplitContainer.Size = new Size(1200, 600);
        mainSplitContainer.SplitterDistance = 350;
        mainSplitContainer.SplitterWidth = 5;

        // === Server List View ===
        serverListView.Dock = DockStyle.Fill;
        serverListView.Name = "serverListView";
        serverListView.ReadOnly = true;
        serverListView.MultiSelect = false;
        serverListView.ContextMenuStrip = serverContextMenu;
        serverListView.SelectionChanged += ServerListView_SelectionChanged;
        serverListView.CellDoubleClick += ServerListView_CellDoubleClick;
        serverListView.ColumnHeaderMouseClick += ServerListView_ColumnHeaderMouseClick;
        serverListView.CellMouseDown += ServerListView_CellMouseDown;

        // === Details Table Layout Panel (3 columns) ===
        detailsTableLayoutPanel.Dock = DockStyle.Top;
        detailsTableLayoutPanel.Location = new Point(0, 0);
        detailsTableLayoutPanel.Name = "detailsTableLayoutPanel";
        detailsTableLayoutPanel.ColumnCount = 3;
        detailsTableLayoutPanel.RowCount = 1;
        detailsTableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        detailsTableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
        detailsTableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
        detailsTableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        detailsTableLayoutPanel.Controls.Add(serverDetailsPanel, 0, 0);
        detailsTableLayoutPanel.Controls.Add(wadsPanel, 1, 0);
        detailsTableLayoutPanel.Controls.Add(playerListPanel, 2, 0);
        detailsTableLayoutPanel.Size = new Size(1200, 200);

        // === Server Details Panel ===
        serverDetailsPanel.Dock = DockStyle.Fill;
        serverDetailsPanel.Name = "serverDetailsPanel";
        serverDetailsPanel.Padding = new Padding(5);
        serverDetailsPanel.Controls.Add(serverInfoTextBox);
        serverDetailsPanel.Controls.Add(serverDetailsLabel);

        serverDetailsLabel.Dock = DockStyle.Top;
        serverDetailsLabel.Name = "serverDetailsLabel";
        serverDetailsLabel.Text = "Server Details";
        serverDetailsLabel.Font = new Font(Font.FontFamily, 9, FontStyle.Bold);
        serverDetailsLabel.Height = 20;
        serverDetailsLabel.Padding = new Padding(0, 0, 0, 5);

        serverInfoTextBox.Dock = DockStyle.Fill;
        serverInfoTextBox.Name = "serverInfoTextBox";
        serverInfoTextBox.ReadOnly = true;
        serverInfoTextBox.BorderStyle = BorderStyle.None;
        serverInfoTextBox.Font = new Font("Consolas", 9);

        // === WADs Panel ===
        wadsPanel.Dock = DockStyle.Fill;
        wadsPanel.Name = "wadsPanel";
        wadsPanel.Padding = new Padding(5);
        wadsPanel.Controls.Add(wadsListView);
        wadsPanel.Controls.Add(wadsLabel);

        wadsLabel.Dock = DockStyle.Top;
        wadsLabel.Name = "wadsLabel";
        wadsLabel.Text = "WADs";
        wadsLabel.Font = new Font(Font.FontFamily, 9, FontStyle.Bold);
        wadsLabel.Height = 20;
        wadsLabel.Padding = new Padding(0, 0, 0, 5);

        wadsListView.Dock = DockStyle.Fill;
        wadsListView.Name = "wadsListView";
        wadsListView.View = View.Details;
        wadsListView.FullRowSelect = true;
        wadsListView.HeaderStyle = ColumnHeaderStyle.None;
        wadsListView.Columns.Add("WAD", 200);
        wadsListView.Resize += WadsListView_Resize;
        wadsListView.ItemSelectionChanged += OnWadsListViewSelectionChanged;

        // === Player List Panel ===
        playerListPanel.Dock = DockStyle.Fill;
        playerListPanel.Name = "playerListPanel";
        playerListPanel.Padding = new Padding(5);
        playerListPanel.Controls.Add(playerListView);
        playerListPanel.Controls.Add(playerListLabel);

        playerListLabel.Dock = DockStyle.Top;
        playerListLabel.Name = "playerListLabel";
        playerListLabel.Text = "Players";
        playerListLabel.Font = new Font(Font.FontFamily, 9, FontStyle.Bold);
        playerListLabel.Height = 20;
        playerListLabel.Padding = new Padding(0, 0, 0, 5);

        playerListView.Dock = DockStyle.Fill;
        playerListView.Name = "playerListView";
        playerListView.View = View.Details;
        playerListView.FullRowSelect = true;
        playerListView.Columns.AddRange(new ColumnHeader[] { playerNameColumn, playerScoreColumn, playerPingColumn, playerTeamColumn });
        playerListView.Resize += PlayerListView_Resize;

        playerNameColumn.Text = "Name";
        playerNameColumn.Width = AppConstants.PlayerListColumns.NameWidth;

        playerScoreColumn.Text = "Score";
        playerScoreColumn.Width = AppConstants.PlayerListColumns.ScoreWidth;

        playerPingColumn.Text = "Ping";
        playerPingColumn.Width = AppConstants.PlayerListColumns.PingWidth;

        playerTeamColumn.Text = "Team";
        playerTeamColumn.Width = AppConstants.PlayerListColumns.TeamWidth;

        // === Log Panel ===
        logPanel.Dock = DockStyle.Bottom;
        logPanel.Name = "logPanel";
        logPanel.Height = 100;
        logPanel.Padding = new Padding(5);
        logPanel.Controls.Add(logTextBox);
        logPanel.Controls.Add(logLabel);

        logLabel.Dock = DockStyle.Top;
        logLabel.Name = "logLabel";
        logLabel.Text = "Log";
        logLabel.Font = new Font(Font.FontFamily, 9, FontStyle.Bold);
        logLabel.Height = 20;
        logLabel.Padding = new Padding(0, 0, 0, 5);

        logTextBox.Dock = DockStyle.Fill;
        logTextBox.Name = "logTextBox";
        logTextBox.ReadOnly = true;
        logTextBox.BorderStyle = BorderStyle.None;
        logTextBox.Font = new Font("Consolas", 8);
        logTextBox.WordWrap = false;
        logTextBox.ScrollBars = RichTextBoxScrollBars.Both;

        // === Status Strip ===
        statusStrip.Items.AddRange(new ToolStripItem[] { serverCountLabel, playerCountLabel, statusLabel, progressBar });
        statusStrip.Location = new Point(0, 649);
        statusStrip.Name = "statusStrip";
        statusStrip.Size = new Size(1200, 22);

        serverCountLabel.Name = "serverCountLabel";
        serverCountLabel.Text = "Servers: 0";
        serverCountLabel.BorderSides = ToolStripStatusLabelBorderSides.Right;

        playerCountLabel.Name = "playerCountLabel";
        playerCountLabel.Text = "Players: 0";
        playerCountLabel.BorderSides = ToolStripStatusLabelBorderSides.Right;

        statusLabel.Name = "statusLabel";
        statusLabel.Text = "Ready";
        statusLabel.Spring = true;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;

        progressBar.Name = "progressBar";
        progressBar.Size = new Size(100, 16);
        progressBar.Visible = false;

        // === Context Menu ===
        serverContextMenu.Items.AddRange(new ToolStripItem[] { 
            connectMenuItem, toggleFavoriteMenuItem, new ToolStripSeparator(), 
            copyConnectMenuItem, copyAddressMenuItem, refreshServerMenuItem, 
            new ToolStripSeparator(), downloadWadsMenuItem,
            new ToolStripSeparator(), addServerMenuItem 
        });
        serverContextMenu.Name = "serverContextMenu";

        connectMenuItem.Name = "connectMenuItem";
        connectMenuItem.Text = "Connect to Server";
        connectMenuItem.Font = new Font(connectMenuItem.Font, FontStyle.Bold);
        connectMenuItem.Click += ConnectMenuItem_Click;

        copyConnectMenuItem.Name = "copyConnectMenuItem";
        copyConnectMenuItem.Text = "Copy Connect Command";
        copyConnectMenuItem.Click += CopyConnectMenuItem_Click;

        copyAddressMenuItem.Name = "copyAddressMenuItem";
        copyAddressMenuItem.Text = "Copy Server Address";
        copyAddressMenuItem.Click += CopyAddressMenuItem_Click;

        refreshServerMenuItem.Name = "refreshServerMenuItem";
        refreshServerMenuItem.Text = "Refresh This Server";
        refreshServerMenuItem.Click += RefreshServerMenuItem_Click;

        downloadWadsMenuItem.Name = "downloadWadsMenuItem";
        downloadWadsMenuItem.Text = "Download Missing WADs...";
        downloadWadsMenuItem.Click += DownloadWadsMenuItem_Click;

        toggleFavoriteMenuItem.Name = "toggleFavoriteMenuItem";
        toggleFavoriteMenuItem.Text = "Add to Favorites";
        toggleFavoriteMenuItem.Click += ToggleFavoriteMenuItem_Click;

        addServerMenuItem.Name = "addServerMenuItem";
        addServerMenuItem.Text = "Add Server Manually...";
        addServerMenuItem.Click += AddServerMenuItem_Click;

        // === Auto Refresh Timer ===
        autoRefreshTimer.Interval = 300000; // 5 minutes
        autoRefreshTimer.Tick += AutoRefreshTimer_Tick;

        // === Form ===
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1200, 671);
        Controls.Add(mainSplitContainer);
        Controls.Add(toolStrip);
        Controls.Add(menuStrip);
        Controls.Add(statusStrip);
        MainMenuStrip = menuStrip;
        Name = "MainForm";
        Text = "Zandronum Server Browser";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(800, 500);

        // Resume layout
        menuStrip.ResumeLayout(false);
        menuStrip.PerformLayout();
        toolStrip.ResumeLayout(false);
        toolStrip.PerformLayout();
        mainSplitContainer.Panel1.ResumeLayout(false);
        mainSplitContainer.Panel2.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)mainSplitContainer).EndInit();
        mainSplitContainer.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)serverListView).EndInit();
        detailsTableLayoutPanel.ResumeLayout(false);
        serverDetailsPanel.ResumeLayout(false);
        wadsPanel.ResumeLayout(false);
        playerListPanel.ResumeLayout(false);
        logPanel.ResumeLayout(false);
        statusStrip.ResumeLayout(false);
        statusStrip.PerformLayout();
        serverContextMenu.ResumeLayout(false);
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    // Menu
    private MenuStrip menuStrip;
    private ToolStripMenuItem fileMenu;
    private ToolStripMenuItem refreshMenuItem;
    private ToolStripMenuItem stopMenuItem;
    private ToolStripSeparator toolStripSeparator1;
    private ToolStripMenuItem exitMenuItem;
    private ToolStripMenuItem viewMenu;
    private ToolStripMenuItem verboseMenuItem;
    private ToolStripMenuItem hexDumpMenuItem;
    private ToolStripSeparator toolStripSeparator2;
    private ToolStripMenuItem showLogPanelMenuItem;
    private ToolStripMenuItem settingsMenu;
    private ToolStripMenuItem autoRefreshMenuItem;
    private ToolStripMenuItem autoRefreshFavoritesOnlyMenuItem;
    private ToolStripMenuItem wadBrowserMenuItem;
    private ToolStripMenuItem testingVersionsMenuItem;
    private ToolStripMenuItem settingsMenuItem;
    private ToolStripMenuItem downloadWadsMenuItem;
    private ToolStripMenuItem helpMenu;
    private ToolStripMenuItem aboutMenuItem;
    private ToolStripMenuItem fetchWadsMenuItem;

    // Toolbar
    private ToolStrip toolStrip;
    private ToolStripButton refreshButton;
    private ToolStripButton stopButton;
    private ToolStripSeparator toolStripSeparator3;
    private ToolStripLabel searchLabel;
    private ToolStripTextBox searchBox;
    private ToolStripSeparator toolStripSeparator4;
    private ToolStripButton hideEmptyCheckBox;
    private ToolStripButton hideBotOnlyCheckBox;
    private ToolStripButton hideFullCheckBox;
    private ToolStripButton hidePasswordedCheckBox;
    private ToolStripSeparator toolStripSeparator5;
    private ToolStripLabel gameModeLabel;
    private ToolStripComboBox gameModeComboBox;
    private ToolStripSeparator toolStripSeparator6;
    private ToolStripButton advancedFilterButton;
    private ToolStripSeparator toolStripSeparator7;
    private ToolStripButton historyButton;

    // Main content
    private SplitContainer mainSplitContainer;
    private DataGridView serverListView;
    private TableLayoutPanel detailsTableLayoutPanel;

    // Server details
    private Panel serverDetailsPanel;
    private Label serverDetailsLabel;
    private RichTextBox serverInfoTextBox;

    // WADs panel
    private Panel wadsPanel;
    private Label wadsLabel;
    private ListView wadsListView;

    // Player list
    private Panel playerListPanel;
    private Label playerListLabel;
    private ListView playerListView;
    private ColumnHeader playerNameColumn;
    private ColumnHeader playerScoreColumn;
    private ColumnHeader playerPingColumn;
    private ColumnHeader playerTeamColumn;

    // Log panel
    private Panel logPanel;
    private Label logLabel;
    private RichTextBox logTextBox;

    // Status bar
    private StatusStrip statusStrip;
    private ToolStripStatusLabel serverCountLabel;
    private ToolStripStatusLabel playerCountLabel;
    private ToolStripStatusLabel statusLabel;
    private ToolStripProgressBar progressBar;

    // Context menu
    private ContextMenuStrip serverContextMenu;
    private ToolStripMenuItem connectMenuItem;
    private ToolStripMenuItem copyConnectMenuItem;
    private ToolStripMenuItem copyAddressMenuItem;
    private ToolStripMenuItem refreshServerMenuItem;
    private ToolStripMenuItem toggleFavoriteMenuItem;
    private ToolStripMenuItem addServerMenuItem;
    private ToolStripButton showFavoritesOnlyCheckBox;

    // Timers
    private System.Windows.Forms.Timer autoRefreshTimer;
    private ToolStripMenuItem refreshOnLaunchMenuItem;
}
