using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.LinuxApp.Pages;

/// <summary>The calendars that other apps subscribe to (ICS addresses) and what goes into each.</summary>
internal static class CalendarFeedsPage
{
    public static Gtk.Widget Build(UiContext context, CalendarFeedsViewModel page)
    {
        var list = Ui.BoxedList(page, () => page.Feeds, feed =>
        {
            var row = Ui.Row(feed.Name, $"{feed.SourcesText}\n{feed.Url}");
            row.AddSuffix(Ui.IconButton("edit-copy-symbolic", Strings.CalendarFeeds_ColAddress, () =>
            {
                Gdk.Display.GetDefault()?.GetClipboard().SetText(feed.Url);
                context.Toast(Strings.CalendarFeeds_ColAddress);
            }, "flat"));
            row.AddSuffix(Ui.IconButton("document-edit-symbolic", Strings.CalendarFeeds_Edit, page.BeginEditCommand, feed, "flat"));
            row.AddSuffix(Ui.IconButton("view-refresh-symbolic", Strings.CalendarFeeds_NewAddress, page.AskRegenerateCommand, feed, "flat"));
            row.AddSuffix(Ui.IconButton("user-trash-symbolic", Strings.CalendarFeeds_Delete, page.AskDeleteCommand, feed, "flat"));
            return row;
        }, [nameof(CalendarFeedsViewModel.Feeds)]);

        var body = Ui.VBox(14,
            Widgets.Header(page.Title, Ui.Muted(Strings.CalendarFeeds_Lede), Ui.Primary(Strings.CalendarFeeds_New, page.BeginCreateCommand)),
            Ui.Muted(Strings.CalendarFeeds_AddressHint).Css("caption"),
            Widgets.ErrorBanner(page, page.RetryCommand), Widgets.Notice(page),
            Editor(page.Editor),
            Widgets.ConfirmBar(page, nameof(CalendarFeedsViewModel.IsConfirming), nameof(CalendarFeedsViewModel.ConfirmText), page.ConfirmCommand, Strings.CalendarFeeds_ConfirmYes, page.CancelConfirmCommand, Strings.CalendarFeeds_Cancel),
            Ui.Label(Strings.CalendarFeeds_Empty, "dim-label").VisibleWhen(page, nameof(CalendarFeedsViewModel.IsEmpty)),
            list);
        return Widgets.PageFrame(page, body, 1000);
    }

    private static Gtk.Widget Editor(CalendarFeedEditorViewModel editor)
    {
        var save = Ui.Primary("", editor.SaveCommand);
        Bind.Watch(save, editor, () => save.SetLabel(editor.IsCreating ? Strings.CalendarFeeds_Create : Strings.CalendarFeeds_Save), nameof(CalendarFeedEditorViewModel.IsCreating));

        var sources = Ui.Repeat(editor, () => editor.Sources, source => Source(editor, source), [nameof(CalendarFeedEditorViewModel.Sources)], 10);
        return Ui.VBox(12,
            Ui.Label(editor, nameof(CalendarFeedEditorViewModel.Title), "heading").Start(),
            Ui.Field(Strings.CalendarFeeds_FormName, Ui.Entry(editor, nameof(CalendarFeedEditorViewModel.Name), name: Strings.CalendarFeeds_FormName)),
            sources,
            Ui.Button(Strings.CalendarFeeds_AddSource, editor.AddSourceCommand).Start().VisibleWhen(editor, nameof(CalendarFeedEditorViewModel.CanAddSource)),
            Ui.Label(editor, nameof(ViewModelBase.ErrorMessage), "error").VisibleWhen(editor, nameof(ViewModelBase.HasError)),
            Ui.HBox(8, save, Ui.Button(Strings.CalendarFeeds_Cancel, editor.CancelCommand)))
            .Css("card").Padded(14).VisibleWhen(editor, nameof(CalendarFeedEditorViewModel.IsOpen));
    }

