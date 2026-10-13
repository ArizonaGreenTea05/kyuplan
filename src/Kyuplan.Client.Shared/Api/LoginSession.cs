using System.Net.Http.Headers;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Kyuplan.Contracts;

namespace Kyuplan.Client.Api;

/// <summary>A user name and password, as the API wants them: HTTP Basic.</summary>
public sealed record Login(string UserName, string Password)
{
    /// <summary>A record prints all its properties; the password must not end up in a log or an error message that way.</summary>
    public override string ToString() => $"Login {{ UserName = {UserName}, Password = ***** }}";

    /// <summary>
    /// The login to send once <paramref name="saved"/> is what the API knows: the same name as long as it still signs in (it is the user name or
    /// an alias), otherwise the user name; with the new password, if there is one.
    /// </summary>
    public Login Renewed(UserDto saved, string? newPassword) => this with
    {
        UserName = saved.UserName.Equals(UserName, StringComparison.OrdinalIgnoreCase) || saved.Aliases.Contains(UserName, StringComparer.OrdinalIgnoreCase)
            ? UserName
            : saved.UserName,
        Password = newPassword ?? Password,
    };
}

/// <summary>
/// The login the API client sends with every request, once a client has asked the user for it (the WPF client does, see
/// <c>LoginViewModel</c>). It stays empty in the browser: there the browser's own login dialog asks, remembers the login for
/// the session and sends it itself.
/// </summary>
public sealed partial class LoginSession : ObservableObject
{
    [ObservableProperty]
    private Login? _login;

    /// <summary>Who the API said the login belongs to, once it has been checked.</summary>
    [ObservableProperty]
    private UserDto? _user;
}

/// <summary>Adds the <see cref="LoginSession"/>'s login, if there is one, as HTTP Basic credentials to every request.</summary>
public sealed class BasicAuthHandler(LoginSession session) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (session.Login is { } login)
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login.UserName}:{login.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
