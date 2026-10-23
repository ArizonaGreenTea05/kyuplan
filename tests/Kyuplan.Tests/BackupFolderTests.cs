using Kyuplan.Api.Backup;
using Kyuplan.Api.Persistence;

namespace Kyuplan.Tests;

/// <summary>Where a SQLite backup database may be put: below the settings file's folder, not in what the web server serves, links followed.</summary>
public sealed class BackupFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pp-folder-").FullName;

    private string Data => Path.Combine(_root, "data");
    private string Settings => Path.Combine(Data, "settings.json");
    private string Content => Path.Combine(_root, "app");

    public BackupFolderTests()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Content);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Resolve(string location, string? webRoot = null) => BackupFolder.Resolve(location, Settings, Content, webRoot);

    private static string Normal(string path) => Path.GetFullPath(path);

    [Fact]
    public void A_folder_below_the_data_folder_is_allowed_whether_it_exists_or_not()
    {
        Assert.Equal(Normal(Path.Combine(Data, "mirror")), Resolve(Path.Combine(Data, "mirror")));
        Assert.Equal(Normal(Path.Combine(Data, "a", "b", "c")), Resolve(Path.Combine(Data, "a", "b", "c")));

        Directory.CreateDirectory(Path.Combine(Data, "there"));
        Assert.Equal(Normal(Path.Combine(Data, "there")), Resolve(Path.Combine(Data, "there")));
    }

    [Fact]
    public void A_relative_folder_is_relative_to_the_program_folder()
    {
        Assert.Throws<InvalidInputException>(() => Resolve("backups")); // app/backups is not below data
        Assert.Equal(Normal(Path.Combine(Data, "x")), Resolve(Path.Combine("..", "data", "x")));
    }

    [Fact]
    public void A_name_that_only_starts_with_two_dots_is_a_folder_like_any_other()
    {
        Assert.Equal(Normal(Path.Combine(Data, "..backups")), Resolve(Path.Combine(Data, "..backups")));
    }

    [Fact]
    public void Dot_dot_cannot_leave_the_data_folder()
    {
        Assert.Throws<InvalidInputException>(() => Resolve(Path.Combine(Data, "..", "outside")));
        Assert.Throws<InvalidInputException>(() => Resolve(Path.Combine(Data, "a", "..", "..", "outside")));
        Assert.Throws<InvalidInputException>(() => Resolve(Path.Combine("..", "outside")));
    }

    [Fact]
    public void A_colon_in_a_folder_name_on_Windows_is_refused_not_a_server_error()
    {
        if (!OperatingSystem.IsWindows()) return; // on Windows a colon in a name is not a valid path, elsewhere it is just a character

        Assert.Throws<InvalidInputException>(() => Resolve(Path.Combine(Data, "a:b")));
        Assert.Throws<InvalidInputException>(() => Resolve(Path.Combine(Data, "x", "c:d", "e")));
    }

    [Fact]
    public void The_data_folder_itself_and_its_parents_are_refused()
    {
        Assert.Throws<InvalidInputException>(() => Resolve(Data));
        Assert.Throws<InvalidInputException>(() => Resolve(Data + Path.DirectorySeparatorChar));
        Assert.Throws<InvalidInputException>(() => Resolve(_root));
        Assert.Throws<InvalidInputException>(() => Resolve(Path.GetPathRoot(_root)!));
    }

    [Fact]
    public void A_sibling_whose_name_starts_like_the_data_folder_is_outside()
    {
        Assert.Throws<InvalidInputException>(() => Resolve(Data + "-other"));
    }

    [Fact]
    public void The_message_names_the_allowed_folder()
    {
        var ex = Assert.Throws<InvalidInputException>(() => Resolve(Content));
        Assert.Contains(Normal(Data), ex.Message);
    }

    [Fact]
    public void The_folder_the_web_server_serves_is_refused_even_below_the_data_folder()
    {
        var web = Path.Combine(Data, "www");

        Assert.Throws<InvalidInputException>(() => Resolve(web, web));
        Assert.Throws<InvalidInputException>(() => Resolve(Path.Combine(web, "backups"), web));
        Assert.Equal(Normal(Path.Combine(Data, "www-not")), Resolve(Path.Combine(Data, "www-not"), web));
    }

    [Fact]
    public void The_wwwroot_of_the_program_folder_is_refused_when_the_data_folder_is_there()
    {
        // The settings file in the program folder (the default layout without a data folder of its own) must not open wwwroot to backups.
        var settings = Path.Combine(Content, "settings.json");

        var ex = Assert.Throws<InvalidInputException>(() => BackupFolder.Resolve(Path.Combine(Content, "wwwroot", "files"), settings, Content, null));
        Assert.Contains("web server", ex.Message);
        Assert.Equal(Normal(Path.Combine(Content, "backups")), BackupFolder.Resolve(Path.Combine(Content, "backups"), settings, Content, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void No_web_root_is_nothing_to_avoid(string? webRoot)
    {
        Assert.Equal(Normal(Path.Combine(Data, "ok")), Resolve(Path.Combine(Data, "ok"), webRoot));
    }

    [Fact]
    public void A_link_inside_the_data_folder_that_points_out_of_it_is_followed()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        if (!TryLink(Path.Combine(Data, "link"), outside)) return; // creating links needs a privilege on Windows

        Assert.Throws<InvalidInputException>(() => Resolve(Path.Combine(Data, "link")));
        Assert.Throws<InvalidInputException>(() => Resolve(Path.Combine(Data, "link", "deeper", "still")));
    }

    [Fact]
    public void A_link_that_stays_in_the_data_folder_is_resolved_to_its_target()
    {
        var real = Directory.CreateDirectory(Path.Combine(Data, "real")).FullName;
        if (!TryLink(Path.Combine(Data, "alias"), real)) return;

        Assert.Equal(Normal(Path.Combine(real, "sub")), Resolve(Path.Combine(Data, "alias", "sub")));
    }

    [Fact]
    public void A_link_into_the_web_root_is_followed_too()
    {
        var web = Directory.CreateDirectory(Path.Combine(Data, "www")).FullName;
        if (!TryLink(Path.Combine(Data, "public"), web)) return;

        Assert.Throws<InvalidInputException>(() => Resolve(Path.Combine(Data, "public", "backups"), web));
    }

    [Fact]
    public void A_data_folder_that_is_itself_a_link_still_allows_folders_below_it()
    {
        // /data is often a mount or link; the settings file path and the backup folder name it the same way.
        var real = Directory.CreateDirectory(Path.Combine(_root, "volume")).FullName;
        var link = Path.Combine(_root, "linked-data");
        if (!TryLink(link, real)) return;

        var folder = BackupFolder.Resolve(Path.Combine(link, "mirror"), Path.Combine(link, "settings.json"), Content, null);

        Assert.Equal(Normal(Path.Combine(real, "mirror")), folder);
    }

    [Fact]
    public void A_link_that_points_nowhere_does_not_open_a_way_out()
    {
        var missing = Path.Combine(_root, "not-there");
        if (!TryLink(Path.Combine(Data, "dangling"), missing)) return;

        Assert.Throws<InvalidInputException>(() => Resolve(Path.Combine(Data, "dangling", "x")));
    }

    [Fact]
    public void A_link_to_a_path_below_another_link_is_followed_to_the_end()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside", "x")).Parent!.FullName;
        if (!TryLink(Path.Combine(Data, "hop"), outside)) return;
        if (!TryLink(Path.Combine(Data, "alias"), Path.Combine(Data, "hop", "x"))) return; // looks like a folder in data, is outside/x

        Assert.Throws<InvalidInputException>(() => Resolve(Path.Combine(Data, "alias")));
        Assert.Throws<InvalidInputException>(() => Resolve(Path.Combine(Data, "alias", "deeper")));
    }

    [Fact]
    public void Links_that_run_in_a_circle_are_refused()
    {
        if (!TryLink(Path.Combine(Data, "a"), Path.Combine(Data, "b"))) return;
        if (!TryLink(Path.Combine(Data, "b"), Path.Combine(Data, "a"))) return;

        Assert.Throws<InvalidInputException>(() => Resolve(Path.Combine(Data, "a", "x")));
    }

    [Fact]
    public void Real_replaces_links_on_the_way_and_keeps_what_does_not_exist()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "volume")).FullName;
        var link = Path.Combine(_root, "mounted");
        Assert.Equal(Normal(Path.Combine(_root, "plain", "x.db")), BackupFolder.Real(Path.Combine(_root, "plain", "x.db")));
        if (!TryLink(link, real)) return;

        Assert.Equal(Normal(Path.Combine(real, "a", "planner.db")), BackupFolder.Real(Path.Combine(link, "a", "planner.db")));
    }

    internal static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return OperatingSystem.IsWindows() && TryJunction(link, target);
        }
    }

    /// <summary>A junction needs no privilege on Windows and is a link to <c>LinkTarget</c> as well.</summary>
    private static bool TryJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        process!.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }
}
