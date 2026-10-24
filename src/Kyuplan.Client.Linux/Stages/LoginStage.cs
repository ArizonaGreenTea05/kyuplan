using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Stages;

/// <summary>The login screen: shown when there is no remembered login (or the API refused it).</summary>
internal static class LoginStage
{
    public static Gtk.Widget Build(UiContext context, LoginViewModel model, BrandingViewModel brand, string apiAddress, Action<string> changeServer)
    {
        var user = Ui.Entry(model, nameof(LoginViewModel.UserName), name: Strings.Login_UserName);
        user.InputPurpose = Gtk.InputPurpose.FreeForm;
        var password = Ui.Password(model, nameof(LoginViewModel.Password), Strings.Login_Password);
        user.OnActivate += (_, _) => password.GrabFocus();
        password.OnActivate += (_, _) => model.SignInCommand.Execute(null);

        var server = Ui.HBox(10,
            Ui.Label(apiAddress).Expand().Tip(apiAddress),
            Ui.Button(Strings.Login_ChangeServer, () => changeServer(apiAddress)));

        var remember = Ui.Check(model, nameof(LoginViewModel.RememberMe), Strings.Login_Remember);
        var buttons = Ui.HBox(10,
            Ui.Button(Strings.Login_Exit, () => context.Window.Close()),
            Ui.Primary(Strings.Login_SignIn, model.SignInCommand)).End();

        var content = Ui.VBox(12,
            BrandLogo.Create(brand, 140),
            Ui.Label(Strings.Login_Title, "title-1").Start(),
            Ui.Muted(Strings.Login_Lede),
            Ui.Field(Strings.Login_Server, server),
            Ui.Field(Strings.Login_UserName, user),
            Ui.Field(Strings.Login_Password, password),
            remember,
            Ui.Muted(Strings.Login_RememberHintKeyring).Css("caption"),
            Ui.ErrorLine(model),
            Ui.BusySpinner(model),
            buttons);

        // With a remembered user name only the password is missing.
        (model.UserName.Length == 0 ? (Gtk.Widget)user : password).GrabFocus();
        return Frame.Centered(content);
    }
}
