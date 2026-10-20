using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.WpfApp.ViewModels;

/// <summary>An entry of the navigation pane: a page and its icon (a Segoe Fluent Icons glyph).</summary>
public sealed record NavigationItem(PageViewModel Page, string Glyph);

/// <summary>
/// The WPF-specific frame around the shared view models: which page is shown. (Blazor navigates by URL instead.) A page is only in the
/// navigation pane while the signed-in user may use it (<see cref="PageViewModel.CanView"/>), as in the browser; the account page is
/// everybody's.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly LoginSession _session;
    private readonly NavigationItem[] _main, _admin, _account;
    private readonly ObservableCollection<NavigationItem> _items = [], _adminItems = [], _accountItems = [];
    private NavigationItem _selectedItem = null!; // assigned in the constructor

    public MainViewModel(
        ShellViewModel shell, ExamsViewModel exams, DashboardViewModel dashboard, AttendanceStatisticsViewModel statistics,
        MembersViewModel members, BeltsViewModel belts, TagsViewModel tags, AccountViewModel account, PreferencesViewModel preferences,
        UserAdminViewModel users, RolesViewModel roles, BrandingAdminViewModel branding, BackupViewModel backup, ExamPricesViewModel prices, CalendarFeedsViewModel calendarFeeds, LoginSession session, ClientUpdateViewModel update,
        CalendarViewModel calendar, EventBannerViewModel banner)
    {
        Shell = shell;
        Update = update;
        Banner = banner;
        _session = session;
        var all = new NavigationItem[]
        {
            new NavigationItem(calendar, ""), // CalendarDay
            new NavigationItem(exams, ""), // CheckboxComposite: a checklist, the calendar page has the calendar icon
            new NavigationItem(dashboard, ""), // Home
            new NavigationItem(statistics, ""), // Chart
            new NavigationItem(members, ""), // People
            new NavigationItem(belts, ""), // Tag
            new NavigationItem(tags, ""), // Flag
            new NavigationItem(account, ""), // Contact
            new NavigationItem(preferences, ""), // Personalize
            new NavigationItem(users,""), // Admin
            new NavigationItem(roles, "\uE72E"), // Lock
            new NavigationItem(branding, ""), // Color
            new NavigationItem(backup, ""), // HardDrive
            new NavigationItem(prices, ""), // Calculator: what an exam costs
            new NavigationItem(calendarFeeds, ""), // Calendar: the calendars apps subscribe to
        };
        NavigationItem Item(PageViewModel page) => all.First(i => ReferenceEquals(i.Page, page));

        // The order of the navigation pane: the dashboard first (it is what opens), the groups Administration and Account at the bottom.
        _main = [Item(dashboard), Item(statistics), Item(calendar), Item(exams), Item(members), Item(belts), Item(prices), Item(tags)];
        _admin = [Item(users), Item(roles), Item(branding), Item(calendarFeeds), Item(backup)];
        _account = [Item(account), Item(preferences)];
        Items = new ReadOnlyObservableCollection<NavigationItem>(_items);
        AdminItems = new ReadOnlyObservableCollection<NavigationItem>(_adminItems);
        AccountItems = new ReadOnlyObservableCollection<NavigationItem>(_accountItems);
        SelectedItem = _main.Concat(_admin).Concat(_account).First(i => i.Page.CanView); // the account page at least

        ShowPermittedPages();
        shell.PermissionsChanged += (_, _) => ShowPermittedPages();

        // The banner of an event in progress opens the event on the calendar page, whatever page is shown.
        banner.OpenRequested += async (_, e) =>
        {
            SelectedItem = Item(calendar);
            await calendar.OpenEventAsync(e.GroupId, e.Id);
        };

        // The exams page sends the user to the statistics page to define a filter.
        exams.OpenStatisticsRequested += (_, _) => SelectedItem = Item(statistics);

        shell.Language.Changed += (_, _) => OnPropertyChanged(nameof(SignedInText));
        session.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SignedInText));
    }

    public ShellViewModel Shell { get; }

    /// <summary>A newer version of this program is on the server: the navigation pane offers to install it.</summary>
    public ClientUpdateViewModel Update { get; }

    /// <summary>Events of the selected group that are going on now; shown above every page.</summary>
    public EventBannerViewModel Banner { get; }

    public ReadOnlyObservableCollection<NavigationItem> Items { get; }

    /// <summary>The pages of the "Administration" group; empty (and the group hidden) for somebody who may use none.</summary>
    public ReadOnlyObservableCollection<NavigationItem> AdminItems { get; }

    /// <summary>The pages of the "Account" group.</summary>
    public ReadOnlyObservableCollection<NavigationItem> AccountItems { get; }

    /// <summary>
    /// Puts the pages the signed-in user may use into the navigation and takes the others out (somebody else signed in, or the user's
    /// permissions changed). A page that is taken out while it is shown gives way to the first one first: the list would otherwise clear the selection.
    /// </summary>
    private void ShowPermittedPages()
    {
        var visible = _main.Concat(_admin).Concat(_account).Where(i => i.Page.CanView).ToList();
        if (!visible.Contains(SelectedItem)) SelectedItem = visible[0];

        Sync(_main, _items);
        Sync(_admin, _adminItems);
        Sync(_account, _accountItems);
        OnPropertyChanged(nameof(MainSelection));
        OnPropertyChanged(nameof(AdminSelection));
        OnPropertyChanged(nameof(AccountSelection));
    }

    private static void Sync(NavigationItem[] source, ObservableCollection<NavigationItem> target)
    {
        var wanted = source.Where(i => i.Page.CanView).ToList();
        foreach (var item in target.Where(i => !wanted.Contains(i)).ToList()) target.Remove(item);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= target.Count || !ReferenceEquals(target[i], wanted[i])) target.Insert(i, wanted[i]);
        }
    }

    /// <summary>"Signed in as Anna": who the API knows the login as, or what was typed while the API cannot be asked.</summary>
    public string SignedInText => string.Format(Strings.Shell_SignedInAs, _session.User?.DisplayName ?? _session.Login?.UserName);

    /// <summary>
    /// The page that is shown. The navigation pane has several lists, and each one writes "nothing chosen" (null) when the choice moves to another
    /// list: that is no choice of the user, so it is ignored.
    /// </summary>
    public NavigationItem SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedItem)) return;

            var old = _selectedItem;
            _selectedItem = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MainSelection));
            OnPropertyChanged(nameof(AdminSelection));
            OnPropertyChanged(nameof(AccountSelection));
            old?.Page.Deactivate();
            _ = value.Page.ActivateAsync();
        }
    }

    // One selection per list: a list shows the choice only when the chosen page is one of its own, so the others show nothing selected.
    public NavigationItem? MainSelection { get => Own(_items); set => SelectedItem = value!; }

    public NavigationItem? AdminSelection { get => Own(_adminItems); set => SelectedItem = value!; }

    public NavigationItem? AccountSelection { get => Own(_accountItems); set => SelectedItem = value!; }

    private NavigationItem? Own(ObservableCollection<NavigationItem> list) => list.Contains(_selectedItem) ? _selectedItem : null;
}
