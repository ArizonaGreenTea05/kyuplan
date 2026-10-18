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

        Assert.Equal(8, _main.Items.Count);
        Assert.Equal(4, _main.AdminItems.Count);
        Assert.IsType<UserAdminViewModel>(_main.AdminItems[0].Page);
        Assert.IsType<RolesViewModel>(_main.AdminItems[1].Page);
        Assert.IsType<BrandingAdminViewModel>(_main.AdminItems[2].Page);
        Assert.IsType<BackupViewModel>(_main.AdminItems[3].Page);
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
        Assert.Equal(4, _main.AdminItems.Count);

        _session.User = Anna;
        Assert.Equal(6, _main.Items.Count);
        Assert.Empty(_main.AdminItems);

        _session.User = Boss;
        Assert.Equal(8, _main.Items.Count);
        Assert.Equal(4, _main.AdminItems.Count);
    }

    [Fact]
    public void A_page_that_is_open_gives_way_to_the_first_one_that_is_left_when_the_permission_is_gone()
    {
        _session.User = Boss;
        _main.SelectedItem = _main.AdminItems[2];
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
        Assert.Same(_services.GetRequiredService<BrandingAdminViewModel>(), _main.AdminItems[2].Page);
        Assert.Same(_session, _services.GetRequiredService<UserAdminViewModel>().Shell.Session);
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
