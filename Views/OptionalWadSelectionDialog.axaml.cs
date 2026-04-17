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

    public OptionalWadSelectionDialog()
        : this([], [], null)
    {
    }

    public OptionalWadSelectionDialog(
        IEnumerable<WadInfo> requiredWads,
        IEnumerable<WadInfo> optionalWads,
        ISet<string>? skippedOptionalPwads)
    {
        InitializeComponent();

        var skipped = skippedOptionalPwads ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var wad in requiredWads)
        {
            wad.IsOptional = false;
            _items.Add(new WadSelectionItem(wad, isRequired: true, isSelected: true));
        }

        foreach (var wad in optionalWads)
        {
            wad.IsOptional = true;
            var isSelected = !skipped.Contains(wad.FileName);
            _items.Add(new WadSelectionItem(wad, isRequired: false, isSelected: isSelected));
        }

        SelectionItemsControl.ItemsSource = _items;
        RememberSkippedCheckBox.IsVisible = _items.Any(item => item.CanChangeSelection);

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

    public class WadSelectionItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public WadSelectionItem(WadInfo wad, bool isRequired, bool isSelected)
        {
            Wad = wad;
            IsRequired = isRequired;
            _isSelected = isRequired || isSelected;
        }

        public WadInfo Wad { get; }

        public bool IsRequired { get; }

        public bool CanChangeSelection => !IsRequired;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                var normalizedValue = IsRequired || value;
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