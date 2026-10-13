using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Kyuplan.Client.Api;
using Kyuplan.Client.Storage;

namespace Kyuplan.Client.WpfApp.Services;

/// <summary>
/// Keeps the login the user chose to remember in <c>%LocalAppData%\Kyuplan\login.dat</c>, encrypted with the Windows
/// Data Protection API for the current Windows user: another user, or the same file on another computer, cannot read it. It
/// also records which API the login belongs to and is only handed out for that one, so changing the address of the API
/// never sends a password to a different server. A file that cannot be read counts as nothing remembered.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiLoginStore(IOptions<PlannerClientOptions> options, string? folder = null) : ILoginStore
{
    /// <summary>Mixed into the encryption, so that another program's DPAPI blob for this user is not accepted as ours.</summary>
    private static readonly byte[] Entropy = "Kyuplan.Login.v1"u8.ToArray();

    private readonly string _folder = folder ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kyuplan");

    private readonly string _api = Normalize(options.Value.BaseAddress);

    private string FilePath => Path.Combine(_folder, "login.dat");

    private sealed record Saved(string Api, string UserName, string Password);

    public Login? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var saved = JsonSerializer.Deserialize<Saved>(plain);
                return saved is { UserName.Length: > 0, Password.Length: > 0 } && Normalize(saved.Api) == _api
                    ? new Login(saved.UserName, saved.Password)
                    : null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return null; // another Windows user, a damaged file, a locked folder: just not remembered
        }
    }

    public void Save(Login login)
    {
        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(new Saved(_api, login.UserName, login.Password));
            try
            {
                Directory.CreateDirectory(_folder);
                File.WriteAllBytes(FilePath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // Not fatal: the login just is not remembered.
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more to do: the file stays, and the next start will try the login again.
        }
    }

    /// <summary>"http://Localhost:5100" and "http://localhost:5100/" are the same API.</summary>
    private static string Normalize(string address) =>
        Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ? uri.AbsoluteUri : address.Trim();
}
