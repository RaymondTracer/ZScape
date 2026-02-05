using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Collections.ObjectModel;
using ZScape.Views;

namespace ZScape.Controls;

/// <summary>
/// Custom server list control that builds DataGrid programmatically to avoid XAML binding issues.
/// </summary>
public class ServerListControl : UserControl
{
    private readonly DataGrid _dataGrid;
    
    public event EventHandler<SelectionChangedEventArgs>? SelectionChanged;
    public new event EventHandler<TappedEventArgs>? DoubleTapped;
    public event EventHandler<DataGridRowEventArgs>? LoadingRow;
    public event EventHandler<DataGridColumnEventArgs>? Sorting;
    public event EventHandler<PointerPressedEventArgs>? GridPointerPressed;
    
    public ServerListControl()
    {
        _dataGrid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserReorderColumns = false,
            CanUserResizeColumns = true,
            CanUserSortColumns = true,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
            RowBackground = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30)),
            BorderBrush = new SolidColorBrush(Colors.Red), // DEBUG: Make border visible
            BorderThickness = new Thickness(3),             // DEBUG: Thick border
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 100 // DEBUG: Ensure minimum height
        };
        
        // Build columns programmatically
        _dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "",
            Binding = new Binding("FavoriteIcon"),
            Width = new DataGridLength(30),
            CanUserResize = false
        });
        
        _dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "",
            Binding = new Binding("LockIcon"),
            Width = new DataGridLength(24),
            CanUserResize = false
        });
        
        _dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Server Name",
            Binding = new Binding("Name"),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            MinWidth = 200
        });
        
        _dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Players",
            Binding = new Binding("PlayersDisplay"),
            Width = new DataGridLength(80)
        });
        
        _dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Ping",
            Binding = new Binding("Ping"),
            Width = new DataGridLength(60)
        });
        
        _dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Map",
            Binding = new Binding("Map"),
            Width = new DataGridLength(100)
        });
        
        _dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Mode",
            Binding = new Binding("GameModeDisplay"),
            Width = new DataGridLength(80)
        });
        
        _dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "IWAD",
            Binding = new Binding("IWAD"),
            Width = new DataGridLength(100)
        });
        
        _dataGrid.Columns.Add(new DataGridTextColumn
        {
            Header = "Address",
            Binding = new Binding("AddressDisplay"),
            Width = new DataGridLength(130)
        });
        
        // Wire up events
        _dataGrid.SelectionChanged += (s, e) => SelectionChanged?.Invoke(this, e);
        _dataGrid.DoubleTapped += (s, e) => DoubleTapped?.Invoke(this, e);
        _dataGrid.LoadingRow += (s, e) => LoadingRow?.Invoke(this, e);
        _dataGrid.Sorting += (s, e) => Sorting?.Invoke(this, e);
        _dataGrid.PointerPressed += (s, e) => GridPointerPressed?.Invoke(this, e);
        
        Content = _dataGrid;
    }
    
    public ObservableCollection<ServerViewModel>? ItemsSource
    {
        get => _dataGrid.ItemsSource as ObservableCollection<ServerViewModel>;
        set => _dataGrid.ItemsSource = value;
    }
    
    public object? SelectedItem
    {
        get => _dataGrid.SelectedItem;
        set => _dataGrid.SelectedItem = value;
    }
    
    public new ContextMenu? ContextMenu
    {
        get => _dataGrid.ContextMenu;
        set => _dataGrid.ContextMenu = value;
    }
    
    public DataGrid InnerDataGrid => _dataGrid;
}
