using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.Storage;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the API client and the shared view models. View models are singletons so that filters and
    /// results survive navigating between pages. The client sends the <see cref="LoginSession"/>'s login, if a client
    /// filled one in; without one (the browser) the requests carry none and the browser adds its own.
    /// </summary>
    public static IServiceCollection AddPlannerClient(this IServiceCollection services, Action<PlannerClientOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<LoginSession>();
        services.TryAddTransient<BasicAuthHandler>();
        // Clients that can remember the last filter replace this with a store of their own.
        services.TryAddSingleton<IFilterSettingsStore, InMemoryFilterSettingsStore>();

        services.AddHttpClient<IPlannerApi, PlannerApiClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<PlannerClientOptions>>().Value;
            http.BaseAddress = options.BaseUri;
            if (!string.IsNullOrWhiteSpace(options.ApiKey)) http.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
        }).AddHttpMessageHandler<BasicAuthHandler>();

        services.AddSingleton<LanguageService>();
        services.AddSingleton<BrandingViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<ThemeViewModel>();
        services.AddSingleton<ExamsViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<AttendanceStatisticsViewModel>();
        services.AddSingleton<AccountViewModel>();
        services.AddSingleton<PreferencesViewModel>();
        services.AddSingleton<CalendarViewModel>();
        services.AddSingleton<EventBannerViewModel>();
        services.AddSingleton<MembersViewModel>();
        services.AddSingleton<BeltsViewModel>();
        services.AddSingleton<TagsViewModel>();
        services.AddSingleton<UserAdminViewModel>(); // the pages of users, roles and branding: shown to those who may use them
        services.AddSingleton<RolesViewModel>();
        services.AddSingleton<BrandingAdminViewModel>();
        services.AddSingleton<BackupViewModel>();
        services.AddSingleton<ApiRestartWatcher>();
        services.AddSingleton<SetupViewModel>(); // asked before anything else: an API without a user has to be set up first
        services.AddSingleton<SettingsViewModel>();
        return services;
    }
}
