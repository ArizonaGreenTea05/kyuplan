using System.Security.Claims;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Accounts;

/// <summary>
/// Permissions on endpoints. The sign-in (<see cref="BasicAuthenticationHandler"/>) puts what the user may do into their claims on every
/// request, so a change of roles or permissions applies from the user's next request on.
/// </summary>
public static class PermissionAuthorization
{
    public static bool Has(this ClaimsPrincipal user, string permission) => user.HasClaim(Permission.ClaimType, permission);

    /// <summary>The endpoint needs a login and any one of these permissions: 401 without the login, 403 without a permission.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, params IEnumerable<string> anyOf) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy.RequireAuthenticatedUser().RequireAssertion(context => anyOf.Any(context.User.Has)));
}
