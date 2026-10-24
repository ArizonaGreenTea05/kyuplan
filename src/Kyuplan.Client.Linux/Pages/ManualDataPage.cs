using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages;

/// <summary>Groups, subgroups, members and events that are entered by hand (the data source "manual").</summary>
internal static class ManualDataPage
{
    public static Gtk.Widget Build(UiContext context, ManualDataViewModel page)
    {
        var group = Ui.Choice(page, () => page.Groups, g => g.Name, () => page.SelectedGroup, g => page.SelectedGroup = g,
            [nameof(ManualDataViewModel.Groups), nameof(ManualDataViewModel.SelectedGroup)], Strings.Manual_Group);
        var groupBar = Ui.HBox(8, Ui.Caption(Strings.Manual_Group, "dim-label"), group,
            Ui.Button(Strings.Manual_RenameGroup, page.NameForm.BeginRenameGroupCommand).VisibleWhen(page, nameof(ManualDataViewModel.HasGroup)),
            Ui.Button(Strings.Manual_DeleteGroup, page.AskDeleteGroupCommand).VisibleWhen(page, nameof(ManualDataViewModel.HasGroup)),
            Ui.Primary(Strings.Manual_NewGroup, page.NameForm.BeginNewGroupCommand));

        var sections = Gtk.Stack.New();
        sections.AddTitled(Members(page), "members", Strings.Manual_TabMembers);
        sections.AddTitled(SubGroups(page), "subgroups", Strings.Manual_TabSubGroups);
        sections.AddTitled(Events(page), "events", Strings.Manual_TabEvents);
        var switcher = Gtk.StackSwitcher.New();
        switcher.SetStack(sections);
        switcher.Halign = Gtk.Align.Start;
        // The tab is the view model's state (it keeps it while other pages are shown).
        Bind.Watch(sections, page, () => sections.SetVisibleChildName(page.SectionIndex switch
        {
            ManualDataViewModel.SubGroupsSection => "subgroups",
            ManualDataViewModel.EventsSection => "events",
            _ => "members",
        }), nameof(ManualDataViewModel.SectionIndex));
        sections.OnNotify += (_, e) =>
        {
            if (e.Pspec.GetName() != "visible-child-name") return;

            page.SectionIndex = sections.GetVisibleChildName() switch
            {
                "subgroups" => ManualDataViewModel.SubGroupsSection,
                "events" => ManualDataViewModel.EventsSection,
                _ => ManualDataViewModel.MembersSection,
            };
        };

        var confirm = Widgets.ConfirmBar(page, nameof(ManualDataViewModel.IsConfirming), nameof(ManualDataViewModel.ConfirmText), page.ConfirmCommand, Strings.Manual_ConfirmYes, page.CancelConfirmCommand, Strings.Manual_Cancel);

        var content = Ui.VBox(12, groupBar, NameForm(page.NameForm), confirm,
            Ui.Muted(Strings.Manual_NoGroups).VisibleWhen(page, nameof(ManualDataViewModel.HasNoGroups)),
            Ui.VBox(12, switcher, sections).VisibleWhen(page, nameof(ManualDataViewModel.HasGroup)))
            .HiddenWhen(page, nameof(ManualDataViewModel.IsWrongSource));

        var body = Ui.VBox(14,
            Widgets.Header(page.Title),
            Ui.Label(Strings.Manual_WrongSource, "warn-text").VisibleWhen(page, nameof(ManualDataViewModel.IsWrongSource)),
            Widgets.ErrorBanner(page, page.RetryCommand), Widgets.Notice(page),
            content);
        return Widgets.PageFrame(page, body, 1100);
    }

    private static Gtk.Widget NameForm(ManualNameFormViewModel form) =>
        Ui.VBox(8,
            Ui.Label(form, nameof(ManualNameFormViewModel.Title), "heading").Start(),
            Ui.Entry(form, nameof(ManualNameFormViewModel.Text), name: Strings.Manual_Name),
            Ui.Label(form, () => form.Problem, [nameof(ManualNameFormViewModel.Problem), nameof(ManualNameFormViewModel.Text)], "error", "caption")
                .VisibleWhen(form, () => form.Problem is not null, nameof(ManualNameFormViewModel.Problem), nameof(ManualNameFormViewModel.Text)),
            Ui.HBox(8, Ui.Primary(Strings.Manual_Save, form.SaveCommand), Ui.Button(Strings.Manual_Cancel, form.CancelCommand)))
            .Css("card").Padded(14).VisibleWhen(form, nameof(ManualNameFormViewModel.IsOpen));

