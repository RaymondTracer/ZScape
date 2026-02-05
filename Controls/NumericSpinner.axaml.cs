using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using System;

namespace ZScape.Controls;

public partial class NumericSpinner : UserControl
{
    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<NumericSpinner, int>(nameof(Value), 1, coerce: CoerceValue);

    public static readonly StyledProperty<int> MinimumProperty =
        AvaloniaProperty.Register<NumericSpinner, int>(nameof(Minimum), 1);

    public static readonly StyledProperty<int> MaximumProperty =
        AvaloniaProperty.Register<NumericSpinner, int>(nameof(Maximum), 100);

    public static readonly StyledProperty<int> IncrementProperty =
        AvaloniaProperty.Register<NumericSpinner, int>(nameof(Increment), 1);

    private TextBox? _valueTextBox;

    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Increment
    {
        get => GetValue(IncrementProperty);
        set => SetValue(IncrementProperty, value);
    }

    public event EventHandler<int>? ValueChanged;

    public NumericSpinner()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _valueTextBox = this.FindControl<TextBox>("ValueTextBox");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        
        if (change.Property == ValueProperty)
        {
            OnValueChanged((int)change.NewValue!);
        }
    }

    private static int CoerceValue(AvaloniaObject obj, int value)
    {
        if (obj is NumericSpinner spinner)
        {
            return Math.Clamp(value, spinner.Minimum, spinner.Maximum);
        }
        return value;
    }

    private void OnValueChanged(int newValue)
    {
        if (_valueTextBox != null)
        {
            _valueTextBox.Text = newValue.ToString();
        }
        ValueChanged?.Invoke(this, newValue);
    }

    private void UpButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Value = Math.Min(Value + Increment, Maximum);
        e.Handled = true;
    }

    private void DownButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Value = Math.Max(Value - Increment, Minimum);
        e.Handled = true;
    }

    private void ValueTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ParseAndSetValue();
            e.Handled = true;
        }
    }

    private void ValueTextBox_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ParseAndSetValue();
    }

    private void ParseAndSetValue()
    {
        if (_valueTextBox != null && int.TryParse(_valueTextBox.Text, out int newValue))
        {
            Value = Math.Clamp(newValue, Minimum, Maximum);
        }
        else if (_valueTextBox != null)
        {
            // Reset to current value if parsing fails
            _valueTextBox.Text = Value.ToString();
        }
    }
}
