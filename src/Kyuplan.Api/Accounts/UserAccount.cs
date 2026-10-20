using Kyuplan.Contracts;

namespace Kyuplan.Api.Accounts;

/// <summary>A user of the API. Lives in the accounts database, which is separate from the one that holds exams and saved filters.</summary>
public sealed class UserAccount
{
    public int Id { get; set; }

    /// <summary>The main login name. Unique regardless of case ("Anna" is "anna"), and so is every <see cref="Aliases">alias</see>.</summary>
    public string UserName { get; set; } = "";

    /// <summary>What other users see for this user. Never empty: the user name, unless the user or an administrator chose another.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>More names to sign in with. Not shown to other users.</summary>
    public List<UserAlias> Aliases { get; set; } = [];

    /// <summary>Salted and stretched by ASP.NET Core's <c>PasswordHasher</c>; the password itself is never stored.</summary>
    public string PasswordHash { get; set; } = "";

    public List<Role> Roles { get; set; } = [];

    /// <summary>What the user may do besides what their <see cref="Roles"/> allow: keys of <see cref="Permission"/>, stored as one JSON column.</summary>
    public List<string> Permissions { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The last request the user made with a valid login, rounded to <see cref="PresenceRules.WriteInterval"/>; <c>null</c> = never.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>The user's own theme for the web client (<see cref="ThemeRules"/>); <c>null</c> = the instance's default.</summary>
    public string? WebTheme { get; set; }

    /// <summary>The user's own theme for the Windows client; <c>null</c> = the instance's default.</summary>
    public string? WindowsTheme { get; set; }

    /// <summary>The user's own language for the clients (<see cref="Contracts.LanguageRules"/>); <c>null</c> = the client's system language.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// Everything the user may do, in the order of the catalogue: the administrator role has it all, otherwise the permissions of the
    /// user and of the roles. Keys that are no longer in the catalogue (an older version wrote them) are ignored. Needs <see cref="Roles"/> loaded.
    /// </summary>
    public IReadOnlyList<string> EffectivePermissions() =>
        Roles.Any(r => r.IsSystem)
            ? Permission.All
            : [.. Permission.All.Where(p => Permissions.Contains(p) || Roles.Any(r => r.Permissions.Contains(p)))];

    public bool IsAdministrator() => Roles.Any(r => r.IsSystem);
}

/// <summary>A name besides the user name that signs in as its user.</summary>
public sealed class UserAlias
{
    public int Id { get; set; }

    public int UserId { get; set; }

    /// <summary>Unique regardless of case. That it differs from every user name is guaranteed by <see cref="AccountName"/>.</summary>
    public string Alias { get; set; } = "";
}

/// <summary>
/// Every login name, the user name and each alias, once: its unique index is what stops two users from owning one name, which an index
/// on <see cref="UserAccount"/> or <see cref="UserAlias"/> alone cannot (it cannot span two tables). Kept in step by <see cref="AccountService"/>.
/// </summary>
public sealed class AccountName
{
    public int Id { get; set; }

    public int UserId { get; set; }

    /// <summary>Unique regardless of case.</summary>
    public string Name { get; set; } = "";
}

/// <summary>A set of permissions many users have in common. One role is the <see cref="IsSystem">administrator</see>.</summary>
public sealed class Role
{
    public int Id { get; set; }

    /// <summary>Unique regardless of case.</summary>
    public string Name { get; set; } = "";

    /// <summary>The administrator role: every permission, also those added later; it cannot be changed or deleted, and the last user who has it cannot lose it.</summary>
    public bool IsSystem { get; set; }

    /// <summary>Keys of <see cref="Permission"/>, stored as one JSON column. Not used for the system role.</summary>
    public List<string> Permissions { get; set; } = [];

    public List<UserAccount> Users { get; set; } = [];
}
