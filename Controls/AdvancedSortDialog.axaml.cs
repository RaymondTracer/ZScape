using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ZScape.Controls;

/// <summary>
/// Reusable editor for an ordered list-view sort chain.
/// </summary>
public partial class AdvancedSortDialog : Window, INotifyPropertyChanged
{
    private static readonly IReadOnlyList<AdvancedSortDirectionOption> Directions =
    [
        new("Ascending (A–Z, low–high)", true),
        new("Descending (Z–A, high–low)", false)
    ];

    private readonly IReadOnlyList<AdvancedSortColumnOption> _availableColumns;
    private AdvancedSortLevelItem? _selectedSortLevel;

    public ObservableCollection<AdvancedSortLevelItem> SortLevels { get; } = [];

    public AdvancedSortLevelItem? SelectedSortLevel
    {
        get => _selectedSortLevel;
        set
        {
            if (ReferenceEquals(_selectedSortLevel, value))
                return;

            _selectedSortLevel = value;
            OnPropertyChanged();
            UpdateControlState();
        }
    }

    public bool Confirmed { get; private set; }

    public IReadOnlyList<ListViewSortDescriptor> Result { get; private set; } = [];

    public new event PropertyChangedEventHandler? PropertyChanged;

    // Required by the XAML loader.
    public AdvancedSortDialog()
        : this([], [])
    {
    }

    internal AdvancedSortDialog(
        IReadOnlyList<AdvancedSortColumnOption> availableColumns,
        IReadOnlyList<ListViewSortDescriptor> currentSorts)
    {
        _availableColumns = availableColumns;

        InitializeComponent();
        DataContext = this;
        SetupSortLevelsList();
        SortLevelsListView.ItemsSource = SortLevels;
        SortLevelsListView.SelectionChanged += SortLevelsListView_SelectionChanged;

        foreach (var descriptor in currentSorts)
        {
            var column = _availableColumns.FirstOrDefault(option =>
                option.ColumnKey.Equals(
                    descriptor.ColumnKey,
                    StringComparison.OrdinalIgnoreCase));
            if (column == null)
                continue;

            AddLevel(column, descriptor.Ascending);
        }

        RefreshPriorities();
        SelectedSortLevel = SortLevels.FirstOrDefault();
        if (SelectedSortLevel != null)
            SortLevelsListView.SelectItem(SelectedSortLevel);
        UpdateControlState();

        KeyDown += OnDialogKeyDown;
    }

    private void SetupSortLevelsList()
    {
        SortLevelsListView.SelectionMode = ListViewSelectionMode.Single;
        SortLevelsListView.RowHeight = 34;
        SortLevelsListView.SuppressHandCursor = true;
        SortLevelsListView.FillLastVisibleColumn = true;

        SortLevelsListView.AddColumn(new ListViewColumn
        {
            Key = "priority",
            Header = "Level",
            Width = 58,
            MinWidth = 58,
            IsFixedWidth = true,
            CanUserHide = false,
            BindingPath = nameof(AdvancedSortLevelItem.Priority),
            ContentAlignment = HorizontalAlignment.Center,
            CellPadding = new Thickness(4, 0)
        });
        SortLevelsListView.AddColumn(new ListViewColumn
        {
            Key = "column",
            Header = "Column",
            Width = 280,
            MinWidth = 150,
            CanUserHide = false,
            CellContentFactory = CreateColumnEditorCell
        });
        SortLevelsListView.AddColumn(new ListViewColumn
        {
            Key = "direction",
            Header = "Direction",
            Width = 220,
            MinWidth = 150,
            CanUserHide = false,
            CellContentFactory = CreateDirectionEditorCell
        });

        SortLevelsListView.Build(ListViewOverflowMode.AutoScroll);
    }

