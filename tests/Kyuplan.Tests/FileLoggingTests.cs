using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Kyuplan.Api.Configuration;

namespace Kyuplan.Tests;

/// <summary>The log files of the API: <c>Logging:File:Path</c>, which ships with a path in appsettings.json.</summary>
public sealed class FileLoggingTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly InMemoryDatabase _planner = new();
    private readonly InMemoryDatabase _accounts = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("pp-logs-").FullName;

    public void Dispose()
    {
        _planner.Dispose();
        _accounts.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private WebApplicationFactory<Program> CreateHost(string logPath) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Spond:UseDemoData", "true");
            builder.UseSetting("Sync:Enabled", "false");
            builder.UseSetting("DefaultData:Enabled", "false"); // the tests fill their own tables; DefaultDataTests starts from the defaults
            builder.UseSetting("Database:ConnectionString", _planner.ConnectionString);
            TestAccounts.Configure(builder, _accounts);
            builder.UseSetting("Logging:File:Path", logPath);
        });

    /// <summary>Everything the files in the folder say, read while the host may still hold them open.</summary>
    private string ReadLog(string folder)
    {
        var lines = Directory.GetFiles(folder, "*.log").SelectMany(file =>
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n');
        });
        return string.Join('\n', lines);
    }

    [Fact]
    public void The_log_goes_to_a_file_named_by_the_setting_with_the_date_before_the_extension()
    {
        var folder = Path.Combine(_folder, "nested", "logs"); // does not exist yet
        using (var host = CreateHost(Path.Combine(folder, "api-.log")))
        {
            host.Services.GetRequiredService<ILogger<FileLoggingTests>>().LogWarning("Spond said {Thing} to Müller", "nothing");
        }

        var file = Assert.Single(Directory.GetFiles(folder));
        Assert.Equal($"api-{DateTime.Now:yyyyMMdd}.log", Path.GetFileName(file));

        var line = Assert.Single(ReadLog(folder).Split('\n'), l => l.Contains("Spond said"));
        Assert.Contains("[WRN]", line);
        Assert.Contains(nameof(FileLoggingTests), line);
        Assert.Contains("Spond said nothing to Müller", line);
    }

    [Fact]
    public void The_levels_of_Logging_LogLevel_apply_to_the_file_as_to_the_console()
    {
        using (var host = CreateHost(Path.Combine(_folder, "api-.log")))
        {
            var logger = host.Services.GetRequiredService<ILogger<FileLoggingTests>>();
            logger.LogDebug("too detailed");
            logger.LogInformation("just enough");
        }

        var log = ReadLog(_folder);
        Assert.Contains("just enough", log);
        Assert.DoesNotContain("too detailed", log);
    }

    [Fact]
    public void Exceptions_are_written_with_their_stack_trace()
    {
        using (var host = CreateHost(Path.Combine(_folder, "api-.log")))
        {
            try { throw new InvalidOperationException("kaputt"); }
            catch (Exception ex) { host.Services.GetRequiredService<ILogger<FileLoggingTests>>().LogError(ex, "Sync failed"); }
        }

        var log = ReadLog(_folder);
        Assert.Contains("Sync failed", log);
        Assert.Contains("System.InvalidOperationException: kaputt", log);
        Assert.Contains(nameof(Exceptions_are_written_with_their_stack_trace), log);
    }

    [Fact]
    public void An_empty_path_means_no_log_files()
    {
        using (var host = CreateHost(""))
        {
            host.Services.GetRequiredService<ILogger<FileLoggingTests>>().LogWarning("nobody keeps this");
        }

        Assert.Empty(Directory.GetFileSystemEntries(_folder));
    }

    [Fact]
    public async Task A_log_file_that_cannot_be_written_does_not_stop_the_API()
    {
        var blocker = Path.Combine(_folder, "a-file"); // a folder cannot be created below a file
        File.WriteAllText(blocker, "");

        using var host = CreateHost(Path.Combine(blocker, "logs", "api-.log"));
        host.Services.GetRequiredService<ILogger<FileLoggingTests>>().LogWarning("nowhere to go");
        using var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/branding")).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Resolve_gives_nothing_when_the_path_is_empty(string? path) =>
        Assert.Null(new FileLogOptions { Path = path }.Resolve(Path.GetTempPath()));

    [Fact]
    public void Resolve_makes_a_relative_path_relative_to_the_content_root_and_keeps_an_absolute_one()
    {
        var root = Path.Combine(Path.GetTempPath(), "content-root");
        Assert.Equal(Path.Combine(root, "logs", "api-.log"), new FileLogOptions { Path = "logs/api-.log" }.Resolve(root));

        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "api-.log");
        Assert.Equal(absolute, new FileLogOptions { Path = absolute }.Resolve(root));
    }
}

/// <summary>
/// appsettings.json ships with a log file path, which every test host would take: they would all write into the API project's folder
/// (several at the same time). They start without log files unless a test sets the path itself; an environment variable of a single
/// blank is used, because an empty one does not exist on Windows.
/// </summary>
internal static class NoLogFilesInTests
{
    [ModuleInitializer]
    internal static void Run() => Environment.SetEnvironmentVariable("Logging__File__Path", " ");
}
