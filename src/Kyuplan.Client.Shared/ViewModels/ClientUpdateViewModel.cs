using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>What the installed program contributes to updating itself: which platform it is built for, which version it is, and how an installer is run. Each desktop client has one.</summary>
public interface IClientUpdater
{
    /// <summary>The <see cref="ClientPlatform"/> this program is built for; it decides which installer of the server is its update.</summary>
    string Platform { get; }

    /// <summary>File extension of that platform's installer, without the dot (<c>exe</c>).</summary>
    string InstallerExtension { get; }

    /// <summary>The version of the running program, e.g. <c>1.2.3</c>.</summary>
    string CurrentVersion { get; }

    /// <summary>Runs the downloaded installer and ends the program, so that the installer can replace its files. The installer starts the program again. Its signature, when the server has one, is the file beside it (<see cref="ClientDownload.SignatureExtension"/>).</summary>
    void Install(string installerPath);
}

/// <summary>
/// The update of the desktop client: the server carries the installer of its own version (<see cref="ServerInfoDto.ClientVersions"/>, the entry of this program's platform), and a client
/// that is older offers to install it. The server is asked once the shell has its info, and again every <see cref="DefaultWatchInterval"/> by <see cref="Watch"/>.
/// </summary>
public sealed partial class ClientUpdateViewModel : ViewModelBase, IDisposable
{
    /// <summary>How often <see cref="Watch"/> asks the server: a release is rare and the client is often left open for days.</summary>
    public static readonly TimeSpan DefaultWatchInterval = TimeSpan.FromHours(1);

    private readonly IPlannerApi _api;
    private readonly IClientUpdater _updater;
    private CancellationTokenSource? _watching;

    public ClientUpdateViewModel(IPlannerApi api, ShellViewModel shell, IClientUpdater updater)
    {
        _api = api;
        _updater = updater;

        _availableVersion = shell.Info?.ClientVersion(updater.Platform);
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Info)) AvailableVersion = shell.Info?.ClientVersion(updater.Platform);
        };
    }

    /// <summary>The version of the installer the server carries, <c>null</c> when it has none or has not said yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateAvailable))]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    [NotifyPropertyChangedFor(nameof(Hint))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private string? _availableVersion;

    /// <summary>The server's installer is a later version than this program.</summary>
    public bool IsUpdateAvailable => ClientDownload.IsNewer(AvailableVersion, _updater.CurrentVersion);

    public string ButtonText => string.Format(Strings.Update_Button, AvailableVersion);

    public string Hint => string.Format(Strings.Update_Hint, _updater.CurrentVersion, AvailableVersion);

    /// <summary>Asks the server which installer it carries. A server that cannot be reached is not an error here: the next try is in an hour.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            AvailableVersion = (await _api.GetInfoAsync(cancellationToken)).ClientVersion(_updater.Platform);
        }
        catch (Exception ex) when (ex is PlannerApiException or OperationCanceledException)
        {
            // keeps what it knew
        }
    }

    /// <summary>Downloads the installer and hands it to the updater, which ends this program.</summary>
    [RelayCommand(CanExecute = nameof(IsUpdateAvailable))]
    private Task InstallAsync() => LoadAsync(async ct =>
    {
        var version = AvailableVersion!;
        var path = Path.Combine(Path.GetTempPath(), ClientDownload.FileName(_updater.Platform, version, _updater.InstallerExtension));
        var signaturePath = path + ClientDownload.SignatureExtension;
        try
        {
            await using (var file = File.Create(path))
            {
                await _api.DownloadClientAsync(_updater.Platform, file, ct);
            }

            // The signature lies beside the installer for the updater to check; none (an unsigned release) is left for it to find missing.
            File.Delete(signaturePath);
            await using var signature = File.Create(signaturePath);
            if (!await _api.DownloadClientSignatureAsync(_updater.Platform, signature, ct))
            {
                await signature.DisposeAsync();
                File.Delete(signaturePath);
            }
        }
        catch
        {
            File.Delete(path); // half an installer must not be run, or found there next time
            File.Delete(signaturePath);
            throw;
        }

        _updater.Install(path);
    });

    /// <summary>Checks every <paramref name="every"/> (<see cref="DefaultWatchInterval"/>) for as long as this lives. Does nothing when it is already watching.</summary>
    public void Watch(TimeSpan? every = null)
    {
        if (_watching is not null) return;

        _watching = new CancellationTokenSource();
        _ = WatchAsync(every ?? DefaultWatchInterval, _watching.Token);
    }

    private async Task WatchAsync(TimeSpan every, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(every);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)) await CheckAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // disposed
        }
    }

    public void Dispose() => _watching?.Cancel();
}
