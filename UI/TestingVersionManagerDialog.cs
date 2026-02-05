using System.Diagnostics;
using ZScape.Services;
using ZScape.Utilities;

namespace ZScape.UI;

/// <summary>
/// Dialog for managing installed testing versions of Zandronum.
/// </summary>
public class TestingVersionManagerDialog : Form
{
    private readonly LoggingService _logger = LoggingService.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;
    
    private ListView versionListView = null!;
    private Button refreshButton = null!;
    private Button deleteButton = null!;
    private Button openFolderButton = null!;
    private Button closeButton = null!;
    private Label statusLabel = null!;
    private Label totalSizeLabel = null!;
    
    private readonly List<TestingVersionInfo> _versions = [];
    
    public TestingVersionManagerDialog()
    {
        InitializeComponent();
        ApplyDarkTheme();
    }
    
    private void InitializeComponent()
    {
        Text = "Testing Version Manager";
        Size = new Size(700, 500);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(500, 350);
        
        // Main layout
        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10)
        };
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));  // Toolbar
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // List
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));  // Bottom bar
        
        // Toolbar
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        
        refreshButton = new Button { Text = "Refresh", Width = 80, Height = 28 };
        refreshButton.Click += RefreshButton_Click;
        
        totalSizeLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(20, 7, 0, 0),
            Text = "Total: --"
        };
        
        toolbar.Controls.AddRange([refreshButton, totalSizeLabel]);
        
        // ListView
        versionListView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true
        };
        
        versionListView.Columns.AddRange([
            new ColumnHeader { Text = "Version", Width = 200 },
            new ColumnHeader { Text = "Size", Width = 100, TextAlign = HorizontalAlignment.Right },
            new ColumnHeader { Text = "Files", Width = 80, TextAlign = HorizontalAlignment.Right },
            new ColumnHeader { Text = "Screenshots", Width = 80, TextAlign = HorizontalAlignment.Right },
            new ColumnHeader { Text = "Path", Width = 200 }
        ]);
        
        versionListView.SelectedIndexChanged += VersionListView_SelectedIndexChanged;
        versionListView.MouseDoubleClick += VersionListView_MouseDoubleClick;
        
        // Bottom bar
        var bottomPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1
        };
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // Status
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); // Open folder
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));  // Delete
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));  // Spacer
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));  // Close
        
        statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Scanning..."
        };
        
        openFolderButton = new Button { Text = "Open Folder", Width = 90, Height = 28, Enabled = false };
        openFolderButton.Click += OpenFolderButton_Click;
        
        deleteButton = new Button { Text = "Delete", Width = 70, Height = 28, Enabled = false };
        deleteButton.Click += DeleteButton_Click;
        
        closeButton = new Button { Text = "Close", Width = 70, Height = 28 };
        closeButton.Click += (s, e) => Close();
        
        bottomPanel.Controls.Add(statusLabel, 0, 0);
        bottomPanel.Controls.Add(openFolderButton, 1, 0);
        bottomPanel.Controls.Add(deleteButton, 2, 0);
        bottomPanel.Controls.Add(new Panel(), 3, 0);
        bottomPanel.Controls.Add(closeButton, 4, 0);
        
        mainPanel.Controls.Add(toolbar, 0, 0);
        mainPanel.Controls.Add(versionListView, 0, 1);
        mainPanel.Controls.Add(bottomPanel, 0, 2);
        
        Controls.Add(mainPanel);
    }
    
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ScanVersions();
    }
    
    private void RefreshButton_Click(object? sender, EventArgs e)
    {
        ScanVersions();
    }
    
    private void ScanVersions()
    {
        _versions.Clear();
        versionListView.Items.Clear();
        
        var testingRoot = GetTestingRootPath();
        if (string.IsNullOrEmpty(testingRoot) || !Directory.Exists(testingRoot))
        {
            statusLabel.Text = "Testing versions path not configured";
            totalSizeLabel.Text = "Total: --";
            return;
        }
        
        statusLabel.Text = "Scanning...";
        totalSizeLabel.Text = "Calculating...";
        versionListView.BeginUpdate();
        
        try
        {
            long totalSize = 0;
            
            foreach (var dir in Directory.GetDirectories(testingRoot))
            {
                var versionName = Path.GetFileName(dir);
                var exePath = Path.Combine(dir, "zandronum.exe");
                bool hasExe = File.Exists(exePath);
                
                // Calculate directory size and file count
                var (size, fileCount) = GetDirectoryInfo(dir);
                
                // Count screenshots
                int screenshotCount = 0;
                try
                {
                    screenshotCount = Directory.GetFiles(dir, "Screenshot_*.png", SearchOption.TopDirectoryOnly).Length;
                }
                catch { }
                
                var info = new TestingVersionInfo
                {
                    VersionName = versionName,
                    Path = dir,
                    Size = size,
                    FileCount = fileCount,
                    ScreenshotCount = screenshotCount,
                    HasExecutable = hasExe
                };
                
                _versions.Add(info);
                totalSize += size;
                
                var item = new ListViewItem(versionName)
                {
                    Tag = info
                };
                item.SubItems.Add(FormatFileSize(size));
                item.SubItems.Add(fileCount.ToString());
                item.SubItems.Add(screenshotCount > 0 ? screenshotCount.ToString() : "-");
                item.SubItems.Add(dir);
                
                // Mark incomplete versions (no exe)
                if (!hasExe)
                {
                    item.ForeColor = Color.Orange;
                }
                
                versionListView.Items.Add(item);
            }
            
            statusLabel.Text = $"Found {_versions.Count} testing version{(_versions.Count != 1 ? "s" : "")}";
            totalSizeLabel.Text = $"Total: {FormatFileSize(totalSize)}";
        }
        catch (Exception ex)
        {
            statusLabel.Text = $"Error: {ex.Message}";
            _logger.Error($"Failed to scan testing versions: {ex.Message}");
        }
        finally
        {
            versionListView.EndUpdate();
        }
    }
    
    private static (long size, int fileCount) GetDirectoryInfo(string path)
    {
        long size = 0;
        int count = 0;
        
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    size += info.Length;
                    count++;
                }
                catch { }
            }
        }
        catch { }
        
        return (size, count);
    }
    
    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
    
    private void VersionListView_SelectedIndexChanged(object? sender, EventArgs e)
    {
        var hasSelection = versionListView.SelectedItems.Count > 0;
        deleteButton.Enabled = hasSelection;
        openFolderButton.Enabled = versionListView.SelectedItems.Count == 1;
    }
    
    private void VersionListView_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (versionListView.SelectedItems.Count == 1 &&
            versionListView.SelectedItems[0].Tag is TestingVersionInfo info)
        {
            OpenFolder(info.Path);
        }
    }
    
    private void OpenFolderButton_Click(object? sender, EventArgs e)
    {
        if (versionListView.SelectedItems.Count == 1 &&
            versionListView.SelectedItems[0].Tag is TestingVersionInfo info)
        {
            OpenFolder(info.Path);
        }
    }
    
    private static void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Process.Start("explorer.exe", path);
        }
    }
    
    private void DeleteButton_Click(object? sender, EventArgs e)
    {
        if (versionListView.SelectedItems.Count == 0) return;
        
        var versions = versionListView.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag as TestingVersionInfo)
            .Where(v => v != null)
            .Cast<TestingVersionInfo>()
            .ToList();
        
        if (versions.Count == 0) return;
        
        var totalSize = versions.Sum(v => v.Size);
        var message = versions.Count == 1
            ? $"Delete testing version '{versions[0].VersionName}'?\n\nSize: {FormatFileSize(versions[0].Size)}\n\nThis action cannot be undone."
            : $"Delete {versions.Count} testing versions?\n\nTotal size: {FormatFileSize(totalSize)}\n\nThis action cannot be undone.";
        
        var result = MessageBox.Show(this, message, "Confirm Delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        
        if (result != DialogResult.Yes) return;
        
        int deleted = 0;
        int failed = 0;
        
        foreach (var version in versions)
        {
            try
            {
                if (Directory.Exists(version.Path))
                {
                    Directory.Delete(version.Path, true);
                    _versions.Remove(version);
                    deleted++;
                    _logger.Info($"Deleted testing version: {version.VersionName}");
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to delete {version.VersionName}: {ex.Message}");
                failed++;
            }
        }
        
        // Refresh the list
        ScanVersions();
        
        if (failed > 0)
        {
            statusLabel.Text = $"Deleted {deleted}, {failed} failed";
        }
        else
        {
            statusLabel.Text = $"Deleted {deleted} version{(deleted != 1 ? "s" : "")}";
        }
    }
    
    private static string? GetTestingRootPath() => PathResolver.GetTestingVersionsPath();
    
    private void ApplyDarkTheme()
    {
        BackColor = DarkTheme.PrimaryBackground;
        ForeColor = DarkTheme.TextPrimary;
        
        DarkTheme.Apply(this);
        DarkTheme.ApplyToButton(refreshButton);
        DarkTheme.ApplyToButton(deleteButton);
        DarkTheme.ApplyToButton(openFolderButton);
        DarkTheme.ApplyToButton(closeButton);
        DarkTheme.ApplyToListView(versionListView);
    }
    
    private class TestingVersionInfo
    {
        public string VersionName { get; set; } = "";
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public int FileCount { get; set; }
        public int ScreenshotCount { get; set; }
        public bool HasExecutable { get; set; }
    }
}
