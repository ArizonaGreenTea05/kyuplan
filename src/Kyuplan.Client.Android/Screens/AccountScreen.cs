using Android.Widget;
using Kyuplan.Client.AndroidApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.AndroidApp.Screens;

/// <summary>Who is signed in, the language, the guides, and the way out: change the server or sign out.</summary>
public sealed class AccountScreen : ScrollScreen
{
    private readonly AccountViewModel _account;
    private readonly PreferencesViewModel _preferences;
    private readonly IClientUpdater _updater;
    private readonly string _server;
    private readonly IAppActions _actions;

    public AccountScreen(Ui ui, AccountViewModel account, PreferencesViewModel preferences, IClientUpdater updater, string server, IAppActions actions) : base(ui)
    {
        _account = account;
        _preferences = preferences;
        _updater = updater;
        _server = server;
        _actions = actions;
        Observe(account);
        Observe(preferences);
        Observe(account.Shell, nameof(ShellViewModel.Info), nameof(ShellViewModel.CurrentUser), nameof(ShellViewModel.HelpLinks));
        Observe(account.Shell.Language);
    }

    protected override void Build(LinearLayout content)
    {
        var shell = _account.Shell;
        if (_preferences.IsBusy) content.AddView(Ui.Busy());
        if (_preferences.HasError) Ui.Add(content, Ui.Notice(_preferences.ErrorMessage!, Tone.Danger), top: 8);

        var name = shell.CurrentUser?.DisplayName is { Length: > 0 } display ? display : shell.CurrentUser?.UserName ?? shell.Session.Login?.UserName;
        Ui.Add(content, Ui.Card(
            Ui.Text(string.Format(Strings.Shell_SignedInAs, name), TextStyle.Heading),
            Ui.Text(_account.RoleText, TextStyle.Caption)), top: 8);

        Ui.Add(content, Ui.Text(Strings.Preferences_LanguageTitle, TextStyle.Heading), bottom: 8);
        Ui.Add(content, Ui.ChipRow([.. _preferences.Languages.Select(language =>
            (Android.Views.View)Ui.Chip(language.Name, language.Code == _preferences.SelectedLanguage.Code, () => _ = _preferences.ChooseLanguageAsync(language)))]));

        if (shell.HelpLinks.Count > 0)
        {
            foreach (var link in shell.HelpLinks) Ui.Add(content, Ui.Button(link.Title, () => _actions.Open(link.Address), filled: false), bottom: 8);
        }

        Ui.Add(content, Ui.Text(Strings.Login_Server, TextStyle.Heading), top: 12, bottom: 4);
        Ui.Add(content, Ui.Text(_server, TextStyle.Body));
        Ui.Add(content, Ui.Text($"{shell.ApiVersionText} · {shell.Brand.ProductName} {_updater.CurrentVersion}", TextStyle.Caption), bottom: 12);
        Ui.Add(content, Ui.Button(Strings.Login_ChangeServer, _actions.ChangeServer, filled: false), bottom: 8);
        Ui.Add(content, Ui.Button(Strings.Shell_SignOut, _actions.SignOut), bottom: 8);
    }
}
