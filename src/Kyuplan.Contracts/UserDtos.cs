namespace Kyuplan.Contracts;

/// <summary>A user account of the API. The password is never part of it.</summary>
/// <param name="UserName">The main login name. Every user name and alias belongs to one user only, regardless of case.</param>
/// <param name="DisplayName">What other users see (e.g. as the author of a comment). Never empty: it is the user name unless set otherwise.</param>
/// <param name="Aliases">More names the user can sign in with (with the same password), by name. Never shown to other users.</param>
/// <param name="Roles">The roles the user has, by name.</param>
/// <param name="Permissions">The permissions given to the user directly, in the order of the catalogue (<see cref="Permission"/>).</param>
/// <param name="EffectivePermissions">What the user may do: their own permissions and those of their roles, in the order of the catalogue.</param>
/// <param name="WebTheme">The theme the user chose for the web client (<see cref="ThemeRules"/>); <c>null</c>: the instance's default applies.</param>
/// <param name="WindowsTheme">The same for the Windows client.</param>
/// <param name="Language">The language the user chose for the clients (<see cref="LanguageRules"/>); <c>null</c>: the client follows its system's language.</param>
/// <param name="MemberIds">The members this user is (an administrator links them, <c>PUT /api/users/{id}/members</c>); usually one, one per Spond group at most. <c>null</c> = none. What is written about them is hidden from the user unless they have the "own" permission of that kind.</param>
public sealed record UserDto(
    int Id, string UserName, string DisplayName, IReadOnlyList<string> Aliases,
    IReadOnlyList<RoleRefDto> Roles, IReadOnlyList<string> Permissions, IReadOnlyList<string> EffectivePermissions,
    string? WebTheme = null, string? WindowsTheme = null, string? Language = null, IReadOnlyList<int>? MemberIds = null)
{
    /// <summary>Whether the user may do this (one of <see cref="Permission"/>).</summary>
    public bool Can(string permission) => EffectivePermissions.Contains(permission);

    /// <summary>Whether this member is the user (one of <see cref="MemberIds"/>).</summary>
    public bool IsMember(int memberId) => MemberIds?.Contains(memberId) == true;

    /// <summary>
    /// Whether the user may read what is written about this member (comments or evaluations): about themselves only with the <paramref name="viewOwn"/>
    /// permission, about everybody else with <paramref name="view"/>. The two do not depend on each other.
    /// </summary>
    public bool MayRead(int memberId, string view, string viewOwn) => Can(IsMember(memberId) ? viewOwn : view);

    /// <summary>Whether the user has the role that has every permission.</summary>
    public bool HasAdministratorRole() => Roles.Any(r => r.Name == RoleRules.AdministratorName);
}

/// <summary>What is sent to create a user or to change one.</summary>
/// <param name="Password">Required for a new user. When changing a user: the new password, or <c>null</c> to keep the current one.</param>
/// <param name="DisplayName">Empty: the user name. <c>null</c>: keep the current one (a new user gets the user name).</param>
/// <param name="Aliases">All the aliases the user is to have; the others are removed. <c>null</c>: keep the current ones (a new user has none).</param>
/// <param name="RoleIds">All the roles the user is to have. <c>null</c>: keep the current ones (a new user has none). Needs <see cref="Permission.UsersGrant"/>.</param>
/// <param name="Permissions">All the permissions the user is to have of their own. <c>null</c>: keep the current ones (a new user has none). Needs <see cref="Permission.UsersGrant"/>.</param>
public sealed record UserInputDto(
    string UserName, string? Password, string? DisplayName = null, IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<int>? RoleIds = null, IReadOnlyList<string>? Permissions = null);

/// <summary>What a user sends to change their own names and password (<c>PUT /api/account</c>).</summary>
/// <param name="CurrentPassword">Required when <paramref name="NewPassword"/> is set.</param>
/// <param name="NewPassword">The new password, or <c>null</c> to keep the current one.</param>
/// <param name="DisplayName">Empty: the user name. <c>null</c>: keep the current one.</param>
/// <param name="Aliases">All the aliases the user is to have; the others are removed. <c>null</c>: keep the current ones.</param>
public sealed record AccountUpdateDto(
    string UserName, string? CurrentPassword, string? NewPassword, string? DisplayName = null, IReadOnlyList<string>? Aliases = null);

/// <summary>What is sent to say which members a user is (<c>PUT /api/users/{id}/members</c>).</summary>
/// <param name="MemberIds">All the members the user is to be; the others are released. A member can be linked to one user only.</param>
public sealed record UserMembersDto(IReadOnlyList<int> MemberIds);

/// <summary>A member one can link a user to (<c>GET /api/members/choices</c>).</summary>
/// <param name="GroupId">The Spond group; a person in two groups is two members.</param>
/// <param name="UserId">The user who already is this member, or <c>null</c>.</param>
public sealed record MemberChoiceDto(int Id, string GroupId, string Name, int? UserId);
