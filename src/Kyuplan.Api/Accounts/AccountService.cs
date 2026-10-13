using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Kyuplan.Api.Configuration;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Accounts;

public sealed class UserNotFoundException(int userId) : Exception($"No user with id {userId}.");

/// <summary>The current password given to change one's own password is not the user's password.</summary>
public sealed class WrongPasswordException() : Exception("The current password is wrong.");

/// <summary>Taking the administrator role from the only user who has it would leave nobody who can manage users.</summary>
public sealed class LastAdminException() : Exception("There must be at least one administrator.");

/// <summary>The signed-in user's permissions do not cover what they asked for.</summary>
public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>Somebody tried to delete their own user: they would be signed out with no way back, and might be the last administrator.</summary>
public sealed class DeleteOwnUserException() : Exception("You cannot delete your own user. Ask somebody else who may delete users to do it.");

/// <summary>
/// Reads and writes user accounts and checks logins. Input is expected to be validated by the endpoints
/// (<see cref="AccountRules"/>). Passwords are only ever stored as hashes.
/// </summary>
public sealed class AccountService(
    AccountsDbContext db,
    IPasswordHasher<UserAccount> hasher,
    TimeProvider time,
    IOptions<AccountsOptions> options,
    ILogger<AccountService> logger)
{
    /// <summary>What a login with an unknown user name is checked against, so that it takes as long as one with a wrong password.</summary>
    private static readonly string UnknownUserHash =
        new PasswordHasher<UserAccount>().HashPassword(new UserAccount(), Guid.NewGuid().ToString("N"));

    /// <summary>The user for a user name (or an alias) and password, or <c>null</c> when there is no such combination.</summary>
    public async Task<UserAccount?> AuthenticateAsync(string userName, string password, CancellationToken cancellationToken)
    {
        try
        {
            var user = await db.Users.AsNoTracking().Include(u => u.Roles)
                .FirstOrDefaultAsync(u => u.UserName == userName || u.Aliases.Any(a => a.Alias == userName), cancellationToken);

            var verdict = hasher.VerifyHashedPassword(user ?? new UserAccount(), user?.PasswordHash ?? UnknownUserHash, password);
            return user is not null && verdict != PasswordVerificationResult.Failed ? user : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Nobody changes or deletes a user who can do more than they can themselves: otherwise whoever may edit users could set the password of an
    /// administrator and sign in as one.
    /// </summary>
    /// <exception cref="ForbiddenException">The user has permissions the signed-in user lacks.</exception>
    public async Task EnsureMayManageAsync(int userId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var user = await FindAsync(userId, cancellationToken);
        if (user.EffectivePermissions().Any(p => !actor.Has(p)))
            throw new ForbiddenException("That user can do more than you can, so only somebody who can do at least as much may change or delete them.");
    }

    /// <summary>Roles and permissions that are given must not be more than the giver has.</summary>
    /// <exception cref="ForbiddenException"/>
    public async Task EnsureMayGrantAsync(UserInputDto input, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var wanted = new List<string>(input.Permissions ?? []);
        if (input.RoleIds is { Count: > 0 } roleIds)
        {
            foreach (var role in await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).ToListAsync(cancellationToken))
                wanted.AddRange(role.IsSystem ? Permission.All : role.Permissions);
        }

        if (wanted.Any(p => !actor.Has(p))) throw new ForbiddenException("You cannot give permissions that you do not have yourself.");
    }

    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await db.Users.AsNoTracking().AsSplitQuery().Include(u => u.Aliases).Include(u => u.Roles).OrderBy(u => u.UserName).ToListAsync(cancellationToken)).Select(ToDto)];

    public async Task<UserDto> GetAsync(int userId, CancellationToken cancellationToken) =>
        ToDto(await FindAsync(userId, cancellationToken));

    /// <exception cref="AlreadyExistsException">The user name or an alias is taken.</exception>
    /// <exception cref="InvalidInputException">A role does not exist.</exception>
    public async Task<UserDto> CreateAsync(UserInputDto input, CancellationToken cancellationToken)
    {
        var userName = input.UserName.Trim();
        var user = new UserAccount
        {
            UserName = userName,
            DisplayName = DisplayNameFor(input.DisplayName, userName, current: null),
            Roles = await FindRolesAsync(input.RoleIds ?? [], cancellationToken),
            Permissions = Permission.InCatalogueOrder(input.Permissions ?? []),
            CreatedAt = time.GetUtcNow(),
        };
        user.PasswordHash = hasher.HashPassword(user, input.Password!);
        db.Users.Add(user);

        await SaveAsync(user, input.Aliases, cancellationToken);
        return ToDto(user);
    }

    /// <summary>
    /// Somebody changes a user: the names, the roles and permissions (each only when given) and, when given, the password (no current
    /// password needed).
    /// </summary>
    /// <exception cref="LastAdminException">The user is the only one with the administrator role and would no longer have it.</exception>
    /// <exception cref="InvalidInputException">A role does not exist.</exception>
    public async Task<UserDto> UpdateAsync(int userId, UserInputDto input, CancellationToken cancellationToken)
    {
        var user = await FindAsync(userId, cancellationToken);

        if (input.RoleIds is { } roleIds)
        {
            var roles = await FindRolesAsync(roleIds, cancellationToken);
            if (user.IsAdministrator() && !roles.Any(r => r.IsSystem) && !await HasOtherAdministratorAsync(userId, cancellationToken))
                throw new LastAdminException();

            user.Roles.Clear();
            user.Roles.AddRange(roles);
        }

        if (input.Permissions is { } permissions) user.Permissions = Permission.InCatalogueOrder(permissions);

        user.UserName = input.UserName.Trim();
        user.DisplayName = DisplayNameFor(input.DisplayName, user.UserName, user.DisplayName);
        if (input.Password is { } password) user.PasswordHash = hasher.HashPassword(user, password);

        await SaveAsync(user, input.Aliases, cancellationToken);
        return ToDto(user);
    }

    /// <summary>Removes a user; their login stops working at once. Nothing else refers to users, so nothing else is lost.</summary>
    /// <param name="actingUserId">The administrator who deletes.</param>
    /// <exception cref="DeleteOwnUserException">The administrator is the user to delete.</exception>
    /// <exception cref="LastAdminException">The user is the only administrator (reachable only when two administrators delete each other at once).</exception>
    public async Task DeleteAsync(int userId, int actingUserId, CancellationToken cancellationToken)
    {
        if (userId == actingUserId) throw new DeleteOwnUserException();

        var user = await FindAsync(userId, cancellationToken);
        if (user.IsAdministrator() && !await HasOtherAdministratorAsync(userId, cancellationToken))
            throw new LastAdminException();

        db.Users.Remove(user);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>A user chooses the theme of one kind of client (<see cref="ThemeClients"/>); <c>null</c> goes back to the instance's default. Input is validated by the endpoint.</summary>
    public async Task<UserDto> SetThemeAsync(int userId, ThemePreferenceDto input, CancellationToken cancellationToken)
    {
        var user = await FindAsync(userId, cancellationToken);
        if (input.Client == ThemeClients.Windows) user.WindowsTheme = input.Theme;
        else user.WebTheme = input.Theme;

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(user);
    }

    /// <summary>A user changes their own names and, when given, their password. Nothing else about the account.</summary>
    /// <exception cref="WrongPasswordException">A new password is given and the current one is wrong.</exception>
    public async Task<UserDto> UpdateOwnAsync(int userId, AccountUpdateDto input, CancellationToken cancellationToken)
    {
        var user = await FindAsync(userId, cancellationToken);

        if (input.NewPassword is { } newPassword)
        {
            if (input.CurrentPassword is null
                || hasher.VerifyHashedPassword(user, user.PasswordHash, input.CurrentPassword) == PasswordVerificationResult.Failed)
                throw new WrongPasswordException();

            user.PasswordHash = hasher.HashPassword(user, newPassword);
        }

        user.UserName = input.UserName.Trim();
        user.DisplayName = DisplayNameFor(input.DisplayName, user.UserName, user.DisplayName);

        await SaveAsync(user, input.Aliases, cancellationToken);
        return ToDto(user);
    }

    /// <summary>
    /// Creates the administrator that <see cref="AccountsOptions.InitialAdminPassword"/> describes when there is no user yet, for an
    /// installation that is set up from its configuration. Without that password nobody is created: the web client asks for the first
    /// administrator (see <c>SettingsService.SetupAsync</c>). Returns whether there is a user now.
    /// </summary>
    public async Task<bool> EnsureAdministratorAsync(CancellationToken cancellationToken)
    {
        if (await db.Users.AnyAsync(cancellationToken)) return true;

        var settings = options.Value;
        if (string.IsNullOrEmpty(settings.InitialAdminPassword)) return false;

        var problem = AccountRules.CheckUserName(settings.InitialAdminUserName) ?? AccountRules.CheckPassword(settings.InitialAdminPassword, "Accounts:InitialAdminPassword");
        if (problem is not null) throw new InvalidOperationException($"Cannot create the first administrator: {problem}");

        var admin = await CreateAdministratorAsync(settings.InitialAdminUserName, settings.InitialAdminPassword, cancellationToken);
        logger.LogInformation("There was no user yet, so the administrator '{UserName}' was created from the configuration.", admin.UserName);
        return true;
    }

    /// <summary>Creates a user with the administrator role.</summary>
    public async Task<UserDto> CreateAdministratorAsync(string userName, string password, CancellationToken cancellationToken)
    {
        var administratorRole = await db.Roles.SingleAsync(r => r.IsSystem, cancellationToken);
        return await CreateAsync(new UserInputDto(userName, password, RoleIds: [administratorRole.Id]), cancellationToken);
    }

    private async Task<UserAccount> FindAsync(int userId, CancellationToken cancellationToken) =>
        await db.Users.AsSplitQuery().Include(u => u.Aliases).Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
        ?? throw new UserNotFoundException(userId);

    private Task<bool> HasOtherAdministratorAsync(int userId, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => u.Id != userId && u.Roles.Any(r => r.IsSystem), cancellationToken);

    private async Task<List<Role>> FindRolesAsync(IReadOnlyList<int> roleIds, CancellationToken cancellationToken)
    {
        var ids = roleIds.Distinct().ToList();
        var roles = await db.Roles.Where(r => ids.Contains(r.Id)).ToListAsync(cancellationToken);
        if (roles.Count != ids.Count) throw new InvalidInputException($"There is no role with id {ids.First(id => roles.All(r => r.Id != id))}.");

        return roles;
    }

    /// <summary>A user chooses their language (<see cref="LanguageRules"/>); <c>null</c> forgets it. Input is validated by the endpoint.</summary>
    public async Task<UserDto> SetLanguageAsync(int userId, LanguagePreferenceDto input, CancellationToken cancellationToken)
    {
        var user = await FindAsync(userId, cancellationToken);
        user.Language = input.Language;

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(user);
    }

    private static UserDto ToDto(UserAccount user) =>
        new(user.Id, user.UserName, user.DisplayName, [.. user.Aliases.Select(a => a.Alias).Order(StringComparer.OrdinalIgnoreCase)],
            [.. user.Roles.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => new RoleRefDto(r.Id, r.Name))],
            Permission.InCatalogueOrder(user.Permissions), user.EffectivePermissions(), user.WebTheme, user.WindowsTheme, user.Language);

    /// <summary>Empty means the user name; <c>null</c> keeps what the user has (a new user has nothing, so gets the user name).</summary>
    private static string DisplayNameFor(string? input, string userName, string? current) =>
        input is null ? current ?? userName : input.Trim().Length > 0 ? input.Trim() : userName;

    /// <summary>
    /// Saves the user and, when <paramref name="aliases"/> is given, makes them the user's aliases (<c>null</c> leaves them alone). The unique
    /// indexes decide what a duplicate is within the user names and within the aliases, so two requests racing for one name cannot both win.
    /// </summary>
    private async Task SaveAsync(UserAccount user, IReadOnlyList<string>? aliases, CancellationToken cancellationToken)
    {
        var wanted = aliases?.Select(a => a.Trim()).ToList();
        await EnsureNamesFreeAsync(user, wanted, cancellationToken);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // Aliases are removed and added, never edited in place: "anna" -> "Anna" is the same name to the unique index but a new row to EF,
            // and the removals go first (together with the user's own changes) so that swapping two names cannot trip over itself.
            List<UserAlias> kept = wanted is null ? user.Aliases : [.. user.Aliases.Where(a => wanted.Contains(a.Alias, StringComparer.Ordinal))];
            db.UserAliases.RemoveRange(user.Aliases.Except(kept).ToList());
            await db.SaveChangesAsync(cancellationToken);

            if (wanted is not null)
            {
                user.Aliases.AddRange(wanted.Except(kept.Select(a => a.Alias), StringComparer.Ordinal).Select(name => new UserAlias { Alias = name }));
                await db.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new AlreadyExistsException("That user name or alias is already taken.");
        }
    }

    /// <summary>
    /// No name signs in as two users: the user name and the aliases of a user must not be a user name or alias of anybody else. The unique indexes
    /// each see one table only, so this is checked here. <c>ponytail:</c> two requests racing for the same name across the tables can both pass;
    /// the name then signs in as whichever user the database finds first (the password still has to fit). A trigger would close it.
    /// </summary>
    private async Task EnsureNamesFreeAsync(UserAccount user, IReadOnlyList<string>? aliases, CancellationToken cancellationToken)
    {
        List<string> names = [user.UserName, .. aliases ?? []];

        var taken = await db.Users.Where(u => u.Id != user.Id && names.Contains(u.UserName)).Select(u => u.UserName).FirstOrDefaultAsync(cancellationToken)
                    ?? await db.UserAliases.Where(a => a.UserId != user.Id && names.Contains(a.Alias)).Select(a => a.Alias).FirstOrDefaultAsync(cancellationToken);
        if (taken is null) return;

        // As typed, not as stored: the stored spelling of somebody else's name is not for everybody to see.
        throw new AlreadyExistsException(
            $"The user name or alias '{names.First(n => n.Equals(taken, StringComparison.OrdinalIgnoreCase))}' is already taken.");
    }
}
