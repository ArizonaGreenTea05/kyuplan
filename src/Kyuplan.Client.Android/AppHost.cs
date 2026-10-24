using Android.Content;
using Kyuplan.Client.AndroidApp.Services;
using Kyuplan.Client.Storage;
using Kyuplan.Client.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Kyuplan.Client.AndroidApp;

/// <summary>What the screens ask the app to do, which only the activity can: leave the server, leave the login, open a page in the browser.</summary>
public interface IAppActions
{
    /// <summary>Asks for another server address; the app starts over with it.</summary>
    void ChangeServer();

    /// <summary>Forgets the login and asks for one again.</summary>
    void SignOut();

    /// <summary>Opens an address in the browser (the guides).</summary>
    void Open(Uri address);
}

/// <summary>Puts the shared client together for one server address. A new address is a new container: nothing of the old server is kept.</summary>
public static class AppHost
{
    public static ServiceProvider Build(Context context, string apiAddress)
    {
        var services = new ServiceCollection();
        services.AddPlannerClient(options => options.BaseAddress = apiAddress);
        services.AddSingleton<ILoginStore>(new KeystoreLoginStore(context, apiAddress));
        services.AddTransient<LoginViewModel>(); // one per sign-in: a fresh form every time
        services.AddSingleton<IClientUpdater>(new AndroidClientUpdater(context));
        services.AddSingleton<ClientUpdateViewModel>();
        services.AddSingleton<MobileShellViewModel>();
        return services.BuildServiceProvider();
    }
}
