using System.ComponentModel;
using System.Windows.Data;
using Kyuplan.Client.Resources;

namespace Kyuplan.Client.WpfApp.Localization;

/// <summary>
/// The texts of Strings.resx as something XAML can bind to, so labels change the moment the language does.
/// <see cref="Refresh"/> tells every <c>{loc:Loc Key}</c> to read its text again.
/// </summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    public static LocalizationSource Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Strings.ResourceManager.GetString(key) ?? $"[{key}]";

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
}
