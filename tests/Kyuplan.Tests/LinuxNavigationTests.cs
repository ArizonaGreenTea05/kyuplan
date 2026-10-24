using Microsoft.Extensions.DependencyInjection;
using Kyuplan.Client;
using Kyuplan.Client.Api;
using Kyuplan.Client.LinuxApp;
using Kyuplan.Client.Storage;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The Linux client's sidebar: a page is in it only for a user who may use it, and the account pages are everybody's.</summary>
public sealed class LinuxNavigationTests : IDisposable
{
    private static readonly UserDto Boss = Dto(1, "boss", "boss", [], true);
    private static readonly UserDto Anna = Dto(2, "Anna", "Anna", [], false);

    private readonly FakePlannerApi _api = new();
    private readonly ServiceProvider _services;
    private readonly LoginSession _session;
    private readonly NavigationViewModel _navigation;

    public LinuxNavigationTests()
    {
        var services = new ServiceCollection();
        services.AddPlannerClient(options => options.BaseAddress = "http://planner.test/");
        _api.AccountGate = new TaskCompletionSource(); // nobody is signed in until a test says so
        services.AddSingleton<IPlannerApi>(_api);
        services.AddSingleton<ILoginStore, FakeLoginStore>();
        services.AddSingleton<IClientUpdater, FakeClientUpdater>();
        services.AddSingleton<ClientUpdateViewModel>();
        services.AddSingleton<NavigationViewModel>();
        _services = services.BuildServiceProvider();

        _session = _services.GetRequiredService<LoginSession>();
        _navigation = _services.GetRequiredService<NavigationViewModel>();
    }

    public void Dispose() => _services.Dispose();

    // Top to bottom as the sidebar shows them: the pages, the Administration group, the Account group.
    private IEnumerable<PageViewModel> Pages => _navigation.Items.Concat(_navigation.AdminItems).Concat(_navigation.AccountItems).Select(i => i.Page);

    private static string[] Names(IEnumerable<PageViewModel> pages) => [.. pages.Select(p => p.GetType().Name.Replace("ViewModel", ""))];

    [Fact]
    public async Task An_upcoming_event_of_the_dashboard_opens_on_the_calendar_page_whatever_page_is_shown()
    {
        _session.User = Boss;
        _navigation.SelectedItem = _navigation.AccountItems[0];
        var upcoming = new CalendarEventDto("e1", "g1", "Training", DateTimeOffset.Now.AddDays(2), DateTimeOffset.Now.AddDays(2).AddHours(1), false, 3, 2, 0, 1);
        _api.CalendarEvents.Add(upcoming);
        _api.Account = Boss;
        _api.AccountGate!.SetResult();
        var calendar = _services.GetRequiredService<CalendarViewModel>();
        var dashboard = _services.GetRequiredService<DashboardViewModel>();

        dashboard.OpenEventCommand.Execute(new UpcomingEventRowViewModel(new UpcomingEventDto("e1", "Training", upcoming.Start, 3, 2, 0, 1), "g1"));

        Assert.Same(calendar, _navigation.SelectedItem.Page);
        for (var waited = 0; !calendar.HasDetail && waited < 5000; waited += 10) await Task.Delay(10); // the event is read in the background
        Assert.Equal("e1", calendar.Detail?.Id);
    }

    [Fact]
    public void Before_anybody_is_known_the_sidebar_has_the_account_pages_only()
    {
        Assert.Empty(_navigation.Items);
        Assert.Empty(_navigation.AdminItems);
        Assert.Equal(2, _navigation.AccountItems.Count);
        Assert.IsType<AccountViewModel>(_navigation.SelectedItem.Page);
    }

    [Fact]
    public void An_ordinary_user_has_the_pages_their_role_allows_and_their_account()
    {
        _session.User = Anna;

        Assert.Equal(["Dashboard", "AttendanceStatistics", "Exams", "Members", "Belts", "Tags", "Account", "Preferences"], Names(Pages));
        Assert.Empty(_navigation.AdminItems);
    }

