namespace Kyuplan.Contracts;

/// <summary>
/// What a user name and a password must look like. The API enforces it; the clients check the same rules so that they only offer
/// to save what the API will accept. Each check returns the problem in words, or <c>null</c> when it is fine.
/// </summary>
public static class AccountRules
{
    public const int MaxUserNameLength = 64;
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 128;
    public const int MaxDisplayNameLength = 64;
    public const int MaxAliases = 10;

    /// <summary>Also what an alias has to look like: it is a name to sign in with.</summary>
    public static string? CheckUserName(string? userName, string name = "userName")
    {
        var trimmed = userName?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return $"{name} is required.";
        if (trimmed.Length > MaxUserNameLength) return $"{name} must be at most {MaxUserNameLength} characters long.";

        // A colon would end the user name early in "user:password", which is what HTTP Basic sends.
        if (trimmed.Contains(':') || trimmed.Any(char.IsControl)) return $"{name} must not contain a colon or control characters.";
        return null;
    }

    /// <summary>A display name is optional (empty means the user name); when there is one it is short and has no control characters.</summary>
    public static string? CheckDisplayName(string? displayName)
    {
        var name = displayName?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (name.Length > MaxDisplayNameLength) return $"displayName must be at most {MaxDisplayNameLength} characters long.";

        return name.Any(char.IsControl) ? "displayName must not contain control characters." : null;
    }

    /// <summary>Each alias is a proper user name; there are not too many, none twice, and none is the user's own user name.</summary>
    public static string? CheckAliases(IReadOnlyList<string>? aliases, string? userName)
    {
        if (aliases is null) return null;
        if (aliases.Count > MaxAliases) return $"aliases must have at most {MaxAliases} entries.";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { userName?.Trim() ?? "" };
        foreach (var alias in aliases)
        {
            if (CheckUserName(alias, "an alias") is { } problem) return problem;
            if (!seen.Add(alias.Trim())) return "aliases must differ from each other and from the userName.";
        }

        return null;
    }

    public static string? CheckPassword(string? password, string name = "password") =>
        password is { Length: >= MinPasswordLength and <= MaxPasswordLength }
            ? null
            : $"{name} must be between {MinPasswordLength} and {MaxPasswordLength} characters long.";
}
