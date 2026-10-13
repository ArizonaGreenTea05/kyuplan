using System.Windows;

namespace Kyuplan.Client.WpfApp.Controls;

/// <summary>
/// Hands the data context of a page to things that are not in the visual tree and so cannot bind to it themselves: the columns of a
/// DataGrid. Put one into the resources of the grid's page (<c>&lt;ctl:BindingProxy x:Key="Page" Data="{Binding}" /&gt;</c>) and bind
/// the column to <c>{Binding Data.CanEdit, Source={StaticResource Page}}</c>; a column the user may not use is then taken out, not left empty.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(object), typeof(BindingProxy), new PropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
