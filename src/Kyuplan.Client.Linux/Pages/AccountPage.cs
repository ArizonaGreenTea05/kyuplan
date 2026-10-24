using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.LinuxApp.Pages.Parts;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages;

internal static class AccountPage
{
    public static Gtk.Widget Build(UiContext context, AccountViewModel page)
    {
        // Who the user is: their roles.
        var rolesLabel = Ui.Label(page, nameof(AccountViewModel.RoleText));
        var rights = Ui.VBox(6, Ui.Label(Strings.Account_Rights, "heading").Start(), rolesLabel).Css("card").Padded(14);

        var names = Ui.VBox(8,
            Ui.Label(Strings.Account_NamesTitle, "heading").Start(),
            Problem(page, Ui.Field(Strings.Account_UserNameTitle, Ui.Entry(page, nameof(AccountViewModel.UserName), name: Strings.Account_UserNameTitle)), () => page.UserNameProblem, nameof(AccountViewModel.UserNameProblem)),
            Problem(page, Ui.Field(Strings.Account_DisplayName, Ui.Entry(page, nameof(AccountViewModel.DisplayName), name: Strings.Account_DisplayName), Strings.Account_DisplayNameHint), () => page.DisplayNameProblem, nameof(AccountViewModel.DisplayNameProblem)),
            Problem(page, Ui.Field(Strings.Account_Aliases, Ui.TextArea(page, nameof(AccountViewModel.AliasesText), 80, Strings.Account_Aliases), page.AliasesHint), () => page.AliasesProblem, nameof(AccountViewModel.AliasesProblem)),
            Ui.Primary(Strings.Account_SaveProfile, page.SaveProfileCommand).Start())
            .Css("card").Padded(14);

        var passwords = Ui.VBox(8,
            Ui.Label(Strings.Account_PasswordTitle, "heading").Start(),
            Ui.Field(Strings.Account_CurrentPassword, Ui.Password(page, nameof(AccountViewModel.CurrentPassword), Strings.Account_CurrentPassword)),
            Problem(page, Ui.Field(Strings.Account_NewPassword, Ui.Password(page, nameof(AccountViewModel.NewPassword), Strings.Account_NewPassword), page.PasswordHint), () => page.PasswordProblem, nameof(AccountViewModel.PasswordProblem)),
            Problem(page, Ui.Field(Strings.Account_ConfirmPassword, Ui.Password(page, nameof(AccountViewModel.ConfirmPassword), Strings.Account_ConfirmPassword)), () => page.ConfirmProblem, nameof(AccountViewModel.ConfirmProblem)),
            Ui.Primary(Strings.Account_ChangePassword, page.ChangePasswordCommand).Start())
            .Css("card").Padded(14);

        var notice = Ui.Label(page, nameof(AccountViewModel.NoticeText), "accent").VisibleWhen(page, nameof(AccountViewModel.HasNotice));

        var body = Ui.VBox(14,
            Widgets.Header(page.Title, Ui.Muted(Strings.Account_Lede)),
            Widgets.ErrorBanner(page), notice, rights,
            Ui.Flow(names, passwords),
            AboutMe(page.AboutMe));
        return Widgets.PageFrame(page, body, 1000);
    }

    /// <summary>A field with the problem the view model finds in it, shown under it.</summary>
    private static Gtk.Widget Problem(AccountViewModel page, Gtk.Widget field, Func<string?> problem, string property)
    {
        var text = Ui.Label(page, problem, [property], "error", "caption").VisibleWhen(page, () => !string.IsNullOrEmpty(problem()), property);
        return Ui.VBox(2, field, text);
    }

    /// <summary>What the user may see about themselves as a member: their ratings and the comments on them.</summary>
    private static Gtk.Widget AboutMe(AboutMeViewModel about)
    {
        var members = Ui.Choice(about, () => about.Members, m => m.Label, () => about.Selected, m => about.SelectCommand.Execute(m),
            [nameof(AboutMeViewModel.Members), nameof(AboutMeViewModel.Selected)], Strings.AboutMe_Member);
        members.Hexpand = false;

        var ratings = Widgets.Section(Strings.AboutMe_RatingsTitle,
            Ui.Label(about, nameof(AboutMeViewModel.RatingSummary), "dim-label"),
            Ui.Repeat(about, () => about.Ratings, r => Ui.HBox(10,
                Ui.Label(r.Glyphs).Tip(r.Tip), Ui.Label(r.ValueText, "numeric"), Ui.Label(r.UserText).Expand(), Ui.Muted(r.WhenText).Css("caption")),
                [nameof(AboutMeViewModel.Ratings)], 6))
            .VisibleWhen(about, nameof(AboutMeViewModel.CanViewRatings));

        var comments = CommentsPanel.Build(about.Comments)
            .VisibleWhen(about, nameof(AboutMeViewModel.CanViewComments));

        return Ui.VBox(12,
            Ui.Label(Strings.AboutMe_Title, "title-3").Start(),
            Ui.Muted(Strings.AboutMe_Lede),
            Ui.Label(about, nameof(ViewModelBase.ErrorMessage), "error").VisibleWhen(about, nameof(ViewModelBase.HasError)),
            Ui.Field(Strings.AboutMe_Member, members).VisibleWhen(about, nameof(AboutMeViewModel.HasSeveralMembers)),
            ratings, comments)
            .VisibleWhen(about, nameof(AboutMeViewModel.IsVisible));
    }
}
