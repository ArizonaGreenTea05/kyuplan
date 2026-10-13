namespace Kyuplan.Contracts;

/// <summary>A role as a user carries it: enough to show it.</summary>
public sealed record RoleRefDto(int Id, string Name);

/// <summary>A set of permissions that is given to many users at once.</summary>
/// <param name="IsSystem">The administrator role: it has every permission (also those added later) and can neither be changed nor deleted.</param>
/// <param name="Permissions">The permissions of the role, in the order of the catalogue. Empty for the administrator role, which does not list them.</param>
/// <param name="UserCount">How many users have the role.</param>
public sealed record RoleDto(int Id, string Name, bool IsSystem, IReadOnlyList<string> Permissions, int UserCount)
{
    public RoleRefDto Ref() => new(Id, Name);
}

/// <summary>What is sent to create a role or to replace one.</summary>
public sealed record RoleInputDto(string Name, IReadOnlyList<string> Permissions);

/// <summary>What a role must look like. The API enforces it; the clients check the same rules. Each check returns the problem in words, or <c>null</c>.</summary>
public static class RoleRules
{
    public const int MaxNameLength = 64;

    /// <summary>The name of the one role that has every permission; no other role can have it (names are unique regardless of case).</summary>
    public const string AdministratorName = "Administrator";

    public static string? CheckName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "name is required.";
        if (trimmed.Length > MaxNameLength) return $"name must be at most {MaxNameLength} characters long.";

        return trimmed.Any(char.IsControl) ? "name must not contain control characters." : null;
    }

    /// <summary>Only permissions of the catalogue, none twice.</summary>
    public static string? CheckPermissions(IReadOnlyList<string>? permissions, string name = "permissions")
    {
        if (permissions is null) return $"{name} is required (it may be empty).";

        var unknown = permissions.FirstOrDefault(p => !Permission.IsKnown(p));
        if (unknown is not null || permissions.Contains(null!)) return $"{name} contains one that does not exist{(unknown is null ? "" : $": {unknown}")}.";

        return permissions.Distinct().Count() == permissions.Count ? null : $"{name} must not list one twice.";
    }
}
