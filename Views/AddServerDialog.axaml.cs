using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace ZScape.Views;

/// <summary>
/// Dialog for manually adding a server by IP address and port.
/// </summary>
public partial class AddServerDialog : Window
{
    /// <summary>
    /// The server IP address entered by the user.
    /// </summary>
    public string ServerAddress { get; private set; } = "";

    /// <summary>
    /// The server port entered by the user.
    /// </summary>
    public int ServerPort { get; private set; } = 10666;

    /// <summary>
    /// Whether to add the server as a favorite.
    /// </summary>
    public bool AddAsFavorite { get; private set; } = true;

    /// <summary>
    /// Whether the dialog was confirmed (OK clicked).
    /// </summary>
    public bool Confirmed { get; private set; }

    public AddServerDialog()
    {
        InitializeComponent();
        
        // Handle Escape/Enter keys
        KeyDown += OnDialogKeyDown;
        
        // Subscribe to text changed after window loads
        Loaded += (_, _) =>
        {
            AddressTextBox.TextChanged += AddressTextBox_TextChanged;
        };
    }
    
    private void OnDialogKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            CancelButton_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Avalonia.Input.Key.Enter && AddButton.IsEnabled)
        {
            AddButton_Click(sender, e);
            e.Handled = true;
        }
    }

    private void AddressTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var address = AddressTextBox.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(address))
        {
            AddButton.IsEnabled = false;
            StatusLabel.Text = "Enter an IP address";
            StatusLabel.Foreground = Brushes.Gray;
            return;
        }

        // Try to parse as IP address
        if (IPAddress.TryParse(address, out _))
        {
            AddButton.IsEnabled = true;
            StatusLabel.Text = "Valid IP address";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(100, 200, 100)); // Success green
        }
        // Also allow hostnames (basic check)
        else if (IsValidHostname(address))
        {
            AddButton.IsEnabled = true;
            StatusLabel.Text = "Hostname (will be resolved)";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)); // Text secondary
        }
        else
        {
            AddButton.IsEnabled = false;
            StatusLabel.Text = "Invalid IP address or hostname";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(200, 100, 100)); // Error red
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

    private async void AddButton_Click(object? sender, RoutedEventArgs e)
    {
        var address = AddressTextBox.Text?.Trim() ?? "";

        // Try to resolve hostname to IP if needed
        if (!IPAddress.TryParse(address, out var ipAddress))
        {
            try
            {
                var hostEntry = await Dns.GetHostEntryAsync(address);
                if (hostEntry.AddressList.Length > 0)
                {
                    // Use the first IPv4 address if available
                    ipAddress = hostEntry.AddressList
                        .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                        ?? hostEntry.AddressList[0];

                    address = ipAddress.ToString();
                }
                else
                {
                    StatusLabel.Text = "Could not resolve hostname";
                    StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(200, 100, 100));
                    return;
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Resolution failed: {ex.Message}";
                StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(200, 100, 100));
                return;
            }
        }

        ServerAddress = address;
        ServerPort = PortNumeric.Value;
        AddAsFavorite = FavoriteCheckBox.IsChecked ?? true;
        Confirmed = true;
        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close(false);
    }
}
