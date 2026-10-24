using Microsoft.Extensions.DependencyInjection;
using Kyuplan.Client;
using Kyuplan.Client.Api;
using Kyuplan.Client.Storage;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The phone client's tab bar: only the pages a member uses day to day, and only the ones the signed-in user may use.</summary>
public sealed class MobileShellViewModelTests : IDisposable
{
    private static readonly UserDto Boss = Dto(1, "boss", "boss", [], true);
    // What a member of the club who uses the app is given: the four pages that follow the group (the calendar is not among the old user role's rights).
    private static readonly UserDto Anna = WithPermissions(2, "Anna", Permission.DashboardView, Permission.CalendarView, Permission.ExamsView, Permission.MembersView);

    private readonly FakePlannerApi _api = new();
    private readonly ServiceProvider _services;
    private readonly LoginSession _session;
    private readonly MobileShellViewModel _mobile;

    public MobileShellViewModelTests()
    {
        var services = new ServiceCollection();
        services.AddPlannerClient(options => options.BaseAddress = "http://planner.test/");
        _api.AccountGate = new TaskCompletionSource(); // nobody is signed in until a test says so
        services.AddSingleton<IPlannerApi>(_api);
        services.AddSingleton<ILoginStore, FakeLoginStore>();
        services.AddSingleton<MobileShellViewModel>();
        _services = services.BuildServiceProvider();

        _session = _services.GetRequiredService<LoginSession>();
        _mobile = _services.GetRequiredService<MobileShellViewModel>();
    }

    public void Dispose() => _services.Dispose();

    private MobileTabKind[] Kinds => [.. _mobile.Tabs.Select(t => t.Kind)];

    [Fact]
    public void Before_anybody_is_known_the_bar_has_the_account_tab_only()
    {
        Assert.Equal([MobileTabKind.Account], Kinds);
        Assert.Equal(MobileTabKind.Account, _mobile.SelectedTab.Kind);
        Assert.IsType<AccountViewModel>(_mobile.SelectedTab.Page);
    }

    [Fact]
    public void An_ordinary_user_has_the_five_tabs_and_the_calendar_opens()
    {
        _session.User = Anna;

        Assert.Equal([MobileTabKind.Dashboard, MobileTabKind.Calendar, MobileTabKind.Exams, MobileTabKind.Members, MobileTabKind.Account], Kinds);
        Assert.IsType<DashboardViewModel>(_mobile.Tabs[0].Page);
        Assert.IsType<CalendarViewModel>(_mobile.Tabs[1].Page);
        Assert.IsType<ExamsViewModel>(_mobile.Tabs[2].Page);
        Assert.IsType<MembersViewModel>(_mobile.Tabs[3].Page);
        Assert.IsType<AccountViewModel>(_mobile.Tabs[4].Page);
    }

    [Fact]
    public void The_calendar_is_what_opens_for_somebody_who_may_see_it()
    {
        // The bar of the fixture was made before anybody was known; one made for a known user starts on the calendar.
        var api = new FakePlannerApi { AccountGate = new TaskCompletionSource() };
        using var again = new ServiceCollection()
            .AddPlannerClient(options => options.BaseAddress = "http://planner.test/")
            .AddSingleton<IPlannerApi>(api)
            .AddSingleton<MobileShellViewModel>()
            .BuildServiceProvider();
        again.GetRequiredService<LoginSession>().User = Anna;

        var fresh = again.GetRequiredService<MobileShellViewModel>();

        Assert.Equal(MobileTabKind.Calendar, fresh.SelectedTab.Kind);
    }

    [Fact]
    public void Somebody_who_may_not_see_the_calendar_starts_on_the_first_tab_they_may_use()
    {
        var api = new FakePlannerApi { AccountGate = new TaskCompletionSource() };
        using var again = new ServiceCollection()
            .AddPlannerClient(options => options.BaseAddress = "http://planner.test/")
            .AddSingleton<IPlannerApi>(api)
            .AddSingleton<MobileShellViewModel>()
            .BuildServiceProvider();
        again.GetRequiredService<LoginSession>().User = WithPermissions(7, "mia", Permission.MembersView);

        Assert.Equal(MobileTabKind.Members, again.GetRequiredService<MobileShellViewModel>().SelectedTab.Kind);
    }

    [Fact]
    public void The_account_tab_is_always_in_the_bar_so_there_is_always_a_tab_to_show()
    {
        // ShowPermittedTabs takes wanted[0] without a check: this pins the invariant that makes it safe.
        var account = _mobile.AllTabs.Single(t => t.Kind == MobileTabKind.Account);

        foreach (var user in new UserDto?[] { null, Nobody(4, "eve"), Anna, Boss })
        {
            _session.User = user;
            Assert.True(account.Page.ShowInNavigation);
            Assert.Contains(account, _mobile.Tabs);
        }
    }

    [Fact]
    public void The_administration_and_the_statistics_are_not_on_the_phone_even_for_an_administrator()
    {
        _session.User = Boss;

        var pages = _mobile.AllTabs.Select(t => t.Page.GetType()).ToList();
        Assert.Equal(5, pages.Count);
        Assert.DoesNotContain(typeof(AttendanceStatisticsViewModel), pages);
        Assert.DoesNotContain(typeof(UserAdminViewModel), pages);
        Assert.DoesNotContain(typeof(RolesViewModel), pages);
        Assert.DoesNotContain(typeof(BrandingAdminViewModel), pages);
        Assert.DoesNotContain(typeof(BackupViewModel), pages);
        Assert.DoesNotContain(typeof(SyncStatusViewModel), pages);
        Assert.DoesNotContain(typeof(CalendarFeedsViewModel), pages);
        Assert.Equal(5, _mobile.Tabs.Count);
    }

