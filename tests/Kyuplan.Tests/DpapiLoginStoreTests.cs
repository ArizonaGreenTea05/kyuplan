using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Options;
using Kyuplan.Client.Api;
using Kyuplan.Client.WpfApp.Services;

namespace Kyuplan.Tests;

/// <summary>
/// The WPF client's encrypted login file. The store's source file is compiled into this project (see the csproj): the WPF
/// project itself targets Windows and cannot be referenced from here. DPAPI is Windows only, like the client.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiLoginStoreTests : IDisposable
{
    private const string Api = "http://localhost:5100/";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pp-login-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private DpapiLoginStore Store(string api = Api) =>
        new(Options.Create(new PlannerClientOptions { BaseAddress = api }), _folder);

    private string FilePath => Path.Combine(_folder, "login.dat");

    [Fact]
    public void Nothing_is_remembered_to_begin_with()
    {
        Assert.Null(Store().Load());
    }

    [Fact]
    public void A_saved_login_comes_back_after_a_restart_and_is_not_readable_in_the_file()
    {
        var login = new Login("Jürgen Köhler", "pä:ss wörd 123");
        Store().Save(login);

        Assert.Equal(login, Store().Load()); // a new store: as after restarting the app

        var file = File.ReadAllBytes(FilePath);
        foreach (var secret in new[] { "Köhler", "pä:ss", "wörd 123", "Jürgen" })
        {
            Assert.False(Contains(file, Encoding.UTF8.GetBytes(secret)), $"'{secret}' is readable in the file (UTF-8)");
            Assert.False(Contains(file, Encoding.Unicode.GetBytes(secret)), $"'{secret}' is readable in the file (UTF-16)");
        }

        Assert.False(Contains(file, Encoding.ASCII.GetBytes("localhost")), "the file is encrypted as a whole, not only the password");
    }

    [Fact]
    public void A_login_is_only_handed_out_for_the_api_it_was_saved_for()
    {
        Store("http://localhost:5100/").Save(new Login("anna", "correct horse"));

        Assert.Null(Store("https://planner.example.org/").Load());
        Assert.Null(Store("http://localhost:5101/").Load());

        // The same API written another way is still that API.
        Assert.Equal(new Login("anna", "correct horse"), Store("http://LOCALHOST:5100").Load());
    }

    [Fact]
    public void Saving_again_replaces_the_login()
    {
        Store().Save(new Login("anna", "correct horse"));
        Store().Save(new Login("ben", "battery staple"));

        Assert.Equal(new Login("ben", "battery staple"), Store().Load());
    }

    [Fact]
    public void Clearing_forgets_the_login_and_needs_nothing_to_forget()
    {
        var store = Store();
        store.Clear(); // no file, no folder: nothing to do, no error

        store.Save(new Login("anna", "correct horse"));
        store.Clear();

        Assert.Null(store.Load());
        Assert.False(File.Exists(FilePath));
    }

    [Theory]
    [InlineData("not encrypted at all")]
    [InlineData("")]
    public void A_file_that_cannot_be_read_counts_as_nothing_remembered(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, content);

        Assert.Null(Store().Load());
    }

    [Fact]
    public void A_damaged_encrypted_file_counts_as_nothing_remembered()
    {
        Store().Save(new Login("anna", "correct horse"));
        var bytes = File.ReadAllBytes(FilePath);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(FilePath, bytes);

        Assert.Null(Store().Load());
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
