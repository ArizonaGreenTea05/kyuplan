using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Kyuplan.Client.WpfApp.Controls;

public partial class NumberBox : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumberBox),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged, CoerceValue));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(int), typeof(NumberBox), new PropertyMetadata(int.MinValue, OnLimitChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(NumberBox), new PropertyMetadata(int.MaxValue, OnLimitChanged));

    /// <summary>Whether a number counts as soon as it is typed instead of when the box loses focus; see <see cref="OnTextChanged"/>.</summary>
    public static readonly DependencyProperty CommitWhileTypingProperty = DependencyProperty.Register(
        nameof(CommitWhileTyping), typeof(bool), typeof(NumberBox), new PropertyMetadata(false));

    private bool _showing;

    public NumberBox()
    {
        InitializeComponent();
        ShowValue();
    }

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Minimum { get => (int)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public int Maximum { get => (int)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public bool CommitWhileTyping { get => (bool)GetValue(CommitWhileTypingProperty); set => SetValue(CommitWhileTypingProperty, value); }

    /// <summary>Typing past the limits is not an error: the value is pulled back to the nearest one.</summary>
    private static object CoerceValue(DependencyObject d, object baseValue) => ((NumberBox)d).Clamp((int)baseValue);

    private int Clamp(int number) => Math.Clamp(number, Minimum, Math.Max(Minimum, Maximum));

    private static void OnLimitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        d.CoerceValue(ValueProperty);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((NumberBox)d).ShowValue();

    private void ShowValue()
    {
        var text = Value.ToString(CultureInfo.CurrentCulture);
        if (Box.Text == text) return;

        _showing = true;
        Box.Text = text;
        _showing = false;
    }

    /// <summary>
    /// Takes what is typed as the value, pulled back to the limits: clamped here, because a binding to <see cref="Value"/> would be handed
    /// the number as typed (150) while the value itself is coerced (99). What is not a number is replaced by the value.
    /// </summary>
    private void Commit()
    {
        if (int.TryParse(Box.Text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture, out var typed))
            Value = Clamp(typed);

        ShowValue();
    }

    /// <summary>
    /// A number that is typed in full and within the limits counts at once, so that what depends on it (a Save button that is only on
    /// when something changed) does not wait for the box to lose focus. Anything else (an empty box, "05", a number past the limits)
    /// waits for <see cref="Commit"/>, so the box never rewrites what is being typed.
    /// </summary>
    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_showing || !CommitWhileTyping) return;

        if (int.TryParse(Box.Text, NumberStyles.None, CultureInfo.CurrentCulture, out var typed)
            && typed.ToString(CultureInfo.CurrentCulture) == Box.Text && typed >= Minimum && typed <= Maximum)
            Value = typed;
    }

    private void OnLostFocus(object sender, RoutedEventArgs e) => Commit();

    private void Step(int delta)
    {
        // Commit what was typed before stepping from it.
        Commit();
        Value = Clamp(Value + delta);
        ShowValue();
    }

    private void OnUp(object sender, RoutedEventArgs e) => Step(1);

    private void OnDown(object sender, RoutedEventArgs e) => Step(-1);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up: Step(1); e.Handled = true; break;
            case Key.Down: Step(-1); e.Handled = true; break;
            case Key.Enter: Commit(); break;
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Box.IsKeyboardFocused) return;
        Step(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }
}