    // ---- members -----------------------------------------------------------------------------------------------------------

    private static Gtk.Widget Members(ManualDataViewModel page)
    {
        var editor = page.MemberEditor;
        var form = Ui.VBox(10,
            Ui.Label(editor, nameof(ManualMemberEditorViewModel.Title), "heading").Start(),
            Ui.Field(Strings.Manual_Name, Ui.Entry(editor, nameof(ManualMemberEditorViewModel.Name), name: Strings.Manual_Name)),
            Ui.Label(editor, () => editor.NameProblem, [nameof(ManualMemberEditorViewModel.NameProblem), nameof(ManualMemberEditorViewModel.Name)], "error", "caption")
                .VisibleWhen(editor, () => editor.NameProblem is not null, nameof(ManualMemberEditorViewModel.NameProblem), nameof(ManualMemberEditorViewModel.Name)),
            Ui.Field(Strings.Manual_BirthDate, DateField.WithClear(editor, nameof(ManualMemberEditorViewModel.BirthDate), Display.None, Strings.Manual_BirthDate, editor.ClearBirthDateCommand, Strings.Manual_ClearBirth)),
            Ui.Label(editor, () => editor.BirthProblem, [nameof(ManualMemberEditorViewModel.BirthProblem), nameof(ManualMemberEditorViewModel.BirthDate)], "error", "caption")
                .VisibleWhen(editor, () => editor.BirthProblem is not null, nameof(ManualMemberEditorViewModel.BirthProblem), nameof(ManualMemberEditorViewModel.BirthDate)),
            Ui.VBox(4, Ui.Label(Strings.Manual_SubGroups, "caption-heading").Start(),
                Ui.Repeat(editor, () => editor.SubGroups, c => Ui.Check(c, nameof(ManualChoiceRow.IsSelected), c.Name), [nameof(ManualMemberEditorViewModel.SubGroups)], 2),
                Ui.Muted(Strings.Manual_NoSubGroupsHint).Css("caption").HiddenWhen(editor, nameof(ManualMemberEditorViewModel.HasSubGroupChoices))),
            Ui.HBox(8, Ui.Primary(Strings.Manual_Save, editor.SaveCommand), Ui.Button(Strings.Manual_Cancel, editor.CancelCommand)))
            .Css("card").Padded(14).VisibleWhen(editor, nameof(ManualMemberEditorViewModel.IsOpen));

        var list = Ui.BoxedList(page, () => page.Members, member =>
        {
            var row = Ui.Row(member.Name, $"{Strings.Manual_ColBirth}: {member.BirthText} · {member.SubGroupsText}");
            row.AddSuffix(Ui.IconButton("document-edit-symbolic", Strings.Manual_Edit, editor.BeginEditCommand, member, "flat"));
            row.AddSuffix(Ui.IconButton("user-trash-symbolic", Strings.Manual_Delete, page.AskDeleteMemberCommand, member, "flat"));
            return row;
        }, [nameof(ManualDataViewModel.Members)]);

        return Ui.VBox(10, Ui.Button(Strings.Manual_NewMember, editor.BeginNewCommand).Start(), form,
            Ui.Muted(Strings.Manual_NoMembers).VisibleWhen(page, nameof(ManualDataViewModel.MembersEmpty)), list);
    }

    // ---- subgroups ---------------------------------------------------------------------------------------------------------

    private static Gtk.Widget SubGroups(ManualDataViewModel page)
    {
        var list = Ui.BoxedList(page, () => page.SubGroups, subGroup =>
        {
            var row = Ui.Row(subGroup.Name, subGroup.CountText);
            row.AddSuffix(Ui.Button(Strings.Manual_Rename, page.NameForm.BeginRenameSubGroupCommand, subGroup, "flat"));
            row.AddSuffix(Ui.IconButton("user-trash-symbolic", Strings.Manual_Delete, page.AskDeleteSubGroupCommand, subGroup, "flat"));
            return row;
        }, [nameof(ManualDataViewModel.SubGroups)]);

        return Ui.VBox(10, Ui.Button(Strings.Manual_NewSubGroup, page.NameForm.BeginNewSubGroupCommand).Start(),
            Ui.Muted(Strings.Manual_NoSubGroups).VisibleWhen(page, nameof(ManualDataViewModel.SubGroupsEmpty)), list);
    }