    private Control CreateColumnEditorCell()
    {
        var combo = new ComboBox
        {
            Margin = new Thickness(4, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        combo.Bind(
            ItemsControl.ItemsSourceProperty,
            new Binding(nameof(AdvancedSortLevelItem.ColumnOptions)));
        combo.Bind(
            SelectingItemsControl.SelectedItemProperty,
            new Binding(nameof(AdvancedSortLevelItem.SelectedColumn))
            {
                Mode = BindingMode.TwoWay
            });
        combo.ItemTemplate = new FuncDataTemplate<AdvancedSortColumnOption>(
            (_, _) =>
            {
                var text = new TextBlock();
                text.Bind(TextBlock.TextProperty, new Binding(nameof(AdvancedSortColumnOption.DisplayName)));
                return text;
            });
        combo.SelectionChanged += LevelEditor_SelectionChanged;
        return combo;
    }

    private Control CreateDirectionEditorCell()
    {
        var combo = new ComboBox
        {
            Margin = new Thickness(0, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        combo.Bind(
            ItemsControl.ItemsSourceProperty,
            new Binding(nameof(AdvancedSortLevelItem.DirectionOptions)));
        combo.Bind(
            SelectingItemsControl.SelectedItemProperty,
            new Binding(nameof(AdvancedSortLevelItem.SelectedDirection))
            {
                Mode = BindingMode.TwoWay
            });
        combo.ItemTemplate = new FuncDataTemplate<AdvancedSortDirectionOption>(
            (_, _) =>
            {
                var text = new TextBlock();
                text.Bind(TextBlock.TextProperty, new Binding(nameof(AdvancedSortDirectionOption.Label)));
                return text;
            });
        combo.SelectionChanged += LevelEditor_SelectionChanged;
        return combo;
    }

    private void AddLevel(
        AdvancedSortColumnOption column,
        bool ascending)
    {
        var level = new AdvancedSortLevelItem(
            _availableColumns,
            Directions,
            column,
            Directions.First(direction => direction.Ascending == ascending));
        level.PropertyChanged += (_, _) => ValidationText.Text = string.Empty;
        SortLevels.Add(level);
    }

    private void AddLevelButton_Click(object? sender, RoutedEventArgs e)
    {
        var usedKeys = SortLevels
            .Select(level => level.SelectedColumn.ColumnKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextColumn = _availableColumns.FirstOrDefault(
            column => !usedKeys.Contains(column.ColumnKey));

        if (nextColumn == null)
        {
            ValidationText.Text = "Every sortable column is already in the list.";
            return;
        }

        AddLevel(nextColumn, !nextColumn.DefaultSortDescending);
        RefreshPriorities();
        SelectedSortLevel = SortLevels[^1];
        SortLevelsListView.SelectItem(SelectedSortLevel);
        ValidationText.Text = string.Empty;
    }

    private void RemoveLevelButton_Click(object? sender, RoutedEventArgs e)
    {
        var index = SelectedSortLevel == null
            ? -1
            : SortLevels.IndexOf(SelectedSortLevel);
        if (index < 0)
            return;

        SortLevels.RemoveAt(index);
        RefreshPriorities();
        SelectedSortLevel = SortLevels.Count == 0
            ? null
            : SortLevels[Math.Min(index, SortLevels.Count - 1)];
        ValidationText.Text = string.Empty;
    }

    private void MoveUpButton_Click(object? sender, RoutedEventArgs e)
    {
        MoveSelectedLevel(-1);
    }

    private void MoveDownButton_Click(object? sender, RoutedEventArgs e)
    {
        MoveSelectedLevel(1);
    }

    private void MoveSelectedLevel(int offset)
    {
        if (SelectedSortLevel == null)
            return;

        var currentIndex = SortLevels.IndexOf(SelectedSortLevel);
        var targetIndex = currentIndex + offset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= SortLevels.Count)
            return;

        SortLevels.Move(currentIndex, targetIndex);
        RefreshPriorities();
        SortLevelsListView.SelectItem(SelectedSortLevel);
        ValidationText.Text = string.Empty;
    }

    private void ClearAllButton_Click(object? sender, RoutedEventArgs e)
    {
        SortLevels.Clear();
        SelectedSortLevel = null;
        ValidationText.Text = string.Empty;
        UpdateControlState();
    }

    private void ApplyButton_Click(object? sender, RoutedEventArgs e)
    {
        var duplicateColumn = SortLevels
            .GroupBy(
                level => level.SelectedColumn.ColumnKey,
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateColumn != null)
        {
            ValidationText.Text =
                $"“{duplicateColumn.First().SelectedColumn.Header}” is used more than once.";
            return;
        }

        Result = SortLevels
            .Select(level => new ListViewSortDescriptor(
                level.SelectedColumn.ColumnIndex,
                level.SelectedColumn.ColumnKey,
                level.SelectedDirection.Ascending))
            .ToArray();
        Confirmed = true;
        Close();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SortLevelsListView_SelectionChanged(object? sender, EventArgs e)
    {
        SelectedSortLevel = SortLevelsListView.SelectedItem as AdvancedSortLevelItem;
        UpdateControlState();
    }

    private void LevelEditor_SelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        ValidationText.Text = string.Empty;
    }

    private void RefreshPriorities()
    {
        for (var index = 0; index < SortLevels.Count; index++)
            SortLevels[index].Priority = index + 1;

        UpdateControlState();
    }

    private void UpdateControlState()
    {
        if (AddLevelButton == null)
            return;

        var selectedIndex = SelectedSortLevel == null
            ? -1
            : SortLevels.IndexOf(SelectedSortLevel);
        var usedColumnCount = SortLevels
            .Select(level => level.SelectedColumn.ColumnKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        AddLevelButton.IsEnabled = usedColumnCount < _availableColumns.Count;
        RemoveLevelButton.IsEnabled = selectedIndex >= 0;
        MoveUpButton.IsEnabled = selectedIndex > 0;
        MoveDownButton.IsEnabled =
            selectedIndex >= 0 && selectedIndex < SortLevels.Count - 1;
        ClearAllButton.IsEnabled = SortLevels.Count > 0;
        SummaryText.Text = SortLevels.Count switch
        {
            0 => "No sort levels",
            1 => "1 sort level",
            _ => $"{SortLevels.Count} sort levels"
        };
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        Close();
        e.Handled = true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record AdvancedSortColumnOption(
    int ColumnIndex,
    string ColumnKey,
    string Header,
    bool DefaultSortDescending,
    bool IsVisible)
{
    public string DisplayName => IsVisible ? Header : $"{Header} (hidden)";
}

public sealed record AdvancedSortDirectionOption(
    string Label,
    bool Ascending);

public sealed class AdvancedSortLevelItem : INotifyPropertyChanged
{
    private int _priority;
    private AdvancedSortColumnOption _selectedColumn;
    private AdvancedSortDirectionOption _selectedDirection;

    internal AdvancedSortLevelItem(
        IReadOnlyList<AdvancedSortColumnOption> columnOptions,
        IReadOnlyList<AdvancedSortDirectionOption> directionOptions,
        AdvancedSortColumnOption selectedColumn,
        AdvancedSortDirectionOption selectedDirection)
    {
        ColumnOptions = columnOptions;
        DirectionOptions = directionOptions;
        _selectedColumn = selectedColumn;
        _selectedDirection = selectedDirection;
    }

    public IReadOnlyList<AdvancedSortColumnOption> ColumnOptions { get; }

    public IReadOnlyList<AdvancedSortDirectionOption> DirectionOptions { get; }

    public int Priority
    {
        get => _priority;
        set
        {
            if (_priority == value)
                return;
            _priority = value;
            OnPropertyChanged();
        }
    }

    public AdvancedSortColumnOption SelectedColumn
    {
        get => _selectedColumn;
        set
        {
            if (ReferenceEquals(_selectedColumn, value))
                return;
            _selectedColumn = value;
            OnPropertyChanged();
        }
    }

    public AdvancedSortDirectionOption SelectedDirection
    {
        get => _selectedDirection;
        set
        {
            if (ReferenceEquals(_selectedDirection, value))
                return;
            _selectedDirection = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
