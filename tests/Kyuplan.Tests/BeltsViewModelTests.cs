using System.Globalization;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

/// <summary>The belts page: the list, and creating, changing and deleting belts.</summary>
public sealed class BeltsViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;
    private readonly BeltsViewModel _vm;

    public BeltsViewModelTests()
    {
        _language.Select("en");
        _shell = new ShellViewModel(_api, _language);
        _vm = new BeltsViewModel(_shell, _api);
    }

    public void Dispose()
    {
        // Selecting a language applies its culture process-wide; do not leak it into other tests.
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private static string Quoted(string text) => $"“{text}”";

    /// <summary>
    /// White, Yellow (from White: 2 years, 3 months), Orange (from White: 10 years, 12 months; from Yellow: 8 years, no waiting time:
    /// what is required depends on the belt a member comes from); a member holds White.
    /// </summary>
    private (BeltDto White, BeltDto Yellow, BeltDto Orange) Club()
    {
        var white = _api.AddBelt("White");
        var yellow = _api.AddBelt("Yellow", "#FFD800", requires: [new(white.Id, 2, 3)]);
        var orange = _api.AddBelt("Orange", "#FF8C00", "#FFFFFF", requires: [new(white.Id, 10, 12), new(yellow.Id, 8, 0)]);
        _api.AddMember("Anna", beltId: white.Id);
        return (white, yellow, orange);
    }

    private async Task<BeltsViewModel> ShownAsync()
    {
        await _vm.ActivateAsync();
        return _vm;
    }

    private static BeltRow Row(BeltsViewModel vm, string name) => vm.Belts.Single(b => b.Name == name);

    // --- The list ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_lists_the_belts_in_the_order_the_api_gives_with_their_requirements()
    {
        Club();

        var vm = await ShownAsync();

        Assert.Equal("Belts", vm.Title);
        Assert.Equal(["White", "Yellow", "Orange"], vm.Belts.Select(b => b.Name));
        // One line per previous belt, with what applies from there; what is not required is left out.
        Assert.Equal(
            [
                "None (first belt)",
                "White: min. age 2 y, waiting time 3 mo",
                "White: min. age 10 y, waiting time 12 mo\nYellow: min. age 8 y",
            ],
            vm.Belts.Select(b => b.PrerequisitesText));
        Assert.Equal(["#FFFFFF", "#FFD800", "#FF8C00"], vm.Belts.Select(b => b.PrimaryColor));
        Assert.Equal([false, false, true], vm.Belts.Select(b => b.HasSecondaryColor));
        Assert.Equal([1, 0, 0], vm.Belts.Select(b => b.MemberCount));
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public async Task Without_belts_the_page_invites_to_create_the_first_one_but_only_once_it_has_loaded()
    {
        Assert.False(_vm.IsEmpty); // nothing was asked yet: not "empty", just not there

        var vm = await ShownAsync();

        Assert.True(vm.IsEmpty);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task A_failed_list_can_be_loaded_again()
    {
        Club();
        _api.BeltListFailure = new PlannerApiException("The list is not available.");
        var vm = await ShownAsync();
        Assert.Equal("The list is not available.", vm.ErrorMessage);
        Assert.False(vm.IsEmpty); // a list that could not be loaded is not an empty one

        _api.BeltListFailure = null;
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.Equal(3, vm.Belts.Count);
    }

    [Fact]
    public async Task The_page_does_not_depend_on_the_selected_group()
    {
        Club();
        var vm = await ShownAsync();
        var requests = _api.BeltListRequests;

        _shell.SelectedGroupId = "g2";

        Assert.Equal(requests, _api.BeltListRequests);
        Assert.Equal(3, vm.Belts.Count);
    }

    // --- Creating ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_belt_needs_a_name_and_a_colour_before_it_can_be_created()
    {
        Club();
        var vm = await ShownAsync();
        Assert.False(vm.IsFormOpen);

        vm.BeginCreateCommand.Execute(null);

        Assert.True(vm.IsFormOpen);
        Assert.True(vm.IsCreating);
        Assert.Equal("New belt", vm.FormTitle);
        Assert.False(vm.CanSave);
        Assert.Null(vm.NameProblem); // nothing typed yet: no nagging
        Assert.Equal("#FFFFFF", vm.FormPrimaryColor);
        Assert.Equal(["White", "Yellow", "Orange"], vm.PrerequisiteOptions.Select(o => o.Name));
        Assert.All(vm.PrerequisiteOptions, o => Assert.False(o.IsSelected));

        vm.FormName = "Green";
        Assert.True(vm.CanSave);
        Assert.True(vm.SaveCommand.CanExecute(null));

        vm.FormPrimaryColor = "green";
        Assert.False(vm.CanSave);
        Assert.Equal("Write the colour as #RRGGBB, for example #FF8C00.", vm.PrimaryColorProblem);
        Assert.Equal("", vm.PrimaryPreview); // not a colour: nothing to paint

        vm.FormPrimaryColor = "#00a650";
        vm.FormSecondaryColor = "#12";
        Assert.False(vm.CanSave);
        Assert.NotNull(vm.SecondaryColorProblem);

        vm.FormSecondaryColor = "";
        Assert.True(vm.CanSave);
        Assert.Null(vm.SecondaryColorProblem); // no stripe is fine
    }

    [Fact]
    public async Task A_name_that_is_taken_in_any_case_cannot_be_saved()
    {
        Club();
        var vm = await ShownAsync();
        vm.BeginCreateCommand.Execute(null);

        vm.FormName = "  orange ";

        Assert.Equal("There already is a belt with this name.", vm.NameProblem);
        Assert.False(vm.CanSave);

        vm.FormName = new string('x', BeltRules.MaxNameLength + 1);
        Assert.NotNull(vm.NameProblem);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task The_age_and_the_waiting_time_of_a_previous_belt_must_be_within_the_limits_but_only_while_it_is_ticked()
    {
        var (_, yellow, _) = Club();
        var vm = await ShownAsync();
        vm.BeginCreateCommand.Execute(null);
        vm.FormName = "Green";
        var fromYellow = vm.PrerequisiteOptions.Single(o => o.Id == yellow.Id);
        Assert.True(vm.CanSave);

        fromYellow.MinAgeYears = BeltRules.MaxMinAgeYears + 1;
        Assert.True(vm.CanSave); // not ticked: the numbers do not count

        fromYellow.IsSelected = true;
        Assert.False(vm.CanSave);
        Assert.Equal($"Enter a whole number from 0 to {BeltRules.MaxMinAgeYears}.", fromYellow.MinAgeProblem);

        fromYellow.MinAgeYears = 12;
        fromYellow.MinWaitMonths = -1;
        Assert.False(vm.CanSave);
        Assert.Null(fromYellow.MinAgeProblem);
        Assert.Equal($"Enter a whole number from 0 to {BeltRules.MaxMinWaitMonths}.", fromYellow.MinWaitProblem);

        fromYellow.MinWaitMonths = 6;
        Assert.True(vm.CanSave);
    }

    [Fact]
    public async Task Creating_a_belt_sends_the_form_closes_it_and_shows_the_new_list()
    {
        var (white, yellow, _) = Club();
        var vm = await ShownAsync();
        vm.BeginCreateCommand.Execute(null);
        vm.FormName = "  Green ";
        vm.FormPrimaryColor = "#00a650";
        vm.FormSecondaryColor = "#ffffff";
        var fromYellow = vm.PrerequisiteOptions.Single(o => o.Id == yellow.Id);
        fromYellow.IsSelected = true;
        fromYellow.MinAgeYears = 10;
        fromYellow.MinWaitMonths = 6;
        var fromWhite = vm.PrerequisiteOptions.Single(o => o.Id == white.Id);
        fromWhite.IsSelected = true;
        fromWhite.MinAgeYears = 12;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["create-belt:Green"], _api.BeltCalls);
        var sent = Assert.Single(_api.BeltInputs);
        Assert.Equal(("Green", "#00A650", "#FFFFFF"), (sent.Name, sent.PrimaryColor, sent.SecondaryColor));
        // By belt, each with its own numbers: a belt that is not ticked is not sent however it was left.
        Assert.Equal([new BeltPrerequisiteDto(white.Id, 12, 0), new BeltPrerequisiteDto(yellow.Id, 10, 6)], sent.Prerequisites);
        Assert.False(vm.IsFormOpen);
        Assert.Equal(["White", "Yellow", "Orange", "Green"], vm.Belts.Select(b => b.Name));
        Assert.Equal("White: min. age 12 y\nYellow: min. age 10 y, waiting time 6 mo", Row(vm, "Green").PrerequisitesText);
        Assert.Equal($"Belt {Quoted("Green")} created.", vm.NoticeText);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task A_belt_without_a_stripe_is_sent_without_one()
    {
        var vm = await ShownAsync();
        vm.BeginCreateCommand.Execute(null);
        vm.FormName = "White";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(_api.BeltInputs).SecondaryColor);
        Assert.Empty(_api.BeltInputs.Single().Prerequisites);
        Assert.False(vm.Belts.Single().HasSecondaryColor);
    }

    [Fact]
    public async Task A_name_taken_meanwhile_keeps_the_form_open_to_be_corrected()
    {
        var vm = await ShownAsync();
        vm.BeginCreateCommand.Execute(null);
        vm.FormName = "Green";
        _api.AddBelt("green"); // somebody else was faster; this client has not seen it

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("There already is a belt with this name.", vm.ErrorMessage);
        Assert.True(vm.IsFormOpen);
        Assert.False(vm.HasNotice);

        vm.FormName = "Blue";
        Assert.False(vm.HasError); // correcting the form takes the message back
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsFormOpen);
        Assert.Contains("Blue", vm.Belts.Select(b => b.Name));
    }

    [Fact]
    public async Task Cancelling_the_form_changes_nothing()
    {
        Club();
        var vm = await ShownAsync();
        vm.BeginCreateCommand.Execute(null);
        vm.FormName = "Green";

        vm.CancelFormCommand.Execute(null);

        Assert.False(vm.IsFormOpen);
        Assert.Empty(_api.BeltCalls);
        Assert.Equal(3, vm.Belts.Count);
    }

    // --- Editing -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Editing_a_belt_starts_from_what_it_is_and_can_only_be_saved_once_something_differs()
    {
        var (white, _, _) = Club();
        var vm = await ShownAsync();

        vm.BeginEditCommand.Execute(Row(vm, "Yellow"));

        Assert.True(vm.IsFormOpen);
        Assert.False(vm.IsCreating);
        Assert.Equal($"Edit {Quoted("Yellow")}", vm.FormTitle);
        Assert.Equal(("Yellow", "#FFD800", ""), (vm.FormName, vm.FormPrimaryColor, vm.FormSecondaryColor));
        var fromWhite = Assert.Single(vm.PrerequisiteOptions);
        Assert.Equal((white.Id, true, 2, 3), (fromWhite.Id, fromWhite.IsSelected, fromWhite.MinAgeYears, fromWhite.MinWaitMonths));
        Assert.False(vm.CanSave); // nothing changed

        vm.FormPrimaryColor = "#ffd800"; // the same colour, only written differently
        Assert.False(vm.CanSave);

        fromWhite.MinWaitMonths = 4;
        Assert.True(vm.CanSave);
        fromWhite.MinWaitMonths = 3;
        Assert.False(vm.CanSave);

        fromWhite.IsSelected = false;
        Assert.True(vm.CanSave);
    }

    [Fact]
    public async Task What_a_belt_requires_depends_on_the_previous_belt_and_each_one_is_changed_on_its_own()
    {
        var (white, yellow, orange) = Club(); // Orange: from White 10 years and 12 months, from Yellow 8 years
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Orange"));

        var fromWhite = vm.PrerequisiteOptions.Single(o => o.Id == white.Id);
        var fromYellow = vm.PrerequisiteOptions.Single(o => o.Id == yellow.Id);
        Assert.Equal((10, 12), (fromWhite.MinAgeYears, fromWhite.MinWaitMonths));
        Assert.Equal((8, 0), (fromYellow.MinAgeYears, fromYellow.MinWaitMonths));
        Assert.False(vm.CanSave);

        fromYellow.MinAgeYears = 9; // only the way from Yellow changes
        Assert.True(vm.CanSave);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal([$"update-belt:{orange.Id}"], _api.BeltCalls);
        Assert.Equal([new BeltPrerequisiteDto(white.Id, 10, 12), new BeltPrerequisiteDto(yellow.Id, 9, 0)], Assert.Single(_api.BeltInputs).Prerequisites);
        Assert.Equal("White: min. age 10 y, waiting time 12 mo\nYellow: min. age 9 y", Row(vm, "Orange").PrerequisitesText);
    }

    [Fact]
    public async Task Saving_an_edited_belt_replaces_it_and_keeps_its_own_name_allowed()
    {
        var (_, yellow, _) = Club();
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Yellow"));
        Assert.Null(vm.NameProblem); // its own name is not "taken"
        vm.FormName = "Yellow II";
        vm.PrerequisiteOptions.Single().MinAgeYears = 5;
        vm.FormSecondaryColor = "#000000";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal([$"update-belt:{yellow.Id}"], _api.BeltCalls);
        Assert.False(vm.IsFormOpen);
        var row = Row(vm, "Yellow II");
        Assert.Equal(("White: min. age 5 y, waiting time 3 mo", "#000000"), (row.PrerequisitesText, row.SecondaryColor));
        Assert.Equal($"Belt {Quoted("Yellow II")} saved.", vm.NoticeText);
    }

    // --- The form's own buttons --------------------------------------------------------------------------------

    [Fact]
    public async Task The_new_and_edit_buttons_are_off_while_they_would_change_nothing()
    {
        Club();
        var vm = await ShownAsync();
        var yellow = Row(vm, "Yellow");
        Assert.True(vm.BeginCreateCommand.CanExecute(null));
        Assert.True(vm.BeginEditCommand.CanExecute(yellow));

        vm.BeginCreateCommand.Execute(null);
        Assert.False(vm.BeginCreateCommand.CanExecute(null)); // the form for a new belt is open already
        Assert.True(vm.BeginEditCommand.CanExecute(yellow));

        vm.BeginEditCommand.Execute(yellow);
        Assert.True(vm.BeginCreateCommand.CanExecute(null));
        Assert.False(vm.BeginEditCommand.CanExecute(yellow)); // that belt is open already
        Assert.True(vm.BeginEditCommand.CanExecute(Row(vm, "Orange")));
    }

    [Fact]
    public async Task A_colour_button_is_off_while_it_is_the_colour_in_use_and_the_stripe_can_be_taken_off_only_when_there_is_one()
    {
        var vm = await ShownAsync();
        vm.BeginCreateCommand.Execute(null);

        Assert.False(vm.PickPrimaryColorCommand.CanExecute("#FFFFFF"));
        Assert.True(vm.PickPrimaryColorCommand.CanExecute("#FFD800"));
        Assert.True(vm.PickSecondaryColorCommand.CanExecute("#FFD800"));
        Assert.False(vm.ClearSecondaryColorCommand.CanExecute(null));

        vm.PickPrimaryColorCommand.Execute("#FFD800");
        vm.PickSecondaryColorCommand.Execute("#000000");

        Assert.Equal(("#FFD800", "#000000"), (vm.FormPrimaryColor, vm.FormSecondaryColor));
        Assert.False(vm.PickPrimaryColorCommand.CanExecute("#ffd800")); // in any case
        Assert.False(vm.PickSecondaryColorCommand.CanExecute("#000000"));
        Assert.True(vm.ClearSecondaryColorCommand.CanExecute(null));

        vm.ClearSecondaryColorCommand.Execute(null);

        Assert.Equal("", vm.FormSecondaryColor);
        Assert.False(vm.ClearSecondaryColorCommand.CanExecute(null));
    }

    // --- Requirements that cannot be ticked --------------------------------------------------------------------

    [Fact]
    public async Task The_options_leave_out_the_belt_itself_and_the_belts_that_require_it()
    {
        Club(); // Yellow requires White; Orange requires White and Yellow
        var vm = await ShownAsync();

        vm.BeginEditCommand.Execute(Row(vm, "White"));
        Assert.Empty(vm.PrerequisiteOptions); // everything else requires White

        vm.BeginEditCommand.Execute(Row(vm, "Yellow"));
        Assert.Equal(["White"], vm.PrerequisiteOptions.Select(o => o.Name)); // Orange requires Yellow

        vm.BeginEditCommand.Execute(Row(vm, "Orange"));
        Assert.Equal(["White", "Yellow"], vm.PrerequisiteOptions.Select(o => o.Name));
    }

    // --- Deleting ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_belt_that_members_hold_or_others_require_says_why_it_cannot_be_deleted()
    {
        Club(); // Anna holds White; Yellow requires White; Orange requires White and Yellow
        var vm = await ShownAsync();

        Assert.Equal("Cannot be deleted: members with this belt: 1.", Row(vm, "White").DeleteBlockedText);
        Assert.Equal("Cannot be deleted: required for Orange.", Row(vm, "Yellow").DeleteBlockedText);
        Assert.Null(Row(vm, "Orange").DeleteBlockedText);

        Assert.False(vm.AskDeleteCommand.CanExecute(Row(vm, "White")));
        Assert.False(vm.AskDeleteCommand.CanExecute(Row(vm, "Yellow")));
        Assert.True(vm.AskDeleteCommand.CanExecute(Row(vm, "Orange")));
    }

    [Fact]
    public async Task Deleting_a_belt_asks_first_and_then_shows_the_new_list()
    {
        var (_, _, orange) = Club();
        var vm = await ShownAsync();

        vm.AskDeleteCommand.Execute(Row(vm, "Orange"));

        Assert.True(vm.IsConfirmingDelete);
        Assert.Equal($"Delete the belt {Quoted("Orange")}?", vm.ConfirmText);
        Assert.Empty(_api.BeltCalls);

        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal([$"delete-belt:{orange.Id}"], _api.BeltCalls);
        Assert.False(vm.IsConfirmingDelete);
        Assert.Equal(["White", "Yellow"], vm.Belts.Select(b => b.Name));
        Assert.Equal($"Belt {Quoted("Orange")} deleted.", vm.NoticeText);
    }

    [Fact]
    public async Task Changing_ones_mind_deletes_nothing()
    {
        Club();
        var vm = await ShownAsync();
        vm.AskDeleteCommand.Execute(Row(vm, "Orange"));

        vm.CancelDeleteCommand.Execute(null);

        Assert.False(vm.IsConfirmingDelete);
        Assert.Empty(_api.BeltCalls);
        Assert.Equal(3, vm.Belts.Count);
    }

    [Fact]
    public async Task Deleting_the_belt_that_is_being_edited_closes_its_form()
    {
        Club();
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Orange"));
        vm.AskDeleteCommand.Execute(Row(vm, "Orange"));

        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.False(vm.IsFormOpen);
    }

    // --- Fresh every time it is shown ---------------------------------------------------------------------------

    [Fact]
    public async Task The_first_time_the_page_is_shown_the_belts_are_read_once()
    {
        Club();

        await ShownAsync();

        Assert.Equal(1, _api.BeltListRequests);
    }

    [Fact]
    public async Task Showing_the_page_again_reads_the_belts_again_so_that_the_member_counts_are_right()
    {
        var (_, yellow, _) = Club();
        var vm = await ShownAsync();
        Assert.Equal(0, Row(vm, "Yellow").MemberCount);
        Assert.NotNull(Row(vm, "Yellow").DeleteBlockedText); // Orange requires it

        // On the members page somebody was given the Yellow belt, and the page was left.
        vm.Deactivate();
        _api.AddMember("Ben", beltId: yellow.Id);
        await vm.ActivateAsync();

        Assert.Equal(2, _api.BeltListRequests);
        Assert.Equal(1, Row(vm, "Yellow").MemberCount);
        Assert.Equal("Cannot be deleted: members with this belt: 1.", Row(vm, "Yellow").DeleteBlockedText);
    }

    [Fact]
    public async Task A_form_that_is_open_is_left_alone_when_the_page_is_shown_again()
    {
        Club();
        var vm = await ShownAsync();
        vm.BeginEditCommand.Execute(Row(vm, "Yellow"));
        vm.FormName = "Yellow II";
        var rows = vm.Belts;

        vm.Deactivate();
        await vm.ActivateAsync();

        Assert.Equal(1, _api.BeltListRequests); // nothing was read: what is being typed must not lose its belts under it
        Assert.Same(rows, vm.Belts);
        Assert.Equal("Yellow II", vm.FormName);
        Assert.True(vm.IsFormOpen);
    }

    // --- Language ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_texts_follow_the_language_without_asking_the_api_again()
    {
        Club();
        var vm = await ShownAsync();
        var row = Row(vm, "White");
        var rowChanged = false;
        row.PropertyChanged += (_, _) => rowChanged = true;
        vm.BeginCreateCommand.Execute(null);
        vm.FormPrimaryColor = "x";
        Assert.Equal("Write the colour as #RRGGBB, for example #FF8C00.", vm.PrimaryColorProblem);
        var fromYellow = vm.PrerequisiteOptions.Single(o => o.Id == Row(vm, "Yellow").Id);
        fromYellow.MinAgeYears = -1;
        Assert.Equal($"Enter a whole number from 0 to {BeltRules.MaxMinAgeYears}.", fromYellow.MinAgeProblem);
        vm.ErrorMessage = "Something the API said.";

        _language.Select("de");

        Assert.Equal("Gürtel", vm.Title);
        Assert.Equal("Keiner (erster Gürtel)", row.PrerequisitesText);
        Assert.Equal("White: Mindestalter 2 J., Wartezeit 3 Mon.", Row(vm, "Yellow").PrerequisitesText);
        Assert.Equal($"Gib eine ganze Zahl von 0 bis {BeltRules.MaxMinAgeYears} ein.", fromYellow.MinAgeProblem);
        Assert.Equal("Something the API said.", vm.ErrorMessage); // a language change is not an edit of the form
        Assert.Equal("Kann nicht gelöscht werden: Mitglieder mit diesem Gürtel: 1.", row.DeleteBlockedText);
        Assert.Equal("Neuer Gürtel", vm.FormTitle);
        Assert.Equal("Schreibe die Farbe als #RRGGBB, zum Beispiel #FF8C00.", vm.PrimaryColorProblem);
        Assert.Same(row, Row(vm, "White")); // the rows stay, they re-read their texts
        Assert.True(rowChanged);
        Assert.Equal(1, _api.BeltListRequests);
    }
}