    [Fact]
    public void Somebody_has_exactly_the_tabs_they_may_use()
    {
        _session.User = WithPermissions(3, "ben", Permission.MembersView, Permission.CalendarView, Permission.BeltsView);

        Assert.Equal([MobileTabKind.Calendar, MobileTabKind.Members, MobileTabKind.Account], Kinds);
    }

    [Fact]
    public void Somebody_without_any_permission_has_their_account_and_nothing_else()
    {
        _session.User = Nobody(4, "eve");

        Assert.Equal([MobileTabKind.Account], Kinds);
    }

    [Fact]
    public void Changing_the_user_does_not_double_the_tabs()
    {
        _session.User = Anna;
        _session.User = Anna with { UserName = "annie" }; // renamed: the same permissions
        Assert.Equal(5, _mobile.Tabs.Count);

        _session.User = Nobody(5, "eve");
        Assert.Single(_mobile.Tabs);

        _session.User = Boss;
        Assert.Equal(5, _mobile.Tabs.Count);
    }

    [Fact]
    public void A_tab_that_is_open_gives_way_to_the_first_one_when_the_permission_is_gone()
    {
        _session.User = Boss;
        _mobile.SelectedTab = _mobile.Tabs[2];
        Assert.Equal(MobileTabKind.Exams, _mobile.SelectedTab.Kind);

        _session.User = WithPermissions(6, "ben", Permission.MembersView);

        Assert.Equal(MobileTabKind.Members, _mobile.SelectedTab.Kind);

        _session.User = null; // signed out
        Assert.Equal(MobileTabKind.Account, _mobile.SelectedTab.Kind);
    }

    [Fact]
    public void The_tab_that_is_open_stays_when_another_permission_is_gone()
    {
        _session.User = Boss;
        _mobile.SelectedTab = _mobile.Tabs[3];

        _session.User = Anna;

        Assert.Equal(MobileTabKind.Members, _mobile.SelectedTab.Kind);
    }

    [Fact]
    public void A_tab_that_is_not_in_the_bar_cannot_be_chosen()
    {
        _session.User = Nobody(4, "eve");
        var dashboard = _mobile.AllTabs[0];

        _mobile.SelectedTab = dashboard;
        _mobile.SelectedTab = null!; // a list control that lost its choice

        Assert.Equal(MobileTabKind.Account, _mobile.SelectedTab.Kind);
    }

    [Fact]
    public void Choosing_a_tab_tells_the_view_the_choice_changed_once()
    {
        _session.User = Anna;
        var changes = 0;
        _mobile.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(MobileShellViewModel.SelectedTab) ? 1 : 0;

        _mobile.SelectedTab = _mobile.Tabs[2];
        _mobile.SelectedTab = _mobile.Tabs[2];

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task The_banner_of_an_event_in_progress_opens_the_calendar_whatever_tab_is_shown()
    {
        _session.User = Anna;
        _mobile.SelectedTab = _mobile.Tabs[3];
        var running = new CalendarEventDto("e1", "g1", "Training", DateTimeOffset.Now.AddMinutes(-10), DateTimeOffset.Now.AddMinutes(50), false, 3, 2, 0, 1);
        _api.CalendarEvents.Add(running);
        _api.Account = Anna;
        _api.AccountGate!.SetResult(); // the sign-in that the first tab started (before anybody was known) is done: the pages can read now
        var calendar = _services.GetRequiredService<CalendarViewModel>();

        _mobile.Banner.OpenCommand.Execute(new BannerEvent(running));

        Assert.Equal(MobileTabKind.Calendar, _mobile.SelectedTab.Kind);
        for (var waited = 0; !calendar.HasDetail && waited < 5000; waited += 10) await Task.Delay(10); // the event is read in the background
        Assert.Equal("e1", calendar.Detail?.Id);
    }

    [Fact]
    public async Task An_upcoming_event_of_the_dashboard_opens_the_calendar_whatever_tab_is_shown()
    {
        _session.User = Anna;
        _mobile.SelectedTab = _mobile.Tabs[0];
        var upcoming = new CalendarEventDto("e1", "g1", "Training", DateTimeOffset.Now.AddDays(2), DateTimeOffset.Now.AddDays(2).AddHours(1), false, 3, 2, 0, 1);
        _api.CalendarEvents.Add(upcoming);
        _api.Account = Anna;
        _api.AccountGate!.SetResult();
        var calendar = _services.GetRequiredService<CalendarViewModel>();
        var dashboard = _services.GetRequiredService<DashboardViewModel>();

        dashboard.OpenEventCommand.Execute(new UpcomingEventRowViewModel(new UpcomingEventDto("e1", "Training", upcoming.Start, 3, 2, 0, 1), "g1"));

        Assert.Equal(MobileTabKind.Calendar, _mobile.SelectedTab.Kind);
        for (var waited = 0; !calendar.HasDetail && waited < 5000; waited += 10) await Task.Delay(10); // the event is read in the background
        Assert.Equal("e1", calendar.Detail?.Id);
    }

    [Fact]
    public void The_tabs_are_the_pages_the_program_keeps()
    {
        _session.User = Anna;

        Assert.Same(_services.GetRequiredService<DashboardViewModel>(), _mobile.Tabs[0].Page);
        Assert.Same(_services.GetRequiredService<CalendarViewModel>(), _mobile.Tabs[1].Page);
        Assert.Same(_services.GetRequiredService<ExamsViewModel>(), _mobile.Tabs[2].Page);
        Assert.Same(_services.GetRequiredService<MembersViewModel>(), _mobile.Tabs[3].Page);
        Assert.Same(_services.GetRequiredService<AccountViewModel>(), _mobile.Tabs[4].Page);
        Assert.Same(_services.GetRequiredService<EventBannerViewModel>(), _mobile.Banner);
    }
}
