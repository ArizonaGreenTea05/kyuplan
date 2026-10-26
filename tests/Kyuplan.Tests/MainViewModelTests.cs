using Microsoft.Extensions.DependencyInjection;
using Kyuplan.Client;
using Kyuplan.Client.Api;
using Kyuplan.Client.Storage;
using Kyuplan.Client.ViewModels;
using Kyuplan.Client.WpfApp.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The desktop client's navigation: a page is in it only for a user who may use it.</summary>
public sealed class MainViewModelTests : IDisposable
{
    private static readonly UserDto Boss = Dto(1, "boss", "boss", [], true);
    private static readonly UserDto Anna = Dto(2, "Anna", "Anna", [], false);

    private readonly FakePlannerApi _api = new();
    private readonly ServiceProvider _services;
    private readonly LoginSession _session;
    private readonly MainViewModel _main;

    public MainViewModelTests()
    {
        var services = new ServiceCollection();
        services.AddPlannerClient(options => options.BaseAddress = "http://planner.test/");
        _api.AccountGate = new TaskCompletionSource(); // nobody is signed in until a test says so: the pages must not learn the user from the fake API by themselves
        services.AddSingleton<IPlannerApi>(_api); // no HTTP: the pages ask this one
        services.AddSingleton<ILoginStore, FakeLoginStore>(); // as in the program, which remembers the login
        services.AddSingleton<IClientUpdater, FakeClientUpdater>();
        services.AddSingleton<ClientUpdateViewModel>();
        services.AddSingleton<MainViewModel>();
        _services = services.BuildServiceProvider();

        _session = _services.GetRequiredService<LoginSession>();
        _main = _services.GetRequiredService<MainViewModel>();
    }

    public void Dispose() => _services.Dispose();

    // Top to bottom as the navigation pane shows them: the pages, then the Administration group, then the Account group.
    private IEnumerable<PageViewModel> Pages => _main.Items.Concat(_main.AdminItems).Concat(_main.AccountItems).Select(i => i.Page);

    [Fact]
    public async Task An_upcoming_event_of_the_dashboard_opens_on_the_calendar_page_whatever_page_is_shown()
    {
        _session.User = Boss;
        _main.SelectedItem = _main.AccountItems[0];
        var upcoming = new CalendarEventDto("e1", "g1", "Training", DateTimeOffset.Now.AddDays(2), DateTimeOffset.Now.AddDays(2).AddHours(1), false, 3, 2, 0, 1);
        _api.CalendarEvents.Add(upcoming);
        _api.Account = Boss;
        _api.AccountGate!.SetResult();
        var calendar = _services.GetRequiredService<CalendarViewModel>();
        var dashboard = _services.GetRequiredService<DashboardViewModel>();

        dashboard.OpenEventCommand.Execute(new UpcomingEventRowViewModel(new UpcomingEventDto("e1", "Training", upcoming.Start, 3, 2, 0, 1), "g1"));

        Assert.Same(calendar, _main.SelectedItem.Page);
        for (var waited = 0; !calendar.HasDetail && waited < 5000; waited += 10) await Task.Delay(10); // the event is read in the background
        Assert.Equal("e1", calendar.Detail?.Id);
    }

    [Fact]
    public void Before_anybody_is_known_the_navigation_has_the_account_page_only()
    {
        Assert.Empty(_main.Items);
        Assert.Empty(_main.AdminItems);
        Assert.Equal(2, _main.AccountItems.Count); // the account and its personal settings
        Assert.IsType<AccountViewModel>(_main.SelectedItem.Page);
    }

    [Fact]
    public void An_ordinary_user_has_the_pages_their_role_allows_and_their_account()
    {
        _session.User = Anna;

        Assert.Equal(6, _main.Items.Count);
        Assert.Empty(_main.AdminItems);
        Assert.DoesNotContain(Pages, page => page is UserAdminViewModel or RolesViewModel or BrandingAdminViewModel);
        Assert.IsType<DashboardViewModel>(_main.Items[0].Page); // what opens
        Assert.Equal(
            ["Dashboard", "AttendanceStatistics", "Exams", "Members", "Belts", "Tags", "Account", "Preferences"],
            Pages.Select(p => p.GetType().Name.Replace("ViewModel", "")).ToArray());
    }

