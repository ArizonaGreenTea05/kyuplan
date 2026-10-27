using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Plugins;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Accounts;

/// <summary>
/// HTTP Basic authentication (RFC 7617) against the accounts database: the <c>Authorization</c> header of every request
/// is checked, so there are no sessions or tokens to manage. An answer of 401 carries <c>WWW-Authenticate: Basic</c>,
/// which is what makes a browser show its own login dialog and then send the login with every later request. Only use it over HTTPS.
/// </summary>
public sealed class BasicAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AccountService accounts,
    LoginThrottle throttle,
    PluginHost plugins)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Basic";

    private const string Realm = "Kyuplan";

    private const string ThrottledKey = "Kyuplan.LoginThrottled";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header)
            || !SchemeName.Equals(header.Scheme, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        if (!TryDecode(header.Parameter, out var userName, out var password))
            return AuthenticateResult.Fail("The Basic credentials are malformed.");

        var address = Context.Connection.RemoteIpAddress?.ToString();
        if (throttle.RetryAfter(address, userName) is { } wait)
        {
            Context.Items[ThrottledKey] = wait;
            return AuthenticateResult.Fail("Too many failed logins.");
        }

        var user = await accounts.AuthenticateAsync(userName, password, Context.RequestAborted);
        if (user is null)
        {
            throttle.Failed(address, userName);
            // The name comes straight from the request: only a plausible one is worth writing into the log (no line breaks, no essays).
            var shown = AccountRules.CheckUserName(userName) is null ? userName : "(not a valid user name)";
            Logger.LogWarning("Rejected a login as '{UserName}' from {RemoteAddress}.", shown, Context.Connection.RemoteIpAddress);
            return AuthenticateResult.Fail("Wrong user name or password.");
        }

        throttle.Succeeded(address, userName);
        await accounts.TouchAsync(user, Context.RequestAborted);

        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        // The name other users get to see (it is what ends up written under comments), not the one that was typed to sign in.
        identity.AddClaim(new Claim(ClaimTypes.Name, user.DisplayName));
        identity.AddClaims(user.EffectivePermissions(plugins.PermissionIds).Select(p => new Claim(Permission.ClaimType, p)));

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Locked out: no login dialog (it would only try again), but the time to wait.
        if (Context.Items[ThrottledKey] is TimeSpan wait)
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        }

        Response.Headers.WWWAuthenticate = $"{SchemeName} realm=\"{Realm}\", charset=\"UTF-8\"";
        return base.HandleChallengeAsync(properties);
    }

    /// <summary>"user:password", Base64 of UTF-8. The password may contain colons, the user name cannot.</summary>
    private static bool TryDecode(string? parameter, out string userName, out string password)
    {
        userName = password = "";
        if (string.IsNullOrEmpty(parameter)) return false;

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parameter));
        }
        catch (FormatException)
        {
            return false;
        }

        var colon = decoded.IndexOf(':');
        if (colon < 0) return false;

        userName = decoded[..colon];
        password = decoded[(colon + 1)..];
        return true;
    }
}
