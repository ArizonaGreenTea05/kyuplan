using Android.Content;
using Android.Provider;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.AndroidApp.Services;

/// <summary>
/// Hands the installer of a newer version (the APK the server carries) to Android, which asks the user to confirm and replaces the app. Android
/// itself only accepts an update that is signed with the key of the installed app, so a server that has been taken over cannot push code to the
/// phones; the signature file of the release (<see cref="ClientDownload.SignatureExtension"/>) is checked on top when there is one.
/// </summary>
public sealed class AndroidClientUpdater(Context context) : IClientUpdater
{
    public string Platform => ClientPlatform.Android;

    public string InstallerExtension => "apk";

    public string CurrentVersion { get; } = context.PackageManager!.GetPackageInfo(context.PackageName!, 0)?.VersionName?.Split('+')[0] ?? "0.0.0";

    public void Install(string installerPath)
    {
        var signaturePath = installerPath + ClientDownload.SignatureExtension;
        if (File.Exists(signaturePath) && !IsSignedWithUpdateKey(installerPath, signaturePath))
        {
            File.Delete(installerPath);
            File.Delete(signaturePath);
            throw new InvalidOperationException("The downloaded update is not signed by the publisher of this app, so it is not installed.");
        }

        // Installing apps is a permission the user gives per app, in the system settings (Android 8 and later; before, one setting for all apps).
        // The first time it is asked for, and the update waits.
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && !context.PackageManager!.CanRequestPackageInstalls())
        {
            var settings = new Intent(Settings.ActionManageUnknownAppSources, Android.Net.Uri.Parse("package:" + context.PackageName));
            settings.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(settings);
            throw new InvalidOperationException(Strings.Update_AllowInstall);
        }

        var install = new Intent(Intent.ActionView);
        install.SetDataAndType(UpdateFileProvider.UriFor(context, installerPath), "application/vnd.android.package-archive");
        install.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        context.StartActivity(install);
    }

    /// <summary>The release carries a signature made with the private update key; the public key is built into the app (<c>installer/update-key.pub.pem</c>).</summary>
    private static bool IsSignedWithUpdateKey(string installerPath, string signaturePath)
    {
        using var stream = typeof(AndroidClientUpdater).Assembly.GetManifestResourceStream("update-key.pub.pem");
        if (stream is null) return false;

        using var reader = new StreamReader(stream);
        return UpdateSignature.IsValid(installerPath, signaturePath, reader.ReadToEnd());
    }
}
