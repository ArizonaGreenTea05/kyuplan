using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>The members page: the members of the selected group, and setting the belt and the start day of one.</summary>
public sealed class MembersViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;
    private readonly MembersViewModel _vm;

    private BeltDto _white = null!;
    private BeltDto _yellow = null!;

    public MembersViewModelTests()
    {
        _language.Select("en");
        _white = _api.AddBelt("White");
        _yellow = _api.AddBelt("Yellow", "#FFD800", "#000000", requires: [new(_white.Id, 0, 0)]);

        // Group g1 (the first one, which the shell selects) has Anna, Ben and Clara; g2 has Dora.
        _api.AddMember("Clara");
        _api.AddMember("Anna", beltId: _yellow.Id, startedAt: new DateOnly(2024, 10, 2));
        _api.AddMember("Ben", beltId: _white.Id);
        _api.AddMember("Dora", groupId: "g2");

        _shell = new ShellViewModel(_api, _language);
        _vm = new MembersViewModel(_shell, _api, new FakeTimeProvider(new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.FromHours(2))));
    }

    public void Dispose()
    {
        // Selecting a language applies its culture process-wide; do not leak it into other tests.
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private async Task<MembersViewModel> ShownAsync()
    {
        await _vm.ActivateAsync();
        return _vm;
    }

    private static MemberRow Row(MembersViewModel vm, string name) => vm.Members.Single(m => m.Name == name);

    // --- The list ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_lists_the_members_of_the_selected_group_by_name_with_belt_and_start_day()
    {
        var vm = await ShownAsync();

        Assert.Equal("Members", vm.Title);
        Assert.Equal(["g1"], _api.MemberListRequests.Distinct());
        Assert.Equal(["Anna", "Ben", "Clara"], vm.Members.Select(m => m.Name));
        Assert.Equal(["Yellow", "White", "No belt"], vm.Members.Select(m => m.Belt.Label));
        Assert.Equal([true, true, false], vm.Members.Select(m => m.HasBelt));
        Assert.Equal(["2 Oct 2024", "not known", "not known"], vm.Members.Select(m => m.StartedText));
        Assert.Equal("3 members", vm.CountText);
        Assert.Equal(["No belt", "White", "Yellow"], vm.BeltOptions.Select(o => o.Label));
        Assert.True(vm.BeltOptions[0].IsNone);
        Assert.False(vm.IsNobodyHere);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Another_group_is_another_list_and_closes_a_form_for_a_member_of_the_old_one()
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Anna"));
        Assert.True(vm.IsFormOpen);

        _shell.SelectedGroupId = "g2";
        await Eventually(() => vm.Members.Select(m => m.Name).SequenceEqual(["Dora"]));

        Assert.False(vm.IsFormOpen);
        Assert.Equal(["g1", "g2"], _api.MemberListRequests.Distinct());
    }

    [Fact]
    public async Task A_group_without_members_says_so_but_only_once_it_has_loaded()
    {
        Assert.False(_vm.IsNobodyHere);
        _api.Members.Clear();

        var vm = await ShownAsync();

        Assert.True(vm.IsNobodyHere);
        Assert.False(vm.NoneMatch);
    }

    [Fact]
    public async Task A_failed_list_can_be_loaded_again()
    {
        _api.MemberListFailure = new PlannerApiException("The list is not available.");
        var vm = await ShownAsync();
        Assert.Equal("The list is not available.", vm.ErrorMessage);
        Assert.False(vm.IsNobodyHere); // a list that could not be loaded is not an empty one

        _api.MemberListFailure = null;
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.Equal(3, vm.Members.Count);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "condition was not met in time");
    }

    // --- Finding members ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_search_finds_members_by_the_words_of_their_name_ignoring_case_and_accents()
    {
        _api.AddMember("Jürgen Müller");
        var vm = await ShownAsync();

        vm.SearchText = "ann";
        Assert.Equal(["Anna"], vm.VisibleMembers.Select(m => m.Name));
        Assert.Equal("1 of 4 members", vm.CountText);

        vm.SearchText = "MULLER jurgen";
        Assert.Equal(["Jürgen Müller"], vm.VisibleMembers.Select(m => m.Name));

        vm.SearchText = "nobody";
        Assert.Empty(vm.VisibleMembers);
        Assert.True(vm.NoneMatch);
        Assert.False(vm.IsNobodyHere);

        vm.SearchText = "  ";
        Assert.Equal(4, vm.VisibleMembers.Count);
        Assert.Equal("4 members", vm.CountText);
    }

    [Fact]
    public async Task The_filter_shows_only_the_members_who_hold_no_belt_yet()
    {
        var vm = await ShownAsync();

        vm.OnlyWithoutBelt = true;

        Assert.Equal(["Clara"], vm.VisibleMembers.Select(m => m.Name));
        Assert.Equal("1 of 3 members", vm.CountText);

        vm.SearchText = "ben";
        Assert.True(vm.NoneMatch); // Ben holds a belt
    }

    // --- Editing -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Editing_a_member_starts_from_what_they_have_and_can_only_be_saved_once_something_differs()
    {
        var vm = await ShownAsync();
        var anna = Row(vm, "Anna");
        Assert.False(vm.IsFormOpen);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.BeginEditCommand.Execute(anna);

        Assert.True(vm.IsFormOpen);
        Assert.Equal("Edit “Anna”", vm.FormTitle);
        Assert.Same(anna.Belt, vm.FormBelt); // the entry of the list, so that a combo box shows it as selected
        Assert.Equal(new DateTime(2024, 10, 2), vm.FormStartedAt);
        Assert.False(vm.CanSave);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.FormBelt = vm.BeltOptions.Single(o => o.Id == _white.Id);
        Assert.True(vm.CanSave);
        vm.FormBelt = anna.Belt;
        Assert.False(vm.CanSave);

        vm.FormStartedAt = new DateTime(2024, 10, 3);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Saving_sends_the_belt_and_the_day_replaces_the_row_and_closes_the_form()
    {
        var vm = await ShownAsync();
        var clara = Row(vm, "Clara");
        vm.BeginEditCommand.Execute(clara);
        vm.FormBelt = vm.BeltOptions.Single(o => o.Id == _yellow.Id);
        vm.FormStartedAt = new DateTime(2025, 1, 13);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal([$"update-member:{clara.Id}"], _api.BeltCalls);
        Assert.Equal(new MemberInputDto(_yellow.Id, new DateOnly(2025, 1, 13)), Assert.Single(_api.MemberInputs));
        Assert.False(vm.IsFormOpen);
        var saved = Row(vm, "Clara");
        Assert.Equal(("Yellow", "13 Jan 2025"), (saved.Belt.Label, saved.StartedText));
        Assert.Same(vm.BeltOptions.Single(o => o.Id == _yellow.Id), saved.Belt);
        Assert.Equal(["Anna", "Ben", "Clara"], vm.Members.Select(m => m.Name)); // the order stays
        Assert.Equal("Clara saved.", vm.NoticeText);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task The_belt_and_the_day_can_be_taken_away_again()
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Anna"));
        Assert.True(vm.ClearStartedAtCommand.CanExecute(null));

        vm.ClearStartedAtCommand.Execute(null);
        vm.FormBelt = vm.BeltOptions[0];
        Assert.False(vm.ClearStartedAtCommand.CanExecute(null)); // nothing left to clear
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new MemberInputDto(null, null), Assert.Single(_api.MemberInputs));
        var anna = Row(vm, "Anna");
        Assert.False(anna.HasBelt);
        Assert.Equal("not known", anna.StartedText);
    }

    [Theory]
    [InlineData(1949, 12, 31, true)]   // before anybody started
    [InlineData(1950, 1, 1, false)]
    [InlineData(2026, 6, 30, false)]   // today
    [InlineData(2026, 7, 1, true)]     // tomorrow
    public async Task A_start_day_the_api_would_refuse_cannot_be_saved(int year, int month, int day, bool refused)
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Clara"));

        vm.FormStartedAt = new DateTime(year, month, day);

        Assert.Equal(!refused, vm.CanSave);
        Assert.Equal(refused, vm.StartedAtProblem is not null);
        if (refused) Assert.Equal("The day must be between 1 Jan 1950 and today.", vm.StartedAtProblem);
    }

    [Fact]
    public async Task A_failed_save_keeps_the_form_open_with_the_reason()
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Clara"));
        vm.FormBelt = vm.BeltOptions.Single(o => o.Id == _yellow.Id);
        _api.Belts.Remove(_yellow); // somebody deleted the belt meanwhile

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("There is no such belt.", vm.ErrorMessage);
        Assert.True(vm.IsFormOpen);
        Assert.False(vm.HasNotice);
        Assert.False(Row(vm, "Clara").HasBelt);

        vm.FormBelt = vm.BeltOptions[0];
        Assert.False(vm.HasError); // correcting the form takes the message back
    }

    // --- The birth date and the day the belt was received ---------------------------------------------------------

    [Fact]
    public async Task The_list_shows_the_birth_date_and_the_day_the_belt_was_received()
    {
        _api.AddMember("Emil", beltId: _white.Id, birthDate: new DateOnly(2015, 3, 4), beltSince: new DateOnly(2025, 11, 2));
        var vm = await ShownAsync();

        var emil = Row(vm, "Emil");
        Assert.Equal(("4 Mar 2015", "2 Nov 2025"), (emil.BirthText, emil.BeltSinceText));
        Assert.Equal("not known", Row(vm, "Ben").BeltSinceText); // a belt, and nobody knows since when
        Assert.Equal(("not known", "–"), (Row(vm, "Clara").BirthText, Row(vm, "Clara").BeltSinceText)); // without a belt there is no day to know
    }

    [Fact]
    public async Task The_birth_date_and_the_day_of_the_belt_are_part_of_the_form_and_sent_with_the_rest()
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Ben"));
        Assert.True(vm.CanSetBeltSince);
        Assert.False(vm.ClearBirthDateCommand.CanExecute(null)); // nothing to clear yet

        vm.FormBirthDate = new DateTime(2015, 3, 4);
        vm.FormBeltSince = new DateTime(2026, 1, 15);
        Assert.True(vm.CanSave);
        Assert.True(vm.ClearBirthDateCommand.CanExecute(null));
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(
            new MemberInputDto(_white.Id, null, new DateOnly(2015, 3, 4), new DateOnly(2026, 1, 15)),
            Assert.Single(_api.MemberInputs));
        var ben = Row(vm, "Ben");
        Assert.Equal(("4 Mar 2015", "15 Jan 2026"), (ben.BirthText, ben.BeltSinceText));
    }

    [Fact]
    public async Task Each_day_can_be_the_only_change_and_can_be_taken_away_again()
    {
        _api.AddMember("Emil", beltId: _white.Id, birthDate: new DateOnly(2015, 3, 4), beltSince: new DateOnly(2025, 11, 2));
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Emil"));
        Assert.False(vm.CanSave);

        vm.ClearBirthDateCommand.Execute(null);
        Assert.True(vm.CanSave);
        vm.FormBirthDate = new DateTime(2015, 3, 4);
        Assert.False(vm.CanSave); // back to what the member has

        vm.ClearBeltSinceCommand.Execute(null);
        Assert.True(vm.CanSave);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new MemberInputDto(_white.Id, null, new DateOnly(2015, 3, 4), null), Assert.Single(_api.MemberInputs));
    }

    [Fact]
    public async Task Without_a_belt_there_is_no_day_it_was_received_to_set_and_taking_the_belt_away_clears_it()
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Clara"));
        Assert.False(vm.CanSetBeltSince);
        Assert.False(vm.ClearBeltSinceCommand.CanExecute(null));

        vm.FormBelt = vm.BeltOptions.Single(o => o.Id == _yellow.Id);
        Assert.True(vm.CanSetBeltSince);
        vm.FormBeltSince = new DateTime(2026, 2, 1);
        Assert.True(vm.ClearBeltSinceCommand.CanExecute(null));

        vm.FormBelt = vm.BeltOptions[0]; // the belt is taken away again
        Assert.Null(vm.FormBeltSince);
        Assert.False(vm.CanSetBeltSince);
        Assert.False(vm.CanSave); // nothing differs from Clara as she is
    }

    [Theory]
    [InlineData(1899, 12, 31, true)]   // nobody is that old
    [InlineData(1900, 1, 1, false)]
    [InlineData(2026, 6, 30, false)]   // born today
    [InlineData(2026, 7, 1, true)]     // tomorrow
    public async Task A_birth_date_the_api_would_refuse_cannot_be_saved(int year, int month, int day, bool refused)
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Clara"));

        vm.FormBirthDate = new DateTime(year, month, day);

        Assert.Equal(!refused, vm.CanSave);
        Assert.Equal(refused, vm.BirthDateProblem is not null);
        if (refused) Assert.Equal("The date must be between 1 Jan 1900 and today.", vm.BirthDateProblem);
    }

    [Theory]
    [InlineData(1949, 12, 31, true)]
    [InlineData(1950, 1, 1, false)]
    [InlineData(2026, 6, 30, false)]
    [InlineData(2026, 7, 1, true)]
    public async Task A_day_for_the_belt_the_api_would_refuse_cannot_be_saved(int year, int month, int day, bool refused)
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Ben"));

        vm.FormBeltSince = new DateTime(year, month, day);

        Assert.Equal(!refused, vm.CanSave);
        Assert.Equal(refused, vm.BeltSinceProblem is not null);
        if (refused) Assert.Equal("The day must be between 1 Jan 1950 and today.", vm.BeltSinceProblem);
    }

    [Fact]
    public async Task Cancelling_the_form_changes_nothing()
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Clara"));
        vm.FormBelt = vm.BeltOptions[1];

        vm.CancelFormCommand.Execute(null);

        Assert.False(vm.IsFormOpen);
        Assert.Empty(_api.BeltCalls);
        Assert.False(Row(vm, "Clara").HasBelt);
    }

    [Fact]
    public async Task The_edit_button_is_off_for_the_member_whose_form_is_open()
    {
        var vm = await ShownAsync();
        var anna = Row(vm, "Anna");
        Assert.True(vm.BeginEditCommand.CanExecute(anna));

        vm.BeginEditCommand.Execute(anna);

        Assert.False(vm.BeginEditCommand.CanExecute(anna));
        Assert.True(vm.BeginEditCommand.CanExecute(Row(vm, "Ben")));
    }

    // --- Fresh every time it is shown ---------------------------------------------------------------------------

    [Fact]
    public async Task The_first_time_the_page_is_shown_the_lists_are_read_once()
    {
        await ShownAsync();

        Assert.Single(_api.MemberListRequests);
        Assert.Equal(1, _api.BeltListRequests);
    }

    [Fact]
    public async Task Showing_the_page_again_offers_the_belts_created_and_leaves_out_the_ones_deleted_on_the_belts_page()
    {
        var vm = await ShownAsync();
        Assert.Equal(["No belt", "White", "Yellow"], vm.BeltOptions.Select(o => o.Label));

        vm.Deactivate();
        var orange = _api.AddBelt("Orange", "#FF8C00", requires: [new(_yellow.Id, 0, 0)]);
        _api.Members.RemoveAll(m => m.BeltId == _white.Id); // Ben leaves, so that White can go
        _api.Belts.Remove(_white);
        await vm.ActivateAsync();

        Assert.Equal(["No belt", "Yellow", "Orange"], vm.BeltOptions.Select(o => o.Label));
        Assert.Equal(["Anna", "Clara"], vm.Members.Select(m => m.Name));
        vm.BeginEditCommand.Execute(Row(vm, "Clara"));
        vm.FormBelt = vm.BeltOptions.Single(o => o.Id == orange.Id);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public async Task A_form_that_is_open_is_left_alone_when_the_page_is_shown_again()
    {
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Clara"));
        vm.FormBelt = vm.BeltOptions.Single(o => o.Id == _yellow.Id);
        var chosen = vm.FormBelt;

        vm.Deactivate();
        await vm.ActivateAsync();

        // Replaced lists would take the selection of a combo box with them, and saving would then silently take the belt away.
        Assert.Single(_api.MemberListRequests);
        Assert.Same(chosen, vm.FormBelt);
        Assert.Contains(chosen, vm.BeltOptions);
        Assert.True(vm.IsFormOpen);
    }

    // --- Language ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_texts_follow_the_language_without_asking_the_api_again()
    {
        var vm = await ShownAsync();
        var clara = Row(vm, "Clara");
        var none = vm.BeltOptions[0];
        Assert.Equal("not known", clara.StartedText);

        _language.Select("de");

        Assert.Equal("Mitglieder", vm.Title);
        Assert.Equal("unbekannt", clara.StartedText);
        Assert.Equal("Kein Gürtel", none.Label);
        Assert.Equal("Kein Gürtel", clara.Belt.Label);
        Assert.StartsWith("2. Okt", Row(vm, "Anna").StartedText);
        Assert.Equal("3 Mitglieder", vm.CountText);
        Assert.Same(clara, Row(vm, "Clara")); // the rows and the entries stay, they re-read their texts
        Assert.Same(none, vm.BeltOptions[0]);
        Assert.Single(_api.MemberListRequests);
    }
}
