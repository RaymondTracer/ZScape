using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using ZScape.Controls;
using ZScape.Services;

namespace ZScape.Views;

/// <summary>
/// Big UI shell: HTPC/Android TV style layout with a sidebar for tab navigation
/// and a main content area. Designed for d-pad, arrow keys, and controller input.
/// Replaces MainContentGrid when Big UI mode is active.
/// </summary>
public partial class BigUIShell : UserControl
{
    private string _activeTab = "servers";
    private readonly Dictionary<string, Border> _tabs = [];

    /// <summary>Fired when the user wants to exit Big UI mode.</summary>
    public event Action? ExitBigUIRequested;

    /// <summary>Fired when the user wants to quit the application entirely.</summary>
    public event Action? ExitZScapeRequested;

    /// <summary>Fired when the user clicks Refresh from the top bar.</summary>
    public event Action? RefreshRequested;

    /// <summary>Fired when the user clicks Launch from the top bar.</summary>
    public event Action? LaunchRequested;

    /// <summary>Fired when the search text changes.</summary>
    public event Action<string>? SearchTextChanged;

    /// <summary>Fired when a sidebar tab is selected.</summary>
    public event Action<string>? TabChanged;

    public string ActiveTab => _activeTab;

    public ResizableListView ServerListView => BigServerListView;

