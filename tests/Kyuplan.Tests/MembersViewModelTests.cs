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
    public async Task The_belt_choices_carry_the_colours_the_pickers_draw_and_the_empty_choice_none()
    {
        var vm = await ShownAsync();

        // A belt without a stripe has an empty second colour, so the pickers draw no stripe; "No belt" has no colours at all, so they draw nothing for it.
        Assert.Equal(["", "#FFFFFF", "#FFD800"], vm.BeltOptions.Select(o => o.Color));
        Assert.Equal(["", "", "#000000"], vm.BeltOptions.Select(o => o.SecondaryColor));
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

    [Fact]
    public async Task A_null_belt_in_the_form_counts_as_no_belt()
    {
        _api.AddMember("Emil", beltId: _white.Id, beltSince: new DateOnly(2025, 11, 2));
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Emil"));

        vm.FormBelt = null!; // a binding can still hand over null

        Assert.False(vm.CanSetBeltSince);
        Assert.True(vm.CanSave);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Null(Assert.Single(_api.MemberInputs).BeltId);
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

    // --- Hidden members ----------------------------------------------------------------------------------------

    private void SignInWith(params string[] permissions) => _api.Account = TestAccounts.WithPermissions(1, "anna", permissions);

    /// <summary>Somebody who may edit members and see the hidden ones (the permissions the page needs to be shown at all included).</summary>
    private void SignInAsKeeper() => SignInWith(Permission.MembersView, Permission.MembersEdit, Permission.MembersHidden, Permission.BeltsView);

    private MemberDto AddHiddenMember(string name, string groupId = "g1")
    {
        var member = _api.AddMember(name, groupId);
        _api.Members.Remove(member);
        _api.HiddenMembers.Add(member);
        return member;
    }

    [Fact]
    public async Task Hiding_a_member_asks_first_and_then_takes_them_off_the_list_and_says_so()
    {
        SignInAsKeeper();
        var vm = await ShownAsync();
        var ben = Row(vm, "Ben");

        vm.AskHideCommand.Execute(ben);

        Assert.True(vm.IsConfirmingHide);
        Assert.Equal("Hide “Ben”? A hidden member is ignored in the statistics and shown nowhere; only someone who may view hidden members can make them visible again.", vm.ConfirmHideText);
        Assert.Equal(3, vm.Members.Count); // nothing is hidden before the question is answered
        Assert.Empty(_api.HiddenMembers);

        await vm.ConfirmHideCommand.ExecuteAsync(null);

        Assert.False(vm.IsConfirmingHide);
        Assert.Equal(["Anna", "Clara"], vm.Members.Select(m => m.Name));
        Assert.Equal("2 members", vm.CountText);
        Assert.Equal(["Ben"], _api.HiddenMembers.Select(m => m.Name));
        Assert.Equal("Ben is hidden.", vm.NoticeText);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Hiding_can_be_called_off_and_changes_nothing()
    {
        SignInAsKeeper();
        var vm = await ShownAsync();
        vm.AskHideCommand.Execute(Row(vm, "Ben"));

        vm.CancelHideCommand.Execute(null);

        Assert.False(vm.IsConfirmingHide);
        Assert.Equal("", vm.ConfirmHideText);
        Assert.Equal(3, vm.Members.Count);
        Assert.Empty(_api.HiddenMembers);
        Assert.DoesNotContain("hide-member", string.Join(' ', _api.BeltCalls));
        await vm.ConfirmHideCommand.ExecuteAsync(null); // nothing is asked: nothing happens
        Assert.Equal(3, vm.Members.Count);
    }

    [Fact]
    public async Task Somebody_who_may_not_edit_members_cannot_hide_one()
    {
        SignInWith(Permission.MembersView, Permission.MembersHidden, Permission.BeltsView);
        var vm = await ShownAsync();
        var ben = Row(vm, "Ben");

        Assert.False(vm.AskHideCommand.CanExecute(ben));
        vm.AskHideCommand.Execute(ben); // Execute does not ask CanExecute: what the user may not do is not done

        Assert.False(vm.IsConfirmingHide);
        Assert.Empty(_api.HiddenMembers);
    }

    [Fact]
    public async Task While_the_question_is_open_nothing_else_is_started_and_the_page_does_not_read_again_under_it()
    {
        SignInAsKeeper();
        var vm = await ShownAsync();
        var ben = Row(vm, "Ben");
        vm.AskHideCommand.Execute(ben);

        Assert.False(vm.BeginEditCommand.CanExecute(Row(vm, "Anna")));
        Assert.False(vm.ShowHiddenCommand.CanExecute(null));
        await vm.ActivateAsync(); // shown again meanwhile
        Assert.Single(_api.MemberListRequests);
        Assert.True(vm.IsConfirmingHide);

        vm.CancelHideCommand.Execute(null);

        Assert.True(vm.BeginEditCommand.CanExecute(Row(vm, "Anna")));
        Assert.True(vm.ShowHiddenCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_member_cannot_be_hidden_while_a_form_is_open_or_the_list_is_busy()
    {
        SignInAsKeeper();
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Anna"));

        Assert.False(vm.AskHideCommand.CanExecute(Row(vm, "Ben")));
        vm.AskHideCommand.Execute(Row(vm, "Ben"));
        Assert.False(vm.IsConfirmingHide);
        Assert.True(vm.IsFormOpen);
    }

    [Fact]
    public async Task A_refused_hide_leaves_the_list_as_it_is_and_says_why()
    {
        SignInAsKeeper();
        var vm = await ShownAsync();
        var gone = new MemberRow(new MemberDto(999, "g1", "Gone", null, null), new BeltOption());
        vm.AskHideCommand.Execute(gone);

        await vm.ConfirmHideCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.False(vm.IsConfirmingHide);
        Assert.Equal(3, vm.Members.Count);
        Assert.False(vm.HasNotice);
    }

    [Fact]
    public async Task The_form_of_a_member_who_was_hidden_meanwhile_is_closed_with_them()
    {
        SignInAsKeeper();
        var vm = await ShownAsync();
        var ben = Row(vm, "Ben");
        vm.AskHideCommand.Execute(ben);
        vm.BeginEditCommand.Execute(ben); // not allowed while the question is open; nothing opens
        Assert.False(vm.IsFormOpen);

        await vm.ConfirmHideCommand.ExecuteAsync(null);

        Assert.False(vm.IsFormOpen);
        Assert.DoesNotContain(vm.Members, m => m.Name == "Ben");
    }

    [Fact]
    public async Task The_hidden_members_are_listed_in_place_of_the_members_and_the_way_back_lists_the_members_again()
    {
        SignInAsKeeper();
        AddHiddenMember("Eva");
        AddHiddenMember("Dora", "g2");
        var vm = await ShownAsync();
        Assert.False(vm.ShowingHidden);
        Assert.Equal("Members", vm.Title);
        Assert.Equal(["Anna", "Ben", "Clara"], vm.Members.Select(m => m.Name));
        Assert.StartsWith("Which belt each member holds", vm.LedeText);

        await vm.ShowHiddenCommand.ExecuteAsync(null);

        Assert.True(vm.ShowingHidden);
        Assert.Equal(["g1"], _api.HiddenMemberListRequests);
        Assert.Equal(["Eva"], vm.Members.Select(m => m.Name));
        Assert.Equal("1 members", vm.CountText);
        Assert.StartsWith("These members are hidden", vm.LedeText);
        Assert.True(vm.ShowsUnhideColumn);
        Assert.False(vm.ShowsEditColumn);
        Assert.False(vm.ShowsCommentsColumn);

        await vm.ShowMembersCommand.ExecuteAsync(null);

        Assert.False(vm.ShowingHidden);
        Assert.Equal(["Anna", "Ben", "Clara"], vm.Members.Select(m => m.Name));
        Assert.False(vm.ShowsUnhideColumn);
        Assert.True(vm.ShowsEditColumn);
    }

    [Fact]
    public async Task In_the_list_of_hidden_members_nobody_can_be_edited_hidden_or_have_comments_opened_only_made_visible()
    {
        SignInWith(Permission.MembersView, Permission.MembersEdit, Permission.MembersHidden, Permission.BeltsView, Permission.CommentsView);
        AddHiddenMember("Eva");
        var vm = await ShownAsync();
        await vm.ShowHiddenCommand.ExecuteAsync(null);
        var eva = Row(vm, "Eva");

        Assert.False(vm.BeginEditCommand.CanExecute(eva));
        Assert.False(vm.AskHideCommand.CanExecute(eva));
        Assert.False(vm.OpenCommentsCommand.CanExecute(eva));
        Assert.True(vm.UnhideCommand.CanExecute(eva));
        vm.BeginEditCommand.Execute(eva);
        vm.AskHideCommand.Execute(eva);
        Assert.False(vm.IsFormOpen);
        Assert.False(vm.IsConfirmingHide);
        Assert.False(vm.ShowHiddenCommand.CanExecute(null)); // already there
        Assert.True(vm.ShowMembersCommand.CanExecute(null));
    }

    [Fact]
    public async Task Making_a_member_visible_takes_them_off_the_hidden_list_and_puts_them_among_the_members()
    {
        SignInAsKeeper();
        AddHiddenMember("Eva");
        AddHiddenMember("Finn");
        var vm = await ShownAsync();
        await vm.ShowHiddenCommand.ExecuteAsync(null);

        await vm.UnhideCommand.ExecuteAsync(Row(vm, "Eva"));

        Assert.Equal(["Finn"], vm.Members.Select(m => m.Name));
        Assert.Equal("Eva is visible again.", vm.NoticeText);
        Assert.Equal(["Finn"], _api.HiddenMembers.Select(m => m.Name));

        await vm.ShowMembersCommand.ExecuteAsync(null);

        Assert.Equal(["Anna", "Ben", "Clara", "Eva"], vm.Members.Select(m => m.Name));
        Assert.False(vm.HasNotice); // the notice is about the list that was left
    }

    [Fact]
    public async Task The_last_hidden_member_made_visible_leaves_an_empty_list_that_says_so()
    {
        SignInAsKeeper();
        AddHiddenMember("Eva");
        var vm = await ShownAsync();
        await vm.ShowHiddenCommand.ExecuteAsync(null);
        Assert.False(vm.IsNobodyHere);

        await vm.UnhideCommand.ExecuteAsync(Row(vm, "Eva"));

        Assert.True(vm.IsNobodyHere);
        Assert.Equal("No members are hidden.", vm.NobodyText);
        _language.Select("de");
        Assert.Equal("Es sind keine Mitglieder ausgeblendet.", vm.NobodyText);
        Assert.StartsWith("Diese Mitglieder sind ausgeblendet", vm.LedeText);
    }

    [Fact]
    public async Task A_refused_unhide_leaves_the_hidden_list_as_it_is_and_says_why()
    {
        SignInAsKeeper();
        AddHiddenMember("Eva");
        var vm = await ShownAsync();
        await vm.ShowHiddenCommand.ExecuteAsync(null);
        var eva = Row(vm, "Eva");
        _api.HiddenMembers.Clear(); // somebody else made them visible meanwhile

        await vm.UnhideCommand.ExecuteAsync(eva);

        Assert.True(vm.HasError);
        Assert.Equal(["Eva"], vm.Members.Select(m => m.Name));
        Assert.False(vm.HasNotice);
    }

    [Fact]
    public async Task Somebody_without_the_permission_has_no_way_to_the_hidden_members_even_by_the_command()
    {
        SignInWith(Permission.MembersView, Permission.MembersEdit, Permission.BeltsView);
        AddHiddenMember("Eva");
        var vm = await ShownAsync();

        Assert.False(vm.CanViewHidden);
        Assert.False(vm.ShowHiddenCommand.CanExecute(null));
        await vm.ShowHiddenCommand.ExecuteAsync(null);

        Assert.False(vm.ShowingHidden);
        Assert.Empty(_api.HiddenMemberListRequests);
        Assert.Equal(["Anna", "Ben", "Clara"], vm.Members.Select(m => m.Name));
        Assert.False(vm.UnhideCommand.CanExecute(Row(vm, "Anna")));
    }

    [Fact]
    public async Task Losing_the_permission_while_the_hidden_members_are_shown_brings_the_page_back_to_its_members()
    {
        SignInAsKeeper();
        AddHiddenMember("Eva");
        var vm = await ShownAsync();
        await vm.ShowHiddenCommand.ExecuteAsync(null);
        Assert.Equal(["Eva"], vm.Members.Select(m => m.Name));

        _shell.Session.User = TestAccounts.WithPermissions(1, "anna", Permission.MembersView, Permission.MembersEdit, Permission.BeltsView);
        await Eventually(() => vm.Members.Select(m => m.Name).SequenceEqual(["Anna", "Ben", "Clara"]));

        Assert.False(vm.ShowingHidden);
        Assert.False(vm.CanViewHidden);
    }

    [Fact]
    public async Task Losing_the_right_to_edit_closes_the_question_about_hiding()
    {
        SignInAsKeeper();
        var vm = await ShownAsync();
        vm.AskHideCommand.Execute(Row(vm, "Ben"));

        _shell.Session.User = TestAccounts.WithPermissions(1, "anna", Permission.MembersView, Permission.BeltsView);

        Assert.False(vm.IsConfirmingHide);
    }

    [Fact]
    public async Task A_hidden_list_that_cannot_be_read_says_so_and_can_be_read_again()
    {
        SignInAsKeeper();
        AddHiddenMember("Eva");
        _api.HiddenListFailure = new PlannerApiException("The hidden list is not available.");
        var vm = await ShownAsync();

        await vm.ShowHiddenCommand.ExecuteAsync(null);

        Assert.Equal("The hidden list is not available.", vm.ErrorMessage);
        Assert.True(vm.ShowingHidden);
        Assert.False(vm.IsNobodyHere); // not an empty list, one that could not be loaded

        _api.HiddenListFailure = null;
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.Equal(["Eva"], vm.Members.Select(m => m.Name));
    }

    [Fact]
    public async Task Another_group_shows_its_hidden_members_while_the_page_stays_on_the_hidden_list()
    {
        SignInAsKeeper();
        AddHiddenMember("Eva");
        AddHiddenMember("Dora", "g2");
        var vm = await ShownAsync();
        await vm.ShowHiddenCommand.ExecuteAsync(null);

        _shell.SelectedGroupId = "g2";
        await Eventually(() => vm.Members.Select(m => m.Name).SequenceEqual(["Dora"]));

        Assert.True(vm.ShowingHidden);
        Assert.Equal(["g1", "g2"], _api.HiddenMemberListRequests);
    }

    [Fact]
    public async Task The_hidden_list_is_not_entered_while_comments_or_a_form_are_open()
    {
        SignInWith(Permission.MembersView, Permission.MembersEdit, Permission.MembersHidden, Permission.BeltsView, Permission.CommentsView);
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Anna"));

        Assert.False(vm.ShowHiddenCommand.CanExecute(null));
        await vm.ShowHiddenCommand.ExecuteAsync(null);
        Assert.False(vm.ShowingHidden);
        Assert.True(vm.IsFormOpen);

        vm.CancelFormCommand.Execute(null);
        await vm.OpenCommentsCommand.ExecuteAsync(Row(vm, "Anna"));

        Assert.False(vm.ShowHiddenCommand.CanExecute(null));
        await vm.ShowHiddenCommand.ExecuteAsync(null);
        Assert.False(vm.ShowingHidden);
        Assert.Empty(_api.HiddenMemberListRequests);
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
