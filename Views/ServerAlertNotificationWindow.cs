using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ZScape.Services;

namespace ZScape.Views;

internal sealed class ServerAlertNotificationWindow : Window
{
    private readonly DispatcherTimer _autoCloseTimer = new();

    public event EventHandler<ServerAlertAction>? ActionInvoked;

    public ServerAlertNotificationWindow(
        string title,
        string message,
        string detail,
        IReadOnlyList<AlertActionDefinition> actions,
        int autoCloseSeconds)
    {
        Width = 420;
        Height = actions.Count > 1 ? 190 : 170;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var actionPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        foreach (var action in actions)
        {
            var button = new Button
            {
                Content = action.Label,
                MinWidth = 108,
                Height = 30
            };

            if (action.IsPrimary)
            {
                button.FontWeight = FontWeight.SemiBold;
            }

            button.Click += (_, _) =>
            {
                ActionInvoked?.Invoke(this, action.Action);
                Close();
            };
            actionPanel.Children.Add(button);
        }

        var dismissButton = new Button
        {
            Content = "Dismiss",
            Width = 82,
            Height = 30
        };
        dismissButton.Click += (_, _) => Close();
        actionPanel.Children.Add(dismissButton);

        Content = new Border
        {
            Margin = new Thickness(1),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(73, 91, 113)),
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 30)),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 15,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Brushes.White,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = message,
                        FontSize = 13,
                        Foreground = Brushes.White,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = detail,
                        FontSize = 11,
                        Foreground = Brushes.Gray,
                        TextWrapping = TextWrapping.Wrap
                    },
                    actionPanel
                }
            }
        };

        var normalizedAutoCloseSeconds = Math.Max(0, autoCloseSeconds);
        _autoCloseTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, normalizedAutoCloseSeconds));
        _autoCloseTimer.Tick += (_, _) => Close();

        Opened += (_, _) =>
        {
            if (normalizedAutoCloseSeconds > 0)
            {
                _autoCloseTimer.Start();
            }
        };
        Closed += (_, _) => _autoCloseTimer.Stop();
    }
}