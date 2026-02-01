using ZScape.Utilities;

namespace ZScape.UI;

/// <summary>
/// Dialog for manually adding a server by IP address and port.
/// </summary>
public class AddServerDialog : Form
{
    private TextBox _addressTextBox = null!;
    private NumericUpDown _portNumeric = null!;
    private CheckBox _favoriteCheckBox = null!;
    private Button _addButton = null!;
    private Button _cancelButton = null!;
    private Label _statusLabel = null!;
    
    /// <summary>
    /// The server IP address entered by the user.
    /// </summary>
    public string ServerAddress => _addressTextBox.Text.Trim();
    
    /// <summary>
    /// The server port entered by the user.
    /// </summary>
    public int ServerPort => (int)_portNumeric.Value;
    
    /// <summary>
    /// Whether to add the server as a favorite.
    /// </summary>
    public bool AddAsFavorite => _favoriteCheckBox.Checked;
    
    public AddServerDialog()
    {
        InitializeComponent();
        ApplyDarkTheme();
        DarkModeHelper.ApplyDarkTitleBar(this);
    }
    
    private void InitializeComponent()
    {
        Text = "Add Server Manually";
        Size = new Size(400, 220);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        
        var mainPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            Padding = new Padding(15)
        };
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        mainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); // Address
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); // Port
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 35)); // Favorite
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Spacer
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); // Buttons
        
        // Address label and textbox
        var addressLabel = new Label
        {
            Text = "IP Address:",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        };
        mainPanel.Controls.Add(addressLabel, 0, 0);
        
        _addressTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 5, 0, 5)
        };
        _addressTextBox.TextChanged += AddressTextBox_TextChanged;
        mainPanel.Controls.Add(_addressTextBox, 1, 0);
        
        // Port label and numeric
        var portLabel = new Label
        {
            Text = "Port:",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        };
        mainPanel.Controls.Add(portLabel, 0, 1);
        
        _portNumeric = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Value = 10666, // Default Zandronum port
            Dock = DockStyle.Left,
            Width = 100,
            Margin = new Padding(0, 5, 0, 5)
        };
        mainPanel.Controls.Add(_portNumeric, 1, 1);
        
        // Favorite checkbox (spans 2 columns)
        _favoriteCheckBox = new CheckBox
        {
            Text = "Add to Favorites",
            Checked = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 5, 0, 5)
        };
        mainPanel.SetColumnSpan(_favoriteCheckBox, 2);
        mainPanel.Controls.Add(_favoriteCheckBox, 0, 2);
        
        // Buttons panel
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 5, 0, 0)
        };
        
        _cancelButton = new Button
        {
            Text = "Cancel",
            Size = new Size(80, 30),
            DialogResult = DialogResult.Cancel
        };
        buttonPanel.Controls.Add(_cancelButton);
        
        _addButton = new Button
        {
            Text = "Add",
            Size = new Size(80, 30),
            Enabled = false,
            Margin = new Padding(0, 0, 10, 0)
        };
        _addButton.Click += AddButton_Click;
        buttonPanel.Controls.Add(_addButton);
        
        _statusLabel = new Label
        {
            Text = "Enter an IP address",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 8, 10, 0),
            ForeColor = Color.Gray
        };
        buttonPanel.Controls.Add(_statusLabel);
        
        mainPanel.SetColumnSpan(buttonPanel, 2);
        mainPanel.Controls.Add(buttonPanel, 0, 4);
        
        Controls.Add(mainPanel);
        
        AcceptButton = _addButton;
        CancelButton = _cancelButton;
    }
    
    private void AddressTextBox_TextChanged(object? sender, EventArgs e)
    {
        var address = _addressTextBox.Text.Trim();
        
        if (string.IsNullOrWhiteSpace(address))
        {
            _addButton.Enabled = false;
            _statusLabel.Text = "Enter an IP address";
            return;
        }
        
        // Try to parse as IP address
        if (System.Net.IPAddress.TryParse(address, out _))
        {
            _addButton.Enabled = true;
            _statusLabel.Text = "Valid IP address";
            _statusLabel.ForeColor = DarkTheme.SuccessColor;
        }
        // Also allow hostnames (basic check)
        else if (IsValidHostname(address))
        {
            _addButton.Enabled = true;
            _statusLabel.Text = "Hostname (will be resolved)";
            _statusLabel.ForeColor = DarkTheme.TextSecondary;
        }
        else
        {
            _addButton.Enabled = false;
            _statusLabel.Text = "Invalid IP address or hostname";
            _statusLabel.ForeColor = DarkTheme.ErrorColor;
        }
    }
    
    private static bool IsValidHostname(string hostname)
    {
        // Basic hostname validation
        if (string.IsNullOrWhiteSpace(hostname) || hostname.Length > 255)
            return false;
        
        // Must contain at least one dot for domain names, or be localhost
        if (hostname.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
            
        // Check for valid characters
        foreach (char c in hostname)
        {
            if (!char.IsLetterOrDigit(c) && c != '.' && c != '-')
                return false;
        }
        
        return hostname.Contains('.');
    }
    
    private void AddButton_Click(object? sender, EventArgs e)
    {
        var address = _addressTextBox.Text.Trim();
        
        // Try to resolve hostname to IP if needed
        if (!System.Net.IPAddress.TryParse(address, out var ipAddress))
        {
            try
            {
                var hostEntry = System.Net.Dns.GetHostEntry(address);
                if (hostEntry.AddressList.Length > 0)
                {
                    // Use the first IPv4 address if available
                    ipAddress = hostEntry.AddressList
                        .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        ?? hostEntry.AddressList[0];
                    
                    _addressTextBox.Text = ipAddress.ToString();
                }
                else
                {
                    MessageBox.Show("Could not resolve hostname to an IP address.", "Resolution Failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to resolve hostname: {ex.Message}", "Resolution Failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }
        
        DialogResult = DialogResult.OK;
        Close();
    }
    
    private void ApplyDarkTheme()
    {
        BackColor = DarkTheme.PrimaryBackground;
        ForeColor = DarkTheme.TextPrimary;
        
        foreach (Control control in Controls)
        {
            ApplyThemeToControl(control);
        }
    }
    
    private void ApplyThemeToControl(Control control)
    {
        control.BackColor = DarkTheme.PrimaryBackground;
        control.ForeColor = DarkTheme.TextPrimary;
        
        if (control is TextBox textBox)
        {
            textBox.BackColor = DarkTheme.SecondaryBackground;
            textBox.ForeColor = DarkTheme.TextPrimary;
            textBox.BorderStyle = BorderStyle.FixedSingle;
        }
        else if (control is NumericUpDown numericUpDown)
        {
            numericUpDown.BackColor = DarkTheme.SecondaryBackground;
            numericUpDown.ForeColor = DarkTheme.TextPrimary;
        }
        else if (control is Button button)
        {
            button.BackColor = DarkTheme.SecondaryBackground;
            button.ForeColor = DarkTheme.TextPrimary;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = DarkTheme.BorderColor;
        }
        else if (control is CheckBox checkBox)
        {
            checkBox.ForeColor = DarkTheme.TextPrimary;
        }
        
        foreach (Control child in control.Controls)
        {
            ApplyThemeToControl(child);
        }
    }
}
