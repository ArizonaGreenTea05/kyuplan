using Kyuplan.Client.Api;

namespace Kyuplan.Client.Storage;

/// <summary>
/// Where a client keeps the login the user chose to remember. Only the WPF client has one, and keeps the login encrypted for
/// the user's Windows account. The web client deliberately has none: a page has no safe place for a password, so it leaves
/// asking for the login, and remembering it for the session, to the browser's own login dialog.
/// </summary>
public interface ILoginStore
{
    /// <summary>The remembered login, or <c>null</c> when there is none, it can no longer be read, or it was saved for another API.</summary>
    Login? Load();

    void Save(Login login);

    /// <summary>Forgets the remembered login.</summary>
    void Clear();
}