    /// <summary>One source of a feed: what kind of entries it adds (events, or the birthdays and similar of people) and which.</summary>
    private static Gtk.Widget Source(CalendarFeedEditorViewModel editor, FeedSourceEditor source)
    {
        var kind = Ui.Choice(source, () => FeedSourceEditor.KindChoices, c => c.Text, () => FeedSourceEditor.KindChoices.FirstOrDefault(c => c.Value == source.Kind),
            c => source.Kind = c.Value, [nameof(FeedSourceEditor.Kind)], Strings.CalendarFeeds_FieldKind);
        var group = Ui.Choice(source, () => source.Groups, g => g.Name, () => source.Groups.FirstOrDefault(g => g.Id == source.GroupId), g => source.GroupId = g.Id,
            [nameof(FeedSourceEditor.GroupId)], Strings.CalendarFeeds_FieldGroup);
        var invitation = Ui.Choice(source, () => FeedSourceEditor.InvitationChoices, c => c.Text, () => FeedSourceEditor.InvitationChoices.FirstOrDefault(c => c.Value == source.Invitation),
            c => source.Invitation = c.Value, [nameof(FeedSourceEditor.Invitation)], Strings.CalendarFeeds_FieldInvitation);

        var events = Ui.VBox(8,
            Ui.Field(Strings.CalendarFeeds_FieldInvitation, invitation),
            Ui.Flow(
                Ui.Field(Strings.CalendarFeeds_FieldContains, Ui.Entry(source, nameof(FeedSourceEditor.NameContains), name: Strings.CalendarFeeds_FieldContains)),
                Ui.Field(Strings.CalendarFeeds_FieldExcludes, Ui.Entry(source, nameof(FeedSourceEditor.NameExcludes), name: Strings.CalendarFeeds_FieldExcludes))),
            Ui.Check(source, nameof(FeedSourceEditor.OnlyExams), Strings.CalendarFeeds_FieldOnlyExams),
            Ui.Check(source, nameof(FeedSourceEditor.IncludeCancelled), Strings.CalendarFeeds_FieldCancelled))
            .VisibleWhen(source, nameof(FeedSourceEditor.IsEvents));

        var people = Ui.VBox(8,
            Ui.Muted(Strings.CalendarFeeds_PeopleHint).Css("caption"),
            Ui.Label(Strings.CalendarFeeds_FieldSubGroups, "caption-heading").Start(),
            Ui.Repeat(source, () => source.SubGroups, Option, [nameof(FeedSourceEditor.SubGroups)], 4),
            Ui.Label(Strings.CalendarFeeds_FieldMembers, "caption-heading").Start(),
            Ui.Repeat(source, () => source.Members.Take(80), Option, [nameof(FeedSourceEditor.Members)], 4))
            .HiddenWhen(source, nameof(FeedSourceEditor.IsEvents));

        return Ui.VBox(8,
            Ui.HBox(8, Ui.Field(Strings.CalendarFeeds_FieldKind, kind).Expand(), Ui.Field(Strings.CalendarFeeds_FieldGroup, group).Expand(),
                Ui.Button(Strings.CalendarFeeds_RemoveSource, editor.RemoveSourceCommand, source).Tap(b => b.Valign = Gtk.Align.End)),
            events, people,
            Ui.Field(Strings.CalendarFeeds_FieldLabel, Ui.Entry(source, nameof(FeedSourceEditor.Label), name: Strings.CalendarFeeds_FieldLabel)))
            .Css("card").Padded(10);
    }

    /// <summary>A subgroup or a member that can be ticked, with the text that stands for it in the calendar.</summary>
    private static Gtk.Widget Option(FeedOption option) =>
        Ui.HBox(8, Ui.Check(option, nameof(FeedOption.IsSelected), option.Name).Expand(),
            Ui.Entry(option, nameof(FeedOption.Label), Strings.CalendarFeeds_SubGroupLabel, option.Name).Tap(e =>
            {
                e.Hexpand = false;
                e.WidthChars = 18;
            }));
}
