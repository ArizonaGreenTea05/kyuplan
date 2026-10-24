using Android.Content;

namespace Kyuplan.Client.AndroidApp.Services;

/// <summary>What the app remembers besides the login: the address of the server and the language the user picked. The app preferences of Android, private to this app.</summary>
public sealed class AppPreferences(Context context)
{
    private const string ApiAddressKey = "api-address";
    private const string LanguageKey = "language";

    private ISharedPreferences Store => context.GetSharedPreferences("kyuplan", FileCreationMode.Private)!;

    /// <summary>The address of the API the user chose (already normalised), <c>null</c> on the first start.</summary>
    public string? ApiAddress
    {
        get => Store.GetString(ApiAddressKey, null);
        set => Store.Edit()!.PutString(ApiAddressKey, value)!.Apply();
    }

    /// <summary>The language code the user picked, <c>null</c> while the app follows the system.</summary>
    public string? Language
    {
        get => Store.GetString(LanguageKey, null);
        set => Store.Edit()!.PutString(LanguageKey, value)!.Apply();
    }
}