    public BigUIShell()
    {
        InitializeComponent();

        _tabs["servers"] = ServerListTab;
        _tabs["settings"] = SettingsTab;
        _tabs["filters"] = FiltersTab;
        _tabs["wads"] = WADsTab;

        ApplyActiveTab("servers");

        // Global escape handler on the root
        RootGrid.AddHandler(KeyDownEvent, OnGlobalKeyDown,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        // Server list: intercept Left/Up-at-top/Down-at-bottom BEFORE the list sees them
        BigServerListView.AddHandler(KeyDownEvent, OnServerListPreviewKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        // Top bar controls
        BigSearchBox.AddHandler(KeyDownEvent, OnTopBarKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        BigRefreshButton.AddHandler(KeyDownEvent, OnTopBarKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        BigLaunchButton.AddHandler(KeyDownEvent, OnTopBarKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        // Sidebar tab keyboard routing (use AddHandler + handledEventsToo to beat default focus nav)
        foreach (var kvp in _tabs)
        {
            kvp.Value.Focusable = true;
            kvp.Value.GotFocus += (_, _) => ClearListSelectionIfNotFocused();
            kvp.Value.AddHandler(KeyDownEvent, OnSidebarTabKeyDown,
                RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        // Exit buttons
        ExitBigUIButton.Focusable = true;
        ExitBigUIButton.GotFocus += (_, _) => ClearListSelectionIfNotFocused();
        ExitBigUIButton.AddHandler(KeyDownEvent, OnExitButtonKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        ExitZScapeButton.Focusable = true;
        ExitZScapeButton.GotFocus += (_, _) => ClearListSelectionIfNotFocused();
        ExitZScapeButton.AddHandler(KeyDownEvent, OnExitButtonKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        BigSearchBox.TextChanged += (_, _) =>
            SearchTextChanged?.Invoke(BigSearchBox.Text ?? "");

        BigSearchBox.GotFocus += (_, _) => ClearListSelectionIfNotFocused();
        BigRefreshButton.GotFocus += (_, _) => ClearListSelectionIfNotFocused();
        BigLaunchButton.GotFocus += (_, _) => ClearListSelectionIfNotFocused();
    }

    private async void OnSidebarTabKeyDown(object? sender, KeyEventArgs e)
    {
        var current = (Control)sender!;

        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            if (current is Border b && b.Tag is string tabId)
            {
                SelectTab(tabId);
                BigServerListView.Focus();
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right)
        {
            BigServerListView.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            FocusNextSidebarItem(current);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up)
        {
            FocusPreviousSidebarItem(current);
            e.Handled = true;
            return;
        }
    }

    private void OnExitButtonKeyDown(object? sender, KeyEventArgs e)
    {
        var current = (Control)sender!;

        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            _ = ConfirmAndActAsync(current == ExitBigUIButton);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right)
        {
            BigServerListView.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            if (current == ExitBigUIButton)
            {
                ExitZScapeButton.Focus();
            }
            else
            {
                BigServerListView.Focus();
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up)
        {
            if (current == ExitZScapeButton)
            {
                ExitBigUIButton.Focus();
            }
            else
            {
                WADsTab.Focus();
            }
            e.Handled = true;
            return;
        }
    }

    private async System.Threading.Tasks.Task ConfirmAndActAsync(bool isExitBigUI)
    {
        var label = isExitBigUI ? "Return to standard UI?" : "Quit ZScape?";
        var result = await ShowConfirmDialogAsync(label);

        if (result)
        {
            if (isExitBigUI)
                ExitBigUIRequested?.Invoke();
            else
                ExitZScapeRequested?.Invoke();
        }
    }

    /// <summary>
    /// Shows a simple two-button confirmation and returns true if the user confirms.
    /// Uses the shell's own visual tree as the parent.
    /// </summary>
    private System.Threading.Tasks.Task<bool> ShowConfirmDialogAsync(string message)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();

        // Build a simple overlay
        var overlay = new Border
        {
            Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(160, 0, 0, 0)),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Child = new Border
            {
                Background = ThemeService.GetBrush("SecondaryBackgroundBrush", "#1E1E1E"),
                CornerRadius = new Avalonia.CornerRadius(12),
                Padding = new Thickness(32, 24),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Child = new StackPanel
                {
                    Spacing = 24,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message,
                            FontSize = 22,
                            TextWrapping = TextWrapping.Wrap,
                            TextAlignment = TextAlignment.Center,
                            MaxWidth = 500
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            Spacing = 20,
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                            Children =
                            {
                                new Button
                                {
                                    Content = "Yes",
                                    FontSize = 18,
                                    Padding = new Thickness(32, 14)
                                },
                                new Button
                                {
                                    Content = "No",
                                    FontSize = 18,
                                    Padding = new Thickness(32, 14)
                                }
                            }
                        }
                    }
                }
            }
        };

        var buttons = ((StackPanel)((Border)overlay.Child!).Child!).Children;
        var yesBtn = (Button)((StackPanel)buttons[1]!).Children[0]!;
        var noBtn = (Button)((StackPanel)buttons[1]!).Children[1]!;

        var rootGrid = RootGrid;
        var contentBarrier = new Border
        {
            IsHitTestVisible = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            Child = overlay
        };

        Grid.SetColumn(contentBarrier, 0);
        Grid.SetColumnSpan(contentBarrier, 3);

        void Cleanup()
        {
            rootGrid.Children.Remove(contentBarrier);
        }

        yesBtn.Click += (_, _) => { Cleanup(); tcs.TrySetResult(true); };
        noBtn.Click += (_, _) => { Cleanup(); tcs.TrySetResult(false); };

        // Escape dismisses = cancel
        contentBarrier.AddHandler(KeyDownEvent, (object? s, KeyEventArgs ke) =>
        {
            if (ke.Key == Key.Escape || ke.Key == Key.N)
            {
                Cleanup();
                tcs.TrySetResult(false);
                ke.Handled = true;
            }
            else if (ke.Key == Key.Y || ke.Key == Key.Enter)
            {
                Cleanup();
                tcs.TrySetResult(true);
                ke.Handled = true;
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        rootGrid.Children.Add(contentBarrier);
        yesBtn.Focus();
        return tcs.Task;
    }

    private void ClearListSelectionIfNotFocused()
    {
        // When focus is NOT on the server list, clear its selection
        // so there's no phantom highlight when the user is in the sidebar or top bar.
        if (!BigServerListView.IsFocused)
            BigServerListView.ClearSelection();
    }

    private void FocusNextSidebarItem(Control current)
    {
        if (current == ExitBigUIButton)
        {
            ExitZScapeButton.Focus();
            return;
        }
        if (current == ExitZScapeButton)
        {
            BigServerListView.Focus();
            return;
        }

        if (current == WADsTab)
            ExitBigUIButton.Focus();
        else if (current == ServerListTab)
            SettingsTab.Focus();
        else if (current == SettingsTab)
            FiltersTab.Focus();
        else if (current == FiltersTab)
            WADsTab.Focus();
    }

    private void FocusPreviousSidebarItem(Control current)
    {
        if (current == ExitZScapeButton)
        {
            ExitBigUIButton.Focus();
            return;
        }
        if (current == ExitBigUIButton)
        {
            WADsTab.Focus();
            return;
        }

        if (current == WADsTab)
            FiltersTab.Focus();
        else if (current == FiltersTab)
            SettingsTab.Focus();
        else if (current == SettingsTab)
            ServerListTab.Focus();
        else if (current == ServerListTab)
            BigServerListView.Focus();
    }

    private void OnServerListPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left)
        {
            ServerListTab.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up && GetListSelectedIndex() <= 0)
        {
            BigSearchBox.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down && GetListSelectedIndex() >= GetListMaxIndex())
        {
            ExitBigUIButton.Focus();
            e.Handled = true;
        }
    }

    private void OnTopBarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left)
        {
            if (sender == BigSearchBox)
                ServerListTab.Focus();
            else if (sender == BigRefreshButton)
                BigSearchBox.Focus();
            else if (sender == BigLaunchButton)
                BigRefreshButton.Focus();

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right)
        {
            if (sender == BigSearchBox)
                BigRefreshButton.Focus();
            else if (sender == BigRefreshButton)
                BigLaunchButton.Focus();

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            BigServerListView.Focus();
            e.Handled = true;
        }
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ExitBigUIRequested?.Invoke();
            e.Handled = true;
        }
    }

    private int GetListSelectedIndex()
    {
        var source = BigServerListView.ItemsSource;
        if (source is System.Collections.IList items)
        {
            var sel = BigServerListView.SelectedItem;
            if (sel != null) return items.IndexOf(sel);
        }
        return 0;
    }

    private int GetListMaxIndex()
    {
        var source = BigServerListView.ItemsSource;
        if (source is System.Collections.ICollection col)
            return Math.Max(0, col.Count - 1);
        return 0;
    }

    private void Tab_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border && border.Tag is string tabId)
        {
            SelectTab(tabId);
            BigServerListView.Focus();
        }
    }

    private void BigRefreshButton_Click(object? sender, RoutedEventArgs e)
        => RefreshRequested?.Invoke();

    private void BigLaunchButton_Click(object? sender, RoutedEventArgs e)
        => LaunchRequested?.Invoke();

    private void ExitBigUIButton_Click(object? sender, RoutedEventArgs e)
        => _ = ConfirmAndActAsync(isExitBigUI: true);

    private void ExitZScapeButton_Click(object? sender, RoutedEventArgs e)
        => _ = ConfirmAndActAsync(isExitBigUI: false);

    public void SelectTab(string tabId)
    {
        if (_activeTab == tabId) return;
        ApplyActiveTab(tabId);
        TabChanged?.Invoke(tabId);
    }

    public void SetServerCount(int servers, int players)
    {
        BigServerCountLabel.Text = $"Servers: {servers}";
        BigPlayerCountLabel.Text = $"Players: {players}";
    }

    public void SetSearchText(string text)
    {
        BigSearchBox.Text = text;
    }

    public string GetSearchText() => BigSearchBox.Text ?? "";

    private void ApplyActiveTab(string tabId)
    {
        foreach (var (_, border) in _tabs)
            border.Classes.Remove("Active");

        _activeTab = tabId;
        if (_tabs.TryGetValue(tabId, out var active))
            active.Classes.Add("Active");
    }
}
