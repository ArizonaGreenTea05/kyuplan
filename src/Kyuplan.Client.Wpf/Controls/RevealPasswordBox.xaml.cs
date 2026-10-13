using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Kyuplan.Client.Resources;

namespace Kyuplan.Client.WpfApp.Controls;

public partial class RevealPasswordBox : UserControl
{
    public static readonly RoutedEvent PasswordChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(PasswordChanged), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(RevealPasswordBox));

    private bool _syncing;

    public RevealPasswordBox()
    {
        InitializeComponent();
        UpdateToggle();
    }

    /// <summary>Raised on every change, like PasswordBox.PasswordChanged.</summary>
    public event RoutedEventHandler PasswordChanged
    {
        add => AddHandler(PasswordChangedEvent, value);
        remove => RemoveHandler(PasswordChangedEvent, value);
    }

    public string Password
    {
        get => Hidden.Password;
        set
        {
            if (Hidden.Password != value) Hidden.Password = value;
        }
    }

    public int MaxLength
    {
        get => Hidden.MaxLength;
        set => Hidden.MaxLength = Shown.MaxLength = value;
    }

    /// <summary>Focus moves on to whichever box is showing.</summary>
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (ReferenceEquals(e.NewFocus, this)) (Toggle.IsChecked == true ? (UIElement)Shown : Hidden).Focus();
    }

    private void OnHiddenChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
        {
            _syncing = true;
            Shown.Text = Hidden.Password;
            _syncing = false;
        }

        RaiseEvent(new RoutedEventArgs(PasswordChangedEvent, this));
    }

    private void OnShownChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Hidden.Password = Shown.Text;
        _syncing = false;
    }

    private void OnToggled(object sender, RoutedEventArgs e)
    {
        var reveal = Toggle.IsChecked == true;
        Shown.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        Hidden.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        Glyph.Text = reveal ? "" : "";
        UpdateToggle();
        (reveal ? (UIElement)Shown : Hidden).Focus();
    }

    private void UpdateToggle() =>
        AutomationProperties.SetName(Toggle, Toggle.IsChecked == true ? Strings.Password_Hide : Strings.Password_Show);
}
