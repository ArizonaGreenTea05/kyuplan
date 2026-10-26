using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages;

/// <summary>View-model-first navigation, as the WPF client's data templates: the page view model picks its view.</summary>
internal static class PageRegistry
{
    public static Gtk.Widget Create(UiContext context, PageViewModel page) => page switch
    {
        DashboardViewModel dashboard => DashboardPage.Build(context, dashboard),
        AccountViewModel account => AccountPage.Build(context, account),
        PreferencesViewModel preferences => PreferencesPage.Build(context, preferences),
        TagsViewModel tags => TagsPage.Build(context, tags),
        BeltsViewModel belts => BeltsPage.Build(context, belts),
        MembersViewModel members => MembersPage.Build(context, members),
        ExamsViewModel exams => ExamsPage.Build(context, exams),
        AttendanceStatisticsViewModel statistics => StatisticsPage.Build(context, statistics),
        CalendarViewModel calendar => CalendarPage.Build(context, calendar),
        ExamPricesViewModel prices => PricesPage.Build(context, prices),
        SyncStatusViewModel sync => SyncStatusPage.Build(context, sync),
        RolesViewModel roles => RolesPage.Build(context, roles),
        UserAdminViewModel users => UsersPage.Build(context, users),
        BackupViewModel backup => BackupPage.Build(context, backup),
        CalendarFeedsViewModel feeds => CalendarFeedsPage.Build(context, feeds),
        BrandingAdminViewModel branding => BrandingPage.Build(context, branding),
        SettingsViewModel settings => SettingsPage.Build(context, settings),
        ManualDataViewModel manual => ManualDataPage.Build(context, manual),
        PluginsViewModel plugins => PluginsPage.Build(context, plugins),
        PluginPageViewModel pluginPage => PluginPage.Build(context, pluginPage),
        _ => Ui.Page(Ui.VBox(12, Ui.Heading(page.Title))),
    };
}
