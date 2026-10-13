using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using Kyuplan.Client;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.WpfApp.Services;

/// <summary>Runs the installer of a newer version (the one the server carries) and ends the program, which the installer then starts again.</summary>
public sealed class WindowsClientUpdater : IClientUpdater
{
    public string Platform => ClientPlatform.Windows;

    public string InstallerExtension => "exe";

    public string CurrentVersion { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public void Install(string installerPath)
    {
        var signaturePath = installerPath + ClientDownload.SignatureExtension;
        if (!IsSignedWithUpdateKey(installerPath, signaturePath) && !IsFromSamePublisher(installerPath, Environment.ProcessPath, AllowUnsigned))
        {
            File.Delete(installerPath);
            File.Delete(signaturePath);
            throw new InvalidOperationException("The downloaded installer is not signed by the publisher of this program, so it is not run.");
        }

        // /Relaunch=1 makes the installer start the program when it is done (installer/Kyuplan.iss); a silent setup would not otherwise.
        Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SUPPRESSMSGBOXES /Relaunch=1") { UseShellExecute = true });
        Application.Current.Shutdown();
    }

    /// <summary>The installer carries a signature made with the private update key of the release; the public key is built into this program (<c>installer/update-key.pub.pem</c>). Needs no certificate.</summary>
    private static bool IsSignedWithUpdateKey(string installerPath, string signaturePath)
    {
        using var stream = typeof(WindowsClientUpdater).Assembly.GetManifestResourceStream("update-key.pub.pem");
        if (stream is null) return false;

        using var reader = new StreamReader(stream);
        return UpdateSignature.IsValid(installerPath, signaturePath, reader.ReadToEnd());
    }

    /// <summary>Environment variable that lets a program that is not signed (a build from source) run the installer of its server anyway; for development only.</summary>
    public const string AllowUnsignedVariable = "KYUPLAN_ALLOW_UNSIGNED_UPDATES";

    private static bool AllowUnsigned => Environment.GetEnvironmentVariable(AllowUnsignedVariable) == "1";

    /// <summary>
    /// Only an installer that is signed (Authenticode) with the same certificate as this program, with a valid chain, is run, so a server that
    /// has been taken over cannot push code to every client. A program that is not signed has nothing to compare with and runs no installer
    /// at all (the update then has to be done by hand), unless <paramref name="allowUnsigned"/> says otherwise.
    /// </summary>
    public static bool IsFromSamePublisher(string installerPath, string? currentExe, bool allowUnsigned = false)
    {
        X509Certificate2 current;
        try
        {
            if (currentExe is null) return allowUnsigned;
            current = new X509Certificate2(X509Certificate.CreateFromSignedFile(currentExe));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FileNotFoundException)
        {
            return allowUnsigned; // this program is not signed
        }

        try
        {
            using var installer = new X509Certificate2(X509Certificate.CreateFromSignedFile(installerPath));
            using var chain = new X509Chain();
            return installer.Thumbprint == current.Thumbprint && chain.Build(installer);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FileNotFoundException)
        {
            return false;
        }
    }
}
