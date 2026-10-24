using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.LinuxApp.Pages;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.LinuxApp.Stages;

/// <summary>The application itself: the sidebar with the pages the user may use, and the page that is shown with the bar above it.</summary>
internal static class ShellStage
{
    public static Gtk.Widget Build(UiContext context, NavigationViewModel navigation, Func<Task> signOut)
    {
        var split = Adw.OverlaySplitView.New();
        split.MinSidebarWidth = 230;
        split.MaxSidebarWidth = 280;
        split.SidebarWidthFraction = 0.2;
        split.SetSidebar(Sidebar(context, navigation, signOut));
        split.SetContent(Content(context, navigation, split));
        return split;
    }

    // ---- sidebar -----------------------------------------------------------------------------------------------------------

    private static Gtk.Widget Sidebar(UiContext context, NavigationViewModel navigation, Func<Task> signOut)
    {
        var shell = navigation.Shell;
        var lists = new List<(Gtk.ListBox List, Func<IEnumerable<NavigationItem>> Items)>();

        Gtk.ListBox NavList(Func<IEnumerable<NavigationItem>> items, System.Collections.Specialized.INotifyCollectionChanged changes)
        {
            var list = Gtk.ListBox.New();
            list.SelectionMode = Gtk.SelectionMode.Single;
            list.AddCssClass("navigation-sidebar");
            var rows = new Dictionary<Gtk.ListBoxRow, NavigationItem>();
            var updating = false;

            void Rebuild()
            {
                updating = true;
                list.RemoveAll();
                rows.Clear();
                foreach (var item in items())
                {
                    var row = Gtk.ListBoxRow.New();
                    var label = Ui.Label(item.Page.Title);
                    label.Wrap = false;
                    label.Ellipsize = Pango.EllipsizeMode.End;
                    row.SetChild(Ui.HBox(10, Gtk.Image.NewFromIconName(item.Icon), label).Margin(6, 4));
                    rows[row] = item;
                    list.Append(row);
                }

                updating = false;
                Select();
            }

            void Select()
            {
                updating = true;
                var current = rows.FirstOrDefault(r => ReferenceEquals(r.Value, navigation.SelectedItem)).Key;
                if (current is not null) list.SelectRow(current);
                else list.UnselectAll();
                updating = false;
            }

            list.OnRowSelected += (_, e) =>
            {
                if (updating || e.Row is null || !rows.TryGetValue(e.Row, out var item)) return;

                navigation.SelectedItem = item;
            };
            changes.CollectionChanged += (_, _) => Rebuild();
            navigation.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(NavigationViewModel.SelectedItem)) Select();
            };
            Rebuild();
            lists.Add((list, items));
            return list;
        }

        var main = NavList(() => navigation.Items, navigation.Items);
        var admin = NavList(() => navigation.AdminItems, navigation.AdminItems);
        var account = NavList(() => navigation.AccountItems, navigation.AccountItems);

        var adminSection = Ui.VBox(2, Ui.Label(Strings.Nav_Administration, "caption-heading").Start().Margin(6, 14), admin);
        ((System.Collections.Specialized.INotifyCollectionChanged)navigation.AdminItems).CollectionChanged += (_, _) => adminSection.Visible = navigation.AdminItems.Count > 0;
        adminSection.Visible = navigation.AdminItems.Count > 0;

        var signOutRow = Ui.Button(Strings.Shell_SignOut, () => _ = signOut(), "flat").Tip(Strings.Shell_SignOutHint);
        signOutRow.Halign = Gtk.Align.Fill;
        var accountSection = Ui.VBox(2,
            Ui.Label(Strings.Nav_Account, "caption-heading").Start().Margin(6, 14).Tip(navigation.SignedInText),
            account, signOutRow);

        // The documentation the user may read; each opens in the browser (the server asks for the login there).
        var help = Ui.Repeat(shell, () => shell.HelpLinks,
            link => Ui.Button(link.Title, () => context.OpenUri(link.Address), "flat").Start(),
            [nameof(ShellViewModel.HelpLinks)], spacing: 0);

        // Only while the server carries a newer version than this program.
        var update = navigation.Update;
        var updateBox = Ui.VBox(6,
            Ui.Primary(update.ButtonText, update.InstallCommand).Tip(update.Hint),
            Ui.Label(update, nameof(ClientUpdateViewModel.ErrorMessage), "caption").VisibleWhen(update, nameof(ClientUpdateViewModel.HasError)))
            .VisibleWhen(update, nameof(ClientUpdateViewModel.IsUpdateAvailable));

        var repository = shell.RepositoryUrl is { } url
            ? Ui.FlatButton("powered by kyuplan", () => context.OpenUri(url)).Start()
            : (Gtk.Widget)Ui.Muted("powered by kyuplan").Css("caption");

        var scroll = Gtk.ScrolledWindow.New();
        scroll.Vexpand = true;
        scroll.HscrollbarPolicy = Gtk.PolicyType.Never;
        scroll.SetChild(Ui.VBox(8, main, adminSection, accountSection).Margin(6, 6));

        var top = Ui.VBox(6,
            BrandLogo.Create(shell.Brand, 120),
            Ui.Label(shell.Brand, nameof(BrandingViewModel.ProductName), "title-3").Start().Margin(0, 12))
            .Margin(10, 6);
        var bottom = Ui.VBox(6, help, updateBox, Ui.Label(shell, nameof(ShellViewModel.ApiVersionText), "caption", "dim-label").Margin(0, 12), repository).Margin(8, 8);

        var column = Ui.VBox(0, top, scroll, bottom);
        var view = Adw.ToolbarView.New();
        view.SetContent(column);
        return view;
    }

    // ---- content -----------------------------------------------------------------------------------------------------------

    private static Gtk.Widget Content(UiContext context, NavigationViewModel navigation, Adw.OverlaySplitView split)
    {
        var shell = navigation.Shell;
        var view = Adw.ToolbarView.New();

        // Top bar: group picker, demo marker, refresh. Group and refresh are of no use to somebody who may use no page that shows what Spond knows.
        var header = Adw.HeaderBar.New();
        var toggle = Gtk.ToggleButton.New();
        toggle.IconName = "sidebar-show-symbolic";
        toggle.Active = true;
        toggle.TooltipText = Strings.Nav_Main;
        toggle.OnToggled += (_, _) => split.ShowSidebar = toggle.Active;
        header.PackStart(toggle);

        var groups = Ui.Choice(shell, () => shell.Groups, g => $"{g.Name} ({g.MemberCount})", () => shell.SelectedGroup, g => shell.SelectedGroupId = g.Id,
            [nameof(ShellViewModel.Groups), nameof(ShellViewModel.SelectedGroup), nameof(ShellViewModel.SelectedGroupId)], Strings.Shell_Group);
        groups.Hexpand = false;
        var groupBox = Ui.HBox(8, Ui.Label(Strings.Shell_Group, "dim-label"), groups).VisibleWhen(shell, nameof(ShellViewModel.UsesSpond));
        header.PackStart(groupBox);

        header.PackEnd(Ui.IconButton("view-refresh-symbolic", Strings.Shell_RefreshHint, shell.RefreshDataCommand)
            .VisibleWhen(shell, nameof(ShellViewModel.UsesSpond)));
        header.PackEnd(Ui.Chip(Strings.Shell_DemoData, "demo-chip").Tip(Strings.Shell_DemoDataHint).VisibleWhen(shell, nameof(ShellViewModel.IsDemoData)));
        view.AddTopBar(header);

        // An event of the selected group is going on: whatever page is shown, each links to its detail page.
        var banner = navigation.Banner;
        view.AddTopBar(Ui.Repeat(banner, () => banner.Events, e =>
        {
            var open = Ui.Button(Strings.Banner_Open, banner.OpenCommand, e, "flat");
            return Ui.HBox(10, Gtk.Image.NewFromIconName("appointment-soon-symbolic"), Ui.Label(e, nameof(BannerEvent.Text)).Expand(), open).Css("banner-event");
        }, [nameof(EventBannerViewModel.Events)], spacing: 0).VisibleWhen(banner, nameof(EventBannerViewModel.HasEvents)));

        // Connection / loading problems of the shell (the pages show their own).
        view.AddTopBar(Ui.HBox(12,
            Ui.Label(shell, nameof(ShellViewModel.ErrorMessage)).Expand(),
            Ui.Button(Strings.Shell_TryAgain, shell.RetryCommand)).Css("banner-problem").VisibleWhen(shell, nameof(ShellViewModel.HasError)));

        view.SetContent(PageHost(context, navigation));
        return view;
    }

    /// <summary>Shows the page of the selected sidebar item; a page is drawn when it is first shown and kept, so that scrolling and typing survive switching pages.</summary>
    private static Gtk.Widget PageHost(UiContext context, NavigationViewModel navigation)
    {
        var stack = Gtk.Stack.New();
        stack.Hexpand = stack.Vexpand = true;
        var pages = new Dictionary<NavigationItem, Gtk.Widget>();

        void Show()
        {
            var item = navigation.SelectedItem;
            if (!pages.TryGetValue(item, out var widget))
            {
                widget = PageRegistry.Create(context, item.Page);
                pages[item] = widget;
                stack.AddChild(widget);
            }

            stack.SetVisibleChild(widget);
        }

        navigation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavigationViewModel.SelectedItem)) Show();
        };
        Show();
        return stack;
    }
}
