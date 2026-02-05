using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using System;

namespace ZScape.Controls;

/// <summary>
/// A ComboBox that maintains its selection when the dropdown is closed without selecting.
/// Works around an Avalonia quirk where clicking away from an open dropdown clears the selection visually.
/// Also prevents spurious SelectionChanged events when opening the dropdown.
/// </summary>
public class PersistentComboBox : ComboBox
{
    private int _lastValidIndex = -1;

    /// <summary>
    /// Indicates whether a selection restoration is in progress.
    /// Check this in SelectionChanged handlers to ignore invalid transitions.
    /// </summary>
    public bool IsRestoringSelection { get; private set; }

    // Tell Avalonia to use ComboBox's theme/template
    protected override Type StyleKeyOverride => typeof(ComboBox);

    public PersistentComboBox()
    {
        SelectionChanged += OnSelectionChanged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        
        // When dropdown opens or closes, ensure selection is maintained
        if (change.Property == IsDropDownOpenProperty)
        {
            var isOpen = (bool)change.NewValue!;
            
            // Schedule immediate restoration after property change completes
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (SelectedIndex < 0 && _lastValidIndex >= 0)
                {
                    IsRestoringSelection = true;
                    SelectedIndex = _lastValidIndex;
                    IsRestoringSelection = false;
                }
            }, Avalonia.Threading.DispatcherPriority.Send);
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SelectedIndex >= 0 && !IsRestoringSelection)
        {
            _lastValidIndex = SelectedIndex;
        }
        else if (SelectedIndex < 0 && _lastValidIndex >= 0 && !IsRestoringSelection)
        {
            // Selection was cleared - restore it immediately
            IsRestoringSelection = true;
            SelectedIndex = _lastValidIndex;
            IsRestoringSelection = false;
        }
    }
}
