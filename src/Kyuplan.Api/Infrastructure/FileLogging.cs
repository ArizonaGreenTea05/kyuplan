using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Configuration;
using Serilog;
using Serilog.Debugging;
using Serilog.Extensions.Logging;

namespace Kyuplan.Api.Infrastructure;

internal static class FileLogging
{
    private const string Template = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Adds the log files of <see cref="FileLogOptions"/> next to the other log outputs. The setting is read when the provider is first
    /// needed (not at registration) so that configuration overrides, e.g. in tests, are honoured.
    /// </summary>
    public static ILoggingBuilder AddConfiguredFile(this ILoggingBuilder logging)
    {
        logging.Services.AddSingleton<ILoggerProvider>(sp =>
        {
            var path = sp.GetRequiredService<IOptions<FileLogOptions>>().Value.Resolve(sp.GetRequiredService<IHostEnvironment>().ContentRootPath);
            return path is null ? NullLoggerProvider.Instance : Create(path);
        });
        return logging;
    }

    internal static ILoggerProvider Create(string path)
    {
        // A file that cannot be written (no permission, disk full) must not stop the API; Serilog reports it here instead.
        SelfLog.Enable(Console.Error);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose() // the filters of Logging:LogLevel decide, as for the console
            .WriteTo.File(path, outputTemplate: Template, rollingInterval: RollingInterval.Day, shared: true)
            .CreateLogger();
        return new SerilogLoggerProvider(logger, dispose: true);
    }
}
