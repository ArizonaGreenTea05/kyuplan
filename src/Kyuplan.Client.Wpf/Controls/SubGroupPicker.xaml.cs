using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.WpfApp.Controls;

public partial class SubGroupPicker : UserControl
{
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
        nameof(Options), typeof(IEnumerable<SubGroupOption>), typeof(SubGroupPicker), new PropertyMetadata(null));

    public static readonly DependencyProperty SummaryProperty = DependencyProperty.Register(
        nameof(Summary), typeof(string), typeof(SubGroupPicker), new PropertyMetadata(""));

    public static readonly DependencyProperty IsEveryoneProperty = DependencyProperty.Register(
        nameof(IsEveryone), typeof(bool), typeof(SubGroupPicker), new PropertyMetadata(true));

    public static readonly DependencyProperty ClearCommandProperty = DependencyProperty.Register(
        nameof(ClearCommand), typeof(ICommand), typeof(SubGroupPicker), new PropertyMetadata(null));

    private DateTime _closedAt = DateTime.MinValue;

    public SubGroupPicker() => InitializeComponent();

    /// <summary>The subgroups to choose from; the picker ticks and unticks them.</summary>
    public IEnumerable<SubGroupOption>? Options { get => (IEnumerable<SubGroupOption>?)GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }

    /// <summary>What the closed field shows: the ticked subgroups, or "Everyone".</summary>
    public string Summary { get => (string)GetValue(SummaryProperty); set => SetValue(SummaryProperty, value); }

    /// <summary>True while no subgroup is ticked: the "Everyone" entry is then the ticked one.</summary>
    public bool IsEveryone { get => (bool)GetValue(IsEveryoneProperty); set => SetValue(IsEveryoneProperty, value); }

    /// <summary>Run when "Everyone" is chosen: unticks every subgroup.</summary>
    public ICommand? ClearCommand { get => (ICommand?)GetValue(ClearCommandProperty); set => SetValue(ClearCommandProperty, value); }

    private void OnTriggerClick(object sender, RoutedEventArgs e)
    {
        // A click on the field while the list is open first closes the list (a click outside it) and then lands here.
        if (DateTime.UtcNow - _closedAt < TimeSpan.FromMilliseconds(300)) return;

        Pop.IsOpen = true;
    }

    private void OnPopupOpened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // Arrow keys and Space work from the first subgroup on; without any, "Everyone" is the only entry.
            List.UpdateLayout();
            if (List.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first) first.Focus();
            else EveryoneRow.Focus();
        });

    private void OnPopupClosed(object? sender, EventArgs e) => _closedAt = DateTime.UtcNow;

    private void OnPanelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        e.Handled = true;
        Pop.IsOpen = false;
        Trigger.Focus();
    }

    /// <summary>Tabbing out of the list closes it; a popup only closes by itself on a click outside.</summary>
    private void OnPanelFocusChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) Pop.IsOpen = false;
    }
}
