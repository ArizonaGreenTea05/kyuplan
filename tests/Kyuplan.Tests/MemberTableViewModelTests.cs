using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

public sealed class MemberTableViewModelTests
{
    private static MemberTableViewModel Table(params MemberAttendanceDto[] members)
    {
        var table = new MemberTableViewModel();
        table.Load(members);
        return table;
    }

    private static MemberTableViewModel Four() => Table(
        TestData.MemberStats("Anna", attended: 9, rate: 90),
        TestData.MemberStats("Ben", attended: 5, rate: 50, status: QualificationStatus.BelowThreshold),
        TestData.MemberStats("Clara", invited: 1, attended: 1, rate: 100, status: QualificationStatus.InsufficientData),
        TestData.MemberStats("Dora", attended: 6, rate: 60, status: QualificationStatus.BelowThreshold));

    private static string[] Names(MemberTableViewModel table) => [.. table.VisibleMembers.Select(m => m.Name)];

    [Fact]
    public void Starts_empty_and_unfiltered()
    {
        var table = new MemberTableViewModel();

        Assert.Empty(table.VisibleMembers);
        Assert.True(table.IsUnfiltered);
        Assert.True(table.SortDescending);
        Assert.Equal(MemberSortKey.AttendanceRate, table.SortKey);
    }

    [Fact]
    public void Load_sorts_by_rate_descending_and_counts_all_members()
    {
        var table = Four();

        Assert.Equal(["Clara", "Anna", "Dora", "Ben"], Names(table));
        Assert.Equal("4 members", table.MembersCaption);
    }

    [Fact]
    public void Load_replaces_the_members_and_keeps_search_and_sort()
    {
        var table = Four();
        table.SearchText = "an";
        table.Load([TestData.MemberStats("Hanna"), TestData.MemberStats("Otto")]);

        Assert.Equal(["Hanna"], Names(table));
        Assert.Equal("1 of 2 members", table.MembersCaption);
    }

    [Fact]
    public void Load_of_nothing_shows_no_rows()
    {
        var table = Four();
        table.Load([]);

        Assert.Empty(table.VisibleMembers);
        Assert.Equal("0 members", table.MembersCaption);
    }

    [Theory]
    [InlineData("ann", new[] { "Anna" })]
    [InlineData("  ANN ", new[] { "Anna" })]
    [InlineData("a", new[] { "Clara", "Anna", "Dora" })]
    [InlineData("zz", new string[0])]
    [InlineData("   ", new[] { "Clara", "Anna", "Dora", "Ben" })]
    public void Search_ignores_case_and_surrounding_blanks(string term, string[] expected)
    {
        var table = Four();
        table.SearchText = term;

        Assert.Equal(expected, Names(table));
    }

    [Theory]
    [InlineData(MemberStatusFilter.All, new[] { "Clara", "Anna", "Dora", "Ben" })]
    [InlineData(MemberStatusFilter.Qualified, new[] { "Anna" })]
    [InlineData(MemberStatusFilter.BelowThreshold, new[] { "Dora", "Ben" })]
    [InlineData(MemberStatusFilter.InsufficientData, new[] { "Clara" })]
    public void Status_filter_keeps_only_that_status(MemberStatusFilter filter, string[] expected)
    {
        var table = Four();
        table.StatusFilter = filter;

        Assert.Equal(expected, Names(table));
    }

    [Fact]
    public void Search_and_status_combine()
    {
        var table = Four();
        table.StatusFilter = MemberStatusFilter.BelowThreshold;
        table.SearchText = "b";

        Assert.Equal(["Ben"], Names(table));
        Assert.Equal("1 of 4 members", table.MembersCaption);
    }

    [Fact]
    public void Sorting_by_the_same_column_flips_the_direction()
    {
        var table = Four();

        table.SortByCommand.Execute(MemberSortKey.Name);
        Assert.Equal(["Anna", "Ben", "Clara", "Dora"], Names(table));
        Assert.False(table.SortDescending); // text columns start ascending

        table.SortByCommand.Execute(MemberSortKey.Name);
        Assert.Equal(["Dora", "Clara", "Ben", "Anna"], Names(table));
    }

    [Theory]
    [InlineData(MemberSortKey.Name, false)]
    [InlineData(MemberSortKey.SubGroups, false)]
    [InlineData(MemberSortKey.DaysSinceLastAttended, false)]
    [InlineData(MemberSortKey.Attended, true)]
    [InlineData(MemberSortKey.Stars, true)]
    [InlineData(MemberSortKey.EngagementScore, true)]
    public void A_new_column_starts_in_its_natural_direction(MemberSortKey key, bool descending)
    {
        var table = Four();
        table.SortByCommand.Execute(key);

        Assert.Equal(key, table.SortKey);
        Assert.Equal(descending, table.SortDescending);
    }

    [Fact]
    public void Equal_values_are_ordered_by_name()
    {
        var table = Table(TestData.MemberStats("Zoe", rate: 50), TestData.MemberStats("Adam", rate: 50), TestData.MemberStats("mia", rate: 50));

        Assert.Equal(["Adam", "mia", "Zoe"], Names(table));
    }

    [Fact]
    public void Every_sort_key_gives_a_stable_complete_order()
    {
        var table = Four();

        foreach (var key in Enum.GetValues<MemberSortKey>())
        {
            table.SortByCommand.Execute(key);
            Assert.Equal(4, table.VisibleMembers.Count);
        }
    }

    [Fact]
    public void IsUnfiltered_follows_search_and_status_but_not_sorting()
    {
        var table = Four();
        var raised = new List<string?>();
        table.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        table.SortByCommand.Execute(MemberSortKey.Name);
        Assert.True(table.IsUnfiltered);
        Assert.DoesNotContain(nameof(MemberTableViewModel.IsUnfiltered), raised);

        table.SearchText = "x";
        Assert.False(table.IsUnfiltered);

        table.SearchText = "";
        table.StatusFilter = MemberStatusFilter.Qualified;
        Assert.False(table.IsUnfiltered);
        Assert.Contains(nameof(MemberTableViewModel.IsUnfiltered), raised);
    }

    [Fact]
    public void ClearFilter_resets_search_and_status_and_keeps_the_sort_order()
    {
        var table = Four();
        table.SortByCommand.Execute(MemberSortKey.Name);
        table.SearchText = "an";
        table.StatusFilter = MemberStatusFilter.Qualified;

        table.ClearFilter();

        Assert.True(table.IsUnfiltered);
        Assert.Equal(["Anna", "Ben", "Clara", "Dora"], Names(table));
    }
}
