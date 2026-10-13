using System.Windows;
using System.Windows.Controls;

namespace Kyuplan.Client.WpfApp.Controls;

public partial class BeltSwatch : UserControl
{
    /// <summary>The belt's colour as <c>#RRGGBB</c>; empty = nothing is drawn.</summary>
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(BeltSwatch), new PropertyMetadata(""));

    /// <summary>The colour of the stripe as <c>#RRGGBB</c>; empty or <c>null</c> = no stripe.</summary>
    public static readonly DependencyProperty StripeProperty = DependencyProperty.Register(
        nameof(Stripe), typeof(string), typeof(BeltSwatch), new PropertyMetadata(""));

    public BeltSwatch() => InitializeComponent();

    public string Color { get => (string)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    public string? Stripe { get => (string?)GetValue(StripeProperty); set => SetValue(StripeProperty, value); }
}
