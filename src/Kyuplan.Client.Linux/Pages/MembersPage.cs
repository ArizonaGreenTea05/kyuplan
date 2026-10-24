using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.LinuxApp.Pages.Parts;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages;

internal static class MembersPage
{
    public static Gtk.Widget Build(UiContext context, MembersViewModel page)
    {
        var list = Ui.VBox(14,
            Widgets.Header(page.Title, Ui.Label(page, nameof(MembersViewModel.LedeText), "dim-label"),
                Ui.HBox(8,
                    Ui.Button(Strings.MemberPage_ViewHidden, page.ShowHiddenCommand).VisibleWhen(page, () => page.CanViewHidden && !page.ShowingHidden, nameof(MembersViewModel.CanViewHidden), nameof(MembersViewModel.ShowingHidden)),
                    Ui.Button(Strings.MemberPage_BackToMembers, page.ShowMembersCommand).VisibleWhen(page, nameof(MembersViewModel.ShowingHidden)))),
            Widgets.ErrorBanner(page, page.RetryCommand), Widgets.Notice(page),
            Form(page),
            Widgets.ConfirmBar(page, nameof(MembersViewModel.IsConfirmingHide), nameof(MembersViewModel.ConfirmHideText), page.ConfirmHideCommand, Strings.MemberPage_ConfirmHideYes, page.CancelHideCommand, Strings.MemberPage_Cancel),
            Ui.HBox(12,
                Ui.Search(page, nameof(MembersViewModel.SearchText), Strings.Stats_FindMember).Expand(),
                Ui.Check(page, nameof(MembersViewModel.OnlyWithoutBelt), Strings.MemberPage_OnlyWithoutBelt),
                Ui.Label(page, nameof(MembersViewModel.CountText), "dim-label")),
            Ui.Label(page, nameof(MembersViewModel.NobodyText), "dim-label").VisibleWhen(page, nameof(MembersViewModel.IsNobodyHere)),
            Ui.Label(Strings.MemberPage_NoneMatch, "dim-label").VisibleWhen(page, nameof(MembersViewModel.NoneMatch)),
            Ui.BoxedList(page, () => page.VisibleMembers, member => Member(page, member),
                [nameof(MembersViewModel.Members), nameof(MembersViewModel.SearchText), nameof(MembersViewModel.OnlyWithoutBelt)]));

        // The comments on one member take the place of the list.
        var comments = Ui.VBox(12,
            Ui.Button(Strings.Comments_Back, page.CloseCommentsCommand).Start(),
            CommentsPanel.Build(page.Comments));

        list.VisibleWhen(page, () => !page.IsShowingComments, nameof(MembersViewModel.IsShowingComments));
        comments.VisibleWhen(page, nameof(MembersViewModel.IsShowingComments));
        return Widgets.PageFrame(page, Ui.VBox(0, list, comments));
    }

    private static Gtk.Widget Member(MembersViewModel page, MemberRow member)
    {
        var details = string.Join(" · ", new[]
        {
            $"{Strings.MemberPage_ColStarted}: {member.StartedText}",
            $"{Strings.MemberPage_ColBirth}: {member.BirthText}",
            member.HasBelt ? $"{Strings.MemberPage_ColBeltSince}: {member.BeltSinceText}" : "",
            member.RegisteredText,
        }.Where(t => t.Length > 0));

        var row = Ui.Row(member.Name, details, null, member.HasBelt ? ColorClass.Belt(member.Belt.Color, member.Belt.SecondaryColor) : Ui.Caption(member.Belt.Label, "dim-label", "caption"));
        row.AddSuffix(Ui.Button(member.CommentsText, page.OpenCommentsCommand, member, "flat").VisibleWhen(page, () => page.ShowsCommentsColumn && page.MayViewComments(member), nameof(MembersViewModel.ShowsCommentsColumn)));
        row.AddSuffix(Ui.IconButton("document-edit-symbolic", Strings.MemberPage_Edit, page.BeginEditCommand, member, "flat").VisibleWhen(page, nameof(MembersViewModel.ShowsEditColumn)));
        row.AddSuffix(Ui.IconButton("edit-delete-symbolic", Strings.MemberPage_Hide, page.AskHideCommand, member, "flat").VisibleWhen(page, nameof(MembersViewModel.ShowsEditColumn)));
        row.AddSuffix(Ui.Button(Strings.MemberPage_Unhide, page.UnhideCommand, member).VisibleWhen(page, nameof(MembersViewModel.ShowsUnhideColumn)));
        return row;
    }

    private static Gtk.Widget Form(MembersViewModel page)
    {
        var belt = Ui.Choice(page, () => page.BeltOptions, o => o.Label, () => page.FormBelt, o => page.FormBelt = o,
            [nameof(MembersViewModel.BeltOptions), nameof(MembersViewModel.FormBelt)], Strings.Belts_ColBelt);

        Gtk.Widget Dated(string caption, string property, string hint, string? problemProperty, Func<string?> problem, System.Windows.Input.ICommand clear, string clearLabel)
        {
            var box = Ui.VBox(4, Ui.Label(caption, "caption-heading").Start(), DateField.WithClear(page, property, Strings.MemberPage_NotKnown, caption, clear, clearLabel), Ui.Muted(hint).Css("caption"));
            if (problemProperty is not null)
                box.Append(Ui.Label(page, problem, [problemProperty], "error", "caption").VisibleWhen(page, () => problem() is not null, problemProperty));
            return box.Expand();
        }

        var form = Ui.VBox(12,
            Ui.Label(page, nameof(MembersViewModel.FormTitle), "heading").Start(),
            Ui.Field(Strings.Belts_ColBelt, belt),
            Ui.Flow(
                Dated(Strings.MemberPage_ColStarted, nameof(MembersViewModel.FormStartedAt), Strings.MemberPage_FormStartedHint, nameof(MembersViewModel.StartedAtProblem), () => page.StartedAtProblem, page.ClearStartedAtCommand, Strings.MemberPage_ClearStarted),
                Dated(Strings.MemberPage_ColBirth, nameof(MembersViewModel.FormBirthDate), Strings.MemberPage_FormBirthHint, nameof(MembersViewModel.BirthDateProblem), () => page.BirthDateProblem, page.ClearBirthDateCommand, Strings.MemberPage_ClearBirth),
                Dated(Strings.MemberPage_ColBeltSince, nameof(MembersViewModel.FormBeltSince), Strings.MemberPage_FormBeltSinceHint, nameof(MembersViewModel.BeltSinceProblem), () => page.BeltSinceProblem, page.ClearBeltSinceCommand, Strings.MemberPage_ClearBirth)
                    .EnabledWhen(page, nameof(MembersViewModel.CanSetBeltSince))),
            Ui.Muted(Strings.MemberPage_BeltSinceNeedsBelt).Css("caption").VisibleWhen(page, () => !page.CanSetBeltSince, nameof(MembersViewModel.CanSetBeltSince)),
            Ui.HBox(8, Ui.Primary(Strings.MemberPage_Save, page.SaveCommand), Ui.Button(Strings.MemberPage_Cancel, page.CancelFormCommand)));
        return form.Css("card").Padded(14).VisibleWhen(page, nameof(MembersViewModel.IsFormOpen));
    }
}
