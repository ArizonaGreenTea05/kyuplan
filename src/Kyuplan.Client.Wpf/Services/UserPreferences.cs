using System.IO;

namespace Kyuplan.Client.WpfApp.Services;

/// <summary>
/// Small per-user settings (theme, language): one text file each in %LocalAppData%\Kyuplan. A setting that
/// was never chosen reads as null, which means "follow Windows".
/// </summary>
public sealed class UserPreferences
{
    private readonly string _folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kyuplan");

    public string? Get(string name)
    {
        try
        {
            var path = Path.Combine(_folder, name + ".txt");
            var text = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (IOException)
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
