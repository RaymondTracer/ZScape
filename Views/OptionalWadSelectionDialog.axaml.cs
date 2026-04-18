using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using ZScape.Models;

namespace ZScape.Views;

public partial class OptionalWadSelectionDialog : Window
{
    private readonly ObservableCollection<WadSelectionItem> _items = [];
    private readonly bool _allowOptionalSelection;

    public OptionalWadSelectionDialog()
        : this([], [], null, allowOptionalSelection: true)
    {
    }

    public OptionalWadSelectionDialog(
        IEnumerable<WadInfo> requiredWads,
        IEnumerable<WadInfo> optionalWads,
        ISet<string>? skippedOptionalPwads,
        bool allowOptionalSelection = true)
    {
        InitializeComponent();

        _allowOptionalSelection = allowOptionalSelection;
        var skipped = skippedOptionalPwads ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var wad in requiredWads.OrderBy(wad => wad.FileName, StringComparer.OrdinalIgnoreCase))
        {
            wad.IsOptional = false;
            var item = new WadSelectionItem(wad, isRequired: true, isSelected: true, canChangeSelection: false);
            item.PropertyChanged += OnItemPropertyChanged;
            _items.Add(item);
        }

        foreach (var wad in optionalWads.OrderBy(wad => wad.FileName, StringComparer.OrdinalIgnoreCase))
        {
            wad.IsOptional = true;
            var isSelected = !allowOptionalSelection || !skipped.Contains(wad.FileName);
            var item = new WadSelectionItem(wad, isRequired: false, isSelected: isSelected, canChangeSelection: allowOptionalSelection);
            item.PropertyChanged += OnItemPropertyChanged;
            _items.Add(item);
        }

        SelectionItemsControl.ItemsSource = _items;

        var requiredCount = _items.Count(item => item.IsRequired);
        var optionalCount = _items.Count - requiredCount;
        var hasSelectableOptionalWads = _items.Any(item => item.CanChangeSelection);

        DescriptionTextBlock.Text = BuildDescription(requiredCount, optionalCount, hasSelectableOptionalWads);
        SelectAllOptionalButton.IsVisible = hasSelectableOptionalWads;
        ClearOptionalButton.IsVisible = hasSelectableOptionalWads;
        RememberSkippedCheckBox.IsVisible = hasSelectableOptionalWads;

        UpdateSelectionSummary();
        UpdateOptionalActionState();

        KeyDown += OnDialogKeyDown;
    }

    public List<WadInfo> SelectedOptionalWads => _items
        .Where(item => item.CanChangeSelection && item.IsSelected)
        .Select(item => item.Wad)
        .ToList();

    public List<string> OptionalWadNames => _items
        .Where(item => item.CanChangeSelection)
        .Select(item => item.Wad.FileName)
        .ToList();

    public List<string> SkippedOptionalWadNames => _items
        .Where(item => item.CanChangeSelection && !item.IsSelected)
        .Select(item => item.Wad.FileName)
        .ToList();

    public bool RememberSkippedSelections => RememberSkippedCheckBox.IsChecked ?? false;

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WadSelectionItem.IsSelected))
        {
            return;
        }

        UpdateSelectionSummary();
        UpdateOptionalActionState();
    }

    private void UpdateSelectionSummary()
    {
        var requiredCount = _items.Count(item => item.IsRequired);
        var optionalCount = _items.Count - requiredCount;
        var selectedOptionalCount = _items.Count(item => !item.IsRequired && item.IsSelected);
        var selectedCount = requiredCount + selectedOptionalCount;

        SummaryTextBlock.Text = optionalCount == 0
            ? $"{requiredCount} required WAD(s)"
            : requiredCount == 0
                ? _allowOptionalSelection
                    ? $"{selectedOptionalCount} of {optionalCount} optional selected"
                    : $"{optionalCount} optional WAD(s)"
                : _allowOptionalSelection
                    ? $"{requiredCount} required, {selectedOptionalCount} of {optionalCount} optional selected"
                    : $"{requiredCount} required, {optionalCount} optional";

        ContinueButton.Content = selectedCount == 0
            ? "Continue Without Downloads"
            : selectedCount == 1
                ? "Download 1 WAD"
                : $"Download {selectedCount} WADs";
    }

    private void UpdateOptionalActionState()
    {
        var optionalItems = _items.Where(item => item.CanChangeSelection).ToList();
        if (optionalItems.Count == 0)
        {
            return;
        }

        SelectAllOptionalButton.IsEnabled = optionalItems.Any(item => !item.IsSelected);
        ClearOptionalButton.IsEnabled = optionalItems.Any(item => item.IsSelected);
    }

    private static string BuildDescription(int requiredCount, int optionalCount, bool allowOptionalSelection)
    {
        if (requiredCount > 0 && optionalCount > 0)
        {
            return allowOptionalSelection
                ? "Required WADs will be downloaded to join this server. Optional WADs can be unchecked to skip for this join."
                : "Review the WADs that will be downloaded before joining this server.";
        }

        if (requiredCount > 0)
        {
            return "These WADs are required to join this server.";
        }

        if (optionalCount > 0)
        {
            return allowOptionalSelection
                ? "Choose which optional WADs to download for this join."
                : "Review the optional WADs that will be downloaded for this join.";
        }

        return "No WAD downloads are needed.";
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            Close(true);
            e.Handled = true;
        }
    }

    private void ContinueButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void SelectAllOptionalButton_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _items.Where(item => item.CanChangeSelection))
        {
            item.IsSelected = true;
        }
    }

    private void ClearOptionalButton_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _items.Where(item => item.CanChangeSelection))
        {
            item.IsSelected = false;
        }
    }

    public class WadSelectionItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private readonly bool _canChangeSelection;

        public WadSelectionItem(WadInfo wad, bool isRequired, bool isSelected, bool canChangeSelection)
        {
            Wad = wad;
            IsRequired = isRequired;
            _canChangeSelection = !isRequired && canChangeSelection;
            _isSelected = isRequired || isSelected;
        }

        public WadInfo Wad { get; }

        public bool IsRequired { get; }

        public bool CanChangeSelection => _canChangeSelection;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                var normalizedValue = IsRequired
                    ? true
                    : CanChangeSelection ? value : _isSelected;
                if (_isSelected == normalizedValue)
                    return;

                _isSelected = normalizedValue;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DetailLabel));
            }
        }

        public string FileName => Wad.FileName;

        public string TypeLabel => IsRequired ? "Required" : "Optional";

        public string DetailLabel => IsRequired
            ? "Required to join"
            : IsSelected ? "Will be downloaded" : "Skipped for this join";

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}