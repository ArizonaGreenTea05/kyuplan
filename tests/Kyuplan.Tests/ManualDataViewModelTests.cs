using System.Globalization;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>The manual data page against a fake API: pick a group, keep its subgroups, members and events, delete only after asking.</summary>
public sealed class ManualDataViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ManualDataViewModel _vm;

    public ManualDataViewModelTests()
    {
        _language.Select("en");
        _api.Account = TestAccounts.Dto(1, "boss", "boss", [], isAdmin: true);
        _vm = new ManualDataViewModel(new ShellViewModel(_api, _language), _api);
    }

    public void Dispose()
    {
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private async Task<ManualGroupDto> SeedGroupAsync()
    {
        var group = new ManualGroupDto("g1", "Dojo", [new SubGroupDto("s1", "Kids")], 0);
        _api.ManualGroups.Add(group);
        _api.ManualMembers.Add(new ManualMemberDto("m1", "g1", "Anna", new DateOnly(2014, 5, 1), ["s1"]));
        _api.ManualMembers.Add(new ManualMemberDto("m2", "g1", "Ben", null, []));
        await _vm.ActivateAsync();
        return group;
    }

    [Fact]
    public async Task The_page_shows_the_groups_and_the_members_subgroups_and_events_of_the_first()
    {
        _api.ManualEvents.Add(new ManualEventDto("e1", "g1", "Training", new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 10, 1, 19, 30, 0, TimeSpan.FromHours(2)), false, [new ManualInvitationDto("m1", ManualRsvp.Accepted), new ManualInvitationDto("m2", ManualRsvp.Declined)]));
        await SeedGroupAsync();

        Assert.Equal("g1", _vm.SelectedGroup?.Id);
        Assert.Equal(["Anna", "Ben"], _vm.Members.Select(m => m.Name));
        Assert.Equal("Kids", _vm.Members[0].SubGroupsText);
        Assert.Equal("Members: 1", _vm.SubGroups.Single().CountText);
        Assert.Equal("2 invited, 1 accepted", Assert.Single(_vm.Events).InvitedText);
        Assert.False(_vm.MembersEmpty);
        Assert.True(_vm.IsMembersSection);
    }

    [Fact]
    public async Task Without_any_group_the_page_invites_to_make_the_first()
    {
        await _vm.ActivateAsync();

        Assert.True(_vm.HasNoGroups);
        Assert.False(_vm.HasGroup);
        Assert.False(_vm.MemberEditor.BeginNewCommand.CanExecute(null)); // nothing to put a member into
        Assert.True(_vm.NameForm.BeginNewGroupCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_group_is_made_selected_and_renamed_by_the_name_form()
    {
        await _vm.ActivateAsync();

        _vm.NameForm.BeginNewGroupCommand.Execute(null);
        Assert.True(_vm.NameForm.IsOpen);
        Assert.False(_vm.NameForm.SaveCommand.CanExecute(null)); // no name yet
        _vm.NameForm.Text = "Dojo";
        await _vm.NameForm.SaveCommand.ExecuteAsync(null);

        Assert.False(_vm.NameForm.IsOpen);
        Assert.Equal("Dojo", _vm.SelectedGroup?.Name);
        Assert.Equal(["create-group:Dojo"], _api.ManualCalls);

        _vm.NameForm.BeginRenameGroupCommand.Execute(null);
        Assert.Equal("Dojo", _vm.NameForm.Text);
        Assert.False(_vm.NameForm.SaveCommand.CanExecute(null)); // the same name changes nothing
        _vm.NameForm.Text = "Karate";
        await _vm.NameForm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Karate", _vm.SelectedGroup?.Name);
        Assert.Contains($"rename-group:{_vm.SelectedGroup!.Id}:Karate", _api.ManualCalls);
    }

    [Fact]
    public async Task A_name_that_is_too_long_cannot_be_saved()
    {
        await _vm.ActivateAsync();
        _vm.NameForm.BeginNewGroupCommand.Execute(null);

        _vm.NameForm.Text = new string('x', ManualDataRules.MaxNameLength + 1);

        Assert.NotNull(_vm.NameForm.Problem);
        Assert.False(_vm.NameForm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_subgroup_is_added_and_the_member_form_offers_it()
    {
        await SeedGroupAsync();

        _vm.SectionIndex = ManualDataViewModel.SubGroupsSection;
        _vm.NameForm.BeginNewSubGroupCommand.Execute(null);
        _vm.NameForm.Text = "Teens";
        await _vm.NameForm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["Kids", "Teens"], _vm.SubGroups.Select(s => s.Name));

        _vm.MemberEditor.BeginNewCommand.Execute(null);
        Assert.Equal(["Kids", "Teens"], _vm.MemberEditor.SubGroups.Select(s => s.Name));
        Assert.True(_vm.MemberEditor.HasSubGroupChoices);
    }

    [Fact]
    public async Task A_member_is_saved_with_the_ticked_subgroups_and_the_birth_date()
    {
        await SeedGroupAsync();

        _vm.MemberEditor.BeginNewCommand.Execute(null);
        Assert.False(_vm.MemberEditor.SaveCommand.CanExecute(null));
        _vm.MemberEditor.Name = "  Clara ";
        _vm.MemberEditor.BirthDate = new DateTime(2012, 3, 4);
        _vm.MemberEditor.SubGroups.Single().IsSelected = true;
        Assert.True(_vm.MemberEditor.SaveCommand.CanExecute(null));
        await _vm.MemberEditor.SaveCommand.ExecuteAsync(null);

        Assert.False(_vm.MemberEditor.IsOpen);
        var input = _api.LastManualMemberInput!;
        Assert.Equal(("Clara", new DateOnly(2012, 3, 4)), (input.Name, input.BirthDate));
        Assert.Equal(["s1"], input.SubGroupIds);
        Assert.Contains("Clara", _vm.Members.Select(m => m.Name));
    }

    [Fact]
    public async Task A_member_is_only_saved_when_something_changed_and_a_birth_date_in_the_future_is_refused()
    {
        await SeedGroupAsync();

        _vm.MemberEditor.BeginEditCommand.Execute(_vm.Members[0]);
        Assert.Equal("Anna", _vm.MemberEditor.Name);
        Assert.True(_vm.MemberEditor.SubGroups.Single().IsSelected);
        Assert.False(_vm.MemberEditor.SaveCommand.CanExecute(null));

        _vm.MemberEditor.BirthDate = DateTime.Today.AddDays(10);
        Assert.NotNull(_vm.MemberEditor.BirthProblem);
        Assert.False(_vm.MemberEditor.SaveCommand.CanExecute(null));

        _vm.MemberEditor.ClearBirthDateCommand.Execute(null);
        Assert.True(_vm.MemberEditor.SaveCommand.CanExecute(null));
        await _vm.MemberEditor.SaveCommand.ExecuteAsync(null);

        Assert.Null(_api.ManualMembers.Single(m => m.Id == "m1").BirthDate);
    }

    [Fact]
    public async Task An_event_is_saved_in_the_club_time_zone_with_the_people_invited_and_what_they_answered()
    {
        await SeedGroupAsync();
        _api.Info = _api.Info with { TimeZone = "Europe/Berlin" };

        _vm.SectionIndex = ManualDataViewModel.EventsSection;
        await _vm.EventEditor.BeginNewCommand.ExecuteAsync(null);
        Assert.True(_vm.EventEditor.IsOpen);
        Assert.Equal(["Anna", "Ben"], _vm.EventEditor.Invitees.Select(i => i.Name));
        Assert.False(_vm.EventEditor.SaveCommand.CanExecute(null)); // no name yet

        _vm.EventEditor.Name = "Training";
        _vm.EventEditor.StartDate = new DateTime(2026, 10, 1);
        _vm.EventEditor.StartTime = "18:00";
        _vm.EventEditor.EndTime = "19:30";
        _vm.EventEditor.InviteAllCommand.Execute(null);
        _vm.EventEditor.AcceptAllCommand.Execute(null);
        _vm.EventEditor.Invitees[1].Answer = ManualRsvpOption.All.Single(o => o.Value == ManualRsvp.Declined);
        await _vm.EventEditor.SaveCommand.ExecuteAsync(null);

        var input = _api.LastManualEventInput!;
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.FromHours(2)), input.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 19, 30, 0, TimeSpan.FromHours(2)), input.End);
        Assert.Equal([new ManualInvitationDto("m1", ManualRsvp.Accepted), new ManualInvitationDto("m2", ManualRsvp.Declined)], input.Invitations);
        Assert.False(_vm.EventEditor.IsOpen);
        Assert.Single(_vm.Events);
    }

    [Fact]
    public async Task The_members_who_host_an_event_are_ticked_in_the_form_and_saved_with_it()
    {
        _api.ManualEvents.Add(new ManualEventDto("e1", "g1", "Training", new DateTimeOffset(2026, 10, 1, 18, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 10, 1, 19, 30, 0, TimeSpan.FromHours(2)), false, [new ManualInvitationDto("m1", ManualRsvp.Accepted)], ["m2"]));
        await SeedGroupAsync();
        _api.Info = _api.Info with { TimeZone = "Europe/Berlin" };
        _vm.SectionIndex = ManualDataViewModel.EventsSection;

        await _vm.EventEditor.BeginEditCommand.ExecuteAsync(_vm.Events.Single());

        Assert.Equal([false, true], _vm.EventEditor.Invitees.Select(i => i.IsTrainer)); // Ben hosts without being invited
        Assert.False(_vm.EventEditor.SaveCommand.CanExecute(null)); // nothing changed

        _vm.EventEditor.Invitees[0].IsTrainer = true;
        Assert.True(_vm.EventEditor.SaveCommand.CanExecute(null));
        await _vm.EventEditor.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["m1", "m2"], _api.LastManualEventInput!.TrainerIds);
        Assert.Equal(["m1", "m2"], _api.ManualEvents.Single().TrainerIds);

        _vm.EventEditor.BeginNewCommand.Execute(null);
        await Task.Yield();
        Assert.All(_vm.EventEditor.Invitees, i => Assert.False(i.IsTrainer));
    }

    [Fact]
    public async Task An_event_that_ends_before_it_starts_or_has_no_readable_time_cannot_be_saved()
    {
        await SeedGroupAsync();
        await _vm.EventEditor.BeginNewCommand.ExecuteAsync(null);
        _vm.EventEditor.Name = "Training";

        _vm.EventEditor.StartDate = new DateTime(2026, 10, 1);
        _vm.EventEditor.StartTime = "18:00";
        _vm.EventEditor.EndDate = new DateTime(2026, 10, 1);
        _vm.EventEditor.EndTime = "17:00";
        Assert.Equal("The end must not be before the start.", _vm.EventEditor.TimeProblem);
        Assert.False(_vm.EventEditor.SaveCommand.CanExecute(null));

        _vm.EventEditor.EndTime = "later";
        Assert.NotNull(_vm.EventEditor.TimeProblem);
        Assert.False(_vm.EventEditor.SaveCommand.CanExecute(null));

        _vm.EventEditor.EndTime = "19:00";
        Assert.Null(_vm.EventEditor.TimeProblem);
        Assert.True(_vm.EventEditor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_event_is_opened_with_what_it_has_and_not_inviting_somebody_takes_them_out()
    {
        await SeedGroupAsync();
        _api.ManualEvents.Add(new ManualEventDto("e1", "g1", "Training", new DateTimeOffset(2026, 10, 1, 16, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 17, 0, 0, TimeSpan.Zero), false, [new ManualInvitationDto("m1", ManualRsvp.Accepted)]));
        await _vm.RetryCommand.ExecuteAsync(null);

        await _vm.EventEditor.BeginEditCommand.ExecuteAsync(_vm.Events[0]);

        Assert.Equal("Training", _vm.EventEditor.Name);
        Assert.True(_vm.EventEditor.Invitees[0].IsInvited);
        Assert.Equal(ManualRsvp.Accepted, _vm.EventEditor.Invitees[0].Answer.Value);
        Assert.False(_vm.EventEditor.Invitees[1].IsInvited);
        Assert.False(_vm.EventEditor.SaveCommand.CanExecute(null));

        _vm.EventEditor.Invitees[0].IsInvited = false;
        Assert.True(_vm.EventEditor.SaveCommand.CanExecute(null));
        await _vm.EventEditor.SaveCommand.ExecuteAsync(null);
        Assert.Empty(_api.LastManualEventInput!.Invitations!);
    }

    [Fact]
    public async Task Deleting_asks_first_and_only_the_answer_yes_deletes()
    {
        await SeedGroupAsync();

        _vm.AskDeleteMemberCommand.Execute(_vm.Members[0]);
        Assert.True(_vm.IsConfirming);
        Assert.Contains("Anna", _vm.ConfirmText);
        _vm.CancelConfirmCommand.Execute(null);
        Assert.False(_vm.IsConfirming);
        Assert.DoesNotContain(_api.ManualCalls, c => c.StartsWith("delete", StringComparison.Ordinal));

        _vm.AskDeleteMemberCommand.Execute(_vm.Members[0]);
        await _vm.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(["delete-member:m1"], _api.ManualCalls);
        Assert.Equal(["Ben"], _vm.Members.Select(m => m.Name));
        Assert.Contains("Anna", _vm.NoticeText);

        _vm.AskDeleteGroupCommand.Execute(null);
        Assert.Contains("Dojo", _vm.ConfirmText);
        await _vm.ConfirmCommand.ExecuteAsync(null);
        Assert.True(_vm.HasNoGroups);
        Assert.Empty(_vm.Members);
    }

    [Fact]
    public async Task Only_one_form_is_open_at_a_time()
    {
        await SeedGroupAsync();

        _vm.NameForm.BeginNewGroupCommand.Execute(null);
        Assert.True(_vm.NameForm.IsOpen);
        Assert.True(_vm.IsAnyFormOpen);

        _vm.MemberEditor.BeginNewCommand.Execute(null);
        Assert.False(_vm.NameForm.IsOpen);
        Assert.True(_vm.MemberEditor.IsOpen);

        await _vm.EventEditor.BeginNewCommand.ExecuteAsync(null);
        Assert.False(_vm.MemberEditor.IsOpen);
        Assert.True(_vm.EventEditor.IsOpen);

        _vm.NameForm.BeginRenameGroupCommand.Execute(null);
        Assert.False(_vm.EventEditor.IsOpen);
        Assert.True(_vm.NameForm.IsOpen);

        _vm.NameForm.CancelCommand.Execute(null);
        Assert.False(_vm.IsAnyFormOpen);
    }

    [Fact]
    public async Task A_delete_question_closes_the_open_form_and_opening_a_form_withdraws_the_question()
    {
        await SeedGroupAsync();

        _vm.MemberEditor.BeginEditCommand.Execute(_vm.Members[0]);
        Assert.True(_vm.MemberEditor.IsOpen);

        _vm.AskDeleteMemberCommand.Execute(_vm.Members[1]);
        Assert.False(_vm.MemberEditor.IsOpen);
        Assert.True(_vm.IsConfirming);

        _vm.NameForm.BeginNewGroupCommand.Execute(null);
        Assert.False(_vm.IsConfirming);
        Assert.True(_vm.NameForm.IsOpen);
    }

    [Fact]
    public async Task Changing_the_section_or_the_group_closes_the_open_form()
    {
        await SeedGroupAsync();
        _api.ManualGroups.Add(new ManualGroupDto("g2", "Other", [], 0));
        await _vm.ActivateAsync();

        _vm.MemberEditor.BeginNewCommand.Execute(null);
        _vm.SectionIndex = ManualDataViewModel.SubGroupsSection;
        Assert.False(_vm.MemberEditor.IsOpen);

        _vm.NameForm.BeginNewSubGroupCommand.Execute(null);
        _vm.SelectedGroup = _vm.Groups.Single(g => g.Id == "g2");
        Assert.False(_vm.NameForm.IsOpen);
    }

    [Fact]
    public async Task Form_titles_and_problems_follow_the_language()
    {
        await SeedGroupAsync();
        _vm.MemberEditor.BeginEditCommand.Execute(_vm.Members[0]);
        _vm.MemberEditor.Name = new string('x', ManualDataRules.MaxNameLength + 1);
        var english = _vm.MemberEditor.NameProblem;
        var titles = new List<string?>();
        _vm.MemberEditor.PropertyChanged += (_, e) => titles.Add(e.PropertyName);

        _language.Select("de");

        Assert.NotNull(english);
        Assert.NotEqual(english, _vm.MemberEditor.NameProblem);
        Assert.Contains(nameof(ManualMemberEditorViewModel.Title), titles);
        Assert.Contains(nameof(ManualMemberEditorViewModel.NameProblem), titles);
    }

    [Fact]
    public async Task Somebody_without_the_permission_has_no_page()
    {
        _api.Account = TestAccounts.WithPermissions(2, "trainer", Permission.MembersView);
        var vm = new ManualDataViewModel(new ShellViewModel(_api, _language), _api);

        await vm.ActivateAsync();

        Assert.False(vm.CanView);
        Assert.Empty(_api.ManualCalls);
        Assert.False(vm.NameForm.BeginNewGroupCommand.CanExecute(null));
    }
}
