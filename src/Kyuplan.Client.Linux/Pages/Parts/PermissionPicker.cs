using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages.Parts;

/// <summary>The permissions of the program in groups, each a tick box (<see cref="PermissionPickerViewModel"/>); used for roles and for the permissions of one user.</summary>
internal static class PermissionPicker
{
    public static Gtk.Widget Build(PermissionPickerViewModel picker)
    {
        var columns = Ui.Flow();
        columns.Homogeneous = true;
        foreach (var group in picker.Groups)
        {
            var box = Ui.VBox(2, Ui.Label(group.Label, "caption-heading").Start());
            foreach (var permission in group.Permissions) box.Append(Ui.Check(permission, nameof(PermissionRow.IsSelected), permission.Label));
            columns.Append(box.Padded(8));
        }

        return columns.EnabledWhen(picker, nameof(PermissionPickerViewModel.IsEnabled));
    }
}
