using System.Runtime.CompilerServices;

namespace Kyuplan.Tests;

/// <summary>
/// The settings file that the web client writes (<c>data/settings.json</c> next to the API's program files) is read on top of
/// <c>appsettings.json</c>; a developer who has used the settings page must not change what the tests run with. Test hosts get a file
/// that does not exist unless a test sets the path itself.
/// </summary>
internal static class NoSettingsFileInTests
{
    [ModuleInitializer]
    internal static void Run() =>
        Environment.SetEnvironmentVariable("Setup__SettingsFile", Path.Combine(Path.GetTempPath(), "pp-tests-no-settings", "settings.json"));
}