    [Fact]
    public void The_tags_page_is_one_of_the_ordinary_pages_right_after_the_belts()
    {
        _session.User = Anna;
        var pages = Pages.ToList();

        Assert.IsType<TagsViewModel>(pages[pages.FindIndex(p => p is BeltsViewModel) + 1]);
        Assert.Same(_services.GetRequiredService<TagsViewModel>(), pages.OfType<TagsViewModel>().Single());
    }

    [Fact]
    public void An_administrator_has_the_users_the_roles_and_the_branding_page_after_the_others()
    {
        _session.User = Boss;

        Assert.Equal(8, _main.Items.Count); // the manual data page waits for the server to say the source is manual
        Assert.Equal(7, _main.AdminItems.Count);
        Assert.IsType<UserAdminViewModel>(_main.AdminItems[0].Page);
        Assert.IsType<RolesViewModel>(_main.AdminItems[1].Page);
        Assert.IsType<PluginsViewModel>(_main.AdminItems[2].Page);
        Assert.IsType<BrandingAdminViewModel>(_main.AdminItems[3].Page);
        Assert.IsType<CalendarFeedsViewModel>(_main.AdminItems[4].Page);
        Assert.IsType<BackupViewModel>(_main.AdminItems[5].Page);
        Assert.IsType<SyncStatusViewModel>(_main.AdminItems[6].Page);
        Assert.All(_main.Items.Concat(_main.AdminItems).Concat(_main.AccountItems), item => Assert.NotEmpty(item.Glyph));
    }

    [Fact]
    public void Somebody_has_exactly_the_pages_they_may_see()
    {
        _session.User = WithPermissions(3, "ben", Permission.BeltsView, Permission.RolesView, Permission.MembersEdit);

        // members.edit alone does not show the members page: only the .view permission does.
        Assert.Equal(["Belts", "Roles", "Account", "Preferences"], Pages.Select(p => p.GetType().Name.Replace("ViewModel", "")).ToArray());
    }

    [Fact]
    public void Somebody_without_any_permission_has_their_account_and_nothing_else()
    {
        _session.User = Nobody(4, "eve");

        Assert.Empty(_main.Items);
        Assert.Empty(_main.AdminItems);
        Assert.IsType<AccountViewModel>(_main.AccountItems[0].Page);
        Assert.IsType<PreferencesViewModel>(_main.AccountItems[1].Page);
    }

    [Fact]
    public void Signing_in_again_as_somebody_else_changes_the_navigation_to_theirs_without_doubling_it()
    {
        _session.User = Boss;
        _session.User = Boss with { UserName = "chief" }; // renamed: still an administrator
        Assert.Equal(7, _main.AdminItems.Count);

        _session.User = Anna;
        Assert.Equal(6, _main.Items.Count);
        Assert.Empty(_main.AdminItems);

        _session.User = Boss;
        Assert.Equal(8, _main.Items.Count); // the manual data page waits for the server to say the source is manual
        Assert.Equal(7, _main.AdminItems.Count);
    }

    [Fact]
    public void A_page_that_is_open_gives_way_to_the_first_one_that_is_left_when_the_permission_is_gone()
    {
        _session.User = Boss;
        _main.SelectedItem = _main.AdminItems[3];
        Assert.IsType<BrandingAdminViewModel>(_main.SelectedItem.Page);

        _session.User = Anna;

        Assert.Equal(6, _main.Items.Count);
        Assert.IsType<DashboardViewModel>(_main.SelectedItem.Page);

        _session.User = null; // signed out
        Assert.IsType<AccountViewModel>(_main.SelectedItem.Page);
    }

