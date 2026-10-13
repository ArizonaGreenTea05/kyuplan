using Microsoft.EntityFrameworkCore;
using Kyuplan.Api.Infrastructure;
using Kyuplan.Api.Persistence;
using Kyuplan.Contracts;

namespace Kyuplan.Api.Accounts;

public sealed class RoleNotFoundException(int roleId) : Exception($"No role with id {roleId}.");

/// <summary>The administrator role has every permission by definition: changing or deleting it would only break that.</summary>
public sealed class SystemRoleException() : Exception("The administrator role cannot be changed or deleted.");

/// <summary>
/// Reads and writes the roles. Input is expected to be validated by the endpoints (<see cref="RoleRules"/>). A role that is deleted is
/// taken from its users (their other roles and their own permissions stay); a role that is changed changes what all its users may do at once.
/// </summary>
public sealed class RoleService(AccountsDbContext db)
{
    /// <summary>All roles by name, the administrator role first.</summary>
    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await db.Roles.AsNoTracking().Select(r => new { Role = r, Users = r.Users.Count }).ToListAsync(cancellationToken))
            .OrderByDescending(r => r.Role.IsSystem).ThenBy(r => r.Role.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => ToDto(r.Role, r.Users))];

    /// <exception cref="RoleNotFoundException"/>
    public async Task<RoleDto> GetAsync(int roleId, CancellationToken cancellationToken) =>
        (await ListAsync(cancellationToken)).FirstOrDefault(r => r.Id == roleId) ?? throw new RoleNotFoundException(roleId);

    /// <exception cref="AlreadyExistsException">The name is taken (also by the administrator role).</exception>
    public async Task<RoleDto> CreateAsync(RoleInputDto input, CancellationToken cancellationToken)
    {
        var role = new Role { Name = input.Name.Trim(), Permissions = Permission.InCatalogueOrder(input.Permissions) };
        db.Roles.Add(role);
        await SaveAsync(cancellationToken);
        return await GetAsync(role.Id, cancellationToken);
    }

    /// <exception cref="RoleNotFoundException"/>
    /// <exception cref="SystemRoleException">It is the administrator role.</exception>
    /// <exception cref="AlreadyExistsException">The name is taken.</exception>
    public async Task<RoleDto> UpdateAsync(int roleId, RoleInputDto input, CancellationToken cancellationToken)
    {
        var role = await FindAsync(roleId, cancellationToken);
        role.Name = input.Name.Trim();
        role.Permissions = Permission.InCatalogueOrder(input.Permissions);
        await SaveAsync(cancellationToken);
        return await GetAsync(roleId, cancellationToken);
    }

    /// <exception cref="RoleNotFoundException"/>
    /// <exception cref="SystemRoleException">It is the administrator role.</exception>
    public async Task DeleteAsync(int roleId, CancellationToken cancellationToken)
    {
        db.Roles.Remove(await FindAsync(roleId, cancellationToken)); // the links to its users go with it (cascade)
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The role to change: one that exists and is not the administrator role.</summary>
    private async Task<Role> FindAsync(int roleId, CancellationToken cancellationToken)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, cancellationToken) ?? throw new RoleNotFoundException(roleId);
        return role.IsSystem ? throw new SystemRoleException() : role;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new AlreadyExistsException("A role with that name already exists.");
        }
    }

    private static RoleDto ToDto(Role role, int users) => new(role.Id, role.Name, role.IsSystem, role.IsSystem ? [] : Permission.InCatalogueOrder(role.Permissions), users);
}
