using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Kyuplan.Client;
using Kyuplan.Client.Api;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Tests;

internal sealed class FakeClientUpdater : IClientUpdater
{
    public string Platform { get; set; } = ClientPlatform.Windows;

    public string InstallerExtension => "exe";

    public string CurrentVersion { get; set; } = "1.0.0";

    /// <summary>The installer the program was asked to run, with what the downloaded file held at that moment.</summary>
    public List<(string Path, byte[] Content)> Installed { get; } = [];

    public void Install(string installerPath) => Installed.Add((installerPath, File.ReadAllBytes(installerPath)));
}

public class ClientVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.2", true)]
    [InlineData("1.10", "1.9", true)]
    [InlineData("2.0", "1.99.99", true)]
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("1.2.2", "1.2.3", false)]
    [InlineData("1.2.3", "1.2.3-beta.1", true)] // a release is later than its prereleases
    [InlineData("1.2.3-beta.1", "1.2.3", false)]
    [InlineData("1.2.3-beta.2", "1.2.3-beta.1", true)]
    [InlineData("1.2.3-beta.10", "1.2.3-beta.9", true)]
    [InlineData("1.2.3-rc.1", "1.2.3-beta.5", true)]
    [InlineData("1.2.4", "1.2.3+abc123", true)] // a build number does not count
    [InlineData("1.2.3", "1.2.3+abc123", false)]
    [InlineData(null, "1.0", false)] // the server has no installer
    [InlineData("1.0", null, false)]
    [InlineData("garbage", "1.0", false)]
    public void IsNewer_compares_versions_like_semantic_versions(string? available, string? current, bool expected) =>
        Assert.Equal(expected, ClientDownload.IsNewer(available, current));

    [Theory]
    [InlineData("Kyuplan-windows-1.2.3.exe", "windows", "1.2.3")]
    [InlineData("Kyuplan-windows-0.1.exe", "windows", "0.1")]
    [InlineData("Kyuplan-windows-1.2.3-beta.1.exe", "windows", "1.2.3-beta.1")]
    [InlineData("Kyuplan-macos-2.0.dmg", "macos", "2.0")]
    [InlineData("Kyuplan-linux-2.0.0.AppImage", "linux", "2.0.0")]
    [InlineData("kyuplan-Linux-2.0.deb", "linux", "2.0")]
    [InlineData("Kyuplan-android-1.2.3.apk", "android", "1.2.3")]
    [InlineData("Kyuplan-android-2.0.0-beta.1.APK", "android", "2.0.0-beta.1")]
    public void Platform_and_version_are_read_from_the_file_name(string name, string platform, string version) =>
        Assert.Equal((platform, version), ClientDownload.Parse(name));

    [Theory]
    [InlineData("Kyuplan-windows-.exe")]
    [InlineData("Kyuplan-windows-1.2.3.txt")]
    [InlineData("Kyuplan-beos-1.2.3.exe")]
    [InlineData("Kyuplan-Setup-1.2.3.exe")]
    [InlineData("Other-windows-1.2.3.exe")]
    [InlineData("Kyuplan-android-1.2.3.apk.sig")]
    [InlineData("Kyuplan-android-1.2.3.aab")]
    public void Other_files_are_no_installers(string name) => Assert.Null(ClientDownload.Parse(name));

    [Fact]
    public void Every_platform_has_a_name_and_an_address()
    {
        Assert.All(ClientPlatform.All, p => Assert.True(ClientPlatform.IsKnown(p)));
        Assert.Equal("macOS", ClientPlatform.DisplayName(ClientPlatform.MacOs));
        Assert.Equal("api/client/linux", ClientDownload.PathFor(ClientPlatform.Linux));
        Assert.Equal("Android", ClientPlatform.DisplayName(ClientPlatform.Android));
        Assert.Equal("api/client/android", ClientDownload.PathFor(ClientPlatform.Android));
        Assert.Equal("Kyuplan-android-1.2.3.apk", ClientDownload.FileName(ClientPlatform.Android, "1.2.3", "apk"));
        Assert.True(ClientPlatform.IsKnown("android"));
        Assert.False(ClientPlatform.IsKnown("beos"));
    }
}

