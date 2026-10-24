using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.LinuxApp.Pages.Parts;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.LinuxApp.Pages;

/// <summary>The calendar of the selected group: day, week or month, and the page of one event with its answers, comments and ratings.</summary>
internal static class CalendarPage
{
    public static Gtk.Widget Build(UiContext context, CalendarViewModel page)
    {
        var overview = Ui.VBox(12,
            Widgets.Header(page.Title, Ui.Muted(Strings.Calendar_Lede)),
            Widgets.ErrorBanner(page, page.RetryCommand),
            Toolbar(page),
            Ui.Label(Strings.Calendar_NoGroup, "dim-label").VisibleWhen(page, nameof(CalendarViewModel.NoGroup)),
            Ui.Label(Strings.Calendar_NoEvents, "dim-label").VisibleWhen(page, nameof(CalendarViewModel.IsEmpty)),
            Grid(page).VisibleWhen(page, nameof(CalendarViewModel.UsesGrid)),
            DayList(page).VisibleWhen(page, nameof(CalendarViewModel.IsDay)));

        var detail = Ui.VBox(12, Ui.Button(Strings.Calendar_Back, page.CloseEventCommand).Start(), Detail(page));
        overview.VisibleWhen(page, () => !page.HasDetail, nameof(CalendarViewModel.HasDetail));
        detail.VisibleWhen(page, nameof(CalendarViewModel.HasDetail));
        return Widgets.PageFrame(page, Ui.VBox(0, overview, detail), 1300);
    }

    // ---- overview ----------------------------------------------------------------------------------------------------------

    private static Gtk.Widget Toolbar(CalendarViewModel page)
    {
        Gtk.Widget View(string text, string view)
        {
            var button = Gtk.ToggleButton.NewWithLabel(text);
            Bind.Watch(button, page, () => button.Active = page.View == view, nameof(CalendarViewModel.View));
            button.OnToggled += (_, _) =>
            {
                if (button.Active && page.View != view) page.ShowViewCommand.Execute(view);
            };
            return button;
        }

        var views = Ui.HBox(0, View(Strings.Calendar_Day, CalendarViews.Day), View(Strings.Calendar_Week, CalendarViews.Week), View(Strings.Calendar_Month, CalendarViews.Month)).Css("linked");
        var moves = Ui.HBox(0,
            Ui.IconButton("go-previous-symbolic", Strings.Calendar_Previous, page.PreviousCommand),
            Ui.Button(Strings.Calendar_Today, page.TodayCommand),
            Ui.IconButton("go-next-symbolic", Strings.Calendar_Next, page.NextCommand)).Css("linked");
        return Ui.HBox(12, moves, Ui.Label(page, nameof(CalendarViewModel.PeriodText), "title-3").Expand(), views);
    }

    private static Gtk.Widget Grid(CalendarViewModel page)
    {
        var grid = Gtk.Grid.New();
        grid.ColumnSpacing = grid.RowSpacing = 6;
        grid.ColumnHomogeneous = true;

        void Rebuild()
        {
            while (grid.GetFirstChild() is { } child) grid.Remove(child);
            for (var c = 0; c < page.WeekdayNames.Count; c++) grid.Attach(Ui.Label(page.WeekdayNames[c], "caption-heading", "dim-label").Start(), c, 0, 1, 1);

            var index = 0;
            foreach (var day in page.Days)
            {
                var cell = Ui.VBox(3, Ui.Label(page.IsMonth ? day.Number.ToString() : day.Label, "caption-heading").Start());
                cell.AddCssClass("calendar-cell");
                if (day.IsToday) cell.AddCssClass("today");
                if (day.IsOutside) cell.AddCssClass("other-month");
                foreach (var row in day.Events) cell.Append(EventChip(page, row));
                cell.Tip(day.FullText);
                cell.SetSizeRequest(0, page.IsMonth ? 96 : 220);
                grid.Attach(cell, index % 7, 1 + index / 7, 1, 1);
                index++;
            }
        }

        Bind.Watch(grid, page, Rebuild, nameof(CalendarViewModel.Days), nameof(CalendarViewModel.View));
        return grid;
    }

