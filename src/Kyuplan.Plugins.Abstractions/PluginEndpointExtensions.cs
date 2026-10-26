using Microsoft.AspNetCore.Builder;

namespace Kyuplan.Plugins;

public static class PluginEndpointExtensions
{
    /// <summary>The claim of the signed-in user that carries one permission (the same the API's own endpoints check).</summary>
    public const string PermissionClaimType = "permission";

    /// <summary>
    /// The endpoint needs a login and any one of these permissions (full ids, from <see cref="IPluginBuilder.Permission"/>): 401 without the login,
    /// 403 without a permission. Without this the endpoint is open to every signed-in user.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, params string[] anyOf) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy.RequireAuthenticatedUser().RequireAssertion(
            context => anyOf.Any(permission => context.User.HasClaim(PermissionClaimType, permission))));
}