    [Fact]
    public void An_administrator_has_every_administration_page_in_order_including_the_settings()
    {
        _session.User = Boss;

        Assert.Equal(["UserAdmin", "Roles", "Settings", "BrandingAdmin", "CalendarFeeds", "Backup", "SyncStatus"], Names(_navigation.AdminItems.Select(i => i.Page)));
        Assert.Equal(8, _navigation.Items.Count); // the manual data page waits for the server to say the source is manual
    }

    [Fact]
    public void Every_page_has_an_icon()
    {
        _session.User = Boss;

        Assert.All(_navigation.Items.Concat(_navigation.AdminItems).Concat(_navigation.AccountItems), item => Assert.EndsWith("-symbolic", item.Icon));
    }

    [Fact]
    public void Somebody_has_exactly_the_pages_they_may_see()
    {
        _session.User = WithPermissions(3, "ben", Permission.BeltsView, Permission.RolesView, Permission.MembersEdit);

        // members.edit alone does not show the members page: only the .view permission does.
        Assert.Equal(["Belts", "Roles", "Account", "Preferences"], Names(Pages));
    }

    [Fact]
    public void Somebody_without_any_permission_has_their_account_and_nothing_else()
    {
        _session.User = Nobody(4, "eve");

        Assert.Empty(_navigation.Items);
        Assert.Empty(_navigation.AdminItems);
        Assert.Equal(["Account", "Preferences"], Names(_navigation.AccountItems.Select(i => i.Page)));
    }

    [Fact]
    public void Signing_in_again_as_somebody_else_changes_the_sidebar_to_theirs_without_doubling_it()
    {
        _session.User = Boss;
        _session.User = Boss with { UserName = "chief" }; // renamed: still an administrator
        Assert.Equal(7, _navigation.AdminItems.Count);

        _session.User = Anna;
        Assert.Equal(6, _navigation.Items.Count);
        Assert.Empty(_navigation.AdminItems);

        _session.User = Boss;
        Assert.Equal(7, _navigation.AdminItems.Count);
    }

    [Fact]
    public void A_page_that_is_open_gives_way_to_the_first_one_that_is_left_when_the_permission_is_gone()
    {
        _session.User = Boss;
        _navigation.SelectedItem = _navigation.AdminItems[3];
        Assert.IsType<BrandingAdminViewModel>(_navigation.SelectedItem.Page);

        _session.User = Anna;
        Assert.IsType<DashboardViewModel>(_navigation.SelectedItem.Page);

        _session.User = null; // signed out
        Assert.IsType<AccountViewModel>(_navigation.SelectedItem.Page);
    }

    [Fact]
    public void The_sidebar_keeps_the_page_that_is_open_when_the_permission_is_gone_from_another_page()
    {
        _session.User = Boss;
        _navigation.SelectedItem = _navigation.Items[5]; // belts

        _session.User = Anna;

        Assert.IsType<BeltsViewModel>(_navigation.SelectedItem.Page);
    }

    [Fact]
    public void Choosing_the_page_that_is_shown_changes_nothing()
    {
        _session.User = Boss;
        var shown = _navigation.SelectedItem;
        var changes = 0;
        _navigation.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(NavigationViewModel.SelectedItem) ? 1 : 0;

        _navigation.SelectedItem = shown;
        _navigation.SelectedItem = null!; // a list that lost its selection writes "nothing": no choice of the user

        Assert.Equal(0, changes);
        Assert.Same(shown, _navigation.SelectedItem);
    }

    [Fact]
    public void The_sidebar_shows_the_pages_the_program_keeps()
    {
        _session.User = Boss;

        Assert.Same(_services.GetRequiredService<UserAdminViewModel>(), _navigation.AdminItems[0].Page);
        Assert.Same(_services.GetRequiredService<SettingsViewModel>(), _navigation.AdminItems[2].Page);
        Assert.Same(_session, _services.GetRequiredService<UserAdminViewModel>().Shell.Session);
    }

    [Fact]
    public void Who_is_signed_in_is_said_in_the_account_group()
    {
        _session.User = Anna;
        var changed = false;
        _navigation.PropertyChanged += (_, e) => changed |= e.PropertyName == nameof(NavigationViewModel.SignedInText);

        _session.User = Anna with { DisplayName = "Anna K." };

        Assert.True(changed);
        Assert.Contains("Anna K.", _navigation.SignedInText);
    }
}
