using System.Diagnostics;
using System.Reflection;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.LinuxApp.Services;

/// <summary>
/// Installs the newer version the server carries: a pacman package (<c>Kyuplan-linux-1.2.3.pkg.tar.zst</c>, built by the release from <c>deploy/arch/PKGBUILD</c>).
/// polkit asks for the administrator's password (<c>pkexec pacman -U</c>), then the program starts again. The package is only installed when it
/// carries a valid signature of the release key, so a server that has been taken over cannot push code to every client.
/// </summary>
public sealed class LinuxClientUpdater(Action quit) : IClientUpdater
{
    /// <summary>Environment variable that lets a program that is not from a release (a build from source) install the package of its server anyway; for development only.</summary>
    public const string AllowUnsignedVariable = "KYUPLAN_ALLOW_UNSIGNED_UPDATES";

    public string Platform => ClientPlatform.Linux;

    public string InstallerExtension => "pkg.tar.zst";

    public string CurrentVersion { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public void Install(string installerPath)
    {
        var signaturePath = installerPath + ClientDownload.SignatureExtension;
        if (!IsSignedWithUpdateKey(installerPath, signaturePath) && Environment.GetEnvironmentVariable(AllowUnsignedVariable) != "1")
        {
            File.Delete(installerPath);
            File.Delete(signaturePath);
            throw new InvalidOperationException("The downloaded package is not signed by the publisher of this program, so it is not installed.");
        }

        var start = new ProcessStartInfo("setsid") { UseShellExecute = false };
        foreach (var argument in InstallCommand(installerPath)) start.ArgumentList.Add(argument);
        Process.Start(start);
        quit();
    }

    /// <summary>
    /// The command that installs the package and starts the program again, detached from this process (which ends right after starting it, so the
    /// files it runs from can be replaced). The program starts again even when the user cancelled the password prompt: nothing is lost then.
    /// </summary>
    public static IReadOnlyList<string> InstallCommand(string packagePath) =>
        ["sh", "-c", "pkexec pacman -U --noconfirm \"$1\"; rm -f \"$1\" \"$1.sig\"; exec kyuplan", "kyuplan-update", packagePath];

    private static bool IsSignedWithUpdateKey(string installerPath, string signaturePath)
    {
        using var stream = typeof(LinuxClientUpdater).Assembly.GetManifestResourceStream("update-key.pub.pem");
        if (stream is null) return false;

        using var reader = new StreamReader(stream);
        return UpdateSignature.IsValid(installerPath, signaturePath, reader.ReadToEnd());
    }
}
