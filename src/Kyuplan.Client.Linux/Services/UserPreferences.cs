namespace Kyuplan.Client.LinuxApp.Services;

/// <summary>
/// Small per-user settings (API address, language, last statistics filter): one text file each in <c>$XDG_CONFIG_HOME/kyuplan</c>
/// (<c>~/.config/kyuplan</c>). A setting that was never chosen reads as null, which means "follow the system".
/// </summary>
public sealed class UserPreferences(string? folder = null)
{
    private readonly string _folder = folder ?? DefaultFolder();

    /// <summary><c>$XDG_CONFIG_HOME/kyuplan</c>, else <c>~/.config/kyuplan</c>, as the XDG base directory specification says.</summary>
    public static string DefaultFolder()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config) || !Path.IsPathRooted(config))
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return Path.Combine(config, "kyuplan");
    }

    public string Folder => _folder;

    public string? Get(string name)
    {
        try
        {
            var path = Path.Combine(_folder, name + ".txt");
            var text = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Set(string name, string value)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, name + ".txt"), value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the choice just does not survive a restart.
        }
    }
}
