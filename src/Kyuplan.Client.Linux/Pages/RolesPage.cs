using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.LinuxApp.Pages.Parts;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages;

internal static class RolesPage
{
    public static Gtk.Widget Build(UiContext context, RolesViewModel page)
    {
        var list = Ui.BoxedList(page, () => page.Roles, role =>
        {
            var row = Ui.Row(role.Name, $"{role.PermissionsText} · {role.UsersText}");
            if (role.IsSystem) row.AddSuffix(Ui.Chip(Strings.Roles_SystemHint).Tip(Strings.Roles_SystemHint));
            row.AddSuffix(Ui.IconButton("document-edit-symbolic", Strings.Roles_Edit, page.BeginEditCommand, role, "flat").VisibleWhen(page, nameof(RolesViewModel.CanEdit)));
            var delete = Ui.IconButton("user-trash-symbolic", Strings.Roles_Delete, page.AskDeleteCommand, role, "flat");
            delete.Visible = page.CanDelete(role) || !role.IsSystem;
            row.AddSuffix(delete);
            return row;
        }, [nameof(RolesViewModel.Roles)]);

        var save = Ui.Primary("", page.SaveCommand);
        Bind.Watch(save, page, () => save.SetLabel(page.IsCreating ? Strings.Roles_Create : Strings.Roles_Save), nameof(RolesViewModel.IsCreating));
        var form = Ui.VBox(12,
            Ui.Label(page, nameof(RolesViewModel.FormTitle), "heading").Start(),
            Ui.Field(Strings.Roles_FormName, Ui.Entry(page, nameof(RolesViewModel.FormName), name: Strings.Roles_FormName)),
            Ui.Label(Strings.Roles_FormPermissions, "caption-heading").Start(),
            Ui.Muted(Strings.Roles_FormPermissionsHint).Css("caption"),
            PermissionPicker.Build(page.Picker),
            Ui.HBox(8, save, Ui.Button(Strings.Roles_Cancel, page.CancelFormCommand)))
            .Css("card").Padded(14).VisibleWhen(page, nameof(RolesViewModel.IsFormOpen));

        var body = Ui.VBox(14,
            Widgets.Header(page.Title, Ui.Muted(Strings.Roles_Lede), Ui.Primary(Strings.Roles_New, page.BeginCreateCommand).VisibleWhen(page, nameof(RolesViewModel.CanEdit))),
            Widgets.ErrorBanner(page, page.RetryCommand), Widgets.Notice(page),
            form,
            Widgets.ConfirmBar(page, nameof(RolesViewModel.IsConfirmingDelete), nameof(RolesViewModel.ConfirmText), page.ConfirmDeleteCommand, Strings.Roles_ConfirmYes, page.CancelDeleteCommand, Strings.Roles_Cancel),
            list);
        return Widgets.PageFrame(page, body, 1000);
    }
}
