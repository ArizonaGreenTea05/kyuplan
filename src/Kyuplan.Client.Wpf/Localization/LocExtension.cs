using System.Windows.Data;
using System.Windows.Markup;

namespace Kyuplan.Client.WpfApp.Localization;

/// <summary>
/// A text from Strings.resx in the current language: <c>Text="{loc:Loc Nav_Dashboard}"</c>. It is a one-way binding to
/// <see cref="LocalizationSource"/>, so it follows a language change without reopening the window.
/// </summary>
public sealed class LocExtension(string key) : MarkupExtension
{
    [ConstructorArgument("key")]
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = LocalizationSource.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