    /// <summary>One event in a day's cell: its time and name, opening the event.</summary>
    private static Gtk.Widget EventChip(CalendarViewModel page, CalendarEventRow row)
    {
        var text = Ui.Label($"{row.TimeText.Split(' ')[0]}  {row.Name}", "caption");
        text.Wrap = false;
        text.Ellipsize = Pango.EllipsizeMode.End;
        var button = Gtk.Button.New();
        button.SetChild(text);
        button.AddCssClass("flat");
        if (row.IsCancelled) text.AddCssClass("dim-label");
        button.Tip($"{row.Name}\n{row.TipText}\n{row.CountsText}");
        button.OnClicked += (_, _) => page.OpenEventCommand.Execute(row);
        return button;
    }

    private static Gtk.Widget DayList(CalendarViewModel page) =>
        Ui.BoxedList(page, () => page.Events, row =>
        {
            var title = row.IsCancelled ? $"{row.Name} ({Strings.Calendar_Cancelled})" : row.Name;
            var r = Ui.Row(title, $"{row.WhenText}\n{row.CountsText} · {row.InvitedText}" + (row.HasTrainers ? $"\n{row.TrainersText}" : ""));
            r.Activatable = true;
            r.OnActivated += (_, _) => page.OpenEventCommand.Execute(row);
            return r;
        }, [nameof(CalendarViewModel.Events)]);

    // ---- one event ---------------------------------------------------------------------------------------------------------

    private static Gtk.Widget Detail(CalendarViewModel page)
    {
        var filters = Ui.Repeat(page, () => page.FilterOptions, option =>
        {
            var button = Gtk.ToggleButton.NewWithLabel(option.Text);
            Bind.Watch(button, option, () =>
            {
                button.Active = option.IsSelected;
                button.SetLabel(option.Text);
            });
            button.OnClicked += (_, _) => page.SetFilterCommand.Execute(option);
            return button;
        }, [nameof(CalendarViewModel.FilterOptions)], 0, Gtk.Orientation.Horizontal).Css("linked");

        var head = Ui.VBox(4,
            Ui.Label(page, () => page.Detail?.Name, [nameof(CalendarViewModel.Detail)], "title-2"),
            Ui.Label(page, () => page.Detail is { } d ? $"{d.WhenText} · {d.CountsText}" : "", [nameof(CalendarViewModel.Detail)], "dim-label"),
            Ui.Label(page, () => page.Detail?.TrainersText, [nameof(CalendarViewModel.Detail)], "dim-label"));

        var attendees = Ui.Repeat(page, () => page.VisibleAttendees, a => Attendee(page, a), [nameof(CalendarViewModel.VisibleAttendees), nameof(CalendarViewModel.Attendees)], 10);
        return Ui.VBox(12,
            head,
            Ui.Muted(Strings.Calendar_NotSynced).Css("caption"),
            Ui.Label(Strings.Rating_NotStarted, "warn-text").VisibleWhen(page, nameof(CalendarViewModel.ShowNotStarted)),
            Ui.Label(Strings.Calendar_Attendees, "heading").Start(),
            Ui.HBox(8, Ui.Caption(Strings.Calendar_FilterLabel, "dim-label"), filters),
            Ui.Muted(Strings.Calendar_NoAttendees).HiddenWhen(page, nameof(CalendarViewModel.HasAttendees)),
            Ui.Muted(Strings.Calendar_NoMatch).VisibleWhen(page, nameof(CalendarViewModel.NoMatch)),
            attendees);
    }

    private static Gtk.Widget Attendee(CalendarViewModel page, EventAttendeeRow attendee)
    {
        var head = Ui.HBox(10, Ui.Label(attendee.Name, "heading").Expand(), Ui.Chip(attendee.AnswerText));

        var content = Ui.VBox(8, head);
        if (attendee.ShowRating && page.ShowRatings) content.Append(Rating(page, attendee));
        if (attendee.HasComments)
            foreach (var comment in attendee.Comments) content.Append(Comment(page, attendee, comment));
        if (attendee.CanComment && attendee.CanCompose) content.Append(Composer(page, attendee));
        else if (attendee.CanComment && attendee.CanReply) content.Append(Composer(page, attendee));
        return content.Css("card").Padded(12);
    }

