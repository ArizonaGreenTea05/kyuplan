namespace Kyuplan.Client.LinuxApp;

public static class Program
{
    public const string ApplicationId = "org.kyuplan.Client";

    public static int Main(string[] args)
    {
        Adw.Module.Initialize();
        var application = Adw.Application.New(ApplicationId, Gio.ApplicationFlags.DefaultFlags);
        var app = new KyuplanApp(application, args);
        application.OnActivate += (_, _) => app.Activate();
        return application.RunWithSynchronizationContext(args);
    }
}
