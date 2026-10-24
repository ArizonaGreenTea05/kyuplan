using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.LinuxApp.Pages.Parts;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages;

internal static class UsersPage
{
    public static Gtk.Widget Build(UiContext context, UserAdminViewModel page)
    {
        var list = Ui.BoxedList(page, () => page.Users, user =>
        {
            var subtitle = string.Join(" · ", new[] { user.DisplayName, user.AliasesText, user.RolesText, user.MembersText, user.StatusText }.Where(t => !string.IsNullOrWhiteSpace(t)));
            var row = Ui.Row(user.IsCurrent ? $"{user.UserName} ({Strings.Users_SignedIn})" : user.UserName, subtitle);
            row.AddSuffix(Ui.IconButton("document-edit-symbolic", Strings.Users_Edit, page.BeginEditCommand, user, "flat").VisibleWhen(page, nameof(UserAdminViewModel.CanEdit)));
            var delete = Ui.IconButton("user-trash-symbolic", user.IsCurrent ? Strings.Users_DeleteSelfHint : Strings.Users_Delete, page.AskDeleteCommand, user, "flat");
            delete.Sensitive = page.CanDelete(user);
            delete.VisibleWhen(page, nameof(UserAdminViewModel.CanDeleteUsers));
            row.AddSuffix(delete);
            return row;
        }, [nameof(UserAdminViewModel.Users)]);

        var body = Ui.VBox(14,
            Widgets.Header(page.Title, Ui.Muted(Strings.Users_Lede), Ui.Primary(Strings.Users_New, page.BeginCreateCommand).VisibleWhen(page, nameof(UserAdminViewModel.CanEdit))),
            Widgets.ErrorBanner(page, page.RetryCommand), Widgets.Notice(page),
            Form(page),
            Widgets.ConfirmBar(page, nameof(UserAdminViewModel.IsConfirmingDelete), nameof(UserAdminViewModel.ConfirmText), page.ConfirmDeleteCommand, Strings.Users_ConfirmYes, page.CancelDeleteCommand, Strings.Users_Cancel),
            list);
        return Widgets.PageFrame(page, body, 1000);
    }

    private static Gtk.Widget Problem(UserAdminViewModel page, Gtk.Widget field, Func<string?> problem, string property) =>
        Ui.VBox(2, field, Ui.Label(page, problem, [property], "error", "caption").VisibleWhen(page, () => !string.IsNullOrEmpty(problem()), property));

    private static Gtk.Widget Form(UserAdminViewModel page)
    {
        var save = Ui.Primary("", page.SaveCommand);
        Bind.Watch(save, page, () => save.SetLabel(page.IsCreating ? Strings.Users_Create : Strings.Users_Save), nameof(UserAdminViewModel.IsCreating));

        var roles = Ui.Repeat(page, () => page.RoleChoices, role => Ui.Check(role, nameof(RoleChoice.IsSelected), role.Name).EnabledWhen(role, nameof(RoleChoice.IsEnabled)),
            [nameof(UserAdminViewModel.RoleChoices)], 4);
        var members = Ui.VBox(6,
            Ui.Label(Strings.Users_FormMembers, "caption-heading").Start(),
            Ui.Muted(Strings.Users_FormMembersHint).Css("caption"),
            Ui.Search(page, nameof(UserAdminViewModel.MemberFilter), Strings.Users_FormMembersFilter),
            Ui.Repeat(page, () => page.VisibleMemberChoices.Take(60), m => Ui.Check(m, nameof(MemberChoice.IsSelected), m.Label),
                [nameof(UserAdminViewModel.VisibleMemberChoices), nameof(UserAdminViewModel.MemberFilter), nameof(UserAdminViewModel.MemberChoices)], 2))
            .VisibleWhen(page, nameof(UserAdminViewModel.HasMemberChoices));

        var form = Ui.VBox(12,
            Ui.Label(page, nameof(UserAdminViewModel.FormTitle), "heading").Start(),
            Ui.Flow(
                Problem(page, Ui.Field(Strings.Users_FormUserName, Ui.Entry(page, nameof(UserAdminViewModel.FormUserName), name: Strings.Users_FormUserName)), () => page.UserNameProblem, nameof(UserAdminViewModel.UserNameProblem)),
                Problem(page, Ui.Field(Strings.Account_DisplayName, Ui.Entry(page, nameof(UserAdminViewModel.FormDisplayName), name: Strings.Account_DisplayName), Strings.Account_DisplayNameHint), () => page.DisplayNameProblem, nameof(UserAdminViewModel.DisplayNameProblem))),
            Problem(page, Ui.Field(Strings.Account_Aliases, Ui.TextArea(page, nameof(UserAdminViewModel.FormAliases), 70, Strings.Account_Aliases), page.AliasesHint), () => page.AliasesProblem, nameof(UserAdminViewModel.AliasesProblem)),
            Problem(page, Ui.Field(Strings.Users_FormPassword, Ui.Password(page, nameof(UserAdminViewModel.FormPassword), Strings.Users_FormPassword), page.PasswordHint), () => page.PasswordProblem, nameof(UserAdminViewModel.PasswordProblem)),
            Ui.VBox(6, Ui.Label(Strings.Users_FormRoles, "caption-heading").Start(), Ui.Muted(Strings.Users_FormRolesHint).Css("caption"), roles,
                Ui.Muted(Strings.Users_FormOnlyAdminHint).Css("caption").VisibleWhen(page, nameof(UserAdminViewModel.HasLockedRole))),
            members,
            Ui.VBox(6, Ui.Label(Strings.Users_FormPermissions, "caption-heading").Start(), Ui.Muted(Strings.Users_FormPermissionsHint).Css("caption"), PermissionPicker.Build(page.Picker))
                .VisibleWhen(page, nameof(UserAdminViewModel.CanGrant)),
            Ui.HBox(8, save, Ui.Button(Strings.Users_Cancel, page.CancelFormCommand)));
        return form.Css("card").Padded(14).VisibleWhen(page, nameof(UserAdminViewModel.IsFormOpen));
    }
}
