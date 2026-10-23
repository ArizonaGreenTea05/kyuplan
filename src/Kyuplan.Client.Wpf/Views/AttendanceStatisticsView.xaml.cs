using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.WpfApp.Views;

public partial class AttendanceStatisticsView : UserControl
{
    public AttendanceStatisticsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private AttendanceStatisticsViewModel? ViewModel => DataContext as AttendanceStatisticsViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AttendanceStatisticsViewModel oldVm) oldVm.Shell.Language.Changed -= OnLanguageChanged;
        if (e.NewValue is AttendanceStatisticsViewModel newVm) newVm.Shell.Language.Changed += OnLanguageChanged;

        if (e.OldValue is AttendanceStatisticsViewModel { Table: var oldTable }) oldTable.PropertyChanged -= OnViewModelChanged;
        if (e.NewValue is AttendanceStatisticsViewModel { Table: var currentTable }) currentTable.PropertyChanged += OnViewModelChanged;
        UpdateSortIndicator();
    }

    /// <summary>A date picker writes its date once; to show it in the new language's format it has to be told to format it again.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var picker in new[] { FromPicker, ToPicker })
        {
            picker.SelectedDateFormat = DatePickerFormat.Long;
            picker.SelectedDateFormat = DatePickerFormat.Short;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MemberTableViewModel.SortKey) or nameof(MemberTableViewModel.SortDescending)
            or nameof(MemberTableViewModel.VisibleMembers))
        {
            UpdateSortIndicator();
        }
    }

    /// <summary>
    /// Sorting lives in the shared view model (so Blazor and WPF behave the same): a header click only tells it which
    /// column to sort by, and the grid merely shows the resulting direction.
    /// </summary>
    private void OnMembersSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (ViewModel is { } vm && Enum.TryParse<MemberSortKey>(e.Column.SortMemberPath, out var key))
            vm.Table.SortByCommand.Execute(key);
    }

    private void UpdateSortIndicator()
    {
        if (ViewModel is not { } vm) return;

        foreach (var column in MembersGrid.Columns)
        {
            column.SortDirection = column.SortMemberPath == vm.Table.SortKey.ToString()
                ? (vm.Table.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }
}
