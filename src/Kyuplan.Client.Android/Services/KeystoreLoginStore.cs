using System.Text.Json;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Kyuplan.Client.Api;
using Kyuplan.Client.Storage;

namespace Kyuplan.Client.AndroidApp.Services;

/// <summary>
/// Keeps the login the user chose to remember, encrypted (AES-GCM) with a key that lives in the Android Keystore and never leaves it: the
/// stored text is useless on another phone, or to another app. It also records which API the login belongs to and is only handed out for that
/// one, so changing the address of the server never sends a password to a different one. Whatever cannot be read counts as nothing remembered.
/// </summary>
public sealed class KeystoreLoginStore(Context context, string api) : ILoginStore
{
    private const string KeyAlias = "kyuplan.login";
    private const string Provider = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";

    private readonly string _api = Normalize(api);

    private ISharedPreferences Store => context.GetSharedPreferences("kyuplan-login", FileCreationMode.Private)!;

    private sealed record Saved(string Api, string UserName, string Password);

    public Login? Load()
    {
        try
        {
            var data = Store.GetString("data", null);
            var iv = Store.GetString("iv", null);
            if (data is null || iv is null) return null;

            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(128, Convert.FromBase64String(iv)));
            var saved = JsonSerializer.Deserialize<Saved>(cipher.DoFinal(Convert.FromBase64String(data))!);
            return saved is { UserName.Length: > 0, Password.Length: > 0 } && Normalize(saved.Api) == _api
                ? new Login(saved.UserName, saved.Password)
                : null;
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or GeneralSecurityException or FormatException or JsonException)
        {
            return null; // a key that was lost (a restored backup, a changed lock screen), a damaged text: just not remembered
        }
    }

    public void Save(Login login)
    {
        try
        {
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.EncryptMode, Key());
            var data = cipher.DoFinal(JsonSerializer.SerializeToUtf8Bytes(new Saved(_api, login.UserName, login.Password)))!;
            Store.Edit()!
                .PutString("data", Convert.ToBase64String(data))!
                .PutString("iv", Convert.ToBase64String(cipher.GetIV()!))!
                .Apply();
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or GeneralSecurityException)
        {
            // Not fatal: the login just is not remembered.
        }
    }

    public void Clear() => Store.Edit()!.Clear()!.Apply();

    /// <summary>The key of this app in the Keystore; made on first use.</summary>
    private static IKey Key()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        if (store.GetKey(KeyAlias, null) is { } existing) return existing;

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
        generator.Init(new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .Build());
        return generator.GenerateKey()!;
    }

    /// <summary>"http://Server:5100" and "http://server:5100/" are the same API.</summary>
    private static string Normalize(string address) => ApiAddress.TryNormalize(address, out var normalized) ? normalized : address.Trim().ToLowerInvariant();
}