    /// <summary>The stars the user gave, a button per choice (and "clear"), and what the others gave.</summary>
    private static Gtk.Widget Rating(CalendarViewModel page, EventAttendeeRow attendee)
    {
        var choices = Ui.HBox(2);
        if (attendee.CanRate)
        {
            foreach (var choice in attendee.Choices)
            {
                var button = Gtk.Button.NewWithLabel(choice.Glyph);
                button.AddCssClass("flat");
                button.Tip(choice.Tip);
                button.OnClicked += (_, _) => page.RateCommand.Execute(choice);
                Bind.Watch(button, choice, () => button.SetLabel(choice.Glyph), string.Empty);
                choices.Append(button);
            }

            var clear = Gtk.Button.NewWithLabel(attendee.ClearChoice.Text);
            clear.AddCssClass("flat");
            clear.Tip(attendee.ClearChoice.Tip);
            clear.OnClicked += (_, _) => page.RateCommand.Execute(attendee.ClearChoice);
            choices.Append(clear);
        }

        var box = Ui.VBox(4, Ui.HBox(8, Ui.Caption(Strings.Rating_YourChoice, "dim-label"), choices).VisibleWhen(page, nameof(CalendarViewModel.CanRate)));
        if (attendee.HasRatings && attendee.MayViewRatings)
        {
            box.Append(Ui.Muted(attendee.RatingSummary).Css("caption"));
            foreach (var rating in attendee.Ratings)
                box.Append(Ui.HBox(8, Ui.Label(rating.Glyphs).Tip(rating.Tip), Ui.Label(rating.UserText).Expand(), Ui.Muted(rating.WhenText).Css("caption")));
        }

        return box;
    }

    private static Gtk.Widget Comment(CalendarViewModel page, EventAttendeeRow attendee, CommentRow row)
    {
        var actions = Ui.HBox(6,
            Ui.Button(Strings.Comments_Reply, attendee.ReplyCommand, row).VisibleWhen(page, nameof(CalendarViewModel.CanWriteComments)),
            Ui.Button(Strings.Comments_Edit, attendee.EditCommand, row).VisibleWhen(page, nameof(CalendarViewModel.CanEditComments)));
        var delete = Ui.Button(Strings.Comments_Delete, page.AskDeleteCommand, row);
        delete.Visible = row.CanDelete;
        actions.Append(delete);

        var confirm = Ui.HBox(8, Ui.Label(row.ConfirmText).Expand(),
            Ui.Button(Strings.Comments_ConfirmYes, page.ConfirmDeleteCommand, row, "destructive-action"),
            Ui.Button(Strings.Comments_Cancel, page.CancelDeleteCommand, row)).Css("banner-problem").VisibleWhen(row, nameof(CommentRow.IsConfirmingDelete));

        var box = Ui.VBox(4,
            Ui.HBox(10, Ui.Label(row.AuthorName, "heading"), Ui.Muted(row.CreatedText).Css("caption")),
            RichText.Label(row.Spans), actions, confirm);
        box.MarginStart = (int)row.IndentWidth;
        return box;
    }

    private static Gtk.Widget Composer(CalendarViewModel page, EventAttendeeRow attendee)
    {
        var post = Ui.Primary("", page.PostCommentCommand, attendee);
        Bind.Watch(post, attendee, () => post.SetLabel(attendee.PostText), nameof(EventAttendeeRow.IsEditing), nameof(EventAttendeeRow.IsReplying));
        return Ui.VBox(6,
            Ui.Label(attendee, nameof(EventAttendeeRow.ComposingText), "caption-heading").Start().VisibleWhen(attendee, nameof(EventAttendeeRow.IsComposing)),
            Ui.TextArea(attendee, nameof(EventAttendeeRow.Draft), 60, Strings.Comments_Placeholder),
            Ui.HBox(8, post, Ui.Button(Strings.Comments_Cancel, attendee.CancelReplyCommand).VisibleWhen(attendee, nameof(EventAttendeeRow.IsComposing))));
    }
}
