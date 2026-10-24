using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Kyuplan.Client.Api;
using Kyuplan.Client.Storage;

namespace Kyuplan.Client.LinuxApp.Services;

/// <summary>
/// Keeps the login the user chose to remember in the desktop's keyring (the freedesktop Secret Service: GNOME Keyring, KWallet, KeePassXC),
/// through <c>secret-tool</c> from libsecret. The password never touches the disk. The entry belongs to one API address and is only handed
/// out for that one, so changing the address never sends a password to a different server. Without <c>secret-tool</c> or a running keyring
/// nothing is remembered: the user signs in on every start, which is the safe way to fail.
/// </summary>
public sealed class SecretServiceLoginStore(IOptions<PlannerClientOptions> options, SecretServiceLoginStore.Runner? run = null) : ILoginStore
{
    /// <summary>Runs <c>secret-tool</c> with the arguments and the text for its standard input; returns its exit code and standard output.</summary>
    public delegate (int ExitCode, string Output) Runner(IReadOnlyList<string> arguments, string? input);

    private readonly string _api = Normalize(options.Value.BaseAddress);
    private readonly Runner _run = run ?? RunSecretTool;

    private sealed record Saved(string UserName, string Password);

    private string[] Attributes => ["application", "kyuplan", "api", _api];

    public Login? Load()
    {
        try
        {
            var (exit, output) = _run(["lookup", .. Attributes], null);
            if (exit != 0 || string.IsNullOrWhiteSpace(output)) return null;

            var saved = JsonSerializer.Deserialize<Saved>(output);
            return saved is { UserName.Length: > 0, Password.Length: > 0 } ? new Login(saved.UserName, saved.Password) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or JsonException or InvalidOperationException)
        {
            return null; // no secret-tool, no keyring, a damaged entry: just not remembered
        }
    }

    public void Save(Login login)
    {
        try
        {
            _run(["store", "--label", "Kyuplan login", .. Attributes], JsonSerializer.Serialize(new Saved(login.UserName, login.Password)));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            // Not fatal: the login just is not remembered.
        }
    }

    public void Clear()
    {
        try
        {
            _run(["clear", .. Attributes], null);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            // Nothing more to do: the entry stays, and the next start will try the login again.
        }
    }

    private static (int, string) RunSecretTool(IReadOnlyList<string> arguments, string? input)
    {
        var start = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("secret-tool did not start.");
        if (input is not null) process.StandardInput.Write(input);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            return (-1, "");
        }

        return (process.ExitCode, output);
    }

    /// <summary>"http://Localhost:5100" and "http://localhost:5100/" are the same API.</summary>
    public static string Normalize(string address) =>
        Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ? uri.AbsoluteUri : address.Trim();
}
