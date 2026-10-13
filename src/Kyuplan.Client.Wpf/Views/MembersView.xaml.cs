using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.WpfApp.Views;

public partial class MembersView : UserControl
{
    private SortDescription? _sort;

    public MembersView() => InitializeComponent();

    private MembersViewModel? ViewModel => DataContext as MembersViewModel;

    // The page is created anew every time it is shown, but its view model lives on: follow it only while visible.
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        viewModel.Shell.Language.Changed += OnLanguageChanged;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        viewModel.Shell.Language.Changed -= OnLanguageChanged;
        viewModel.PropertyChanged -= OnViewModelChanged;
    }

    /// <summary>A date picker writes its date once; to show it in the new language's format it has to be told to format it again.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var picker in new[] { StartedPicker, BeltSincePicker, BirthPicker })
        {
            picker.SelectedDateFormat = DatePickerFormat.Long;
            picker.SelectedDateFormat = DatePickerFormat.Short;
        }
    }

    /// <summary>
    /// The grid sorts its own view of the list and forgets the sort whenever the list is replaced, which a keystroke in the search box
    /// or a ticked filter does. So the sort is kept here and put back on the new list (after the binding has handed it over).
    /// </summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MembersViewModel.VisibleMembers) && _sort is not null)
            Dispatcher.BeginInvoke(ApplySort, DispatcherPriority.DataBind);
    }

    private void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (e.Column.SortMemberPath.Length == 0) return;

        _sort = new SortDescription(
            e.Column.SortMemberPath, e.Column.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending);
        ApplySort();
    }

    private void ApplySort()
    {
        foreach (var column in MembersGrid.Columns)
            column.SortDirection = _sort is { } sort && column.SortMemberPath == sort.PropertyName ? sort.Direction : null;

        MembersGrid.Items.SortDescriptions.Clear();
        if (_sort is { } current) MembersGrid.Items.SortDescriptions.Add(current);
    }
}
