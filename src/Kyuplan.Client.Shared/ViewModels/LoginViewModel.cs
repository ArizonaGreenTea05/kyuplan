using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Client.Storage;

namespace Kyuplan.Client.ViewModels;

/// <summary>
/// The sign-in of a client that has to ask for the login itself (WPF; the browser has its own login dialog and does not use
/// this). A login is only used once the API has accepted it, and only remembered when the user asked for that.
/// </summary>
public sealed partial class LoginViewModel : ViewModelBase
{
    private readonly IPlannerApi _api;
    private readonly LoginSession _session;
    private readonly ILoginStore _store;

    public LoginViewModel(IPlannerApi api, LoginSession session, ILoginStore store)
    {
        _api = api;
        _session = session;
        _store = store;

        // Starts from the remembered login (the user name only: the password stays in the store) so signing in again is a matter of typing the password.
        if (store.Load() is { } saved)
        {
            UserName = saved.UserName;
            RememberMe = true;
        }

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) SignInCommand.NotifyCanExecuteChanged();
        };
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _userName = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _password = "";

    /// <summary>Keep the login (encrypted) on this computer, so that the next start signs in by itself.</summary>
    [ObservableProperty]
    private bool _rememberMe;

    /// <summary>Raised once the API has accepted a login; <see cref="LoginSession"/> holds it by then.</summary>
    public event EventHandler? SignedIn;

    /// <summary>Signing in is only possible with a user name and a password, and not while the last attempt is still running.</summary>
    private bool CanSignIn => !IsBusy && UserName.Trim().Length > 0 && Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private Task SignInAsync() => TryAsync(new Login(UserName.Trim(), Password));

    /// <summary>
    /// Signs in with the login remembered on this computer. <c>true</c> when the client can go on without asking: the API accepted
    /// it, or could not be asked (then the pages report the problem and offer to try again, as they do without a login).
    /// <c>false</c> when there is none or the API refuses it; the remembered login is forgotten then, and <see cref="ErrorMessage"/> says why.
    /// </summary>
    public async Task<bool> TrySignInWithSavedLoginAsync()
    {
        if (_store.Load() is not { } saved) return false;

        UserName = saved.UserName;
        RememberMe = true;

        var outcome = await TryAsync(saved);
        if (outcome is SignInOutcome.Rejected)
        {
            _store.Clear();
            ErrorMessage = Strings.Login_SavedRejected;
        }
        else if (outcome is SignInOutcome.Failed)
        {
            // Not the login's fault (the API is down, say): keep it, and let the pages report the problem.
            _session.Login = saved;
            ErrorMessage = null;
        }

        return outcome is not SignInOutcome.Rejected;
    }

    /// <summary>Rejected: the API says the login is wrong. Failed: the API could not say.</summary>
    private enum SignInOutcome { SignedIn, Rejected, Failed }

    private async Task<SignInOutcome> TryAsync(Login login)
    {
        IsBusy = true;
        ErrorMessage = null;
        _session.Login = login;
        try
        {
            _session.User = await _api.GetAccountAsync();

            if (RememberMe) _store.Save(login);
            else _store.Clear();

            SignedIn?.Invoke(this, EventArgs.Empty);
            return SignInOutcome.SignedIn;
        }
        catch (PlannerApiException ex)
        {
            _session.Login = null;
            _session.User = null;

            if (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                ErrorMessage = Strings.Login_Rejected;
                return SignInOutcome.Rejected;
            }

            ErrorMessage = ex.Message;
            return SignInOutcome.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _session.Login = null;
            _session.User = null;
            ErrorMessage = string.Format(Strings.Error_Unexpected, ex.Message);
            return SignInOutcome.Failed;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
