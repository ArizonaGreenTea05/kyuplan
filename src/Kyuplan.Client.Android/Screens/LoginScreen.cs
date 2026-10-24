using Android.Views;
using Android.Widget;
using Kyuplan.Client.AndroidApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.AndroidApp.Screens;

/// <summary>The sign-in: user name and password, and whether to remember them. Shows the name of the instance and the server it talks to.</summary>
public sealed class LoginScreen : Screen
{
    private readonly LoginViewModel _vm;
    private readonly ScrollView _root;
    private readonly TextView _error;
    private readonly TextView _signIn;
    private readonly ProgressBar _busy;

    public LoginScreen(Ui ui, LoginViewModel vm, string productName, string server, Action changeServer) : base(ui)
    {
        _vm = vm;
        Observe(vm);

        var user = ui.Input(Strings.Login_UserName, vm.UserName, text => vm.UserName = text);
        user.SetSingleLine(true);
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) user.SetAutofillHints("username");
        var password = ui.Input(Strings.Login_Password, vm.Password, text => vm.Password = text, password: true);
        password.SetSingleLine(true);
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) password.SetAutofillHints("password");
        var remember = new CheckBox(ui.Context) { Text = Strings.Login_Remember, Checked = vm.RememberMe };
        remember.SetTextColor(ui.P.Text);
        remember.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(ui.P.Accent);
        remember.CheckedChange += (_, e) => vm.RememberMe = e.IsChecked;

        _error = ui.Text("", TextStyle.Body, Tone.Danger);
        _busy = ui.Busy();
        _signIn = ui.Button(Strings.Login_SignIn, () =>
        {
            ui.Hide(password);
            if (vm.SignInCommand.CanExecute(null)) vm.SignInCommand.Execute(null);
        });
        password.EditorAction += (_, e) =>
        {
            if (e.ActionId == Android.Views.InputMethods.ImeAction.Done) _signIn.PerformClick();
        };
        password.ImeOptions = Android.Views.InputMethods.ImeAction.Done;

        var form = ui.Column();
        form.SetPadding(ui.Dp(24), ui.Dp(48), ui.Dp(24), ui.Dp(24));
        ui.Add(form, ui.Text(productName, TextStyle.Title), bottom: 4);
        ui.Add(form, ui.Text(Strings.Login_Title, TextStyle.Heading), bottom: 4);
        ui.Add(form, ui.Text(Strings.Login_Lede, muted: true), bottom: 16);
        ui.Add(form, user, bottom: 8);
        ui.Add(form, password, bottom: 8);
        ui.Add(form, remember, bottom: 8);
        ui.Add(form, _error, bottom: 8);
        ui.Add(form, _busy, bottom: 8);
        ui.Add(form, _signIn, bottom: 24);
        ui.Add(form, ui.Text(Strings.Login_Server, TextStyle.Caption));
        ui.Add(form, ui.Text(server, TextStyle.Body), bottom: 4);
        ui.Add(form, ui.Button(Strings.Login_ChangeServer, changeServer, filled: false));

        _root = new ScrollView(ui.Context) { FillViewport = true };
        _root.AddView(form);
    }

    public override View Root => _root;

    protected override void Render()
    {
        _error.Text = _vm.ErrorMessage ?? "";
        _error.Visibility = _vm.HasError ? ViewStates.Visible : ViewStates.Gone;
        _busy.Visibility = _vm.IsBusy ? ViewStates.Visible : ViewStates.Invisible;
        _signIn.Alpha = _vm.SignInCommand.CanExecute(null) ? 1f : 0.45f;
    }
}
