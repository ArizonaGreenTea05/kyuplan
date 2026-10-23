using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

public enum MemberStatusFilter
{
    All,
    Qualified,
    BelowThreshold,
    InsufficientData,
}

/// <summary>Columns of the member table the user can sort by.</summary>
public enum MemberSortKey
{
    Name,
    SubGroups,
    Invited,
    Attended,
    AttendanceRate,
    ResponseRate,
    CurrentStreak,
    DaysSinceLastAttended,
    SessionsPerWeek,
    Stars,
    EngagementScore,
}

/// <summary>
/// The member table of the statistics page: the loaded members, seen through a search term, a status filter and a sort order.
/// All of it works on the loaded result and applies instantly; nothing here talks to the server.
/// </summary>
public sealed partial class MemberTableViewModel : ObservableObject
{
    private IReadOnlyList<MemberRowViewModel> _all = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private MemberStatusFilter _statusFilter = MemberStatusFilter.All;

    [ObservableProperty]
    private MemberSortKey _sortKey = MemberSortKey.AttendanceRate;

    [ObservableProperty]
    private bool _sortDescending = true;

    [ObservableProperty]
    private IReadOnlyList<MemberRowViewModel> _visibleMembers = [];

    [ObservableProperty]
    private string _membersCaption = "";

    /// <summary>Whether search and status filter are in their start state (sorting is not a filter and is left alone).</summary>
    public bool IsUnfiltered => SearchText.Length == 0 && StatusFilter == MemberStatusFilter.All;

    /// <summary>Replaces the members; the current search, filter and sort order apply to the new ones.</summary>
    public void Load(IEnumerable<MemberAttendanceDto> members)
    {
        _all = members.Select(m => new MemberRowViewModel(m)).ToList();
        Rebuild();
    }

    /// <summary>Clears the search and the status filter.</summary>
    public void ClearFilter()
    {
        SearchText = "";
        StatusFilter = MemberStatusFilter.All;
    }

    /// <summary>Sorts by <paramref name="key"/>; choosing the active column again flips the direction.</summary>
    [RelayCommand]
    private void SortBy(MemberSortKey key)
    {
        if (SortKey == key)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortKey = key;
            SortDescending = key is not MemberSortKey.Name and not MemberSortKey.SubGroups and not MemberSortKey.DaysSinceLastAttended;
        }
    }

    partial void OnSearchTextChanged(string value) => FilterChanged();

    partial void OnStatusFilterChanged(MemberStatusFilter value) => FilterChanged();

    partial void OnSortKeyChanged(MemberSortKey value) => Rebuild();

    partial void OnSortDescendingChanged(bool value) => Rebuild();

    // Sorting is not a filter: only search and status tell the page that the filter card differs from its start state.
    private void FilterChanged()
    {
        OnPropertyChanged(nameof(IsUnfiltered));
        Rebuild();
    }

    private void Rebuild()
    {
        IEnumerable<MemberRowViewModel> rows = _all;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            rows = rows.Where(m => m.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        }

        rows = StatusFilter switch
        {
            MemberStatusFilter.Qualified => rows.Where(m => m.Status == QualificationStatus.Qualified),
            MemberStatusFilter.BelowThreshold => rows.Where(m => m.Status == QualificationStatus.BelowThreshold),
            MemberStatusFilter.InsufficientData => rows.Where(m => m.Status == QualificationStatus.InsufficientData),
            _ => rows,
        };

        VisibleMembers = Sort(rows).ToList();
        MembersCaption = VisibleMembers.Count == _all.Count
            ? string.Format(Strings.Stats_MembersCount, _all.Count)
            : string.Format(Strings.Stats_MembersCountOf, VisibleMembers.Count, _all.Count);
    }

    private IEnumerable<MemberRowViewModel> Sort(IEnumerable<MemberRowViewModel> rows)
    {
        Func<MemberRowViewModel, IComparable> key = SortKey switch
        {
            MemberSortKey.Name => m => m.Name.ToUpperInvariant(),
            MemberSortKey.SubGroups => m => m.SubGroupsText.ToUpperInvariant(),
            MemberSortKey.Invited => m => m.Invited,
            MemberSortKey.Attended => m => m.Attended,
            MemberSortKey.ResponseRate => m => m.ResponseRate,
            MemberSortKey.CurrentStreak => m => m.CurrentStreak,
            MemberSortKey.DaysSinceLastAttended => m => m.DaysSinceLastAttended,
            MemberSortKey.SessionsPerWeek => m => m.SessionsPerWeek,
            MemberSortKey.Stars => m => m.Stars,
            MemberSortKey.EngagementScore => m => m.EngagementScore,
            _ => m => m.AttendanceRate,
        };

        // Ties keep a stable, readable order: by name.
        var ordered = SortDescending ? rows.OrderByDescending(key) : rows.OrderBy(key);
        return ordered.ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase);
    }
}