public class UpdateSignatureTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("kyuplan-signature").FullName;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_folder, true);
    }

    private string Installer(byte[] content, byte[]? signature = null)
    {
        var path = Path.Combine(_folder, "Kyuplan-windows-1.0.exe");
        File.WriteAllBytes(path, content);
        File.WriteAllBytes(path + ".sig", signature ?? _key.SignData(content, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        return path;
    }

    private string PublicKey => _key.ExportSubjectPublicKeyInfoPem();

    [Fact]
    public void A_signature_of_the_publisher_is_valid()
    {
        var installer = Installer([1, 2, 3]);

        Assert.True(UpdateSignature.IsValid(installer, installer + ".sig", PublicKey));
    }

    [Fact]
    public void A_changed_installer_is_not_valid()
    {
        var installer = Installer([1, 2, 3]);
        File.WriteAllBytes(installer, [1, 2, 4]);

        Assert.False(UpdateSignature.IsValid(installer, installer + ".sig", PublicKey));
    }

    [Fact]
    public void A_signature_of_another_key_is_not_valid()
    {
        var installer = Installer([1, 2, 3]);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.False(UpdateSignature.IsValid(installer, installer + ".sig", other.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void Garbage_or_a_missing_signature_is_not_valid()
    {
        var installer = Installer([1, 2, 3], [9, 9, 9]);
        Assert.False(UpdateSignature.IsValid(installer, installer + ".sig", PublicKey));

        File.Delete(installer + ".sig");
        Assert.False(UpdateSignature.IsValid(installer, installer + ".sig", PublicKey));
        Assert.False(UpdateSignature.IsValid(installer, installer + ".sig", "not a key"));
    }
}

public class ClientUpdateViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly FakeClientUpdater _updater = new();
    private readonly ShellViewModel _shell;
    private readonly ClientUpdateViewModel _update;

    public ClientUpdateViewModelTests()
    {
        _shell = new ShellViewModel(_api);
        _update = new ClientUpdateViewModel(_api, _shell, _updater);
    }

    public void Dispose() => _update.Dispose();

    private void ServerCarries(string? version, string platform = ClientPlatform.Windows) =>
        _api.Info = _api.Info with { ClientVersions = version is null ? null : new Dictionary<string, string> { [platform] = version } };

    [Fact]
    public async Task A_server_without_an_installer_offers_no_update()
    {
        await _update.CheckAsync();

        Assert.False(_update.IsUpdateAvailable);
        Assert.False(_update.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_newer_installer_on_the_server_is_offered()
    {
        ServerCarries("1.1.0");

        await _update.CheckAsync();

        Assert.True(_update.IsUpdateAvailable);
        Assert.Contains("1.1.0", _update.ButtonText);
        Assert.True(_update.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_installer_for_another_system_is_no_update_for_this_one()
    {
        ServerCarries("9.0", ClientPlatform.Linux);

        await _update.CheckAsync();

        Assert.False(_update.IsUpdateAvailable);
    }

    [Fact]
    public async Task A_client_of_another_system_asks_for_its_own_installer()
    {
        _updater.Platform = ClientPlatform.Linux;
        var linux = new ClientUpdateViewModel(_api, _shell, _updater);
        ServerCarries("2.0", ClientPlatform.Linux);

        await linux.CheckAsync();
        await linux.InstallCommand.ExecuteAsync(null);

        Assert.Equal([ClientPlatform.Linux], _api.ClientDownloads);
        Assert.Equal("Kyuplan-linux-2.0.exe", Path.GetFileName(Assert.Single(_updater.Installed).Path));
        File.Delete(_updater.Installed[0].Path);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9")]
    public async Task The_same_or_an_older_installer_is_not_an_update(string version)
    {
        ServerCarries(version);

        await _update.CheckAsync();

        Assert.False(_update.IsUpdateAvailable);
    }

    [Fact]
    public async Task What_the_shell_learned_from_the_server_counts_without_asking_again()
    {
        ServerCarries("1.1.0");
        var changed = new List<string?>();
        _update.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await _shell.InitializeAsync();

        Assert.True(_update.IsUpdateAvailable);
        Assert.Contains(nameof(ClientUpdateViewModel.IsUpdateAvailable), changed);
    }

    [Fact]
    public async Task An_unreachable_server_keeps_what_was_known_and_is_no_error()
    {
        ServerCarries("1.1.0");
        await _update.CheckAsync();

        _api.FailWith = new PlannerApiException("down", null);
        await _update.CheckAsync();

        Assert.True(_update.IsUpdateAvailable);
        Assert.False(_update.HasError);
    }

    [Fact]
    public async Task Installing_downloads_the_installer_and_runs_it()
    {
        ServerCarries("1.1.0");
        _api.ClientInstaller = [9, 8, 7];
        await _update.CheckAsync();

        await _update.InstallCommand.ExecuteAsync(null);

        var run = Assert.Single(_updater.Installed);
        Assert.Equal("Kyuplan-windows-1.1.0.exe", Path.GetFileName(run.Path));
        Assert.Equal([ClientPlatform.Windows], _api.ClientDownloads);
        Assert.Equal(new byte[] { 9, 8, 7 }, run.Content);
        Assert.False(_update.HasError);
        File.Delete(run.Path);
    }

    [Fact]
    public async Task The_signature_is_downloaded_beside_the_installer_when_the_server_has_one()
    {
        ServerCarries("1.1.0");
        _api.ClientSignature = [4, 5];
        await _update.CheckAsync();

        await _update.InstallCommand.ExecuteAsync(null);

        var installer = Assert.Single(_updater.Installed).Path;
        Assert.Equal(new byte[] { 4, 5 }, File.ReadAllBytes(installer + ClientDownload.SignatureExtension));
        File.Delete(installer);
        File.Delete(installer + ClientDownload.SignatureExtension);
    }

    [Fact]
    public async Task An_unsigned_release_leaves_no_signature_file_behind()
    {
        ServerCarries("1.1.0");
        var stale = Path.Combine(Path.GetTempPath(), "Kyuplan-windows-1.1.0.exe" + ClientDownload.SignatureExtension);
        File.WriteAllBytes(stale, [1]);
        await _update.CheckAsync();

        await _update.InstallCommand.ExecuteAsync(null);

        Assert.False(File.Exists(stale));
        File.Delete(Assert.Single(_updater.Installed).Path);
    }

    [Fact]
    public async Task A_failed_download_runs_nothing_and_is_reported()
    {
        ServerCarries("1.1.0");
        await _update.CheckAsync();
        _api.FailWith = new PlannerApiException("The server went away.", null);

        await _update.InstallCommand.ExecuteAsync(null);

        Assert.Empty(_updater.Installed);
        Assert.Equal("The server went away.", _update.ErrorMessage);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "Kyuplan-windows-1.1.0.exe")));
    }
}

/// <summary>The server carries the Windows installer in a folder and offers it to the clients.</summary>
public sealed class ClientDownloadApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly InMemoryDatabase _database = new();
    private readonly InMemoryDatabase _accounts = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("kyuplan-downloads").FullName;

    public void Dispose()
    {
        _database.Dispose();
        _accounts.Dispose();
        Directory.Delete(_folder, true);
    }

    private WebApplicationFactory<Program> Host() => factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Spond:UseDemoData", "true");
        builder.UseSetting("Sync:Enabled", "false");
        builder.UseSetting("DefaultData:Enabled", "false");
        builder.UseSetting("Database:ConnectionString", _database.ConnectionString);
        builder.UseSetting("ClientDownload:Directory", _folder);
        TestAccounts.Configure(builder, _accounts);
    });

    private void Put(string name, params byte[] content) => File.WriteAllBytes(Path.Combine(_folder, name), content);

    [Fact]
    public async Task Without_an_installer_there_is_no_download_and_no_version()
    {
        var client = Host().CreateSignedInClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/" + ClientDownload.PathFor(ClientPlatform.Windows))).StatusCode);
        Assert.Null((await new PlannerApiClient(client).GetInfoAsync()).ClientVersion(ClientPlatform.Windows));
    }

    [Fact]
    public async Task The_installer_is_offered_with_its_version_and_can_be_downloaded()
    {
        Put("Kyuplan-windows-1.2.3.exe", 1, 2, 3, 4);
        Put("Kyuplan-macos-1.2.2.dmg", 5, 6);
        Put("notes.txt", 0);
        var client = Host().CreateSignedInClient();
        var api = new PlannerApiClient(client);

        var info = await api.GetInfoAsync();
        Assert.Equal("1.2.3", info.ClientVersion(ClientPlatform.Windows));
        Assert.Equal("1.2.2", info.ClientVersion(ClientPlatform.MacOs));
        Assert.Null(info.ClientVersion(ClientPlatform.Linux));

        using var received = new MemoryStream();
        await api.DownloadClientAsync(ClientPlatform.Windows, received);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, received.ToArray());

        using var mac = new MemoryStream();
        await api.DownloadClientAsync(ClientPlatform.MacOs, mac);
        Assert.Equal(new byte[] { 5, 6 }, mac.ToArray());

        var response = await client.GetAsync("/" + ClientDownload.PathFor(ClientPlatform.Windows));
        Assert.Equal("Kyuplan-windows-1.2.3.exe", response.Content.Headers.ContentDisposition?.FileName);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/" + ClientDownload.PathFor(ClientPlatform.Linux))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/client/beos")).StatusCode);
    }

    [Fact]
    public async Task The_android_app_is_offered_like_the_desktop_installers()
    {
        Put("Kyuplan-android-1.2.3.apk", 9, 8, 7);
        Put("Kyuplan-android-1.2.3.apk.sig", 6);
        var client = Host().CreateSignedInClient();
        var api = new PlannerApiClient(client);

        Assert.Equal("1.2.3", (await api.GetInfoAsync()).ClientVersion(ClientPlatform.Android));

        using var received = new MemoryStream();
        await api.DownloadClientAsync(ClientPlatform.Android, received);
        Assert.Equal(new byte[] { 9, 8, 7 }, received.ToArray());
        Assert.Equal("Kyuplan-android-1.2.3.apk", (await client.GetAsync("/" + ClientDownload.PathFor(ClientPlatform.Android))).Content.Headers.ContentDisposition?.FileName);

        using var signature = new MemoryStream();
        Assert.True(await api.DownloadClientSignatureAsync(ClientPlatform.Android, signature));
        Assert.Equal(new byte[] { 6 }, signature.ToArray());
    }

    [Fact]
    public async Task The_signature_beside_the_installer_is_offered_and_missing_is_404()
    {
        Put("Kyuplan-windows-1.2.3.exe", 1);
        var api = new PlannerApiClient(Host().CreateSignedInClient());

        using var none = new MemoryStream();
        Assert.False(await api.DownloadClientSignatureAsync(ClientPlatform.Windows, none));
        Assert.Empty(none.ToArray());

        Put("Kyuplan-windows-1.2.3.exe.sig", 7, 7);
        using var signature = new MemoryStream();
        Assert.True(await api.DownloadClientSignatureAsync(ClientPlatform.Windows, signature));
        Assert.Equal(new byte[] { 7, 7 }, signature.ToArray());
        Assert.Equal("1.2.3", (await api.GetInfoAsync()).ClientVersion(ClientPlatform.Windows)); // the .sig file is no installer
    }

    [Fact]
    public async Task The_newest_of_several_installers_is_offered()
    {
        Put("Kyuplan-windows-1.2.exe", 1);
        Put("Kyuplan-windows-1.10.0.exe", 2);
        Put("Kyuplan-windows-1.9.0.exe", 3);
        var api = new PlannerApiClient(Host().CreateSignedInClient());

        Assert.Equal("1.10.0", (await api.GetInfoAsync()).ClientVersion(ClientPlatform.Windows));
    }

    [Fact]
    public async Task The_download_needs_a_login()
    {
        Put("Kyuplan-windows-1.2.3.exe", 1);

        var response = await Host().CreateClient().GetAsync("/" + ClientDownload.PathFor(ClientPlatform.Windows));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
