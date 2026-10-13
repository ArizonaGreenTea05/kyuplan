using Kyuplan.Client.Api;
using Kyuplan.Client.Storage;

namespace Kyuplan.Client.Blazor;

/// <summary>
/// The browser keeps no login: a page has no safe place for a password. The login lives in the <see cref="LoginSession"/> for as long as
/// the page is open (a reload or signing out asks again), so this store remembers nothing.
/// </summary>
internal sealed class SessionOnlyLoginStore : ILoginStore
{
    public Login? Load() => null;

    public void Save(Login login)
    {
    }

    public void Clear()
    {
    }
}
