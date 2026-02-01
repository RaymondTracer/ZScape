using System.Diagnostics;
using ZScape.Services;

namespace ZScape.UI;

/// <summary>
/// Dialog for browsing, organizing, and managing WAD files.
/// </summary>
public class WadBrowserDialog : Form
{
    private readonly WadManager _wadManager;
    private readonly LoggingService _logger;
    
    private ListView wadListView = null!;
    private Button refreshButton = null!;
    private Button deleteButton = null!;
    private Button moveButton = null!;
    private Button showDuplicatesButton = null!;
    private Button openFolderButton = null!;
    private Button closeButton = null!;
    private Label statusLabel = null!;
    private ProgressBar progressBar = null!;
    private CheckBox showDuplicatesOnlyCheckBox = null!;
    
    private readonly Dictionary<string, List<WadEntry>> _wadsByHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WadEntry> _allWads = [];
    private bool _isScanning;
    
    public WadBrowserDialog()
    {
        _wadManager = WadManager.Instance;
        _logger = LoggingService.Instance;
        
        InitializeComponent();
        ApplyDarkTheme();
    }
    
    private void InitializeComponent()
    {
        Text = "WAD Browser";
        Size = new Size(900, 600);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(700, 400);
        
        // Main layout
        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10)
        };
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));  // Toolbar
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
        
        showDuplicatesOnlyCheckBox = new CheckBox
        {
            Text = "Show duplicates only",
            AutoSize = true,
            Margin = new Padding(20, 5, 10, 0)
        };
        showDuplicatesOnlyCheckBox.CheckedChanged += ShowDuplicatesOnlyCheckBox_CheckedChanged;
        
        showDuplicatesButton = new Button { Text = "Find Duplicates", Width = 110, Height = 28 };
        showDuplicatesButton.Click += ShowDuplicatesButton_Click;
        
        toolbar.Controls.AddRange([refreshButton, showDuplicatesOnlyCheckBox, showDuplicatesButton]);
        
        // ListView
        wadListView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true
        };
        
        wadListView.Columns.AddRange([
            new ColumnHeader { Text = "Name", Width = 200 },
            new ColumnHeader { Text = "Size", Width = 80, TextAlign = HorizontalAlignment.Right },
            new ColumnHeader { Text = "Hash", Width = 120 },
            new ColumnHeader { Text = "Duplicate Of", Width = 150 },
            new ColumnHeader { Text = "Location", Width = 300 }
        ]);
        
        wadListView.SelectedIndexChanged += WadListView_SelectedIndexChanged;
        wadListView.MouseDoubleClick += WadListView_MouseDoubleClick;
        
        // Bottom bar
        var bottomPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1
        };
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // Status
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); // Open folder
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));  // Move
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));  // Delete
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));  // Spacer
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));  // Close
        
        var statusPanel = new Panel { Dock = DockStyle.Fill };
        statusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 20,
            Text = "Click Refresh to scan WAD directories..."
        };
        progressBar = new ProgressBar
        {
            Dock = DockStyle.Bottom,
            Height = 20,
            Visible = false
        };
        statusPanel.Controls.AddRange([statusLabel, progressBar]);
        
        openFolderButton = new Button { Text = "Open Folder", Width = 90, Height = 28, Enabled = false };
        openFolderButton.Click += OpenFolderButton_Click;
        
        moveButton = new Button { Text = "Move...", Width = 70, Height = 28, Enabled = false };
        moveButton.Click += MoveButton_Click;
        
        deleteButton = new Button { Text = "Delete", Width = 70, Height = 28, Enabled = false };
        deleteButton.Click += DeleteButton_Click;
        
        closeButton = new Button { Text = "Close", Width = 70, Height = 28 };
        closeButton.Click += (s, e) => Close();
        
        bottomPanel.Controls.Add(statusPanel, 0, 0);
        bottomPanel.Controls.Add(openFolderButton, 1, 0);
        bottomPanel.Controls.Add(moveButton, 2, 0);
        bottomPanel.Controls.Add(deleteButton, 3, 0);
        bottomPanel.Controls.Add(new Panel(), 4, 0);
        bottomPanel.Controls.Add(closeButton, 5, 0);
        
        mainPanel.Controls.Add(toolbar, 0, 0);
        mainPanel.Controls.Add(wadListView, 0, 1);
        mainPanel.Controls.Add(bottomPanel, 0, 2);
        
        Controls.Add(mainPanel);
    }
    
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Auto-refresh on open
        _ = ScanWadsAsync();
    }
    
    private async void RefreshButton_Click(object? sender, EventArgs e)
    {
        await ScanWadsAsync();
    }
    
    private async Task ScanWadsAsync()
    {
        if (_isScanning) return;
        _isScanning = true;
        
        refreshButton.Enabled = false;
        showDuplicatesButton.Enabled = false;
        progressBar.Visible = true;
        progressBar.Style = ProgressBarStyle.Marquee;
        statusLabel.Text = "Scanning WAD directories...";
        
        _allWads.Clear();
        _wadsByHash.Clear();
        wadListView.Items.Clear();
        
        try
        {
            // Refresh the WAD manager cache first
            await Task.Run(() => _wadManager.RefreshCache());
            
            var cachedWads = _wadManager.GetAllCachedWads();
            var total = cachedWads.Count;
            var processed = 0;
            
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Maximum = total;
            progressBar.Value = 0;
            
            await Task.Run(() =>
            {
                foreach (var (name, path) in cachedWads)
                {
                    try
                    {
                        var fileInfo = new FileInfo(path);
                        if (!fileInfo.Exists) continue;
                        
                        var entry = new WadEntry
                        {
                            Name = name,
                            Path = path,
                            Size = fileInfo.Length,
                            Hash = null // Will be computed on demand for duplicates
                        };
                        
                        lock (_allWads)
                        {
                            _allWads.Add(entry);
                        }
                    }
                    catch
                    {
                        // Skip files we can't access
                    }
                    
                    processed++;
                    BeginInvoke(() =>
                    {
                        progressBar.Value = Math.Min(processed, total);
                        statusLabel.Text = $"Scanning... {processed}/{total}";
                    });
                }
            });
            
            PopulateListView();
            statusLabel.Text = $"Found {_allWads.Count} WAD files";
        }
        catch (Exception ex)
        {
            statusLabel.Text = $"Error: {ex.Message}";
            _logger.Error($"WAD scan failed: {ex.Message}");
        }
        finally
        {
            refreshButton.Enabled = true;
            showDuplicatesButton.Enabled = true;
            progressBar.Visible = false;
            _isScanning = false;
        }
    }
    
    private async void ShowDuplicatesButton_Click(object? sender, EventArgs e)
    {
        if (_isScanning) return;
        _isScanning = true;
        
        refreshButton.Enabled = false;
        showDuplicatesButton.Enabled = false;
        progressBar.Visible = true;
        progressBar.Style = ProgressBarStyle.Continuous;
        progressBar.Maximum = _allWads.Count;
        progressBar.Value = 0;
        statusLabel.Text = "Computing hashes to find duplicates...";
        
        _wadsByHash.Clear();
        
        try
        {
            var processed = 0;
            
            await Task.Run(() =>
            {
                foreach (var wad in _allWads)
                {
                    if (wad.Hash == null)
                    {
                        wad.Hash = WadManager.ComputeFileHash(wad.Path) ?? "unknown";
                    }
                    
                    lock (_wadsByHash)
                    {
                        if (!_wadsByHash.TryGetValue(wad.Hash, out var list))
                        {
                            list = [];
                            _wadsByHash[wad.Hash] = list;
                        }
                        list.Add(wad);
                    }
                    
                    processed++;
                    BeginInvoke(() =>
                    {
                        progressBar.Value = Math.Min(processed, _allWads.Count);
                        statusLabel.Text = $"Computing hashes... {processed}/{_allWads.Count}";
                    });
                }
            });
            
            // Mark duplicates
            int duplicateCount = 0;
            foreach (var (hash, wads) in _wadsByHash)
            {
                if (wads.Count > 1)
                {
                    duplicateCount += wads.Count;
                    // First one is the "original", rest are duplicates
                    var original = wads[0];
                    for (int i = 1; i < wads.Count; i++)
                    {
                        wads[i].DuplicateOf = original.Name;
                    }
                }
            }
            
            PopulateListView();
            statusLabel.Text = $"Found {duplicateCount} duplicate files ({_wadsByHash.Values.Count(v => v.Count > 1)} groups)";
            showDuplicatesOnlyCheckBox.Checked = duplicateCount > 0;
        }
        catch (Exception ex)
        {
            statusLabel.Text = $"Error: {ex.Message}";
            _logger.Error($"Hash computation failed: {ex.Message}");
        }
        finally
        {
            refreshButton.Enabled = true;
            showDuplicatesButton.Enabled = true;
            progressBar.Visible = false;
            _isScanning = false;
        }
    }
    
    private void ShowDuplicatesOnlyCheckBox_CheckedChanged(object? sender, EventArgs e)
    {
        PopulateListView();
    }
    
    private void PopulateListView()
    {
        wadListView.BeginUpdate();
        wadListView.Items.Clear();
        
        var showDuplicatesOnly = showDuplicatesOnlyCheckBox.Checked;
        
        foreach (var wad in _allWads.OrderBy(w => w.Name))
        {
            // Skip non-duplicates if filter is on
            if (showDuplicatesOnly)
            {
                bool isDuplicate = !string.IsNullOrEmpty(wad.DuplicateOf);
                bool hasHash = wad.Hash != null;
                bool isInDuplicateGroup = hasHash && _wadsByHash.TryGetValue(wad.Hash!, out var group) && group.Count > 1;
                
                if (!isDuplicate && !isInDuplicateGroup)
                    continue;
            }
            
            var item = new ListViewItem(wad.Name)
            {
                Tag = wad
            };
            
            item.SubItems.Add(FormatFileSize(wad.Size));
            item.SubItems.Add(wad.Hash?.Substring(0, Math.Min(12, wad.Hash?.Length ?? 0)) ?? "-");
            item.SubItems.Add(wad.DuplicateOf ?? "");
            item.SubItems.Add(wad.Path);
            
            // Color duplicates
            if (!string.IsNullOrEmpty(wad.DuplicateOf))
            {
                item.ForeColor = Color.Orange;
            }
            
            wadListView.Items.Add(item);
        }
        
        wadListView.EndUpdate();
    }
    
    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
    
    private void WadListView_SelectedIndexChanged(object? sender, EventArgs e)
    {
        var hasSelection = wadListView.SelectedItems.Count > 0;
        deleteButton.Enabled = hasSelection;
        moveButton.Enabled = hasSelection;
        openFolderButton.Enabled = wadListView.SelectedItems.Count == 1;
    }
    
    private void WadListView_MouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (wadListView.SelectedItems.Count == 1)
        {
            if (wadListView.SelectedItems[0].Tag is WadEntry wad)
            {
                OpenFolderAndSelect(wad.Path);
            }
        }
    }
    
    private void OpenFolderButton_Click(object? sender, EventArgs e)
    {
        if (wadListView.SelectedItems.Count == 1)
        {
            if (wadListView.SelectedItems[0].Tag is WadEntry wad)
            {
                OpenFolderAndSelect(wad.Path);
            }
        }
    }
    
    private static void OpenFolderAndSelect(string filePath)
    {
        if (File.Exists(filePath))
        {
            Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
    }
    
    private void DeleteButton_Click(object? sender, EventArgs e)
    {
        if (wadListView.SelectedItems.Count == 0) return;
        
        var wads = wadListView.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag as WadEntry)
            .Where(w => w != null)
            .Cast<WadEntry>()
            .ToList();
        
        if (wads.Count == 0) return;
        
        var message = wads.Count == 1
            ? $"Delete '{wads[0].Name}'?\n\nThis action cannot be undone."
            : $"Delete {wads.Count} selected WAD files?\n\nThis action cannot be undone.";
        
        var result = MessageBox.Show(this, message, "Confirm Delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        
        if (result != DialogResult.Yes) return;
        
        int deleted = 0;
        int failed = 0;
        
        foreach (var wad in wads)
        {
            try
            {
                if (File.Exists(wad.Path))
                {
                    File.Delete(wad.Path);
                    _allWads.Remove(wad);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to delete {wad.Path}: {ex.Message}");
                failed++;
            }
        }
        
        PopulateListView();
        
        if (failed > 0)
        {
            statusLabel.Text = $"Deleted {deleted} files, {failed} failed";
        }
        else
        {
            statusLabel.Text = $"Deleted {deleted} files";
        }
        
        // Refresh WAD manager cache
        _wadManager.RefreshCache();
    }
    
    private void MoveButton_Click(object? sender, EventArgs e)
    {
        if (wadListView.SelectedItems.Count == 0) return;
        
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select destination folder",
            UseDescriptionForTitle = true
        };
        
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        
        var destination = dialog.SelectedPath;
        var wads = wadListView.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag as WadEntry)
            .Where(w => w != null)
            .Cast<WadEntry>()
            .ToList();
        
        if (wads.Count == 0) return;
        
        int moved = 0;
        int failed = 0;
        
        foreach (var wad in wads)
        {
            try
            {
                if (File.Exists(wad.Path))
                {
                    var newPath = Path.Combine(destination, Path.GetFileName(wad.Path));
                    
                    // Handle name conflicts
                    if (File.Exists(newPath))
                    {
                        var baseName = Path.GetFileNameWithoutExtension(wad.Path);
                        var ext = Path.GetExtension(wad.Path);
                        var counter = 1;
                        while (File.Exists(newPath))
                        {
                            newPath = Path.Combine(destination, $"{baseName}_{counter++}{ext}");
                        }
                    }
                    
                    File.Move(wad.Path, newPath);
                    wad.Path = newPath;
                    moved++;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to move {wad.Path}: {ex.Message}");
                failed++;
            }
        }
        
        PopulateListView();
        
        if (failed > 0)
        {
            statusLabel.Text = $"Moved {moved} files, {failed} failed";
        }
        else
        {
            statusLabel.Text = $"Moved {moved} files to {destination}";
        }
        
        // Refresh WAD manager cache
        _wadManager.RefreshCache();
    }
    
    private void ApplyDarkTheme()
    {
        BackColor = DarkTheme.PrimaryBackground;
        ForeColor = DarkTheme.TextPrimary;
        
        DarkTheme.Apply(this);
        DarkTheme.ApplyToButton(refreshButton);
        DarkTheme.ApplyToButton(showDuplicatesButton);
        DarkTheme.ApplyToButton(deleteButton);
        DarkTheme.ApplyToButton(moveButton);
        DarkTheme.ApplyToButton(openFolderButton);
        DarkTheme.ApplyToButton(closeButton);
        DarkTheme.ApplyToListView(wadListView);
    }
    
    private class WadEntry
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public string? Hash { get; set; }
        public string? DuplicateOf { get; set; }
    }
}