    [Fact]
    public void The_navigation_keeps_the_page_that_is_open_when_the_permission_is_gone_from_another_page()
    {
        _session.User = Boss;
        _main.SelectedItem = _main.Items[5]; // belts

        _session.User = Anna;

        Assert.IsType<BeltsViewModel>(_main.SelectedItem.Page);
    }

    [Fact]
    public void The_navigation_shows_the_pages_the_program_keeps()
    {
        // The pages keep what was typed while the user looks at another one, and the users page renews the login of the program's session.
        _session.User = Boss;

        Assert.Same(_services.GetRequiredService<UserAdminViewModel>(), _main.AdminItems[0].Page);
        Assert.Same(_services.GetRequiredService<RolesViewModel>(), _main.AdminItems[1].Page);
        Assert.Same(_services.GetRequiredService<BrandingAdminViewModel>(), _main.AdminItems[3].Page);
        Assert.Same(_session, _services.GetRequiredService<UserAdminViewModel>().Shell.Session);
    }

    [Fact]
    public async Task The_pages_the_plugins_added_follow_the_main_pages_and_come_and_go_with_the_catalog()
    {
        _api.PluginCatalog = PluginTestKit.AcmeCatalog();
        _api.Account = WithPermissions(5, "viewer", "acme.view");
        _api.AccountGate!.SetResult();
        var shell = _services.GetRequiredService<ShellViewModel>();

        // The shell learns who is signed in itself. (The clients change the navigation on one thread; setting the user here while the
        // start-up still runs in the background would make two threads do it at once.)
        await shell.InitializeAsync();

        var pluginPages = _main.Items.Select(i => i.Page).OfType<PluginPageViewModel>().ToList();
        Assert.Equal(["acme/items", "acme/about"], pluginPages.Select(p => p.Key));
        Assert.Same(pluginPages[1], _main.Items[^1].Page); // after the program's own pages
        Assert.All(_main.Items.Where(i => i.Page is PluginPageViewModel), item => Assert.NotEmpty(item.Glyph));

        // The open page gives way when its permission goes: somebody else signs in.
        _main.SelectedItem = _main.Items[^2];
        Assert.Same(pluginPages[0], _main.SelectedItem.Page);
        _session.User = Nobody(6, "nobody");
        await shell.RefreshPluginsAsync();

        Assert.DoesNotContain(_main.Items.Select(i => i.Page), page => ReferenceEquals(page, pluginPages[0]));
        Assert.False(ReferenceEquals(_main.SelectedItem.Page, pluginPages[0]));
    }

    [Fact]
    public async Task The_plugins_page_is_in_the_administration_group_for_those_who_may_see_it()
    {
        _session.User = WithPermissions(7, "ops", Permission.PluginsView);
        await Task.CompletedTask;

        Assert.Contains(_main.AdminItems.Select(i => i.Page), page => page is PluginsViewModel);
        _session.User = Nobody(8, "nobody");
        Assert.DoesNotContain(_main.AdminItems.Select(i => i.Page), page => page is PluginsViewModel);
    }
}

/// <summary>How the clients wire the administrators' pages up (DI is the one thing the unit tests of those do by hand).</summary>
public class AdministratorPageRegistrationTests
{
    [Theory]
    [InlineData(false)] // the browser: it keeps the login itself and has no store
    [InlineData(true)]  // WPF: a store that can remember the login
    public void The_administrators_pages_are_available_with_and_without_a_login_store(bool withLoginStore)
    {
        var services = new ServiceCollection();
        services.AddPlannerClient(options => options.BaseAddress = "http://planner.test/");
        if (withLoginStore) services.AddSingleton<ILoginStore, FakeLoginStore>();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<UserAdminViewModel>());
        Assert.NotNull(provider.GetRequiredService<RolesViewModel>());
        Assert.NotNull(provider.GetRequiredService<BrandingAdminViewModel>());
    }
}
