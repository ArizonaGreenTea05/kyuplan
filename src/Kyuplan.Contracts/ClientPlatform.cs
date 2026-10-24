namespace Kyuplan.Contracts;

/// <summary>
/// The operating systems the desktop client is (or will be) built for. A platform is named by a lower-case id that appears in the address
/// of its download (<c>api/client/windows</c>), in the name of its installer file (<c>Kyuplan-windows-1.2.3.exe</c>) and in
/// <see cref="ServerInfoDto.ClientVersions"/>. A new client for another system only needs its id here, its installer in the server's
/// <c>downloads</c> folder, and an <c>IClientUpdater</c> that names its platform; the download, the version check and the web client's button follow.
/// </summary>
public static class ClientPlatform
{
    public const string Windows = "windows";
    public const string MacOs = "macos";
    public const string Linux = "linux";
    public const string Android = "android";

    public static readonly IReadOnlyList<string> All = [Windows, MacOs, Linux, Android];

    public static bool IsKnown(string? platform) => platform is not null && All.Contains(platform);

    /// <summary>The name shown to people ("Windows", "macOS"), the id for an unknown platform.</summary>
    public static string DisplayName(string platform) => platform switch
    {
        Windows => "Windows",
        MacOs => "macOS",
        Linux => "Linux",
        Android => "Android",
        _ => platform,
    };
}