    // ---- events ------------------------------------------------------------------------------------------------------------

    private static Gtk.Widget Events(ManualDataViewModel page)
    {
        var editor = page.EventEditor;
        var invitees = Table.Create(editor, () => editor.Invitees, [nameof(ManualEventEditorViewModel.Invitees)],
            new Column<ManualInviteRow>(Strings.Manual_ColName, i => Ui.Check(i, nameof(ManualInviteRow.IsInvited), i.Name), true),
            new Column<ManualInviteRow>(Strings.Manual_ColTrainer, i => Ui.Check(i, nameof(ManualInviteRow.IsTrainer), "")),
            new Column<ManualInviteRow>(Strings.Manual_ColAnswer, i => Ui.Choice(i, () => editor.RsvpOptions, o => o.Text, () => i.Answer, o => i.Answer = o,
                [nameof(ManualInviteRow.Answer)]).Tap(w => w.Hexpand = false)));

        var form = Ui.VBox(10,
            Ui.Label(editor, nameof(ManualEventEditorViewModel.Title), "heading").Start(),
            Ui.Field(Strings.Manual_Name, Ui.Entry(editor, nameof(ManualEventEditorViewModel.Name), name: Strings.Manual_Name)),
            Ui.Label(editor, () => editor.NameProblem, [nameof(ManualEventEditorViewModel.NameProblem), nameof(ManualEventEditorViewModel.Name)], "error", "caption")
                .VisibleWhen(editor, () => editor.NameProblem is not null, nameof(ManualEventEditorViewModel.NameProblem), nameof(ManualEventEditorViewModel.Name)),
            Ui.Flow(
                Ui.Field(Strings.Manual_Start, Ui.HBox(6, DateField.Create(editor, nameof(ManualEventEditorViewModel.StartDate), Display.None, Strings.Manual_Start),
                    Ui.Entry(editor, nameof(ManualEventEditorViewModel.StartTime), name: Strings.Manual_Start).Tap(e => { e.WidthChars = 6; e.Hexpand = false; })), Strings.Manual_TimeHint),
                Ui.Field(Strings.Manual_End, Ui.HBox(6, DateField.Create(editor, nameof(ManualEventEditorViewModel.EndDate), Display.None, Strings.Manual_End),
                    Ui.Entry(editor, nameof(ManualEventEditorViewModel.EndTime), name: Strings.Manual_End).Tap(e => { e.WidthChars = 6; e.Hexpand = false; })))),
            Ui.Check(editor, nameof(ManualEventEditorViewModel.Cancelled), Strings.Manual_Cancelled),
            Ui.Label(Strings.Manual_Invitations, "caption-heading").Start(),
            Ui.HBox(8, Ui.Button(Strings.Manual_InviteAll, editor.InviteAllCommand), Ui.Button(Strings.Manual_InviteNone, editor.InviteNoneCommand), Ui.Button(Strings.Manual_AcceptAll, editor.AcceptAllCommand)),
            Ui.Muted(Strings.Manual_NoMembersToInvite).HiddenWhen(editor, nameof(ManualEventEditorViewModel.HasInvitees)),
            invitees,
            Ui.HBox(8, Ui.Primary(Strings.Manual_Save, editor.SaveCommand), Ui.Button(Strings.Manual_Cancel, editor.CancelCommand)))
            .Css("card").Padded(14).VisibleWhen(editor, nameof(ManualEventEditorViewModel.IsOpen));

        var list = Ui.BoxedList(page, () => page.Events, ev =>
        {
            var row = Ui.Row(ev.Name, ev.InvitedText);
            row.AddSuffix(Ui.IconButton("document-edit-symbolic", Strings.Manual_Edit, editor.BeginEditCommand, ev, "flat"));
            row.AddSuffix(Ui.IconButton("user-trash-symbolic", Strings.Manual_Delete, page.AskDeleteEventCommand, ev, "flat"));
            return row;
        }, [nameof(ManualDataViewModel.Events)]);

        return Ui.VBox(10, Ui.Button(Strings.Manual_NewEvent, editor.BeginNewCommand).Start(), form,
            Ui.Muted(Strings.Manual_NoEvents).VisibleWhen(page, nameof(ManualDataViewModel.EventsEmpty)), list);
    }
}
