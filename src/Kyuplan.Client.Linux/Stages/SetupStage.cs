using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.LinuxApp.Pages.Parts;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Stages;

/// <summary>Shown instead of the application while the API has no user: the first administrator and the settings.</summary>
internal static class SetupStage
{
    public static Gtk.Widget Build(UiContext context, SetupViewModel setup)
    {
        Gtk.Widget Problem(Gtk.Widget field, Func<string?> problem, string property, Func<bool>? when = null) => Ui.VBox(2, field,
            Ui.Label(setup, problem, [property, nameof(SetupViewModel.AdminPassword)], "error", "caption")
                .VisibleWhen(setup, () => !string.IsNullOrEmpty(problem()) && (when?.Invoke() ?? true), property, nameof(SetupViewModel.AdminPassword)));

        var password = Ui.Password(setup, nameof(SetupViewModel.AdminPassword), Strings.Setup_AdminPassword);
        var repeat = Ui.Password(setup, nameof(SetupViewModel.AdminPasswordRepeat), Strings.Setup_AdminPasswordRepeat);
        repeat.OnActivate += (_, _) => setup.FinishCommand.Execute(null);

        var admin = Widgets.Section(Strings.Setup_AdminTitle,
            Problem(Ui.Field(Strings.Setup_AdminUserName, Ui.Entry(setup, nameof(SetupViewModel.AdminUserName), name: Strings.Setup_AdminUserName)), () => setup.AdminUserNameProblem, nameof(SetupViewModel.AdminUserNameProblem)),
            Ui.Flow(
                Problem(Ui.Field(Strings.Setup_AdminPassword, password), () => setup.AdminPasswordProblem, nameof(SetupViewModel.AdminPasswordProblem), () => setup.AdminPassword.Length > 0),
                Problem(Ui.Field(Strings.Setup_AdminPasswordRepeat, repeat), () => setup.RepeatProblem, nameof(SetupViewModel.RepeatProblem))));

        var path = Ui.Entry(setup, nameof(SetupViewModel.SettingsFilePath), name: Strings.Setup_SettingsFilePath).EnabledWhen(setup, () => !setup.SettingsFileLocked && !setup.IsBusy, nameof(SetupViewModel.SettingsFileLocked), nameof(SetupViewModel.IsBusy));
        var file = Widgets.Section(Strings.Setup_SettingsFileTitle,
            Ui.Muted(Strings.Setup_SettingsFileHint).Css("caption"),
            Problem(Ui.Field(Strings.Setup_SettingsFilePath, path), () => setup.SettingsFilePathProblem, nameof(SetupViewModel.SettingsFilePathProblem), () => !setup.SettingsFileLocked),
            Ui.Muted(Strings.Setup_SettingsFileLocked).Css("caption").VisibleWhen(setup, nameof(SetupViewModel.SettingsFileLocked)));

        var progress = Ui.Label(setup, () => setup.IsDone ? Strings.Setup_Done : Strings.Setup_Restarting, [nameof(SetupViewModel.IsDone), nameof(SetupViewModel.IsRestarting)], "accent")
            .VisibleWhen(setup, () => setup.IsRestarting || setup.IsDone, nameof(SetupViewModel.IsDone), nameof(SetupViewModel.IsRestarting));

        var form = Ui.VBox(14,
            admin, file, SettingsFormView.Build(setup.Form),
            Ui.Primary(Strings.Setup_Finish, setup.FinishCommand).Start())
            .HiddenWhen(setup, nameof(SetupViewModel.IsRestarting)).VisibleWhen(setup, () => !setup.IsRestarting && !setup.IsDone, nameof(SetupViewModel.IsRestarting), nameof(SetupViewModel.IsDone));

        var content = Ui.VBox(14,
            Ui.Label(Strings.Setup_Title, "title-1").Start(),
            Ui.Muted(Strings.Setup_Lede),
            Ui.ErrorLine(setup), Ui.BusySpinner(setup), progress, form);

        var clamp = Adw.Clamp.New();
        clamp.MaximumSize = 820;
        clamp.TighteningThreshold = 700;
        clamp.SetChild(content.Margin(24, 18));
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetChild(clamp);
        scroll.Vexpand = true;
        scroll.HscrollbarPolicy = Gtk.PolicyType.Never;
        var view = Adw.ToolbarView.New();
        var header = Adw.HeaderBar.New();
        header.ShowTitle = false;
        view.AddTopBar(header);
        view.SetContent(scroll);
        return view;
    }
}
