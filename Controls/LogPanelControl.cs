using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ZScape.Views;

namespace ZScape.Controls;

/// <summary>
/// Custom log panel that displays log entries with colored text and auto-scrolling.
/// Built programmatically to avoid XAML binding issues.
/// </summary>
public class LogPanelControl : UserControl
{
    private readonly ScrollViewer _scrollViewer;
    private readonly StackPanel _logPanel;
    private ObservableCollection<LogEntryViewModel>? _logEntries;
    
    public LogPanelControl()
    {
        _logPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 0
        };
        
        _scrollViewer = new ScrollViewer
        {
            Content = _logPanel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        
        Content = _scrollViewer;
    }
    
    public ObservableCollection<LogEntryViewModel>? ItemsSource
    {
        get => _logEntries;
        set
        {
            if (_logEntries != null)
            {
                _logEntries.CollectionChanged -= OnLogEntriesChanged;
            }
            
            _logEntries = value;
            _logPanel.Children.Clear();
            
            if (_logEntries != null)
            {
                foreach (var entry in _logEntries)
                {
                    AddLogEntry(entry);
                }
                _logEntries.CollectionChanged += OnLogEntriesChanged;
            }
        }
    }
    
    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems != null)
                {
                    foreach (LogEntryViewModel entry in e.NewItems)
                    {
                        AddLogEntry(entry);
                    }
                    // Auto-scroll to bottom
                    _scrollViewer.ScrollToEnd();
                }
                break;
                
            case NotifyCollectionChangedAction.Remove:
                if (e.OldStartingIndex >= 0 && e.OldItems != null)
                {
                    for (int i = 0; i < e.OldItems.Count; i++)
                    {
                        if (_logPanel.Children.Count > e.OldStartingIndex)
                        {
                            _logPanel.Children.RemoveAt(e.OldStartingIndex);
                        }
                    }
                }
                break;
                
            case NotifyCollectionChangedAction.Reset:
                _logPanel.Children.Clear();
                break;
        }
    }
    
    private void AddLogEntry(LogEntryViewModel entry)
    {
        var textBlock = new TextBlock
        {
            Text = entry.Text,
            Foreground = entry.Color,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 1)
        };
        _logPanel.Children.Add(textBlock);
    }
    
    public void ScrollToEnd()
    {
        _scrollViewer.ScrollToEnd();
    }
}
