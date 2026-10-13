using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.WpfApp.Controls;

public partial class EventPicker : UserControl
{
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
        nameof(Options), typeof(IEnumerable<EventOption>), typeof(EventPicker),
        new PropertyMetadata(null, (d, _) => ((EventPicker)d).UpdateSelected()));

    /// <summary>Id of the chosen event; empty = none.</summary>
    public static readonly DependencyProperty SelectedIdProperty = DependencyProperty.Register(
        nameof(SelectedId), typeof(string), typeof(EventPicker),
        new FrameworkPropertyMetadata(EventOption.NoneId, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((EventPicker)d).UpdateSelected()));

    private static readonly DependencyPropertyKey SelectedPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Selected), typeof(EventOption), typeof(EventPicker), new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedProperty = SelectedPropertyKey.DependencyProperty;

    public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(
        nameof(SearchText), typeof(string), typeof(EventPicker),
        new PropertyMetadata("", (d, _) => ((EventPicker)d).ApplyFilter()));

    private DateTime _closedAt = DateTime.MinValue;

    public EventPicker() => InitializeComponent();

    /// <summary>The events to choose from. The empty entry the view model leads its list with is not offered.</summary>
    public IEnumerable<EventOption>? Options { get => (IEnumerable<EventOption>?)GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }

    public string? SelectedId { get => (string?)GetValue(SelectedIdProperty); set => SetValue(SelectedIdProperty, value); }

    /// <summary>The chosen event, or <c>null</c> while there is none.</summary>
    public EventOption? Selected => (EventOption?)GetValue(SelectedProperty);

    /// <summary>What is typed into the search box.</summary>
    public string SearchText { get => (string)GetValue(SearchTextProperty); set => SetValue(SearchTextProperty, value); }

    private void UpdateSelected() =>
        SetValue(SelectedPropertyKey, string.IsNullOrEmpty(SelectedId) ? null : Options?.FirstOrDefault(o => o.Id == SelectedId));

    /// <summary>The list after the search; the first event is highlighted, so typing and then Enter picks the best hit.</summary>
    private void ApplyFilter()
    {
        var matches = (Options ?? []).Where(o => o.Id != EventOption.NoneId && o.Matches(SearchText)).ToList();
        List.ItemsSource = matches;
        List.SelectedIndex = matches.Count > 0 ? 0 : -1;
        FindScrollViewer(List)?.ScrollToHome(); // a new list does not start where the old one was scrolled to
        NoMatches.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = matches.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnTriggerClick(object sender, RoutedEventArgs e)
    {
        // A click on the field while the list is open first closes the list (a click outside it) and then lands here.
        if (DateTime.UtcNow - _closedAt < TimeSpan.FromMilliseconds(300)) return;

        SearchText = "";
        ApplyFilter();
        Pop.IsOpen = true;
    }

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        // The list opens at the chosen event, so the surroundings of the current span are in view.
        if (Selected is { } chosen && List.Items.Contains(chosen)) List.SelectedItem = chosen;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            List.UpdateLayout();
            if (List.SelectedItem is { } item) List.ScrollIntoView(item);
            if (FindScrollViewer(List) is { } scroll && List.SelectedIndex >= 0)
                scroll.ScrollToVerticalOffset(Math.Max(0, List.SelectedIndex - scroll.ViewportHeight / 2)); // items, not pixels
            Search.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
    }

    private void OnPopupClosed(object? sender, EventArgs e) => _closedAt = DateTime.UtcNow;

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Move(1); break;
            case Key.Up: Move(-1); break;
            case Key.PageDown: Move(8); break;
            case Key.PageUp: Move(-8); break;
            case Key.Enter when List.SelectedItem is EventOption option: Choose(option); break;
            case Key.Escape: Close(); break;
            default: return;
        }

        e.Handled = true;
    }

    private void Move(int by)
    {
        if (List.Items.Count == 0) return;

        List.SelectedIndex = Math.Clamp(List.SelectedIndex + by, 0, List.Items.Count - 1);
        List.ScrollIntoView(List.SelectedItem);
    }

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(List, source) is ListBoxItem { Content: EventOption option })
        {
            Choose(option);
        }
    }

    private void Choose(EventOption option)
    {
        SelectedId = option.Id;
        Close();
    }

    private void Close()
    {
        Pop.IsOpen = false;
        Trigger.Focus();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if ((child as ScrollViewer ?? FindScrollViewer(child)) is { } found) return found;
        }

        return null;
    }
}
