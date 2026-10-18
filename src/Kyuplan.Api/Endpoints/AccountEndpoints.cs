using System.Security.Claims;
using Kyuplan.Api.Accounts;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Endpoints;

/// <summary>
/// Logins and what the users may do. Every user can see and change their own names and password (<c>/api/account</c>), whatever their
/// permissions; users are listed, created and changed with <c>users.*</c> permissions (<c>/api/users</c>), and the roles that bundle
/// permissions with <c>roles.*</c> permissions (<c>/api/roles</c>).
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/api/account").WithTags("Own account");

        account.MapGet("/", async (ClaimsPrincipal principal, AccountService service, MemberService members, CancellationToken ct) =>
                await WithMembers(await service.GetAsync(UserId(principal), ct), members, ct))
            .WithSummary("The signed-in user, with their roles, what they may do (effectivePermissions) and the members they are (memberIds). A client can use it to check a login.");

        account.MapGet("/members", (ClaimsPrincipal principal, MemberService members, CancellationToken ct) => members.OfUserAsync(UserId(principal), ct))
            .WithSummary("The members the signed-in user is (an administrator links them). What is written about them is read with comments.viewOwn and ratings.viewOwn, whatever else the user may read.");

        account.MapPut("/", async (ClaimsPrincipal principal, AccountUpdateDto input, AccountService service, MemberService members, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);

                return Results.Ok(await WithMembers(await service.UpdateOwnAsync(UserId(principal), input, ct), members, ct));
            })
            .WithSummary("Change your own user name, display name, aliases and/or password. Setting a new password needs the current one. Nothing else can be changed here.");

        account.MapPut("/theme", async (ClaimsPrincipal principal, ThemePreferenceDto input, AccountService service, MemberService members, Kyuplan.Api.Infrastructure.ThemePackStore packs, CancellationToken ct) =>
            {
                var errors = new Dictionary<string, string[]>();
                if (!ThemeClients.IsKnown(input?.Client)) errors["client"] = [$"client must be one of: {ThemeClients.Web}, {ThemeClients.Windows}."];
                else if (input!.Theme is not null) Check(errors, "theme", ThemeRules.Check(input.Client, input.Theme, "theme") ?? packs.CheckInstalled(input.Theme, "theme"));
                if (errors.Count > 0) return Results.ValidationProblem(errors);

                return Results.Ok(await WithMembers(await service.SetThemeAsync(UserId(principal), input!, ct), members, ct));
            })
            .WithSummary("Choose your own theme for the web or the Windows client (client = web | windows); theme = null follows the instance's default again. Answers with the user.");

        account.MapPut("/language", async (ClaimsPrincipal principal, LanguagePreferenceDto input, AccountService service, MemberService members, CancellationToken ct) =>
            {
                if (input is null) return Results.ValidationProblem(Fail([], nameof(input), "A request body is required."));
                if (input.Language is not null && LanguageRules.Check(input.Language) is { } problem)
                    return Results.ValidationProblem(Fail([], "language", problem));

                return Results.Ok(await WithMembers(await service.SetLanguageAsync(UserId(principal), input, ct), members, ct));
            })
            .WithSummary("Choose your own language for the clients (en | de); language = null forgets the choice, so a client follows its system's language again. Answers with the user.");

        var users = app.MapGroup("/api/users").WithTags("Users");

        users.MapGet("/", async (AccountService service, MemberService members, CancellationToken ct) =>
            {
                var links = await members.IdsByUserAsync(ct);
                return (await service.ListAsync(ct)).Select(u => u with { MemberIds = links.GetValueOrDefault(u.Id, []) }).ToList();
            })
            .WithSummary("All users, by user name.")
            .RequirePermission(Permission.UsersView);

        users.MapGet("/{userId:int}", async (int userId, AccountService service, MemberService members, CancellationToken ct) =>
                await WithMembers(await service.GetAsync(userId, ct), members, ct))
            .WithName("GetUser")
            .WithSummary("One user.")
            .RequirePermission(Permission.UsersView);

        users.MapPost("/", async (UserInputDto input, ClaimsPrincipal principal, AccountService service, CancellationToken ct) =>
            {
                EnsureMayGrant(input, principal);
                if (Validate(input, creating: true) is { } errors) return Results.ValidationProblem(errors);
                await service.EnsureMayGrantAsync(input, principal, ct);

                var user = await service.CreateAsync(input, ct);
                return Results.CreatedAtRoute("GetUser", new { userId = user.Id }, user);
            })
            .WithSummary("Create a user. The password is required; the user name and the aliases must be new (regardless of case: no name belongs to two users). Roles and permissions can only be given with users.grant; without them the user may do nothing but manage their own account.")
            .RequirePermission(Permission.UsersEdit);

        users.MapPut("/{userId:int}", async (int userId, UserInputDto input, ClaimsPrincipal principal, AccountService service, MemberService members, CancellationToken ct) =>
            {
                EnsureMayGrant(input, principal);
                if (Validate(input, creating: false) is { } errors) return Results.ValidationProblem(errors);
                await service.EnsureMayManageAsync(userId, principal, ct);
                await service.EnsureMayGrantAsync(input, principal, ct);

                return Results.Ok(await WithMembers(await service.UpdateAsync(userId, input, ct), members, ct));
            })
            .WithSummary("Change a user's names and, when a password is given, password; with users.grant also their roles and permissions (leave them out to keep them). The last user with the administrator role cannot lose it.")
            .RequirePermission(Permission.UsersEdit);

        users.MapPut("/{userId:int}/members", async (int userId, UserMembersDto input, ClaimsPrincipal principal, AccountService service, MemberService members, CancellationToken ct) =>
            {
                if (input?.MemberIds is null) return Results.ValidationProblem(Fail([], nameof(input.MemberIds), "memberIds is required (it may be empty)."));
                await service.EnsureMayManageAsync(userId, principal, ct);

                await members.SetUserMembersAsync(userId, input.MemberIds, ct);
                return Results.Ok(await WithMembers(await service.GetAsync(userId, ct), members, ct));
            })
            .WithSummary("Say which members a user is (all of them; the others are released): what is written about them is then hidden from the user unless they have comments.viewOwn / ratings.viewOwn. A member can be one user's only (409), and a user may be one member per Spond group.")
            .RequirePermission(Permission.UsersEdit);

        users.MapDelete("/{userId:int}", async (int userId, ClaimsPrincipal principal, AccountService service, MemberService members, CancellationToken ct) =>
            {
                await service.EnsureMayManageAsync(userId, principal, ct);
                await service.DeleteAsync(userId, UserId(principal), ct);
                await members.ReleaseUserAsync(userId, ct);
                return Results.NoContent();
            })
            .WithSummary("Delete a user. You cannot delete yourself, so the last administrator stays.")
            .RequirePermission(Permission.UsersDelete);

        var roles = app.MapGroup("/api/roles").WithTags("Roles");

        roles.MapGet("/", (RoleService service, CancellationToken ct) => service.ListAsync(ct))
            .WithSummary("All roles by name, the administrator role first, each with its permissions and how many users have it.")
            .RequirePermission(Permission.ReadsRoles);

        roles.MapGet("/{roleId:int}", (int roleId, RoleService service, CancellationToken ct) => service.GetAsync(roleId, ct))
            .WithName("GetRole")
            .WithSummary("One role.")
            .RequirePermission(Permission.ReadsRoles);

        roles.MapPost("/", async (RoleInputDto input, ClaimsPrincipal principal, RoleService service, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);
                EnsureMayGive(input.Permissions, [], principal);

                var role = await service.CreateAsync(input, ct);
                return Results.CreatedAtRoute("GetRole", new { roleId = role.Id }, role);
            })
            .WithSummary("Create a role. Its name must be new (regardless of case); permissions are keys of the catalogue.")
            .RequirePermission(Permission.RolesEdit);

        roles.MapPut("/{roleId:int}", async (int roleId, RoleInputDto input, ClaimsPrincipal principal, RoleService service, CancellationToken ct) =>
            {
                if (Validate(input) is { } errors) return Results.ValidationProblem(errors);
                EnsureMayGive(input.Permissions, (await service.GetAsync(roleId, ct)).Permissions, principal);

                return Results.Ok(await service.UpdateAsync(roleId, input, ct));
            })
            .WithSummary("Replace the name and the permissions of a role; everybody who has it is affected from their next request on. The administrator role cannot be changed (409).")
            .RequirePermission(Permission.RolesEdit);

        roles.MapDelete("/{roleId:int}", async (int roleId, RoleService service, CancellationToken ct) =>
            {
                await service.DeleteAsync(roleId, ct);
                return Results.NoContent();
            })
            .WithSummary("Delete a role and take it from its users. The administrator role cannot be deleted (409).")
            .RequirePermission(Permission.RolesEdit);

        return app;
    }

    /// <summary>The members a user is live in the planner database, the users in the accounts database: they are put together here.</summary>
    private static async Task<UserDto> WithMembers(UserDto user, MemberService members, CancellationToken cancellationToken) =>
        user with { MemberIds = [.. (await members.IdsOfUserAsync(user.Id, cancellationToken)).Order()] };

    private static int UserId(ClaimsPrincipal principal) => int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>A role may only gain permissions its editor has: otherwise whoever may edit roles could give themselves anything.</summary>
    private static void EnsureMayGive(IEnumerable<string>? wanted, IEnumerable<string> already, ClaimsPrincipal principal)
    {
        if ((wanted ?? []).Except(already).Any(p => !principal.Has(p)))
            throw new ForbiddenException("You cannot give a role permissions that you do not have yourself.");
    }

    /// <summary>Roles and permissions are not the business of whoever may only change names: asking for them without <c>users.grant</c> is refused, not ignored.</summary>
    private static void EnsureMayGrant(UserInputDto? input, ClaimsPrincipal principal)
    {
        if ((input?.RoleIds is not null || input?.Permissions is not null) && !principal.Has(Permission.UsersGrant))
            throw new ForbiddenException($"Giving roles and permissions to users needs the permission {Permission.UsersGrant}.");
    }

    internal static Dictionary<string, string[]>? Validate(UserInputDto? input, bool creating)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) return Fail(errors, nameof(input), "A request body is required.");

        Check(errors, nameof(input.UserName), AccountRules.CheckUserName(input.UserName));
        Check(errors, nameof(input.DisplayName), AccountRules.CheckDisplayName(input.DisplayName));
        Check(errors, nameof(input.Aliases), AccountRules.CheckAliases(input.Aliases, input.UserName));
        // On an existing user, no password means "keep it"; a password that is given must still be a proper one.
        if (creating || input.Password is not null) Check(errors, nameof(input.Password), AccountRules.CheckPassword(input.Password));
        if (input.Permissions is not null) Check(errors, nameof(input.Permissions), RoleRules.CheckPermissions(input.Permissions));

        return errors.Count == 0 ? null : errors;
    }

    internal static Dictionary<string, string[]>? Validate(AccountUpdateDto? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) return Fail(errors, nameof(input), "A request body is required.");

        Check(errors, nameof(input.UserName), AccountRules.CheckUserName(input.UserName));
        Check(errors, nameof(input.DisplayName), AccountRules.CheckDisplayName(input.DisplayName));
        Check(errors, nameof(input.Aliases), AccountRules.CheckAliases(input.Aliases, input.UserName));
        if (input.NewPassword is not null) Check(errors, nameof(input.NewPassword), AccountRules.CheckPassword(input.NewPassword, "newPassword"));

        return errors.Count == 0 ? null : errors;
    }

    internal static Dictionary<string, string[]>? Validate(RoleInputDto? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null) return Fail(errors, nameof(input), "A request body is required.");

        Check(errors, nameof(input.Name), RoleRules.CheckName(input.Name));
        Check(errors, nameof(input.Permissions), RoleRules.CheckPermissions(input.Permissions));

        return errors.Count == 0 ? null : errors;
    }

    private static void Check(Dictionary<string, string[]> errors, string key, string? problem)
    {
        if (problem is not null) errors[key] = [problem];
    }

    private static Dictionary<string, string[]> Fail(Dictionary<string, string[]> errors, string key, string message)
    {
        errors[key] = [message];
        return errors;
    }
}
