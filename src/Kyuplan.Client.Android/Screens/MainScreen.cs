using Android.Views;
using Android.Widget;
using Kyuplan.Client.AndroidApp.Kit;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kyuplan.Client.AndroidApp.Screens;

/// <summary>
/// The frame of the app once somebody is signed in: the title of the page with its refresh button, what is going on now (an event in
/// progress, an update, a problem), the group, the page itself and, at the bottom, the tab bar with the pages.
/// </summary>
public sealed class MainScreen : Screen
{
    private readonly MobileShellViewModel _mobile;
    private readonly ClientUpdateViewModel _update;
    private readonly IServiceProvider _services;
    private readonly IAppActions _actions;
    private readonly LinearLayout _root;
    private readonly LinearLayout _top;
    private readonly FrameLayout _content;
    private readonly LinearLayout _tabBar;
    private readonly Dictionary<MobileTabKind, Screen> _pages = [];
    private MobileTab? _shown;

    public MainScreen(Ui ui, IServiceProvider services, IAppActions actions) : base(ui)
    {
        _services = services;
        _actions = actions;
        _mobile = services.GetRequiredService<MobileShellViewModel>();
        _update = services.GetRequiredService<ClientUpdateViewModel>();

        _top = ui.Column();
        _content = new FrameLayout(ui.Context);
        _tabBar = ui.Row();
        _tabBar.SetBackgroundColor(ui.P.Surface);

        _root = ui.Column();
        _root.AddView(_top);
        _root.AddView(_content, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        _root.AddView(_tabBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        Observe(_mobile);
        ObserveCollection(_mobile.Tabs);
        Observe(_mobile.Shell, nameof(ShellViewModel.Groups), nameof(ShellViewModel.SelectedGroupId), nameof(ShellViewModel.HasError), nameof(ShellViewModel.ErrorMessage), nameof(ShellViewModel.IsBusy), nameof(ShellViewModel.Info));
        Observe(_mobile.Shell.Language);
        Observe(_mobile.Banner);
        Observe(_update);
        foreach (var tab in _mobile.AllTabs) Observe(tab.Page, nameof(ViewModelBase.IsBusy));
    }

    public override View Root => _root;

    public override bool HandleBack() => _shown is { } tab && _pages.TryGetValue(tab.Kind, out var page) && page.HandleBack();

    protected override void Render()
    {
        BuildTop();
        BuildTabBar();
        ShowSelectedPage();
    }

    // --- The page --------------------------------------------------------------------------------------------

    private void ShowSelectedPage()
    {
        var selected = _mobile.SelectedTab;
        if (ReferenceEquals(selected, _shown)) return;

        _shown = selected;
        if (!_pages.TryGetValue(selected.Kind, out var page))
        {
            _pages[selected.Kind] = page = CreatePage(selected.Kind);
            page.Start();
        }

        _content.RemoveAllViews();
        _content.AddView(page.Root, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
    }

    private Screen CreatePage(MobileTabKind kind) => kind switch
    {
        MobileTabKind.Dashboard => new DashboardScreen(Ui, _services.GetRequiredService<DashboardViewModel>()),
        MobileTabKind.Calendar => new CalendarScreen(Ui, _services.GetRequiredService<CalendarViewModel>()),
        MobileTabKind.Exams => new ExamsScreen(Ui, _services.GetRequiredService<ExamsViewModel>()),
        MobileTabKind.Members => new MembersScreen(Ui, _services.GetRequiredService<MembersViewModel>()),
        _ => new AccountScreen(
            Ui, _services.GetRequiredService<AccountViewModel>(), _services.GetRequiredService<PreferencesViewModel>(),
            _services.GetRequiredService<IClientUpdater>(), _services.GetRequiredService<IOptions<PlannerClientOptions>>().Value.BaseUri.AbsoluteUri, _actions),
    };

    // --- Above the page --------------------------------------------------------------------------------------

    private void BuildTop()
    {
        _top.RemoveAllViews();
        var shell = _mobile.Shell;
        var page = _mobile.SelectedTab.Page;

        var title = Ui.Text(page.Title, TextStyle.Title);
        var header = Ui.Row();
        header.AddView(title, Ui.Params(weight: 1));
        if (_mobile.SelectedTab.Kind != MobileTabKind.Account)
            header.AddView(Ui.IconButton(Resource.Drawable.ic_refresh, Strings.Shell_Refresh, () => page.RefreshCommand.Execute(null)));
        header.SetPadding(Ui.Dp(16), Ui.Dp(8), Ui.Dp(4), 0);
        _top.AddView(header);

        // An event that is going on now: one tap opens it.
        foreach (var running in _mobile.Banner.Events)
        {
            var banner = Ui.Notice(running.Text, Tone.Success);
            banner.Clickable = true;
            banner.Click += (_, _) => _mobile.Banner.OpenCommand.Execute(running);
            _top.AddView(banner, Ui.Params(left: 16, right: 16, top: 4));
        }

        if (_update.IsUpdateAvailable)
        {
            var install = Ui.Button(_update.ButtonText, () => _update.InstallCommand.Execute(null));
            _top.AddView(Ui.Notice(_update.Hint, Tone.Neutral, install), Ui.Params(left: 16, right: 16, top: 4));
        }

        if (_update.HasError) _top.AddView(Ui.Notice(_update.ErrorMessage!, Tone.Danger), Ui.Params(left: 16, right: 16, top: 4));

        if (shell.HasError)
        {
            var retry = Ui.Button(Strings.Shell_TryAgain, () => shell.RetryCommand.Execute(null), filled: false);
            _top.AddView(Ui.Notice(shell.ErrorMessage!, Tone.Danger, retry), Ui.Params(left: 16, right: 16, top: 4));
        }

        if (shell.IsDemoData) _top.AddView(Ui.Notice(Strings.Shell_DemoData, Tone.Warning), Ui.Params(left: 16, right: 16, top: 4));

        // The group everything is about: only for pages that show a group's data.
        if (_mobile.SelectedTab.Kind != MobileTabKind.Account && shell.UsesSpond && shell.Groups.Count > 0) _top.AddView(GroupPicker(shell), Ui.Params(left: 16, right: 16, top: 4));
        if (page.IsBusy) _top.AddView(Ui.Busy(), Ui.Params(top: 4));
    }

    private View GroupPicker(ShellViewModel shell)
    {
        var current = shell.SelectedGroup?.Name ?? Strings.Shell_Group;
        var picker = Ui.Row(Ui.Text(Strings.Shell_Group, TextStyle.Caption), Ui.Text(current, TextStyle.Heading));
        picker.GetChildAt(0)!.LayoutParameters = Ui.Params(wrap: true, right: 8);
        picker.GetChildAt(1)!.LayoutParameters = Ui.Params(weight: 1);
        picker.AddView(Ui.Text("▾", TextStyle.Heading), Ui.Params(wrap: true));
        picker.SetMinimumHeight(Ui.Dp(48));
        picker.Clickable = true;
        picker.Click += (_, _) =>
        {
            var groups = shell.Groups;
            var names = groups.Select(g => g.Name).ToArray();
            var selected = groups.ToList().FindIndex(g => g.Id == shell.SelectedGroupId);
            AlertDialog? dialog = null;
            dialog = new AlertDialog.Builder(Ui.Context)!
                .SetTitle(Strings.Shell_Group)!
                .SetSingleChoiceItems(names, selected, (_, e) =>
                {
                    shell.SelectedGroupId = groups[e.Which].Id;
                    dialog!.Dismiss();
                })!
                .Show();
        };
        return picker;
    }

    // --- The tab bar -----------------------------------------------------------------------------------------

    private void BuildTabBar()
    {
        _tabBar.RemoveAllViews();
        foreach (var tab in _mobile.Tabs)
        {
            var selected = ReferenceEquals(tab, _mobile.SelectedTab);
            var color = selected ? Ui.P.Accent : Ui.P.Muted;
            var icon = new ImageView(Ui.Context);
            icon.SetImageResource(IconOf(tab.Kind));
            icon.SetColorFilter(color);
            var label = Ui.Text(tab.Page.Title, TextStyle.Caption);
            label.SetTextColor(color);
            label.SetTextSize(Android.Util.ComplexUnitType.Sp, 11);
            label.SetSingleLine(true);
            label.Gravity = GravityFlags.Center;
            if (selected) label.SetTypeface(Android.Graphics.Typeface.DefaultBold, Android.Graphics.TypefaceStyle.Bold);

            var item = Ui.Column(icon, label);
            item.SetGravity(GravityFlags.Center);
            item.SetPadding(Ui.Dp(4), Ui.Dp(8), Ui.Dp(4), Ui.Dp(8));
            item.SetMinimumHeight(Ui.Dp(64));
            item.Clickable = true;
            item.Focusable = true;
            item.Foreground = Ui.Ripple(null);
            item.ContentDescription = tab.Page.Title;
            icon.LayoutParameters = new LinearLayout.LayoutParams(Ui.Dp(24), Ui.Dp(24)) { Gravity = GravityFlags.CenterHorizontal };
            var picked = tab;
            item.Click += (_, _) => _mobile.SelectedTab = picked;
            _tabBar.AddView(item, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        }
    }

    private static int IconOf(MobileTabKind kind) => kind switch
    {
        MobileTabKind.Dashboard => Resource.Drawable.ic_tab_dashboard,
        MobileTabKind.Calendar => Resource.Drawable.ic_tab_calendar,
        MobileTabKind.Exams => Resource.Drawable.ic_tab_exams,
        MobileTabKind.Members => Resource.Drawable.ic_tab_members,
        _ => Resource.Drawable.ic_tab_account,
    };
}
