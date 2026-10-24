using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kyuplan.Client.ViewModels;

/// <summary>What a tab of the phone client shows. The client picks the icon from it.</summary>
public enum MobileTabKind
{
    Dashboard,
    Calendar,
    Exams,
    Members,
    Account,
}

/// <summary>An entry of the tab bar: the kind of page and the page itself.</summary>
public sealed record MobileTab(MobileTabKind Kind, PageViewModel Page);

/// <summary>
/// The frame of the phone client (Android): a bottom tab bar with the pages a member of the club uses day to day. The administration pages
/// and the statistics are not on the phone. As in the desktop clients a tab is only there while the signed-in user may use its page
/// (<see cref="PageViewModel.ShowInNavigation"/>); the account tab is everybody's, and holds the personal settings.
/// </summary>
public sealed class MobileShellViewModel : ObservableObject
{
    private readonly MobileTab[] _all;
    private readonly ObservableCollection<MobileTab> _tabs = [];
    private MobileTab? _selected;

    public MobileShellViewModel(
        ShellViewModel shell, DashboardViewModel dashboard, CalendarViewModel calendar, ExamsViewModel exams, MembersViewModel members,
        AccountViewModel account, EventBannerViewModel banner)
    {
        Shell = shell;
        Banner = banner;
        _all =
        [
            new(MobileTabKind.Dashboard, dashboard),
            new(MobileTabKind.Calendar, calendar),
            new(MobileTabKind.Exams, exams),
            new(MobileTabKind.Members, members),
            new(MobileTabKind.Account, account),
        ];
        Tabs = new ReadOnlyObservableCollection<MobileTab>(_tabs);
        MobileTab Tab(MobileTabKind kind) => _all.First(t => t.Kind == kind);

        ShowPermittedTabs(); // selects the first tab (once, so one page loads at start-up)
        shell.PermissionsChanged += (_, _) => ShowPermittedTabs();

        // The banner of an event in progress and an upcoming event of the dashboard open the event on the calendar, whatever tab is shown.
        async void OpenOnCalendar(string groupId, string eventId)
        {
            SelectedTab = Tab(MobileTabKind.Calendar);
            try
            {
                await calendar.OpenEventAsync(groupId, eventId);
            }
            catch (Exception)
            {
                // An event handler nobody awaits: whatever goes wrong while the event is read is the calendar's to report (it does), not to end the app.
            }
        }

        banner.OpenRequested += (_, e) => OpenOnCalendar(e.GroupId, e.Id);
        dashboard.OpenEventRequested += (_, e) => OpenOnCalendar(e.GroupId, e.Id);
    }

    public ShellViewModel Shell { get; }

    /// <summary>Events of the selected group that are going on now; shown above every tab.</summary>
    public EventBannerViewModel Banner { get; }

    /// <summary>Every tab there is, whether the signed-in user may use it or not.</summary>
    public IReadOnlyList<MobileTab> AllTabs => _all;

    /// <summary>The tabs the signed-in user may use, in the order of the tab bar.</summary>
    public ReadOnlyObservableCollection<MobileTab> Tabs { get; }

    /// <summary>The tab that is shown. Choosing one that is not in <see cref="Tabs"/> (or none) changes nothing.</summary>
    public MobileTab SelectedTab
    {
        get => _selected!;
        set
        {
            if (value is null || ReferenceEquals(value, _selected) || !_tabs.Contains(value) && _selected is not null) return;

            Select(value);
        }
    }

    private void Select(MobileTab tab)
    {
        var old = _selected;
        _selected = tab;
        OnPropertyChanged(nameof(SelectedTab));
        old?.Page.Deactivate();
        _ = tab.Page.ActivateAsync();
    }

    /// <summary>
    /// Puts the tabs the signed-in user may use into the bar and takes the others out (somebody else signed in, or their permissions changed).
    /// A tab that is taken out while it is shown gives way to the first one. The first time (nothing is shown yet) the calendar is what opens:
    /// a phone is for a quick look at what is going on, as in the browser on a phone.
    /// </summary>
    private void ShowPermittedTabs()
    {
        // The account tab is everybody's (no view permission), so there is always one tab; wanted[0] cannot fail.
        var wanted = _all.Where(t => t.Page.ShowInNavigation).ToList();
        if (_selected is null || !wanted.Contains(_selected))
        {
            var calendar = wanted.Find(t => t.Kind == MobileTabKind.Calendar);
            Select(_selected is null && calendar is not null ? calendar : wanted[0]);
        }

        foreach (var tab in _tabs.Where(t => !wanted.Contains(t)).ToList()) _tabs.Remove(tab);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= _tabs.Count || !ReferenceEquals(_tabs[i], wanted[i])) _tabs.Insert(i, wanted[i]);
        }
    }
}
