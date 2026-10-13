using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Client.Storage;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>A login store that keeps its login in a field, and counts what the view model asked of it.</summary>
internal sealed class FakeLoginStore : ILoginStore
{
    public Login? Saved { get; set; }

    public int Saves { get; private set; }

    public int Clears { get; private set; }

    public Login? Load() => Saved;

    public void Save(Login login)
    {
        Saves++;
        Saved = login;
    }

    public void Clear()
    {
        Clears++;
        Saved = null;
    }
}

public class LoginViewModelTests
{
    private static readonly Login Anna = new("anna", "correct horse");

    private readonly FakePlannerApi _api = new();
    private readonly FakeLoginStore _store = new();
    private readonly LoginSession _session = new();

    public LoginViewModelTests() => CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en-GB");

    private LoginViewModel Create() => new(_api, _session, _store);

    private static PlannerApiException Rejected() => new("The API rejected the request.", HttpStatusCode.Unauthorized);

    private static PlannerApiException Unreachable() => new("Cannot reach the Kyuplan API.");

    private static async Task SignInAsync(LoginViewModel vm, string userName, string password, bool remember = false)
    {
        vm.UserName = userName;
        vm.Password = password;
        vm.RememberMe = remember;
        await vm.SignInCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Signing_in_asks_the_api_who_the_login_is_and_hands_it_to_the_session()
    {
        var vm = Create();
        var signedIn = 0;
        vm.SignedIn += (_, _) => signedIn++;

        await SignInAsync(vm, "  anna ", "correct horse");

        Assert.Equal(Anna, _session.Login);
        Assert.Equal(_api.Account, _session.User);
        Assert.Equal(1, signedIn);
        Assert.Equal(1, _api.AccountRequests);
        Assert.False(vm.HasError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Only_a_login_the_user_asked_to_remember_is_saved()
    {
        var remembered = Create();
        await SignInAsync(remembered, "anna", "correct horse", remember: true);
        Assert.Equal(Anna, _store.Saved);

        // Untick "remember me" and sign in again: what was kept before is forgotten, nothing is saved.
        var forgetting = Create();
        Assert.True(forgetting.RememberMe); // it starts from what is remembered
        await SignInAsync(forgetting, "anna", "correct horse", remember: false);

        Assert.Null(_store.Saved);
        Assert.Equal(1, _store.Saves);
    }

    [Fact]
    public async Task A_login_the_api_refuses_is_neither_used_nor_saved()
    {
        _api.FailWith = Rejected();
        var vm = Create();
        var signedIn = 0;
        vm.SignedIn += (_, _) => signedIn++;

        await SignInAsync(vm, "anna", "wrong", remember: true);

        Assert.Equal(Strings.Login_Rejected, vm.ErrorMessage);
        Assert.True(vm.HasError);
        Assert.Null(_session.Login);
        Assert.Null(_session.User);
        Assert.Equal(0, _store.Saves);
        Assert.Equal(0, signedIn);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task When_the_api_cannot_be_reached_the_login_is_not_blamed()
    {
        _api.FailWith = Unreachable();
        var vm = Create();

        await SignInAsync(vm, "anna", "correct horse", remember: true);

        Assert.Equal("Cannot reach the Kyuplan API.", vm.ErrorMessage);
        Assert.Null(_session.Login);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task An_unexpected_failure_is_reported_and_does_not_escape_the_sign_in()
    {
        _api.FailWith = new InvalidOperationException("boom");
        var vm = Create();

        await SignInAsync(vm, "anna", "correct horse");

        Assert.Equal(string.Format(Strings.Error_Unexpected, "boom"), vm.ErrorMessage);
        Assert.Null(_session.Login);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task After_a_failed_attempt_the_next_one_can_succeed_and_clears_the_message()
    {
        _api.FailWith = Rejected();
        var vm = Create();
        await SignInAsync(vm, "anna", "wrong");

        _api.FailWith = null;
        await SignInAsync(vm, "anna", "correct horse");

        Assert.False(vm.HasError);
        Assert.Equal(Anna, _session.Login);
    }

    [Fact]
    public async Task Signing_in_is_only_possible_with_a_user_name_and_a_password_and_not_while_one_attempt_runs()
    {
        var vm = Create();
        var changes = 0;
        vm.SignInCommand.CanExecuteChanged += (_, _) => changes++;
        Assert.False(vm.SignInCommand.CanExecute(null));

        vm.UserName = "anna";
        Assert.False(vm.SignInCommand.CanExecute(null)); // no password yet
        vm.Password = "correct horse";
        Assert.True(vm.SignInCommand.CanExecute(null));
        vm.UserName = "   ";
        Assert.False(vm.SignInCommand.CanExecute(null)); // a user name of blanks is none
        vm.UserName = "anna";
        Assert.True(changes >= 4, "the button must be told whenever the fields change");

        _api.AccountGate = new TaskCompletionSource();
        var attempt = vm.SignInCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.SignInCommand.CanExecute(null)); // no second sign-in while the first is under way

        _api.AccountGate.SetResult();
        await attempt;
        Assert.False(vm.IsBusy);
        Assert.Equal(1, _api.AccountRequests);
    }

    // --- The login remembered from last time ---------------------------------------------------------------------

    [Fact]
    public void The_form_starts_with_the_remembered_user_name_but_never_shows_the_password()
    {
        _store.Saved = Anna;

        var vm = Create();

        Assert.Equal("anna", vm.UserName);
        Assert.Equal("", vm.Password);
        Assert.True(vm.RememberMe);
    }

    [Fact]
    public async Task A_remembered_login_signs_in_without_asking()
    {
        _store.Saved = Anna;
        var vm = Create();

        var signedIn = await vm.TrySignInWithSavedLoginAsync();

        Assert.True(signedIn);
        Assert.Equal(Anna, _session.Login);
        Assert.Equal(_api.Account, _session.User);
        Assert.False(vm.HasError);
        Assert.Equal(Anna, _store.Saved);
    }

    [Fact]
    public async Task Without_a_remembered_login_there_is_nothing_to_try()
    {
        var vm = Create();

        Assert.False(await vm.TrySignInWithSavedLoginAsync());
        Assert.Equal(0, _api.AccountRequests);
        Assert.Null(_session.Login);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task A_remembered_login_the_api_no_longer_accepts_is_forgotten_and_the_user_asked_again()
    {
        _store.Saved = Anna;
        _api.FailWith = Rejected();
        var vm = Create();

        var signedIn = await vm.TrySignInWithSavedLoginAsync();

        Assert.False(signedIn);
        Assert.Null(_store.Saved);
        Assert.Null(_session.Login);
        Assert.Equal(Strings.Login_SavedRejected, vm.ErrorMessage);
        Assert.Equal("anna", vm.UserName); // only the password has to be typed again
    }

    [Fact]
    public async Task A_remembered_login_survives_an_api_that_is_down_and_the_app_starts_anyway()
    {
        _store.Saved = Anna;
        _api.FailWith = Unreachable();
        var vm = Create();

        var signedIn = await vm.TrySignInWithSavedLoginAsync();

        // The pages show that the API cannot be reached and offer to try again, as they do for any start with the API down.
        Assert.True(signedIn);
        Assert.Equal(Anna, _session.Login);
        Assert.Equal(Anna, _store.Saved);
        Assert.False(vm.HasError);
    }
}

public class LoginSessionTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<AuthenticationHeaderValue?> Authorization { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization.Add(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (HttpClient Client, RecordingHandler Recorder) Create(LoginSession session)
    {
        var recorder = new RecordingHandler();
        return (new HttpClient(new BasicAuthHandler(session) { InnerHandler = recorder }) { BaseAddress = new Uri("http://planner.test/") }, recorder);
    }

    [Fact]
    public async Task The_login_is_sent_as_basic_credentials_in_utf8()
    {
        var session = new LoginSession { Login = new Login("Jürgen Köhler", "pä:ss wörd") };
        var (client, recorder) = Create(session);

        await client.GetAsync("api/info");

        var header = Assert.Single(recorder.Authorization)!;
        Assert.Equal("Basic", header.Scheme);
        Assert.Equal("Jürgen Köhler:pä:ss wörd", Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter!)));
    }

    [Fact]
    public async Task Without_a_login_nothing_is_sent_which_is_how_the_browser_client_leaves_it_to_the_browser()
    {
        var (client, recorder) = Create(new LoginSession());

        await client.GetAsync("api/info");

        Assert.Null(Assert.Single(recorder.Authorization));
    }

    [Fact]
    public async Task A_client_follows_the_session_when_the_login_changes()
    {
        var session = new LoginSession();
        var (client, recorder) = Create(session);

        await client.GetAsync("api/info");
        session.Login = new Login("anna", "correct horse");
        await client.GetAsync("api/info");
        session.Login = null; // signed out
        await client.GetAsync("api/info");

        Assert.Equal([false, true, false], recorder.Authorization.Select(a => a is not null));
    }

    [Fact]
    public void A_login_does_not_print_its_password()
    {
        var text = new Login("anna", "correct horse").ToString();

        Assert.Contains("anna", text);
        Assert.DoesNotContain("correct horse", text);
    }

    [Fact]
    public async Task The_account_is_asked_for_at_api_account()
    {
        var json = new StubJson("""{"id":3,"userName":"anna","displayName":"anna","aliases":[],"roles":[{"id":2,"name":"User"}],"permissions":[],"effectivePermissions":["members.view"]}""");
        var api = new PlannerApiClient(new HttpClient(json) { BaseAddress = new Uri("http://planner.test/") });

        var user = await api.GetAccountAsync();

        Assert.Equal(Dto(3, "anna", "anna", [], false).Show(), user.Show());
        Assert.Equal("/api/account", json.Request!.RequestUri!.AbsolutePath);
    }

    private sealed class StubJson(string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
